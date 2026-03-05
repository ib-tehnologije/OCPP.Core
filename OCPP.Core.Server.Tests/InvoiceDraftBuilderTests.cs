using System;
using System.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class InvoiceDraftBuilderTests
    {
        [Fact]
        public void Build_CreatesExpectedLines_ForCompletedChargingSession()
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-1",
                ConnectorId = 2,
                PricePerKwh = 0.30m,
                UserSessionFee = 0.50m,
                UsageFeePerMinute = 0.20m,
                UsageFeeAnchorMinutes = 1,
                Currency = "eur",
                CapturedAmountCents = 765
            };

            var transaction = new Transaction
            {
                TransactionId = 42,
                StartTime = new DateTime(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 3, 5, 11, 0, 0, DateTimeKind.Utc),
                EnergyKwh = 12.5,
                EnergyCost = 3.75m,
                UserSessionFeeAmount = 0.50m,
                UsageFeeMinutes = 20,
                UsageFeeAmount = 3.50m,
                IdleUsageFeeMinutes = 15,
                IdleUsageFeeAmount = 3.00m
            };

            var session = new Session
            {
                CustomerDetails = new SessionCustomerDetails { Email = "billing@example.com" },
                Metadata = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["invoice_type"] = "R1",
                    ["buyer_company"] = "Acme d.o.o.",
                    ["buyer_oib"] = "12345678901"
                }
            };

            var builder = new InvoiceDraftBuilder();
            var draft = builder.Build(reservation, transaction, session);

            Assert.Equal("R1", draft.InvoiceKind);
            Assert.Equal("EUR", draft.Currency);
            Assert.Equal("Acme d.o.o.", draft.BuyerCompanyName);
            Assert.Equal("12345678901", draft.BuyerOib);
            Assert.Equal("billing@example.com", draft.BuyerEmail);
            Assert.Equal(3, draft.Lines.Count);

            var energyLine = draft.Lines.Single(l => l.Type == "Energy");
            Assert.Equal(12.5m, energyLine.Quantity);
            Assert.Equal(0.30m, energyLine.UnitPrice);
            Assert.Equal(3.75m, energyLine.LineAmount);

            var sessionFeeLine = draft.Lines.Single(l => l.Type == "SessionFee");
            Assert.Equal(1m, sessionFeeLine.Quantity);
            Assert.Equal(0.50m, sessionFeeLine.LineAmount);

            var idleLine = draft.Lines.Single(l => l.Type == "IdleFee");
            Assert.Equal(15m, idleLine.Quantity);
            Assert.Equal(3.00m, idleLine.LineAmount);

            Assert.Equal(7.75m, draft.TotalAmount);
        }

        [Fact]
        public void Build_UsesRetailKind_WhenR1WasNotRequested()
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-2",
                ConnectorId = 1,
                PricePerKwh = 0.40m,
                Currency = "EUR"
            };

            var transaction = new Transaction
            {
                TransactionId = 7,
                StartTime = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 3, 5, 12, 30, 0, DateTimeKind.Utc),
                EnergyKwh = 5,
                EnergyCost = 2.00m
            };

            var builder = new InvoiceDraftBuilder();
            var draft = builder.Build(reservation, transaction, checkoutSession: null);

            Assert.Equal("Retail", draft.InvoiceKind);
            Assert.Single(draft.Lines);
            Assert.Equal(2.00m, draft.TotalAmount);
        }

        [Fact]
        public void Build_UsesUsageFeeLine_WhenTimeFeeIsNotIdleAnchored()
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-3",
                ConnectorId = 3,
                PricePerKwh = 0.35m,
                UsageFeePerMinute = 0.10m,
                UsageFeeAnchorMinutes = 0,
                Currency = "eur"
            };

            var transaction = new Transaction
            {
                TransactionId = 9,
                StartTime = new DateTime(2026, 3, 5, 13, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc),
                EnergyKwh = 6,
                EnergyCost = 2.10m,
                UsageFeeMinutes = 12,
                UsageFeeAmount = 1.20m,
                IdleUsageFeeMinutes = 0,
                IdleUsageFeeAmount = 0m
            };

            var builder = new InvoiceDraftBuilder();
            var draft = builder.Build(reservation, transaction, checkoutSession: null);

            var usageLine = draft.Lines.Single(l => l.Type == "UsageFee");
            Assert.Equal("Occupancy fee", usageLine.Description);
            Assert.Equal(12m, usageLine.Quantity);
            Assert.Equal(0.10m, usageLine.UnitPrice);
            Assert.Equal(1.20m, usageLine.LineAmount);
            Assert.Equal(3.30m, draft.TotalAmount);
        }
    }
}
