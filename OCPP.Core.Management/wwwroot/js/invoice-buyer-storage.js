(function (root) {
  "use strict";

  const storageKey = "ocpp.invoiceBuyer.v1";
  const stringFields = [
    "buyerCountry",
    "buyerCompanyName",
    "buyerStreet",
    "buyerPostalCode",
    "buyerCity",
    "buyerEmail",
    "buyerTaxIdentifier",
    "buyerRegistrationNumber",
  ];

  function sanitize(details) {
    if (!details || typeof details !== "object" || Array.isArray(details)) {
      return null;
    }

    const result = {};
    stringFields.forEach((field) => {
      if (typeof details[field] !== "string") return;
      const value = details[field].trim();
      if (!value) return;
      result[field] = field === "buyerCountry" ? value.toUpperCase() : value;
    });
    if (typeof details.buyerIdentifierIsVatRegistration === "boolean") {
      result.buyerIdentifierIsVatRegistration = details.buyerIdentifierIsVatRegistration;
    }

    return Object.keys(result).length > 0 ? result : null;
  }

  function load(storage) {
    try {
      if (!storage || typeof storage.getItem !== "function") return null;
      const value = storage.getItem(storageKey);
      if (!value) return null;
      return sanitize(JSON.parse(value));
    } catch (_) {
      return null;
    }
  }

  function save(storage, details) {
    try {
      if (!storage || typeof storage.setItem !== "function") return false;
      const value = sanitize(details);
      if (!value) return false;
      storage.setItem(storageKey, JSON.stringify(value));
      return true;
    } catch (_) {
      return false;
    }
  }

  function clear(storage) {
    try {
      if (!storage || typeof storage.removeItem !== "function") return false;
      storage.removeItem(storageKey);
      return true;
    } catch (_) {
      return false;
    }
  }

  root.invoiceBuyerStorage = Object.freeze({ storageKey, load, save, clear });
})(globalThis);
