import { expect, test } from "@playwright/test";
import {
  findMockStripeArtifactsByReservationId,
  readLatestInvoiceSubmissionLog,
} from "./helpers.mjs";
import {
  startPublicSession,
  withDriver,
} from "./support/session_helpers.mjs";

const invoicesEnabled = process.env.OCPP_PLAYWRIGHT_ENABLE_INVOICES === "1";
const publicStartInvoiceTarget = {
  protocol: "1.6",
  chargePointId: "INVOICE-R1-16",
  connectorId: 1,
  chargeTagId: process.env.CP16_TAG ?? "PAY16",
};
const publicStatusInvoiceTarget = {
  protocol: "2.0.1",
  chargePointId: "INVOICE-R1-20",
  connectorId: 2,
  chargeTagId: process.env.CP20_TAG ?? "PAY20",
};

async function waitForInvoiceEvidence(reservationId, buyerCompanyName, buyerOib) {
  await expect.poll(() => {
    const log = readLatestInvoiceSubmissionLog(reservationId);
    if (!log?.status || !log?.requestPayloadJson) {
      return null;
    }

    return ["Submitted", "Failed"].includes(log.status)
      ? log.status
      : null;
  }, {
    timeout: 90_000,
    message: `Expected invoice submission log for reservation ${reservationId}`,
  }).not.toBeNull();

  const invoiceLog = readLatestInvoiceSubmissionLog(reservationId);
  expect(invoiceLog).toBeTruthy();
  expect(invoiceLog.mode).toBe("Submit");
  expect(invoiceLog.invoiceKind).toBe("R1");
  expect(invoiceLog.requestPayloadJson).toContain(buyerCompanyName);
  expect(invoiceLog.requestPayloadJson).toContain(buyerOib);

  if (invoiceLog.status === "Submitted") {
    expect(invoiceLog.externalDocumentId).toBeTruthy();
  } else {
    expect(invoiceLog.status).toBe("Failed");
    expect(invoiceLog.error || invoiceLog.responseBody || "").toContain("e-racuni");
  }

  return invoiceLog;
}

async function waitForStripeMetadata(reservationId, buyerCompanyName, buyerOib) {
  await expect.poll(() => {
    const { session, paymentIntent } = findMockStripeArtifactsByReservationId(reservationId);
    return Boolean(
      session?.metadata?.invoice_type === "R1" &&
      session?.metadata?.buyer_company === buyerCompanyName &&
      session?.metadata?.buyer_oib === buyerOib &&
      paymentIntent?.metadata?.invoice_type === "R1" &&
      paymentIntent?.metadata?.buyer_company === buyerCompanyName &&
      paymentIntent?.metadata?.buyer_oib === buyerOib);
  }, {
    timeout: 15_000,
    message: `Expected mock Stripe metadata for reservation ${reservationId}`,
  }).toBe(true);

  return findMockStripeArtifactsByReservationId(reservationId);
}

async function completeChargingSession(page, driver) {
  await driver.signalPluggedIn();
  await driver.waitUntilStarted();

  await expect(page.locator("#status-badge-text")).toHaveText("Charging in progress", {
    timeout: 30_000,
  });
  await expect(page.locator("#stop-charging")).toBeEnabled();

  await page.locator("#stop-charging").click();
  await expect(page.locator("#status-badge-text")).toHaveText("Charging stopped, unplug vehicle", {
    timeout: 30_000,
  });

  await driver.waitUntilFinished();
  await expect(page.locator("#status-badge-text")).toHaveText("Charging complete", {
    timeout: 30_000,
  });
}

test("public map shows mixed availability, offline normalization, and case-insensitive station status", async ({ page }) => {
  await page.goto("/Public/Map");

  const mixedCard = page.locator('.cp-card[data-cp-id="MAP-MIXED-01"]');
  await expect(mixedCard.locator(".cc-status")).toHaveText("Available");
  await expect(mixedCard).toContainText("1/2");
  await expect(mixedCard).toContainText("Available");

  await mixedCard.click();
  const popup = page.locator(".leaflet-popup-content");
  await expect(popup).toContainText("Mixed availability");
  await expect(popup).toContainText("Available");
  await expect(popup).toContainText("1/2 available");

  const offlineCard = page.locator('.cp-card[data-cp-id="MAP-OFFLINE-01"]');
  await expect(offlineCard.locator(".cc-status")).toHaveText("Offline");
  await expect(offlineCard).toContainText("Offline");
  await expect(offlineCard).not.toContainText("Unknown");

  const caseMismatchCard = page.locator('.cp-card[data-cp-id="map-case-01"]');
  await expect(caseMismatchCard.locator(".cc-status")).toHaveText("Available");
});

test("public start page renders offline connectors without raw unknown status", async ({ page }) => {
  await page.goto("/Public/Start?cp=MAP-OFFLINE-01&conn=1");

  await expect(page.locator("#connector-status-text")).toHaveText("Offline");
  await expect(page.locator("#availability-message")).toContainText("currently offline");
  await expect(page.locator(".connector-option.selected")).toContainText("Offline");
  await expect(page.locator("body")).not.toContainText("Unknown");
});

test("@invoice public start R1 details flow into Stripe metadata and invoice submission", async ({ page }) => {
  test.skip(!invoicesEnabled, "Invoice validation requires OCPP_PLAYWRIGHT_ENABLE_INVOICES=1");

  const buyerCompanyName = "Acme d.o.o.";
  const buyerOib = "12345678903";

  await withDriver(publicStartInvoiceTarget, "live_meter_progress", async (driver) => {
    const reservationId = await startPublicSession(page, publicStartInvoiceTarget, {
      requestR1Invoice: true,
      buyerCompanyName,
      buyerOib,
    });

    expect(reservationId).toBeTruthy();

    const stripeArtifacts = await waitForStripeMetadata(reservationId, buyerCompanyName, buyerOib);
    expect(stripeArtifacts.session?.metadata?.reservation_id).toBe(reservationId);

    await completeChargingSession(page, driver);

    const invoiceLog = await waitForInvoiceEvidence(reservationId, buyerCompanyName, buyerOib);
    await expect(page.locator("#done-invoice-section")).toBeVisible({ timeout: 30_000 });
    await expect(page.locator("#done-invoice-status")).toHaveText(invoiceLog.status);
  });
});

test("@invoice public status R1 submission updates metadata and produces an R1 invoice log", async ({ page }) => {
  test.skip(!invoicesEnabled, "Invoice validation requires OCPP_PLAYWRIGHT_ENABLE_INVOICES=1");

  const buyerCompanyName = "Status Buyer d.o.o.";
  const buyerOib = "12345678903";

  await withDriver(publicStatusInvoiceTarget, "live_meter_progress", async (driver) => {
    const reservationId = await startPublicSession(page, publicStatusInvoiceTarget);
    expect(reservationId).toBeTruthy();

    await page.locator("#r1-company").fill(buyerCompanyName);
    await page.locator("#r1-oib").fill(buyerOib);
    await page.locator("#r1-submit").click();
    await expect(page.locator("#r1-result")).toContainText("saved successfully");

    await waitForStripeMetadata(reservationId, buyerCompanyName, buyerOib);

    await completeChargingSession(page, driver);

    const invoiceLog = await waitForInvoiceEvidence(reservationId, buyerCompanyName, buyerOib);
    await expect(page.locator("#done-invoice-section")).toBeVisible({ timeout: 30_000 });
    await expect(page.locator("#done-invoice-status")).toHaveText(invoiceLog.status);
  });
});
