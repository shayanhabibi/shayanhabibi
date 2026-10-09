import { test, expect } from '@playwright/test';

const projectNames = ['Partas.Solid', 'Xantham', 'Partas.Build', 'Ranvier', 'loony', 'wrflock', 'Fable.Electron'];

test('homepage links to the complete project list and its walkthrough', async ({ page, request }) => {
  expect((await request.get('/all-projects/')).ok()).toBe(true);
  await page.goto('/');
  await page.getByRole('link', { name: 'All projects', exact: true }).click();
  await expect(page).toHaveURL(/\/all-projects\/$/);
  await expect(page.getByRole('heading', { level: 1, name: /^All projects/ })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Project list' }).locator('.project-row h3')).toHaveText(projectNames);
  await expect(page.locator('#project-window')).toHaveCount(0);
  await page.getByRole('region', { name: 'Project list' }).locator('.project-row').filter({ has: page.getByRole('heading', { name: 'Partas.Solid', exact: true }) }).getByRole('link', { name: 'Technical walkthrough' }).click();
  await expect(page).toHaveURL(/\/partas-solid\/$/);
});

test('the full project list is useful without JavaScript', async ({ browser }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  const response = await page.goto('http://localhost:8080/all-projects/');
  expect(response.ok()).toBe(true);
  const list = page.getByRole('region', { name: 'Project list' });
  await expect(list.locator('.project-row h3')).toHaveText(projectNames);
  await expect(list.getByRole('link', { name: 'Source ↗' })).toHaveCount(7);
  await expect(list.getByRole('link', { name: 'Documentation ↗' })).toHaveCount(7);
  await expect(list.locator('summary')).toHaveCount(0);
  for (const detail of await list.locator('.project-details p').all()) await expect(detail).toBeVisible();
  await expect(list.getByText('Building on Oxpecker.Solid', { exact: false })).toBeVisible();
  await page.getByRole('link', { name: 'Back to selected work' }).click();
  await expect(page).toHaveURL(/\/#work$/);
  await context.close();
});

for (const width of [1440, 320]) {
  test(`full project list fits ${width}px and scrolls with the page`, async ({ page, request }) => {
    expect((await request.get('/all-projects/')).ok()).toBe(true);
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('/all-projects/');
    const list = page.getByRole('region', { name: 'Project list' });
    await expect(list.locator('.project-row')).toHaveCount(7);
    await expect(list).toHaveCSS('overflow-y', 'visible');
    await expect(list.locator('summary')).toHaveCount(0);
    for (const detail of await list.locator('.project-details p').all()) await expect(detail).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const ids = await page.locator('[id]').evaluateAll(elements => elements.map(element => element.id));
    expect(new Set(ids).size).toBe(ids.length);
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
    await page.screenshot({ path: `test-results/all-projects-${width}.png`, fullPage: true });
  });
}
