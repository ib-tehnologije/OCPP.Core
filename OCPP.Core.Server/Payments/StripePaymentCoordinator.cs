/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using Stripe;
using Stripe.Checkout;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Payments
{
    public partial class StripePaymentCoordinator : IPaymentCoordinator
    {
        private readonly StripeOptions _options;
        private readonly PaymentFlowOptions _flowOptions;
        private readonly ILogger<StripePaymentCoordinator> _logger;
        private readonly IStripeSessionService _sessionService;
        private readonly IStripePaymentIntentService _paymentIntentService;
        private readonly Func<DateTime> _utcNow;
        private readonly IStripeEventFactory _eventFactory;
        private readonly IEmailNotificationService _emailNotificationService;
        private readonly StartChargingMediator _startMediator;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly IInvoiceIntegrationService _invoiceIntegrationService;
        private const string IdempotencyCheckoutCreate = "checkout_create";
        private const string IdempotencyCapture = "capture";
        private const string IdempotencyCancel = "cancel";

        public bool IsEnabled =>
            _options.Enabled &&
            !string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.ReturnBaseUrl);

        public StripePaymentCoordinator(
            IOptions<StripeOptions> options,
            IOptions<PaymentFlowOptions> flowOptions,
            ILogger<StripePaymentCoordinator> logger)
            : this(options, flowOptions, logger, new StripeSessionServiceWrapper(), new StripePaymentIntentServiceWrapper(), new StripeEventFactoryWrapper(), () => DateTime.UtcNow, null, null, null, null)
        {
        }

        internal StripePaymentCoordinator(
            IOptions<StripeOptions> options,
            IOptions<PaymentFlowOptions> flowOptions,
            ILogger<StripePaymentCoordinator> logger,
            IStripeSessionService sessionService,
            IStripePaymentIntentService paymentIntentService,
            IStripeEventFactory eventFactory,
            Func<DateTime> utcNow,
            IEmailNotificationService emailNotificationService = null,
            StartChargingMediator startMediator = null,
            IBackgroundJobClient backgroundJobClient = null,
            IInvoiceIntegrationService invoiceIntegrationService = null)
        {
            _options = options?.Value ?? new StripeOptions();
            _flowOptions = flowOptions?.Value ?? new PaymentFlowOptions();
            _logger = logger;
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _paymentIntentService = paymentIntentService ?? throw new ArgumentNullException(nameof(paymentIntentService));
            _eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _emailNotificationService = emailNotificationService;
            _startMediator = startMediator;
            _backgroundJobClient = backgroundJobClient;
            _invoiceIntegrationService = invoiceIntegrationService;

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                StripeConfiguration.ApiKey = _options.ApiKey;
            }
        }

        public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request)
        {
            if (!IsEnabled)
            {
                throw new InvalidOperationException("Stripe integration is disabled.");
            }
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ChargePointId)) throw new ArgumentException("ChargePointId is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ChargeTagId)) throw new ArgumentException("ChargeTagId is required.", nameof(request));

            var chargePoint = dbContext.ChargePoints.Find(request.ChargePointId);
            if (chargePoint == null)
            {
                throw new ArgumentException($"Charge point '{request.ChargePointId}' not found.", nameof(request.ChargePointId));
            }

            double maxEnergyKwh = chargePoint.MaxSessionKwh;
            decimal pricePerKwh = chargePoint.PricePerKwh;
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

            var maxEnergyCents = CalculateAmountInCents(maxEnergyKwh, pricePerKwh);
            var maxUsageFeeCents = CalculateUsageFeeInCents(
                Math.Max(0, maxUsageFeeMinutes - startUsageFeeAfterMinutes),
                usageFeePerMinute);
            var sessionFeeCents = CalculateFlatAmountInCents(userSessionFee);
            var maxTotalCents = maxEnergyCents + maxUsageFeeCents + sessionFeeCents;

            if (maxTotalCents <= 0)
            {
                throw new InvalidOperationException("Calculated maximum amount is zero. Check pricing configuration.");
            }

            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = request.ChargePointId,
                ConnectorId = request.ConnectorId,
                ChargeTagId = normalizedTag,
                MaxEnergyKwh = maxEnergyKwh,
                PricePerKwh = pricePerKwh,
                UserSessionFee = userSessionFee,
                OwnerSessionFee = ownerSessionFee,
                OwnerCommissionPercent = ownerCommissionPercent,
                OwnerCommissionFixedPerKwh = ownerCommissionFixedPerKwh,
                UsageFeePerMinute = usageFeePerMinute,
                StartUsageFeeAfterMinutes = startUsageFeeAfterMinutes,
                MaxUsageFeeMinutes = maxUsageFeeMinutes,
                UsageFeeAnchorMinutes = usageFeeAnchorMinutes,
                MaxAmountCents = maxTotalCents,
                Currency = string.IsNullOrWhiteSpace(_options.Currency) ? "eur" : _options.Currency.ToLowerInvariant(),
                Status = PaymentReservationStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            _logger.LogInformation(
                "Stripe/CreateCheckout => reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} tag={ChargeTagId} maxTotalCents={MaxTotalCents} currency={Currency} maxEnergyKwh={MaxEnergyKwh} pricePerKwh={PricePerKwh} sessionFee={SessionFee} usageFeePerMin={UsageFeePerMinute} maxUsageMinutes={MaxUsageMinutes}",
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
                ? _options.ReturnBaseUrl
                : request.ReturnBaseUrl;

            var successUrl = $"{TrimTrailingSlash(baseReturnUrl)}/Payments/Success?reservationId={reservation.ReservationId}&session_id={{CHECKOUT_SESSION_ID}}";
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

            if (request.RequestR1Invoice)
            {
                metadata["invoice_type"] = "R1";
            }

            var buyerOib = NormalizeOibDigits(request.BuyerOib);
            if (!string.IsNullOrWhiteSpace(buyerOib))
            {
                metadata["buyer_oib"] = TrimMetadataValue(buyerOib, 32);
            }

            var buyerCompanyName = (request.BuyerCompanyName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(buyerCompanyName))
            {
                metadata["buyer_company"] = TrimMetadataValue(buyerCompanyName, 200);
            }

            var sessionOptions = new SessionCreateOptions
            {
                Mode = "payment",
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
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
                            Currency = reservation.Currency,
                            UnitAmount = reservation.MaxAmountCents,
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = string.IsNullOrWhiteSpace(_options.ProductName)
                                    ? $"Charging session ({reservation.MaxEnergyKwh:0.#} kWh)"
                                    : _options.ProductName
                            }
                        }
                    }
                }
            };

            try
            {
                var session = _sessionService.Create(
                    sessionOptions,
                    BuildIdempotencyOptions(IdempotencyCheckoutCreate, reservation.ReservationId));

                reservation.StripeCheckoutSessionId = session.Id;
                reservation.StripePaymentIntentId = session.PaymentIntentId;
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
                    "Stripe/CreateCheckout => created sessionId={SessionId} paymentIntentId={PaymentIntentId} checkoutUrlLength={CheckoutUrlLength} reservation={ReservationId}",
                    session.Id,
                    session.PaymentIntentId ?? "(none)",
                    session.Url?.Length ?? 0,
                    reservation.ReservationId);

                return new PaymentSessionResult
                {
                    Reservation = reservation,
                    CheckoutUrl = session.Url
                };
            }
            catch (StripeException sex)
            {
                _logger.LogError(sex, "Stripe checkout session creation failed: {Message}", sex.Message);
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
                Error = "Stripe disabled"
            };

            if (!IsEnabled)
            {
                _logger.LogWarning("Stripe/Confirm => Stripe disabled while confirming reservation={ReservationId}", reservationId);
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null)
            {
                _logger.LogWarning("Stripe/Confirm => Reservation not found reservation={ReservationId}", reservationId);
                result.Error = "Reservation not found.";
                result.Status = "NotFound";
                return result;
            }

            result.Reservation = reservation;

            if (IsTerminalReservationStatus(reservation.Status))
            {
                _logger.LogWarning("Stripe/Confirm => Reservation is terminal and cannot be confirmed reservation={ReservationId} status={Status}",
                    reservationId,
                    reservation.Status ?? "(null)");
                result.Error = $"Reservation is in terminal status '{reservation.Status ?? "Unknown"}'.";
                result.Status = reservation.Status ?? "InvalidStatus";
                return result;
            }

            if (!string.Equals(reservation.StripeCheckoutSessionId, checkoutSessionId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Stripe/Confirm => Checkout session mismatch reservation={ReservationId} expected={Expected} received={Received}", reservationId, reservation.StripeCheckoutSessionId, checkoutSessionId);
                result.Error = "Checkout session mismatch.";
                result.Status = "SessionMismatch";
                return result;
            }

            try
            {
                _logger.LogInformation("Stripe/Confirm => Fetching session reservation={ReservationId} sessionId={SessionId}", reservationId, checkoutSessionId);
                var session = _sessionService.Get(checkoutSessionId);
                if (!string.Equals(session.Status, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Stripe/Confirm => Session not complete reservation={ReservationId} status={Status}", reservationId, session.Status);
                    result.Error = $"Checkout session not completed (status={session.Status}).";
                    result.Status = session.Status;
                    return result;
                }

                var paymentIntentId = session.PaymentIntentId;
                if (string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    _logger.LogWarning("Stripe/Confirm => PaymentIntent missing reservation={ReservationId} sessionId={SessionId}", reservationId, checkoutSessionId);
                    result.Error = "PaymentIntent missing in session.";
                    result.Status = "MissingPaymentIntent";
                    return result;
                }

                var paymentIntent = _paymentIntentService.Get(paymentIntentId);

                if (!string.Equals(paymentIntent.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(paymentIntent.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Stripe/Confirm => Unexpected PaymentIntent status reservation={ReservationId} paymentIntent={PaymentIntentId} status={Status}", reservationId, paymentIntentId, paymentIntent.Status);
                    result.Error = $"Unexpected PaymentIntent status '{paymentIntent.Status}'.";
                    result.Status = paymentIntent.Status;
                    reservation.Status = PaymentReservationStatus.Failed;
                    reservation.LastError = result.Error;
                    reservation.UpdatedAtUtc = _utcNow();
                    dbContext.SaveChanges();
                    return result;
                }

                string previousStatus = reservation.Status;
                bool shouldSendAuthorizationEmail =
                    string.Equals(previousStatus, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(previousStatus, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase);

                reservation.StripePaymentIntentId = paymentIntentId;
                var paymentIntentAmount = (long?)paymentIntent.Amount;
                reservation.MaxAmountCents = paymentIntentAmount ?? reservation.MaxAmountCents;

                var now = _utcNow();
                if (string.Equals(previousStatus, PaymentReservationStatus.Pending, StringComparison.OrdinalIgnoreCase))
                {
                    reservation.Status = PaymentReservationStatus.Authorized;
                    reservation.AuthorizedAtUtc = now;
                    reservation.StartDeadlineAtUtc = now.AddMinutes(Math.Max(1, _flowOptions.StartWindowMinutes));
                }
                else if (string.Equals(previousStatus, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase))
                {
                    reservation.AuthorizedAtUtc ??= now;
                    reservation.StartDeadlineAtUtc ??= reservation.AuthorizedAtUtc.Value.AddMinutes(Math.Max(1, _flowOptions.StartWindowMinutes));
                }

                if (shouldSendAuthorizationEmail)
                {
                    ClearFailureState(reservation);
                    if (string.IsNullOrWhiteSpace(reservation.OcppIdTag))
                    {
                        reservation.OcppIdTag = GenerateOcppIdTag();
                    }
                    EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
                }

                reservation.UpdatedAtUtc = now;
                dbContext.SaveChanges();

                _logger.LogInformation(
                    "Stripe/Confirm => Reservation confirmed reservation={ReservationId} paymentIntent={PaymentIntentId} amount={Amount} paymentStatus={PaymentStatus} previousStatus={PreviousStatus} newStatus={NewStatus}",
                    reservationId,
                    paymentIntentId,
                    paymentIntentAmount,
                    paymentIntent.Status,
                    previousStatus ?? "(null)",
                    reservation.Status ?? "(null)");

                if (shouldSendAuthorizationEmail)
                {
                    try
                    {
                        var recipientEmail = session?.CustomerDetails?.Email;
                        var sessionId = session?.Id;
                        if (_backgroundJobClient != null)
                        {
                            _backgroundJobClient.Enqueue<PaymentAuthorizationEmailJob>(job =>
                                job.SendPaymentAuthorized(reservationId, recipientEmail, sessionId));
                            _logger.LogInformation("Stripe/Confirm => Queued payment authorization email reservation={ReservationId}", reservationId);
                        }
                        else
                        {
                            _emailNotificationService?.SendPaymentAuthorized(recipientEmail, reservation, session);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Stripe/Confirm => Failed to enqueue payment authorization email for reservation={ReservationId}", reservationId);
                    }
                }

                result.Success = true;
                result.Status = reservation.Status ?? PaymentReservationStatus.Authorized;
                result.Error = null;

                return result;
            }
            catch (StripeException sex)
            {
                _logger.LogError(sex, "Stripe confirmation failed for reservation {ReservationId}", reservationId);
                result.Error = sex.Message;
                result.Status = "StripeError";
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = sex.Message;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                return result;
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

            if (!IsEnabled)
            {
                result.Status = "Disabled";
                result.Error = "Stripe integration is disabled.";
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservationId == Guid.Empty) return result;

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null)
            {
                _logger.LogWarning("Stripe/Resume => Reservation not found reservation={ReservationId}", reservationId);
                result.Status = "NotFound";
                result.Error = "Reservation not found.";
                return result;
            }

            result.Reservation = reservation;

            if (PaymentReservationStatus.IsTerminal(reservation.Status))
            {
                _logger.LogInformation("Stripe/Resume => Reservation already terminal reservation={ReservationId} status={Status}",
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
                _logger.LogInformation("Stripe/Resume => Reservation should continue on status page reservation={ReservationId} status={Status}",
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
                _logger.LogWarning("Stripe/Resume => Missing checkout session reservation={ReservationId}", reservationId);
                result.Status = "MissingCheckoutSession";
                result.Error = "Checkout session is missing.";
                return result;
            }

            try
            {
                var session = _sessionService.Get(reservation.StripeCheckoutSessionId);
                if (session == null)
                {
                    _logger.LogWarning("Stripe/Resume => Session not found reservation={ReservationId} sessionId={SessionId}",
                        reservationId,
                        reservation.StripeCheckoutSessionId);
                    result.Status = "MissingCheckoutSession";
                    result.Error = "Checkout session was not found.";
                    return result;
                }

                if (string.Equals(session.Status, "open", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(session.Url))
                {
                    _logger.LogInformation("Stripe/Resume => Resuming open checkout reservation={ReservationId} sessionId={SessionId}",
                        reservationId,
                        session.Id);
                    result.Success = true;
                    result.Status = "Redirect";
                    result.CheckoutUrl = session.Url;
                    result.Error = null;
                    return result;
                }

                _logger.LogInformation("Stripe/Resume => Checkout session not resumable reservation={ReservationId} sessionId={SessionId} sessionStatus={SessionStatus}",
                    reservationId,
                    session.Id ?? reservation.StripeCheckoutSessionId,
                    session.Status ?? "(null)");
                result.Status = "ResumeUnavailable";
                result.Error = $"Checkout session is '{session.Status ?? "unknown"}' and can no longer be resumed.";
                return result;
            }
            catch (StripeException sex)
            {
                _logger.LogWarning(sex, "Stripe/Resume => Failed fetching checkout session reservation={ReservationId}", reservationId);
                result.Status = "StripeError";
                result.Error = sex.Message;
                return result;
            }
        }

        public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request)
        {
            var result = new PaymentR1InvoiceResult
            {
                Success = false,
                Status = "Invalid",
                Error = "Invalid request."
            };

            if (!IsEnabled)
            {
                result.Status = "Disabled";
                result.Error = "Stripe integration is disabled.";
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            if (request == null || request.ReservationId == Guid.Empty)
            {
                result.Error = "Reservation id is required.";
                return result;
            }

            var normalizedOib = NormalizeOibDigits(request.BuyerOib);
            if (string.IsNullOrWhiteSpace(normalizedOib) || !IsValidOib(normalizedOib))
            {
                result.Status = "InvalidOib";
                result.Error = "Valid OIB (11 digits) is required for R1 invoice.";
                return result;
            }

            var reservation = dbContext.ChargePaymentReservations.Find(request.ReservationId);
            if (reservation == null)
            {
                result.Status = "NotFound";
                result.Error = "Reservation not found.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
            {
                result.Status = "MissingCheckoutSession";
                result.Error = "Checkout session id is missing.";
                result.Reservation = reservation;
                return result;
            }

            var buyerCompanyName = TrimMetadataValue(request.BuyerCompanyName, 200);
            var buyerOibForMetadata = TrimMetadataValue(normalizedOib, 32);

            try
            {
                var session = _sessionService.Get(reservation.StripeCheckoutSessionId);
                if (session == null)
                {
                    result.Status = "SessionNotFound";
                    result.Error = "Stripe checkout session not found.";
                    result.Reservation = reservation;
                    return result;
                }

                bool alreadyRequested = IsR1InvoiceRequested(session);

                var sessionMetadata = new Dictionary<string, string>(
                    session.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                sessionMetadata["invoice_type"] = "R1";
                sessionMetadata["buyer_oib"] = buyerOibForMetadata;
                if (!string.IsNullOrWhiteSpace(buyerCompanyName))
                {
                    sessionMetadata["buyer_company"] = buyerCompanyName;
                }
                else
                {
                    sessionMetadata.Remove("buyer_company");
                }

                _sessionService.Update(
                    reservation.StripeCheckoutSessionId,
                    new SessionUpdateOptions
                    {
                        Metadata = sessionMetadata
                    });

                var paymentIntentId = string.IsNullOrWhiteSpace(session.PaymentIntentId)
                    ? reservation.StripePaymentIntentId
                    : session.PaymentIntentId;
                if (!string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    var paymentIntent = _paymentIntentService.Get(paymentIntentId);
                    var intentMetadata = new Dictionary<string, string>(
                        paymentIntent?.Metadata ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
                    intentMetadata["invoice_type"] = "R1";
                    intentMetadata["buyer_oib"] = buyerOibForMetadata;
                    if (!string.IsNullOrWhiteSpace(buyerCompanyName))
                    {
                        intentMetadata["buyer_company"] = buyerCompanyName;
                    }
                    else
                    {
                        intentMetadata.Remove("buyer_company");
                    }

                    _paymentIntentService.Update(
                        paymentIntentId,
                        new PaymentIntentUpdateOptions
                        {
                            Metadata = intentMetadata
                        });
                    reservation.StripePaymentIntentId = paymentIntentId;
                }

                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();

                if (!alreadyRequested)
                {
                    TrySendR1RequestedNotification(dbContext, reservation);
                }

                result.Success = true;
                result.Status = "Updated";
                result.Error = null;
                result.Reservation = reservation;
                result.BuyerCompanyName = buyerCompanyName;
                result.BuyerOib = buyerOibForMetadata;

                _logger.LogInformation(
                    "Stripe/R1Request => Updated metadata reservation={ReservationId} sessionId={SessionId} hasCompany={HasCompany}",
                    reservation.ReservationId,
                    reservation.StripeCheckoutSessionId,
                    !string.IsNullOrWhiteSpace(buyerCompanyName));

                return result;
            }
            catch (StripeException sex)
            {
                _logger.LogError(sex, "Stripe/R1Request => Failed updating metadata reservation={ReservationId}", request.ReservationId);
                result.Status = "StripeError";
                result.Error = sex.Message;
                result.Reservation = reservation;
                return result;
            }
        }

        public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null) return;

            if (!PaymentReservationStatus.IsCancelable(reservation.Status))
            {
                _logger.LogInformation(
                    "Stripe/Cancel => Ignoring cancel for reservation={ReservationId} status={Status}",
                    reservationId,
                    reservation.Status ?? "(none)");
                return;
            }

            CancelPaymentIntentIfCancelable(dbContext, reservation, reason);

            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.UpdatedAtUtc = _utcNow();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                reservation.LastError = reason;
            }
            dbContext.SaveChanges();

            _logger.LogInformation("Stripe/Cancel => Marked reservation={ReservationId} as Cancelled. LastError={LastError}", reservationId, reservation.LastError ?? "(none)");
        }

        public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservation == null) return;
            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId)) return;

            try
            {
                var pi = _paymentIntentService.Get(reservation.StripePaymentIntentId);
                if (pi == null) return;

                if (string.Equals(pi.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pi.Status, "requires_payment_method", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pi.Status, "requires_confirmation", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Stripe/CancelPI => Cancelling uncaptured PaymentIntent reservation={ReservationId} paymentIntent={PaymentIntentId} status={Status} reason={Reason}",
                        reservation.ReservationId,
                        pi.Id,
                        pi.Status,
                        reason ?? "(none)");

                    _paymentIntentService.Cancel(
                        pi.Id,
                        BuildIdempotencyOptions(IdempotencyCancel, reservation.ReservationId));
                }
                else
                {
                    _logger.LogInformation("Stripe/CancelPI => PaymentIntent not cancelable status={Status} reservation={ReservationId}", pi.Status, reservation.ReservationId);
                }
            }
            catch (StripeException sex)
            {
                _logger.LogWarning(sex, "Stripe/CancelPI => Unable to cancel payment intent {PaymentIntentId}", reservation.StripePaymentIntentId);
                reservation.LastError = sex.Message;
            }
        }

        public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var normalizedTag = NormalizeChargeTag(chargeTagId);

            var reservation = dbContext.ChargePaymentReservations
                .Where(r =>
                    r.ChargePointId == chargePointId &&
                    r.ConnectorId == connectorId &&
                    (r.OcppIdTag == normalizedTag || r.ChargeTagId == normalizedTag) &&
                    (r.Status == PaymentReservationStatus.Authorized || r.Status == PaymentReservationStatus.StartRequested))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();

            if (reservation == null) return;

            reservation.TransactionId = transactionId;
            reservation.StartTransactionId = transactionId;
            reservation.StartTransactionAtUtc = _utcNow();
            reservation.Status = PaymentReservationStatus.Charging;
            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();

            _logger.LogInformation("Stripe/MarkTransactionStarted => reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId}",
                reservation.ReservationId,
                transactionId,
                reservation.ChargePointId,
                reservation.ConnectorId);

            TrySendR1RequestedNotification(dbContext, reservation);
        }

        public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var reservation = FindReservationForTransaction(dbContext, transaction);
            if (reservation == null) return;
            if (reservation.Status == PaymentReservationStatus.Completed ||
                reservation.Status == PaymentReservationStatus.Cancelled)
            {
                return;
            }

            _logger.LogInformation("Stripe/Complete => Begin capture for reservation={ReservationId} tx={TransactionId} cp={ChargePointId} connector={ConnectorId} currentStatus={Status}",
                reservation.ReservationId,
                transaction.TransactionId,
                transaction.ChargePointId,
                transaction.ConnectorId,
                reservation.Status);

            double actualEnergy = 0;
            if (transaction.MeterStop.HasValue)
            {
                actualEnergy = Math.Max(0, transaction.MeterStop.Value - transaction.MeterStart);
            }

            reservation.ActualEnergyKwh = actualEnergy;
            reservation.UpdatedAtUtc = _utcNow();

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                _logger.LogWarning("Stripe/Complete => Missing payment intent reservation={ReservationId} tx={TransactionId}", reservation.ReservationId, transaction.TransactionId);
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = "Missing Stripe payment intent during capture.";
                dbContext.SaveChanges();
                return;
            }

            var energyCostCents = CalculateAmountInCents(actualEnergy, reservation.PricePerKwh);
            var sessionFeeCents = CalculateFlatAmountInCents(reservation.UserSessionFee);
            var usageFeeMinutes = CalculateUsageFeeMinutes(transaction, reservation, _utcNow());
            var usageFeeCents = usageFeeMinutes > 0
                ? CalculateUsageFeeInCents(usageFeeMinutes, reservation.UsageFeePerMinute)
                : 0L;

            var amountToCapture = energyCostCents + usageFeeCents + sessionFeeCents;

            _logger.LogInformation(
                "Stripe/Complete => Calculated amounts reservation={ReservationId} tx={TransactionId} energyKwh={EnergyKwh:0.###} energyCents={EnergyCents} usageMinutes={UsageMinutes} usageCents={UsageCents} sessionFeeCents={SessionFeeCents} amountToCapture={AmountToCapture}",
                reservation.ReservationId,
                transaction.TransactionId,
                actualEnergy,
                energyCostCents,
                usageFeeMinutes,
                usageFeeCents,
                sessionFeeCents,
                amountToCapture);

            try
            {
                var paymentIntent = _paymentIntentService.Get(reservation.StripePaymentIntentId);

                if (paymentIntent.Status == "requires_capture")
                {
                    if (amountToCapture > 0)
                    {
                        var paymentIntentAmount = (long?)paymentIntent.Amount;
                        var maxCaptureAmount = paymentIntentAmount ?? amountToCapture;
                        var captureOptions = new PaymentIntentCaptureOptions
                        {
                            AmountToCapture = Math.Min(amountToCapture, maxCaptureAmount)
                        };
                        var captured = _paymentIntentService.Capture(
                            paymentIntent.Id,
                            captureOptions,
                            BuildIdempotencyOptions(IdempotencyCapture, reservation.ReservationId, captureOptions.AmountToCapture));
                        reservation.CapturedAmountCents = captureOptions.AmountToCapture;
                        reservation.CapturedAtUtc = _utcNow();
                        reservation.Status = PaymentReservationStatus.Completed;

                        _logger.LogInformation(
                            "Stripe/Complete => Captured payment reservation={ReservationId} paymentIntent={PaymentIntentId} capturedCents={CapturedCents} status={PaymentIntentStatus}",
                            reservation.ReservationId,
                            paymentIntent.Id,
                            captureOptions.AmountToCapture,
                            captured.Status);
                    }
                    else
                    {
                        _paymentIntentService.Cancel(
                            paymentIntent.Id,
                            BuildIdempotencyOptions(IdempotencyCancel, reservation.ReservationId));
                        reservation.Status = PaymentReservationStatus.Cancelled;

                        _logger.LogInformation(
                            "Stripe/Complete => Cancelled zero-amount capture reservation={ReservationId} paymentIntent={PaymentIntentId}",
                            reservation.ReservationId,
                            paymentIntent.Id);
                    }
                }
                else if (paymentIntent.Status == "succeeded")
                {
                    reservation.CapturedAmountCents = paymentIntent.AmountReceived;
                    reservation.CapturedAtUtc = _utcNow();
                    reservation.Status = PaymentReservationStatus.Completed;

                    _logger.LogInformation(
                        "Stripe/Complete => Payment already succeeded reservation={ReservationId} paymentIntent={PaymentIntentId} amountReceived={AmountReceived}",
                        reservation.ReservationId,
                        paymentIntent.Id,
                        paymentIntent.AmountReceived);
                }
                else
                {
                    reservation.Status = PaymentReservationStatus.Failed;
                    reservation.LastError = $"Unexpected PaymentIntent status '{paymentIntent.Status}' during capture.";

                    _logger.LogWarning(
                        "Stripe/Complete => Unexpected PaymentIntent status reservation={ReservationId} paymentIntent={PaymentIntentId} status={PaymentIntentStatus}",
                        reservation.ReservationId,
                        paymentIntent.Id,
                        paymentIntent.Status);
                }

                dbContext.SaveChanges();

                PersistTransactionBreakdown(dbContext, transaction, reservation, actualEnergy, energyCostCents, usageFeeMinutes, usageFeeCents, sessionFeeCents, amountToCapture);
                TryHandleInvoiceIntegration(dbContext, reservation, transaction);
                TrySendCompletionNotifications(dbContext, reservation, transaction);
            }
            catch (StripeException sex)
            {
                _logger.LogError(sex, "Stripe capture failed for reservation {ReservationId}", reservation.ReservationId);
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = sex.Message;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
            }
        }

        private void TrySendR1RequestedNotification(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (_emailNotificationService == null || reservation == null)
            {
                return;
            }

            var session = TryGetCheckoutSession(reservation.StripeCheckoutSessionId, reservation.ReservationId, "R1Requested");
            if (!IsR1InvoiceRequested(session))
            {
                return;
            }

            var recipientEmail = session?.CustomerDetails?.Email;
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogDebug("Stripe/Notify => R1 requested email skipped due to missing recipient reservation={ReservationId}", reservation.ReservationId);
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
                    GetMetadataValue(session, "buyer_company"),
                    GetMetadataValue(session, "buyer_oib"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stripe/Notify => Failed to send R1 requested email reservation={ReservationId}", reservation.ReservationId);
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

            var session = TryGetCheckoutSession(reservation.StripeCheckoutSessionId, reservation.ReservationId, "Completion");
            var recipientEmail = session?.CustomerDetails?.Email;
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogDebug("Stripe/Notify => Completion emails skipped due to missing recipient reservation={ReservationId}", reservation.ReservationId);
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

                if (IsR1InvoiceRequested(session))
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
                _logger.LogWarning(ex, "Stripe/Notify => Failed sending completion emails reservation={ReservationId}", reservation.ReservationId);
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
                var session = TryGetCheckoutSession(reservation.StripeCheckoutSessionId, reservation.ReservationId, "InvoiceIntegration");
                _invoiceIntegrationService.HandleCompletedReservation(dbContext, reservation, transaction, session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invoice/Integration => Failed to prepare invoice draft reservation={ReservationId}", reservation.ReservationId);
            }
        }

        private Session TryGetCheckoutSession(string checkoutSessionId, Guid reservationId, string reason)
        {
            if (string.IsNullOrWhiteSpace(checkoutSessionId))
            {
                return null;
            }

            try
            {
                return _sessionService.Get(checkoutSessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Stripe/Notify => Unable to load checkout session reservation={ReservationId} sessionId={SessionId} reason={Reason}",
                    reservationId,
                    checkoutSessionId,
                    reason);
                return null;
            }
        }

        private static bool IsR1InvoiceRequested(Session session)
        {
            var invoiceType = GetMetadataValue(session, "invoice_type");
            return string.Equals(invoiceType, "R1", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMetadataValue(Session session, string key)
        {
            if (session?.Metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (session.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return null;
        }

        private string BuildStatusUrl(Guid reservationId)
        {
            if (reservationId == Guid.Empty || string.IsNullOrWhiteSpace(_options?.ReturnBaseUrl))
            {
                return null;
            }

            return $"{TrimTrailingSlash(_options.ReturnBaseUrl)}/Payments/Status?reservationId={reservationId}&origin=public";
        }

        private int CalculateUsageFeeMinutes(Transaction transaction, ChargePaymentReservation reservation, DateTime? nowUtc = null)
        {
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (reservation.UsageFeePerMinute <= 0 || transaction.StartTime == default)
            {
                return 0;
            }

            bool usageAfterChargingEnds = reservation.UsageFeeAnchorMinutes == 1;
            var stopTimeUtc = transaction.StopTime ?? nowUtc ?? DateTime.UtcNow;
            DateTime? anchorStartUtc = transaction.StartTime;

            if (usageAfterChargingEnds)
            {
                anchorStartUtc = transaction.ChargingEndedAtUtc;
            }

            if (!anchorStartUtc.HasValue || stopTimeUtc <= anchorStartUtc.Value)
            {
                return 0;
            }

            int totalMinutes = 0;
            if (usageAfterChargingEnds &&
                TryParseDailyWindow(_flowOptions?.IdleFeeExcludedWindow, out var excludedStart, out var excludedEnd) &&
                TryResolveTimeZone(_flowOptions?.IdleFeeExcludedTimeZoneId, out var tz))
            {
                totalMinutes = CalculateChargeableMinutesExcludingWindow(anchorStartUtc.Value, stopTimeUtc, tz, excludedStart, excludedEnd);
            }
            else
            {
                totalMinutes = Math.Max(0, (int)Math.Ceiling((stopTimeUtc - anchorStartUtc.Value).TotalMinutes));
            }

            return Math.Min(
                Math.Max(0, totalMinutes - reservation.StartUsageFeeAfterMinutes),
                reservation.MaxUsageFeeMinutes);
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

        public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
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
                        return;
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
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stripe webhook validation failed: {Message}", ex.Message);
                return;
            }

            if (HasProcessedWebhookEvent(dbContext, stripeEvent.Id))
            {
                _logger.LogInformation("Stripe/Webhook => Duplicate event ignored eventId={EventId} type={Type}", stripeEvent.Id, stripeEvent.Type);
                return;
            }

            _logger.LogInformation("Stripe/Webhook => Processing eventId={EventId} type={Type}", stripeEvent.Id, stripeEvent.Type);

            Guid? linkedReservationId = null;
            switch (stripeEvent.Type)
            {
                case EventTypes.CheckoutSessionCompleted:
                    linkedReservationId = HandleCheckoutCompleted(dbContext, stripeEvent);
                    break;
                case EventTypes.CheckoutSessionExpired:
                    linkedReservationId = HandleCheckoutExpired(dbContext, stripeEvent);
                    break;
                case EventTypes.PaymentIntentPaymentFailed:
                    linkedReservationId = HandlePaymentFailed(dbContext, stripeEvent);
                    break;
                default:
                    _logger.LogDebug("Unhandled Stripe event type: {Type}", stripeEvent.Type);
                    break;
            }

            MarkWebhookEventProcessed(dbContext, stripeEvent, linkedReservationId);
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

        private Guid? HandleCheckoutCompleted(OCPPCoreContext dbContext, Event stripeEvent)
        {
            var session = stripeEvent.Data.Object as Session;
            if (session == null) return null;

            _logger.LogInformation("Stripe/Webhook => Checkout session completed eventId={EventId} sessionId={SessionId} paymentStatus={PaymentStatus}", stripeEvent.Id, session.Id, session.PaymentStatus);

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripeCheckoutSessionId == session.Id);

            if (reservation == null)
            {
                _logger.LogWarning("Stripe/Webhook => Checkout completed but reservation not found sessionId={SessionId}", session.Id);
                return null;
            }

            reservation.StripePaymentIntentId = reservation.StripePaymentIntentId ?? session.PaymentIntentId;
            if (reservation.Status == PaymentReservationStatus.Pending &&
                string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                reservation.Status = PaymentReservationStatus.Authorized;
                reservation.AuthorizedAtUtc = _utcNow();
                reservation.StartDeadlineAtUtc = reservation.AuthorizedAtUtc.Value.AddMinutes(Math.Max(1, _flowOptions.StartWindowMinutes));
                ClearFailureState(reservation);
                if (string.IsNullOrWhiteSpace(reservation.OcppIdTag))
                {
                    reservation.OcppIdTag = GenerateOcppIdTag();
                }
                EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
            }

            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();
            return reservation.ReservationId;
        }

        private Guid? HandleCheckoutExpired(OCPPCoreContext dbContext, Event stripeEvent)
        {
            var session = stripeEvent.Data.Object as Session;
            if (session == null) return null;

            _logger.LogInformation("Stripe/Webhook => Checkout session expired eventId={EventId} sessionId={SessionId}", stripeEvent.Id, session.Id);

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripeCheckoutSessionId == session.Id);

            if (reservation == null)
            {
                _logger.LogWarning("Stripe/Webhook => Checkout expired but reservation not found sessionId={SessionId}", session.Id);
                return null;
            }
            if (reservation.Status != PaymentReservationStatus.Pending &&
                reservation.Status != PaymentReservationStatus.Authorized)
            {
                return reservation.ReservationId;
            }

            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.LastError = "Checkout session expired";
            reservation.FailureCode = "CheckoutExpired";
            reservation.FailureMessage = reservation.LastError;
            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();
            return reservation.ReservationId;
        }

        private Guid? HandlePaymentFailed(OCPPCoreContext dbContext, Event stripeEvent)
        {
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent == null) return null;

            _logger.LogInformation("Stripe/Webhook => Payment failed eventId={EventId} paymentIntent={PaymentIntentId} code={ErrorCode} message={ErrorMessage}",
                stripeEvent.Id,
                paymentIntent.Id,
                paymentIntent.LastPaymentError?.Code ?? "(none)",
                paymentIntent.LastPaymentError?.Message ?? "(none)");

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripePaymentIntentId == paymentIntent.Id);

            if (reservation == null)
            {
                _logger.LogWarning("Stripe/Webhook => Payment failed but reservation not found paymentIntent={PaymentIntentId}", paymentIntent.Id);
                return null;
            }

            if (IsTerminalReservationStatus(reservation.Status))
            {
                _logger.LogInformation("Stripe/Webhook => Payment failed ignored for terminal reservation={ReservationId} status={Status}",
                    reservation.ReservationId,
                    reservation.Status ?? "(null)");
                return reservation.ReservationId;
            }

            reservation.Status = PaymentReservationStatus.Failed;
            reservation.LastError = paymentIntent.LastPaymentError?.Message ?? "Payment failed.";
            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();
            return reservation.ReservationId;
        }

        private ChargePaymentReservation FindReservationForTransaction(OCPPCoreContext dbContext, Transaction transaction)
        {
            ChargePaymentReservation reservation = null;
            if (transaction.TransactionId > 0)
            {
                reservation = dbContext.ChargePaymentReservations
                    .FirstOrDefault(r => r.TransactionId == transaction.TransactionId);
            }

            if (reservation != null) return reservation;

            reservation = dbContext.ChargePaymentReservations
                .Where(r =>
                    r.TransactionId == null &&
                    r.ChargePointId == transaction.ChargePointId &&
                    r.ConnectorId == transaction.ConnectorId &&
                    (r.OcppIdTag == transaction.StartTagId || r.ChargeTagId == transaction.StartTagId))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();

            if (reservation != null)
            {
                reservation.TransactionId = transaction.TransactionId;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
            }

            return reservation;
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
            dbContext.SaveChanges();
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

        private static void PersistTransactionBreakdown(
            OCPPCoreContext dbContext,
            Transaction transaction,
            ChargePaymentReservation reservation,
            double energyKwh,
            long energyCostCents,
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

        private RequestOptions BuildIdempotencyOptions(string purpose, Guid reservationId, long? amount = null)
        {
            if (string.IsNullOrWhiteSpace(purpose) || reservationId == Guid.Empty) return null;
            var key = amount.HasValue
                ? $"{purpose}:{reservationId}:{amount.Value}"
                : $"{purpose}:{reservationId}";
            return new RequestOptions { IdempotencyKey = key };
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

        private void MarkWebhookEventProcessed(OCPPCoreContext dbContext, Event stripeEvent, Guid? reservationId = null)
        {
            if (stripeEvent == null || string.IsNullOrWhiteSpace(stripeEvent.Id)) return;
            try
            {
                var processed = new StripeWebhookEvent
                {
                    EventId = stripeEvent.Id,
                    Type = stripeEvent.Type,
                    StripeCreatedAtUtc = stripeEvent.Created == default
                        ? (DateTime?)null
                        : stripeEvent.Created.ToUniversalTime(),
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
    }

    internal interface IStripeSessionService
    {
        Session Create(SessionCreateOptions options, RequestOptions requestOptions = null);
        Session Get(string id);
        Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null);
    }

    internal interface IStripePaymentIntentService
    {
        PaymentIntent Get(string id);
        PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null);
        PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null);
        void Cancel(string id, RequestOptions requestOptions = null);
    }

    internal interface IStripeEventFactory
    {
        Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true);
    }

    internal class StripeSessionServiceWrapper : IStripeSessionService
    {
        private readonly SessionService _inner = new SessionService();

        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null) => _inner.Create(options, requestOptions);

        public Session Get(string id) => _inner.Get(id);

        public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null) =>
            _inner.Update(id, options, requestOptions);
    }

    internal class StripePaymentIntentServiceWrapper : IStripePaymentIntentService
    {
        private readonly PaymentIntentService _inner = new PaymentIntentService();

        public PaymentIntent Get(string id) => _inner.Get(id);

        public PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null) =>
            _inner.Update(id, options, requestOptions);

        public PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null) => _inner.Capture(id, options, requestOptions);

        public void Cancel(string id, RequestOptions requestOptions = null) =>
            _inner.Cancel(id, options: null, requestOptions: requestOptions);
    }

    internal class StripeEventFactoryWrapper : IStripeEventFactory
    {
        public Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true) =>
            EventUtility.ConstructEvent(payload, signatureHeader, webhookSecret, throwOnApiVersionMismatch: throwOnApiVersionMismatch);
    }
}
