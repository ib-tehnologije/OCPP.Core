/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System.Collections.Generic;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    /// <summary>Worldline Saferpay JSON API settings, bound from the <c>Saferpay</c> section.</summary>
    public class SaferpayOptions
    {
        public const string TestBaseUrl = "https://test.saferpay.com/api";
        public const string LiveBaseUrl = "https://www.saferpay.com/api";

        /// <summary>Default <see cref="CheckoutLifetimeMinutes"/>: Saferpay considers payment page sessions older than one hour abandoned.</summary>
        public const int DefaultCheckoutLifetimeMinutes = 60;

        /// <summary>
        /// Payment methods offered when <see cref="PaymentMethods"/> is empty: card brands that support a deferred
        /// (later, partial) capture of a (pre-)authorization.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultPaymentMethods = new[] { "VISA", "MASTERCARD", "MAESTRO" };

        /// <summary>Mock <see cref="MockAssertOutcome"/> values.</summary>
        public const string MockOutcomeAuthorized = "Authorized";
        public const string MockOutcomeCaptured = "Captured";
        public const string MockOutcomeDeclined = "Declined";
        public const string MockOutcomeAborted = "Aborted";
        public const string MockOutcomeInProgress = "InProgress";

        public bool Enabled { get; set; } = false;

        /// <summary>
        /// JSON API base URL: <see cref="TestBaseUrl"/> (default) or <see cref="LiveBaseUrl"/>. Outside mock mode it must
        /// be an absolute https URL, because every request carries the API password as basic authentication; an http
        /// URL leaves the gateway unconfigured.
        /// </summary>
        public string BaseUrl { get; set; } = TestBaseUrl;

        /// <summary>Saferpay customer id (first part of the account id, e.g. 123456 in 123456-12345678).</summary>
        public string CustomerId { get; set; }

        /// <summary>8-digit Saferpay terminal id.</summary>
        public string TerminalId { get; set; }

        /// <summary>JSON API basic-authentication user. Supply through user secrets or environment variables.</summary>
        public string ApiUsername { get; set; }

        /// <summary>JSON API basic-authentication password. Supply through user secrets or environment variables.</summary>
        public string ApiPassword { get; set; }

        public string SpecVersion { get; set; } = "1.54";

        /// <summary>
        /// Public base URL of the OCPP server <c>/API</c> endpoint (e.g. https://ocpp.example.com/API) that
        /// Saferpay calls for success/fail notifications. When empty, no notification URLs are sent and the
        /// flow relies on the browser return plus the cleanup sweep. Use https: the URLs carry a bearer signature.
        /// An http URL is still sent, but outside mock mode the gateway logs a warning.
        /// </summary>
        public string NotifyBaseUrl { get; set; }

        /// <summary>Key used to sign notification URLs. Falls back to a key derived from <see cref="ApiPassword"/>.</summary>
        public string NotifySecret { get; set; }

        /// <summary>
        /// Request a pre-authorization instead of a final authorization. Recommended for EV charging because
        /// the captured amount is usually lower than the held amount; the acquirer must enable it.
        /// </summary>
        public bool PreAuth { get; set; } = true;

        /// <summary>
        /// Payment methods offered on the payment page. When null or empty, <see cref="DefaultPaymentMethods"/>
        /// (VISA, MASTERCARD, MAESTRO) is sent; a non-empty list replaces that default.
        /// </summary>
        /// <remarks>
        /// The charging flow holds an amount at checkout and captures the final (usually lower) amount after the
        /// session, so it needs deferred capture. Do not add payment methods that capture immediately (Saferpay's
        /// payment method chapters state which methods "do not need the capture"): Saferpay reports them as
        /// <c>CAPTURED</c> at checkout, which charges the payer the full held amount and needs a manual refund
        /// (see <see cref="IPaymentGateway.CapturedCheckoutRequiresRefund"/>).
        /// The default lives in the gateway rather than in this property because configuration binding appends
        /// configured array items to an initialized array instead of replacing it.
        /// </remarks>
        public string[] PaymentMethods { get; set; }

        /// <summary>Optional wallet filter (e.g. APPLEPAY, GOOGLEPAY). Wallets pay with a card and support deferred capture.</summary>
        public string[] Wallets { get; set; }

        /// <summary>Optional payment page language (ISO 639-1, e.g. hr). Empty lets Saferpay detect the browser language.</summary>
        public string LanguageCode { get; set; }

        /// <summary>Optional payment page configuration set name from the Saferpay back office.</summary>
        public string ConfigSet { get; set; }

        /// <summary>
        /// Optional 3-D Secure condition: NONE, THREE_DS_AUTHENTICATION_SUCCESSFUL_OR_ATTEMPTED or
        /// WITH_SUCCESSFUL_THREE_DS_CHALLENGE.
        /// </summary>
        public string Condition { get; set; }

        /// <summary>
        /// Ask the payer for an e-mail address on the payment page so receipts, idle-fee warnings and
        /// invoices can be delivered (Saferpay, unlike Stripe Checkout, does not collect one by default).
        /// </summary>
        public bool CollectPayerEmail { get; set; } = true;

        public int TimeoutSeconds { get; set; } = 100;

        /// <summary>
        /// How long a payer can still complete a payment page after it was created (Saferpay: up to one hour, normally
        /// at most 20 minutes). Reported as <see cref="IPaymentGateway.CheckoutLifetime"/>. 0 or less means
        /// <see cref="DefaultCheckoutLifetimeMinutes"/>.
        /// </summary>
        public int CheckoutLifetimeMinutes { get; set; } = DefaultCheckoutLifetimeMinutes;

        /// <summary>Use an in-process fake of the Saferpay JSON API for local simulator and browser runs.</summary>
        public bool UseMockServices { get; set; } = false;

        /// <summary>Management base URL hosting <c>/Payments/MockCheckout</c> in mock mode. Falls back to the checkout return URL host.</summary>
        public string MockCheckoutBaseUrl { get; set; }

        /// <summary>Payer e-mail the mock reports in <c>Payer.BillingAddress.Email</c>. Defaults to <c>driver@example.test</c>.</summary>
        public string MockPayerEmail { get; set; }

        /// <summary>
        /// Mock mode only: what PaymentPage/Assert reports for a payment page without a transaction yet.
        /// <list type="bullet">
        /// <item><c>Authorized</c> (default, also for empty or unknown values): creates an AUTHORIZED transaction.</item>
        /// <item><c>Captured</c>: creates a CAPTURED transaction, like a payment method without deferred capture.</item>
        /// <item><c>Declined</c>: TRANSACTION_DECLINED, no transaction.</item>
        /// <item><c>Aborted</c>: TRANSACTION_ABORTED (payer canceled on the payment page), no transaction.</item>
        /// <item><c>InProgress</c>: TRANSACTION_IN_WRONG_STATE (payer has not finished yet), no transaction.</item>
        /// </list>
        /// The value is read on every Assert, so a test can switch from <c>InProgress</c> to <c>Authorized</c> to
        /// model the payer finishing the page.
        /// </summary>
        public string MockAssertOutcome { get; set; }

        /// <summary>
        /// Mock mode only: Transaction/Inquire reports TRANSACTION_NOT_FOUND for this many seconds after a transaction
        /// was authorized, modelling Saferpay's documented Inquire update delay. 0 (default) disables the delay.
        /// Capture and Cancel are not delayed.
        /// </summary>
        public int MockInquireNotFoundSeconds { get; set; }
    }
}
