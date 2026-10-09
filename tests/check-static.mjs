import assert from 'node:assert/strict';
import { readFile, access } from 'node:fs/promises';

const html = await readFile(new URL('../site/output/index.html', import.meta.url), 'utf8');
for (const text of ['Partas.Solid', 'Xantham', 'Ranvier', 'Partas.Build', 'loony', 'wrflock', 'Fable.Electron', 'project-fallback', 'Hiring an engineer.', 'Solving a specific problem.']) {
  assert.ok(html.includes(text), `Static output is missing ${text}`);
}
assert.match(html, /_partas\/solid\/[^"\s]+\.js/, 'Compiled Solid assets must be linked');
assert.ok(html.includes('mailto:shayan.habibi01@gmail.com'), 'The configured contact must be linked');
await access(new URL('../site/output/.nojekyll', import.meta.url));
for (const id of ['partas-solid', 'xantham', 'ranvier', 'partas-build', 'loony', 'wrflock', 'fable-electron']) {
  assert.ok(html.includes(`/shayanhabibi/graphs/${id}.svg`), `Static project row is missing its ${id} illustration under the GitHub Pages base path`);
  const svg = await readFile(new URL(`../site/output/graphs/${id}.svg`, import.meta.url), 'utf8');
  assert.ok(svg.includes('xmlns="http://www.w3.org/2000/svg"'), `${id} is not a standalone SVG`);
}
console.log('Static content, browser assets and Pages marker verified.');
