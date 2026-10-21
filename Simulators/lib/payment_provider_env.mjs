// Payment-provider switch shared by the local E2E harnesses.
//
// OCPP_E2E_PAYMENT_PROVIDER selects the PSP mock the server uses for NEW checkouts:
//   - "Stripe" (default, also when unset or empty): the historical Stripe mock setup.
//   - "Saferpay": Payments:Provider=Saferpay with the in-process Saferpay JSON API mock. The Stripe mock
//     settings stay in place so the Stripe gateway remains registered and configured for reservations
//     that Stripe created earlier.
//
// Both modes pin Payments:Provider and Payments:ReturnBaseUrl explicitly. The harnesses inherit the
// developer's environment and run with ASPNETCORE_ENVIRONMENT=Development (user secrets), so a Payments:*
// or Saferpay:* value set there must not switch the run to another provider or return host.
//
// Keep Simulators/run_local_regression.py in sync with this file (Python cannot import it).

export const PAYMENT_PROVIDER_ENV_VAR = "OCPP_E2E_PAYMENT_PROVIDER";
export const STRIPE_PROVIDER = "Stripe";
export const SAFERPAY_PROVIDER = "Saferpay";
export const DEFAULT_PAYMENT_PROVIDER = STRIPE_PROVIDER;
export const DEFAULT_MOCK_PAYER_EMAIL = "driver@example.test";

const KNOWN_PROVIDERS = new Map([
  ["stripe", STRIPE_PROVIDER],
  ["saferpay", SAFERPAY_PROVIDER],
]);

function trimTrailingSlash(value) {
  return String(value ?? "").trim().replace(/\/+$/, "");
}

/** Returns "Stripe" or "Saferpay"; throws for any other non-empty value so a typo never runs the wrong PSP. */
export function resolvePaymentProvider(env = process.env) {
  const raw = String(env?.[PAYMENT_PROVIDER_ENV_VAR] ?? "").trim();
  if (!raw) {
    return DEFAULT_PAYMENT_PROVIDER;
  }

  const provider = KNOWN_PROVIDERS.get(raw.toLowerCase());
  if (!provider) {
    throw new Error(`Unsupported ${PAYMENT_PROVIDER_ENV_VAR}='${raw}'. Supported values: ${[...KNOWN_PROVIDERS.values()].join(", ")}.`);
  }

  return provider;
}

export function isStripeProvider(provider = resolvePaymentProvider()) {
  return provider === STRIPE_PROVIDER;
}

export function isSaferpayProvider(provider = resolvePaymentProvider()) {
  return provider === SAFERPAY_PROVIDER;
}

/**
 * Extra OCPP server environment for the selected provider. Stripe mode pins the provider, the return base
 * URL and a disabled Saferpay gateway; the Stripe mock settings themselves are set by each harness.
 */
export function buildPaymentProviderServerEnv({
  provider = resolvePaymentProvider(),
  managementBaseUrl,
  serverApiBaseUrl,
  mockPayerEmail = DEFAULT_MOCK_PAYER_EMAIL,
} = {}) {
  if (!managementBaseUrl) {
    throw new Error("managementBaseUrl is required for the payment provider harness environment.");
  }

  if (!isSaferpayProvider(provider)) {
    return {
      Payments__Provider: STRIPE_PROVIDER,
      Payments__ReturnBaseUrl: trimTrailingSlash(managementBaseUrl),
      Saferpay__Enabled: "false",
    };
  }

  if (!serverApiBaseUrl) {
    throw new Error("managementBaseUrl and serverApiBaseUrl are required for the Saferpay harness environment.");
  }

  return {
    Payments__Provider: SAFERPAY_PROVIDER,
    Payments__ReturnBaseUrl: trimTrailingSlash(managementBaseUrl),
    Saferpay__Enabled: "true",
    Saferpay__UseMockServices: "true",
    Saferpay__MockPayerEmail: mockPayerEmail,
    Saferpay__NotifyBaseUrl: trimTrailingSlash(serverApiBaseUrl),
  };
}

/**
 * Extra management-app environment. The management app reads Payments:Provider only to decide whether
 * the Stripe webhook-secret admin banner applies. Pinned in both modes, like the server.
 */
export function buildPaymentProviderManagementEnv({ provider = resolvePaymentProvider() } = {}) {
  return { Payments__Provider: isSaferpayProvider(provider) ? SAFERPAY_PROVIDER : STRIPE_PROVIDER };
}

/**
 * Throws when a checkout URL was not produced by the selected provider's mock. The Saferpay mock adds
 * provider=Saferpay to /Payments/MockCheckout and no session_id; the Stripe mock adds session_id and no
 * provider parameter.
 */
export function assertCheckoutUrlMatchesProvider(checkoutUrl, provider = resolvePaymentProvider(), label = "checkout") {
  const params = new URL(checkoutUrl).searchParams;
  const checkoutProvider = params.get("provider");

  if (isSaferpayProvider(provider)) {
    if (checkoutProvider !== SAFERPAY_PROVIDER) {
      throw new Error(`Expected a ${SAFERPAY_PROVIDER} mock checkout for ${label}, got provider=${checkoutProvider ?? "(none)"}.`);
    }

    return;
  }

  if (checkoutProvider !== null && checkoutProvider !== STRIPE_PROVIDER) {
    throw new Error(`Expected a ${STRIPE_PROVIDER} mock checkout for ${label}, got provider=${checkoutProvider}.`);
  }

  if (!params.get("session_id")) {
    throw new Error(`Expected a ${STRIPE_PROVIDER} mock checkout with session_id for ${label}, got none.`);
  }
}
