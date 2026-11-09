import { defineConfig } from "@playwright/test";

// Optional outbound proxy for running the suite against a remote deployment from a proxied network (SERK_E2E_PROXY=URL).
// Applies to both the browser and Playwright's API request context.
const proxyUrl = process.env.SERK_E2E_PROXY ? new URL(process.env.SERK_E2E_PROXY) : undefined;
const proxy = proxyUrl
  ? { server: `${proxyUrl.protocol}//${proxyUrl.host}`, username: decodeURIComponent(proxyUrl.username) || undefined, password: decodeURIComponent(proxyUrl.password) || undefined }
  : undefined;

export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  fullyParallel: false,
  workers: 1,
  reporter: [["list"], ["json", { outputFile: "../../artifacts/e2e-results.json" }]],
  use: {
    baseURL: process.env.SERK_E2E_BASE_URL ?? "http://localhost:5080",
    viewport: { width: 1366, height: 768 },
    deviceScaleFactor: 1,
    trace: "off",
    ...(proxy ? { proxy } : {}),
  },
  outputDir: "../../artifacts/e2e-output",
});
