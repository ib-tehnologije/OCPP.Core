using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Invoices;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class NightEnergyTariffTests
    {
        private const string Zagreb = "Europe/Zagreb";

        private static NightTariffWindow Window() =>
            NightTariffWindow.TryCreate(NightTariffWindow.DefaultStartMinute, NightTariffWindow.DefaultEndMinute, Zagreb)!;

        // July is CEST (UTC+2): 22:00 local = 20:00Z, 07:00 local = 05:00Z.
        private static DateTime Utc(int month, int day, int hour, int minute = 0, int second = 0) =>
            new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

        [Fact]
        public void Window_IsHalfOpen_StartIsNightEndIsDay()
        {
            var window = Window();

            Assert.False(window.IsNight(Utc(7, 20, 19, 59, 59)));
            Assert.True(window.IsNight(Utc(7, 20, 20, 0, 0)));
            Assert.True(window.IsNight(Utc(7, 21, 4, 59, 59)));
            Assert.False(window.IsNight(Utc(7, 21, 5, 0, 0)));
        }

        [Fact]
        public void NightDuration_OrdinaryNight_IsNineHours()
        {
            Assert.Equal(TimeSpan.FromHours(9), Window().NightDuration(Utc(7, 20, 12), Utc(7, 21, 12)));
        }

        [Fact]
        public void NightDuration_DaylightSavingNights_FollowTheLocalClock()
        {
            var window = Window();

            // Spring forward 2026-03-29: 22:00 CET (21:00Z) to 07:00 CEST (05:00Z) is 8 elapsed hours.
            Assert.Equal(TimeSpan.FromHours(8), window.NightDuration(Utc(3, 28, 12), Utc(3, 29, 12)));
            // Fall back 2026-10-25: 22:00 CEST (20:00Z) to 07:00 CET (06:00Z) is 10 elapsed hours.
            Assert.Equal(TimeSpan.FromHours(10), window.NightDuration(Utc(10, 24, 12), Utc(10, 25, 12)));
        }

        [Fact]
        public void NightDuration_TwoNights_CountsBothWindows()
        {
            // 21:00 local on day 1 to 08:00 local on day 3.
            Assert.Equal(TimeSpan.FromHours(18), Window().NightDuration(Utc(7, 20, 19), Utc(7, 22, 6)));
        }

        [Fact]
        public void NightShare_IntervalAcrossBoundary_IsProportionalToTime()
        {
            // 21:50 to 22:10 local: half of the interval is night.
            Assert.Equal(0.5d, Window().NightShare(Utc(7, 20, 19, 50), Utc(7, 20, 20, 10)), 6);
        }

        [Fact]
        public void NonOvernightWindow_IsSupported()
        {
            var window = NightTariffWindow.TryCreate(60, 5 * 60, Zagreb)!;

            Assert.Equal(TimeSpan.FromHours(4), window.NightDuration(Utc(7, 20, 12), Utc(7, 21, 12)));
            Assert.False(window.IsNight(Utc(7, 20, 22, 59)));
            Assert.True(window.IsNight(Utc(7, 20, 23, 0)));
        }

        [Theory]
        [InlineData(1320, 1320, Zagreb)]
        [InlineData(1320, 1440, Zagreb)]
        [InlineData(-1, 420, Zagreb)]
        [InlineData(1320, 420, "Not/A_Zone")]
        public void TryCreate_RejectsInvalidConfiguration(int start, int end, string timeZoneId)
        {
            Assert.Null(NightTariffWindow.TryCreate(start, end, timeZoneId));
        }

        [Fact]
        public void MeterEvidence_AccumulatesNightEnergyAcrossTheBoundary()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(Utc(7, 20, 19), withWindow: true);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            // 1 kWh per hour from 21:00 to 23:00 local, sampled at the boundary, then stopped.
            Process(db, transaction, "11", Utc(7, 20, 20));
            Process(db, transaction, "12", Utc(7, 20, 21));
            var terminal = Process(db, transaction, "12", Utc(7, 20, 21, 0, 30), terminal: true);

            Assert.Equal(MeterEvidenceOutcome.Accepted, terminal.Outcome);
            Assert.Equal(12d, transaction.MeterStop);
            Assert.Equal(1d, transaction.NightEnergyKwh, 6);
        }

        [Fact]
        public void MeterEvidence_MissingBoundarySample_SplitsTheIntervalByTime()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(Utc(7, 20, 19, 50), withWindow: true);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            Process(db, transaction, "12", Utc(7, 20, 20, 10));

            Assert.Equal(1d, transaction.NightEnergyKwh, 6);
        }

        [Fact]
        public void MeterEvidence_RejectedReading_DoesNotMoveNightEnergy()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(Utc(7, 20, 20), withWindow: true);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            Process(db, transaction, "11", Utc(7, 20, 21));
            var rejected = Process(db, transaction, "500", Utc(7, 20, 21, 1));

            Assert.Equal(MeterEvidenceOutcome.Rejected, rejected.Outcome);
            Assert.Equal(1d, transaction.NightEnergyKwh, 6);
        }

        [Fact]
        public void MeterEvidence_WithoutWindow_LeavesNightEnergyAtZero()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(Utc(7, 20, 20), withWindow: false);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            Process(db, transaction, "13", Utc(7, 20, 23));

            Assert.Equal(0d, transaction.NightEnergyKwh);
        }

        [Fact]
        public void Settlement_PricesDayAndNightEnergySeparately()
        {
            var (reservation, transaction) = SettledSession(nightKwh: 1, meterStop: 12);

            var result = FinancialSettlementCalculator.Calculate(reservation, transaction, new PaymentFlowOptions(), Utc(7, 20, 22));

            Assert.Equal(2d, result.ActualEnergyKwh);
            Assert.Equal(1d, result.NightEnergyKwh);
            Assert.Equal(25L, result.NightEnergyCostCents);
            Assert.Equal(65L, result.EnergyCostCents);
            Assert.Equal(65L, result.AmountToCaptureCents);
        }

        [Fact]
        public void Settlement_ClampsNightEnergyToDeliveredEnergy()
        {
            var (reservation, transaction) = SettledSession(nightKwh: 5, meterStop: 12);

            var result = FinancialSettlementCalculator.Calculate(reservation, transaction, new PaymentFlowOptions(), Utc(7, 20, 22));

            Assert.Equal(2d, result.NightEnergyKwh);
            Assert.Equal(50L, result.EnergyCostCents);
        }

        [Fact]
        public void Settlement_BelowMinimumEnergy_StaysNoCharge()
        {
            var (reservation, transaction) = SettledSession(nightKwh: 0.8, meterStop: 10.8);

            var result = FinancialSettlementCalculator.Calculate(reservation, transaction, new PaymentFlowOptions(), Utc(7, 20, 22));

            Assert.True(result.ShouldNoChargeForDeliveredEnergy);
            Assert.Equal(0L, result.EnergyCostCents);
            Assert.Equal(0L, result.NightEnergyCostCents);
        }

        [Fact]
        public void Settlement_ReservationWithoutNightPrice_UsesTheSinglePrice()
        {
            var (reservation, transaction) = SettledSession(nightKwh: 1, meterStop: 12);
            reservation.NightPricePerKwh = null;

            var result = FinancialSettlementCalculator.Calculate(reservation, transaction, new PaymentFlowOptions(), Utc(7, 20, 22));

            Assert.Equal(0d, result.NightEnergyKwh);
            Assert.Equal(80L, result.EnergyCostCents);
        }

        [Fact]
        public void InvoiceDraft_SplitsEnergyIntoDayAndNightLinesThatSumToEnergyCost()
        {
            var (reservation, transaction) = SettledSession(nightKwh: 1.25, meterStop: 13);
            // Settlement rounds each part: 1.75 kWh x 0.40 = 0.70 and 1.25 kWh x 0.25 = 0.3125 -> 0.31.
            transaction.EnergyKwh = 3;
            transaction.NightEnergyCost = 0.31m;
            transaction.EnergyCost = 1.01m;

            var draft = new InvoiceDraftBuilder().Build(reservation, transaction, null);

            var energy = draft.Lines.Where(line => line.Type.StartsWith("Energy", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, energy.Count);
            Assert.Equal("Energy", energy[0].Type);
            Assert.Equal(1.75m, energy[0].Quantity);
            Assert.Equal(0.40m, energy[0].UnitPrice);
            Assert.Equal(0.70m, energy[0].LineAmount);
            Assert.Equal("EnergyNight", energy[1].Type);
            Assert.Equal(1.25m, energy[1].Quantity);
            Assert.Equal(0.25m, energy[1].UnitPrice);
            Assert.Equal(0.31m, energy[1].LineAmount);
            Assert.Equal(transaction.EnergyCost, energy.Sum(line => line.LineAmount));
        }

        [Fact]
        public void InvoiceDraft_WithoutNightEnergy_KeepsASingleEnergyLine()
        {
            var (reservation, transaction) = SettledSession(nightKwh: 0, meterStop: 12);
            transaction.EnergyKwh = 2;
            transaction.EnergyCost = 0.80m;

            var draft = new InvoiceDraftBuilder().Build(reservation, transaction, null);

            var line = Assert.Single(draft.Lines, l => l.Type.StartsWith("Energy", StringComparison.Ordinal));
            Assert.Equal("Energy", line.Type);
            Assert.Equal("Charging energy", line.Description);
            Assert.Equal(0.80m, line.LineAmount);
        }

        private static (ChargePaymentReservation, Transaction) SettledSession(double nightKwh, double meterStop)
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-TEST",
                ConnectorId = 1,
                PricePerKwh = 0.40m,
                NightPricePerKwh = 0.25m,
                NightTariffStartMinute = NightTariffWindow.DefaultStartMinute,
                NightTariffEndMinute = NightTariffWindow.DefaultEndMinute,
                NightTariffTimeZoneId = Zagreb,
                Currency = "eur"
            };
            var transaction = CreateTransaction(Utc(7, 20, 19), withWindow: true);
            transaction.MeterStop = meterStop;
            transaction.StopTime = Utc(7, 20, 21);
            transaction.NightEnergyKwh = nightKwh;
            return (reservation, transaction);
        }

        private static MeterEvidenceResult Process(OCPPCoreContext db, Transaction transaction, string kwh, DateTime atUtc, bool terminal = false) =>
            MeterEvidenceProcessor.Process(db, transaction, new MeterEvidenceObservation
            {
                RawValue = kwh,
                Unit = "kWh",
                ObservedAtUtc = atUtc,
                Protocol = "OCPP1.6",
                Source = terminal ? "StopTransaction" : "MeterValues",
                IsTerminal = terminal,
                OfferedPowerRawValue = terminal ? null : "22",
                OfferedPowerUnit = terminal ? null : "kW",
                CandidateOfferedPowerRawValue = terminal ? null : "22",
                CandidateOfferedPowerUnit = terminal ? null : "kW",
                CandidateOfferedPowerMultiplier = terminal ? null : "0"
            });

        private static Transaction CreateTransaction(DateTime startUtc, bool withWindow) => new()
        {
            TransactionId = 777,
            ChargePointId = "CP-TEST",
            ConnectorId = 1,
            StartTime = startUtc,
            MeterStart = 10,
            MaxEnergyKwh = 80,
            NightTariffStartMinute = withWindow ? NightTariffWindow.DefaultStartMinute : null,
            NightTariffEndMinute = withWindow ? NightTariffWindow.DefaultEndMinute : null,
            NightTariffTimeZoneId = withWindow ? Zagreb : null
        };

        private static OCPPCoreContext CreateContext() =>
            new(new DbContextOptionsBuilder<OCPPCoreContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }
}
