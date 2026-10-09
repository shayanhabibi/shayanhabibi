import { test, expect } from '@playwright/test';

const visibleNames = page => page.locator('#project-window').evaluate(window => {
  const bounds = window.getBoundingClientRect();
  return [...window.querySelectorAll('.project-row')]
    .filter(row => {
      const box = row.getBoundingClientRect();
      return box.top >= bounds.top - 2 && box.bottom <= bounds.bottom + 2;
    })
    .map(row => row.querySelector('h3').textContent);
});

test('script component mounts, filters and discloses project details', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  const explorer = page.locator('#project-explorer');
  await expect(explorer).toBeVisible();
  await expect(page.locator('#project-fallback')).toBeHidden();
  await expect(explorer.locator('.project-row')).toHaveCount(7);
  await expect(explorer.locator('.project-illustration')).toHaveCount(7);
  await page.getByRole('button', { name: 'Interop', exact: true }).click();
  await expect(explorer.locator('.project-row')).toHaveCount(2);
  await expect(explorer.getByRole('heading', { name: 'Xantham' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Interop', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(explorer.locator('.result-count')).toHaveText('2 projects');
  const disclosure = explorer.locator('summary').first();
  await disclosure.focus();
  await page.keyboard.press('Enter');
  await expect(explorer.getByText('I built an F# generator around the TypeScript 7 compiler API', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: 'Automation', exact: true }).click();
  await expect(explorer.locator('.project-row')).toHaveCount(1);
  await expect(explorer.getByRole('heading', { name: 'Partas.Build', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Concurrency', exact: true }).click();
  await expect(explorer.locator('.project-row')).toHaveCount(2);
  await expect(explorer.getByRole('heading', { name: 'loony', exact: true })).toBeVisible();
  await expect(explorer.getByRole('heading', { name: 'wrflock', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'All', exact: true }).click();
  await expect(explorer.locator('.project-row')).toHaveCount(7);
  expect(errors).toEqual([]);
});

test('blocked browser bundle retains static project links', async ({ page }) => {
  await page.route('**/_partas/solid/**', route => route.abort());
  await page.goto('/');
  const fallback = page.locator('#project-fallback');
  await expect(fallback).toBeVisible();
  await expect(fallback.getByRole('heading', { name: 'Partas.Solid' })).toBeVisible();
  await expect(fallback.getByRole('link', { name: 'Source ↗' })).toHaveCount(7);
  await expect(fallback.locator('.project-illustration')).toHaveCount(7);
  for (const name of ['Partas.Build', 'loony', 'wrflock']) {
    await expect(fallback.getByRole('heading', { name, exact: true })).toBeVisible();
  }
});

test('JavaScript-disabled site remains useful', async ({ browser }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  await page.goto('http://localhost:8080');
  await expect(page.locator('#project-fallback')).toBeVisible();
  await expect(page.locator('#project-fallback .project-row')).toHaveCount(7);
  await expect(page.getByRole('heading', { name: 'Something worth building?' })).toBeVisible();
  await context.close();
});

test('each project illustration is served and decodes as an image', async ({ page, request }) => {
  await page.goto('/');
  const images = page.locator('#project-explorer .project-illustration');
  await expect(images).toHaveCount(7);
  for (const id of ['partas-solid', 'xantham', 'ranvier', 'partas-build', 'loony', 'wrflock', 'fable-electron']) {
    const response = await request.get(`/shayanhabibi/graphs/${id}.svg`);
    expect(response.ok()).toBe(true);
    expect(response.headers()['content-type']).toContain('image/svg+xml');
  }
  for (const image of await images.all()) {
    await image.scrollIntoViewIfNeeded();
    await expect.poll(() => image.evaluate(element => element.complete && element.naturalWidth > 0)).toBe(true);
    expect(await image.getAttribute('alt')).toBeTruthy();
  }
});

test('vertical carousel shows at most three projects and resets on filtering', async ({ page }) => {
  await page.goto('/');
  const explorer = page.locator('#project-explorer');
  const rows = explorer.locator('.project-row');
  const previous = explorer.getByRole('button', { name: 'Previous project', exact: true });
  const next = explorer.getByRole('button', { name: 'Next project', exact: true });
  const names = () => visibleNames(page);
  await expect(rows).toHaveCount(7);
  await expect(previous).toBeDisabled();
  await expect(next).toBeEnabled();
  await expect(explorer.locator('.result-count')).toHaveText('1–3 of 7 projects');
  await expect.poll(names).toEqual(['Partas.Solid', 'Xantham', 'Partas.Build']);
  await next.focus();
  await page.keyboard.press('Enter');
  await expect.poll(names).toEqual(['Xantham', 'Partas.Build', 'Ranvier']);
  await expect(explorer.locator('.result-count')).toHaveText('2–4 of 7 projects');
  await next.click();
  await expect.poll(names).toEqual(['Partas.Build', 'Ranvier', 'loony']);
  await next.click();
  await expect.poll(names).toEqual(['Ranvier', 'loony', 'wrflock']);
  await next.click();
  await expect.poll(names).toEqual(['loony', 'wrflock', 'Fable.Electron']);
  await expect(next).toBeDisabled();
  await expect(previous).toBeEnabled();
  await previous.click();
  await expect.poll(names).toEqual(['Ranvier', 'loony', 'wrflock']);
  await page.getByRole('button', { name: 'Concurrency', exact: true }).click();
  await expect(rows).toHaveCount(2);
  await expect(previous).toBeDisabled();
  await expect(next).toBeDisabled();
  await page.getByRole('button', { name: 'All', exact: true }).click();
  await expect(explorer.locator('.result-count')).toHaveText('1–3 of 7 projects');
  await expect.poll(names).toEqual(['Partas.Solid', 'Xantham', 'Partas.Build']);
});

test('carousel supports wheel scrolling and focused keyboard navigation without a scrollbar', async ({ page }) => {
  await page.goto('/');
  const window = page.locator('#project-window');
  await window.scrollIntoViewIfNeeded();
  await window.hover();
  await expect(window).toHaveCSS('scrollbar-width', 'none');
  await page.mouse.wheel(0, 300);
  await expect.poll(() => window.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  await window.focus();
  await page.keyboard.press('End');
  await expect.poll(() => visibleNames(page)).toEqual(['loony', 'wrflock', 'Fable.Electron']);
  await page.keyboard.press('Home');
  await expect.poll(() => visibleNames(page)).toEqual(['Partas.Solid', 'Xantham', 'Partas.Build']);
  await page.keyboard.press('ArrowDown');
  await expect.poll(() => visibleNames(page)).toEqual(['Xantham', 'Partas.Build', 'Ranvier']);
});

for (const width of [1440, 820, 390, 320]) {
  test(`layout fits ${width}px and saves design preview`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.goto('/');
    await expect(page.locator('#project-explorer')).toBeVisible();
    const graph = page.getByRole('img', { name: 'Ideas connected through tooling to working systems' });
    await expect(graph).toBeVisible();
    await expect(graph.locator('polyline')).toHaveCount(3);
    await expect(graph.locator('text')).toHaveText(['IDEA', 'TOOLING', 'SYSTEM']);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(page.locator('html')).toHaveCSS('scroll-behavior', 'auto');
    const window = page.locator('#project-window');
    await expect(window).toHaveCSS('scroll-behavior', 'auto');
    const heights = await window.locator('.project-row').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height));
    expect(Math.max(...heights) - Math.min(...heights)).toBeLessThan(1);
    expect(await window.evaluate(element => element.clientHeight)).toBeCloseTo(heights[0] * 3, 0);
    for (const row of await window.locator('.project-row').all()) {
      const fits = () => row.evaluate(element => {
        const bottom = element.getBoundingClientRect().bottom - 10;
        return [...element.querySelectorAll('.project-copy, .project-links, .project-index')].every(child => child.getBoundingClientRect().bottom <= bottom);
      });
      expect(await fits()).toBe(true);
      await row.locator('summary').click();
      expect((await row.boundingBox()).height).toBeCloseTo(heights[0], 0);
      expect(await fits()).toBe(true);
      await row.locator('summary').click();
    }
    await window.focus();
    await page.keyboard.press('Home');
    await expect.poll(() => visibleNames(page)).toEqual(['Partas.Solid', 'Xantham', 'Partas.Build']);
    await page.evaluate(() => {
      document.activeElement?.blur();
      window.scrollTo({ top: 0, behavior: 'instant' });
    });
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
    await page.screenshot({ path: `test-results/design-${width}.png`, fullPage: true });
  });
}
