/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OCPP.Core.Database;
using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    public class StripePaymentCoordinator : IPaymentCoordinator
    {
        private readonly StripeOptions _options;
        private readonly ILogger<StripePaymentCoordinator> _logger;
        private readonly SessionService _sessionService;
        private readonly PaymentIntentService _paymentIntentService;

        public bool IsEnabled =>
            _options.Enabled &&
            !string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.ReturnBaseUrl);

        public StripePaymentCoordinator(IOptions<StripeOptions> options, ILogger<StripePaymentCoordinator> logger)
        {
            _options = options?.Value ?? new StripeOptions();
            _logger = logger;

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                StripeConfiguration.ApiKey = _options.ApiKey;
            }

            _sessionService = new SessionService();
            _paymentIntentService = new PaymentIntentService();
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
            int startUsageFeeAfterMinutes = Math.Max(0, chargePoint.StartUsageFeeAfterMinutes);
            int maxUsageFeeMinutes = Math.Max(0, chargePoint.MaxUsageFeeMinutes);
            decimal usageFeePerMinute = chargePoint.ConnectorUsageFeePerMinute;

            if ((maxEnergyKwh <= 0 && usageFeePerMinute <= 0) || (pricePerKwh <= 0 && usageFeePerMinute <= 0))
            {
                throw new InvalidOperationException("Pricing is not configured for this charge point.");
            }

            var now = DateTime.UtcNow;
            var normalizedTag = NormalizeChargeTag(request.ChargeTagId);

            EnsureChargeTagExists(dbContext, normalizedTag);

            var maxEnergyCents = CalculateAmountInCents(maxEnergyKwh, pricePerKwh);
            var maxUsageFeeCents = CalculateUsageFeeInCents(
                Math.Max(0, maxUsageFeeMinutes - startUsageFeeAfterMinutes),
                usageFeePerMinute);
            var maxTotalCents = maxEnergyCents + maxUsageFeeCents;

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
                UsageFeePerMinute = usageFeePerMinute,
                StartUsageFeeAfterMinutes = startUsageFeeAfterMinutes,
                MaxUsageFeeMinutes = maxUsageFeeMinutes,
                MaxAmountCents = maxTotalCents,
                Currency = string.IsNullOrWhiteSpace(_options.Currency) ? "eur" : _options.Currency.ToLowerInvariant(),
                Status = PaymentReservationStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

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

            var sessionOptions = new SessionCreateOptions
            {
                Mode = "payment",
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    CaptureMethod = "manual",
                    Metadata = new Dictionary<string, string>
                    {
                        ["reservation_id"] = reservation.ReservationId.ToString(),
                        ["charge_point_id"] = reservation.ChargePointId,
                        ["connector_id"] = reservation.ConnectorId.ToString(),
                        ["charge_tag_id"] = reservation.ChargeTagId
                    }
                },
                Metadata = new Dictionary<string, string>
                {
                    ["reservation_id"] = reservation.ReservationId.ToString(),
                    ["charge_point_id"] = reservation.ChargePointId,
                    ["connector_id"] = reservation.ConnectorId.ToString(),
                    ["charge_tag_id"] = reservation.ChargeTagId
                },
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
                var session = _sessionService.Create(sessionOptions);

                reservation.StripeCheckoutSessionId = session.Id;
                reservation.StripePaymentIntentId = session.PaymentIntentId;
                reservation.UpdatedAtUtc = DateTime.UtcNow;

                dbContext.Add(reservation);
                dbContext.SaveChanges();

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
                return result;
            }

            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null)
            {
                result.Error = "Reservation not found.";
                result.Status = "NotFound";
                return result;
            }

            result.Reservation = reservation;

            if (!string.Equals(reservation.StripeCheckoutSessionId, checkoutSessionId, StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "Checkout session mismatch.";
                result.Status = "SessionMismatch";
                return result;
            }

            try
            {
                var session = _sessionService.Get(checkoutSessionId);
                if (!string.Equals(session.Status, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = $"Checkout session not completed (status={session.Status}).";
                    result.Status = session.Status;
                    return result;
                }

                var paymentIntentId = session.PaymentIntentId;
                if (string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    result.Error = "PaymentIntent missing in session.";
                    result.Status = "MissingPaymentIntent";
                    return result;
                }

                var paymentIntent = _paymentIntentService.Get(paymentIntentId);

                if (!string.Equals(paymentIntent.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(paymentIntent.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = $"Unexpected PaymentIntent status '{paymentIntent.Status}'.";
                    result.Status = paymentIntent.Status;
                    reservation.Status = PaymentReservationStatus.Failed;
                    reservation.LastError = result.Error;
                    reservation.UpdatedAtUtc = DateTime.UtcNow;
                    dbContext.SaveChanges();
                    return result;
                }

                reservation.StripePaymentIntentId = paymentIntentId;
                reservation.Status = PaymentReservationStatus.Authorized;
                reservation.AuthorizedAtUtc = DateTime.UtcNow;
                reservation.UpdatedAtUtc = reservation.AuthorizedAtUtc.Value;
                var paymentIntentAmount = (long?)paymentIntent.Amount;
                reservation.MaxAmountCents = paymentIntentAmount ?? reservation.MaxAmountCents;

                dbContext.SaveChanges();

                result.Success = true;
                result.Status = PaymentReservationStatus.Authorized;
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
                reservation.UpdatedAtUtc = DateTime.UtcNow;
                dbContext.SaveChanges();
                return result;
            }
        }

        public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));

            var reservation = dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
                {
                    _paymentIntentService.Cancel(reservation.StripePaymentIntentId);
                }
            }
            catch (StripeException sex)
            {
                _logger.LogWarning(sex, "Unable to cancel payment intent {PaymentIntent}", reservation.StripePaymentIntentId);
                reservation.LastError = sex.Message;
            }
            finally
            {
                reservation.Status = PaymentReservationStatus.Cancelled;
                reservation.UpdatedAtUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reservation.LastError = reason;
                }
                dbContext.SaveChanges();
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
                    r.ChargeTagId == normalizedTag &&
                    (r.Status == PaymentReservationStatus.Authorized || r.Status == PaymentReservationStatus.StartRequested))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();

            if (reservation == null) return;

            reservation.TransactionId = transactionId;
            reservation.Status = PaymentReservationStatus.Charging;
            reservation.UpdatedAtUtc = DateTime.UtcNow;
            dbContext.SaveChanges();
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

            double actualEnergy = 0;
            if (transaction.MeterStop.HasValue)
            {
                actualEnergy = Math.Max(0, transaction.MeterStop.Value - transaction.MeterStart);
            }

            reservation.ActualEnergyKwh = actualEnergy;
            reservation.UpdatedAtUtc = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = "Missing Stripe payment intent during capture.";
                dbContext.SaveChanges();
                return;
            }

            var amountToCapture = CalculateAmountInCents(actualEnergy, reservation.PricePerKwh);

            if (reservation.UsageFeePerMinute > 0 && transaction.StartTime != default)
            {
                var stopTime = transaction.StopTime ?? DateTime.UtcNow;
                var totalMinutes = Math.Max(0, (int)Math.Ceiling((stopTime - transaction.StartTime).TotalMinutes));
                var billableMinutes = Math.Min(
                    Math.Max(0, totalMinutes - reservation.StartUsageFeeAfterMinutes),
                    reservation.MaxUsageFeeMinutes);
                if (billableMinutes > 0)
                {
                    amountToCapture += CalculateUsageFeeInCents(billableMinutes, reservation.UsageFeePerMinute);
                }
            }

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
                        var captured = _paymentIntentService.Capture(paymentIntent.Id, captureOptions);
                        reservation.CapturedAmountCents = captureOptions.AmountToCapture;
                        reservation.CapturedAtUtc = DateTime.UtcNow;
                        reservation.Status = PaymentReservationStatus.Completed;
                    }
                    else
                    {
                        _paymentIntentService.Cancel(paymentIntent.Id);
                        reservation.Status = PaymentReservationStatus.Cancelled;
                    }
                }
                else if (paymentIntent.Status == "succeeded")
                {
                    reservation.CapturedAmountCents = paymentIntent.AmountReceived;
                    reservation.CapturedAtUtc = DateTime.UtcNow;
                    reservation.Status = PaymentReservationStatus.Completed;
                }
                else
                {
                    reservation.Status = PaymentReservationStatus.Failed;
                    reservation.LastError = $"Unexpected PaymentIntent status '{paymentIntent.Status}' during capture.";
                }

                dbContext.SaveChanges();
            }
            catch (StripeException sex)
            {
                _logger.LogError(sex, "Stripe capture failed for reservation {ReservationId}", reservation.ReservationId);
                reservation.Status = PaymentReservationStatus.Failed;
                reservation.LastError = sex.Message;
                reservation.UpdatedAtUtc = DateTime.UtcNow;
                dbContext.SaveChanges();
            }
        }

        public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader)
        {
            if (!IsEnabled) return;
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            {
                _logger.LogWarning("Stripe webhook secret is not configured; webhook ignored.");
                return;
            }

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, _options.WebhookSecret);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stripe webhook validation failed: {Message}", ex.Message);
                return;
            }

            switch (stripeEvent.Type)
            {
                case Events.CheckoutSessionCompleted:
                    HandleCheckoutCompleted(dbContext, stripeEvent);
                    break;
                case Events.PaymentIntentPaymentFailed:
                    HandlePaymentFailed(dbContext, stripeEvent);
                    break;
                default:
                    _logger.LogDebug("Unhandled Stripe event type: {Type}", stripeEvent.Type);
                    break;
            }
        }

        private void HandleCheckoutCompleted(OCPPCoreContext dbContext, Event stripeEvent)
        {
            var session = stripeEvent.Data.Object as Session;
            if (session == null) return;

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripeCheckoutSessionId == session.Id);

            if (reservation == null) return;

            reservation.StripePaymentIntentId = reservation.StripePaymentIntentId ?? session.PaymentIntentId;
            if (reservation.Status == PaymentReservationStatus.Pending &&
                string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                reservation.Status = PaymentReservationStatus.Authorized;
                reservation.AuthorizedAtUtc = DateTime.UtcNow;
            }

            reservation.UpdatedAtUtc = DateTime.UtcNow;
            dbContext.SaveChanges();
        }

        private void HandlePaymentFailed(OCPPCoreContext dbContext, Event stripeEvent)
        {
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent == null) return;

            var reservation = dbContext.ChargePaymentReservations
                .FirstOrDefault(r => r.StripePaymentIntentId == paymentIntent.Id);

            if (reservation == null) return;

            reservation.Status = PaymentReservationStatus.Failed;
            reservation.LastError = paymentIntent.LastPaymentError?.Message ?? "Payment failed.";
            reservation.UpdatedAtUtc = DateTime.UtcNow;
            dbContext.SaveChanges();
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
                    r.ChargeTagId == transaction.StartTagId)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();

            if (reservation != null)
            {
                reservation.TransactionId = transaction.TransactionId;
                reservation.UpdatedAtUtc = DateTime.UtcNow;
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
    }
}
