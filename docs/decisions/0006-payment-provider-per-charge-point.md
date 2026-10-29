# 0006 - Payment provider per charge point

Status: accepted

## Context

[ADR 0005](0005-pluggable-payment-providers.md) made the payment provider pluggable, but `Payments:Provider` selected one provider for all new checkouts. Operators need some charge points on Worldline Saferpay while the rest stay on Stripe, with both providers active at the same time. A charge point's provider can change while its sessions are in flight.

## Decision

1. **Default plus nullable override.** `Payments:DefaultProvider` (Stripe when empty) names the provider for charge points without their own setting. `Payments:Provider` stays as a legacy alias; it is read only when `Payments:DefaultProvider` is empty, and the two naming different providers stop startup. Migration `AddChargePointPaymentProvider` adds a nullable `ChargePoint.PaymentProvider`; `NULL` means "use the default", so existing charge points are unchanged.
2. **One resolver.** `PaymentProviderResolver` implements `ChargePoint.PaymentProvider ?? Payments:DefaultProvider` and is used only when a checkout is created. An unknown or unconfigured provider throws `PaymentProviderUnavailableException`; there is no fallback to another provider.
3. **The reservation's provider is final.** The checkout stores the resolved provider on `ChargePaymentReservation.PaymentProvider` (ADR 0005). Confirm, resume, cancel, capture, authorization release, webhooks and recovery keep resolving the gateway from that stored value, never from the charge point.
4. **Both providers stay live.** Every gateway and every webhook route is registered whatever the default is. The payment API gate is `IsOperational` (any provider configured); `IsEnabled` now only reports whether the default provider is configured.
5. **Fail fast at startup.** After the database migration, `PaymentProviderStartupValidator` stops the server when any charge point overrides to an unknown or unconfigured provider, naming each one.
6. **Admin UI.** The charge point edit page offers Default, Stripe and Worldline Saferpay. The stored value is the canonical provider name (`Stripe`, `Saferpay`) or `NULL`.

## Alternatives Considered

- **Resolve the provider from the charge point on every operation.** Simpler to describe, but switching a charge point would send captures and releases of in-flight sessions to the wrong provider. Rejected.
- **Warn instead of failing at startup.** The runtime already fails closed per charge point, but a silent misconfiguration would leave a charge point unable to take payments until someone noticed. Failing fast was chosen; the cost is that a wrong admin selection blocks the next server restart until it is fixed.
- **Provider per owner or per connector.** Not needed for the current requirement; the override can be moved later without changing reservations.

## Consequences

- Deployments that set `Payments__Provider` must rename it to `Payments__DefaultProvider`, because the committed server `appsettings.json` now sets `Payments:DefaultProvider=Stripe` and conflicting values stop startup.
- Before disabling a provider, every charge point that overrides to it must be set back to Default, and its reservations must finish (see [Maintenance](../maintenance.md#switching-payment-providers)).
- The management app cannot see which providers the server has configured, so it cannot block selecting an unconfigured provider; the hint text and the startup check cover this.
- Provider-neutral checkout settings (`Payments:ReturnBaseUrl`, `Payments:Currency`, `Payments:ProductName`) remain shared by both providers.
