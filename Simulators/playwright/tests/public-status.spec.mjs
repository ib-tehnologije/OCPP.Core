import { expect, test } from "@playwright/test";
import {
  currentReservationId,
  protocolPresets,
  readLatestSinkEmail,
  runtimeInfo,
  startBrowserScenario,
  startPublicSession,
  waitForNonZeroEnergy,
} from "./helpers.mjs";

test.describe("Public payment flows", () => {
  test("keeps the session active until unplug across all protocols", async ({ page }) => {
    for (const protocol of ["1.6", "2.0.1", "2.1"]) {
      await test.step(protocol, async () => {
        const scenario = await startBrowserScenario(protocol, "stop_then_unplug");
        const preset = protocolPresets[protocol];

        await startPublicSession(page, preset);
        await expect(page).toHaveURL(/\/Payments\/Status\?/);
        await expect(page.locator("#status-badge-text")).toContainText(/Charging|Paused by vehicle/i);
        await waitForNonZeroEnergy(page);

        await expect(page.locator("#status-badge-text")).toContainText("Charging stopped, unplug vehicle", { timeout: 30_000 });
        await expect(page.locator(".public-session-nav [data-step='charge']")).toHaveClass(/active/);
        await expect(page.locator("#view-done")).not.toHaveClass(/active/);

        await expect(page.locator("#view-done")).toHaveClass(/active/, { timeout: 30_000 });
        await expect(page.locator("#done-stop")).not.toHaveText("-");

        const summary = await scenario.waitForCompletion();
        expect(summary.transactionId).toBeTruthy();
      });
    }
  });

  test("shows quiet-hours idle messaging and localizes dynamic status copy", async ({ page }) => {
    const scenario = await startBrowserScenario("1.6", "quiet_hours_idle_excluded");

    await startPublicSession(page, protocolPresets["1.6"]);
    await page.locator("[data-public-lang='hr']").click();
    await expect(page.locator(".public-lang-btn.is-active")).toHaveText("HR");

    await expect(page.locator("#status-badge-text")).toContainText("Punjenje zaustavljeno, ištekajte vozilo", { timeout: 30_000 });
    await expect(page.locator("#bd-idle-sub")).toContainText("Naplata zauzeća je pauzirana", { timeout: 30_000 });

    await expect(page.locator("#view-done")).toHaveClass(/active/, { timeout: 30_000 });
    await scenario.waitForCompletion();
  });

  test("reopens the same live session from the authorization email link", async ({ browser, page }) => {
    const scenario = await startBrowserScenario("1.6", "live_meter_progress");
    await startPublicSession(page, protocolPresets["1.6"]);

    const reservationId = currentReservationId(page);
    expect(reservationId).toBeTruthy();
    await waitForNonZeroEnergy(page);

    const email = readLatestSinkEmail("PaymentAuthorized");
    expect(email.actionText).toBe("Open session");
    expect(email.actionUrl).toContain(`reservationId=${reservationId}`);

    const secondPage = await browser.newPage({ baseURL: runtimeInfo().managementBaseUrl });
    await secondPage.goto(email.actionUrl);
    await expect(secondPage).toHaveURL(/\/Payments\/Status\?/);
    await expect(secondPage.locator("#charger-title")).not.toHaveText("-");
    await expect(secondPage.locator("#stat-energy")).not.toHaveText("-");

    await secondPage.close();
    await scenario.waitForCompletion();
  });
});
