import { defineConfig } from "@playwright/test";

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
  },
  outputDir: "../../artifacts/e2e-output",
});
