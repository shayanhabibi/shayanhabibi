import { test, expect } from '@playwright/test';

test('walkthrough section navigation and highlighting work without JavaScript', async ({ browser }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto('http://localhost:8080/partas-solid/');
  const navigation = page.getByRole('navigation', { name: 'On this page' });
  const link = navigation.getByRole('link', { name: 'The compilation path', exact: true });
  await link.click();
  await expect(page).toHaveURL(/#the-compilation-path$/);
  await expect(page.locator('pre code span[class]').first()).toBeAttached();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await context.close();
});

test('homepage provides direct hiring and consulting paths', async ({ page }) => {
  await page.goto('/');
  await page.locator('.hero').getByRole('link', { name: 'Hiring', exact: true }).click();
  await expect(page).toHaveURL(/#employment$/);
  await page.locator('.hero').getByRole('link', { name: 'Consulting / contracting', exact: true }).click();
  await expect(page).toHaveURL(/#contracting$/);
});

test('Partas.Solid case study serves a compiled reactive example', async ({ page, request }) => {
  expect((await request.get('/partas-solid/')).ok()).toBe(true);
  await page.goto('/');
  await page.locator('#project-explorer .project-row').filter({ has: page.getByRole('heading', { name: 'Partas.Solid', exact: true }) }).getByRole('link', { name: 'Technical walkthrough' }).click();
  await expect(page).toHaveURL(/\/partas-solid\/$/);
  await expect(page.getByRole('heading', { level: 1, name: /^Partas.Solid: F# components to Solid JSX/ })).toBeVisible();
  await expect(page.locator('#project-explorer')).toHaveCount(0);
  const demo = page.locator('#compilation-demo');
  await expect(demo.getByRole('status')).toHaveText('Build requests: 0');
  await demo.getByRole('button', { name: 'Add request' }).click();
  await demo.getByRole('button', { name: 'Add request' }).click();
  await expect(demo.getByRole('status')).toHaveText('Build requests: 2');
  await demo.getByRole('button', { name: 'Reset' }).click();
  await expect(demo.getByRole('status')).toHaveText('Build requests: 0');
  await page.getByRole('link', { name: 'Back to selected work' }).click();
  await expect(page).toHaveURL(/\/#work$/);
});

test('case study retains technical content and links without JavaScript', async ({ browser }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  await page.setViewportSize({ width: 390, height: 900 });
  const response = await page.goto('http://localhost:8080/partas-solid/');
  expect(response.ok()).toBe(true);
  await expect(page.getByRole('heading', { name: /^The compilation path/ })).toBeVisible();
  await expect(page.getByRole('heading', { name: /^Design choices and tradeoffs/ })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Plugin source', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await context.close();
});

for (const width of [1440, 320]) {
  test(`case study fits ${width}px and saves a preview`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('/partas-solid/');
    await expect(page.locator('#compilation-demo')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/case-study-${width}.png`, fullPage: true });
  });
}
