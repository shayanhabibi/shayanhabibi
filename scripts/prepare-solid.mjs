import { mkdir, copyFile } from 'node:fs/promises';

// The plugin creates its own npm workspace. Seed it with the reviewed lockfile
// before its first install so cold builds use the same transitive dependencies.
const workspace = new URL('../site/.nacara/partas-solid/', import.meta.url);
await mkdir(workspace, { recursive: true });
await copyFile(new URL('../site/solid-package-lock.json', import.meta.url), new URL('package-lock.json', workspace));
