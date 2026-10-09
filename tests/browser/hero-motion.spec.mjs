import { test, expect } from '@playwright/test';

test('hero loops smoothly beneath the nodes without duplicating on filtering', async ({ page }) => {
  await page.addInitScript(() => { Math.random = () => 0.1; });
  await page.goto('/');
  const pulse = page.locator('#connecting-thread .hero-pulse');
  await expect(pulse).toBeAttached();
  const start = await pulse.getAttribute('transform');
  await expect.poll(() => pulse.getAttribute('transform')).not.toBe(start);
  const x = () => pulse.evaluate(element => element.transform.baseVal.consolidate().matrix.e);
  await expect(pulse).toHaveAttribute('d', /C.*L/);
  await expect(pulse).toHaveCSS('opacity', '0.6');
  await expect.poll(() => pulse.evaluate(element => {
    const matrix = element.transform.baseVal.consolidate().matrix;
    return Math.atan2(matrix.b, matrix.a) * 180 / Math.PI;
  }), { timeout: 6000, intervals: [100] }).toBeCloseTo(90, 1);
  expect(await page.locator('#connecting-thread').evaluate(svg => {
    const layer = svg.querySelector('.hero-motion');
    const nodes = [...svg.children].filter(element => element.tagName.toLowerCase() === 'circle');
    return nodes.every(node => layer.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_FOLLOWING);
  })).toBe(true);
  await expect(page.locator('#connecting-thread .hero-pulse-trail')).toHaveAttribute('points', /,/);
  // Observe a complete return to the beginning after reaching the final segment.
  await expect.poll(x, { timeout: 6000, intervals: [100] }).toBeGreaterThan(230);
  await expect.poll(x, { timeout: 6000, intervals: [100] }).toBeLessThan(100);
  await page.evaluate(() => { Math.random = () => 0.9; });
  await expect.poll(() => pulse.evaluate(element => {
    const matrix = element.transform.baseVal.consolidate().matrix;
    return Math.atan2(matrix.b, matrix.a) * 180 / Math.PI;
  }), { timeout: 6000, intervals: [50] }).toBeCloseTo(-90, 1);
  await expect(page.locator('#connecting-thread')).toBeVisible();
  await page.getByRole('button', { name: 'Interop', exact: true }).click();
  await expect(pulse).toHaveCount(1);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await expect(page.locator('#connecting-thread .hero-motion')).toHaveCount(0);
});

test('reduced motion keeps the hero static while the project explorer works', async ({ browser }) => {
  const context = await browser.newContext({ reducedMotion: 'reduce' });
  const page = await context.newPage();
  await page.goto('http://localhost:8080/');
  await expect(page.locator('#project-explorer')).toBeVisible();
  await expect(page.locator('#connecting-thread .hero-pulse')).toHaveCount(0);
  await expect(page.locator('#connecting-thread')).toBeVisible();
  await context.close();
});
