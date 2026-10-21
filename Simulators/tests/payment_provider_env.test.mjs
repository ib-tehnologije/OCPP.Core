import assert from "node:assert/strict";
import test from "node:test";

import {
  PAYMENT_PROVIDER_ENV_VAR,
  assertCheckoutUrlMatchesProvider,
  buildPaymentProviderManagementEnv,
  buildPaymentProviderServerEnv,
  resolvePaymentProvider,
} from "../lib/payment_provider_env.mjs";

const stripeCheckoutUrl =
  "http://127.0.0.1:8082/Payments/MockCheckout?reservationId=r1&session_id=mock_sess_1&successUrl=x&cancelUrl=y";
const saferpayCheckoutUrl =
  "http://127.0.0.1:8082/Payments/MockCheckout?reservationId=r1&successUrl=x&cancelUrl=y&provider=Saferpay";

test("defaults to Stripe and accepts provider names case-insensitively", () => {
  assert.equal(resolvePaymentProvider({}), "Stripe");
  assert.equal(resolvePaymentProvider({ [PAYMENT_PROVIDER_ENV_VAR]: "  " }), "Stripe");
  assert.equal(resolvePaymentProvider({ [PAYMENT_PROVIDER_ENV_VAR]: "stripe" }), "Stripe");
  assert.equal(resolvePaymentProvider({ [PAYMENT_PROVIDER_ENV_VAR]: "SAFERPAY" }), "Saferpay");
});

test("rejects unknown providers instead of silently running Stripe", () => {
  assert.throws(
    () => resolvePaymentProvider({ [PAYMENT_PROVIDER_ENV_VAR]: "Safer-pay" }),
    /Unsupported OCPP_E2E_PAYMENT_PROVIDER='Safer-pay'/,
  );
});

test("Stripe pins the provider so developer Payments:* or Saferpay:* settings cannot switch the run", () => {
  assert.deepEqual(
    buildPaymentProviderServerEnv({ provider: "Stripe", managementBaseUrl: "http://127.0.0.1:8082/" }),
    {
      Payments__Provider: "Stripe",
      Payments__ReturnBaseUrl: "http://127.0.0.1:8082",
      Saferpay__Enabled: "false",
    },
  );
  assert.deepEqual(buildPaymentProviderManagementEnv({ provider: "Stripe" }), { Payments__Provider: "Stripe" });
  assert.throws(() => buildPaymentProviderServerEnv({ provider: "Stripe" }), /managementBaseUrl is required/);
});

test("Saferpay selects the provider and the in-process mock with server notifications", () => {
  assert.deepEqual(
    buildPaymentProviderServerEnv({
      provider: "Saferpay",
      managementBaseUrl: "http://127.0.0.1:8082/",
      serverApiBaseUrl: "http://127.0.0.1:8081/API/",
    }),
    {
      Payments__Provider: "Saferpay",
      Payments__ReturnBaseUrl: "http://127.0.0.1:8082",
      Saferpay__Enabled: "true",
      Saferpay__UseMockServices: "true",
      Saferpay__MockPayerEmail: "driver@example.test",
      Saferpay__NotifyBaseUrl: "http://127.0.0.1:8081/API",
    },
  );
  assert.deepEqual(buildPaymentProviderManagementEnv({ provider: "Saferpay" }), { Payments__Provider: "Saferpay" });
  assert.throws(() => buildPaymentProviderServerEnv({ provider: "Saferpay" }), /required/);
});

test("checkout URLs are checked against the selected provider's mock", () => {
  assert.doesNotThrow(() => assertCheckoutUrlMatchesProvider(stripeCheckoutUrl, "Stripe"));
  assert.doesNotThrow(() => assertCheckoutUrlMatchesProvider(saferpayCheckoutUrl, "Saferpay"));
  assert.throws(
    () => assertCheckoutUrlMatchesProvider(stripeCheckoutUrl, "Saferpay", "1.6/stop_then_unplug"),
    /Expected a Saferpay mock checkout for 1.6\/stop_then_unplug, got provider=\(none\)/,
  );
  assert.throws(
    () => assertCheckoutUrlMatchesProvider(saferpayCheckoutUrl, "Stripe", "2.0.1/stop_then_unplug"),
    /Expected a Stripe mock checkout for 2.0.1\/stop_then_unplug, got provider=Saferpay/,
  );
  assert.throws(
    () => assertCheckoutUrlMatchesProvider(
      "http://127.0.0.1:8082/Payments/MockCheckout?reservationId=r1&successUrl=x&cancelUrl=y",
      "Stripe",
      "2.1/stop_then_unplug",
    ),
    /Expected a Stripe mock checkout with session_id for 2.1\/stop_then_unplug/,
  );
});
