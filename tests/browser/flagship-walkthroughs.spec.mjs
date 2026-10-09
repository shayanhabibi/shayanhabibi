import { test, expect } from '@playwright/test';

for (const [project, route, heading] of [
  ['Xantham', '/xantham/', 'Xantham: TypeScript APIs in F#'],
  ['Partas.Build', '/partas-build/', 'Partas.Build: CLI options from build stages'],
  ['Ranvier', '/ranvier/', 'Ranvier: making reactive computation inspectable'],
]) {
  test(`${project} walkthrough is linked from both project lists`, async ({ page, request }) => {
    expect((await request.get(route)).ok()).toBe(true);
    for (const [url, list] of [['/', '#project-explorer'], ['/all-projects/', '#project-catalog-list']]) {
      await page.goto(url);
      const row = page.locator(`${list} .project-row`).filter({ has: page.getByRole('heading', { name: project, exact: true }) });
      await row.getByRole('link', { name: 'Technical walkthrough' }).click();
      await expect(page).toHaveURL(new RegExp(`${route}$`));
      await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
      await expect(page.getByRole('heading', { name: /^Design choices and tradeoffs/ })).toBeVisible();
    }
  });

  test(`${project} walkthrough is readable on mobile without JavaScript`, async ({ browser }) => {
    const context = await browser.newContext({ javaScriptEnabled: false, viewport: { width: 320, height: 900 } });
    const page = await context.newPage();
    const response = await page.goto(`http://localhost:8080${route}`);
    expect(response.ok()).toBe(true);
    await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Project repository', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/${route.replaceAll('/', '')}-320.png`, fullPage: true });
    await context.close();
  });
}
