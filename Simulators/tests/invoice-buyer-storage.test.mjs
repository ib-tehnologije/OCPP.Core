import assert from "node:assert/strict";
import test from "node:test";

await import("../../OCPP.Core.Management/wwwroot/js/invoice-buyer-storage.js");

function memoryStorage(initial = {}) {
  const values = new Map(Object.entries(initial));
  return {
    getItem(key) {
      return values.has(key) ? values.get(key) : null;
    },
    setItem(key, value) {
      values.set(key, String(value));
    },
    removeItem(key) {
      values.delete(key);
    },
  };
}

test("stores only versioned invoice buyer fields", () => {
  const storage = memoryStorage();

  globalThis.invoiceBuyerStorage.save(storage, {
    buyerCountry: " CZ ",
    buyerCompanyName: " Example s.r.o. ",
    buyerStreet: " Pražská 1 ",
    buyerPostalCode: "110 00",
    buyerCity: "Praha",
    buyerEmail: "billing@example.cz",
    buyerTaxIdentifier: "CZ 123-ABC",
    buyerRegistrationNumber: "C 12345",
    buyerIdentifierIsVatRegistration: true,
    reservationId: "must-not-be-stored",
    stripePaymentIntentId: "must-not-be-stored",
  });

  const raw = storage.getItem("ocpp.invoiceBuyer.v1");
  assert.ok(raw);
  assert.equal(raw.includes("reservationId"), false);
  assert.equal(raw.includes("stripePaymentIntentId"), false);
  assert.deepEqual(globalThis.invoiceBuyerStorage.load(storage), {
    buyerCountry: "CZ",
    buyerCompanyName: "Example s.r.o.",
    buyerStreet: "Pražská 1",
    buyerPostalCode: "110 00",
    buyerCity: "Praha",
    buyerEmail: "billing@example.cz",
    buyerTaxIdentifier: "CZ 123-ABC",
    buyerRegistrationNumber: "C 12345",
    buyerIdentifierIsVatRegistration: true,
  });
});

test("clears saved invoice buyer details", () => {
  const storage = memoryStorage();
  globalThis.invoiceBuyerStorage.save(storage, { buyerCountry: "HR", buyerTaxIdentifier: "12345678903" });

  globalThis.invoiceBuyerStorage.clear(storage);

  assert.equal(globalThis.invoiceBuyerStorage.load(storage), null);
});

test("ignores malformed and unavailable browser storage", () => {
  const malformed = memoryStorage({ "ocpp.invoiceBuyer.v1": "not-json" });
  const unavailable = {
    getItem() { throw new Error("denied"); },
    setItem() { throw new Error("denied"); },
    removeItem() { throw new Error("denied"); },
  };

  assert.equal(globalThis.invoiceBuyerStorage.load(malformed), null);
  assert.equal(globalThis.invoiceBuyerStorage.load(unavailable), null);
  assert.equal(globalThis.invoiceBuyerStorage.save(unavailable, { buyerCountry: "HR" }), false);
  assert.equal(globalThis.invoiceBuyerStorage.clear(unavailable), false);
});
