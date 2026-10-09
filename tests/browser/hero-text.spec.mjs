import { test, expect } from '@playwright/test';

for (const width of [1440, 320]) {
  test(`hero text rotates without moving surrounding content at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto('/');
    await expect(page.locator('#project-explorer')).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: 'F# tooling. Across ecosystems.' })).toBeVisible();
    const description = page.locator('.hero-description');
    const initial = await description.boundingBox();
    await page.evaluate(() => {
      window.heroTextOverlap = false;
      const sample = () => {
        const visible = [...document.querySelectorAll('.hero-word')]
          .filter(word => Number(getComputedStyle(word).opacity) > 0.02);
        if (visible.length > 1) window.heroTextOverlap = true;
        window.heroTextSample = requestAnimationFrame(sample);
      };
      sample();
    });
    await expect(page.locator('.hero-word[data-active="true"]')).toHaveText('Typed bindings.', { timeout: 6000 });
    await expect(page.locator('.hero-word[data-active="true"]')).toHaveCSS('opacity', '1');
    expect((await description.boundingBox()).y).toBeCloseTo(initial.y, 1);
    await expect(page.locator('.hero-word[data-active="true"]')).toHaveText('Reactive systems.', { timeout: 6000 });
    await expect(page.locator('.hero-word[data-active="true"]')).toHaveCSS('opacity', '1');
    expect(await page.evaluate(() => {
      cancelAnimationFrame(window.heroTextSample);
      return window.heroTextOverlap;
    })).toBe(false);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect(page.locator('.hero-word[data-active="true"]')).toHaveText('F# tooling.');
  });
}

test('hero text stays static without motion or JavaScript', async ({ browser }) => {
  for (const options of [{ reducedMotion: 'reduce' }, { javaScriptEnabled: false }]) {
    const context = await browser.newContext(options);
    const page = await context.newPage();
    await page.goto('http://localhost:8080/');
    await expect(page.locator('.hero-word').first()).toHaveCSS('opacity', '1');
    await page.waitForTimeout(3600);
    await expect(page.locator('.hero-word[data-active="true"]')).toHaveText('F# tooling.');
    await context.close();
  }
});
