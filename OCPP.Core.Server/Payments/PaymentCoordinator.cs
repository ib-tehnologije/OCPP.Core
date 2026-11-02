/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Extensions.Hangfire;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Providers;
using System.Threading;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Provider-neutral payment reservation coordinator. It owns the reservation lifecycle, pricing,
    /// settlement, invoicing hand-off and the authorization release state machine, and talks to the
    /// payment service provider only through <see cref="IPaymentGateway"/>.
    /// </summary>
    public partial class PaymentCoordinator : IPaymentCoordinator
    {
        private const string PaymentAuthorizationEmailQueue = "payments";
        private readonly IPaymentGatewayRegistry _gateways;
        private readonly PaymentCheckoutSettings _settings;
        private readonly PaymentFlowOptions _flowOptions;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly IEmailNotificationService _emailNotificationService;
        private readonly StartChargingMediator _startMediator;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly IInvoiceIntegrationService _invoiceIntegrationService;
        private readonly IViesVerificationService _viesVerificationService;
        private const string IdempotencyCheckoutCreate = "checkout_create";
        private const string IdempotencyCapture = "capture";
        private const string IdempotencyCancel = "cancel";
        private static readonly TimeSpan ConnectorAvailableBeforeStopGrace = TimeSpan.FromSeconds(30);

        /// <summary>FailureCode of a charging reservation whose settlement failed transiently and is retried.</summary>
        internal const string SettlementRetryPendingCode = "settlement_retry_pending";
        /// <summary>FailureCode of a reservation whose settlement kept failing transiently past the retry window.</summary>
        internal const string SettlementRetryExhaustedCode = "settlement_retry_exhausted";
        /// <summary>FailureCode of a completed reservation whose captured amount the provider could not confirm.</summary>
        internal const string CaptureAmountUnverifiedCode = "capture_amount_unverified";
        private const int DefaultSettlementRetryWindowMinutes = 60;

        public bool IsEnabled =>
            !string.IsNullOrWhiteSpace(_settings.ReturnBaseUrl) &&
            DefaultGateway.IsConfigured;

        public PaymentCoordinator(
            IPaymentGatewayRegistry gateways,
            PaymentCheckoutSettings settings,
            IOptions<PaymentFlowOptions> flowOptions,
            ILogger logger,
            Func<DateTime> utcNow,
            IEmailNotificationService emailNotificationService = null,
            StartChargingMediator startMediator = null,
            IBackgroundJobClient backgroundJobClient = null,
            IInvoiceIntegrationService invoiceIntegrationService = null,
            IConfiguration configuration = null,
            IViesVerificationService viesVerificationService = null)
        {
            _gateways = gateways ?? throw new ArgumentNullException(nameof(gateways));
            if (_gateways.Default == null)
            {
                throw new ArgumentException("The payment gateway registry has no default gateway.", nameof(gateways));
            }
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _flowOptions = flowOptions?.Value ?? new PaymentFlowOptions();
            _configuration = configuration;
            _logger = logger ?? NullLogger.Instance;
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _emailNotificationService = emailNotificationService;
            _startMediator = startMediator;
            _backgroundJobClient = backgroundJobClient;
            _invoiceIntegrationService = invoiceIntegrationService;
            _viesVerificationService = viesVerificationService ??
                new UnavailableViesVerificationService(_utcNow);
        }

        /// <summary>
        /// Gateway for new checkouts on charge points without their own provider (<c>Payments:DefaultProvider</c>).
        /// New checkouts pick their gateway with <see cref="PaymentProviderResolver"/>, never this property directly.
        /// </summary>
        protected IPaymentGateway DefaultGateway => _gateways.Default;

        /// <summary>Provider name used in user-visible status texts not tied to a reservation (e.g. "Stripe disabled").</summary>
        private string DefaultProviderName => DefaultGateway.ProviderName;

        /// <summary>
        /// True when existing reservations can be processed: the return URL is configured and at least one
        /// registered gateway is configured. Equals <see cref="IsEnabled"/> for a single-gateway setup; with
        /// several gateways it keeps reservations of every provider capturable and releasable, and lets charge
        /// points that override the default provider create checkouts.
        /// </summary>
        public bool IsOperational =>
            !string.IsNullOrWhiteSpace(_settings.ReturnBaseUrl) &&
            _gateways.All.Any(gateway => gateway != null && gateway.IsConfigured);

        /// <summary>
        /// Gateway that owns <paramref name="reservation"/> (its persisted provider). Null when the provider is
        /// unknown or not registered; callers then fail closed and never call another provider.
        /// </summary>
        private IPaymentGateway ResolveGateway(ChargePaymentReservation reservation) =>
            _gateways.Resolve(reservation?.PaymentProvider);

        /// <summary>
        /// Gateway for a new checkout on <paramref name="chargePoint"/>: its <c>PaymentProvider</c> override, else the
        /// default provider. Throws <see cref="PaymentProviderUnavailableException"/> instead of falling back.
        /// </summary>
        private IPaymentGateway ResolveCheckoutGateway(ChargePoint chargePoint)
        {
            try
            {
                return PaymentProviderResolver.ResolveCheckoutGateway(_gateways, chargePoint?.PaymentProvider);
            }
            catch (PaymentProviderUnavailableException unavailable)
            {
                _logger.LogWarning(
                    "Payments/CreateCheckout => Payment provider unavailable cp={ChargePointId} provider={Provider} reason={Reason}",
                    chargePoint?.ChargePointId,
                    unavailable.Provider ?? "(none)",
                    unavailable.Reason);
                throw;
            }
        }

        private static bool IsGatewayUsable(IPaymentGateway gateway) =>
            gateway != null && gateway.IsConfigured;

        private static string ReservationProviderName(ChargePaymentReservation reservation)
        {
            var configured = reservation?.PaymentProvider;
            return PaymentProviderNames.Normalize(configured) ?? configured.Trim();
        }

        private static bool IsOwnedBy(ChargePaymentReservation reservation, IPaymentGateway gateway) =>
            reservation != null &&
            gateway != null &&
            string.Equals(
                string.IsNullOrWhiteSpace(reservation.PaymentProvider) ? PaymentProviderNames.Stripe : reservation.PaymentProvider.Trim(),
                gateway.ProviderName,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>Non-transient failure used when a reservation's provider cannot be called.</summary>
        private static PaymentProviderException ProviderUnavailable(ChargePaymentReservation reservation, IPaymentGateway gateway)
        {
            var providerName = gateway?.ProviderName ?? ReservationProviderName(reservation);
            return gateway == null
                ? new PaymentProviderException(
                    providerName,
                    ProviderNotRegisteredCode,
                    $"Payment provider '{providerName}' is not registered; the reservation cannot be processed by another provider.",
                    isTransient: false)
                : new PaymentProviderException(
                    providerName,
                    ProviderNotConfiguredCode,
                    $"Payment provider '{providerName}' is not configured.",
                    isTransient: false);
        }

        private const string ProviderNotRegisteredCode = "provider_not_registered";
        private const string ProviderNotConfiguredCode = "provider_not_configured";
        private const string ProviderUnavailableStatus = "ProviderUnavailable";

        public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request)
        {
            return CreateCheckoutSessionAsync(dbContext, request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        public async Task<PaymentSessionResult> CreateCheckoutSessionAsync(
            OCPPCoreContext dbContext,
            PaymentSessionRequest request,
            CancellationToken cancellationToken)
        {
            if (!IsOperational)
            {
                throw new InvalidOperationException("Payment integration is disabled.");
            }
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ChargePointId)) throw new ArgumentException("ChargePointId is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ChargeTagId)) throw new ArgumentException("ChargeTagId is required.", nameof(request));
            cancellationToken.ThrowIfCancellationRequested();

            InvoiceBuyerData invoiceBuyer = null;
            string pendingR1Email = null;
            if (request.RequestR1Invoice && InvoiceBuyerDataValidator.IsEmailOnlyRequest(request))
            {
                var emailValidation = InvoiceBuyerDataValidator.ValidatePendingR1Email(request.BuyerEmail);
                if (!emailValidation.Success)
                {
                    throw new InvoiceBuyerValidationException(emailValidation.Status, emailValidation.Field, emailValidation.Error);
                }
                pendingR1Email = emailValidation.Data.Email;
            }
            else if (request.RequestR1Invoice)
            {
                var validation = InvoiceBuyerDataValidator.ValidateAndNormalize(request);
                if (!validation.Success)
                {
                    throw new InvoiceBuyerValidationException(validation.Status, validation.Field, validation.Error);
                }
                invoiceBuyer = validation.Data;
            }

            var chargePoint = dbContext.ChargePoints.Find(request.ChargePointId);
            if (chargePoint == null)
            {
                throw new ArgumentException($"Charge point '{request.ChargePointId}' not found.", nameof(request.ChargePointId));
            }

            // Pick the provider once, from the charge point's current setting; the reservation then stores it and
            // every later step (confirm, capture, cancel, webhooks, recovery) uses the stored provider.
            var gateway = ResolveCheckoutGateway(chargePoint);

            double maxEnergyKwh = chargePoint.MaxSessionKwh;
            decimal pricePerKwh = chargePoint.PricePerKwh;
            var flowOptions = ResolveFlowOptions(dbContext);
            var nightTariffTimeZoneId = string.IsNullOrWhiteSpace(flowOptions.NightTariffTimeZoneId)
                ? NightTariffWindow.DefaultTimeZoneId
                : flowOptions.NightTariffTimeZoneId.Trim();
            var nightTariff = chargePoint.NightTariffEnabled && chargePoint.NightPricePerKwh >= 0
                ? NightTariffWindow.TryCreate(chargePoint.NightTariffStartMinute, chargePoint.NightTariffEndMinute, nightTariffTimeZoneId)
                : null;
            decimal? nightPricePerKwh = nightTariff != null ? chargePoint.NightPricePerKwh : null;
            decimal userSessionFee = chargePoint.UserSessionFee;
            decimal ownerSessionFee = chargePoint.OwnerSessionFee;
            decimal ownerCommissionPercent = chargePoint.OwnerCommissionPercent;
            decimal ownerCommissionFixedPerKwh = chargePoint.OwnerCommissionFixedPerKwh;
            int startUsageFeeAfterMinutes = Math.Max(0, chargePoint.StartUsageFeeAfterMinutes);
            int maxUsageFeeMinutes = Math.Max(0, chargePoint.MaxUsageFeeMinutes);
            decimal usageFeePerMinute = chargePoint.ConnectorUsageFeePerMinute;
            int usageFeeAnchorMinutes = chargePoint.UsageFeeAfterChargingEnds ? 1 : 0;

            if ((maxEnergyKwh <= 0 && usageFeePerMinute <= 0 && userSessionFee <= 0) ||
                (pricePerKwh <= 0 && usageFeePerMinute <= 0 && userSessionFee <= 0))
            {
                throw new InvalidOperationException("Pricing is not configured for this charge point.");
            }

            var now = _utcNow();
            var normalizedTag = NormalizeChargeTag(request.ChargeTagId);

            EnsureChargeTagExists(dbContext, normalizedTag);

            // The hold must cover the whole session at the higher of the two energy prices.
            var maxEnergyCents = CalculateAmountInCents(maxEnergyKwh, Math.Max(pricePerKwh, nightPricePerKwh ?? 0m));
            var maxUsageFeeCents = CalculateUsageFeeInCents(
                Math.Max(0, maxUsageFeeMinutes - startUsageFeeAfterMinutes),
                usageFeePerMinute);
            var sessionFeeCents = CalculateFlatAmountInCents(userSessionFee);
            var maxTotalCents = maxEnergyCents + maxUsageFeeCents + sessionFeeCents;

            if (maxTotalCents <= 0)
            {
                throw new InvalidOperationException("Calculated maximum amount is zero. Check pricing configuration.");
            }

            ViesVerificationResult viesVerification = null;
            if (!string.IsNullOrWhiteSpace(invoiceBuyer?.NormalizedVatIdentifier) &&
                !string.IsNullOrWhiteSpace(invoiceBuyer.ViesCountryCode))
            {
                var vatNumber = invoiceBuyer.NormalizedVatIdentifier.Substring(
                    invoiceBuyer.ViesCountryCode.Length);
                viesVerification = await _viesVerificationService.VerifyAsync(
                    invoiceBuyer.ViesCountryCode,
                    vatNumber,
                    cancellationToken);
            }

            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = request.ChargePointId,
                ConnectorId = request.ConnectorId,
                ChargeTagId = normalizedTag,
                MaxEnergyKwh = maxEnergyKwh,
                PricePerKwh = pricePerKwh,
                NightPricePerKwh = nightPricePerKwh,
                NightTariffStartMinute = nightTariff?.StartMinute,
                NightTariffEndMinute = nightTariff?.EndMinute,
                NightTariffTimeZoneId = nightTariff != null ? nightTariffTimeZoneId : null,
                UserSessionFee = userSessionFee,
                OwnerSessionFee = ownerSessionFee,
                OwnerCommissionPercent = ownerCommissionPercent,
                OwnerCommissionFixedPerKwh = ownerCommissionFixedPerKwh,
                UsageFeePerMinute = usageFeePerMinute,
                StartUsageFeeAfterMinutes = startUsageFeeAfterMinutes,
                MaxUsageFeeMinutes = maxUsageFeeMinutes,
                UsageFeeAnchorMinutes = usageFeeAnchorMinutes,
                MaxAmountCents = maxTotalCents,
                Currency = string.IsNullOrWhiteSpace(_settings.Currency) ? PaymentCheckoutSettings.DefaultCurrency : _settings.Currency.ToLowerInvariant(),
                Status = PaymentReservationStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            if (invoiceBuyer != null)
            {
                ApplyConfirmedBuyer(reservation, invoiceBuyer, now, viesVerification);
            }
            if (request.RequestR1Invoice)
            {
                reservation.InvoiceR1RequestedAtUtc = now;
            }
            if (pendingR1Email != null)
            {
                reservation.InvoiceBuyerEmail = pendingR1Email;
            }

            _logger.LogInformation(
                "Payments/CreateCheckout => reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} tag={ChargeTagId} maxTotalCents={MaxTotalCents} currency={Currency} maxEnergyKwh={MaxEnergyKwh} pricePerKwh={PricePerKwh} sessionFee={SessionFee} usageFeePerMin={UsageFeePerMinute} maxUsageMinutes={MaxUsageMinutes}",
                reservation.ReservationId,
                reservation.ChargePointId,
                reservation.ConnectorId,
                reservation.ChargeTagId,
                reservation.MaxAmountCents,
                reservation.Currency,
                reservation.MaxEnergyKwh,
                reservation.PricePerKwh,
                reservation.UserSessionFee,
                reservation.UsageFeePerMinute,
                reservation.MaxUsageFeeMinutes);

            var baseReturnUrl = string.IsNullOrWhiteSpace(request.ReturnBaseUrl)
                ? _settings.ReturnBaseUrl
                : request.ReturnBaseUrl;

            var successUrl = $"{TrimTrailingSlash(baseReturnUrl)}/Payments/Success?reservationId={reservation.ReservationId}";
            if (!string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder))
            {
                successUrl += $"&session_id={gateway.SuccessUrlSessionPlaceholder}";
            }
            var cancelUrl = $"{TrimTrailingSlash(baseReturnUrl)}/Payments/Cancel?reservationId={reservation.ReservationId}";

            if (!string.IsNullOrWhiteSpace(request.Origin))
            {
                var originParam = Uri.EscapeDataString(request.Origin);
                successUrl += $"&origin={originParam}";
                cancelUrl += $"&origin={originParam}";
            }

            var metadata = new Dictionary<string, string>
            {
                ["reservation_id"] = reservation.ReservationId.ToString(),
                ["charge_point_id"] = reservation.ChargePointId,
                ["connector_id"] = reservation.ConnectorId.ToString(),
                ["charge_tag_id"] = reservation.ChargeTagId
            };

            if (invoiceBuyer != null)
            {
                metadata["invoice_type"] = "R1";
                SetOrRemoveMetadata(
                    metadata,
                    "buyer_oib",
                    string.Equals(invoiceBuyer.Country, "HR", StringComparison.OrdinalIgnoreCase)
                        ? TrimMetadataValue(invoiceBuyer.TaxIdentifier, 32)
                        : null);
                SetOrRemoveMetadata(metadata, "buyer_country", invoiceBuyer.Country);
                SetOrRemoveMetadata(metadata, "buyer_tax_identifier", TrimMetadataValue(invoiceBuyer.TaxIdentifier, 64));
                SetOrRemoveMetadata(metadata, "buyer_company", TrimMetadataValue(invoiceBuyer.CompanyName, 200));
            }
            else if (pendingR1Email != null)
            {
                metadata["invoice_type"] = "R1";
            }

            var checkoutRequest = new PaymentCheckoutRequest
            {
                ReservationId = reservation.ReservationId,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                AmountMinor = reservation.MaxAmountCents,
                Currency = reservation.Currency,
                ProductName = string.IsNullOrWhiteSpace(_settings.ProductName)
                    ? $"Charging session ({reservation.MaxEnergyKwh:0.#} kWh)"
                    : _settings.ProductName,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                CustomerEmail = pendingR1Email,
                Metadata = metadata,
                IdempotencyKey = BuildIdempotencyKey(IdempotencyCheckoutCreate, reservation.ReservationId)
            };

            try
            {
                var session = gateway.CreateCheckout(checkoutRequest);

                reservation.PaymentProvider = gateway.ProviderName;
                reservation.StripeCheckoutSessionId = session?.SessionReference;
                reservation.StripePaymentIntentId = session?.AuthorizationReference;
                reservation.UpdatedAtUtc = _utcNow();

                dbContext.Add(reservation);
                try
                {
                    dbContext.SaveChanges();
                }
                catch (DbUpdateException dbUpdateEx) when (IsActiveReservationConflict(dbUpdateEx))
                {
                    throw new InvalidOperationException("ConnectorBusy", dbUpdateEx);
                }

                _logger.LogInformation(
                    "Payments/CreateCheckout => created provider={Provider} sessionId={SessionId} paymentIntentId={PaymentIntentId} checkoutUrlLength={CheckoutUrlLength} reservation={ReservationId}",
                    gateway.ProviderName,
                    session?.SessionReference,
                    session?.AuthorizationReference ?? "(none)",
                    session?.RedirectUrl?.Length ?? 0,
                    reservation.ReservationId);

                return new PaymentSessionResult
                {
                    Reservation = reservation,
                    CheckoutUrl = session?.RedirectUrl
                };
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogError(pex, "{Provider} checkout session creation failed: {Message}", gateway.ProviderName, pex.Message);
                throw;
            }
        }

        private static string NormalizeOibDigits(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var digits = new string(input.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : digits;
        }

        private static string TrimMetadataValue(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Trim();
            if (maxLength > 0 && value.Length > maxLength)
            {
                value = value.Substring(0, maxLength);
            }

            return value;
        }

        /// <summary>
        /// Croatian OIB validation (ISO 7064 MOD 11,10).
        /// </summary>
        private static bool IsValidOib(string oibDigits)
        {
            if (string.IsNullOrWhiteSpace(oibDigits) ||
                oibDigits.Length != 11 ||
                oibDigits.Any(c => c < '0' || c > '9'))
            {
                return false;
            }

            int a = 10;
            for (int i = 0; i < 10; i++)
            {
                a = a + (oibDigits[i] - '0');
                a = a % 10;
                if (a == 0) a = 10;
                a = (a * 2) % 11;
            }

            int control = 11 - a;
            if (control == 10) control = 0;
            return control == (oibDigits[10] - '0');
        }

        public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId)
        {
            var result = new PaymentConfirmationResult
            {
                Success = false,
                Status = "Invalid",
                Error = $"{DefaultProviderName} disabled"
            };

            if (!IsOperational)
            {
                _logger.LogWarning("Payments/Confirm => {Provider} disabled while confirming reservation={ReservationId}", DefaultProviderName, reservationId);
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null)
            {
                _logger.LogWarning("Payments/Confirm => Reservation not found reservation={ReservationId}", reservationId);
                result.Error = "Reservation not found.";
                result.Status = "NotFound";
                return result;
            }

            result.Reservation = reservation;

            if (IsTerminalReservationStatus(reservation.Status))
            {
                _logger.LogWarning("Payments/Confirm => Reservation is terminal and cannot be confirmed reservation={ReservationId} status={Status}",
                    reservationId,
                    reservation.Status ?? "(null)");
                // The payer may have completed a still-open payment page after the reservation ended.
                TryReleaseLateAuthorizationOnReturn(dbContext, reservation);
                result.Error = $"Reservation is in terminal status '{reservation.Status ?? "Unknown"}'.";
                result.Status = reservation.Status ?? "InvalidStatus";
                return result;
            }

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                var unavailable = ProviderUnavailable(reservation, gateway);
                _logger.LogWarning("Payments/Confirm => Provider unavailable reservation={ReservationId} provider={Provider} code={Code}", reservationId, unavailable.Provider, unavailable.Code);
                result.Error = unavailable.Message;
                result.Status = ProviderUnavailableStatus;
                return result;
            }

            // Stripe returns the checkout id through the success-URL placeholder and it must match the
            // stored one. Providers without a placeholder confirm with the stored reference; a supplied
            // value must still match it.
            var hasSessionPlaceholder = !string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder);
            if ((hasSessionPlaceholder || !string.IsNullOrWhiteSpace(checkoutSessionId)) &&
                !string.Equals(reservation.StripeCheckoutSessionId, checkoutSessionId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Payments/Confirm => Checkout session mismatch reservation={ReservationId} expected={Expected} received={Received}", reservationId, reservation.StripeCheckoutSessionId, checkoutSessionId);
                result.Error = "Checkout session mismatch.";
                result.Status = "SessionMismatch";
                return result;
            }

            var sessionReference = hasSessionPlaceholder ? checkoutSessionId : reservation.StripeCheckoutSessionId;
            if (!hasSessionPlaceholder && string.IsNullOrWhiteSpace(sessionReference))
            {
                _logger.LogWarning("Payments/Confirm => Missing checkout session reservation={ReservationId}", reservationId);
                result.Error = "Checkout session is missing.";
                result.Status = "MissingCheckoutSession";
                return result;
            }

            if (!hasSessionPlaceholder)
            {
                // Without a payer-supplied checkout reference anyone who knows the reservation id can call this.
                // Only a pending reservation needs the provider; later states are already decided.
                if (!string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Payments/Confirm => Reservation already confirmed reservation={ReservationId} status={Status}", reservationId, reservation.Status ?? "(null)");
                    result.Success = true;
                    result.Status = reservation.Status;
                    result.Error = null;
                    return result;
                }

                if (!TryEnterProviderCheck(reservationId))
                {
                    _logger.LogInformation("Payments/Confirm => Provider check throttled reservation={ReservationId}", reservationId);
                    result.Status = PaymentStillProcessingStatus;
                    result.Error = PaymentStillProcessingMessage;
                    return result;
                }
            }

            try
            {
                _logger.LogInformation("Payments/Confirm => Fetching session provider={Provider} reservation={ReservationId} sessionId={SessionId}", gateway.ProviderName, reservationId, sessionReference);
                var checkout = gateway.GetCheckout(sessionReference);
                if (checkout == null)
                {
                    _logger.LogWarning("Payments/Confirm => Checkout session not found reservation={ReservationId} sessionId={SessionId}", reservationId, sessionReference);
                    result.Error = "Checkout session was not found.";
                    result.Status = "MissingCheckoutSession";
                    return result;
                }

                if (checkout.State == PaymentCheckoutState.Canceled)
                {
                    return ConfirmCanceledCheckout(dbContext, reservation, gateway, checkout, result);
                }

                if (checkout.State == PaymentCheckoutState.Failed)
                {
                    return ConfirmFailedCheckout(dbContext, reservation, gateway, checkout, result);
                }

                if (checkout.State != PaymentCheckoutState.Complete)
                {
                    _logger.LogWarning("Payments/Confirm => Session not complete reservation={ReservationId} status={Status}", reservationId, checkout.RawStatus);
                    result.Error = $"Checkout session not completed (status={checkout.RawStatus}).";
                    result.Status = checkout.RawStatus;
                    return result;
                }

                return ConfirmCompletedCheckout(dbContext, reservation, gateway, checkout, sessionReference, result);
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogError(pex, "{Provider} confirmation failed for reservation {ReservationId} transient={Transient}", gateway.ProviderName, reservationId, pex.IsTransient);
                result.Error = pex.Message;
                result.Status = ProviderErrorStatus(gateway);
                if (pex.IsTransient)
                {
                    // The payer may already hold an authorization. Keep the reservation so a retry, a
                    // provider notification or the stale-reservation sweep (which releases holds) can finish it.
                    return result;
                }

                if (!string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
                {
                    // A provider notification may already have authorized and started this reservation; a failed
                    // repeat lookup must not end it or release the funds it charges against.
                    return result;
                }

                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = pex.Message;
                // An authorization may exist even though it could not be confirmed; let the release
                // reconciliation look it up and cancel it instead of leaving the funds held.
                ArmAuthorizationReleaseState(reservation);
                reservation.UpdatedAtUtc = _utcNow();
                if (!TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending))
                {
                    // A notification, cancellation or the sweep decided first; its state stands.
                    _logger.LogInformation("Payments/Confirm => Reservation changed while confirming reservation={ReservationId} status={Status}", reservationId, reservation.Status ?? "(null)");
                }

                return result;
            }
        }

        private PaymentConfirmationResult ConfirmCompletedCheckout(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentCheckoutStatus checkout,
            string sessionReference,
            PaymentConfirmationResult result)
        {
            var reservationId = reservation.ReservationId;
            var paymentIntentId = string.IsNullOrWhiteSpace(checkout.AuthorizationReference)
                ? checkout.Authorization?.AuthorizationReference
                : checkout.AuthorizationReference;
            if (string.IsNullOrWhiteSpace(paymentIntentId))
            {
                _logger.LogWarning("Payments/Confirm => Payment authorization missing reservation={ReservationId} sessionId={SessionId}", reservationId, sessionReference);
                result.Error = $"{AuthorizationSubject(gateway)} missing in session.";
                result.Status = "MissingPaymentIntent";
                return result;
            }

            var authorization = checkout.Authorization ?? gateway.GetAuthorization(paymentIntentId);
            if (authorization == null)
            {
                _logger.LogWarning("Payments/Confirm => Payment authorization not found reservation={ReservationId} paymentIntent={PaymentIntentId}", reservationId, paymentIntentId);
                result.Error = $"{AuthorizationSubject(gateway)} was not found.";
                result.Status = "MissingPaymentIntent";
                return result;
            }

            if (gateway.CapturedCheckoutRequiresRefund &&
                authorization.State == PaymentAuthorizationState.Captured &&
                string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                ApplyCapturedAtCheckout(reservation, gateway, authorization, checkout, paymentIntentId, "Confirm");
                if (!TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending))
                {
                    return ConfirmAfterLostTransition(dbContext, reservation, gateway, checkout, paymentIntentId, sessionReference, result);
                }

                result.Success = false;
                result.Status = PaymentReservationStatus.Failed;
                result.Error = CapturedAtCheckoutMessage;
                return result;
            }

            if (authorization.State != PaymentAuthorizationState.RequiresCapture &&
                authorization.State != PaymentAuthorizationState.Captured)
            {
                _logger.LogWarning("Payments/Confirm => Unexpected payment authorization status reservation={ReservationId} paymentIntent={PaymentIntentId} status={Status}", reservationId, paymentIntentId, authorization.RawStatus);
                result.Error = $"Unexpected {AuthorizationTerm(gateway)} status '{authorization.RawStatus}'.";
                result.Status = authorization.RawStatus;
                var loadedStatus = PersistedStatus(dbContext, reservation);
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = result.Error;
                reservation.UpdatedAtUtc = _utcNow();
                if (!TryCommitReservationTransition(dbContext, reservation, loadedStatus))
                {
                    _logger.LogInformation("Payments/Confirm => Reservation changed while confirming reservation={ReservationId} status={Status}", reservationId, reservation.Status ?? "(null)");
                }

                return result;
            }

            string previousStatus = reservation.Status;
            bool wasPending = string.Equals(previousStatus, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase);
            bool wasAuthorized = string.Equals(previousStatus, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase);
            bool shouldPrepareChargeTag = wasPending || wasAuthorized;
            // Providers without a payer-supplied checkout reference can be confirmed by anyone who knows the
            // reservation id, so they e-mail the payer only on the transition to Authorized.
            bool shouldSendAuthorizationEmail = string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder)
                ? wasPending
                : shouldPrepareChargeTag;

            reservation.StripePaymentIntentId = paymentIntentId;
            var paymentIntentAmount = (long?)authorization.AmountMinor;
            reservation.MaxAmountCents = paymentIntentAmount ?? reservation.MaxAmountCents;
            PersistPayerDetails(reservation, checkout);

            var now = _utcNow();
            if (wasPending)
            {
                reservation.Status = PaymentReservationStatus.Authorized;
                reservation.AuthorizedAtUtc = now;
                reservation.StartDeadlineAtUtc = now.AddMinutes(Math.Max(1, _flowOptions.StartWindowMinutes));
                ClearFailureState(reservation);
                reservation.UpdatedAtUtc = now;
                // Authorize only a reservation that is still pending: a cancellation or the stale sweep may have
                // ended it (and released this hold) while the provider was being asked.
                if (!TryCommitAuthorization(dbContext, reservation))
                {
                    return ConfirmAfterLostTransition(dbContext, reservation, gateway, checkout, paymentIntentId, sessionReference, result);
                }
            }
            else
            {
                if (wasAuthorized)
                {
                    reservation.AuthorizedAtUtc ??= now;
                    reservation.StartDeadlineAtUtc ??= reservation.AuthorizedAtUtc.Value.AddMinutes(Math.Max(1, _flowOptions.StartWindowMinutes));
                }

                if (shouldPrepareChargeTag)
                {
                    ClearFailureState(reservation);
                    ClaimOcppIdTag(dbContext, reservation);
                }

                reservation.UpdatedAtUtc = now;
                dbContext.SaveChanges();
            }

            _logger.LogInformation(
                "Payments/Confirm => Reservation confirmed provider={Provider} reservation={ReservationId} paymentIntent={PaymentIntentId} amount={Amount} paymentStatus={PaymentStatus} previousStatus={PreviousStatus} newStatus={NewStatus}",
                gateway.ProviderName,
                reservationId,
                paymentIntentId,
                paymentIntentAmount,
                authorization.RawStatus,
                previousStatus ?? "(null)",
                reservation.Status ?? "(null)");

            if (shouldSendAuthorizationEmail)
            {
                SendPaymentAuthorizedEmail(gateway, reservation, checkout, "Confirm");
            }

            result.Success = true;
            result.Status = reservation.Status ?? PaymentReservationStatus.Authorized;
            result.Error = null;

            return result;
        }

        /// <summary>
        /// The pending reservation changed while its completed checkout was being confirmed; the entity holds the
        /// current row. Authorized by a provider signal: the confirmation succeeds (a provider with a payer-supplied
        /// checkout reference still prepares the tag and e-mails as for any authorized reservation; others were
        /// e-mailed by the request that authorized it). Ended by a cancellation or the sweep: the authorization
        /// found here is released unless that request already did, and the reservation is not started.
        /// </summary>
        private PaymentConfirmationResult ConfirmAfterLostTransition(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentCheckoutStatus checkout,
            string authorizationReference,
            string sessionReference,
            PaymentConfirmationResult result)
        {
            _logger.LogInformation(
                "Payments/Confirm => Reservation changed while it was being confirmed reservation={ReservationId} status={Status}",
                reservation.ReservationId,
                reservation.Status ?? "(null)");

            if (dbContext.Entry(reservation).State == EntityState.Detached)
            {
                result.Success = false;
                result.Status = "NotFound";
                result.Error = "Reservation not found.";
                return result;
            }

            if (!string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder) &&
                string.Equals(reservation.Status, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase))
            {
                return ConfirmCompletedCheckout(dbContext, reservation, gateway, checkout, sessionReference, result);
            }

            if (IsAuthorizedActiveStatus(reservation.Status))
            {
                result.Success = true;
                result.Status = reservation.Status;
                result.Error = null;
                return result;
            }

            result.Success = false;
            result.Status = reservation.Status ?? "InvalidStatus";
            if (IsTerminalReservationStatus(reservation.Status))
            {
                ReleaseAuthorizationAfterLostTransition(dbContext, gateway, reservation, checkout, authorizationReference, "Confirm");
                result.Status = reservation.Status ?? "InvalidStatus";
                result.Error = $"Reservation is in terminal status '{reservation.Status ?? "Unknown"}'.";
                return result;
            }

            result.Error = "Reservation changed while it was being confirmed.";
            return result;
        }

        /// <summary>
        /// Sends (or queues) the payment-authorized e-mail with the live status link. For providers without a
        /// payer-supplied checkout reference it is sent once per reservation, by whichever of the browser return
        /// and the provider notification authorizes the reservation first.
        /// </summary>
        private void SendPaymentAuthorizedEmail(
            IPaymentGateway gateway,
            ChargePaymentReservation reservation,
            PaymentCheckoutStatus checkout,
            string source)
        {
            var reservationId = reservation.ReservationId;
            if (string.IsNullOrEmpty(gateway?.SuccessUrlSessionPlaceholder) && !TryClaimAuthorizationEmail(reservationId))
            {
                _logger.LogInformation("Payments/{Source} => Payment authorization email already sent reservation={ReservationId}", source, reservationId);
                return;
            }

            try
            {
                var recipientEmail = FirstNonEmpty(checkout?.CustomerEmail, reservation.PayerEmail, reservation.InvoiceBuyerEmail);
                var sessionId = checkout?.SessionReference;
                var statusUrl = BuildStatusUrl(reservationId);
                if (_backgroundJobClient != null)
                {
                    var job = Job.FromExpression<IPaymentAuthorizationEmailJob>(emailJob =>
                        emailJob.SendPaymentAuthorized(reservationId, recipientEmail, sessionId, statusUrl));
                    _backgroundJobClient.Create(job, new EnqueuedState(PaymentAuthorizationEmailQueue));
                    _logger.LogInformation("Payments/{Source} => Queued payment authorization email reservation={ReservationId}", source, reservationId);
                }
                else
                {
                    _emailNotificationService?.SendPaymentAuthorized(
                        recipientEmail,
                        reservation,
                        ToCheckoutDetails(gateway, reservation, checkout),
                        statusUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Payments/{Source} => Failed to enqueue payment authorization email for reservation={ReservationId}", source, reservationId);
            }
        }

        private PaymentConfirmationResult ConfirmCanceledCheckout(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentCheckoutStatus checkout,
            PaymentConfirmationResult result)
        {
            _logger.LogInformation("Payments/Confirm => Checkout cancelled by payer provider={Provider} reservation={ReservationId} status={Status}",
                gateway.ProviderName,
                reservation.ReservationId,
                checkout.RawStatus ?? "(null)");

            result.Success = false;
            if (!string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                // Only an unpaid checkout can be abandoned; never cancel a reservation that already holds funds.
                result.Status = reservation.Status ?? "InvalidStatus";
                result.Error = $"Checkout reported '{checkout.RawStatus ?? "cancelled"}' but the reservation is '{reservation.Status ?? "Unknown"}'.";
                return result;
            }

            ApplyCheckoutCancelled(reservation);
            if (!TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending))
            {
                result.Status = reservation.Status ?? "InvalidStatus";
                result.Error = $"Checkout reported '{checkout.RawStatus ?? "cancelled"}' but the reservation is '{reservation.Status ?? "Unknown"}'.";
                return result;
            }

            result.Status = PaymentReservationStatus.Cancelled;
            result.Error = SanitizeProviderError(checkout.FailureMessage, gateway) ?? "Checkout was cancelled.";
            return result;
        }

        private PaymentConfirmationResult ConfirmFailedCheckout(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentCheckoutStatus checkout,
            PaymentConfirmationResult result)
        {
            _logger.LogWarning("Payments/Confirm => Checkout failed provider={Provider} reservation={ReservationId} status={Status} code={Code}",
                gateway.ProviderName,
                reservation.ReservationId,
                checkout.RawStatus ?? "(null)",
                checkout.FailureCode ?? "(none)");

            result.Success = false;
            if (!string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                result.Status = reservation.Status ?? "InvalidStatus";
                result.Error = $"Checkout reported '{checkout.RawStatus ?? "failed"}' but the reservation is '{reservation.Status ?? "Unknown"}'.";
                return result;
            }

            ApplyCheckoutFailed(reservation, gateway, checkout);
            if (!TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending))
            {
                result.Status = reservation.Status ?? "InvalidStatus";
                result.Error = $"Checkout reported '{checkout.RawStatus ?? "failed"}' but the reservation is '{reservation.Status ?? "Unknown"}'.";
                return result;
            }

            result.Status = PaymentReservationStatus.Failed;
            result.Error = reservation.LastError;
            return result;
        }

        private const string CheckoutCancelledReason = "checkout_cancelled";

        /// <summary>Same fields <see cref="CancelReservation"/> sets, with reason <c>checkout_cancelled</c>.</summary>
        private void ApplyCheckoutCancelled(ChargePaymentReservation reservation)
        {
            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.UpdatedAtUtc = _utcNow();
            reservation.LastError = CheckoutCancelledReason;
        }

        private void ApplyCheckoutFailed(ChargePaymentReservation reservation, IPaymentGateway gateway, PaymentCheckoutStatus checkout)
        {
            var message = SanitizeProviderError(checkout?.FailureMessage, gateway) ?? "Payment failed.";
            reservation.Status = PaymentReservationStatus.Failed;
            reservation.LastError = message;
            reservation.FailureCode = Truncate(SanitizeProviderError(checkout?.FailureCode, gateway), 100);
            reservation.FailureMessage = message;
            reservation.UpdatedAtUtc = _utcNow();
        }

        /// <summary>Keeps the payer details the provider reported, for receipts and invoices of providers without deferred lookup.</summary>
        private static void PersistPayerDetails(ChargePaymentReservation reservation, PaymentCheckoutStatus checkout)
        {
            if (reservation == null || checkout == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(reservation.PayerEmail) && !string.IsNullOrWhiteSpace(checkout.CustomerEmail))
            {
                reservation.PayerEmail = Truncate(checkout.CustomerEmail.Trim(), 254);
            }

            if (string.IsNullOrWhiteSpace(reservation.PayerName) && !string.IsNullOrWhiteSpace(checkout.CustomerName))
            {
                reservation.PayerName = Truncate(checkout.CustomerName.Trim(), 200);
            }
        }

        public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId)
        {
            var result = new PaymentResumeResult
            {
                Success = false,
                Status = "Invalid",
                Error = "Reservation id is required."
            };

            if (!IsOperational)
            {
                result.Status = "Disabled";
                result.Error = $"{DefaultProviderName} integration is disabled.";
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservationId == Guid.Empty) return result;

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null)
            {
                _logger.LogWarning("Payments/Resume => Reservation not found reservation={ReservationId}", reservationId);
                result.Status = "NotFound";
                result.Error = "Reservation not found.";
                return result;
            }

            result.Reservation = reservation;

            if (PaymentReservationStatus.IsTerminal(reservation.Status))
            {
                _logger.LogInformation("Payments/Resume => Reservation already terminal reservation={ReservationId} status={Status}",
                    reservationId,
                    reservation.Status ?? "(null)");
                result.Success = true;
                result.Status = reservation.Status ?? "InvalidStatus";
                result.Error = null;
                return result;
            }

            if (string.Equals(reservation.Status, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservation.Status, PaymentReservationStatus.StartRequested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservation.Status, PaymentReservationStatus.Charging, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Payments/Resume => Reservation should continue on status page reservation={ReservationId} status={Status}",
                    reservationId,
                    reservation.Status ?? "(null)");
                result.Success = true;
                result.Status = "Status";
                result.Error = null;
                return result;
            }

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                result.Success = true;
                result.Status = reservation.Status ?? "Status";
                result.Error = null;
                return result;
            }

            if (string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
            {
                _logger.LogWarning("Payments/Resume => Missing checkout session reservation={ReservationId}", reservationId);
                result.Status = "MissingCheckoutSession";
                result.Error = "Checkout session is missing.";
                return result;
            }

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                var unavailable = ProviderUnavailable(reservation, gateway);
                _logger.LogWarning("Payments/Resume => Provider unavailable reservation={ReservationId} provider={Provider} code={Code}", reservationId, unavailable.Provider, unavailable.Code);
                result.Status = ProviderUnavailableStatus;
                result.Error = unavailable.Message;
                return result;
            }

            // The start page resumes on every load with the recovery cookie; without a payer-supplied checkout
            // reference the provider is asked at most once per interval, like the browser return.
            if (string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder) && !TryEnterProviderCheck(reservationId))
            {
                // Another check (usually the browser return's confirmation) is running: the payer waits on the
                // polling status page instead of being offered to cancel a payment that may be confirmed right now.
                _logger.LogInformation("Payments/Resume => Provider check throttled reservation={ReservationId}", reservationId);
                result.Success = true;
                result.Status = "Status";
                result.Error = null;
                return result;
            }

            try
            {
                var checkout = gateway.GetCheckout(reservation.StripeCheckoutSessionId);
                if (checkout == null)
                {
                    _logger.LogWarning("Payments/Resume => Session not found reservation={ReservationId} sessionId={SessionId}",
                        reservationId,
                        reservation.StripeCheckoutSessionId);
                    result.Status = "MissingCheckoutSession";
                    result.Error = "Checkout session was not found.";
                    return result;
                }

                if (checkout.State == PaymentCheckoutState.Open &&
                    !string.IsNullOrWhiteSpace(checkout.ResumeUrl))
                {
                    _logger.LogInformation("Payments/Resume => Resuming open checkout reservation={ReservationId} sessionId={SessionId}",
                        reservationId,
                        checkout.SessionReference);
                    result.Success = true;
                    result.Status = "Redirect";
                    result.CheckoutUrl = checkout.ResumeUrl;
                    result.Error = null;
                    return result;
                }

                if (checkout.State == PaymentCheckoutState.Complete &&
                    string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder) &&
                    IsHeldOrCapturedCheckoutAuthorization(checkout))
                {
                    // The payer paid but the browser return never arrived (and no notification has yet): finish the
                    // confirmation now, so the payer continues on the status page instead of cancelling a paid hold.
                    _logger.LogInformation("Payments/Resume => Confirming completed checkout reservation={ReservationId} sessionId={SessionId} sessionStatus={SessionStatus}",
                        reservationId,
                        checkout.SessionReference ?? reservation.StripeCheckoutSessionId,
                        checkout.RawStatus ?? "(null)");
                    var confirmation = ConfirmCompletedCheckout(
                        dbContext,
                        reservation,
                        gateway,
                        checkout,
                        reservation.StripeCheckoutSessionId,
                        new PaymentConfirmationResult { Reservation = reservation });
                    if (confirmation.Success ||
                        !string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Success = true;
                        result.Status = "Status";
                        result.Error = null;
                        return result;
                    }

                    result.Status = "ResumeUnavailable";
                    result.Error = confirmation.Error;
                    return result;
                }

                _logger.LogInformation("Payments/Resume => Checkout session not resumable reservation={ReservationId} sessionId={SessionId} sessionStatus={SessionStatus}",
                    reservationId,
                    checkout.SessionReference ?? reservation.StripeCheckoutSessionId,
                    checkout.RawStatus ?? "(null)");
                result.Status = "ResumeUnavailable";
                result.Error = $"Checkout session is '{checkout.RawStatus ?? "unknown"}' and can no longer be resumed.";
                return result;
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogWarning(pex, "Payments/Resume => Failed fetching checkout session reservation={ReservationId}", reservationId);
                result.Status = ProviderErrorStatus(gateway);
                result.Error = pex.Message;
                return result;
            }
        }

        public PaymentR1InvoiceResult RequestR1Invoice(
            OCPPCoreContext dbContext,
            PaymentR1InvoiceRequest request) =>
            RequestR1InvoiceAsync(dbContext, request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public async Task<PaymentR1InvoiceResult> RequestR1InvoiceAsync(
            OCPPCoreContext dbContext,
            PaymentR1InvoiceRequest request,
            CancellationToken cancellationToken)
        {
            var result = new PaymentR1InvoiceResult
            {
                Success = false,
                Status = "Invalid",
                Error = "Invalid request."
            };

            if (!IsOperational)
            {
                result.Status = "Disabled";
                result.Error = $"{DefaultProviderName} integration is disabled.";
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            if (request == null || request.ReservationId == Guid.Empty)
            {
                result.Error = "Reservation id is required.";
                return result;
            }

            using var buyerMutationGate = dbContext.Database.IsRelational()
                ? null
                : await InvoiceBuyerMutationGate
                    .EnterAsync(request.ReservationId, cancellationToken)
                    .ConfigureAwait(false);

            var reservation = dbContext.ChargePaymentReservations.Find(request.ReservationId);
            if (reservation == null)
            {
                result.Status = "NotFound";
                result.Error = "Reservation not found.";
                return result;
            }

            var buyerConfirmedBeforeRequest = reservation.InvoiceBuyerConfirmedAtUtc.HasValue;
            var buyerValidation = reservation.InvoiceBuyerConfirmedAtUtc.HasValue
                ? InvoiceBuyerDataValidator.ValidateAndNormalizeComplete(request)
                : InvoiceBuyerDataValidator.ValidateAndNormalize(request);
            if (!buyerValidation.Success)
            {
                result.Status = buyerValidation.Status;
                result.Error = buyerValidation.Error;
                return result;
            }
            var buyerData = buyerValidation.Data;

            if (!InvoiceSubmissionLogLookup.TryHasSubmittedOrExternalInvoice(
                    dbContext,
                    reservation.ReservationId,
                    _logger,
                    "Payments/RequestR1",
                    out var hasSubmittedOrExternalInvoice))
            {
                result.Status = "InvoiceStateUnavailable";
                result.Error = "Invoice status could not be verified. Try again later or contact support.";
                result.Reservation = reservation;
                result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                return result;
            }

            if (hasSubmittedOrExternalInvoice)
            {
                result.Status = "InvoiceAlreadyIssued";
                result.Error = "Invoice buyer data cannot be changed after submission. Use the supported provider correction, storno, or reissue process.";
                result.Reservation = reservation;
                result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                return result;
            }

            if (!InvoiceSubmissionLogLookup.TryHasIndeterminateOrActiveInvoice(
                    dbContext,
                    reservation.ReservationId,
                    _utcNow(),
                    _logger,
                    "Payments/RequestR1",
                    out var hasIndeterminateOrActiveInvoice))
            {
                result.Status = "InvoiceStateUnavailable";
                result.Error = "Invoice status could not be verified. Try again later or contact support.";
                result.Reservation = reservation;
                result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                return result;
            }

            if (hasIndeterminateOrActiveInvoice)
            {
                result.Status = "InvoiceStateUnavailable";
                result.Error = "Invoice submission may be in progress or require reconciliation. Buyer details are temporarily locked; contact support if this persists.";
                result.Reservation = reservation;
                result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                return result;
            }

            bool buyerDataMatches = reservation.InvoiceBuyerConfirmedAtUtc.HasValue &&
                MatchesConfirmedBuyer(reservation, buyerData);
            bool buyerDataChanged = !buyerDataMatches;

            if (buyerDataChanged &&
                reservation.InvoiceBuyerConfirmedAtUtc.HasValue &&
                request.BuyerDataVersion != reservation.InvoiceBuyerConfirmedAtUtc)
            {
                result.Status = "BuyerDataChanged";
                result.Error = "Invoice buyer data changed in another session. Reload the latest details and try again.";
                result.Reservation = reservation;
                result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                return result;
            }

            if (buyerDataChanged &&
                !reservation.InvoiceBuyerConfirmedAtUtc.HasValue &&
                request.BuyerDataVersion.HasValue)
            {
                result.Status = "BuyerDataChanged";
                result.Error = "Invoice buyer data changed in another session. Reload the latest details and try again.";
                result.Reservation = reservation;
                return result;
            }

            if (string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
            {
                result.Status = "MissingCheckoutSession";
                result.Error = "Checkout session id is missing.";
                result.Reservation = reservation;
                return result;
            }

            if (buyerDataChanged)
            {
                bool preserveVatVerification = reservation.InvoiceBuyerConfirmedAtUtc.HasValue &&
                    string.Equals(reservation.InvoiceBuyerCountry, buyerData.Country, StringComparison.Ordinal) &&
                    reservation.InvoiceBuyerIdentifierIsVatRegistration == buyerData.IdentifierIsVatRegistration &&
                    (buyerData.IdentifierIsVatRegistration
                        ? string.Equals(
                            reservation.InvoiceBuyerNormalizedVatIdentifier,
                            buyerData.NormalizedVatIdentifier,
                            StringComparison.Ordinal)
                        : string.Equals(
                            reservation.InvoiceBuyerTaxIdentifier,
                            buyerData.TaxIdentifier,
                            StringComparison.Ordinal));
                ViesVerificationResult viesVerification = null;
                if (!preserveVatVerification &&
                    !string.IsNullOrWhiteSpace(buyerData.NormalizedVatIdentifier) &&
                    !string.IsNullOrWhiteSpace(buyerData.ViesCountryCode))
                {
                    var vatNumber = buyerData.NormalizedVatIdentifier.Substring(
                        buyerData.ViesCountryCode.Length);
                    viesVerification = await _viesVerificationService.VerifyAsync(
                            buyerData.ViesCountryCode,
                            vatNumber,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                using var databaseMutationLock = InvoiceBuyerDatabaseMutationLock.Acquire(
                    dbContext,
                    reservation.ReservationId);
                if (databaseMutationLock != null)
                {
                    dbContext.Entry(reservation).Reload();
                    buyerDataMatches = reservation.InvoiceBuyerConfirmedAtUtc.HasValue &&
                        MatchesConfirmedBuyer(reservation, buyerData);
                    buyerDataChanged = !buyerDataMatches;
                    if (buyerDataChanged &&
                        reservation.InvoiceBuyerConfirmedAtUtc.HasValue &&
                        request.BuyerDataVersion != reservation.InvoiceBuyerConfirmedAtUtc)
                    {
                        result.Status = "BuyerDataChanged";
                        result.Error = "Invoice buyer data changed in another session. Reload the latest details and try again.";
                        result.Reservation = reservation;
                        result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                        return result;
                    }

                    if (!buyerDataChanged)
                    {
                        databaseMutationLock.Commit();
                        goto MirrorBuyerMetadata;
                    }
                }

                if (!InvoiceSubmissionLogLookup.TryHasSubmittedOrExternalInvoice(
                        dbContext,
                        reservation.ReservationId,
                        _logger,
                        "Payments/RequestR1BeforeSave",
                        out hasSubmittedOrExternalInvoice) ||
                    !InvoiceSubmissionLogLookup.TryHasIndeterminateOrActiveInvoice(
                        dbContext,
                        reservation.ReservationId,
                        _utcNow(),
                        _logger,
                        "Payments/RequestR1BeforeSave",
                        out hasIndeterminateOrActiveInvoice))
                {
                    result.Status = "InvoiceStateUnavailable";
                    result.Error = "Invoice status could not be verified. Try again later or contact support.";
                    result.Reservation = reservation;
                    result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                    return result;
                }

                if (hasSubmittedOrExternalInvoice || hasIndeterminateOrActiveInvoice)
                {
                    result.Status = hasSubmittedOrExternalInvoice
                        ? "InvoiceAlreadyIssued"
                        : "InvoiceStateUnavailable";
                    result.Error = hasSubmittedOrExternalInvoice
                        ? "Invoice buyer data cannot be changed after submission. Use the supported provider correction, storno, or reissue process."
                        : "Invoice submission may be in progress or require reconciliation. Buyer details are temporarily locked; contact support if this persists.";
                    result.Reservation = reservation;
                    result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                    return result;
                }

                var existingVatVerificationStatus = reservation.InvoiceBuyerVatVerificationStatus;
                var existingVatVerificationCheckedAtUtc = reservation.InvoiceBuyerVatVerificationCheckedAtUtc;
                var existingVatVerificationReference = reservation.InvoiceBuyerVatVerificationReference;
                var buyerDataVersion = NextBuyerDataVersion(
                    reservation.InvoiceBuyerConfirmedAtUtc,
                    _utcNow());
                ApplyConfirmedBuyer(
                    reservation,
                    buyerData,
                    buyerDataVersion,
                    viesVerification);
                if (preserveVatVerification)
                {
                    reservation.InvoiceBuyerVatVerificationStatus = existingVatVerificationStatus;
                    reservation.InvoiceBuyerVatVerificationCheckedAtUtc = existingVatVerificationCheckedAtUtc;
                    reservation.InvoiceBuyerVatVerificationReference = existingVatVerificationReference;
                }
                reservation.UpdatedAtUtc = buyerDataVersion;
                try
                {
                    dbContext.SaveChanges();
                    databaseMutationLock?.Commit();
                }
                catch (DbUpdateConcurrencyException)
                {
                    var entry = dbContext.Entry(reservation);
                    entry.Reload();
                    if (!reservation.InvoiceBuyerConfirmedAtUtc.HasValue || !MatchesConfirmedBuyer(reservation, buyerData))
                    {
                        result.Status = "BuyerDataChanged";
                        result.Error = "Invoice buyer data changed in another session. Reload the latest details and try again.";
                        result.Reservation = reservation;
                        result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                        return result;
                    }
                }
            }

        MirrorBuyerMetadata:
            dbContext.Entry(reservation).Reload();
            bool alreadyRequested = false;
            bool metadataMirrored = false;
            Exception metadataMirrorError = null;
            try
            {
                metadataMirrored = MirrorR1BuyerMetadata(
                    dbContext,
                    reservation,
                    buyerConfirmedBeforeRequest,
                    out alreadyRequested);
            }
            catch (Exception ex) when (ex is PaymentProviderException || ex is InvalidOperationException)
            {
                metadataMirrorError = ex;
                _logger.LogWarning(
                    ex,
                    "Payments/R1Request => Buyer snapshot saved but metadata mirror failed reservation={ReservationId}",
                    request.ReservationId);
            }

            var reconciliationQueued = false;
            if (!metadataMirrored)
            {
                reconciliationQueued = TryQueueR1BuyerMetadataReconciliation(reservation.ReservationId);
                _logger.LogWarning(
                    metadataMirrorError,
                    "Payments/R1Request => Database buyer snapshot remains authoritative; metadata mirror is pending reservation={ReservationId} reconciliationQueued={ReconciliationQueued}",
                    reservation.ReservationId,
                    reconciliationQueued);
            }
            else if (!alreadyRequested)
            {
                TrySendR1RequestedNotification(dbContext, reservation);
            }

            var finalBuyerTaxIdentifier = reservation.InvoiceBuyerTaxIdentifier;
            var finalBuyerOib = string.Equals(reservation.InvoiceBuyerCountry, "HR", StringComparison.OrdinalIgnoreCase)
                ? TrimMetadataValue(finalBuyerTaxIdentifier, 32)
                : null;
            var metadataRetryRequired = !metadataMirrored && !reconciliationQueued;
            result.Success = !metadataRetryRequired;
            result.Status = metadataMirrored
                ? "Updated"
                : reconciliationQueued
                    ? "UpdatedMetadataPending"
                    : "UpdatedMetadataRetryRequired";
            result.Error = metadataRetryRequired
                ? "Buyer details were saved, but payment metadata could not be synchronized or queued. Retry this save or contact support."
                : null;
            result.Reservation = reservation;
            result.BuyerCompanyName = reservation.InvoiceBuyerCompanyName;
            result.BuyerOib = finalBuyerOib;
            result.BuyerCountry = reservation.InvoiceBuyerCountry;
            result.BuyerTaxIdentifier = finalBuyerTaxIdentifier;
            result.BuyerDataVersion = reservation.InvoiceBuyerConfirmedAtUtc;

            _logger.LogInformation(
                "Payments/R1Request => Saved authoritative buyer snapshot reservation={ReservationId} sessionId={SessionId} metadataMirrored={MetadataMirrored}",
                reservation.ReservationId,
                reservation.StripeCheckoutSessionId,
                metadataMirrored);

            return result;
        }

        public bool ReconcileR1BuyerMetadata(OCPPCoreContext dbContext, Guid reservationId)
        {
            if (!IsOperational || dbContext == null || reservationId == Guid.Empty)
            {
                return false;
            }

            using var buyerMutationGate = dbContext.Database.IsRelational()
                ? null
                : InvoiceBuyerMutationGate.Enter(reservationId);
            using var databaseMutationLock = InvoiceBuyerDatabaseMutationLock.Acquire(
                dbContext,
                reservationId);
            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null || !reservation.InvoiceBuyerConfirmedAtUtc.HasValue)
            {
                return false;
            }

            dbContext.Entry(reservation).Reload();
            var mirrored = MirrorR1BuyerMetadata(
                dbContext,
                reservation,
                buyerConfirmedBeforeRequest: true,
                out var alreadyRequested);
            if (!mirrored)
            {
                return false;
            }

            databaseMutationLock?.Commit();
            if (!alreadyRequested)
            {
                TrySendR1RequestedNotification(dbContext, reservation);
            }

            return true;
        }

        private bool MirrorR1BuyerMetadata(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            bool buyerConfirmedBeforeRequest,
            out bool alreadyRequested)
        {
            alreadyRequested = false;
            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway) || !gateway.SupportsMetadata)
            {
                // Providers without free-form metadata (or a provider that cannot be called) keep no buyer
                // copy: the database snapshot is authoritative, so there is nothing to mirror or reconcile.
                alreadyRequested = reservation.InvoiceR1RequestedAtUtc.HasValue || buyerConfirmedBeforeRequest;
                _logger.LogInformation(
                    "Payments/R1Request => Buyer snapshot kept in the database only provider={Provider} reservation={ReservationId} metadataSupported={MetadataSupported}",
                    gateway?.ProviderName ?? ReservationProviderName(reservation),
                    reservation.ReservationId,
                    gateway?.SupportsMetadata ?? false);
                return true;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var mirroredVersion = reservation.InvoiceBuyerConfirmedAtUtc;
                var buyerCompanyName = reservation.InvoiceBuyerCompanyName;
                var buyerTaxIdentifier = reservation.InvoiceBuyerTaxIdentifier;
                var buyerCountry = reservation.InvoiceBuyerCountry;
                var buyerOibForMetadata = string.Equals(buyerCountry, "HR", StringComparison.OrdinalIgnoreCase)
                    ? TrimMetadataValue(buyerTaxIdentifier, 32)
                    : null;
                var previousMetadata = gateway.MirrorMetadata(
                    reservation.StripeCheckoutSessionId,
                    reservation.StripePaymentIntentId,
                    metadata => ApplyR1BuyerMetadata(
                        metadata,
                        buyerCompanyName,
                        buyerCountry,
                        buyerTaxIdentifier,
                        buyerOibForMetadata,
                        mirroredVersion));
                alreadyRequested |= IsR1InvoiceRequested(previousMetadata);

                dbContext.Entry(reservation).Reload();
                if (reservation.InvoiceBuyerConfirmedAtUtc == mirroredVersion)
                {
                    return true;
                }
            }

            throw new InvalidOperationException(
                $"{gateway.ProviderName} buyer metadata did not converge to the latest version for reservation '{reservation.ReservationId}'.");
        }

        private bool TryQueueR1BuyerMetadataReconciliation(Guid reservationId)
        {
            if (_backgroundJobClient == null)
            {
                return false;
            }

            try
            {
                var job = Job.FromExpression<IStripeBuyerMetadataReconciliationJob>(reconciliationJob =>
                    reconciliationJob.Reconcile(reservationId));
                var queue = _configuration?.GetValue<string>("Hangfire:Queue")?.Trim();
                if (string.IsNullOrWhiteSpace(queue))
                {
                    queue = PaymentAuthorizationEmailQueue;
                }
                var jobId = _backgroundJobClient.Create(
                    job,
                    new EnqueuedState(queue));
                return !string.IsNullOrWhiteSpace(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Payments/R1Request => Failed to queue buyer metadata reconciliation reservation={ReservationId}",
                    reservationId);
                return false;
            }
        }

        private static void ApplyConfirmedBuyer(
            ChargePaymentReservation reservation,
            InvoiceBuyerData buyer,
            DateTime confirmedAtUtc,
            ViesVerificationResult viesVerification = null)
        {
            reservation.InvoiceBuyerCountry = buyer.Country;
            reservation.InvoiceBuyerCompanyName = buyer.CompanyName;
            reservation.InvoiceBuyerStreet = buyer.Street;
            reservation.InvoiceBuyerPostalCode = buyer.PostalCode;
            reservation.InvoiceBuyerCity = buyer.City;
            reservation.InvoiceBuyerEmail = buyer.Email;
            reservation.InvoiceBuyerTaxIdentifier = buyer.TaxIdentifier;
            reservation.InvoiceBuyerOriginalTaxIdentifier =
                buyer.OriginalTaxIdentifier ?? buyer.TaxIdentifier;
            reservation.InvoiceBuyerNormalizedVatIdentifier = buyer.NormalizedVatIdentifier;
            reservation.InvoiceBuyerVatValidationStatus =
                string.IsNullOrWhiteSpace(buyer.NormalizedVatIdentifier)
                    ? VatValidationStatus.NotApplicable
                    : VatValidationStatus.Valid;
            reservation.InvoiceBuyerVatVerificationStatus =
                viesVerification?.Status ?? ViesVerificationStatus.NotChecked;
            reservation.InvoiceBuyerVatVerificationCheckedAtUtc =
                viesVerification?.CheckedAtUtc;
            reservation.InvoiceBuyerVatVerificationReference =
                viesVerification?.Reference;
            reservation.InvoiceBuyerRegistrationNumber = buyer.RegistrationNumber;
            reservation.InvoiceBuyerIdentifierIsVatRegistration = buyer.IdentifierIsVatRegistration;
            reservation.InvoiceBuyerConfirmedAtUtc = confirmedAtUtc;
        }

        private static DateTime NextBuyerDataVersion(DateTime? currentVersion, DateTime utcNow)
        {
            if (!currentVersion.HasValue || utcNow > currentVersion.Value)
            {
                return utcNow;
            }

            return currentVersion.Value.AddMilliseconds(1);
        }

        private static bool MatchesConfirmedBuyer(ChargePaymentReservation reservation, InvoiceBuyerData buyer)
        {
            return string.Equals(reservation.InvoiceBuyerCountry, buyer.Country, StringComparison.Ordinal) &&
                   string.Equals(reservation.InvoiceBuyerCompanyName, buyer.CompanyName, StringComparison.Ordinal) &&
                   string.Equals(reservation.InvoiceBuyerStreet, buyer.Street, StringComparison.Ordinal) &&
                   string.Equals(reservation.InvoiceBuyerPostalCode, buyer.PostalCode, StringComparison.Ordinal) &&
                   string.Equals(reservation.InvoiceBuyerCity, buyer.City, StringComparison.Ordinal) &&
                   string.Equals(reservation.InvoiceBuyerEmail, buyer.Email, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(reservation.InvoiceBuyerTaxIdentifier, buyer.TaxIdentifier, StringComparison.Ordinal) &&
                   string.Equals(
                       reservation.InvoiceBuyerOriginalTaxIdentifier ?? reservation.InvoiceBuyerTaxIdentifier,
                       buyer.OriginalTaxIdentifier ?? buyer.TaxIdentifier,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       reservation.InvoiceBuyerNormalizedVatIdentifier,
                       buyer.NormalizedVatIdentifier,
                       StringComparison.Ordinal) &&
                   string.Equals(reservation.InvoiceBuyerRegistrationNumber, buyer.RegistrationNumber, StringComparison.Ordinal) &&
                   reservation.InvoiceBuyerIdentifierIsVatRegistration == buyer.IdentifierIsVatRegistration;
        }

        private static void SetOrRemoveMetadata(IDictionary<string, string> metadata, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) metadata.Remove(key);
            else metadata[key] = value;
        }

        private static void ApplyR1BuyerMetadata(
            IDictionary<string, string> metadata,
            string buyerCompanyName,
            string buyerCountry,
            string buyerTaxIdentifier,
            string buyerOib,
            DateTime? buyerDataVersion)
        {
            metadata["invoice_type"] = "R1";
            SetOrRemoveMetadata(metadata, "buyer_oib", buyerOib);
            SetOrRemoveMetadata(metadata, "buyer_country", buyerCountry);
            SetOrRemoveMetadata(metadata, "buyer_tax_identifier", TrimMetadataValue(buyerTaxIdentifier, 64));
            SetOrRemoveMetadata(metadata, "buyer_company", buyerCompanyName);
            SetOrRemoveMetadata(metadata, "buyer_data_version", buyerDataVersion?.ToString("O"));
        }

        public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason)
        {
            if (!IsOperational) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null) return;

            CancelLoadedReservation(dbContext, reservation, reason, remainingClaims: 3);
        }

        /// <summary>
        /// Cancels the reservation in its current status. The cancel is claimed (a conditional status update) before
        /// any provider call: a confirmation or notification that authorizes the reservation at the same time either
        /// wins and the cancel then applies to the authorized reservation, or loses and releases what it found
        /// instead of starting a session on a hold cancelled here.
        /// </summary>
        private void CancelLoadedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason, int remainingClaims)
        {
            var reservationId = reservation.ReservationId;
            if (!PaymentReservationStatus.IsCancelable(reservation.Status))
            {
                _logger.LogInformation(
                    "Payments/Cancel => Ignoring cancel for reservation={ReservationId} status={Status}",
                    reservationId,
                    reservation.Status ?? "(none)");
                return;
            }

            var loadedStatus = reservation.Status;
            var resolvePendingCheckout =
                string.Equals(loadedStatus, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) &&
                !string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId) &&
                ResolvesPendingCheckoutBeforeCancel(reservation);
            var loadedReleaseState = reservation.AuthorizationReleaseState;
            var loadedReleaseNextAttemptAtUtc = reservation.AuthorizationReleaseNextAttemptAtUtc;
            var loadedReleaseAttemptCount = reservation.AuthorizationReleaseAttemptCount;

            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.UpdatedAtUtc = _utcNow();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                reservation.LastError = reason;
            }

            if (!string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) || resolvePendingCheckout)
            {
                // A hold may exist: the release is armed in the same conditional update that claims the cancel, so a
                // failure or a stop before the provider confirms the cancel leaves a release for the cleanup sweep.
                // The first attempt waits one in-progress timeout so the sweep does not race this request's own
                // provider cancel (which marks the release Released when it is confirmed).
                ArmAuthorizationReleaseState(reservation, _utcNow().Add(ResolveAuthorizationReleaseInProgressTimeout()));
            }

            if (!TryCommitReservationTransition(dbContext, reservation, loadedStatus))
            {
                _logger.LogInformation(
                    "Payments/Cancel => Reservation changed before the cancel was claimed reservation={ReservationId} previousStatus={PreviousStatus} status={Status}",
                    reservationId,
                    loadedStatus ?? "(none)",
                    reservation.Status ?? "(none)");
                if (remainingClaims > 1 && dbContext.Entry(reservation).State != EntityState.Detached)
                {
                    CancelLoadedReservation(dbContext, reservation, reason, remainingClaims - 1);
                }

                return;
            }

            var pendingCheckoutResolved = false;
            if (resolvePendingCheckout)
            {
                pendingCheckoutResolved = ResolvePendingCheckoutBeforeCancel(reservation, reason, out var releaseNotNeeded);
                if (releaseNotNeeded)
                {
                    // The provider showed that nothing is held (or the payment was captured at checkout and is
                    // refunded instead): the arming of the claim is withdrawn.
                    reservation.AuthorizationReleaseState = loadedReleaseState;
                    reservation.AuthorizationReleaseNextAttemptAtUtc = loadedReleaseNextAttemptAtUtc;
                    reservation.AuthorizationReleaseAttemptCount = loadedReleaseAttemptCount;
                }

                if (!string.Equals(reservation.Status, PaymentReservationStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                {
                    // The checkout turned out to be captured already; that outcome is recorded instead of a cancel.
                    reservation.UpdatedAtUtc = _utcNow();
                    dbContext.SaveChanges();
                    return;
                }
            }

            if (!pendingCheckoutResolved)
            {
                CancelPaymentIntentIfCancelable(dbContext, reservation, reason);
            }

            // The cancel reason stays the reservation's outcome; a release problem is kept in the release fields.
            reservation.UpdatedAtUtc = _utcNow();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                reservation.LastError = reason;
            }
            dbContext.SaveChanges();

            _logger.LogInformation("Payments/Cancel => Marked reservation={ReservationId} as Cancelled. LastError={LastError}", reservationId, reservation.LastError ?? "(none)");
        }

        public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason)
        {
            if (!IsOperational) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservation == null) return;
            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId)) return;

            // A hold that cannot be confirmed released stays armed for the release reconciliation, which retries
            // with backoff; callers move the reservation to a terminal status that the reconciliation covers.
            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                var unavailable = ProviderUnavailable(reservation, gateway);
                _logger.LogWarning("Payments/CancelPI => Provider unavailable reservation={ReservationId} provider={Provider} code={Code}", reservation.ReservationId, unavailable.Provider, unavailable.Code);
                reservation.LastError = unavailable.Message;
                ArmAuthorizationRelease(reservation, FormatProviderError(unavailable, unavailable.Message), gateway);
                dbContext.SaveChanges();
                return;
            }

            try
            {
                var authorization = gateway.GetAuthorization(reservation.StripePaymentIntentId);
                if (authorization == null)
                {
                    var message = $"{AuthorizationSubject(gateway)} was not found while cancelling; release will be retried.";
                    _logger.LogWarning("Payments/CancelPI => Payment authorization not found reservation={ReservationId} paymentIntent={PaymentIntentId}", reservation.ReservationId, reservation.StripePaymentIntentId);
                    reservation.LastError = message;
                    ArmAuthorizationRelease(reservation, message, gateway);
                    dbContext.SaveChanges();
                    return;
                }

                if (authorization.IsCancelable)
                {
                    _logger.LogInformation("Payments/CancelPI => Cancelling uncaptured payment authorization provider={Provider} reservation={ReservationId} paymentIntent={PaymentIntentId} status={Status} reason={Reason}",
                        gateway.ProviderName,
                        reservation.ReservationId,
                        authorization.AuthorizationReference,
                        authorization.RawStatus,
                        reason ?? "(none)");

                    var canceled = gateway.Cancel(
                        authorization.AuthorizationReference,
                        BuildIdempotencyKey(IdempotencyCancel, reservation.ReservationId));
                    if (canceled?.State != PaymentAuthorizationState.Canceled)
                    {
                        var message = $"{AuthorizationSubject(gateway)} cancellation returned status '{Truncate(canceled?.RawStatus, 50) ?? "missing"}'; release will be retried.";
                        _logger.LogWarning("Payments/CancelPI => Cancellation not confirmed reservation={ReservationId} status={Status}", reservation.ReservationId, canceled?.RawStatus ?? "(null)");
                        reservation.LastError = message;
                        ArmAuthorizationRelease(reservation, message, gateway);
                        dbContext.SaveChanges();
                    }
                    else
                    {
                        MarkArmedAuthorizationReleased(dbContext, reservation);
                    }
                }
                else
                {
                    _logger.LogInformation("Payments/CancelPI => Payment authorization not cancelable status={Status} reservation={ReservationId}", authorization.RawStatus, reservation.ReservationId);
                    if (authorization.State == PaymentAuthorizationState.Canceled)
                    {
                        MarkArmedAuthorizationReleased(dbContext, reservation);
                    }

                    // Any other state stays armed when the caller armed the release: the release reconciliation
                    // checks it (a captured or unsettled authorization ends there in review).
                }
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogWarning(pex, "Payments/CancelPI => Unable to cancel payment authorization {PaymentIntentId}", reservation.StripePaymentIntentId);
                reservation.LastError = pex.Message;
                ArmAuthorizationRelease(reservation, FormatProviderError(pex, pex.Message), gateway);
                dbContext.SaveChanges();
            }
        }

        /// <summary>
        /// The provider confirmed that the authorization is cancelled: a release that the caller armed (the cancel
        /// and start-timeout claims arm it before the provider call) is finished as released.
        /// </summary>
        private void MarkArmedAuthorizationReleased(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (!PaymentAuthorizationReleaseState.IsArmed(reservation.AuthorizationReleaseState))
            {
                return;
            }

            MarkAuthorizationReleased(reservation);
            dbContext.SaveChanges();
        }

        public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId)
        {
            if (!IsOperational) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var normalizedTag = NormalizeChargeTag(chargeTagId);

            var reservation = FindActiveReservationByTag(dbContext, chargePointId, connectorId, normalizedTag);

            if (reservation == null) return;

            var transaction = dbContext.Transactions.Find(transactionId);
            CloseSupersededTransactions(dbContext, reservation, transaction);
            RelinkReservationToTransaction(reservation, transactionId, transaction);
            reservation.Status = PaymentReservationStatus.Charging;
            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();

            _logger.LogInformation("Payments/MarkTransactionStarted => reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId} startAt={StartAtUtc}",
                reservation.ReservationId,
                transactionId,
                reservation.ChargePointId,
                reservation.ConnectorId,
                reservation.StartTransactionAtUtc);

            TrySendR1RequestedNotification(dbContext, reservation);
        }

        public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction)
        {
            CompleteReservationCore(dbContext, transaction, allowTerminalReplay: false, suppressCustomerSideEffects: false, recoveryAssessment: null, recoveryReservation: null, recoveryAuthorization: null);
        }

        public void RecoverTerminalSettlement(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            Transaction transaction)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var assessment = AssessTerminalSettlement(dbContext, reservation, transaction);
            if (!assessment.Eligible || assessment.TotalCents <= 0)
            {
                throw new InvalidOperationException(
                    assessment.Eligible
                        ? "Recovery settlement has no positive billable amount."
                        : assessment.Reason);
            }

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                throw new InvalidOperationException(ProviderUnavailable(reservation, gateway).Message);
            }

            var authorization = gateway.GetAuthorization(reservation.StripePaymentIntentId);
            if (authorization == null ||
                !string.Equals(authorization.AuthorizationReference, reservation.StripePaymentIntentId, StringComparison.Ordinal) ||
                !ProviderOwnershipMatches(authorization, reservation.ReservationId) ||
                string.IsNullOrWhiteSpace(authorization.Currency) ||
                !string.Equals(authorization.Currency, reservation.Currency, StringComparison.OrdinalIgnoreCase) ||
                authorization.State != PaymentAuthorizationState.RequiresCapture ||
                authorization.AmountMinor != assessment.TotalCents ||
                authorization.AmountCapturableMinor != assessment.TotalCents)
            {
                throw new InvalidOperationException(
                    "Provider payment authorization does not exactly match the reviewed recovery settlement.");
            }

            CompleteReservationCore(dbContext, transaction, allowTerminalReplay: true, suppressCustomerSideEffects: true, recoveryAssessment: assessment, recoveryReservation: reservation, recoveryAuthorization: authorization);
        }

        public Recovery.FinancialRecoverySettlementDecision AssessTerminalSettlement(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            Transaction transaction)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (!IsOperational)
            {
                return new Recovery.FinancialRecoverySettlementDecision
                {
                    Eligible = false,
                    Reason = "Payment provider integration is disabled."
                };
            }
            if (reservation == null || transaction == null)
            {
                return Recovery.FinancialRecoverySettlementAssessor.Assess(reservation, transaction);
            }

            var flowOptions = ResolveFlowOptions(dbContext);
            return Recovery.FinancialRecoverySettlementAssessor.Assess(
                reservation,
                transaction,
                flowOptions,
                _logger);
        }

        private void CompleteReservationCore(
            OCPPCoreContext dbContext,
            Transaction transaction,
            bool allowTerminalReplay,
            bool suppressCustomerSideEffects,
            Recovery.FinancialRecoverySettlementDecision recoveryAssessment,
            ChargePaymentReservation recoveryReservation,
            PaymentAuthorizationSnapshot recoveryAuthorization)
        {
            if (!IsOperational) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var reservation = recoveryAssessment == null
                ? FindReservationForTransaction(dbContext, transaction)
                : recoveryReservation;
            if (reservation == null) return;
            if ((!allowTerminalReplay && reservation.Status == PaymentReservationStatus.Completed) ||
                reservation.Status == PaymentReservationStatus.Cancelled)
            {
                return;
            }

            var meterEvidence = recoveryAssessment == null
                ? MeterEvidenceSettlementGuard.EnsureReady(dbContext, reservation, transaction, "StripeComplete")
                : MeterEvidenceSettlementGuard.Assess(reservation, transaction);
            if (!meterEvidence.Ready)
            {
                reservation.Status = PaymentReservationStatus.ReviewRequired;
                reservation.LastError = meterEvidence.Reason;
                if (string.Equals(reservation.FailureCode, SettlementRetryPendingCode, StringComparison.Ordinal))
                {
                    reservation.FailureCode = null;
                }
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                _logger.LogWarning(
                    "Payments/Complete => Meter evidence requires review reservation={ReservationId} tx={TransactionId} reason={Reason}",
                    reservation.ReservationId,
                    transaction.TransactionId,
                    meterEvidence.Reason);
                return;
            }

            var now = _utcNow();
            // Set when an earlier settlement attempt failed transiently and this run retries it.
            var retryingSettlement = recoveryAssessment == null &&
                                     string.Equals(reservation.FailureCode, SettlementRetryPendingCode, StringComparison.Ordinal);
            if (recoveryAssessment == null)
            {
                CloseSupersededTransactions(dbContext, reservation, transaction);
                RelinkReservationToTransaction(reservation, transaction.TransactionId, transaction);
                reservation.StopTransactionAtUtc ??= transaction.StopTime ?? now;
                reservation.UpdatedAtUtc = now;
            }

            if (reservation.UsageFeeAnchorMinutes == 1 &&
                !transaction.ChargingEndedAtUtc.HasValue &&
                reservation.StopTransactionAtUtc.HasValue)
            {
                // A retried settlement already holds its final idle snapshot on the transaction: count no further
                // idle interval, so the retry settles (and captures with the idempotency key of) the same amount.
                transaction.ChargingEndedAtUtc = retryingSettlement
                    ? reservation.DisconnectedAtUtc ?? reservation.StopTransactionAtUtc.Value
                    : reservation.StopTransactionAtUtc.Value;
            }

            bool connectorAvailable = false;
            bool hasConnectorAvailability = recoveryAssessment == null &&
                                            TryGetConnectorAvailability(dbContext, reservation, out connectorAvailable);
            bool disconnected = recoveryAssessment != null ||
                                reservation.DisconnectedAtUtc.HasValue ||
                                IsDisconnectedStopReason(transaction.StopReason) ||
                                connectorAvailable;

            if (recoveryAssessment == null && !disconnected && hasConnectorAvailability)
            {
                reservation.Status = PaymentReservationStatus.WaitingForDisconnect;
                dbContext.SaveChanges();

                _logger.LogInformation(
                    "Payments/Complete => Deferred capture until disconnect reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId}",
                    reservation.ReservationId,
                    transaction.TransactionId,
                    transaction.ChargePointId,
                    transaction.ConnectorId);
                return;
            }

            if (recoveryAssessment == null)
            {
                reservation.DisconnectedAtUtc ??= ResolveDisconnectedAtUtc(dbContext, reservation) ?? reservation.StopTransactionAtUtc ?? now;
                dbContext.SaveChanges();
            }

            _logger.LogInformation("Payments/Complete => Begin capture for reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId} currentStatus={Status}",
                reservation.ReservationId,
                transaction.TransactionId,
                transaction.ChargePointId,
                transaction.ConnectorId,
                reservation.Status);

            var flowOptions = ResolveFlowOptions(dbContext);
            var settlement = recoveryAssessment?.Calculation ?? FinancialSettlementCalculator.Calculate(
                reservation,
                transaction,
                flowOptions,
                reservation.DisconnectedAtUtc ?? now,
                _logger);
            bool hasValidDeliveredEnergy = settlement.HasValidDeliveredEnergy;
            double actualEnergy = settlement.ActualEnergyKwh;

            reservation.ActualEnergyKwh = actualEnergy;
            reservation.UpdatedAtUtc = now;

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                _logger.LogWarning("Payments/Complete => Missing payment intent reservation={ReservationId} tx={TransactionId}", reservation.ReservationId, transaction.TransactionId);
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = $"Missing {ResolveGateway(reservation)?.ProviderName ?? ReservationProviderName(reservation)} {AuthorizationNoun(ResolveGateway(reservation))} during capture.";
                dbContext.SaveChanges();
                return;
            }

            var shouldNoChargeForDeliveredEnergy = settlement.ShouldNoChargeForDeliveredEnergy;
            var deliveredEnergyNoChargeReason = settlement.DeliveredEnergyNoChargeReason;
            var energyCostCents = settlement.EnergyCostCents;
            var configuredSessionFeeCents = settlement.ConfiguredSessionFeeCents;
            var sessionFeeSuppressionReason = settlement.SessionFeeSuppressionReason;
            var sessionFeeCents = settlement.SessionFeeCents;

            if (configuredSessionFeeCents > 0 && sessionFeeCents == 0)
            {
                _logger.LogInformation(
                    "Payments/Complete => Suppressed session fee reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId} energyKwh={EnergyKwh:0.###} minimumSessionFeeKwh={MinimumSessionFeeKwh:0.###} reason={Reason}",
                    reservation.ReservationId,
                    transaction.TransactionId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    actualEnergy,
                    flowOptions.MinimumSessionFeeKwh,
                    sessionFeeSuppressionReason ?? "no configured session fee");
            }

            if (shouldNoChargeForDeliveredEnergy)
            {
                _logger.LogInformation(
                    "Payments/Complete => Treating session as no-charge reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId} energyKwh={EnergyKwh:0.###} reason={Reason}",
                    reservation.ReservationId,
                    transaction.TransactionId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    actualEnergy,
                    deliveredEnergyNoChargeReason);
            }

            var idleSnapshot = settlement.IdleSnapshot;
            var usageFeeMinutes = settlement.UsageFeeMinutes;
            var usageFeeCents = settlement.UsageFeeCents;
            var amountToCapture = settlement.AmountToCaptureCents;
            var minimumChargeAmountCents = settlement.MinimumChargeAmountCents;

            // Preserve the normal completion path's final idle snapshot before the
            // provider call. A provider exception is persisted below, so these
            // transaction fields must remain part of that same durable failure state.
            if (!shouldNoChargeForDeliveredEnergy && reservation.UsageFeeAnchorMinutes == 1)
            {
                transaction.IdleUsageFeeMinutes = idleSnapshot.TotalMinutes;
                transaction.IdleUsageFeeAmount = idleSnapshot.TotalAmount;
                transaction.ChargingEndedAtUtc = null;
            }

            _logger.LogInformation(
                "Payments/Complete => Calculated amounts reservation={ReservationId} tx={TransactionId} energyKwh={EnergyKwh:0.###} energyCents={EnergyCents} usageMinutes={UsageMinutes} usageCents={UsageCents} sessionFeeCents={SessionFeeCents} amountToCapture={AmountToCapture} minimumChargeAmountCents={MinimumChargeAmountCents}",
                reservation.ReservationId,
                transaction.TransactionId,
                actualEnergy,
                energyCostCents,
                usageFeeMinutes,
                usageFeeCents,
                sessionFeeCents,
                amountToCapture,
                minimumChargeAmountCents);

            _logger.LogInformation(
                "Payments/Complete => Final idle snapshot reservation={ReservationId} tx={TransactionId} minutes={IdleMinutes} amount={IdleAmount}",
                reservation.ReservationId,
                transaction.TransactionId,
                idleSnapshot.TotalMinutes,
                idleSnapshot.TotalAmount);

            var authorizationReleaseExpected = shouldNoChargeForDeliveredEnergy ||
                                               amountToCapture <= 0 ||
                                               (minimumChargeAmountCents > 0 &&
                                                amountToCapture < minimumChargeAmountCents);

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                // Fail closed: never capture or release through another provider.
                RecordCaptureProviderFailure(dbContext, reservation, gateway, ProviderUnavailable(reservation, gateway), authorizationReleaseExpected, now, transaction);
                return;
            }

            // A retried settlement calls capture or cancel only after a fresh lookup showed the authorization still
            // uncaptured, with a key scoped to the retry (see SettlementRetryOrdinal): Stripe stores the answer to a
            // key, a 5xx included, and would otherwise replay a stored error on every retry. A first attempt keeps
            // the plain keys.
            string SettlementIdempotencyKey(string key) =>
                retryingSettlement && !string.IsNullOrEmpty(key)
                    ? $"{key}:r{SettlementRetryOrdinal(reservation, now)}"
                    : key;

            // Set when a settlement cancel found the funds already captured, or the provider could not confirm the
            // captured amount: the captured amount is recorded, but no invoice or receipt is issued for an amount
            // the provider contradicts or has not confirmed.
            var captureFoundExistingCapture = false;
            try
            {
                var paymentIntent = recoveryAuthorization ?? gateway.GetAuthorization(reservation.StripePaymentIntentId);
                if (paymentIntent == null)
                {
                    throw new PaymentProviderException(
                        gateway.ProviderName,
                        "authorization_not_found",
                        $"{AuthorizationSubject(gateway)} was not found.",
                        isTransient: false);
                }

                if (retryingSettlement)
                {
                    // The retried settlement reached the provider: the retry marker and the retry's own error are no
                    // longer the outcome. They are cleared before the outcome is set, so its message stays; a new
                    // transient failure below records them again.
                    ClearFailureState(reservation);
                }

                if (paymentIntent.State == PaymentAuthorizationState.RequiresCapture)
                {
                    if (amountToCapture > 0)
                    {
                        var finalCaptureAmount = recoveryAssessment == null
                            ? Math.Min(amountToCapture, paymentIntent.AmountMinor)
                            : amountToCapture;
                        if (finalCaptureAmount > 0 &&
                            minimumChargeAmountCents > 0 &&
                            finalCaptureAmount < minimumChargeAmountCents)
                        {
                            authorizationReleaseExpected = true;
                            var canceled = gateway.Cancel(
                                paymentIntent.AuthorizationReference,
                                SettlementIdempotencyKey(BuildIdempotencyKey(IdempotencyCancel, reservation.ReservationId)));
                            if (ApplySettlementCancelResult(reservation, gateway, canceled, now))
                            {
                                reservation.Status = PaymentReservationStatus.Cancelled;
                                reservation.CapturedAmountCents = 0;
                                reservation.CapturedAtUtc = null;
                                reservation.LastError = $"Payment capture skipped because calculated capture amount {finalCaptureAmount} cents is below configured minimum charge amount {minimumChargeAmountCents} cents.";

                                _logger.LogInformation(
                                    "Payments/Complete => Cancelled below-minimum capture reservation={ReservationId} paymentIntent={PaymentIntentId} captureCents={CaptureCents} minimumChargeAmountCents={MinimumChargeAmountCents}",
                                    reservation.ReservationId,
                                    paymentIntent.AuthorizationReference,
                                    finalCaptureAmount,
                                    minimumChargeAmountCents);
                            }
                            else
                            {
                                captureFoundExistingCapture = true;
                            }
                        }
                        else if (finalCaptureAmount > 0)
                        {
                            var captured = gateway.Capture(
                                paymentIntent.AuthorizationReference,
                                finalCaptureAmount,
                                reservation.Currency,
                                SettlementIdempotencyKey(BuildIdempotencyKey(IdempotencyCapture, reservation.ReservationId, finalCaptureAmount)));
                            if (captured?.State == PaymentAuthorizationState.Pending)
                            {
                                // The provider accepted the capture but has not settled it (rare payment methods). Do not
                                // report the reservation as completed until an operator has confirmed the outcome.
                                reservation.Status = PaymentReservationStatus.Failed;
                                reservation.FailureCode = CapturePendingCode;
                                reservation.LastError = $"{gateway.ProviderName} reported the capture of {finalCaptureAmount} minor units as pending; review the payment before completing the reservation.";
                                reservation.FailureMessage = reservation.LastError;
                                reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.ReviewRequired;
                                reservation.AuthorizationReleaseNextAttemptAtUtc = null;
                                reservation.AuthorizationReleaseLastError = reservation.LastError;

                                _logger.LogError(
                                    "Payments/Complete => Capture pending provider={Provider} reservation={ReservationId} paymentIntent={PaymentIntentId} requestedCents={RequestedCents} status={PaymentIntentStatus}",
                                    gateway.ProviderName,
                                    reservation.ReservationId,
                                    paymentIntent.AuthorizationReference,
                                    finalCaptureAmount,
                                    captured.RawStatus);
                            }
                            else
                            {
                                var capturedAmount = captured?.State == PaymentAuthorizationState.Captured && captured.AmountReceivedMinor > 0
                                    ? captured.AmountReceivedMinor
                                    : finalCaptureAmount;
                                reservation.CapturedAmountCents = capturedAmount;
                                reservation.CapturedAtUtc = _utcNow();
                                reservation.Status = PaymentReservationStatus.Completed;
                                if (IsUnverifiedCapture(captured))
                                {
                                    // The provider reported an earlier capture but could not show its amount yet. The
                                    // reservation completes (the connector is free) and is flagged for review; the
                                    // invoice waits for an operator to confirm the amount.
                                    captureFoundExistingCapture = true;
                                    reservation.FailureCode = CaptureAmountUnverifiedCode;
                                    reservation.LastError = $"{gateway.ProviderName} reported the payment as already captured but could not confirm the captured amount; {capturedAmount} minor units are recorded. Review the payment before invoicing.";
                                    reservation.FailureMessage = reservation.LastError;
                                    _logger.LogError(
                                        "Payments/Complete => Capture amount unverified provider={Provider} reservation={ReservationId} paymentIntent={PaymentIntentId} recordedCents={RecordedCents} requestedCents={RequestedCents}",
                                        gateway.ProviderName,
                                        reservation.ReservationId,
                                        paymentIntent.AuthorizationReference,
                                        capturedAmount,
                                        finalCaptureAmount);
                                }

                                _logger.LogInformation(
                                    "Payments/Complete => Captured payment provider={Provider} reservation={ReservationId} paymentIntent={PaymentIntentId} capturedCents={CapturedCents} requestedCents={RequestedCents} status={PaymentIntentStatus}",
                                    gateway.ProviderName,
                                    reservation.ReservationId,
                                    paymentIntent.AuthorizationReference,
                                    capturedAmount,
                                    finalCaptureAmount,
                                    captured?.RawStatus);
                            }
                        }
                        else
                        {
                            authorizationReleaseExpected = true;
                            var canceled = gateway.Cancel(
                                paymentIntent.AuthorizationReference,
                                SettlementIdempotencyKey(BuildIdempotencyKey(IdempotencyCancel, reservation.ReservationId)));
                            if (ApplySettlementCancelResult(reservation, gateway, canceled, now))
                            {
                                reservation.Status = PaymentReservationStatus.Cancelled;
                                reservation.CapturedAmountCents = 0;
                                reservation.CapturedAtUtc = null;
                                reservation.LastError = $"No capturable amount available on the {gateway.ProviderName} {AuthorizationNoun(gateway)}.";

                                _logger.LogInformation(
                                    "Payments/Complete => Cancelled non-capturable payment authorization reservation={ReservationId} paymentIntent={PaymentIntentId}",
                                    reservation.ReservationId,
                                    paymentIntent.AuthorizationReference);
                            }
                            else
                            {
                                captureFoundExistingCapture = true;
                            }
                        }
                    }
                    else
                    {
                        authorizationReleaseExpected = true;
                        var canceled = gateway.Cancel(
                            paymentIntent.AuthorizationReference,
                            SettlementIdempotencyKey(BuildIdempotencyKey(IdempotencyCancel, reservation.ReservationId)));
                        if (ApplySettlementCancelResult(reservation, gateway, canceled, now))
                        {
                            reservation.Status = PaymentReservationStatus.Cancelled;
                            reservation.CapturedAmountCents = 0;
                            reservation.CapturedAtUtc = null;
                            reservation.LastError = string.IsNullOrWhiteSpace(sessionFeeSuppressionReason)
                                ? "No billable amount to capture."
                                : $"No billable amount to capture ({sessionFeeSuppressionReason}).";

                            _logger.LogInformation(
                                "Payments/Complete => Cancelled zero-amount capture reservation={ReservationId} paymentIntent={PaymentIntentId}",
                                reservation.ReservationId,
                                paymentIntent.AuthorizationReference);
                        }
                        else
                        {
                            captureFoundExistingCapture = true;
                        }
                    }
                }
                else if (paymentIntent.State == PaymentAuthorizationState.Captured)
                {
                    if (recoveryAssessment != null && paymentIntent.AmountReceivedMinor != amountToCapture)
                    {
                        throw new InvalidOperationException(
                            "Provider-reported captured amount contradicts the assessed recovery settlement.");
                    }

                    reservation.CapturedAmountCents = paymentIntent.AmountReceivedMinor;
                    reservation.CapturedAtUtc = now;
                    reservation.Status = PaymentReservationStatus.Completed;

                    _logger.LogInformation(
                        "Payments/Complete => Payment already succeeded reservation={ReservationId} paymentIntent={PaymentIntentId} amountReceived={AmountReceived}",
                        reservation.ReservationId,
                        paymentIntent.AuthorizationReference,
                        paymentIntent.AmountReceivedMinor);
                }
                else if (paymentIntent.State == PaymentAuthorizationState.Canceled &&
                         retryingSettlement &&
                         authorizationReleaseExpected)
                {
                    // An earlier run's no-charge cancel was applied, but its answer was lost: the hold is released.
                    reservation.Status = PaymentReservationStatus.Cancelled;
                    reservation.CapturedAmountCents = 0;
                    reservation.CapturedAtUtc = null;
                    reservation.LastError = $"No billable amount to capture; the {AuthorizationTerm(gateway)} was already released.";
                    MarkAuthorizationReleased(reservation);

                    _logger.LogInformation(
                        "Payments/Complete => Retried settlement found the payment authorization already cancelled reservation={ReservationId} paymentIntent={PaymentIntentId}",
                        reservation.ReservationId,
                        paymentIntent.AuthorizationReference);
                }
                else
                {
                    reservation.Status = PaymentReservationStatus.Failed;
                    reservation.LastError = $"Unexpected {AuthorizationTerm(gateway)} status '{paymentIntent.RawStatus}' during capture.";

                    _logger.LogWarning(
                        "Payments/Complete => Unexpected payment authorization status reservation={ReservationId} paymentIntent={PaymentIntentId} status={PaymentIntentStatus}",
                        reservation.ReservationId,
                        paymentIntent.AuthorizationReference,
                        paymentIntent.RawStatus);
                }

                // This run's provider answer is the outcome. A concurrent run of the same settlement can have
                // recorded a retry marker or a failure (and armed the release) since this run read the row; those
                // fields are written explicitly so the saved outcome replaces them.
                MarkSettlementOutcomeFieldsModified(dbContext, reservation);
                dbContext.SaveChanges();

                PersistTransactionBreakdown(dbContext, transaction, reservation, actualEnergy, energyCostCents, settlement.NightEnergyKwh, settlement.NightEnergyCostCents, usageFeeMinutes, usageFeeCents, sessionFeeCents, amountToCapture);
                if (string.Equals(reservation.Status, PaymentReservationStatus.Completed, StringComparison.OrdinalIgnoreCase) &&
                    amountToCapture > 0 &&
                    !captureFoundExistingCapture)
                {
                    if (!suppressCustomerSideEffects)
                    {
                        TryHandleInvoiceIntegration(dbContext, reservation, transaction);
                        TrySendCompletionNotifications(dbContext, reservation, transaction);
                    }
                }
            }
            catch (PaymentProviderException pex)
            {
                if (pex.IsTransient &&
                    recoveryAssessment == null &&
                    IsWithinSettlementRetryWindow(reservation, now))
                {
                    RecordSettlementRetry(dbContext, reservation, gateway, pex, now, transaction);
                    return;
                }

                RecordCaptureProviderFailure(
                    dbContext,
                    reservation,
                    gateway,
                    pex,
                    authorizationReleaseExpected,
                    now,
                    transaction,
                    settlementRetryExhausted: pex.IsTransient && recoveryAssessment == null);
            }
        }

        /// <summary>
        /// A transient provider failure during settlement (for example a lagging Saferpay Inquire or a Stripe 5xx)
        /// keeps the reservation in its charging status with the retry marker, so the completion is retried (by the
        /// cleanup sweep or the next connector event) instead of ending Failed without capture or release. The retry
        /// reads the authorization first and settles the same amount, so an earlier capture or cancel that went
        /// through is recognised instead of repeated.
        /// </summary>
        private void RecordSettlementRetry(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentProviderException exception,
            DateTime now,
            Transaction transaction)
        {
            var providerErrorCode = exception.Code ?? "provider_error";
            var sanitizedProviderError = SanitizeProviderError(
                $"{providerErrorCode}: {exception.ProviderMessage ?? exception.Message}",
                gateway);
            _logger.LogWarning(
                "{Provider} settlement failed transiently for reservation={ReservationId} status={Status} providerError={ProviderError}; the completion will be retried",
                exception.Provider ?? gateway?.ProviderName,
                reservation.ReservationId,
                reservation.Status ?? "(null)",
                sanitizedProviderError);

            var status = PersistedStatus(dbContext, reservation);
            reservation.FailureCode = SettlementRetryPendingCode;
            reservation.FailureMessage = null;
            reservation.LastError = sanitizedProviderError;
            reservation.UpdatedAtUtc = now;
            if (!TryCommitReservationTransition(dbContext, reservation, status))
            {
                // Another run settled the reservation first; keep its result and drop this run's transaction changes.
                ReloadQuietly(dbContext, transaction);
                _logger.LogInformation(
                    "Payments/Complete => Reservation settled concurrently reservation={ReservationId} status={Status}",
                    reservation.ReservationId,
                    reservation.Status ?? "(null)");
                return;
            }

            // The final idle snapshot belongs to the retried settlement.
            dbContext.SaveChanges();
        }

        /// <summary>
        /// Whole minutes since the settlement started (the base of the retry window), used in the idempotency keys of
        /// a retried settlement. Runs within the same minute share a key, so concurrent retries stay deduplicated at
        /// the provider; a later retry gets a new key instead of a stored answer to an earlier one.
        /// </summary>
        private static long SettlementRetryOrdinal(ChargePaymentReservation reservation, DateTime now)
        {
            var settlementStartedAtUtc = reservation.DisconnectedAtUtc ?? reservation.StopTransactionAtUtc ?? now;
            return Math.Max(0L, (long)Math.Floor((now - settlementStartedAtUtc).TotalMinutes));
        }

        /// <summary>
        /// Marks the outcome fields of a settlement as modified, so a plain save writes this run's values over a
        /// retry marker, failure or release arming that a concurrent run of the same settlement saved meanwhile.
        /// </summary>
        private static void MarkSettlementOutcomeFieldsModified(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            var entry = dbContext.Entry(reservation);
            if (entry.State != EntityState.Unchanged && entry.State != EntityState.Modified)
            {
                return;
            }

            entry.Property(row => row.FailureCode).IsModified = true;
            entry.Property(row => row.FailureMessage).IsModified = true;
            entry.Property(row => row.LastError).IsModified = true;
            entry.Property(row => row.AuthorizationReleaseState).IsModified = true;
            entry.Property(row => row.AuthorizationReleaseNextAttemptAtUtc).IsModified = true;
            entry.Property(row => row.AuthorizationReleaseLastError).IsModified = true;
            entry.Property(row => row.AuthorizationReleaseAttemptCount).IsModified = true;
        }

        /// <summary>
        /// Transient settlement failures are retried while the settlement started at most
        /// <c>Maintenance:SettlementRetryWindowMinutes</c> ago (default 60, 0 disables the retry).
        /// </summary>
        private bool IsWithinSettlementRetryWindow(ChargePaymentReservation reservation, DateTime now)
        {
            var configuredMinutes = _configuration?.GetValue<int?>("Maintenance:SettlementRetryWindowMinutes") ??
                                    DefaultSettlementRetryWindowMinutes;
            var windowMinutes = Math.Clamp(configuredMinutes, 0, 24 * 60);
            if (windowMinutes == 0)
            {
                return false;
            }

            var settlementStartedAtUtc = reservation.DisconnectedAtUtc ?? reservation.StopTransactionAtUtc ?? now;
            return now - settlementStartedAtUtc <= TimeSpan.FromMinutes(windowMinutes);
        }

        private static bool IsUnverifiedCapture(PaymentAuthorizationSnapshot captured) =>
            captured?.State == PaymentAuthorizationState.Captured &&
            string.Equals(captured.RawStatus, Providers.Saferpay.SaferpayPaymentGateway.UnverifiedCaptureStatus, StringComparison.OrdinalIgnoreCase);

        private void RecordCaptureProviderFailure(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentProviderException exception,
            bool authorizationReleaseExpected,
            DateTime now,
            Transaction transaction = null,
            bool settlementRetryExhausted = false)
        {
            var providerErrorCode = exception.Code ?? "provider_error";
            var sanitizedProviderErrorCode = Truncate(SanitizeProviderError(providerErrorCode, gateway), 100);
            var sanitizedProviderError = SanitizeProviderError(
                $"{providerErrorCode}: {exception.ProviderMessage ?? exception.Message}",
                gateway);
            _logger.LogError(
                "{Provider} capture failed for reservation={ReservationId} providerCode={ProviderCode} providerError={ProviderError}",
                exception.Provider ?? gateway?.ProviderName,
                reservation.ReservationId,
                sanitizedProviderErrorCode,
                sanitizedProviderError);
            var status = PersistedStatus(dbContext, reservation);
            reservation.Status = PaymentReservationStatus.Failed;
            if (settlementRetryExhausted)
            {
                // Transient failures outlasted the settlement retry window (or retries are disabled): an operator
                // settles the reservation (recover-settlement) or releases the hold.
                reservation.FailureCode = SettlementRetryExhaustedCode;
                reservation.FailureMessage = null;
            }
            else if (string.Equals(reservation.FailureCode, SettlementRetryPendingCode, StringComparison.Ordinal))
            {
                reservation.FailureCode = null;
                reservation.FailureMessage = null;
            }

            if (authorizationReleaseExpected)
            {
                reservation.LastError = "Payment authorization release could not be completed automatically.";
                ArmAuthorizationReleaseState(reservation);
                reservation.AuthorizationReleaseLastError = sanitizedProviderError;
            }
            else
            {
                reservation.LastError = sanitizedProviderError;
            }
            reservation.UpdatedAtUtc = now;
            if (!TryCommitReservationTransition(dbContext, reservation, status))
            {
                // Another run settled the reservation first; keep its result.
                ReloadQuietly(dbContext, transaction);
                _logger.LogInformation(
                    "Payments/Complete => Reservation settled concurrently reservation={ReservationId} status={Status}",
                    reservation.ReservationId,
                    reservation.Status ?? "(null)");
                return;
            }

            // Persist the transaction's final idle snapshot with the failure.
            dbContext.SaveChanges();
        }

        public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc)
        {
            if (!IsOperational || dbContext == null || string.IsNullOrWhiteSpace(chargePointId) || connectorId <= 0)
            {
                return;
            }

            var reservation = dbContext.ChargePaymentReservations
                .Where(r =>
                    r.ChargePointId == chargePointId &&
                    r.ConnectorId == connectorId &&
                    r.Status == PaymentReservationStatus.WaitingForDisconnect)
                .OrderByDescending(r => r.UpdatedAtUtc)
                .FirstOrDefault();

            if (reservation == null)
            {
                return;
            }

            reservation.DisconnectedAtUtc ??= disconnectedAtUtc;
            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();

            if (!reservation.TransactionId.HasValue)
            {
                _logger.LogWarning(
                    "Payments/ConnectorAvailable => Waiting reservation has no transaction reservation={ReservationId} cp={ChargePointId} connector={ConnectorId}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    reservation.ConnectorId);
                return;
            }

            var transaction = dbContext.Transactions.Find(reservation.TransactionId.Value);
            if (transaction == null)
            {
                _logger.LogWarning(
                    "Payments/ConnectorAvailable => Transaction not found reservation={ReservationId} tx={TransactionId}",
                    reservation.ReservationId,
                    reservation.TransactionId.Value);
                return;
            }

            CompleteReservation(dbContext, transaction);
        }

        private void TrySendR1RequestedNotification(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (_emailNotificationService == null || reservation == null)
            {
                return;
            }

            var session = ResolveCheckoutDetails(reservation, "R1Requested");
            if (!IsR1Requested(reservation, session))
            {
                return;
            }

            bool buyerConfirmed = reservation.InvoiceBuyerConfirmedAtUtc.HasValue;
            var recipientEmail = !string.IsNullOrWhiteSpace(reservation.InvoiceBuyerEmail)
                ? reservation.InvoiceBuyerEmail
                : session?.CustomerEmail;
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogDebug("Payments/Notify => R1 requested email skipped due to missing recipient reservation={ReservationId}", reservation.ReservationId);
                return;
            }

            try
            {
                var chargePoint = dbContext.ChargePoints.Find(reservation.ChargePointId);
                var statusUrl = BuildStatusUrl(reservation.ReservationId);
                _emailNotificationService.SendR1InvoiceRequested(
                    recipientEmail,
                    reservation,
                    chargePoint,
                    statusUrl,
                    buyerConfirmed ? reservation.InvoiceBuyerCompanyName : session?.GetMetadataValue("buyer_company"),
                    buyerConfirmed ? reservation.InvoiceBuyerTaxIdentifier : session?.GetMetadataValue("buyer_oib"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Payments/Notify => Failed to send R1 requested email reservation={ReservationId}", reservation.ReservationId);
            }
        }

        private void TrySendCompletionNotifications(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction)
        {
            if (_emailNotificationService == null || reservation == null || transaction == null)
            {
                return;
            }

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Completed, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(reservation.Status, PaymentReservationStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var session = ResolveCheckoutDetails(reservation, "Completion");
            var recipientEmail = session?.CustomerEmail;
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogDebug("Payments/Notify => Completion emails skipped due to missing recipient reservation={ReservationId}", reservation.ReservationId);
                return;
            }

            var chargePoint = dbContext.ChargePoints.Find(reservation.ChargePointId);
            var statusUrl = BuildStatusUrl(reservation.ReservationId);
            var invoiceLog = InvoiceSubmissionLogLookup.TryGetLatestDeliverable(
                dbContext,
                reservation.ReservationId,
                _logger,
                "CompletionNotifications");
            var invoiceNumber = invoiceLog?.ExternalInvoiceNumber;
            var invoiceUrl = InvoiceSubmissionLogLookup.GetPreferredDocumentUrl(invoiceLog);

            try
            {
                _emailNotificationService.SendChargingCompleted(recipientEmail, reservation, transaction, chargePoint, statusUrl);
                _emailNotificationService.SendSessionReceipt(
                    recipientEmail,
                    reservation,
                    transaction,
                    chargePoint,
                    statusUrl,
                    invoiceNumber,
                    invoiceUrl);

                bool r1Issued = !string.IsNullOrWhiteSpace(invoiceLog?.InvoiceKind)
                    ? string.Equals(invoiceLog.InvoiceKind, "R1", StringComparison.OrdinalIgnoreCase)
                    : reservation.InvoiceBuyerConfirmedAtUtc.HasValue ||
                        (IsR1InvoiceRequested(session) && !reservation.InvoiceR1RequestedAtUtc.HasValue);
                if (r1Issued)
                {
                    _emailNotificationService.SendR1InvoiceReady(
                        recipientEmail,
                        reservation,
                        transaction,
                        chargePoint,
                        statusUrl,
                        invoiceNumber,
                        invoiceUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Payments/Notify => Failed sending completion emails reservation={ReservationId}", reservation.ReservationId);
            }
        }

        private void TryHandleInvoiceIntegration(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction)
        {
            if (_invoiceIntegrationService == null || reservation == null || transaction == null)
            {
                return;
            }

            try
            {
                var checkoutDetails = ResolveCheckoutDetails(reservation, "InvoiceIntegration");
                _invoiceIntegrationService.HandleCompletedReservation(dbContext, reservation, transaction, checkoutDetails);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invoice/Integration => Failed to prepare invoice draft reservation={ReservationId}", reservation.ReservationId);
            }
        }

        /// <summary>
        /// Checkout details for e-mails and invoices. Gateways with deferred lookup (Stripe) are queried
        /// as before (errors swallowed); otherwise, or when the lookup fails, the payer details persisted
        /// on the reservation are used.
        /// </summary>
        public PaymentCheckoutDetails GetCheckoutDetails(ChargePaymentReservation reservation) =>
            ResolveCheckoutDetails(reservation, "Lookup");

        private PaymentCheckoutDetails ResolveCheckoutDetails(ChargePaymentReservation reservation, string reason)
        {
            if (reservation == null)
            {
                return null;
            }

            var gateway = ResolveGateway(reservation);
            var checkoutSessionId = reservation.StripeCheckoutSessionId;
            if (IsGatewayUsable(gateway) &&
                gateway.SupportsDeferredCheckoutLookup &&
                !string.IsNullOrWhiteSpace(checkoutSessionId))
            {
                try
                {
                    var checkout = gateway.GetCheckout(checkoutSessionId);
                    if (checkout != null)
                    {
                        var details = ToCheckoutDetails(gateway, reservation, checkout);
                        details.CustomerEmail = FirstNonEmpty(details.CustomerEmail, reservation.PayerEmail, reservation.InvoiceBuyerEmail);
                        details.CustomerName ??= reservation.PayerName;
                        return details;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Payments/Notify => Unable to load checkout session reservation={ReservationId} sessionId={SessionId} reason={Reason}",
                        reservation.ReservationId,
                        checkoutSessionId,
                        reason);
                }
            }

            return new PaymentCheckoutDetails
            {
                Provider = gateway?.ProviderName ?? reservation.PaymentProvider,
                SessionReference = reservation.StripeCheckoutSessionId,
                AuthorizationReference = reservation.StripePaymentIntentId,
                // The invoice buyer e-mail is the payer's own address when the provider reported none.
                CustomerEmail = FirstNonEmpty(reservation.PayerEmail, reservation.InvoiceBuyerEmail),
                CustomerName = reservation.PayerName,
                Metadata = EmptyMetadata
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static PaymentCheckoutDetails ToCheckoutDetails(
            IPaymentGateway gateway,
            ChargePaymentReservation reservation,
            PaymentCheckoutStatus checkout)
        {
            return new PaymentCheckoutDetails
            {
                Provider = gateway?.ProviderName ?? reservation?.PaymentProvider,
                SessionReference = checkout?.SessionReference,
                AuthorizationReference = checkout?.AuthorizationReference ?? reservation?.StripePaymentIntentId,
                CustomerEmail = checkout?.CustomerEmail,
                CustomerName = checkout?.CustomerName,
                Metadata = gateway != null && gateway.SupportsMetadata
                    ? checkout?.Metadata ?? EmptyMetadata
                    : EmptyMetadata
            };
        }

        private static bool IsR1InvoiceRequested(PaymentCheckoutDetails session) =>
            string.Equals(session?.GetMetadataValue("invoice_type"), "R1", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// R1 request detection. Stripe mirrors the request into checkout metadata; providers without
        /// metadata rely on the database state (R1 requested at checkout, or a confirmed buyer snapshot).
        /// </summary>
        private bool IsR1Requested(ChargePaymentReservation reservation, PaymentCheckoutDetails details)
        {
            if (reservation.InvoiceR1RequestedAtUtc.HasValue || IsR1InvoiceRequested(details))
            {
                return true;
            }

            var gateway = ResolveGateway(reservation);
            return (gateway == null || !gateway.SupportsMetadata) &&
                   reservation.InvoiceBuyerConfirmedAtUtc.HasValue;
        }

        private static bool IsR1InvoiceRequested(IReadOnlyDictionary<string, string> metadata) =>
            IsR1InvoiceRequested(new PaymentCheckoutDetails { Metadata = metadata });

        private string BuildStatusUrl(Guid reservationId)
        {
            if (reservationId == Guid.Empty || string.IsNullOrWhiteSpace(_settings.ReturnBaseUrl))
            {
                return null;
            }

            return $"{TrimTrailingSlash(_settings.ReturnBaseUrl)}/Payments/Status?reservationId={reservationId}&origin=public";
        }

        private int CalculateUsageFeeMinutes(Transaction transaction, ChargePaymentReservation reservation, PaymentFlowOptions flowOptions, DateTime? nowUtc = null)
        {
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (reservation.UsageFeePerMinute <= 0 || transaction.StartTime == default)
            {
                return 0;
            }

            bool usageAfterChargingEnds = reservation.UsageFeeAnchorMinutes == 1;
            if (usageAfterChargingEnds)
            {
                return IdleFeeCalculator.CalculateSnapshot(
                    transaction,
                    reservation,
                    flowOptions,
                    nowUtc ?? DateTime.UtcNow,
                    _logger).TotalMinutes;
            }

            var stopTimeUtc = transaction.StopTime ?? nowUtc ?? DateTime.UtcNow;
            DateTime? anchorStartUtc = transaction.StartTime;
            if (!anchorStartUtc.HasValue || stopTimeUtc <= anchorStartUtc.Value)
            {
                return 0;
            }

            int billableMinutes = IdleFeeCalculator.CalculateBillableUsageMinutes(
                anchorStartUtc.Value,
                stopTimeUtc,
                reservation.StartUsageFeeAfterMinutes,
                flowOptions,
                _logger);

            return Math.Min(
                Math.Max(0, billableMinutes),
                Math.Max(0, reservation.MaxUsageFeeMinutes));
        }

        private PaymentFlowOptions ResolveFlowOptions(OCPPCoreContext dbContext)
        {
            return PaymentFlowOptionsResolver.Resolve(_configuration, dbContext, _flowOptions);
        }

        private static bool IsDisconnectedStopReason(string stopReason)
        {
            if (string.IsNullOrWhiteSpace(stopReason))
            {
                return false;
            }

            return stopReason.IndexOf("EVDisconnected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   stopReason.IndexOf("EVDeparted", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ConnectorStatus LoadConnectorStatusFresh(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (dbContext == null || reservation == null)
            {
                return null;
            }

            return dbContext.ConnectorStatuses
                .AsNoTracking()
                .FirstOrDefault(cs =>
                    cs.ChargePointId == reservation.ChargePointId &&
                    cs.ConnectorId == reservation.ConnectorId);
        }

        private static DateTime? ResolveDisconnectedAtUtc(ChargePaymentReservation reservation, ConnectorStatus connectorStatus)
        {
            if (reservation == null || connectorStatus == null)
            {
                return null;
            }

            if (connectorStatus.LastStatusTime.HasValue &&
                string.Equals(connectorStatus.LastStatus, "Available", StringComparison.OrdinalIgnoreCase))
            {
                DateTime referenceUtc = reservation.StopTransactionAtUtc ?? reservation.StartTransactionAtUtc ?? reservation.CreatedAtUtc;
                if (reservation.StopTransactionAtUtc.HasValue)
                {
                    referenceUtc = reservation.StopTransactionAtUtc.Value.Subtract(ConnectorAvailableBeforeStopGrace);
                }

                if (connectorStatus.LastStatusTime.Value >= referenceUtc)
                {
                    return connectorStatus.LastStatusTime.Value;
                }
            }

            return null;
        }

        private static DateTime? ResolveDisconnectedAtUtc(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            return ResolveDisconnectedAtUtc(reservation, LoadConnectorStatusFresh(dbContext, reservation));
        }

        private static bool TryGetConnectorAvailability(OCPPCoreContext dbContext, ChargePaymentReservation reservation, out bool isAvailable)
        {
            isAvailable = false;
            if (dbContext == null || reservation == null)
            {
                return false;
            }

            var connectorStatus = LoadConnectorStatusFresh(dbContext, reservation);
            if (connectorStatus == null)
            {
                return false;
            }

            isAvailable = ResolveDisconnectedAtUtc(reservation, connectorStatus).HasValue;
            return true;
        }

        private static bool TryParseDailyWindow(string window, out TimeSpan start, out TimeSpan end)
        {
            start = default;
            end = default;
            if (string.IsNullOrWhiteSpace(window)) return false;

            var parts = window.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return false;
            return TimeSpan.TryParse(parts[0], out start) && TimeSpan.TryParse(parts[1], out end);
        }

        private static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo tz)
        {
            tz = null;
            if (string.IsNullOrWhiteSpace(timeZoneId))
            {
                tz = TimeZoneInfo.Local;
                return tz != null;
            }

            // Cross-platform convenience: allow either IANA or Windows ids for Europe/Zagreb.
            var candidates = new List<string> { timeZoneId };
            if (string.Equals(timeZoneId, "Europe/Zagreb", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Central European Standard Time");
            }
            else if (string.Equals(timeZoneId, "Central European Standard Time", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Europe/Zagreb");
            }

            foreach (var id in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                    if (tz != null) return true;
                }
                catch
                {
                    // try next
                }
            }

            tz = TimeZoneInfo.Local;
            return tz != null;
        }

        private int CalculateChargeableMinutesExcludingWindow(DateTime anchorStartUtc, DateTime stopUtc, TimeZoneInfo tz, TimeSpan excludedStartLocal, TimeSpan excludedEndLocal)
        {
            if (stopUtc <= anchorStartUtc) return 0;

            var startUtc = DateTime.SpecifyKind(anchorStartUtc, DateTimeKind.Utc);
            var endUtc = DateTime.SpecifyKind(stopUtc, DateTimeKind.Utc);

            DateTime startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
            DateTime endLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, tz);

            // Iterate days (include the previous day to catch windows crossing midnight).
            DateTime day = startLocal.Date.AddDays(-1);
            DateTime lastDay = endLocal.Date;

            double excludedSeconds = 0;
            while (day <= lastDay)
            {
                DateTime exStartLocal;
                DateTime exEndLocal;
                if (excludedStartLocal <= excludedEndLocal)
                {
                    // Same-day window.
                    exStartLocal = day.Add(excludedStartLocal);
                    exEndLocal = day.Add(excludedEndLocal);
                }
                else
                {
                    // Cross-midnight window: [day+start, day+1+end)
                    exStartLocal = day.Add(excludedStartLocal);
                    exEndLocal = day.AddDays(1).Add(excludedEndLocal);
                }

                // Compute overlap in UTC to respect DST shifts.
                try
                {
                    var exStartUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(exStartLocal, DateTimeKind.Unspecified), tz);
                    var exEndUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(exEndLocal, DateTimeKind.Unspecified), tz);

                    var overlapStart = exStartUtc > startUtc ? exStartUtc : startUtc;
                    var overlapEnd = exEndUtc < endUtc ? exEndUtc : endUtc;
                    if (overlapEnd > overlapStart)
                    {
                        excludedSeconds += (overlapEnd - overlapStart).TotalSeconds;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Idle fee exclusion window UTC conversion failed tz={TimeZoneId} day={Day:yyyy-MM-dd}", tz.Id, day);
                }

                day = day.AddDays(1);
            }

            var totalSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            var chargeableSeconds = Math.Max(0, totalSeconds - excludedSeconds);
            return (int)Math.Ceiling(chargeableSeconds / 60.0);
        }

        /// <summary>Legacy Stripe webhook entry point (<c>/API/Payments/Webhook</c>).</summary>
        public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader)
        {
            HandleWebhook(
                dbContext,
                new PaymentWebhookRequest
                {
                    Provider = null,
                    Method = "POST",
                    Body = payload,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Stripe-Signature"] = signatureHeader
                    }
                },
                CancellationToken.None);
        }

        public Task<PaymentWebhookResult> HandleWebhookAsync(
            OCPPCoreContext dbContext,
            PaymentWebhookRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(HandleWebhook(dbContext, request, cancellationToken));

        private PaymentWebhookResult HandleWebhook(
            OCPPCoreContext dbContext,
            PaymentWebhookRequest request,
            CancellationToken cancellationToken)
        {
            var result = new PaymentWebhookResult();

            // Null or empty provider is the legacy Stripe route; an unknown provider has no endpoint.
            var gateway = _gateways.Resolve(request?.Provider);
            if (gateway == null)
            {
                _logger.LogWarning("Payments/Webhook => No payment gateway registered for provider={Provider}", Truncate(request?.Provider, 32) ?? "(none)");
                result.StatusCode = 404;
                return result;
            }

            result.Provider = gateway.ProviderName;
            if (!IsOperational || !gateway.IsConfigured)
            {
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            cancellationToken.ThrowIfCancellationRequested();

            PaymentWebhookParseResult parse;
            try
            {
                parse = gateway.ParseWebhook(request ?? new PaymentWebhookRequest());
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogWarning(pex, "Payments/Webhook => {Provider} notification verification failed code={Code} transient={Transient}", gateway.ProviderName, pex.Code, pex.IsTransient);
                if (pex.IsTransient)
                {
                    result.StatusCode = 503;
                }
                return result;
            }

            if (parse == null || !parse.Accepted)
            {
                if (parse != null)
                {
                    result.StatusCode = parse.StatusCode;
                    _logger.LogInformation("Payments/Webhook => {Provider} notification rejected reason={Reason} status={StatusCode}", gateway.ProviderName, parse.RejectReason ?? "(none)", parse.StatusCode);
                }
                return result;
            }

            var authorizedReservationIds = new List<Guid>();
            foreach (var webhookEvent in parse.Events ?? Array.Empty<PaymentWebhookEvent>())
            {
                if (webhookEvent == null)
                {
                    continue;
                }

                Guid? linkedReservationId;
                try
                {
                    linkedReservationId = ProcessWebhookEvent(dbContext, gateway, webhookEvent);
                }
                catch (PaymentProviderException pex) when (pex.IsTransient)
                {
                    // Not marked as processed: the PSP retries and the event is evaluated again.
                    _logger.LogWarning(pex, "Payments/Webhook => Transient {Provider} failure eventId={EventId} type={Type}; asking the provider to retry", gateway.ProviderName, webhookEvent.EventId, webhookEvent.EventType);
                    result.StatusCode = 503;
                    break;
                }
                catch (PaymentProviderException pex) when (webhookEvent.Kind == PaymentWebhookEventKind.CheckoutNotification)
                {
                    // A permanent provider answer will not change on a retry: acknowledge it.
                    _logger.LogWarning(pex, "Payments/Webhook => {Provider} notification could not be processed eventId={EventId} reservation={ReservationId} code={Code}", gateway.ProviderName, webhookEvent.EventId, webhookEvent.ReservationId, pex.Code);
                    continue;
                }
                catch (Exception ex) when (webhookEvent.Kind == PaymentWebhookEventKind.CheckoutNotification)
                {
                    // Our own failure (for example the database): drop the half-applied changes and ask the provider
                    // to deliver the notification again; processing re-reads the checkout and is idempotent.
                    _logger.LogError(ex, "Payments/Webhook => {Provider} notification processing failed eventId={EventId} reservation={ReservationId}; asking the provider to retry", gateway.ProviderName, webhookEvent.EventId, webhookEvent.ReservationId);
                    dbContext.ChangeTracker.Clear();
                    result.StatusCode = 500;
                    authorizedReservationIds.Clear();
                    break;
                }

                if (linkedReservationId.HasValue &&
                    (webhookEvent.Kind == PaymentWebhookEventKind.CheckoutCompleted ||
                     webhookEvent.Kind == PaymentWebhookEventKind.CheckoutNotification))
                {
                    var reservation = dbContext.ChargePaymentReservations.Find(linkedReservationId.Value);
                    if (reservation != null &&
                        string.Equals(reservation.Status, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase) &&
                        !authorizedReservationIds.Contains(reservation.ReservationId))
                    {
                        authorizedReservationIds.Add(reservation.ReservationId);
                    }
                }
            }

            result.AuthorizedReservationIds = authorizedReservationIds;
            return result;
        }

        private Guid? ProcessWebhookEvent(OCPPCoreContext dbContext, IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            var eventKey = WebhookEventKey(gateway, webhookEvent);
            if (HasProcessedWebhookEvent(dbContext, eventKey))
            {
                _logger.LogInformation("Payments/Webhook => Duplicate event ignored provider={Provider} eventId={EventId} type={Type}", gateway.ProviderName, webhookEvent.EventId, webhookEvent.EventType);
                return null;
            }

            _logger.LogInformation("Payments/Webhook => Processing provider={Provider} eventId={EventId} type={Type}", gateway.ProviderName, webhookEvent.EventId, webhookEvent.EventType);

            Guid? linkedReservationId = null;
            switch (webhookEvent.Kind)
            {
                case PaymentWebhookEventKind.CheckoutCompleted:
                    linkedReservationId = HandleCheckoutCompleted(dbContext, gateway, webhookEvent);
                    break;
                case PaymentWebhookEventKind.CheckoutExpired:
                    linkedReservationId = HandleCheckoutExpired(dbContext, gateway, webhookEvent);
                    break;
                case PaymentWebhookEventKind.AuthorizationFailed:
                    linkedReservationId = HandlePaymentFailed(dbContext, gateway, webhookEvent);
                    break;
                case PaymentWebhookEventKind.AuthorizationCapturable:
                    linkedReservationId = HandleAmountCapturableUpdated(dbContext, gateway, webhookEvent);
                    break;
                case PaymentWebhookEventKind.CheckoutNotification:
                    linkedReservationId = HandleCheckoutNotification(dbContext, gateway, webhookEvent);
                    break;
                default:
                    _logger.LogDebug("Unhandled {Provider} event type: {Type}", gateway.ProviderName, webhookEvent.EventType);
                    break;
            }

            MarkWebhookEventProcessed(dbContext, eventKey, webhookEvent, linkedReservationId);
            return linkedReservationId;
        }

        /// <summary>
        /// Duplicate-suppression key in the (historically Stripe-named) processed-webhook table. Stripe event
        /// ids are stored unchanged; other providers are prefixed so their ids can never collide with Stripe's.
        /// </summary>
        private static string WebhookEventKey(IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            if (string.IsNullOrWhiteSpace(webhookEvent?.EventId))
            {
                return null;
            }

            var key = IsStripe(gateway)
                ? webhookEvent.EventId
                : $"{gateway.ProviderName}:{webhookEvent.EventId}";
            return key.Length <= 200 ? key : key.Substring(0, 200);
        }

        private static bool IsActiveReservationConflict(DbUpdateException dbUpdateEx)
        {
            if (dbUpdateEx == null) return false;

            var message = dbUpdateEx.InnerException?.Message ?? dbUpdateEx.Message;
            if (string.IsNullOrWhiteSpace(message)) return false;

            // SQL Server includes the unique index name; SQLite reports the column list.
            return message.Contains("UX_PaymentReservations_ActiveConnector", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("ChargePaymentReservation.ChargePointId", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("ChargePaymentReservations.ChargePointId", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
        }

        private Guid? HandleCheckoutCompleted(OCPPCoreContext dbContext, IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            var session = webhookEvent.Checkout;
            if (session == null) return null;

            _logger.LogInformation("Payments/Webhook => Checkout session completed provider={Provider} eventId={EventId} sessionId={SessionId} paid={Paid}", gateway.ProviderName, webhookEvent.EventId, session.SessionReference, session.IsPaid);

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripeCheckoutSessionId == session.SessionReference);

            if (reservation == null)
            {
                _logger.LogWarning("Payments/Webhook => Checkout completed but reservation not found sessionId={SessionId}", session.SessionReference);
                return null;
            }

            if (!IsOwnedBy(reservation, gateway))
            {
                _logger.LogWarning("Payments/Webhook => Checkout completed for a reservation owned by another provider reservation={ReservationId} provider={Provider}", reservation.ReservationId, reservation.PaymentProvider);
                return null;
            }

            reservation.StripePaymentIntentId = reservation.StripePaymentIntentId ?? session.AuthorizationReference;
            PersistPayerDetails(reservation, session);
            reservation.UpdatedAtUtc = _utcNow();
            if (reservation.Status == PaymentReservationStatus.Pending &&
                session.IsPaid)
            {
                if (!MarkAuthorizedFromProviderSignal(dbContext, reservation))
                {
                    // The browser return authorized it, or a cancellation or the sweep ended it, first. Link the
                    // authorization to an ended reservation so it can be released below.
                    _logger.LogInformation("Payments/Webhook => Reservation changed while the checkout completed reservation={ReservationId} status={Status}", reservation.ReservationId, reservation.Status ?? "(null)");
                    if (dbContext.Entry(reservation).State == EntityState.Detached)
                    {
                        return null;
                    }

                    if (IsTerminalNoChargeStatus(reservation.Status))
                    {
                        reservation.StripePaymentIntentId = reservation.StripePaymentIntentId ?? session.AuthorizationReference;
                        PersistPayerDetails(reservation, session);
                        reservation.UpdatedAtUtc = _utcNow();
                        dbContext.SaveChanges();
                    }
                }
            }
            else
            {
                dbContext.SaveChanges();
            }

            var armed = IsArmedTerminalAuthorizationRelease(reservation);
            if (armed && HasAuthorizationReleaseBudget(reservation))
            {
                ReconcileTerminalPaymentAuthorization(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook);
            }
            else if (IsTerminalNoChargeCandidate(reservation) &&
                     !string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) &&
                     string.Equals(reservation.StripePaymentIntentId, session.AuthorizationReference, StringComparison.Ordinal))
            {
                // The payer completed the checkout after the reservation ended (e.g. cancelled while the checkout
                // was still open), or an armed release has used up its retries. Nothing will capture this
                // authorization: release it on this fresh provider signal.
                var authorization = LookupLateAuthorization(dbContext, gateway, reservation);
                if (!TryReleaseLateAuthorization(dbContext, gateway, reservation, authorization, "Webhook") && armed)
                {
                    ReconcileTerminalPaymentAuthorization(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook);
                }
            }
            else if (armed)
            {
                ReconcileTerminalPaymentAuthorization(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook);
            }

            return reservation.ReservationId;
        }

        /// <summary>
        /// Moves a pending reservation to Authorized on a provider signal. Returns false when another request
        /// authorized or ended it first; the entity then holds the current row.
        /// </summary>
        private bool MarkAuthorizedFromProviderSignal(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            var now = _utcNow();
            reservation.Status = PaymentReservationStatus.Authorized;
            reservation.AuthorizedAtUtc = now;
            reservation.StartDeadlineAtUtc = now.AddMinutes(Math.Max(1, _flowOptions.StartWindowMinutes));
            ClearFailureState(reservation);
            reservation.UpdatedAtUtc = now;
            return TryCommitAuthorization(dbContext, reservation);
        }

        private Guid? HandleAmountCapturableUpdated(OCPPCoreContext dbContext, IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            var paymentIntent = webhookEvent.Authorization;
            if (paymentIntent == null || string.IsNullOrWhiteSpace(paymentIntent.AuthorizationReference)) return null;

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(candidate => candidate.StripePaymentIntentId == paymentIntent.AuthorizationReference);

            if (reservation == null &&
                paymentIntent.OwnerReservationId.HasValue)
            {
                reservation = dbContext.ChargePaymentReservations.Find(paymentIntent.OwnerReservationId.Value);
            }

            if (reservation == null)
            {
                _logger.LogWarning(
                    "Payments/Webhook => Capturable payment authorization reservation not found paymentIntent={PaymentIntentId}",
                    paymentIntent.AuthorizationReference);
                return null;
            }

            if (!IsOwnedBy(reservation, gateway))
            {
                _logger.LogWarning("Payments/Webhook => Capturable payment authorization for a reservation owned by another provider reservation={ReservationId} provider={Provider}", reservation.ReservationId, reservation.PaymentProvider);
                return null;
            }

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                reservation.StripePaymentIntentId = paymentIntent.AuthorizationReference;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
            }
            else if (!string.Equals(reservation.StripePaymentIntentId, paymentIntent.AuthorizationReference, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Payments/Webhook => Capturable payment authorization does not match reservation={ReservationId}",
                    reservation.ReservationId);
                return reservation.ReservationId;
            }

            var armed = IsArmedTerminalAuthorizationRelease(reservation);
            if (armed && HasAuthorizationReleaseBudget(reservation))
            {
                ReconcileTerminalPaymentAuthorization(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseTrigger.AmountCapturableWebhook);
            }
            else if (IsTerminalNoChargeCandidate(reservation))
            {
                if (!TryReleaseLateAuthorization(dbContext, gateway, reservation, paymentIntent, "Webhook") && armed)
                {
                    ReconcileTerminalPaymentAuthorization(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseTrigger.AmountCapturableWebhook);
                }
            }
            else if (armed)
            {
                ReconcileTerminalPaymentAuthorization(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseTrigger.AmountCapturableWebhook);
            }

            return reservation.ReservationId;
        }

        private static bool IsArmedTerminalAuthorizationRelease(ChargePaymentReservation reservation) =>
            reservation != null &&
            PaymentAuthorizationReleaseState.IsArmed(reservation.AuthorizationReleaseState) &&
            IsTerminalNoChargeStatus(reservation.Status);

        private Guid? HandleCheckoutExpired(OCPPCoreContext dbContext, IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            var session = webhookEvent.Checkout;
            if (session == null) return null;

            _logger.LogInformation("Payments/Webhook => Checkout session expired provider={Provider} eventId={EventId} sessionId={SessionId}", gateway.ProviderName, webhookEvent.EventId, session.SessionReference);

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripeCheckoutSessionId == session.SessionReference);

            if (reservation == null)
            {
                _logger.LogWarning("Payments/Webhook => Checkout expired but reservation not found sessionId={SessionId}", session.SessionReference);
                return null;
            }
            if (!IsOwnedBy(reservation, gateway))
            {
                _logger.LogWarning("Payments/Webhook => Checkout expired for a reservation owned by another provider reservation={ReservationId} provider={Provider}", reservation.ReservationId, reservation.PaymentProvider);
                return null;
            }

            if (IsExpiredCheckoutOfOpenRelease(reservation, session))
            {
                // The checkout expired unpaid, so no funds are held for this ended reservation.
                _logger.LogInformation("Payments/Webhook => Expired checkout finishes the authorization release reservation={ReservationId} releaseState={ReleaseState}", reservation.ReservationId, reservation.AuthorizationReleaseState ?? "(null)");
                FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.Released,
                    PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                    session.RawStatus ?? "expired",
                    null);
                return reservation.ReservationId;
            }

            if (reservation.Status != PaymentReservationStatus.Pending &&
                reservation.Status != PaymentReservationStatus.Authorized)
            {
                return reservation.ReservationId;
            }

            var loadedStatus = reservation.Status;
            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.LastError = "Checkout session expired";
            reservation.FailureCode = "CheckoutExpired";
            reservation.FailureMessage = reservation.LastError;
            reservation.UpdatedAtUtc = _utcNow();
            if (!TryCommitReservationTransition(dbContext, reservation, loadedStatus))
            {
                _logger.LogInformation("Payments/Webhook => Reservation changed before the checkout expiry was applied reservation={ReservationId} status={Status}", reservation.ReservationId, reservation.Status ?? "(null)");
            }

            return reservation.ReservationId;
        }

        /// <summary>
        /// An ended reservation whose release is still armed or failed, without a linked authorization, and whose
        /// checkout expired: an expired checkout was never paid, so the release is finished.
        /// </summary>
        private bool IsExpiredCheckoutOfOpenRelease(ChargePaymentReservation reservation, PaymentCheckoutStatus session) =>
            IsTerminalNoChargeCandidate(reservation) &&
            string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) &&
            string.IsNullOrWhiteSpace(AuthorizationReferenceOf(session)) &&
            !session.IsPaid &&
            !IsFreshInProgressRelease(reservation) &&
            (PaymentAuthorizationReleaseState.IsArmed(reservation.AuthorizationReleaseState) ||
             string.Equals(reservation.AuthorizationReleaseState, PaymentAuthorizationReleaseState.PermanentFailure, StringComparison.OrdinalIgnoreCase));

        private Guid? HandlePaymentFailed(OCPPCoreContext dbContext, IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            var paymentIntent = webhookEvent.Authorization;
            if (paymentIntent == null) return null;

            _logger.LogInformation("Payments/Webhook => Payment failed provider={Provider} eventId={EventId} paymentIntent={PaymentIntentId} message={ErrorMessage}",
                gateway.ProviderName,
                webhookEvent.EventId,
                paymentIntent.AuthorizationReference,
                paymentIntent.FailureMessage ?? "(none)");

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripePaymentIntentId == paymentIntent.AuthorizationReference);

            if (reservation == null)
            {
                _logger.LogWarning("Payments/Webhook => Payment failed but reservation not found paymentIntent={PaymentIntentId}", paymentIntent.AuthorizationReference);
                return null;
            }

            if (!IsOwnedBy(reservation, gateway))
            {
                _logger.LogWarning("Payments/Webhook => Payment failed for a reservation owned by another provider reservation={ReservationId} provider={Provider}", reservation.ReservationId, reservation.PaymentProvider);
                return null;
            }

            if (IsTerminalReservationStatus(reservation.Status))
            {
                _logger.LogInformation("Payments/Webhook => Payment failed ignored for terminal reservation={ReservationId} status={Status}",
                    reservation.ReservationId,
                    reservation.Status ?? "(null)");
                return reservation.ReservationId;
            }

            var loadedStatus = reservation.Status;
            reservation.Status = PaymentReservationStatus.Failed;
            reservation.LastError = paymentIntent.FailureMessage ?? "Payment failed.";
            reservation.UpdatedAtUtc = _utcNow();
            if (!TryCommitReservationTransition(dbContext, reservation, loadedStatus))
            {
                _logger.LogInformation("Payments/Webhook => Reservation changed before the payment failure was applied reservation={ReservationId} status={Status}", reservation.ReservationId, reservation.Status ?? "(null)");
            }

            return reservation.ReservationId;
        }

        /// <summary>
        /// Content-free PSP signal (e.g. Saferpay notify): nothing in the request is trusted. The checkout is
        /// re-read from the owning provider and the same transitions as a completed Stripe checkout apply.
        /// </summary>
        private Guid? HandleCheckoutNotification(OCPPCoreContext dbContext, IPaymentGateway gateway, PaymentWebhookEvent webhookEvent)
        {
            if (!webhookEvent.ReservationId.HasValue)
            {
                _logger.LogWarning("Payments/Webhook => {Provider} notification without reservation id eventId={EventId}", gateway.ProviderName, webhookEvent.EventId);
                return null;
            }

            var reservation = dbContext.ChargePaymentReservations.Find(webhookEvent.ReservationId.Value);
            if (reservation == null)
            {
                _logger.LogWarning("Payments/Webhook => {Provider} notification for unknown reservation={ReservationId}", gateway.ProviderName, webhookEvent.ReservationId.Value);
                return null;
            }

            if (!IsOwnedBy(reservation, gateway))
            {
                _logger.LogWarning("Payments/Webhook => {Provider} notification for a reservation owned by another provider reservation={ReservationId} provider={ReservationProvider}", gateway.ProviderName, reservation.ReservationId, reservation.PaymentProvider);
                return null;
            }

            if (string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
            {
                _logger.LogWarning("Payments/Webhook => {Provider} notification but reservation has no checkout reference reservation={ReservationId}", gateway.ProviderName, reservation.ReservationId);
                return reservation.ReservationId;
            }

            var checkout = gateway.GetCheckout(reservation.StripeCheckoutSessionId);
            if (checkout == null)
            {
                _logger.LogWarning("Payments/Webhook => {Provider} notification but checkout was not found reservation={ReservationId}", gateway.ProviderName, reservation.ReservationId);
                return reservation.ReservationId;
            }

            if ((checkout.OwnerReservationId.HasValue && checkout.OwnerReservationId.Value != reservation.ReservationId) ||
                (!string.IsNullOrWhiteSpace(checkout.SessionReference) &&
                 !string.Equals(checkout.SessionReference, reservation.StripeCheckoutSessionId, StringComparison.Ordinal)))
            {
                _logger.LogWarning("Payments/Webhook => {Provider} checkout does not belong to reservation={ReservationId}", gateway.ProviderName, reservation.ReservationId);
                return null;
            }

            _logger.LogInformation("Payments/Webhook => {Provider} notification reservation={ReservationId} checkoutState={State} rawStatus={RawStatus} reservationStatus={ReservationStatus}",
                gateway.ProviderName,
                reservation.ReservationId,
                checkout.State,
                checkout.RawStatus ?? "(null)",
                reservation.Status ?? "(null)");

            switch (checkout.State)
            {
                case PaymentCheckoutState.Complete:
                    ApplyNotifiedCompletedCheckout(dbContext, gateway, reservation, checkout);
                    break;
                case PaymentCheckoutState.Canceled:
                    if (string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyCheckoutCancelled(reservation);
                        TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending);
                    }
                    break;
                case PaymentCheckoutState.Failed:
                    if (string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyCheckoutFailed(reservation, gateway, checkout);
                        TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending);
                    }
                    break;
                default:
                    // Open, Expired or Unknown: nothing verified yet; the browser return or cleanup sweep decides.
                    break;
            }

            return reservation.ReservationId;
        }

        private void ApplyNotifiedCompletedCheckout(
            OCPPCoreContext dbContext,
            IPaymentGateway gateway,
            ChargePaymentReservation reservation,
            PaymentCheckoutStatus checkout)
        {
            var authorizationReference = checkout.AuthorizationReference;
            var authorization = checkout.Authorization;
            if (authorization == null && !string.IsNullOrWhiteSpace(authorizationReference))
            {
                authorization = gateway.GetAuthorization(authorizationReference);
            }

            if (string.IsNullOrWhiteSpace(authorizationReference))
            {
                authorizationReference = authorization?.AuthorizationReference;
            }

            LinkNotifiedAuthorization(reservation, checkout, authorizationReference);
            var authorizationHeld = IsNotifiedAuthorizationHeld(reservation, authorization);
            var authorizedByNotification = false;
            var transitionAttempted = false;
            var transitionLost = false;
            if (string.Equals(reservation.Status, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                if (authorizationHeld &&
                    authorization.State == PaymentAuthorizationState.Captured &&
                    gateway.CapturedCheckoutRequiresRefund)
                {
                    ApplyCapturedAtCheckout(reservation, gateway, authorization, checkout, reservation.StripePaymentIntentId, "Webhook");
                    reservation.UpdatedAtUtc = _utcNow();
                    transitionAttempted = true;
                    transitionLost = !TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending);
                }
                else if (authorizationHeld)
                {
                    if (authorization.AmountMinor > 0)
                    {
                        reservation.MaxAmountCents = authorization.AmountMinor;
                    }

                    transitionAttempted = true;
                    authorizedByNotification = MarkAuthorizedFromProviderSignal(dbContext, reservation);
                    transitionLost = !authorizedByNotification;
                }
                else if (authorization != null &&
                         (authorization.State == PaymentAuthorizationState.Failed ||
                          authorization.State == PaymentAuthorizationState.Canceled))
                {
                    ApplyCheckoutFailed(
                        reservation,
                        gateway,
                        new PaymentCheckoutStatus
                        {
                            FailureCode = checkout.FailureCode,
                            FailureMessage = checkout.FailureMessage ?? authorization.FailureMessage
                        });
                    transitionAttempted = true;
                    transitionLost = !TryCommitReservationTransition(dbContext, reservation, PaymentReservationStatus.Pending);
                }
            }

            if (transitionLost)
            {
                // The browser return authorized it, or a cancellation or the sweep ended it, first (reloaded).
                _logger.LogInformation("Payments/Webhook => Reservation changed while the notification was processed reservation={ReservationId} status={Status}", reservation.ReservationId, reservation.Status ?? "(null)");
                if (dbContext.Entry(reservation).State == EntityState.Detached)
                {
                    return;
                }

                if (IsTerminalNoChargeStatus(reservation.Status))
                {
                    LinkNotifiedAuthorization(reservation, checkout, authorizationReference);
                }

                authorizationHeld = IsNotifiedAuthorizationHeld(reservation, authorization);
            }

            if (!transitionAttempted || transitionLost)
            {
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
            }

            if (authorizedByNotification && string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder))
            {
                // A browser return no longer re-confirms an authorized reservation of such a provider, so the
                // notification that authorized it sends the e-mail with the live status link.
                SendPaymentAuthorizedEmail(gateway, reservation, checkout, "Webhook");
            }

            // A held authorization the checkout reports is cancelled directly, whatever the release state: the
            // checkout carries the freshest provider view and a reconciliation lookup can lag behind it. A
            // cancellation or sweep that ended the reservation and already released it is not repeated.
            if (authorizationHeld &&
                (transitionLost
                    ? ReleaseAuthorizationAfterLostTransition(dbContext, gateway, reservation, checkout, reservation.StripePaymentIntentId, "Webhook")
                    : TryReleaseLateAuthorization(dbContext, gateway, reservation, authorization, "Webhook")))
            {
                return;
            }

            if (IsArmedTerminalAuthorizationRelease(reservation))
            {
                ReconcileTerminalPaymentAuthorization(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook);
            }
        }

        /// <summary>Links the authorization a notification reported, and the payer details, to a reservation without one.</summary>
        private static void LinkNotifiedAuthorization(ChargePaymentReservation reservation, PaymentCheckoutStatus checkout, string authorizationReference)
        {
            if (!string.IsNullOrWhiteSpace(authorizationReference) &&
                string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                reservation.StripePaymentIntentId = authorizationReference;
            }

            PersistPayerDetails(reservation, checkout);
        }

        private static bool IsNotifiedAuthorizationHeld(ChargePaymentReservation reservation, PaymentAuthorizationSnapshot authorization) =>
            authorization != null &&
            (authorization.State == PaymentAuthorizationState.RequiresCapture ||
             authorization.State == PaymentAuthorizationState.Captured) &&
            !string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) &&
            (string.IsNullOrWhiteSpace(authorization.AuthorizationReference) ||
             string.Equals(reservation.StripePaymentIntentId, authorization.AuthorizationReference, StringComparison.Ordinal));

        private ChargePaymentReservation FindReservationForTransaction(OCPPCoreContext dbContext, Transaction transaction)
        {
            ChargePaymentReservation reservation = null;
            if (transaction.TransactionId > 0)
            {
                reservation = dbContext.ChargePaymentReservations
                    .FirstOrDefault(r =>
                        r.TransactionId == transaction.TransactionId ||
                        r.StartTransactionId == transaction.TransactionId);
            }

            if (reservation != null) return RefreshTrackedReservation(dbContext, reservation);

            reservation = FindActiveReservationByTag(
                dbContext,
                transaction.ChargePointId,
                transaction.ConnectorId,
                transaction.StartTagId);

            if (reservation != null)
            {
                reservation = RefreshTrackedReservation(dbContext, reservation);
                if (reservation == null) return null;
                RelinkReservationToTransaction(reservation, transaction.TransactionId, transaction);
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
            }

            return reservation;
        }

        private static ChargePaymentReservation RefreshTrackedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            // A long-lived context (e.g. a charge point WebSocket) can hold a copy loaded before the buyer confirmed
            // R1 invoice data in the portal. That write bumps the InvoiceBuyerConfirmedAtUtc concurrency token,
            // so settle against the current row instead of the cached one.
            var entry = dbContext.Entry(reservation);
            if (entry.State == EntityState.Unchanged)
            {
                entry.Reload();
                if (entry.State == EntityState.Detached) return null;
            }

            return reservation;
        }

        private ChargePaymentReservation FindActiveReservationByTag(
            OCPPCoreContext dbContext,
            string chargePointId,
            int connectorId,
            string tagId)
        {
            if (dbContext == null ||
                string.IsNullOrWhiteSpace(chargePointId) ||
                connectorId <= 0)
            {
                return null;
            }

            var normalizedTag = NormalizeChargeTag(tagId);
            if (string.IsNullOrWhiteSpace(normalizedTag))
            {
                return null;
            }

            // A charging reservation whose settlement waits for a retry belongs to its stopped transaction: a new
            // transaction with the same tag must not take it over. (A charger that stops and restarts with the same
            // tag still hands the reservation to the new transaction, as before.)
            var retryCode = SettlementRetryPendingCode;
            return dbContext.ChargePaymentReservations
                .Where(r =>
                    r.ChargePointId == chargePointId &&
                    r.ConnectorId == connectorId &&
                    (r.OcppIdTag == normalizedTag || r.ChargeTagId == normalizedTag) &&
                    (r.Status == PaymentReservationStatus.Authorized ||
                     r.Status == PaymentReservationStatus.StartRequested ||
                     ((r.Status == PaymentReservationStatus.Charging ||
                       r.Status == PaymentReservationStatus.WaitingForDisconnect) &&
                      (r.FailureCode == null || r.FailureCode != retryCode))))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();
        }

        private void RelinkReservationToTransaction(
            ChargePaymentReservation reservation,
            int transactionId,
            Transaction transaction)
        {
            if (reservation == null || transactionId <= 0)
            {
                return;
            }

            var transactionStartTimeUtc = transaction?.StartTime;
            if (transaction != null &&
                transaction.TransactionId == transactionId &&
                !transaction.NightTariffStartMinute.HasValue &&
                NightTariffWindow.ForReservation(reservation) != null)
            {
                // Freeze the reservation's night window on the transaction so accepted meter readings
                // can be split into day and night energy as they arrive.
                transaction.NightTariffStartMinute = reservation.NightTariffStartMinute;
                transaction.NightTariffEndMinute = reservation.NightTariffEndMinute;
                transaction.NightTariffTimeZoneId = reservation.NightTariffTimeZoneId;
            }

            reservation.TransactionId = transactionId;
            reservation.StartTransactionId ??= transactionId;
            if (!reservation.StartTransactionAtUtc.HasValue &&
                transactionStartTimeUtc.HasValue &&
                transactionStartTimeUtc.Value != default)
            {
                reservation.StartTransactionAtUtc = transactionStartTimeUtc.Value;
            }
            else if (!reservation.StartTransactionAtUtc.HasValue)
            {
                reservation.StartTransactionAtUtc = _utcNow();
            }
        }

        private void CloseSupersededTransactions(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            Transaction currentTransaction)
        {
            if (dbContext == null ||
                reservation == null ||
                currentTransaction == null ||
                currentTransaction.TransactionId <= 0)
            {
                return;
            }

            var relatedOpenTransactions = dbContext.Transactions
                .Where(t =>
                    t.ChargePointId == currentTransaction.ChargePointId &&
                    t.ConnectorId == currentTransaction.ConnectorId &&
                    t.TransactionId != currentTransaction.TransactionId &&
                    !t.StopTime.HasValue)
                .OrderBy(t => t.StartTime)
                .ToList();

            if (relatedOpenTransactions.Count == 0)
            {
                return;
            }

            var relatedTransactionIds = new HashSet<int>();
            if (reservation.TransactionId.HasValue && reservation.TransactionId.Value != currentTransaction.TransactionId)
            {
                relatedTransactionIds.Add(reservation.TransactionId.Value);
            }

            if (reservation.StartTransactionId.HasValue && reservation.StartTransactionId.Value != currentTransaction.TransactionId)
            {
                relatedTransactionIds.Add(reservation.StartTransactionId.Value);
            }

            var normalizedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNormalizedTag(normalizedTags, currentTransaction.StartTagId);
            AddNormalizedTag(normalizedTags, reservation.OcppIdTag);
            AddNormalizedTag(normalizedTags, reservation.ChargeTagId);

            DateTime stopTimeUtc = currentTransaction.StartTime != default
                ? currentTransaction.StartTime
                : (currentTransaction.StopTime ?? _utcNow());

            foreach (var supersededTransaction in relatedOpenTransactions)
            {
                bool isLinkedTransaction = relatedTransactionIds.Contains(supersededTransaction.TransactionId);
                bool matchesTag = normalizedTags.Count > 0 &&
                                  normalizedTags.Contains(NormalizeChargeTag(supersededTransaction.StartTagId));
                if (!isLinkedTransaction && !matchesTag)
                {
                    continue;
                }

                supersededTransaction.StopTime = stopTimeUtc;
                supersededTransaction.MeterStop ??= currentTransaction.MeterStart > 0
                    ? Math.Max(supersededTransaction.MeterStart, currentTransaction.MeterStart)
                    : supersededTransaction.MeterStart;
                supersededTransaction.StopReason ??= $"SupersededByTx{currentTransaction.TransactionId}";
                supersededTransaction.ChargingEndedAtUtc ??= stopTimeUtc;

                _logger.LogWarning(
                    "Payments/Transactions => Closed superseded open transaction oldTx={SupersededTransactionId} newTx={CurrentTransactionId} cp={ChargePointId} connector={ConnectorId} stopAt={StopTimeUtc:u}",
                    supersededTransaction.TransactionId,
                    currentTransaction.TransactionId,
                    currentTransaction.ChargePointId,
                    currentTransaction.ConnectorId,
                    stopTimeUtc);
            }
        }

        private static void AddNormalizedTag(ISet<string> normalizedTags, string tag)
        {
            var normalized = NormalizeChargeTag(tag);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                normalizedTags.Add(normalized);
            }
        }

        private static string NormalizeChargeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return tag;
            int separatorIndex = tag.IndexOf('_');
            return separatorIndex >= 0 ? tag.Substring(0, separatorIndex) : tag;
        }

        private static bool IsTerminalReservationStatus(string status)
        {
            return PaymentReservationStatus.IsTerminal(status);
        }

        private static void ClearFailureState(ChargePaymentReservation reservation)
        {
            if (reservation == null) return;
            reservation.LastError = null;
            reservation.FailureCode = null;
            reservation.FailureMessage = null;
        }

        private static void EnsureChargeTagExists(OCPPCoreContext dbContext, string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId)) return;

            var existing = dbContext.ChargeTags.Find(tagId);
            if (existing != null) return;

            var newTag = new ChargeTag
            {
                TagId = tagId,
                TagName = $"Web session {tagId}",
                Blocked = false
            };

            dbContext.ChargeTags.Add(newTag);
            try
            {
                dbContext.SaveChanges();
            }
            catch (DbUpdateException ex) when (ex.Entries.Any(entry => ReferenceEquals(entry.Entity, newTag)))
            {
                // A concurrent request (browser return and provider notification) created the same tag first.
                dbContext.Entry(newTag).State = EntityState.Detached;
                if (!dbContext.ChargeTags.AsNoTracking().Any(tag => tag.TagId == tagId))
                {
                    throw;
                }

                // Persist the remaining changes that the failed save carried.
                dbContext.SaveChanges();
            }
        }

        private static string TrimTrailingSlash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.TrimEnd('/');
        }

        private static long CalculateAmountInCents(double energyKwh, decimal pricePerKwh)
        {
            var kwh = Convert.ToDecimal(energyKwh);
            var subtotal = Math.Round(pricePerKwh * kwh, 2, MidpointRounding.AwayFromZero);
            return (long)Math.Round(subtotal * 100m, 0, MidpointRounding.AwayFromZero);
        }

        private static long CalculateUsageFeeInCents(int minutes, decimal pricePerMinute)
        {
            if (minutes <= 0 || pricePerMinute <= 0) return 0;
            var subtotal = Math.Round(pricePerMinute * minutes, 2, MidpointRounding.AwayFromZero);
            return (long)Math.Round(subtotal * 100m, 0, MidpointRounding.AwayFromZero);
        }

        private static long CalculateFlatAmountInCents(decimal amount)
        {
            if (amount <= 0) return 0;
            var subtotal = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            return (long)Math.Round(subtotal * 100m, 0, MidpointRounding.AwayFromZero);
        }

        private static bool ShouldChargeSessionFee(
            ChargePaymentReservation reservation,
            double actualEnergyKwh,
            bool hasValidDeliveredEnergy,
            PaymentFlowOptions flowOptions,
            out string suppressionReason)
        {
            suppressionReason = null;

            if (reservation == null || reservation.UserSessionFee <= 0)
            {
                return false;
            }

            if (!hasValidDeliveredEnergy)
            {
                suppressionReason = "missing or inconsistent delivered energy";
                return false;
            }

            var minimumSessionFeeKwh = flowOptions?.MinimumSessionFeeKwh ?? 1.0m;
            if (minimumSessionFeeKwh < 0)
            {
                minimumSessionFeeKwh = 0;
            }

            if (minimumSessionFeeKwh > 0 &&
                Convert.ToDecimal(actualEnergyKwh) < minimumSessionFeeKwh)
            {
                suppressionReason = $"delivered energy below {minimumSessionFeeKwh:0.###} kWh";
                return false;
            }

            return true;
        }

        private static bool ShouldTreatAsNoChargeForDeliveredEnergy(
            double actualEnergyKwh,
            bool hasValidDeliveredEnergy,
            PaymentFlowOptions flowOptions,
            out string reason)
        {
            reason = null;

            if (!hasValidDeliveredEnergy)
            {
                reason = "missing or inconsistent delivered energy";
                return true;
            }

            var minimumSessionFeeKwh = flowOptions?.MinimumSessionFeeKwh ?? 1.0m;
            if (minimumSessionFeeKwh < 0)
            {
                minimumSessionFeeKwh = 0;
            }

            if (minimumSessionFeeKwh > 0 &&
                Convert.ToDecimal(actualEnergyKwh) < minimumSessionFeeKwh)
            {
                reason = $"delivered energy below configured minimum session energy {minimumSessionFeeKwh:0.###} kWh";
                return true;
            }

            return false;
        }

        private static void PersistTransactionBreakdown(
            OCPPCoreContext dbContext,
            Transaction transaction,
            ChargePaymentReservation reservation,
            double energyKwh,
            long energyCostCents,
            double nightEnergyKwh,
            long nightEnergyCostCents,
            int usageFeeMinutes,
            long usageFeeCents,
            long sessionFeeCents,
            long totalCents)
        {
            if (transaction == null) return;

            decimal energyCost = ConvertToDecimal(energyCostCents);
            decimal usageFee = ConvertToDecimal(usageFeeCents);
            decimal userSessionFee = ConvertToDecimal(sessionFeeCents);
            decimal gross = energyCost + usageFee + userSessionFee;

            decimal operatorCommission = 0m;
            if (reservation.OwnerCommissionPercent > 0)
            {
                operatorCommission = Math.Round(gross * (reservation.OwnerCommissionPercent / 100m), 4, MidpointRounding.AwayFromZero);
            }
            else if (reservation.OwnerCommissionFixedPerKwh > 0 && energyKwh > 0)
            {
                operatorCommission = Math.Round(reservation.OwnerCommissionFixedPerKwh * Convert.ToDecimal(energyKwh), 4, MidpointRounding.AwayFromZero);
            }

            decimal ownerSessionFee = reservation.OwnerSessionFee;
            decimal operatorRevenueTotal = operatorCommission + ownerSessionFee;
            decimal ownerPayout = Math.Max(0m, gross - operatorRevenueTotal);

            bool usageAfterChargingEnds = reservation?.UsageFeeAnchorMinutes == 1;
            transaction.EnergyKwh = energyKwh;
            transaction.EnergyCost = energyCost;
            transaction.NightEnergyKwh = nightEnergyKwh;
            transaction.NightEnergyCost = ConvertToDecimal(nightEnergyCostCents);
            transaction.UsageFeeMinutes = usageFeeMinutes;
            transaction.UsageFeeAmount = usageFee;
            transaction.UserSessionFeeAmount = userSessionFee;
            transaction.IdleUsageFeeMinutes = usageAfterChargingEnds ? usageFeeMinutes : 0;
            transaction.IdleUsageFeeAmount = usageAfterChargingEnds ? usageFee : 0;
            transaction.OwnerSessionFeeAmount = ownerSessionFee;
            transaction.OwnerCommissionPercent = reservation.OwnerCommissionPercent;
            transaction.OwnerCommissionFixedPerKwh = reservation.OwnerCommissionFixedPerKwh;
            transaction.OperatorCommissionAmount = operatorCommission;
            transaction.OperatorRevenueTotal = operatorRevenueTotal;
            transaction.OwnerPayoutTotal = ownerPayout;
            transaction.Currency = reservation.Currency;

            dbContext.SaveChanges();
        }

        private static decimal ConvertToDecimal(long cents) =>
            Math.Round(cents / 100m, 4, MidpointRounding.AwayFromZero);

        private static string BuildIdempotencyKey(string purpose, Guid reservationId, long? amount = null)
        {
            if (string.IsNullOrWhiteSpace(purpose) || reservationId == Guid.Empty) return null;
            return amount.HasValue
                ? $"{purpose}:{reservationId}:{amount.Value}"
                : $"{purpose}:{reservationId}";
        }

        private static string GenerateOcppIdTag()
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = Guid.NewGuid().ToByteArray();
            int bits = 0, value = 0;
            var chars = new System.Text.StringBuilder();
            foreach (var b in bytes)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    chars.Append(alphabet[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0)
            {
                chars.Append(alphabet[(value << (5 - bits)) & 31]);
            }
            var tag = "R" + chars.ToString();
            return tag.Length > 20 ? tag.Substring(0, 20) : tag;
        }

        // EnsureChargeTagExists defined earlier in file; keep single definition.

        private bool HasProcessedWebhookEvent(OCPPCoreContext dbContext, string eventId)
        {
            if (string.IsNullOrWhiteSpace(eventId)) return false;
            try
            {
                return dbContext.StripeWebhookEvents.Any(e => e.EventId == eventId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping webhook idempotency check (table may be missing).");
                return false;
            }
        }

        private void MarkWebhookEventProcessed(OCPPCoreContext dbContext, string eventKey, PaymentWebhookEvent webhookEvent, Guid? reservationId = null)
        {
            if (webhookEvent == null || string.IsNullOrWhiteSpace(eventKey)) return;
            try
            {
                var processed = new StripeWebhookEvent
                {
                    EventId = eventKey,
                    Type = webhookEvent.EventType,
                    StripeCreatedAtUtc = webhookEvent.CreatedAtUtc,
                    ProcessedAtUtc = _utcNow(),
                    ReservationId = reservationId
                };
                dbContext.StripeWebhookEvents.Add(processed);
                dbContext.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to persist webhook idempotency record (table may be missing).");
            }
        }

        private static string AuthorizationTerm(IPaymentGateway gateway) =>
            IsStripe(gateway) ? "PaymentIntent" : "payment authorization";

        private static string AuthorizationSubject(IPaymentGateway gateway) =>
            IsStripe(gateway) ? "PaymentIntent" : "Payment authorization";

        private static string AuthorizationNoun(IPaymentGateway gateway) =>
            IsStripe(gateway) ? "payment intent" : "payment authorization";

        private static string ProviderErrorStatus(IPaymentGateway gateway) =>
            $"{gateway?.ProviderName ?? "Provider"}Error";

        private static bool IsStripe(IPaymentGateway gateway) =>
            string.Equals(gateway?.ProviderName, PaymentProviderNames.Stripe, StringComparison.OrdinalIgnoreCase);
    }
}
