import { test, expect } from '@playwright/test';

async function observeStyles(page) {
  await page.evaluate(() => {
    window.projectStyleChanges = 0;
    new MutationObserver(records => {
      window.projectStyleChanges += records.filter(record => record.attributeName === 'style').length;
    }).observe(document.querySelector('#project-window'), { subtree: true, attributes: true, attributeFilter: ['style'] });
  });
}

test('filter transition restores row styles and survives rapid changes', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('#project-explorer')).toBeVisible();
  await observeStyles(page);
  await page.getByRole('button', { name: 'Interop', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.projectStyleChanges)).toBeGreaterThan(0);
  await expect.poll(() => page.locator('#project-window .project-row').first().evaluate(row => {
    const style = getComputedStyle(row);
    return Number(style.opacity) < 0.9 && new DOMMatrix(style.transform).m42 > 1;
  }), { intervals: [20] }).toBe(true);
  await page.getByRole('button', { name: 'All', exact: true }).click();
  await page.getByRole('button', { name: 'Interop', exact: true }).click();
  await expect.poll(() => page.locator('#project-window .project-row').evaluateAll(rows =>
    rows.every(row => !row.style.opacity && !row.style.transform))).toBe(true);
  await expect(page.locator('#project-window .project-row').first()).toBeVisible();
});

test('reduced motion skips filter transitions', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/');
  await expect(page.locator('#project-explorer')).toBeVisible();
  await observeStyles(page);
  await page.getByRole('button', { name: 'Interop', exact: true }).click();
  await expect(page.locator('#project-window .project-row').first()).toBeVisible();
  await page.waitForTimeout(350);
  expect(await page.evaluate(() => window.projectStyleChanges)).toBe(0);
});
