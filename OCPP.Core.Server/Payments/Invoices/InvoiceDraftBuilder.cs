using System;
using System.Globalization;
using Stripe.Checkout;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Invoices
{
    public interface IInvoiceDraftBuilder
    {
        InvoiceDraft Build(ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession);
    }

    public class InvoiceDraftBuilder : IInvoiceDraftBuilder
    {
        public InvoiceDraft Build(ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession)
        {
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var draft = new InvoiceDraft
            {
                ReservationId = reservation.ReservationId,
                TransactionId = transaction.TransactionId,
                InvoiceKind = IsR1Requested(checkoutSession) ? "R1" : "Retail",
                IssueDateUtc = reservation.CapturedAtUtc ?? transaction.StopTime ?? reservation.UpdatedAtUtc,
                ServiceDateFromUtc = transaction.StartTime,
                ServiceDateToUtc = transaction.StopTime,
                Currency = NormalizeCurrency(reservation.Currency ?? transaction.Currency),
                BuyerCompanyName = GetMetadataValue(checkoutSession, "buyer_company"),
                BuyerPersonalName = checkoutSession?.CustomerDetails?.Name?.Trim(),
                BuyerOib = GetMetadataValue(checkoutSession, "buyer_oib"),
                BuyerEmail = checkoutSession?.CustomerDetails?.Email?.Trim(),
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StripeCheckoutSessionId = reservation.StripeCheckoutSessionId,
                StripePaymentIntentId = reservation.StripePaymentIntentId,
                CapturedAmountCents = reservation.CapturedAmountCents
            };

            AddEnergyLine(draft, reservation, transaction);
            AddSessionFeeLine(draft, reservation, transaction);
            AddTimeFeeLine(draft, reservation, transaction);

            draft.TotalAmount = transaction.EnergyCost +
                                transaction.UserSessionFeeAmount +
                                transaction.UsageFeeAmount;

            return draft;
        }

        private static void AddEnergyLine(InvoiceDraft draft, ChargePaymentReservation reservation, Transaction transaction)
        {
            if (transaction.EnergyKwh <= 0 || transaction.EnergyCost <= 0)
            {
                return;
            }

            draft.Lines.Add(new InvoiceDraftLine
            {
                Type = "Energy",
                Description = "Charging energy",
                Quantity = Convert.ToDecimal(transaction.EnergyKwh, CultureInfo.InvariantCulture),
                UnitCode = "kWh",
                UnitPrice = reservation.PricePerKwh,
                LineAmount = transaction.EnergyCost
            });
        }

        private static void AddSessionFeeLine(InvoiceDraft draft, ChargePaymentReservation reservation, Transaction transaction)
        {
            if (transaction.UserSessionFeeAmount <= 0)
            {
                return;
            }

            draft.Lines.Add(new InvoiceDraftLine
            {
                Type = "SessionFee",
                Description = "Session fee",
                Quantity = 1m,
                UnitCode = "H87",
                UnitPrice = reservation.UserSessionFee,
                LineAmount = transaction.UserSessionFeeAmount
            });
        }

        private static void AddTimeFeeLine(InvoiceDraft draft, ChargePaymentReservation reservation, Transaction transaction)
        {
            if (transaction.UsageFeeAmount <= 0)
            {
                return;
            }

            var useIdleLine = transaction.IdleUsageFeeAmount > 0 || reservation.UsageFeeAnchorMinutes == 1;
            var quantity = useIdleLine ? transaction.IdleUsageFeeMinutes : transaction.UsageFeeMinutes;
            var amount = useIdleLine ? transaction.IdleUsageFeeAmount : transaction.UsageFeeAmount;
            var description = useIdleLine ? "Idle / occupancy fee" : "Occupancy fee";

            if (quantity <= 0 || amount <= 0)
            {
                return;
            }

            draft.Lines.Add(new InvoiceDraftLine
            {
                Type = useIdleLine ? "IdleFee" : "UsageFee",
                Description = description,
                Quantity = quantity,
                UnitCode = "MIN",
                UnitPrice = reservation.UsageFeePerMinute,
                LineAmount = amount
            });
        }

        private static bool IsR1Requested(Session checkoutSession)
        {
            var invoiceType = GetMetadataValue(checkoutSession, "invoice_type");
            return string.Equals(invoiceType, "R1", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMetadataValue(Session checkoutSession, string key)
        {
            if (checkoutSession?.Metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (checkoutSession.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return null;
        }

        private static string NormalizeCurrency(string currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
            {
                return "EUR";
            }

            return currency.Trim().ToUpperInvariant();
        }
    }
}
