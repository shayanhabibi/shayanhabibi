import { test, expect } from '@playwright/test';

test('Ranvier maps run the engine and recover through a boundary', async ({ page }) => {
  await page.goto('/ranvier/');
  const propagation = page.locator('#ranvier-propagation');
  await expect(propagation.locator('.rv-map-node')).toHaveCount(4);
  await expect(propagation.getByLabel('Event', { exact: true })).toHaveCSS('accent-color', 'rgb(51, 51, 51)');
  await propagation.getByLabel('Speed', { exact: true }).selectOption('0.25');
  await propagation.getByRole('button', { name: 'Increment', exact: true }).click();
  const travelling = propagation.locator('.rv-map-dot').first();
  await expect(travelling).toBeAttached();
  expect(await travelling.evaluate(dot => getComputedStyle(dot).fill)).toMatch(/^rgb\((102, 102, 102|51, 51, 51)\)$/);
  await expect(propagation.locator('.is-flash .rv-map-node__shape').first()).toHaveCSS('stroke', 'rgb(51, 51, 51)');
  await propagation.getByLabel('Speed', { exact: true }).selectOption('4');
  await expect(propagation.locator('.rv-map-node__value')).toContainText(['2', '4', '6']);
  await propagation.getByRole('button', { name: 'Batch two writes', exact: true }).click();
  await expect(propagation.locator('.rv-map-node__value')).toContainText(['4', '8', '12']);
  await propagation.getByLabel('Event', { exact: true }).focus();
  await propagation.getByLabel('Event', { exact: true }).press('End');
  await propagation.locator('.rv-map-node').filter({ has: page.locator('.rv-map-node__name', { hasText: /^doubled$/ }) }).click();
  await expect(propagation.locator('.rv-map__why')).not.toBeEmpty();
  await propagation.getByLabel('Speed', { exact: true }).selectOption('2');
  await expect(propagation.getByLabel('Speed', { exact: true })).toHaveValue('2');
  await propagation.getByLabel('Event', { exact: true }).focus();
  await propagation.getByLabel('Event', { exact: true }).press('Home');
  await propagation.getByRole('button', { name: 'Step', exact: true }).click();
  expect(Number(await propagation.getByLabel('Event', { exact: true }).inputValue())).toBeGreaterThan(0);
  await propagation.getByRole('button', { name: 'Reset', exact: true }).click();
  const boundary = page.locator('#ranvier-boundary');
  await boundary.getByRole('button', { name: 'Settle 4', exact: true }).click();
  await expect(boundary.locator('.rv-map-node__value')).toContainText(['4', '12', 'Total 12']);
  await boundary.getByRole('button', { name: 'Fail', exact: true }).click();
  await expect(boundary.locator('.rv-map__log')).toContainText('feed offline');
  await boundary.getByRole('button', { name: 'Settle 5', exact: true }).click();
  await expect(boundary.locator('.rv-map-node__value')).toContainText(['5', '15', 'Total 15']);
  await expect(page.locator('.rv-map__error')).toHaveText(['', '']);
});

for (const width of [1440, 320]) {
  test(`Ranvier maps and controls fit ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('/ranvier/');
    await expect(page.locator('.rv-map')).toHaveCount(2);
    const connections = await page.locator('.rv-map-edge').evaluateAll(edges => edges.map(edge => edge.getAttribute('d')));
    expect(connections.length).toBeGreaterThan(0);
    for (const path of connections) {
      expect(path).not.toMatch(/[CQSA]/i);
      expect(path).toMatch(/H.*V.*H/);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/ranvier-${width}.png`, fullPage: true });
    await page.locator('#ranvier-propagation').screenshot({ path: `test-results/ranvier-map-${width}.png` });
  });
}
