import { test, expect } from "@playwright/test";
import {
  readLatestEmail,
  setQuietWindowAroundNow,
  startPublicSession,
  targetForProtocol,
  withDriver,
} from "./support/session_helpers.mjs";

function buildStatusPayload(overrides = {}) {
  return {
    status: "Charging",
    sessionStage: "charging",
    reservationId: "33333333-3333-3333-3333-333333333333",
    chargePointId: "ACE0816130",
    connectorId: 1,
    startTransactionAtUtc: "2026-03-12T18:00:00",
    stopTransactionAtUtc: null,
    disconnectedAtUtc: null,
    maxEnergyKwh: 80,
    maxAmountCents: 4670,
    capturedAmountCents: null,
    currency: "eur",
    liveOcppStatus: "Charging",
    liveChargeRateKw: 11.2,
    liveSessionEnergyKwh: 1.5,
    liveMeterKwh: 1.5,
    transactionMeterStart: 0,
    transactionEnergyKwh: 0,
    transactionEnergyCost: 0,
    transactionSessionFeeAmount: 0,
    transactionIdleFeeAmount: 0,
    liveIdleFeeAmount: 0,
    liveIdleFeeMinutes: 0,
    pricePerKwh: 0.4,
    userSessionFee: 0.5,
    invoice: null,
    ...overrides,
  };
}

async function mockStatusSequence(page, reservationId, payloads) {
  let index = 0;
  await page.route(`**/Payments/StatusData?reservationId=${reservationId}*`, async (route) => {
    const payload = payloads[Math.min(index, payloads.length - 1)];
    index += 1;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(payload),
    });
  });
}

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

test("public status estimates live totals while persisted costs are still zero", async ({ page }) => {
  const reservationId = "33333333-3333-3333-3333-333333333333";
  await mockStatusSequence(page, reservationId, [
    buildStatusPayload({ reservationId }),
  ]);

  await page.goto(`/Payments/Status?reservationId=${reservationId}&origin=public&lang=en`);

  await expect(page.locator("#stat-energy")).toHaveText("1.5");
  await expect(page.locator('[data-i18n="status.label.currentTotal"]')).toHaveText("Estimated total");
  await expect(page.locator("#stat-cost")).toHaveText("1.10 EUR");
  await expect(page.locator("#bd-energy")).toHaveText("0.60 EUR");
  await expect(page.locator("#bd-session-fee")).toHaveText("0.50 EUR");
});
