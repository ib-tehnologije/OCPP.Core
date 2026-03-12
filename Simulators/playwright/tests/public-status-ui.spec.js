import { test, expect } from "@playwright/test";
import {
  readLatestEmail,
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

test("public status localizes waiting-for-disconnect and quiet-hours idle messaging", async ({ page }) => {
  const target = targetForProtocol("1.6");
  await setQuietWindowAroundNow(true);

  await withDriver(target, "quiet_hours_idle_excluded", async (driver) => {
    await startPublicSession(page, target);
    await driver.signalPluggedIn();
    await driver.waitUntilStarted();

    await expect(page.locator("#status-badge-text")).toHaveText("Paused by vehicle / idle", {
      timeout: 30_000,
    });
    await expect(page.locator("#bd-idle-sub")).toHaveText("Occupancy billing is paused during the quiet-hours window.");

    await page.locator('[data-public-lang="hr"]').click();

    await expect(page.locator("#status-badge-text")).toHaveText("Pauzirano od vozila / idle");
    await expect(page.locator("#bd-idle-sub")).toHaveText("Naplata zauzeća je pauzirana tijekom mirnog razdoblja.");

    await page.locator("#stop-charging").click();
    await expect(page.locator("#status-badge-text")).toHaveText("Punjenje zaustavljeno, ištekajte vozilo", {
      timeout: 30_000,
    });
  });
});

test("payment authorized email reopens the live session page", async ({ browser, page }) => {
  const target = targetForProtocol("2.0.1");

  await withDriver(target, "live_meter_progress", async (driver) => {
    const reservationId = await startPublicSession(page, target);
    await driver.signalPluggedIn();
    await driver.waitUntilStarted();

    await expect.poll(async () => {
      return Boolean(await readLatestEmail({
        eventName: "PaymentAuthorized",
        reservationId,
      }));
    }).toBeTruthy();

    const email = await readLatestEmail({
      eventName: "PaymentAuthorized",
      reservationId,
    });

    expect(email?.actionUrl).toContain(`/Payments/Status?reservationId=${reservationId}`);

    const reopenedPage = await browser.newPage();
    await reopenedPage.goto(email.actionUrl);
    await expect(reopenedPage).toHaveURL(new RegExp(`reservationId=${reservationId}`));
    await expect(reopenedPage.locator("#charger-title")).toHaveText(target.chargePointId);
    await expect(reopenedPage.locator("#status-badge-text")).toHaveText("Charging in progress");
    await reopenedPage.close();
  });
});
