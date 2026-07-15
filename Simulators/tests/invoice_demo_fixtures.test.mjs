import assert from "node:assert/strict";
import test from "node:test";

import {
  buildInvoiceDemoMockStripeSnapshot,
  buildInvoiceDemoSql,
  invoiceDemoFixtures,
} from "../lib/invoice_demo_fixtures.mjs";

const expectedReservationIds = [
  "10000000-0000-4000-8000-000000000001",
  "10000000-0000-4000-8000-000000000002",
  "10000000-0000-4000-8000-000000000003",
];

function visitValues(value, callback) {
  if (Array.isArray(value)) {
    value.forEach((item) => visitValues(item, callback));
    return;
  }

  if (value && typeof value === "object") {
    Object.entries(value).forEach(([key, item]) => {
      callback(key, item);
      visitValues(item, callback);
    });
  }
}

test("defines three fixed public-safe invoice demo reservations", () => {
  assert.deepEqual(Object.keys(invoiceDemoFixtures), [
    "foreignEditable",
    "croatianEditable",
    "foreignLocked",
  ]);
  assert.deepEqual(
    Object.values(invoiceDemoFixtures).map(({ reservationId }) => reservationId),
    expectedReservationIds,
  );

  visitValues(invoiceDemoFixtures, (key, value) => {
    if (/email/i.test(key)) {
      assert.match(value, /@[^@]+\.test$/);
    }
    if (typeof value === "string") {
      assert.doesNotMatch(value, /(?:^|\.)evcharge\.hr(?:\/|$)/i);
    }
  });
});

test("locked fixture includes confirmed buyer data and an invoice marker", () => {
  const { buyer, invoiceLog } = invoiceDemoFixtures.foreignLocked;

  assert.equal(buyer.country, "CZ");
  assert.equal(buyer.confirmed, true);
  assert.ok(buyer.confirmedAtUtc);
  assert.ok(buyer.companyName);
  assert.ok(buyer.taxIdentifier);
  assert.deepEqual(invoiceLog, {
    provider: "LocalDemo",
    mode: "Disabled",
    status: "Completed",
    invoiceKind: "R1",
    externalDocumentId: "LOCAL-DEMO-INVOICE-0001",
  });
});

test("builds mock Stripe snapshot entries that match every fixture reservation", () => {
  const snapshot = buildInvoiceDemoMockStripeSnapshot("2026-07-15T10:00:00.000Z");
  assert.equal(snapshot.generatedAtUtc, "2026-07-15T10:00:00.000Z");
  assert.equal(snapshot.sessions.length, 3);
  assert.equal(snapshot.paymentIntents.length, 3);

  for (const [index, reservationId] of expectedReservationIds.entries()) {
    const fixtureNumber = index + 1;
    const session = snapshot.sessions.find(({ metadata }) => metadata.reservation_id === reservationId);
    const paymentIntent = snapshot.paymentIntents.find(({ metadata }) => metadata.reservation_id === reservationId);
    assert.equal(session?.id, `cs_test_invoice_demo_${fixtureNumber}`);
    assert.equal(session?.paymentIntentId, `pi_test_invoice_demo_${fixtureNumber}`);
    assert.equal(paymentIntent?.id, `pi_test_invoice_demo_${fixtureNumber}`);
  }
});

test("builds bounded SQL for one charge point, three connectors, three reservations, and one invoice log", () => {
  const sql = buildInvoiceDemoSql("2026-07-15T10:00:00.000Z");

  assert.match(sql, /^\s*BEGIN TRANSACTION;/);
  assert.match(sql, /COMMIT;\s*$/);
  assert.match(sql, /INSERT INTO ChargePoint\s*\(/);
  assert.match(sql, /INSERT INTO ConnectorStatus\s*\(/);
  assert.match(sql, /INSERT INTO ChargePaymentReservation\s*\(/);
  assert.match(sql, /INSERT INTO InvoiceSubmissionLog\s*\(/);
  assert.match(sql, /INVOICE-DEMO-LOCAL/);

  for (const reservationId of expectedReservationIds) {
    assert.match(sql, new RegExp(reservationId, "i"));
  }

  assert.doesNotMatch(sql, /DELETE FROM ChargePoint\s*;/);
  assert.doesNotMatch(sql, /DELETE FROM ConnectorStatus\s*;/);
  assert.doesNotMatch(sql, /DELETE FROM ChargePaymentReservation\s*;/);
  assert.doesNotMatch(sql, /DELETE FROM InvoiceSubmissionLog\s*;/);
  assert.equal((sql.match(/LOCAL-DEMO-INVOICE-0001/g) ?? []).length, 1);
});
