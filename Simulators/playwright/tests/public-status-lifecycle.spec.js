import { test, expect } from "@playwright/test";
import {
  setQuietWindowAroundNow,
  startPublicSession,
  targetForProtocol,
  withDriver,
} from "./support/session_helpers.mjs";

test.afterEach(async () => {
  try {
    await setQuietWindowAroundNow(false);
  } catch {
    // quiet-window toggling is only available in the heavy runner
  }
});

for (const protocol of ["1.6", "2.0.1", "2.1"]) {
  test(`public session stays active until unplug for OCPP ${protocol}`, async ({ page }) => {
    const target = targetForProtocol(protocol);

    await withDriver(target, "live_meter_progress", async (driver) => {
      const reservationId = await startPublicSession(page, target);
      expect(reservationId).toBeTruthy();

      await driver.signalPluggedIn();
      await driver.waitUntilStarted();

      await expect(page.locator("#charger-title")).toHaveText(target.chargePointId);
      await expect(page.locator("#status-badge-text")).toHaveText("Charging in progress");
      await expect(page.locator("#stop-charging")).toBeEnabled();
      await expect.poll(async () => {
        return (await page.locator("#stat-energy").textContent())?.trim() ?? "";
      }).not.toBe("0.0");

      await page.locator("#stop-charging").click();

      await expect(page.locator("#status-badge-text")).toHaveText("Charging stopped, unplug vehicle", {
        timeout: 30_000,
      });
      await expect(page.locator('[data-step="charging"]')).toHaveClass(/active/);
      await expect(page.locator('[data-step="done"]')).not.toHaveClass(/active/);

      await driver.waitUntilFinished();

      await expect(page.locator("#status-badge-text")).toHaveText("Charging complete", {
        timeout: 30_000,
      });
      await expect(page.locator('[data-step="done"]')).toHaveClass(/active/);
      await expect(page.locator("#done-stop")).not.toHaveText("-");
    });
  });
}
