import { defineConfig } from "@playwright/test";

const managementBaseUrl = process.env.MGMT_HTTP_BASE ?? "http://127.0.0.1:8082";
const channel = process.env.PLAYWRIGHT_CHROMIUM_CHANNEL || undefined;

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "public-status-lifecycle.spec.js",
    "public-status-ui.spec.js",
    "public-urgent-validation.spec.js",
  ],
  fullyParallel: false,
  workers: 1,
  timeout: 120_000,
  expect: {
    timeout: 20_000,
  },
  reporter: [["list"]],
  use: {
    baseURL: managementBaseUrl,
    browserName: "chromium",
    channel,
    headless: true,
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
    video: "retain-on-failure",
  },
});
