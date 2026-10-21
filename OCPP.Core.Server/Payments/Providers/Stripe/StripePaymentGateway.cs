/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments.Providers
{
    /// <summary>
    /// Stripe Checkout Session + manual-capture PaymentIntent gateway. The coordinator passes the
    /// idempotency keys; this class only maps them onto Stripe request options and maps Stripe
    /// objects to the provider-neutral snapshots.
    /// </summary>
    public sealed class StripePaymentGateway : IPaymentGateway
    {
        /// <summary>Stripe replaces this token with the Checkout Session id in the success URL.</summary>
        public const string CheckoutSessionPlaceholder = "{CHECKOUT_SESSION_ID}";

        private const string ReservationIdMetadataKey = "reservation_id";
        private const string AmountCapturableUpdatedEventType = "payment_intent.amount_capturable_updated";

        private static readonly Regex ProviderIdentifierPattern = new(
            @"\b(?:pi|ch|cs|req|cus|pm|evt|in|src|tok)_[A-Za-z0-9_]+\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly StripeOptions _options;
        private readonly IStripeSessionService _sessionService;
        private readonly IStripePaymentIntentService _paymentIntentService;
        private readonly IStripeEventFactory _eventFactory;
        private readonly ILogger _logger;

        public StripePaymentGateway(StripeOptions options, ILogger logger = null)
            : this(options, new StripeSessionServiceWrapper(), new StripePaymentIntentServiceWrapper(), new StripeEventFactoryWrapper(), logger)
        {
        }

        internal StripePaymentGateway(
            StripeOptions options,
            IStripeSessionService sessionService,
            IStripePaymentIntentService paymentIntentService,
            IStripeEventFactory eventFactory,
            ILogger logger = null)
        {
            _options = options ?? new StripeOptions();
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _paymentIntentService = paymentIntentService ?? throw new ArgumentNullException(nameof(paymentIntentService));
            _eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            _logger = logger ?? NullLogger.Instance;

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                StripeConfiguration.ApiKey = _options.ApiKey;
            }
        }

        public string ProviderName => PaymentProviderNames.Stripe;

        public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

        public string SuccessUrlSessionPlaceholder => CheckoutSessionPlaceholder;

        public bool SupportsMetadata => true;

        public bool SupportsDeferredCheckoutLookup => true;

        public PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var metadata = request.Metadata == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(request.Metadata);

            var sessionOptions = new SessionCreateOptions
            {
                Mode = "payment",
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
                // Stripe remains payment-only. The reservation owns confirmed company-invoice data.
                BillingAddressCollection = "auto",
                CustomerEmail = request.CustomerEmail,
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    CaptureMethod = "manual",
                    Metadata = new Dictionary<string, string>(metadata)
                },
                Metadata = new Dictionary<string, string>(metadata),
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        Quantity = 1,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = request.Currency?.Trim().ToLowerInvariant(),
                            UnitAmount = request.AmountMinor,
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = request.ProductName
                            }
                        }
                    }
                }
            };

            Session session;
            try
            {
                session = _sessionService.Create(sessionOptions, BuildRequestOptions(request.IdempotencyKey));
            }
            catch (StripeException ex)
            {
                throw ToProviderException(ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw ToTransportException(ex);
            }

            return new PaymentCheckoutSession
            {
                SessionReference = session?.Id,
                AuthorizationReference = session?.PaymentIntentId,
                RedirectUrl = session?.Url,
                ExpiresAtUtc = session == null || session.ExpiresAt == default
                    ? null
                    : session.ExpiresAt.ToUniversalTime()
            };
        }

        public PaymentCheckoutStatus GetCheckout(string sessionReference)
        {
            try
            {
                return ToCheckoutStatus(_sessionService.Get(sessionReference));
            }
            catch (StripeException ex)
            {
                throw ToProviderException(ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw ToTransportException(ex);
            }
        }

        public PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference)
        {
            try
            {
                return ToAuthorizationSnapshot(_paymentIntentService.Get(authorizationReference));
            }
            catch (StripeException ex)
            {
                throw ToProviderException(ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw ToTransportException(ex);
            }
        }

        public PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey)
        {
            // Stripe releases the uncaptured remainder of a manual-capture PaymentIntent on partial capture.
            var captureOptions = new PaymentIntentCaptureOptions
            {
                AmountToCapture = amountMinor
            };

            try
            {
                return ToAuthorizationSnapshot(_paymentIntentService.Capture(
                    authorizationReference,
                    captureOptions,
                    BuildRequestOptions(idempotencyKey)));
            }
            catch (StripeException ex)
            {
                throw ToProviderException(ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw ToTransportException(ex);
            }
        }

        public PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey)
        {
            try
            {
                return ToAuthorizationSnapshot(_paymentIntentService.Cancel(
                    authorizationReference,
                    BuildRequestOptions(idempotencyKey)));
            }
            catch (StripeException ex)
            {
                throw ToProviderException(ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw ToTransportException(ex);
            }
        }

        public IReadOnlyDictionary<string, string> MirrorMetadata(
            string sessionReference,
            string fallbackAuthorizationReference,
            Action<IDictionary<string, string>> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));

            try
            {
                var session = _sessionService.Get(sessionReference);
                if (session == null)
                {
                    throw new InvalidOperationException(
                        $"Stripe checkout session '{sessionReference}' was not found.");
                }

                var previousMetadata = CopyMetadata(session.Metadata);
                var sessionMetadata = new Dictionary<string, string>(
                    session.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                mutate(sessionMetadata);
                _sessionService.Update(
                    sessionReference,
                    new SessionUpdateOptions { Metadata = sessionMetadata });

                var paymentIntentId = string.IsNullOrWhiteSpace(session.PaymentIntentId)
                    ? fallbackAuthorizationReference
                    : session.PaymentIntentId;
                if (!string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    var paymentIntent = _paymentIntentService.Get(paymentIntentId);
                    var intentMetadata = new Dictionary<string, string>(
                        paymentIntent?.Metadata ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
                    mutate(intentMetadata);
                    _paymentIntentService.Update(
                        paymentIntentId,
                        new PaymentIntentUpdateOptions { Metadata = intentMetadata });
                }

                return previousMetadata;
            }
            catch (StripeException ex)
            {
                throw ToProviderException(ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw ToTransportException(ex);
            }
        }

        public PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request)
        {
            var payload = request?.Body;
            var signatureHeader = GetHeader(request, "Stripe-Signature");

            Event stripeEvent;
            try
            {
                if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
                {
                    if (_options.AllowInsecureWebhooks)
                    {
                        _logger.LogWarning("Stripe webhook secret is not configured; processing webhook WITHOUT signature verification because AllowInsecureWebhooks=true.");
                        stripeEvent = EventUtility.ParseEvent(payload);
                    }
                    else
                    {
                        _logger.LogError("Stripe webhook secret is not configured; rejecting webhook because signature verification is required.");
                        return PaymentWebhookParseResult.Rejected("webhook_secret_missing");
                    }
                }
                else
                {
                    stripeEvent = _eventFactory.ConstructEvent(payload, signatureHeader, _options.WebhookSecret, throwOnApiVersionMismatch: true);
                }
            }
            catch (StripeException sex) when (IsApiVersionMismatch(sex))
            {
                var apiVersion = TryGetApiVersionFromPayload(payload);
                _logger.LogWarning(sex, "Stripe webhook API version mismatch (payload api_version={ApiVersion}); retrying without strict version check.", apiVersion ?? "(unknown)");
                try
                {
                    stripeEvent = _eventFactory.ConstructEvent(payload, signatureHeader, _options.WebhookSecret, throwOnApiVersionMismatch: false);
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "Stripe webhook validation failed after relaxing API version check: {Message}", ex2.Message);
                    return PaymentWebhookParseResult.Rejected("signature_invalid");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stripe webhook validation failed: {Message}", ex.Message);
                return PaymentWebhookParseResult.Rejected("signature_invalid");
            }

            if (stripeEvent == null)
            {
                return PaymentWebhookParseResult.Rejected("empty_event");
            }

            return new PaymentWebhookParseResult
            {
                Accepted = true,
                Events = new[] { ToWebhookEvent(stripeEvent) }
            };
        }

        public string SanitizeProviderText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return ProviderIdentifierPattern.Replace(text, "[redacted-id]");
        }

        /// <summary>Maps a Stripe Checkout Session to the details handed to e-mail and invoice services.</summary>
        public static PaymentCheckoutDetails ToCheckoutDetails(Session session)
        {
            if (session == null)
            {
                return null;
            }

            return new PaymentCheckoutDetails
            {
                Provider = PaymentProviderNames.Stripe,
                SessionReference = session.Id,
                AuthorizationReference = session.PaymentIntentId,
                CustomerEmail = session.CustomerDetails?.Email,
                CustomerName = session.CustomerDetails?.Name,
                Metadata = CopyMetadata(session.Metadata)
            };
        }

        internal static PaymentCheckoutStatus ToCheckoutStatus(Session session)
        {
            if (session == null)
            {
                return null;
            }

            var state = MapCheckoutState(session.Status);
            var metadata = CopyMetadata(session.Metadata);
            return new PaymentCheckoutStatus
            {
                SessionReference = session.Id,
                State = state,
                RawStatus = session.Status,
                IsPaid = string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase),
                AuthorizationReference = session.PaymentIntentId,
                ResumeUrl = state == PaymentCheckoutState.Open ? session.Url : null,
                CustomerEmail = session.CustomerDetails?.Email,
                CustomerName = session.CustomerDetails?.Name,
                Metadata = metadata,
                OwnerReservationId = ParseOwnerReservationId(session.Metadata)
            };
        }

        internal static PaymentAuthorizationSnapshot ToAuthorizationSnapshot(PaymentIntent paymentIntent)
        {
            if (paymentIntent == null)
            {
                return null;
            }

            return new PaymentAuthorizationSnapshot
            {
                AuthorizationReference = paymentIntent.Id,
                State = MapAuthorizationState(paymentIntent.Status),
                RawStatus = paymentIntent.Status,
                AmountMinor = paymentIntent.Amount,
                AmountCapturableMinor = paymentIntent.AmountCapturable,
                AmountReceivedMinor = paymentIntent.AmountReceived,
                Currency = paymentIntent.Currency,
                IsCancelable = IsCancelableStatus(paymentIntent.Status),
                OwnerReservationId = ParseOwnerReservationId(paymentIntent.Metadata),
                Metadata = CopyMetadata(paymentIntent.Metadata),
                FailureMessage = paymentIntent.LastPaymentError?.Message
            };
        }

        internal static PaymentProviderException ToProviderException(StripeException exception)
        {
            var code = exception?.StripeError?.Code ?? exception?.StripeError?.Type ?? "stripe_error";
            var statusCode = exception == null ? 0 : Convert.ToInt32(exception.HttpStatusCode);
            return new PaymentProviderException(
                PaymentProviderNames.Stripe,
                code,
                exception?.Message,
                IsTransient(exception),
                statusCode == 0 ? null : statusCode,
                exception?.StripeError?.Message ?? exception?.Message,
                exception);
        }

        /// <summary>
        /// Stripe.net surfaces connection failures as <see cref="System.Net.Http.HttpRequestException"/> and request
        /// timeouts as <see cref="System.Threading.Tasks.TaskCanceledException"/> (these calls carry no cancellation
        /// token). Both are retryable and must reach the coordinator as transient provider failures.
        /// </summary>
        private static bool IsTransportFailure(Exception exception) =>
            exception is System.Net.Http.HttpRequestException ||
            exception is System.Threading.Tasks.TaskCanceledException ||
            exception is TimeoutException;

        internal static PaymentProviderException ToTransportException(Exception exception)
        {
            var code = exception is System.Net.Http.HttpRequestException ? "network_error" : "timeout";
            return new PaymentProviderException(
                PaymentProviderNames.Stripe,
                code,
                $"Stripe request failed ({code}): {exception?.Message}",
                isTransient: true,
                httpStatusCode: null,
                providerMessage: exception?.Message,
                innerException: exception);
        }

        private static bool IsTransient(StripeException exception)
        {
            if (exception == null) return false;

            var statusCode = Convert.ToInt32(exception.HttpStatusCode);
            var type = exception.StripeError?.Type ?? string.Empty;
            return statusCode == 408 ||
                   statusCode == 409 ||
                   statusCode == 429 ||
                   statusCode >= 500 ||
                   string.Equals(type, "api_connection_error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "api_error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "rate_limit_error", StringComparison.OrdinalIgnoreCase);
        }

        private static PaymentWebhookEvent ToWebhookEvent(Event stripeEvent)
        {
            var webhookEvent = new PaymentWebhookEvent
            {
                EventId = stripeEvent.Id,
                EventType = stripeEvent.Type,
                Kind = PaymentWebhookEventKind.Ignored,
                CreatedAtUtc = stripeEvent.Created == default
                    ? null
                    : stripeEvent.Created.ToUniversalTime()
            };

            var dataObject = stripeEvent.Data?.Object;
            switch (stripeEvent.Type)
            {
                case EventTypes.CheckoutSessionCompleted:
                    webhookEvent.Kind = PaymentWebhookEventKind.CheckoutCompleted;
                    webhookEvent.Checkout = ToCheckoutStatus(dataObject as Session);
                    break;
                case EventTypes.CheckoutSessionExpired:
                    webhookEvent.Kind = PaymentWebhookEventKind.CheckoutExpired;
                    webhookEvent.Checkout = ToCheckoutStatus(dataObject as Session);
                    break;
                case EventTypes.PaymentIntentPaymentFailed:
                    webhookEvent.Kind = PaymentWebhookEventKind.AuthorizationFailed;
                    webhookEvent.Authorization = ToAuthorizationSnapshot(dataObject as PaymentIntent);
                    break;
                case AmountCapturableUpdatedEventType:
                    webhookEvent.Kind = PaymentWebhookEventKind.AuthorizationCapturable;
                    webhookEvent.Authorization = ToAuthorizationSnapshot(dataObject as PaymentIntent);
                    break;
            }

            webhookEvent.ReservationId = webhookEvent.Checkout?.OwnerReservationId ??
                                         webhookEvent.Authorization?.OwnerReservationId;
            return webhookEvent;
        }

        private static PaymentCheckoutState MapCheckoutState(string status)
        {
            if (string.Equals(status, "open", StringComparison.OrdinalIgnoreCase)) return PaymentCheckoutState.Open;
            if (string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)) return PaymentCheckoutState.Complete;
            if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)) return PaymentCheckoutState.Expired;
            return PaymentCheckoutState.Unknown;
        }

        private static PaymentAuthorizationState MapAuthorizationState(string status)
        {
            if (string.Equals(status, "requires_capture", StringComparison.OrdinalIgnoreCase)) return PaymentAuthorizationState.RequiresCapture;
            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)) return PaymentAuthorizationState.Captured;
            if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)) return PaymentAuthorizationState.Canceled;
            if (string.Equals(status, "requires_payment_method", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "requires_confirmation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "requires_action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentAuthorizationState.Pending;
            }

            return PaymentAuthorizationState.Unknown;
        }

        private static bool IsCancelableStatus(string status) =>
            string.Equals(status, "requires_capture", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "requires_payment_method", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "requires_confirmation", StringComparison.OrdinalIgnoreCase);

        private static Guid? ParseOwnerReservationId(IDictionary<string, string> metadata)
        {
            if (metadata == null ||
                !metadata.TryGetValue(ReservationIdMetadataKey, out var providerReservationId) ||
                !Guid.TryParse(providerReservationId, out var parsedReservationId))
            {
                return null;
            }

            return parsedReservationId;
        }

        private static IReadOnlyDictionary<string, string> CopyMetadata(IDictionary<string, string> metadata)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (metadata == null)
            {
                return copy;
            }

            foreach (var pair in metadata)
            {
                if (pair.Key != null)
                {
                    copy[pair.Key] = pair.Value;
                }
            }

            return copy;
        }

        private static RequestOptions BuildRequestOptions(string idempotencyKey) =>
            string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : new RequestOptions { IdempotencyKey = idempotencyKey };

        private static string GetHeader(PaymentWebhookRequest request, string name)
        {
            if (request?.Headers == null)
            {
                return null;
            }

            if (request.Headers.TryGetValue(name, out var value))
            {
                return value;
            }

            foreach (var pair in request.Headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static bool IsApiVersionMismatch(StripeException ex) =>
            ex?.Message?.IndexOf("API version", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string TryGetApiVersionFromPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var jobj = JsonConvert.DeserializeObject<JObject>(payload);
                return jobj?["api_version"]?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
