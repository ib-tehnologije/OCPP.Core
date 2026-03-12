import { defineConfig } from "@playwright/test";

const managementBaseUrl = process.env.MGMT_HTTP_BASE ?? "http://127.0.0.1:18082";
const useExistingStack = process.env.OCPP_PLAYWRIGHT_USE_EXISTING_STACK === "1";
const browserChannel = process.env.OCPP_PLAYWRIGHT_BROWSER_CHANNEL ?? (!process.env.CI ? "chrome" : "");

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  workers: 1,
  timeout: 120_000,
  expect: {
    timeout: 15_000,
  },
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report" }],
  ],
  use: {
    baseURL: managementBaseUrl,
    trace: "retain-on-failure",
    video: "retain-on-failure",
    screenshot: "only-on-failure",
    headless: process.env.PLAYWRIGHT_HEADLESS !== "0",
  },
  projects: [
    {
      name: browserChannel ? browserChannel : "chromium",
      use: {
        browserName: "chromium",
        ...(browserChannel ? { channel: browserChannel } : {}),
      },
    },
  ],
  webServer: useExistingStack
    ? undefined
    : {
        command: "node ./stack.mjs",
        url: `${managementBaseUrl}/Public/Map`,
        reuseExistingServer: !process.env.CI,
        timeout: 120_000,
      },
});
