/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;

namespace OCPP.Core.Server.Payments.Providers
{
    /// <summary>
    /// Provider-specific payment operations used by the provider-neutral payment coordinator.
    /// One implementation exists per payment service provider (PSP). The coordinator owns the
    /// reservation lifecycle, pricing and settlement; a gateway only talks to its PSP and maps
    /// PSP objects to the neutral snapshots below.
    /// </summary>
    /// <remarks>
    /// All calls are synchronous because the coordinator's lifecycle methods are synchronous.
    /// Gateways must throw <see cref="PaymentProviderException"/> for every PSP or transport
    /// failure so the coordinator can classify it without knowing the PSP SDK.
    /// </remarks>
    public interface IPaymentGateway
    {
        /// <summary>Canonical provider name, one of <see cref="PaymentProviderNames"/>. Persisted on reservations.</summary>
        string ProviderName { get; }

        /// <summary>True when the gateway has the credentials and settings it needs to call its PSP.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Token the PSP replaces with its own checkout reference when redirecting the payer to the
        /// success URL (Stripe: <c>{CHECKOUT_SESSION_ID}</c>), or null when the PSP appends nothing.
        /// When non-null the coordinator adds <c>&amp;session_id={placeholder}</c> to the success URL.
        /// </summary>
        string SuccessUrlSessionPlaceholder { get; }

        /// <summary>True when the PSP stores free-form key/value metadata on checkouts and authorizations.</summary>
        bool SupportsMetadata { get; }

        /// <summary>
        /// True when <see cref="GetCheckout"/> can be called long after checkout (hours later) to read
        /// payer details for receipts and invoices. When false, the coordinator relies on the payer
        /// details persisted on the reservation at authorization time.
        /// </summary>
        bool SupportsDeferredCheckoutLookup { get; }

        /// <summary>Creates a hosted checkout that authorizes (but does not capture) <see cref="PaymentCheckoutRequest.AmountMinor"/>.</summary>
        PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request);

        /// <summary>
        /// Reads the current state of a hosted checkout. Returns null when the PSP reports that the
        /// checkout does not exist.
        /// </summary>
        PaymentCheckoutStatus GetCheckout(string sessionReference);

        /// <summary>Reads the current state of an authorization. Returns null when the PSP reports that it does not exist.</summary>
        PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference);

        /// <summary>
        /// Captures <paramref name="amountMinor"/> (which may be lower than the authorized amount) and
        /// releases any uncaptured remainder. Must be safe to repeat with the same idempotency key.
        /// </summary>
        PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey);

        /// <summary>Cancels an uncaptured authorization and releases the held funds.</summary>
        PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey);

        /// <summary>
        /// Applies <paramref name="mutate"/> to the checkout metadata and, when an authorization exists,
        /// to the authorization metadata, then persists both at the PSP. Returns the checkout metadata
        /// as it was before the mutation. Only valid when <see cref="SupportsMetadata"/> is true.
        /// Throws <see cref="InvalidOperationException"/> when the checkout no longer exists.
        /// </summary>
        IReadOnlyDictionary<string, string> MirrorMetadata(
            string sessionReference,
            string fallbackAuthorizationReference,
            Action<IDictionary<string, string>> mutate);

        /// <summary>
        /// Authenticates and parses an inbound PSP notification. Never trusts unauthenticated data:
        /// a rejected notification returns <see cref="PaymentWebhookParseResult.Accepted"/> = false.
        /// </summary>
        PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request);

        /// <summary>Redacts PSP identifiers and secrets from text that is persisted or shown to users.</summary>
        string SanitizeProviderText(string text);

        /// <summary>
        /// Maximum time a payer can still complete a hosted checkout after it was created, for PSPs that do not
        /// reliably signal late completions (Saferpay payment page: 1 hour). While a checkout of such a PSP may
        /// still complete, a terminal reservation must keep polling for a late authorization instead of treating
        /// the checkout as closed. Null when late completions are always signalled (Stripe webhooks).
        /// </summary>
        TimeSpan? CheckoutLifetime => null;

        /// <summary>
        /// True when an authorization that is already <see cref="PaymentAuthorizationState.Captured"/> at checkout
        /// means the payer was charged the full held amount (payment methods without deferred capture). The
        /// coordinator then refuses to start a session on it and flags it for a manual refund.
        /// </summary>
        bool CapturedCheckoutRequiresRefund => false;

        /// <summary>
        /// Looks up a successful authorization by reservation id when the checkout reference can no longer be
        /// used (Saferpay Transaction/Inquire by OrderId). Returns null when none exists or the PSP cannot search.
        /// </summary>
        PaymentAuthorizationSnapshot FindAuthorizationByReservation(Guid reservationId) => null;
    }

    /// <summary>Resolves gateways: the active one for new checkouts and the owning one for existing reservations.</summary>
    public interface IPaymentGatewayRegistry
    {
        /// <summary>
        /// Gateway selected by <c>Payments:DefaultProvider</c>; used for new checkouts on charge points without
        /// their own provider. Pick the gateway for a new checkout with <see cref="PaymentProviderResolver"/>.
        /// </summary>
        IPaymentGateway Default { get; }

        /// <summary>
        /// Gateway that owns a reservation created under <paramref name="providerName"/>. Null or empty
        /// resolves to Stripe (rows created before provider tracking). Returns null for an unknown or
        /// unregistered provider.
        /// </summary>
        IPaymentGateway Resolve(string providerName);

        IReadOnlyCollection<IPaymentGateway> All { get; }
    }

    public static class PaymentProviderNames
    {
        public const string Stripe = "Stripe";
        public const string Saferpay = "Saferpay";

        public static readonly IReadOnlyList<string> Known = new[] { Stripe, Saferpay };

        /// <summary>
        /// Maps a configured or persisted provider name to its canonical spelling. Null, empty or
        /// whitespace maps to <see cref="Stripe"/>; an unknown name returns null.
        /// </summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Stripe;
            }

            var trimmed = value.Trim();
            foreach (var known in Known)
            {
                if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return null;
        }
    }

    public sealed class PaymentCheckoutRequest
    {
        public Guid ReservationId { get; set; }
        public string ChargePointId { get; set; }
        public int ConnectorId { get; set; }

        /// <summary>Maximum amount to authorize, in minor currency units.</summary>
        public long AmountMinor { get; set; }

        /// <summary>Reservation currency as stored (ISO 4217, any case). Gateways normalize it for their PSP.</summary>
        public string Currency { get; set; }

        /// <summary>Line item name / payment description shown to the payer.</summary>
        public string ProductName { get; set; }

        /// <summary>
        /// Browser return URL after a completed (or, for single-return PSPs, any) checkout outcome.
        /// Already contains <see cref="IPaymentGateway.SuccessUrlSessionPlaceholder"/> when the gateway has one.
        /// </summary>
        public string SuccessUrl { get; set; }

        /// <summary>Browser return URL when the payer abandons the checkout (PSPs with a separate cancel URL).</summary>
        public string CancelUrl { get; set; }

        /// <summary>Optional e-mail to prefill at the PSP.</summary>
        public string CustomerEmail { get; set; }

        /// <summary>Reservation metadata (reservation_id, charge_point_id, ...). Ignored by PSPs without metadata.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }

        public string IdempotencyKey { get; set; }
    }

    public sealed class PaymentCheckoutSession
    {
        /// <summary>PSP checkout reference (Stripe Checkout Session id, Saferpay PaymentPage token).</summary>
        public string SessionReference { get; set; }

        /// <summary>PSP authorization reference when already known at creation (Stripe PaymentIntent id); otherwise null.</summary>
        public string AuthorizationReference { get; set; }

        /// <summary>Hosted payment page URL the payer is redirected to.</summary>
        public string RedirectUrl { get; set; }

        public DateTime? ExpiresAtUtc { get; set; }
    }

    public enum PaymentCheckoutState
    {
        Unknown = 0,
        /// <summary>Checkout can still be completed by the payer (or is in progress).</summary>
        Open,
        /// <summary>Payer completed the checkout; an authorization exists.</summary>
        Complete,
        /// <summary>Checkout timed out without an authorization.</summary>
        Expired,
        /// <summary>Payer aborted the checkout.</summary>
        Canceled,
        /// <summary>Payment was declined or failed authentication.</summary>
        Failed
    }

    public sealed class PaymentCheckoutStatus
    {
        public string SessionReference { get; set; }
        public PaymentCheckoutState State { get; set; }

        /// <summary>PSP status text, kept for logs and for API status values (e.g. Stripe "open", "expired").</summary>
        public string RawStatus { get; set; }

        /// <summary>True when the PSP reports the checkout as paid/authorized.</summary>
        public bool IsPaid { get; set; }

        public string AuthorizationReference { get; set; }

        /// <summary>Authorization returned inline by the PSP (Saferpay Assert). Null when it must be fetched separately.</summary>
        public PaymentAuthorizationSnapshot Authorization { get; set; }

        /// <summary>URL to resume an open checkout, or null when the PSP cannot resume it.</summary>
        public string ResumeUrl { get; set; }

        public string CustomerEmail { get; set; }
        public string CustomerName { get; set; }

        /// <summary>Checkout metadata (empty for PSPs without metadata). Keys compare case-insensitively.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }

        /// <summary>Reservation id the PSP associates with this checkout (metadata reservation_id, Saferpay OrderId).</summary>
        public Guid? OwnerReservationId { get; set; }

        public string FailureCode { get; set; }
        public string FailureMessage { get; set; }
    }

    public enum PaymentAuthorizationState
    {
        Unknown = 0,
        /// <summary>Authorization is not yet usable (payment method missing, confirmation or action pending, processing).</summary>
        Pending,
        /// <summary>Funds are held and can be captured or released.</summary>
        RequiresCapture,
        Captured,
        Canceled,
        Failed
    }

    public sealed class PaymentAuthorizationSnapshot
    {
        public string AuthorizationReference { get; set; }
        public PaymentAuthorizationState State { get; set; }

        /// <summary>PSP status text (Stripe PaymentIntent status, Saferpay Transaction.Status).</summary>
        public string RawStatus { get; set; }

        public long AmountMinor { get; set; }
        public long AmountCapturableMinor { get; set; }
        public long AmountReceivedMinor { get; set; }
        public string Currency { get; set; }

        /// <summary>True when <see cref="IPaymentGateway.Cancel"/> is allowed in the current state.</summary>
        public bool IsCancelable { get; set; }

        public Guid? OwnerReservationId { get; set; }
        public IReadOnlyDictionary<string, string> Metadata { get; set; }

        /// <summary>PSP capture reference when captured (Saferpay CaptureId); null otherwise.</summary>
        public string CaptureReference { get; set; }

        /// <summary>Last payment error reported by the PSP, if any.</summary>
        public string FailureMessage { get; set; }
    }

    /// <summary>
    /// Provider-neutral checkout details handed to e-mail and invoice services after authorization.
    /// Built from a live PSP lookup when <see cref="IPaymentGateway.SupportsDeferredCheckoutLookup"/>
    /// is true, otherwise from the payer details persisted on the reservation.
    /// </summary>
    public sealed class PaymentCheckoutDetails
    {
        public string Provider { get; set; }
        public string SessionReference { get; set; }
        public string AuthorizationReference { get; set; }
        public string CustomerEmail { get; set; }
        public string CustomerName { get; set; }
        public IReadOnlyDictionary<string, string> Metadata { get; set; }

        /// <summary>Returns a trimmed non-empty metadata value, or null.</summary>
        public string GetMetadataValue(string key)
        {
            if (Metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            foreach (var pair in Metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }

            return null;
        }
    }

    public sealed class PaymentWebhookRequest
    {
        /// <summary>Provider segment from the route (/API/Payments/Webhook/{provider}); null for the legacy route.</summary>
        public string Provider { get; set; }
        public string Method { get; set; }
        public IReadOnlyDictionary<string, string> Query { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body { get; set; }
    }

    public enum PaymentWebhookEventKind
    {
        Ignored = 0,
        /// <summary>Checkout completed (Stripe checkout.session.completed). Carries <see cref="PaymentWebhookEvent.Checkout"/>.</summary>
        CheckoutCompleted,
        /// <summary>Checkout expired (Stripe checkout.session.expired). Carries <see cref="PaymentWebhookEvent.Checkout"/>.</summary>
        CheckoutExpired,
        /// <summary>Authorization failed (Stripe payment_intent.payment_failed). Carries <see cref="PaymentWebhookEvent.Authorization"/>.</summary>
        AuthorizationFailed,
        /// <summary>Authorization became capturable (Stripe payment_intent.amount_capturable_updated). Carries <see cref="PaymentWebhookEvent.Authorization"/>.</summary>
        AuthorizationCapturable,
        /// <summary>
        /// Content-free "something changed" signal for <see cref="PaymentWebhookEvent.ReservationId"/>
        /// (Saferpay notify). The coordinator must re-read the checkout from the PSP.
        /// </summary>
        CheckoutNotification
    }

    public sealed class PaymentWebhookEvent
    {
        /// <summary>PSP event id used for duplicate suppression; null when the PSP has none.</summary>
        public string EventId { get; set; }
        public string EventType { get; set; }
        public PaymentWebhookEventKind Kind { get; set; }
        public DateTime? CreatedAtUtc { get; set; }
        public PaymentCheckoutStatus Checkout { get; set; }
        public PaymentAuthorizationSnapshot Authorization { get; set; }

        /// <summary>Reservation the notification is about, when the PSP (or our signed notify URL) identifies it.</summary>
        public Guid? ReservationId { get; set; }
    }

    public sealed class PaymentWebhookParseResult
    {
        public bool Accepted { get; set; }

        /// <summary>HTTP status to answer the PSP with when the notification is rejected or cannot be processed.</summary>
        public int StatusCode { get; set; } = 200;

        public string RejectReason { get; set; }
        public IReadOnlyList<PaymentWebhookEvent> Events { get; set; } = Array.Empty<PaymentWebhookEvent>();

        public static PaymentWebhookParseResult Rejected(string reason, int statusCode = 200) =>
            new PaymentWebhookParseResult { Accepted = false, RejectReason = reason, StatusCode = statusCode };
    }

    /// <summary>
    /// A PSP call failed. <see cref="Exception.Message"/> keeps the PSP/SDK message so existing
    /// diagnostics stay comparable; <see cref="Code"/> is a stable, parseable error code.
    /// </summary>
    public class PaymentProviderException : Exception
    {
        public PaymentProviderException(
            string provider,
            string code,
            string message,
            bool isTransient,
            int? httpStatusCode = null,
            string providerMessage = null,
            Exception innerException = null)
            : base(message, innerException)
        {
            Provider = provider;
            Code = code;
            IsTransient = isTransient;
            HttpStatusCode = httpStatusCode;
            ProviderMessage = providerMessage ?? message;
        }

        public string Provider { get; }

        /// <summary>Stable error code (Stripe error code/type, Saferpay ErrorName, or a gateway-defined code).</summary>
        public string Code { get; }

        /// <summary>True when retrying later may succeed (timeouts, throttling, 5xx, PSP RETRY behaviour).</summary>
        public bool IsTransient { get; }

        public int? HttpStatusCode { get; }

        /// <summary>PSP-provided error message (may be shown in sanitized form).</summary>
        public string ProviderMessage { get; }
    }
}
