import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  use: { baseURL: 'http://localhost:8080', screenshot: 'only-on-failure' },
  webServer: {
    command: 'dotnet run --project Build.fsproj -- serve',
    url: 'http://localhost:8080',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000
  }
});
