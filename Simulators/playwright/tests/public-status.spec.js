import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";

function buildPayload(overrides = {})
{
  return {
    status: "Charging",
    sessionStage: "charging",
    reservationId: "11111111-1111-1111-1111-111111111111",
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
    liveChargeRateKw: 7.2,
    liveSessionEnergyKwh: 12.4,
    liveMeterKwh: 12.4,
    transactionMeterStart: 0,
    transactionEnergyKwh: 0,
    transactionEnergyCost: 3.72,
    transactionSessionFeeAmount: 0.5,
    transactionIdleFeeAmount: 0,
    liveIdleFeeAmount: 0,
    liveIdleFeeMinutes: 0,
    invoice: null,
    ...overrides,
  };
}

async function mockStatusSequence(page, reservationId, payloads)
{
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

test("keeps the session in charging flow until unplug and only then shows done", async ({ page }) =>
{
  const reservationId = "11111111-1111-1111-1111-111111111111";
  await mockStatusSequence(page, reservationId, [
    buildPayload(),
    buildPayload({
      status: "WaitingForDisconnect",
      sessionStage: "waitingForDisconnect",
      stopTransactionAtUtc: "2026-03-12T18:14:00",
      liveOcppStatus: "SuspendedEV",
      liveChargeRateKw: 0,
      liveSessionEnergyKwh: 24.6,
      liveMeterKwh: 24.6,
    }),
    buildPayload({
      status: "Completed",
      sessionStage: "done",
      stopTransactionAtUtc: "2026-03-12T18:14:00",
      disconnectedAtUtc: "2026-03-12T18:15:00",
      liveOcppStatus: "Available",
      liveChargeRateKw: 0,
      liveSessionEnergyKwh: 24.6,
      capturedAmountCents: 958,
      transactionEnergyCost: 9.08,
      transactionSessionFeeAmount: 0.5,
      transactionIdleFeeAmount: 0,
      liveIdleFeeAmount: 0,
      liveIdleFeeMinutes: 0,
    }),
  ]);

  await page.goto(`/Payments/Status?reservationId=${reservationId}&origin=public&lang=en`);

  await expect(page.locator("#status-badge-text")).toHaveText("Charging");
  await expect(page.locator("#view-charging")).toHaveClass(/active/);
  await expect(page.locator("#stat-energy")).toHaveText("12.4");

  await page.reload();

  await expect(page.locator("#status-badge-text")).toHaveText("Charging stopped, unplug vehicle");
  await expect(page.locator('[data-step="charging"]')).toHaveClass(/active/);
  await expect(page.locator("#view-charging")).toHaveClass(/active/);
  await expect(page.locator("#view-done")).not.toHaveClass(/active/);
  await expect(page.locator("#stat-energy")).toHaveText("24.6");

  await page.reload();

  await expect(page.locator("#status-badge-text")).toHaveText("Charging complete");
  await expect(page.locator('[data-step="done"]')).toHaveClass(/active/);
  await expect(page.locator("#view-done")).toHaveClass(/active/);
  await expect(page.locator("#done-energy")).toHaveText("24.6");
  await expect(page.locator("#done-stop")).not.toHaveText("-");
});

test("localizes waiting-for-disconnect and quiet-hours idle copy", async ({ page }) =>
{
  const reservationId = "22222222-2222-2222-2222-222222222222";
  await mockStatusSequence(page, reservationId, [
    buildPayload({
      reservationId,
      status: "WaitingForDisconnect",
      sessionStage: "waitingForDisconnect",
      stopTransactionAtUtc: "2026-03-12T19:30:00",
      liveOcppStatus: "SuspendedEV",
      liveChargeRateKw: 0,
      liveSessionEnergyKwh: 0.7,
      liveMeterKwh: 0.7,
      idleBillingPausedByWindow: true,
      liveIdleFeeAmount: 0,
      liveIdleFeeMinutes: 0,
    }),
  ]);

  await page.goto(`/Payments/Status?reservationId=${reservationId}&origin=public&lang=hr`);

  await expect(page.locator("#status-badge-text")).toHaveText("Punjenje zaustavljeno, ištekajte vozilo");
  await expect(page.locator("#stat-energy")).toHaveText("0.7");
  await expect(page.locator("#bd-idle-sub")).toHaveText("Naplata zauzeća je pauzirana tijekom mirnog razdoblja.");
  await expect(page.locator("#charger-sub")).toContainText("Konektor 1");
});

test("reopens the public session from the payment email link", async ({ page, baseURL }) =>
{
  const fixturePath = path.join(path.dirname(fileURLToPath(import.meta.url)), "../fixtures/payment-authorized-email.json");
  const emailFixture = JSON.parse(fs.readFileSync(fixturePath, "utf8"));
  const reservationId = emailFixture.reservationId;

  await mockStatusSequence(page, reservationId, [
    buildPayload({
      reservationId,
      status: "WaitingForDisconnect",
      sessionStage: "waitingForDisconnect",
      stopTransactionAtUtc: "2026-03-12T19:45:00",
      liveOcppStatus: "SuspendedEV",
      liveChargeRateKw: 0,
      liveSessionEnergyKwh: 24.6,
      liveMeterKwh: 24.6,
    }),
  ]);

  const actionUrl = new URL(emailFixture.actionUrl, baseURL).toString();
  await page.goto(actionUrl);

  await expect(page.locator("#charger-title")).toHaveText("ACE0816130");
  await expect(page.locator("#status-badge-text")).toHaveText("Charging stopped, unplug vehicle");
  await expect(page.locator("#stat-energy")).toHaveText("24.6");
});
