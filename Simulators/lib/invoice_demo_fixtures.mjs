import { runSqlite, sqlQuote } from "./sqlite_helpers.mjs";

const chargePointId = "INVOICE-DEMO-LOCAL";

export const invoiceDemoFixtures = Object.freeze({
  foreignEditable: Object.freeze({
    reservationId: "10000000-0000-4000-8000-000000000001",
    chargePointId,
    connectorId: 1,
    buyer: Object.freeze({
      country: "CZ",
      companyName: "Example Praha s.r.o.",
      street: "Testovaci 10",
      postalCode: "110 00",
      city: "Praha",
      email: "invoice-prague@example.test",
      taxIdentifier: "CZ00000001",
      registrationNumber: "00000001",
      identifierIsVatRegistration: true,
    }),
  }),
  croatianEditable: Object.freeze({
    reservationId: "10000000-0000-4000-8000-000000000002",
    chargePointId,
    connectorId: 2,
    buyer: Object.freeze({
      country: "HR",
      companyName: "Primjer Zagreb d.o.o.",
      street: "Testna ulica 20",
      postalCode: "10000",
      city: "Zagreb",
      email: "invoice-zagreb@example.test",
      invalidOib: "12345678901",
      taxIdentifier: "69435151530",
      identifierIsVatRegistration: false,
    }),
  }),
  foreignLocked: Object.freeze({
    reservationId: "10000000-0000-4000-8000-000000000003",
    chargePointId,
    connectorId: 3,
    buyer: Object.freeze({
      country: "CZ",
      companyName: "Locked Brno s.r.o.",
      street: "Ukazkova 30",
      postalCode: "602 00",
      city: "Brno",
      email: "invoice-brno@example.test",
      taxIdentifier: "CZ00000003",
      registrationNumber: "00000003",
      identifierIsVatRegistration: true,
      confirmed: true,
      confirmedAtUtc: "2026-07-15T09:00:00.000Z",
    }),
    invoiceLog: Object.freeze({
      provider: "LocalDemo",
      mode: "Disabled",
      status: "Completed",
      invoiceKind: "R1",
      externalDocumentId: "LOCAL-DEMO-INVOICE-0001",
    }),
  }),
});

export function buildInvoiceDemoMockStripeSnapshot(createdAt = new Date().toISOString()) {
  const generatedAtUtc = new Date(createdAt).toISOString();
  const fixtures = Object.values(invoiceDemoFixtures);
  const metadataFor = (fixture) => ({
    reservation_id: fixture.reservationId,
    charge_point_id: fixture.chargePointId,
    connector_id: String(fixture.connectorId),
    charge_tag_id: `DEMO-TAG-${fixture.connectorId}`,
  });

  return {
    generatedAtUtc,
    sessions: fixtures.map((fixture) => ({
      id: `cs_test_invoice_demo_${fixture.connectorId}`,
      url: "http://127.0.0.1/local-invoice-demo",
      paymentIntentId: `pi_test_invoice_demo_${fixture.connectorId}`,
      status: "complete",
      paymentStatus: "paid",
      metadata: metadataFor(fixture),
    })),
    paymentIntents: fixtures.map((fixture) => ({
      id: `pi_test_invoice_demo_${fixture.connectorId}`,
      status: "requires_capture",
      amount: 1000,
      amountReceived: 650,
      metadata: metadataFor(fixture),
    })),
  };
}

function reservationRow(fixture, transactionId, nowIso, includeBuyer) {
  const buyer = includeBuyer ? fixture.buyer : {};

  return `(
    ${sqlQuote(fixture.reservationId)},
    ${sqlQuote(fixture.chargePointId)},
    ${fixture.connectorId},
    ${sqlQuote(`DEMO-TAG-${fixture.connectorId}`)},
    20.0,
    0.35,
    0.50,
    0.00,
    0.00,
    0.00,
    1000,
    0.20,
    0,
    180,
    1,
    'EUR',
    ${sqlQuote(`cs_test_invoice_demo_${fixture.connectorId}`)},
    ${sqlQuote(`pi_test_invoice_demo_${fixture.connectorId}`)},
    'Completed',
    ${sqlQuote(nowIso)},
    ${sqlQuote(nowIso)},
    ${sqlQuote(nowIso)},
    ${sqlQuote(nowIso)},
    ${transactionId},
    650,
    12.5,
    ${sqlQuote(`DEMO-ID-${fixture.connectorId}`)},
    ${sqlQuote(nowIso)},
    ${sqlQuote(nowIso)},
    ${sqlQuote(nowIso)},
    ${sqlQuote(nowIso)},
    ${sqlQuote(fixture.reservationId.toUpperCase())},
    ${sqlQuote(buyer.country)},
    ${sqlQuote(buyer.companyName)},
    ${sqlQuote(buyer.street)},
    ${sqlQuote(buyer.postalCode)},
    ${sqlQuote(buyer.city)},
    ${sqlQuote(buyer.email)},
    ${sqlQuote(buyer.taxIdentifier)},
    ${sqlQuote(buyer.registrationNumber)},
    ${buyer.identifierIsVatRegistration === undefined ? "NULL" : buyer.identifierIsVatRegistration ? 1 : 0},
    ${sqlQuote(buyer.confirmedAtUtc)}
  )`;
}

export function buildInvoiceDemoSql(nowIso) {
  const normalizedNowIso = new Date(nowIso).toISOString();
  const fixtures = Object.values(invoiceDemoFixtures);
  const reservationIds = fixtures.map(({ reservationId }) => sqlQuote(reservationId)).join(", ");
  const locked = invoiceDemoFixtures.foreignLocked;

  return `
BEGIN TRANSACTION;
DELETE FROM InvoiceSubmissionLog WHERE ReservationId IN (${reservationIds});
DELETE FROM ChargePaymentReservation WHERE ReservationId IN (${reservationIds});
DELETE FROM ConnectorStatus WHERE ChargePointId = ${sqlQuote(chargePointId)} AND ConnectorId IN (1, 2, 3);
DELETE FROM ChargePoint WHERE ChargePointId = ${sqlQuote(chargePointId)};

INSERT INTO ChargePoint (
  ChargePointId,
  Name,
  PublicDisplayCode,
  Description,
  FreeChargingEnabled,
  PricePerKwh,
  UserSessionFee,
  OwnerSessionFee,
  OwnerCommissionPercent,
  OwnerCommissionFixedPerKwh,
  MaxSessionKwh,
  StartUsageFeeAfterMinutes,
  MaxUsageFeeMinutes,
  ConnectorUsageFeePerMinute,
  UsageFeeAfterChargingEnds,
  Latitude,
  Longitude,
  LocationDescription
) VALUES (
  ${sqlQuote(chargePointId)},
  'Local invoice demo',
  'XX*DEMO*000001',
  'Disposable local company invoice fixture',
  0,
  0.35,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  0,
  180,
  0.20,
  1,
  0.0,
  0.0,
  'Local-only test location'
);

INSERT INTO ConnectorStatus (
  ChargePointId,
  ConnectorId,
  ConnectorName,
  LastStatus,
  LastStatusTime
) VALUES
  (${sqlQuote(chargePointId)}, 1, 'Czech editable demo', 'Available', ${sqlQuote(normalizedNowIso)}),
  (${sqlQuote(chargePointId)}, 2, 'Croatian editable demo', 'Available', ${sqlQuote(normalizedNowIso)}),
  (${sqlQuote(chargePointId)}, 3, 'Czech locked demo', 'Available', ${sqlQuote(normalizedNowIso)});

INSERT INTO ChargePaymentReservation (
  ReservationId,
  ChargePointId,
  ConnectorId,
  ChargeTagId,
  MaxEnergyKwh,
  PricePerKwh,
  UserSessionFee,
  OwnerSessionFee,
  OwnerCommissionPercent,
  OwnerCommissionFixedPerKwh,
  MaxAmountCents,
  UsageFeePerMinute,
  StartUsageFeeAfterMinutes,
  MaxUsageFeeMinutes,
  UsageFeeAnchorMinutes,
  Currency,
  StripeCheckoutSessionId,
  StripePaymentIntentId,
  Status,
  CreatedAtUtc,
  UpdatedAtUtc,
  AuthorizedAtUtc,
  CapturedAtUtc,
  TransactionId,
  CapturedAmountCents,
  ActualEnergyKwh,
  OcppIdTag,
  StartTransactionAtUtc,
  StopTransactionAtUtc,
  DisconnectedAtUtc,
  LastOcppEventAtUtc,
  ActiveConnectorKey,
  InvoiceBuyerCountry,
  InvoiceBuyerCompanyName,
  InvoiceBuyerStreet,
  InvoiceBuyerPostalCode,
  InvoiceBuyerCity,
  InvoiceBuyerEmail,
  InvoiceBuyerTaxIdentifier,
  InvoiceBuyerRegistrationNumber,
  InvoiceBuyerIdentifierIsVatRegistration,
  InvoiceBuyerConfirmedAtUtc
) VALUES
  ${reservationRow(invoiceDemoFixtures.foreignEditable, 910001, normalizedNowIso, false)},
  ${reservationRow(invoiceDemoFixtures.croatianEditable, 910002, normalizedNowIso, false)},
  ${reservationRow(locked, 910003, normalizedNowIso, true)};

INSERT INTO InvoiceSubmissionLog (
  ReservationId,
  TransactionId,
  Provider,
  Mode,
  Status,
  InvoiceKind,
  ProviderOperation,
  ExternalDocumentId,
  ProviderResponseStatus,
  CreatedAtUtc,
  CompletedAtUtc
) VALUES (
  ${sqlQuote(locked.reservationId)},
  910003,
  ${sqlQuote(locked.invoiceLog.provider)},
  ${sqlQuote(locked.invoiceLog.mode)},
  ${sqlQuote(locked.invoiceLog.status)},
  ${sqlQuote(locked.invoiceLog.invoiceKind)},
  'LocalFixture',
  ${sqlQuote(locked.invoiceLog.externalDocumentId)},
  'Synthetic local completion',
  ${sqlQuote(normalizedNowIso)},
  ${sqlQuote(normalizedNowIso)}
);
COMMIT;`;
}

export async function seedInvoiceDemoFixtures(dbPath) {
  await runSqlite(dbPath, buildInvoiceDemoSql(new Date().toISOString()));
}
