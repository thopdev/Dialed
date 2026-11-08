import { expect, test, type Page } from '@playwright/test';

// The app keeps its data in memory for now, so every step moves by tapping links:
// a full page load (page.goto) would start with an empty store again.

async function startApp(page: Page) {
  await page.goto('/');
  // Blazor WebAssembly needs a moment to download and start.
  await expect(page).toHaveURL(/\/welcome$/, { timeout: 60_000 });
}

async function skipSetup(page: Page) {
  await startApp(page);
  await page.locator('#onboarding-skip').click();
  await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible();
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

  await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible();
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
  await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible();
});

test('settings page opens with every section', async ({ page }) => {
  await skipSetup(page);

  await page.getByRole('link', { name: 'More', exact: true }).click();
  await page.getByRole('link', { name: 'Settings' }).click();

  await expect(page.locator('#settings-units')).toBeVisible();
  await expect(page.locator('#settings-version')).toContainText('Version');
});
