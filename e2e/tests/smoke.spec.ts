import { expect, test, type Page } from '@playwright/test';

// Records are kept in the local database, but preferences (such as "setup skipped") are still in memory,
// so after a page load the app shows the setup again.

async function startApp(page: Page) {
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

test('settings page opens with every section', async ({ page }) => {
  await skipSetup(page);

  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Settings' }).click();

  await expect(page.locator('#settings-units')).toBeVisible();
  await expect(page.locator('#settings-version')).toContainText('Version');
});
