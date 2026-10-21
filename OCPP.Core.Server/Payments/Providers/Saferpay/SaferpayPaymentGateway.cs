/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    /// <summary>
    /// Worldline Saferpay gateway: Payment Page checkout with a (pre-)authorization, later partial capture
    /// or cancel through the Transaction interface.
    /// </summary>
    /// <remarks>
    /// Mapping to the neutral contract:
    /// <list type="bullet">
    /// <item>Checkout reference = Payment Page <c>Token</c>; authorization reference = <c>Transaction.Id</c>.</item>
    /// <item>The reservation id is sent as <c>Payment.OrderId</c> and read back as the owner reservation.</item>
    /// <item>Saferpay has no metadata and its token can only be asserted for a limited time, so payer
    /// details must be persisted at authorization (<see cref="SupportsDeferredCheckoutLookup"/> is false).</item>
    /// <item>Saferpay never captures automatically. A capture can be done once and releases the uncaptured
    /// remainder; a zero amount is not allowed, so the coordinator cancels instead.</item>
    /// <item>Payment methods without deferred capture report <c>CAPTURED</c> at checkout, so only card brands are
    /// offered by default (<see cref="SaferpayOptions.DefaultPaymentMethods"/>) and a captured checkout needs a refund
    /// (<see cref="CapturedCheckoutRequiresRefund"/>).</item>
    /// <item>Transaction/Inquire lists only successful transactions and can lag by a few minutes, so its
    /// TRANSACTION_NOT_FOUND never proves that a known transaction does not exist.</item>
    /// </list>
    /// </remarks>
    public sealed class SaferpayPaymentGateway : IPaymentGateway
    {
        /// <summary>
        /// <see cref="PaymentAuthorizationSnapshot.RawStatus"/> of a capture that Saferpay reported as already done
        /// (TRANSACTION_ALREADY_CAPTURED) while Transaction/Inquire still showed the authorization: the state is
        /// Captured, but the captured amount is the requested one and not confirmed by Saferpay.
        /// </summary>
        public const string UnverifiedCaptureStatus = "CAPTURED_UNVERIFIED";

        internal const string DefaultDescription = "EV charging session";

        private const int MaxDescriptionLength = 1000;
        private const int MaxEmailLength = 254;

        private readonly SaferpayOptions _options;
        private readonly SaferpayApiClient _client;
        private readonly SaferpayNotificationSigner _signer;
        private readonly ILogger<SaferpayPaymentGateway> _logger;
        private int _insecureBaseUrlWarningLogged;
        private int _insecureNotifyUrlWarningLogged;

        public SaferpayPaymentGateway(
            IHttpClientFactory httpClientFactory,
            IOptions<SaferpayOptions> options,
            ILogger<SaferpayPaymentGateway> logger)
        {
            if (httpClientFactory == null) throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? new SaferpayOptions();
            _client = new SaferpayApiClient(httpClientFactory, _options, logger);
            _signer = new SaferpayNotificationSigner(_options);
        }

        public string ProviderName => PaymentProviderNames.Saferpay;

        /// <summary>
        /// True when the gateway is enabled and either uses the mock or has all live settings. Outside mock mode,
        /// <see cref="SaferpayOptions.BaseUrl"/> must be an absolute https URL, because every request carries the API
        /// password as basic authentication; an http URL keeps the gateway unconfigured and logs a warning once.
        /// </summary>
        public bool IsConfigured
        {
            get
            {
                if (!_options.Enabled)
                {
                    return false;
                }

                if (_options.UseMockServices)
                {
                    return true;
                }

                WarnAboutInsecureNotifyUrl();
                if (string.IsNullOrWhiteSpace(_options.BaseUrl) ||
                    string.IsNullOrWhiteSpace(_options.CustomerId) ||
                    string.IsNullOrWhiteSpace(_options.TerminalId) ||
                    string.IsNullOrWhiteSpace(_options.ApiUsername) ||
                    string.IsNullOrWhiteSpace(_options.ApiPassword))
                {
                    return false;
                }

                if (!SaferpayApiClient.IsAbsoluteHttpsUrl(_options.BaseUrl))
                {
                    if (Interlocked.Exchange(ref _insecureBaseUrlWarningLogged, 1) == 0)
                    {
                        _logger.LogWarning(
                            "Saferpay => Saferpay:BaseUrl is not an absolute https URL; the gateway stays unconfigured so the API credentials are never sent unencrypted.");
                    }

                    return false;
                }

                return true;
            }
        }

        /// <summary>Saferpay appends nothing to the return URL; the coordinator identifies the reservation with its own parameters.</summary>
        public string SuccessUrlSessionPlaceholder => null;

        public bool SupportsMetadata => false;

        public bool SupportsDeferredCheckoutLookup => false;

        /// <summary>
        /// A Saferpay payment page can still be completed for up to an hour after Initialize, and its success
        /// notification is not guaranteed (it needs <see cref="SaferpayOptions.NotifyBaseUrl"/>). Configurable through
        /// <see cref="SaferpayOptions.CheckoutLifetimeMinutes"/>.
        /// </summary>
        public TimeSpan? CheckoutLifetime =>
            TimeSpan.FromMinutes(_options.CheckoutLifetimeMinutes > 0
                ? _options.CheckoutLifetimeMinutes
                : SaferpayOptions.DefaultCheckoutLifetimeMinutes);

        /// <summary>
        /// A transaction that is already CAPTURED at checkout was paid with a method without deferred capture: the
        /// payer was charged the full held amount, and only a refund can return the difference.
        /// </summary>
        public bool CapturedCheckoutRequiresRefund => true;

        /// <summary>Signer for notification URLs; exposed so integration code and tests can build matching URLs.</summary>
        internal SaferpayNotificationSigner NotificationSigner => _signer;

        public PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.AmountMinor <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.AmountMinor,
                    "Saferpay checkouts require a positive amount in minor currency units.");
            }

            if (request.ReservationId == Guid.Empty) throw new ArgumentException("A reservation id is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.SuccessUrl)) throw new ArgumentException("A return URL is required.", nameof(request));

            string currency = NormalizeCurrency(request.Currency, nameof(request));
            EnsureConfigured("PaymentPage/Initialize");

            var payment = new JObject
            {
                ["Amount"] = BuildAmount(request.AmountMinor, currency),
                ["OrderId"] = request.ReservationId.ToString("D"),
                ["Description"] = BuildDescription(request.ProductName)
            };
            if (_options.PreAuth)
            {
                payment["Options"] = new JObject { ["PreAuth"] = true };
            }

            var payload = new JObject
            {
                ["TerminalId"] = _options.TerminalId?.Trim() ?? string.Empty,
                ["Payment"] = payment
            };

            AddValueList(payload, "PaymentMethods", ResolvePaymentMethods());
            AddValueList(payload, "Wallets", _options.Wallets);
            if (!string.IsNullOrWhiteSpace(_options.ConfigSet))
            {
                payload["ConfigSet"] = _options.ConfigSet.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_options.Condition))
            {
                payload["Condition"] = _options.Condition.Trim().ToUpperInvariant();
            }

            var payer = BuildPayer(request.CustomerEmail);
            if (payer != null)
            {
                payload["Payer"] = payer;
            }

            payload["ReturnUrl"] = new JObject { ["Url"] = request.SuccessUrl.Trim() };

            if (_signer.CanBuildNotifyUrls)
            {
                payload["Notification"] = new JObject
                {
                    ["SuccessNotifyUrl"] = _signer.BuildSuccessNotifyUrl(request.ReservationId),
                    ["FailNotifyUrl"] = _signer.BuildFailNotifyUrl(request.ReservationId)
                };
            }

            if (_options.CollectPayerEmail)
            {
                // Saferpay shows its address form; only the e-mail is mandatory. A Payer.BillingAddress sent
                // above prefills the form.
                payload["BillingAddressForm"] = new JObject
                {
                    ["AddressSource"] = "SAFERPAY",
                    ["MandatoryFields"] = new JArray("EMAIL")
                };
            }

            var response = _client.Post(SaferpayApiClient.InitializePath, payload, request.IdempotencyKey);
            string token = SaferpayApiClient.ReadString(response, "Token");
            string redirectUrl = SaferpayApiClient.ReadString(response, "RedirectUrl");
            if (token == null || redirectUrl == null)
            {
                throw InvalidResponse("PaymentPage/Initialize", "Token or RedirectUrl is missing.");
            }

            _logger.LogInformation(
                "Saferpay/Initialize => Payment page created reservation={ReservationId} amount={AmountMinor} currency={Currency}",
                request.ReservationId,
                request.AmountMinor,
                currency);

            return new PaymentCheckoutSession
            {
                SessionReference = token,
                AuthorizationReference = null,
                RedirectUrl = redirectUrl,
                ExpiresAtUtc = ParseUtcDate(SaferpayApiClient.ReadString(response, "Expiration"))
            };
        }

        public PaymentCheckoutStatus GetCheckout(string sessionReference)
        {
            if (string.IsNullOrWhiteSpace(sessionReference))
            {
                throw new ArgumentException("A Saferpay payment page token is required.", nameof(sessionReference));
            }

            EnsureConfigured("PaymentPage/Assert");
            string token = sessionReference.Trim();

            JObject response;
            try
            {
                response = _client.Post(SaferpayApiClient.AssertPath, new JObject { ["Token"] = token }, null);
            }
            catch (SaferpayApiException exception) when (exception.ErrorName != null)
            {
                if (exception.HasErrorName("TOKEN_EXPIRED"))
                {
                    return ResolveExpiredToken(token, exception);
                }

                if (TryMapAssertError(token, exception, out var status))
                {
                    _logger.LogInformation(
                        "Saferpay/Assert => {ErrorName} mapped to checkout state {State}",
                        exception.ErrorName,
                        status?.State.ToString() ?? "NotFound");
                    return status;
                }

                throw;
            }

            return MapCheckoutResponse(token, response, "PaymentPage/Assert");
        }

        /// <summary>
        /// Reads a transaction with Transaction/Inquire. Never returns null: callers only hold transaction ids from a
        /// successful Assert, and Inquire's TRANSACTION_NOT_FOUND is indeterminate for them (see below), so it is raised
        /// as a transient <see cref="PaymentProviderException"/> with code <c>inquire_not_found</c>.
        /// </summary>
        public PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference)
        {
            if (string.IsNullOrWhiteSpace(authorizationReference))
            {
                throw new ArgumentException("A Saferpay transaction id is required.", nameof(authorizationReference));
            }

            EnsureConfigured("Transaction/Inquire");
            JObject response;
            try
            {
                response = _client.Post(SaferpayApiClient.InquirePath, TransactionReference(authorizationReference.Trim()), null);
            }
            catch (SaferpayApiException exception) when (exception.HasErrorName("TRANSACTION_NOT_FOUND"))
            {
                // Inquire lists only successful transactions and its data can lag by a few minutes behind Assert, so a
                // transaction authorized moments ago can be "not found". Treating that as "does not exist" would skip
                // the cancel or capture of a real hold; report it as transient so the caller retries later.
                throw new SaferpayApiException(
                    "Transaction/Inquire",
                    "inquire_not_found",
                    "Saferpay Transaction/Inquire did not find the transaction; Inquire lists only successful transactions and can lag by a few minutes.",
                    isTransient: true,
                    httpStatusCode: exception.HttpStatusCode,
                    providerMessage: exception.ProviderMessage,
                    innerException: exception);
            }

            return MapInquiredTransaction(response);
        }

        /// <summary>
        /// Transaction/Inquire by <c>OrderId</c>, which is the reservation id sent with Initialize. Inquire lists only
        /// successful transactions, so null means Saferpay knows no successful authorization for the reservation (or,
        /// within a few minutes of authorizing, has not indexed it yet). Use it for checkouts that can no longer be
        /// asserted, not instead of Assert.
        /// </summary>
        public PaymentAuthorizationSnapshot FindAuthorizationByReservation(Guid reservationId)
        {
            if (reservationId == Guid.Empty)
            {
                throw new ArgumentException("A reservation id is required.", nameof(reservationId));
            }

            EnsureConfigured("Transaction/Inquire");
            var response = InquireOrNull(OrderReference(reservationId.ToString("D")));
            if (response == null)
            {
                _logger.LogInformation("Saferpay/Inquire => No successful transaction for reservation={ReservationId}", reservationId);
                return null;
            }

            var snapshot = MapInquiredTransaction(response);
            if (snapshot.OwnerReservationId.HasValue && snapshot.OwnerReservationId.Value != reservationId)
            {
                throw InvalidResponse("Transaction/Inquire", "Transaction.OrderId does not match the requested reservation.");
            }

            snapshot.OwnerReservationId = reservationId;
            _logger.LogInformation(
                "Saferpay/Inquire => Transaction found by order reservation={ReservationId} status={Status}",
                reservationId,
                snapshot.RawStatus);
            return snapshot;
        }

        public PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(authorizationReference))
            {
                throw new ArgumentException("A Saferpay transaction id is required.", nameof(authorizationReference));
            }

            if (amountMinor <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amountMinor),
                    amountMinor,
                    "Saferpay cannot capture a zero amount; cancel the authorization instead.");
            }

            string normalizedCurrency = NormalizeCurrency(currency, nameof(currency));
            EnsureConfigured("Transaction/Capture");
            string transactionId = authorizationReference.Trim();

            var payload = TransactionReference(transactionId);
            payload["Amount"] = BuildAmount(amountMinor, normalizedCurrency);

            JObject response;
            try
            {
                response = _client.Post(SaferpayApiClient.CapturePath, payload, idempotencyKey);
            }
            catch (SaferpayApiException exception) when (exception.HasErrorName("TRANSACTION_ALREADY_CAPTURED"))
            {
                // Replay of a capture that already succeeded (e.g. a retry after a lost response), or a capture made
                // elsewhere (back office). Only Inquire can tell what was captured.
                return ResolveAlreadyCaptured(transactionId, amountMinor, normalizedCurrency, exception);
            }

            string status = SaferpayApiClient.ReadString(response, "Status");
            var snapshot = new PaymentAuthorizationSnapshot
            {
                AuthorizationReference = transactionId,
                RawStatus = status,
                AmountMinor = amountMinor,
                Currency = normalizedCurrency.ToLowerInvariant(),
                CaptureReference = SaferpayApiClient.ReadString(response, "CaptureId"),
                Metadata = EmptyMetadata()
            };

            switch (status?.ToUpperInvariant())
            {
                case "CAPTURED":
                    snapshot.State = PaymentAuthorizationState.Captured;
                    snapshot.AmountReceivedMinor = amountMinor;
                    break;
                case "PENDING":
                    snapshot.State = PaymentAuthorizationState.Pending;
                    break;
                default:
                    snapshot.State = PaymentAuthorizationState.Unknown;
                    break;
            }

            _logger.LogInformation(
                "Saferpay/Capture => status={Status} amount={AmountMinor} currency={Currency}",
                status,
                amountMinor,
                normalizedCurrency);
            return snapshot;
        }

        public PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(authorizationReference))
            {
                throw new ArgumentException("A Saferpay transaction id is required.", nameof(authorizationReference));
            }

            EnsureConfigured("Transaction/Cancel");
            string transactionId = authorizationReference.Trim();

            JObject response;
            try
            {
                response = _client.Post(SaferpayApiClient.CancelPath, TransactionReference(transactionId), idempotencyKey);
            }
            catch (SaferpayApiException exception) when (exception.HasErrorName("TRANSACTION_IN_WRONG_STATE", "TRANSACTION_ALREADY_CAPTURED"))
            {
                return ResolveCancelRefused(transactionId, exception);
            }

            _logger.LogInformation("Saferpay/Cancel => authorization canceled");
            return new PaymentAuthorizationSnapshot
            {
                AuthorizationReference = SaferpayApiClient.ReadString(response, "TransactionId") ?? transactionId,
                State = PaymentAuthorizationState.Canceled,
                RawStatus = "CANCELED",
                AmountCapturableMinor = 0,
                IsCancelable = false,
                OwnerReservationId = ParseOrderId(SaferpayApiClient.ReadString(response, "OrderId")),
                Metadata = EmptyMetadata()
            };
        }

        public IReadOnlyDictionary<string, string> MirrorMetadata(
            string sessionReference,
            string fallbackAuthorizationReference,
            Action<IDictionary<string, string>> mutate)
        {
            throw new NotSupportedException("Saferpay does not store checkout metadata.");
        }

        public PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request)
        {
            if (request == null)
            {
                return PaymentWebhookParseResult.Rejected("Missing notification request.", 400);
            }

            // Saferpay calls notification URLs with GET. A missing method is treated as GET.
            string method = request.Method?.Trim();
            if (!string.IsNullOrEmpty(method) &&
                !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentWebhookParseResult.Rejected("Saferpay notifications must use GET or POST.", 405);
            }

            string reservationValue = QueryValue(request.Query, SaferpayNotificationSigner.ReservationIdParameter);
            string signature = QueryValue(request.Query, SaferpayNotificationSigner.SignatureParameter);
            if (reservationValue == null || signature == null)
            {
                return PaymentWebhookParseResult.Rejected("Saferpay notification is missing reservationId or sig.", 400);
            }

            if (!Guid.TryParse(reservationValue, out var reservationId) || reservationId == Guid.Empty)
            {
                return PaymentWebhookParseResult.Rejected("Saferpay notification reservationId is invalid.", 400);
            }

            if (!_signer.Verify(reservationId, signature))
            {
                _logger.LogWarning("Saferpay/Notify => Rejected notification with an invalid signature reservation={ReservationId}", reservationId);
                return PaymentWebhookParseResult.Rejected("Saferpay notification signature is invalid.", 400);
            }

            string outcome = QueryValue(request.Query, SaferpayNotificationSigner.OutcomeParameter);
            if (outcome == null || string.Equals(outcome, SaferpayNotificationSigner.OutcomeSuccess, StringComparison.OrdinalIgnoreCase))
            {
                outcome = SaferpayNotificationSigner.OutcomeSuccess;
            }
            else if (string.Equals(outcome, SaferpayNotificationSigner.OutcomeFail, StringComparison.OrdinalIgnoreCase))
            {
                outcome = SaferpayNotificationSigner.OutcomeFail;
            }
            else
            {
                return PaymentWebhookParseResult.Rejected("Saferpay notification outcome is invalid.", 400);
            }

            return new PaymentWebhookParseResult
            {
                Accepted = true,
                StatusCode = 200,
                Events = new[]
                {
                    new PaymentWebhookEvent
                    {
                        Kind = PaymentWebhookEventKind.CheckoutNotification,
                        ReservationId = reservationId,
                        EventId = null,
                        EventType = "saferpay.notify." + outcome
                    }
                }
            };
        }

        public string SanitizeProviderText(string text) => SaferpayTextSanitizer.Sanitize(text, _options);

        /// <summary>Posts Transaction/Inquire and returns the response, or null when Saferpay reports TRANSACTION_NOT_FOUND.</summary>
        private JObject InquireOrNull(JObject transactionReference)
        {
            try
            {
                return _client.Post(SaferpayApiClient.InquirePath, transactionReference, null);
            }
            catch (SaferpayApiException exception) when (exception.HasErrorName("TRANSACTION_NOT_FOUND"))
            {
                return null;
            }
        }

        private static PaymentAuthorizationSnapshot MapInquiredTransaction(JObject response)
        {
            if (response.GetValue("Transaction", StringComparison.OrdinalIgnoreCase) is not JObject transaction)
            {
                throw InvalidResponse("Transaction/Inquire", "Transaction is missing.");
            }

            return MapTransaction(transaction, "Transaction/Inquire");
        }

        /// <summary>
        /// Saferpay refused a cancel with TRANSACTION_IN_WRONG_STATE or TRANSACTION_ALREADY_CAPTURED. Only an Inquire
        /// that shows the transaction canceled (a repeated cancel) proves the hold is released. Inquire can lag, so
        /// every other answer is raised: callers must not treat the hold as released.
        /// </summary>
        private PaymentAuthorizationSnapshot ResolveCancelRefused(string transactionId, SaferpayApiException error)
        {
            var response = InquireOrNull(TransactionReference(transactionId));
            var current = response == null ? null : MapInquiredTransaction(response);
            if (current?.State == PaymentAuthorizationState.Canceled)
            {
                _logger.LogInformation("Saferpay/Cancel => {ErrorName}; transaction is already canceled", error.ErrorName);
                return current;
            }

            if (current?.State == PaymentAuthorizationState.Captured)
            {
                _logger.LogWarning("Saferpay/Cancel => {ErrorName}; transaction is captured, the hold cannot be released", error.ErrorName);
                throw new SaferpayApiException(
                    "Transaction/Cancel",
                    "transaction_already_captured",
                    "Saferpay Transaction/Cancel was refused because the transaction is already captured.",
                    isTransient: false,
                    httpStatusCode: error.HttpStatusCode,
                    providerMessage: error.ProviderMessage,
                    innerException: error);
            }

            string inquiredStatus = current?.RawStatus ?? "not found";
            _logger.LogWarning(
                "Saferpay/Cancel => {ErrorName}; Transaction/Inquire reports {Status}, cancel outcome unknown",
                error.ErrorName,
                inquiredStatus);
            throw new SaferpayApiException(
                "Transaction/Cancel",
                "cancel_state_unknown",
                $"Saferpay Transaction/Cancel failed with {error.ErrorName} and Transaction/Inquire reports {inquiredStatus}; the hold may not be released.",
                isTransient: true,
                httpStatusCode: error.HttpStatusCode,
                providerMessage: error.ProviderMessage,
                innerException: error);
        }

        /// <summary>
        /// Capture returned TRANSACTION_ALREADY_CAPTURED. Returns the Inquire snapshot when it shows the capture. When
        /// Inquire still shows the authorization in the same currency (it can lag by a few minutes), reports a capture
        /// of the requested amount (capped at the authorization) with <see cref="UnverifiedCaptureStatus"/>. Anything
        /// else is raised as transient <c>capture_state_unknown</c>, because the captured amount is unknown.
        /// </summary>
        private PaymentAuthorizationSnapshot ResolveAlreadyCaptured(
            string transactionId,
            long amountMinor,
            string currency,
            SaferpayApiException error)
        {
            var response = InquireOrNull(TransactionReference(transactionId));
            var current = response == null ? null : MapInquiredTransaction(response);
            if (current?.State == PaymentAuthorizationState.Captured)
            {
                _logger.LogInformation("Saferpay/Capture => transaction was already captured; returning its current state");
                return current;
            }

            if (current?.State == PaymentAuthorizationState.RequiresCapture &&
                string.Equals(current.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                long received = current.AmountMinor > 0 ? Math.Min(amountMinor, current.AmountMinor) : amountMinor;
                _logger.LogWarning(
                    "Saferpay/Capture => transaction already captured but inquiry still reports {Status}; reporting {AmountMinor} as captured, unverified",
                    current.RawStatus,
                    received);
                return new PaymentAuthorizationSnapshot
                {
                    AuthorizationReference = transactionId,
                    State = PaymentAuthorizationState.Captured,
                    RawStatus = UnverifiedCaptureStatus,
                    AmountMinor = current.AmountMinor,
                    AmountCapturableMinor = 0,
                    AmountReceivedMinor = received,
                    Currency = current.Currency,
                    IsCancelable = false,
                    OwnerReservationId = current.OwnerReservationId,
                    Metadata = EmptyMetadata()
                };
            }

            string inquiredStatus = current == null
                ? "not found"
                : current.State == PaymentAuthorizationState.RequiresCapture
                    ? $"{current.RawStatus} in {current.Currency?.ToUpperInvariant() ?? "no currency"}"
                    : current.RawStatus;
            _logger.LogWarning(
                "Saferpay/Capture => transaction already captured but inquiry reports {Status}; capture state unknown",
                inquiredStatus);
            throw new SaferpayApiException(
                "Transaction/Capture",
                "capture_state_unknown",
                $"Saferpay Transaction/Capture reported TRANSACTION_ALREADY_CAPTURED but Transaction/Inquire reports {inquiredStatus}; the captured amount is unknown.",
                isTransient: true,
                httpStatusCode: error.HttpStatusCode,
                providerMessage: error.ProviderMessage,
                innerException: error);
        }

        /// <summary>
        /// Assert returned TOKEN_EXPIRED, which says nothing about whether the payer authorized before the token
        /// expired. Saferpay's documented fallback is Transaction/Inquire: by the OrderId of the error (our reservation
        /// id), or by its TransactionId when no OrderId is present. A successful transaction is reported like an Assert
        /// result; otherwise the checkout is Expired.
        /// </summary>
        private PaymentCheckoutStatus ResolveExpiredToken(string token, SaferpayApiException error)
        {
            var expired = ErrorStatus(token, error, PaymentCheckoutState.Expired);
            JObject reference = error.OrderId != null
                ? OrderReference(error.OrderId)
                : error.TransactionId != null
                    ? TransactionReference(error.TransactionId)
                    : null;
            if (reference == null)
            {
                _logger.LogInformation("Saferpay/Assert => TOKEN_EXPIRED without order or transaction reference mapped to checkout state Expired");
                return expired;
            }

            var response = InquireOrNull(reference);
            if (response == null)
            {
                _logger.LogInformation("Saferpay/Assert => TOKEN_EXPIRED and no successful transaction found; checkout state Expired");
                return expired;
            }

            var status = MapCheckoutResponse(token, response, "Transaction/Inquire");
            status.OwnerReservationId ??= expired.OwnerReservationId;
            _logger.LogInformation(
                "Saferpay/Assert => TOKEN_EXPIRED but Transaction/Inquire reports {Status}; checkout state {State}",
                status.RawStatus,
                status.State);
            return status;
        }

        private PaymentCheckoutStatus MapCheckoutResponse(string token, JObject response, string operation)
        {
            if (response.GetValue("Transaction", StringComparison.OrdinalIgnoreCase) is not JObject transaction)
            {
                throw InvalidResponse(operation, "Transaction is missing.");
            }

            var authorization = MapTransaction(transaction, operation);
            var status = new PaymentCheckoutStatus
            {
                SessionReference = token,
                RawStatus = authorization.RawStatus,
                AuthorizationReference = authorization.AuthorizationReference,
                Authorization = authorization,
                ResumeUrl = null,
                CustomerEmail = ReadPayerEmail(response),
                CustomerName = ReadPayerName(response),
                Metadata = EmptyMetadata(),
                OwnerReservationId = authorization.OwnerReservationId
            };

            switch (authorization.State)
            {
                case PaymentAuthorizationState.RequiresCapture:
                case PaymentAuthorizationState.Captured:
                    status.State = PaymentCheckoutState.Complete;
                    status.IsPaid = true;
                    break;
                case PaymentAuthorizationState.Pending:
                    status.State = PaymentCheckoutState.Open;
                    break;
                case PaymentAuthorizationState.Canceled:
                    // The payer completed the checkout but the authorization was canceled afterwards.
                    status.State = PaymentCheckoutState.Complete;
                    break;
                default:
                    status.State = PaymentCheckoutState.Unknown;
                    break;
            }

            return status;
        }

        private bool TryMapAssertError(string token, SaferpayApiException error, out PaymentCheckoutStatus status)
        {
            status = null;
            string errorName = error.ErrorName.Trim().ToUpperInvariant();
            switch (errorName)
            {
                case "TOKEN_INVALID":
                    // Unknown token (or not ours): the checkout does not exist.
                    return true;
                case "TRANSACTION_ABORTED":
                    status = ErrorStatus(token, error, PaymentCheckoutState.Canceled);
                    return true;
                case "TRANSACTION_IN_WRONG_STATE":
                case "TRANSACTION_NOT_STARTED":
                    // Still in progress or abandoned by the payer: not a final outcome.
                    status = ErrorStatus(token, error, PaymentCheckoutState.Open);
                    return true;
            }

            if (SaferpayErrorClassification.IsPaymentDecline(error))
            {
                status = ErrorStatus(token, error, PaymentCheckoutState.Failed);
                status.FailureCode = error.ErrorName;
                status.FailureMessage = SanitizeProviderText(error.PayerMessage ?? error.ErrorMessage ?? error.ErrorName);
                return true;
            }

            return false;
        }

        private static PaymentCheckoutStatus ErrorStatus(string token, SaferpayApiException error, PaymentCheckoutState state) =>
            new PaymentCheckoutStatus
            {
                SessionReference = token,
                State = state,
                RawStatus = error.ErrorName,
                IsPaid = false,
                Metadata = EmptyMetadata(),
                OwnerReservationId = ParseOrderId(error.OrderId)
            };

        private static PaymentAuthorizationSnapshot MapTransaction(JObject transaction, string operation)
        {
            string id = SaferpayApiClient.ReadString(transaction, "Id");
            string status = SaferpayApiClient.ReadString(transaction, "Status");
            if (id == null || status == null)
            {
                throw InvalidResponse(operation, "Transaction.Id or Transaction.Status is missing.");
            }

            string amountValue = SaferpayApiClient.ReadString(transaction, "Amount", "Value");
            if (amountValue == null ||
                !long.TryParse(amountValue, NumberStyles.None, CultureInfo.InvariantCulture, out long amount))
            {
                throw InvalidResponse(operation, "Transaction.Amount.Value is missing or not an integer.");
            }

            var snapshot = new PaymentAuthorizationSnapshot
            {
                AuthorizationReference = id,
                RawStatus = status,
                AmountMinor = amount,
                Currency = SaferpayApiClient.ReadString(transaction, "Amount", "CurrencyCode")?.ToLowerInvariant(),
                OwnerReservationId = ParseOrderId(SaferpayApiClient.ReadString(transaction, "OrderId")),
                Metadata = EmptyMetadata()
            };

            switch (status.ToUpperInvariant())
            {
                case "AUTHORIZED":
                    snapshot.State = PaymentAuthorizationState.RequiresCapture;
                    snapshot.AmountCapturableMinor = amount;
                    snapshot.IsCancelable = true;
                    break;
                case "CAPTURED":
                    // Inquire/Assert report the captured amount of a captured transaction.
                    snapshot.State = PaymentAuthorizationState.Captured;
                    snapshot.AmountReceivedMinor = amount;
                    snapshot.CaptureReference = SaferpayApiClient.ReadString(transaction, "CaptureId");
                    break;
                case "CANCELED":
                case "CANCELLED":
                    snapshot.State = PaymentAuthorizationState.Canceled;
                    break;
                case "PENDING":
                    snapshot.State = PaymentAuthorizationState.Pending;
                    break;
                default:
                    snapshot.State = PaymentAuthorizationState.Unknown;
                    break;
            }

            return snapshot;
        }

        private static string ReadPayerEmail(JObject response) =>
            SaferpayApiClient.ReadString(response, "Payer", "BillingAddress", "Email") ??
            SaferpayApiClient.ReadString(response, "PaymentMeans", "PayPal", "Email");

        private static string ReadPayerName(JObject response)
        {
            string firstName = SaferpayApiClient.ReadString(response, "Payer", "BillingAddress", "FirstName");
            string lastName = SaferpayApiClient.ReadString(response, "Payer", "BillingAddress", "LastName");
            string fullName = string.Join(" ", new[] { firstName, lastName }.Where(part => part != null));
            return string.IsNullOrWhiteSpace(fullName)
                ? SaferpayApiClient.ReadString(response, "PaymentMeans", "Card", "HolderName")
                : fullName;
        }

        private JObject BuildPayer(string customerEmail)
        {
            var payer = new JObject();
            if (!string.IsNullOrWhiteSpace(_options.LanguageCode))
            {
                payer["LanguageCode"] = _options.LanguageCode.Trim();
            }

            // Per the specification, payer addresses sent with Initialize prefill the Saferpay address form.
            string email = customerEmail?.Trim();
            if (IsPlausibleEmail(email))
            {
                payer["BillingAddress"] = new JObject { ["Email"] = email };
            }

            return payer.HasValues ? payer : null;
        }

        private static bool IsPlausibleEmail(string email)
        {
            if (string.IsNullOrEmpty(email) || email.Length > MaxEmailLength || email.Any(char.IsWhiteSpace))
            {
                return false;
            }

            int at = email.IndexOf('@');
            return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 1;
        }

        /// <summary>
        /// Configured payment methods, or <see cref="SaferpayOptions.DefaultPaymentMethods"/> when none are configured.
        /// Without a filter Saferpay offers every method enabled on the terminal, including methods that capture
        /// immediately, which this deferred-capture flow cannot use.
        /// </summary>
        internal IReadOnlyList<string> ResolvePaymentMethods()
        {
            var configured = NormalizeValues(_options.PaymentMethods);
            return configured.Count > 0 ? configured : SaferpayOptions.DefaultPaymentMethods;
        }

        private static void AddValueList(JObject payload, string name, IEnumerable<string> values)
        {
            var items = NormalizeValues(values);
            if (items.Count > 0)
            {
                payload[name] = new JArray(items);
            }
        }

        private static List<string> NormalizeValues(IEnumerable<string> values) =>
            values == null
                ? new List<string>()
                : values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

        private static string BuildDescription(string productName)
        {
            string description = string.IsNullOrWhiteSpace(productName) ? DefaultDescription : productName.Trim();
            return description.Length <= MaxDescriptionLength ? description : description.Substring(0, MaxDescriptionLength);
        }

        private static JObject BuildAmount(long amountMinor, string currency) =>
            new JObject
            {
                ["Value"] = amountMinor.ToString(CultureInfo.InvariantCulture),
                ["CurrencyCode"] = currency
            };

        private static JObject TransactionReference(string transactionId) =>
            new JObject
            {
                ["TransactionReference"] = new JObject { ["TransactionId"] = transactionId }
            };

        private static JObject OrderReference(string orderId) =>
            new JObject
            {
                ["TransactionReference"] = new JObject { ["OrderId"] = orderId }
            };

        private static string NormalizeCurrency(string currency, string parameterName)
        {
            string normalized = currency?.Trim().ToUpperInvariant();
            if (normalized == null || normalized.Length != 3 || !normalized.All(c => c >= 'A' && c <= 'Z'))
            {
                throw new ArgumentException("An ISO 4217 currency code is required.", parameterName);
            }

            return normalized;
        }

        private static Guid? ParseOrderId(string orderId) =>
            Guid.TryParse(orderId, out var reservationId) && reservationId != Guid.Empty ? reservationId : null;

        private static DateTime? ParseUtcDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed.UtcDateTime
                : null;
        }

        private static string QueryValue(IReadOnlyDictionary<string, string> query, string name)
        {
            if (query == null)
            {
                return null;
            }

            if (!query.TryGetValue(name, out var value))
            {
                value = query.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            }

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static IReadOnlyDictionary<string, string> EmptyMetadata() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void EnsureConfigured(string operation)
        {
            if (!IsConfigured)
            {
                throw new SaferpayApiException(
                    operation,
                    "not_configured",
                    "Saferpay is not enabled, is missing Saferpay:BaseUrl, CustomerId, TerminalId, ApiUsername or ApiPassword, or Saferpay:BaseUrl is not an absolute https URL.",
                    isTransient: false);
            }
        }

        private void WarnAboutInsecureNotifyUrl()
        {
            if (string.IsNullOrWhiteSpace(_options.NotifyBaseUrl) ||
                SaferpayApiClient.IsAbsoluteHttpsUrl(_options.NotifyBaseUrl) ||
                Interlocked.Exchange(ref _insecureNotifyUrlWarningLogged, 1) != 0)
            {
                return;
            }

            _logger.LogWarning(
                "Saferpay => Saferpay:NotifyBaseUrl is not an absolute https URL; notification URLs and their signatures are sent unencrypted.");
        }

        private static SaferpayApiException InvalidResponse(string operation, string detail) =>
            new SaferpayApiException(
                operation,
                "invalid_response",
                $"Saferpay {operation} returned an unexpected response: {detail}",
                isTransient: true);
    }
}
