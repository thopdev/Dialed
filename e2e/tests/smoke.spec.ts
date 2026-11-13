import { expect, test, type Page } from '@playwright/test';

const cspViolations = new WeakMap<Page, string[]>();

test.afterEach(({ page }) => {
  expect(cspViolations.get(page) ?? [], 'Content-Security-Policy violations').toEqual([]);
});

// The PWA runs under its enforced Content-Security-Policy (e2e/serve.mjs sends the real headers). A blocked script, style,
// image or request is a bug, so any violation fails the test.
async function failOnCspViolations(page: Page) {
  const violations: string[] = [];
  await page.exposeFunction('reportCspViolation', (text: string) => violations.push(text));
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', event =>
      (window as unknown as { reportCspViolation: (text: string) => void }).reportCspViolation(
        `${event.violatedDirective} blocked ${event.blockedURI || 'inline'} (${event.sourceFile ?? ''}:${event.lineNumber})`));
  });
  return violations;
}

async function startApp(page: Page) {
  const violations = await failOnCspViolations(page);
  test.info().annotations.push({ type: 'csp', description: 'enforced' });
  cspViolations.set(page, violations);
  // Show what the app reports while it starts, so a failed run explains itself in the CI log.
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') {
      console.log(`[browser ${message.type()}] ${message.text()}`);
    }
  });
  page.on('pageerror', error => console.log(`[page error] ${error.message}`));
  page.on('requestfailed', request => console.log(`[request failed] ${request.url()} ${request.failure()?.errorText}`));
  await page.goto('/');
  // Blazor WebAssembly needs a moment to download and start.
  await expect(page).toHaveURL(/\/welcome$/, { timeout: 90_000 });
}

// Home greets by time of day ("Good morning", "Good evening, Alice").
const homeGreeting = /^Good (morning|afternoon|evening)/;

async function skipSetup(page: Page) {
  await startApp(page);
  await page.locator('#onboarding-skip').click();
  await expect(page.getByRole('heading', { level: 1, name: homeGreeting })).toBeVisible();
}

async function addBeanAndBag(page: Page, beanName: string) {
  await page.locator('#home-first-bean').click();
  await page.locator('#bean-name').fill(beanName);
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page).toHaveURL(/\/beans\/[0-9a-f-]{36}$/);

  await page.getByRole('link', { name: 'Add bag' }).click();
  await page.locator('#bag-weight').fill('250');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page).toHaveURL(/\/bags\/[0-9a-f-]{36}$/);
}

async function logBrew(page: Page) {
  await page.getByRole('link', { name: 'Brew', exact: true }).click();
  await page.locator('.dialed-row-link').first().click();
  await page.locator('#brew-blank').click();
  await page.locator('#brew-dose').fill('18');
  await page.locator('#brew-dose').blur();
  await page.getByRole('button', { name: 'Save brew' }).click();
}

test('first launch shows setup, which can be skipped', async ({ page }) => {
  await startApp(page);
  await expect(page.locator('#onboarding-progress')).toContainText('Step 1 of');

  await page.locator('#onboarding-skip').click();

  await expect(page.getByRole('heading', { level: 1, name: homeGreeting })).toBeVisible();
  await expect(page.locator('#home-first-bean')).toBeVisible();
});

test('add a bean and a bag, then see them on Home', async ({ page }) => {
  await skipSetup(page);
  await addBeanAndBag(page, 'Gesha Village');

  await page.getByRole('link', { name: 'Home', exact: true }).click();

  await expect(page.locator('#home-log-brew')).toBeVisible();
  // The new bag is still sealed, so it is not an open bag yet.
  await expect(page.locator('#home-open-bags')).toHaveCount(0);
});

test('records are still there after reloading the app', async ({ page }) => {
  await skipSetup(page);
  await addBeanAndBag(page, 'Gesha Village');

  // The app opens on the same page again (the bag), which only works when the bag was loaded from the local database.
  await page.reload();
  await expect(page.getByRole('link', { name: 'Beans', exact: true })).toBeVisible({ timeout: 90_000 });
  await expect(page).toHaveURL(/\/bags\/[0-9a-f-]{36}$/);
  await expect(page.getByText('This bag no longer exists.')).toHaveCount(0);
  await page.getByRole('link', { name: 'Beans', exact: true }).click();

  await expect(page.getByText('Gesha Village')).toBeVisible();
});

test('log a brew and find it in the history', async ({ page }) => {
  await skipSetup(page);
  await addBeanAndBag(page, 'Gesha Village');

  await logBrew(page);

  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Brew history' }).click();
  await expect(page.locator('.dialed-row-link')).toHaveCount(1);
});

test('keeps working offline and when the connection returns', async ({ page, context }) => {
  await skipSetup(page);
  await addBeanAndBag(page, 'Offline Roast');

  await context.setOffline(true);
  await logBrew(page);
  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Brew history' }).click();
  await expect(page.locator('.dialed-row-link')).toHaveCount(1);

  await context.setOffline(false);
  await page.getByRole('link', { name: 'Home', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name: homeGreeting })).toBeVisible();
});

test('the service worker caches the app shell and the barcode reader for offline use', async ({ page }) => {
  await skipSetup(page);

  // The published worker precaches every file in service-worker-assets.js (generated on publish); the zxing reader must be among them,
  // or a barcode scan without a connection fails.
  const cached = await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
    const names = (await caches.keys()).filter(name => name.startsWith('offline-cache-'));
    const urls: string[] = [];
    for (const name of names) {
      for (const request of await (await caches.open(name)).keys()) {
        urls.push(new URL(request.url).pathname);
      }
    }
    return urls;
  });
  expect(cached).toContain('/lib/zxing/reader/zxing_reader.wasm');
  expect(cached).toContain('/lib/zxing/reader/index.js');
  expect(cached).toContain('/index.html');
});

test('a bean with an origin shows a zoomed map, and Statistics leads back to its beans', async ({ page }) => {
  await skipSetup(page);
  await page.locator('#home-first-bean').click();
  await page.locator('#bean-name').fill('Guji Hambela');
  await page.locator('#bean-country').fill('Ethiopia');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page).toHaveURL(/\/beans\/[0-9a-f-]{36}$/);
  // The bean page draws the country itself (zoomed in), not just a dot on the world.
  await expect(page.locator('#bean-origin-map path.dialed-origin-map-country')).toHaveCount(1);

  await page.getByRole('link', { name: 'Add bag' }).click();
  await page.locator('#bag-weight').fill('250');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page).toHaveURL(/\/bags\/[0-9a-f-]{36}$/);
  await logBrew(page);

  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Statistics' }).click();
  for (const id of ['#stats-origin-map', '#stats-passport', '#stats-calendar', '#stats-milestones']) {
    await expect(page.locator(id)).toBeVisible();
  }
  await expect(page.locator('#stats-origin-map svg')).toHaveAttribute('aria-label', /Ethiopia/);
  // Nothing may be wider than the phone: the charts must shrink, not scroll the page sideways.
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(1);

  await page.locator('#stats-origins a', { hasText: 'Ethiopia' }).click();
  await expect(page).toHaveURL(/\/beans\?country=/);
  await expect(page.locator('#bean-filters-toggle')).toContainText('Filters (1)');
  await expect(page.getByText('Guji Hambela')).toBeVisible();
});

test('a brew can be shared as a picture card', async ({ page }) => {
  // Force the download path: a CI browser has no share sheet to hand the file to.
  await page.addInitScript(() => Object.defineProperty(navigator, 'canShare', { value: undefined }));
  await skipSetup(page);
  await addBeanAndBag(page, 'Card Roast');
  await logBrew(page);
  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Brew history' }).click();
  await page.locator('.dialed-row-link').first().click();

  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('#history-share-card').click()]);

  expect(download.suggestedFilename()).toBe('dialed-brew.png');
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(chunk as Buffer);
  const bytes = Buffer.concat(chunks);
  expect([...bytes.subarray(0, 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]); // a PNG
  expect(bytes.length).toBeGreaterThan(5_000); // an actual picture, not an empty canvas
});

test('settings page opens with every section', async ({ page }) => {
  await skipSetup(page);

  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Settings' }).click();

  await expect(page.locator('#settings-signin')).toBeVisible();
  await expect(page.locator('#settings-version')).toContainText('Version');
  await page.locator('[data-settings="preferences"]').click();
  await expect(page.locator('#settings-units')).toBeVisible();
});

// ---- Accounts and sync, against the Development API that playwright.config.ts starts (#110) ----------------------------

type DevUser = 'alice' | 'bob' | 'carol';

// Signs in through the Development-only dev login: the test fetches tokens from /auth/dev (no credentials) and puts them where
// the app keeps its sign-in, exactly as a finished passkey sign-in would. Call before startApp.
async function signInAs(page: Page, user: DevUser) {
  const response = await page.request.post('/auth/dev', { data: { user } });
  expect(response.ok(), `POST /auth/dev as ${user}`).toBe(true);
  const tokens = await response.json() as { accessToken: string; displayName: string };
  await page.addInitScript(json => localStorage.setItem('dialed.session.v1', json), JSON.stringify(tokens));
  return tokens;
}

async function openSettings(page: Page) {
  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Settings' }).click();
}

test('signs in with the dev login and shows the account and household', async ({ page }) => {
  await signInAs(page, 'alice');
  await skipSetup(page);

  await openSettings(page);

  await expect(page.locator('#account-signed-in')).toContainText('Alice');
  await expect(page.locator('#account-signup-form')).toHaveCount(0);
  await expect(page.locator('#household-name')).toHaveText('Alice and Bob');
  await expect(page.locator('#household-members')).toContainText('Bob');
});

test('a bean added by one household member reaches the other', async ({ page, browser }) => {
  // Two cold app starts (Alice's device, then Bob's) in one test.
  test.slow();
  const beanName = `Synced ${test.info().project.name} ${Date.now()}`;

  const alice = await signInAs(page, 'alice');
  await skipSetup(page);
  // Through the Beans tab, not Home's "first bean" button: the shared household may already hold beans from other tests.
  await page.getByRole('link', { name: 'Beans', exact: true }).click();
  await page.locator('a[href="beans/new"]').first().click();
  await page.locator('#bean-name').fill(beanName);
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page).toHaveURL(/\/beans\/[0-9a-f-]{36}$/);

  // The app pushes shortly after a change; wait until the server has the bean before Bob opens the app.
  await expect.poll(async () => {
    const response = await page.request.get('/sync/beans', { headers: { authorization: `Bearer ${alice.accessToken}` } });
    return response.ok() ? await response.text() : '';
  }, { timeout: 30_000 }).toContain(beanName);

  // Bob is on another device: a fresh browser context with its own storage, same iPhone or iPad profile.
  const bobContext = await browser.newContext(test.info().project.use);
  try {
    const bobPage = await bobContext.newPage();
    await signInAs(bobPage, 'bob');
    await skipSetup(bobPage);
    await bobPage.getByRole('link', { name: 'Beans', exact: true }).click();

    await expect(bobPage.getByText(beanName)).toBeVisible();
    expect(cspViolations.get(bobPage) ?? [], 'Content-Security-Policy violations on the second device').toEqual([]);
  } finally {
    await bobContext.close();
  }
});
