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

    return ["Submitted", "LoggedOnly", "Failed"].includes(log.status)
      ? log.status
      : null;
  }, {
    timeout: 90_000,
    message: `Expected invoice submission log for reservation ${reservationId}`,
  }).not.toBeNull();

  const invoiceLog = readLatestInvoiceSubmissionLog(reservationId);
  expect(invoiceLog).toBeTruthy();
  expect(invoiceLog.providerOperation).toBe("SalesInvoiceCreate");
  expect(invoiceLog.invoiceKind).toBe("R1");
  expect(invoiceLog.requestPayloadJson).toContain(buyerCompanyName);
  expect(invoiceLog.requestPayloadJson).toContain(buyerOib);

  if (invoiceLog.status === "Submitted") {
    expect(invoiceLog.externalDocumentId).toBeTruthy();
  } else if (invoiceLog.status === "Failed") {
    expect(invoiceLog.status).toBe("Failed");
    expect(invoiceLog.error || invoiceLog.responseBody || "").toContain("e-racuni");
  } else {
    expect(invoiceLog.status).toBe("LoggedOnly");
    expect(invoiceLog.mode).toBe("LogOnly");
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
  const tooltip = page.locator(".leaflet-tooltip");
  await expect(tooltip).toContainText("Mixed availability");
  await expect(tooltip).toContainText("1/2 available");
  await expect(mixedCard).toHaveClass(/active/);

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
      buyerCountry: "HR",
      buyerCompanyName,
      buyerStreet: "Ilica 1",
      buyerPostalCode: "10000",
      buyerCity: "Zagreb",
      buyerEmail: "invoices@example.test",
      buyerOib,
      buyerRegistrationNumber: "MBS-123",
      buyerIdentifierIsVatRegistration: true,
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

test("@invoice public start blocks incomplete Croatian buyer and invalidates stale confirmation", async ({ page }) => {
  test.skip(!invoicesEnabled, "Invoice validation requires OCPP_PLAYWRIGHT_ENABLE_INVOICES=1");

  await page.goto(`/Public/Start?cp=${encodeURIComponent(publicStartInvoiceTarget.chargePointId)}&conn=${publicStartInvoiceTarget.connectorId}`);
  await page.locator("#wantsR1").check();
  await page.locator("#buyerTaxIdentifier").fill("12345678903");
  await page.locator("#buyerDataConfirmed").check();

  await expect(page.locator("#buyerCompanyName")).toHaveAttribute("required", "");
  await page.locator("#buyerCompanyName").fill("Acme d.o.o.");
  await expect(page.locator("#buyerDataConfirmed")).not.toBeChecked();

  await page.locator("#buyerDataConfirmed").check();
  await page.locator('form[method="post"][action*="Start"] button[type="submit"]').click();
  await expect(page.locator("#buyerStreet")).toBeFocused();
  await expect(page).toHaveURL(/\/Public\/Start/);

  await page.evaluate(() => localStorage.setItem("ocpp.invoiceBuyer.v1", JSON.stringify({ version: 1, buyerCompanyName: "Shared device data" })));
  await page.locator("#wantsR1").uncheck();
  await page.evaluate(() => {
    const form = document.querySelector('form[method="post"][action*="Start"]');
    form.addEventListener("submit", event => event.preventDefault(), { once: true });
    form.requestSubmit();
  });
  await expect.poll(() => page.evaluate(() => localStorage.getItem("ocpp.invoiceBuyer.v1"))).toBeNull();
});

test("@invoice public start remembers buyer details without late status editing", async ({ page }) => {
  test.skip(!invoicesEnabled, "Invoice validation requires OCPP_PLAYWRIGHT_ENABLE_INVOICES=1");

  const buyerCompanyName = "Remembered Buyer d.o.o.";
  const buyerOib = "12345678903";

  await withDriver(publicStatusInvoiceTarget, "live_meter_progress", async (driver) => {
    const reservationId = await startPublicSession(page, publicStatusInvoiceTarget, {
      requestR1Invoice: true,
      buyerCountry: "HR",
      buyerCompanyName,
      buyerStreet: "Vukovarska 2",
      buyerPostalCode: "10000",
      buyerCity: "Zagreb",
      buyerEmail: "remembered@example.test",
      buyerOib,
      rememberInvoiceBuyer: true,
    });
    expect(reservationId).toBeTruthy();

    await expect(page.locator("#r1-submit")).toHaveCount(0);
    await waitForStripeMetadata(reservationId, buyerCompanyName, buyerOib);
    await completeChargingSession(page, driver);

    await page.goto(`/Public/Start?cp=${encodeURIComponent(publicStatusInvoiceTarget.chargePointId)}&conn=${publicStatusInvoiceTarget.connectorId}`);
    await expect(page.locator("#rememberInvoiceBuyer")).toBeChecked();
    await page.locator("#wantsR1").check();
    await expect(page.locator("#buyerCompanyName")).toHaveValue(buyerCompanyName);
    await expect(page.locator("#buyerTaxIdentifier")).toHaveValue(buyerOib);
    await expect(page.locator("#buyerEmail")).toHaveValue("remembered@example.test");
  });
});
