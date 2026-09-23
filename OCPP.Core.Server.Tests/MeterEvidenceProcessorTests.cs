using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MeterEvidenceProcessorTests
    {
        [Fact]
        public void Process_Tx12529Jump_FallsBackToLastAcceptedProjectionAndPreservesRawEvidence()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var acceptedAt = transaction.StartTime.AddSeconds(10);
            var accepted = MeterEvidenceProcessor.Process(
                db,
                transaction,
                Observation("17.30859375", "kWh", acceptedAt, "OCPP1.6", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            var terminal = MeterEvidenceProcessor.Process(
                db,
                transaction,
                Observation("6135.992", "kWh", acceptedAt.AddSeconds(587), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Accepted, accepted.Outcome);
            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, terminal.Outcome);
            Assert.Equal(17.30859375d, terminal.SettlementMeterKwh);
            Assert.Equal(17.30859375d, transaction.AcceptedMeterKwh);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);

            var suspect = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal("6135.992", suspect.RawValue);
            Assert.Equal("kWh", suspect.RawUnit);
            Assert.Equal("OCPP1.6", suspect.Protocol);
            Assert.Equal("StopTransaction", suspect.Source);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, suspect.Reason);
            Assert.Equal(17.30859375d, suspect.AcceptedMeterKwh);
            Assert.Equal(22d, suspect.TrustedMaximumPowerKw);
        }

        [Theory]
        [InlineData("OCPP1.6")]
        [InlineData("OCPP2.0.1")]
        [InlineData("OCPP2.1")]
        public void Process_OrdinaryTerminalSample_IsAcceptedForEveryProtocol(string protocol)
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, protocol, "StartTransaction", offeredPowerRaw: "350", offeredPowerUnit: "kW"));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("44.5", "kWh", transaction.StartTime.AddMinutes(10), protocol, "Terminal", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(44.5d, transaction.MeterStop);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Process_PlausibleMaxEnergyOvershoot_RequiresReviewWithoutReplacingAcceptedProjection()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 5);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP2.1", "Started", offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("16", "kWh", transaction.StartTime.AddHours(1), "OCPP2.1", "Ended", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(16d, transaction.AcceptedMeterKwh);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.ReviewRequired, transaction.MeterEvidenceState);
            Assert.Equal(MeterEvidenceReason.AuthorizationLimitExceeded, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Process_HighTerminalSampleWithoutPowerEvidence_RequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", transaction.StartTime, "OCPP1.6", "MeterValues"));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", transaction.StartTime.AddSeconds(587), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(17.30859375d, transaction.AcceptedMeterKwh);
            Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Process_PositiveTerminalIncreaseBelowAuthorizationLimitWithoutPowerEvidence_RequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP1.6", "MeterValues"));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.1", "kWh", transaction.StartTime.AddMinutes(10), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Theory]
        [InlineData("not-a-number", "kWh", MeterEvidenceReason.Malformed)]
        [InlineData("NaN", "kWh", MeterEvidenceReason.NonFinite)]
        [InlineData("Infinity", "kWh", MeterEvidenceReason.NonFinite)]
        [InlineData("-1", "kWh", MeterEvidenceReason.Negative)]
        [InlineData("11", "J", MeterEvidenceReason.UnsupportedUnit)]
        public void Process_InvalidTerminalSample_FallsBackAndPreservesReason(string raw, string unit, string reason)
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.5", "kWh", transaction.StartTime.AddMinutes(1), "OCPP2.0.1", "MeterValues"));

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation(raw, unit, transaction.StartTime.AddMinutes(2), "OCPP2.0.1", "TransactionEvent", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, result.Outcome);
            Assert.Equal(10.5d, transaction.MeterStop);
            Assert.Equal(reason, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Process_ResetAndOutOfOrderSamples_DoNotReplaceProjection()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var acceptedAt = transaction.StartTime.AddMinutes(2);
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("12", "kWh", acceptedAt, "OCPP2.1", "MeterValues"));

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("11", "kWh", acceptedAt.AddMinutes(1), "OCPP2.1", "MeterValues"));
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("13", "kWh", acceptedAt.AddSeconds(-1), "OCPP2.1", "MeterValues"));

            Assert.Equal(12d, transaction.AcceptedMeterKwh);
            Assert.Equal(new[] { MeterEvidenceReason.NonMonotonic, MeterEvidenceReason.TimestampRegression },
                db.MeterEvidenceAnomalies.OrderBy(x => x.MeterEvidenceAnomalyId).Select(x => x.Reason).ToArray());
        }

        [Fact]
        public void Process_InvalidTerminalSampleWithoutAcceptedProjection_RequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: -1);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("NaN", "kWh", transaction.StartTime.AddMinutes(1), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.ReviewRequired, transaction.MeterEvidenceState);
        }

        [Fact]
        public void Process_ReplayOfSameSuspectTerminalEvidence_IsIdempotent()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var acceptedAt = transaction.StartTime.AddSeconds(10);
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", acceptedAt, "OCPP1.6", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            var suspect = Observation("6135.992", "kWh", acceptedAt.AddSeconds(587), "OCPP1.6", "StopTransaction", terminal: true);

            MeterEvidenceProcessor.Process(db, transaction, suspect);
            MeterEvidenceProcessor.Process(db, transaction, suspect);

            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Single(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Process_ReplayWithoutPowerCandidate_ReusesLegacyEvidenceKey()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var acceptedAt = transaction.StartTime.AddSeconds(10);
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", acceptedAt, "OCPP1.6", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            var observedAt = acceptedAt.AddSeconds(587);
            var legacyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f",
                transaction.TransactionId.ToString(CultureInfo.InvariantCulture),
                observedAt.ToString("O", CultureInfo.InvariantCulture),
                "OCPP1.6",
                "StopTransaction",
                "6135.992",
                "kWh",
                "0",
                MeterEvidenceReason.PhysicallyImpossibleIncrease)))).ToLowerInvariant();
            db.MeterEvidenceAnomalies.Add(new MeterEvidenceAnomaly
            {
                TransactionId = transaction.TransactionId,
                ChargePointId = transaction.ChargePointId,
                ConnectorId = transaction.ConnectorId,
                ObservedAtUtc = observedAt,
                Protocol = "OCPP1.6",
                Source = "StopTransaction",
                RawValue = "6135.992",
                RawUnit = "kWh",
                EvidenceKey = legacyKey,
                Outcome = MeterEvidenceOutcome.FallbackAccepted,
                Reason = MeterEvidenceReason.PhysicallyImpossibleIncrease,
                CreatedAtUtc = observedAt
            });
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", observedAt, "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal(legacyKey, Assert.Single(db.MeterEvidenceAnomalies).EvidenceKey);
        }

        [Fact]
        public void Process_SameRawValueWithDifferentMultiplier_PreservesDistinctEvidence()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var observedAt = transaction.StartTime.AddMinutes(1);

            MeterEvidenceProcessor.Process(db, transaction,
                new MeterEvidenceObservation
                {
                    RawValue = "11", Unit = "J", UnitMultiplier = 0, ObservedAtUtc = observedAt,
                    Protocol = "OCPP2.1", Source = "Ended", IsTerminal = true
                });
            MeterEvidenceProcessor.Process(db, transaction,
                new MeterEvidenceObservation
                {
                    RawValue = "11", Unit = "J", UnitMultiplier = 1, ObservedAtUtc = observedAt,
                    Protocol = "OCPP2.1", Source = "Ended", IsTerminal = true
                });

            Assert.Equal(new[] { 0, 1 }, db.MeterEvidenceAnomalies.OrderBy(item => item.RawUnitMultiplier).Select(item => item.RawUnitMultiplier));
            Assert.Equal(2, db.MeterEvidenceAnomalies.Select(item => item.EvidenceKey).Distinct().Count());
        }

        [Fact]
        public void Process_UncorroboratedHigherTerminalPowerRequiresReviewWithoutReplacingCapacity()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375, maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var acceptedAt = transaction.StartTime.AddSeconds(10);
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", acceptedAt, "OCPP2.0.1", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", acceptedAt.AddSeconds(587), "OCPP2.0.1", "TransactionEvent.Ended", terminal: true,
                    offeredPowerRaw: "1000000", offeredPowerUnit: "kW"));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, result.Reason);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
            var anomaly = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal("1000000", anomaly.CandidateOfferedPowerRawValue);
            Assert.Equal("kW", anomaly.CandidateOfferedPowerUnit);
            Assert.Equal(0, anomaly.CandidateOfferedPowerMultiplier);
        }

        [Fact]
        public void Process_RisingOfferedPowerMakesTheIntervalAmbiguousRatherThanImpossible()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP2.1", "Started", offeredPowerRaw: "3", offeredPowerUnit: "kW"));

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("13", "kWh", transaction.StartTime.AddMinutes(10), "OCPP2.1", "Ended", terminal: true,
                    offeredPowerRaw: "22", offeredPowerUnit: "kW"));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Equal(3d, transaction.TrustedMaximumPowerKw);
            Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void AvailableConnectorRecovery_ValidatesSuspectRawMeterAndFallsBack()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 200);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP1.6", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));

            var closed = OpenTransactionRecovery.TryCloseForAvailableConnector(
                db,
                transaction,
                transaction.ChargePointId,
                transaction.ConnectorId,
                transaction.StartTime.AddMinutes(10),
                100,
                NullLogger.Instance,
                "Cleanup");

            Assert.True(closed);
            Assert.Equal(10d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            var anomaly = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal("100", anomaly.RawValue);
            Assert.Equal("Recovery", anomaly.Protocol);
            Assert.Contains("LiveConnectorMeter", anomaly.Source);
        }

        [Theory]
        [InlineData("VAh")]
        [InlineData("varh")]
        [InlineData("kVAh")]
        [InlineData("kvarh")]
        public void Process_NonActiveEnergyUnit_DoesNotReplaceAcceptedProjection(string unit)
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("11", unit, transaction.StartTime.AddMinutes(1), "OCPP2.1", "TransactionEvent.Ended", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, result.Outcome);
            Assert.Equal(MeterEvidenceReason.UnsupportedUnit, result.Reason);
            Assert.Equal(10d, transaction.MeterStop);
        }

        [Fact]
        public void EnsureReady_RevalidatesLegacyTerminalMeterAgainstAcceptedProjection()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 200);
            transaction.AcceptedMeterKwh = 10;
            transaction.AcceptedMeterAtUtc = transaction.StartTime;
            transaction.AcceptedMeterToleranceKwh = 0.0005;
            transaction.TrustedMaximumPowerKw = 22;
            transaction.TrustedMaximumPowerToleranceKw = 0.5;
            transaction.TrustedMaximumPowerAtUtc = transaction.StartTime;
            transaction.TrustedMaximumPowerSource = "OCPP1.6:MeterValues:Power.Offered";
            transaction.MeterStop = 100;
            transaction.StopTime = transaction.StartTime.AddMinutes(10);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                TransactionId = transaction.TransactionId,
                ChargePointId = transaction.ChargePointId,
                ConnectorId = transaction.ConnectorId,
                ChargeTagId = "TAG-TEST",
                Status = PaymentReservationStatus.Charging,
                Currency = "eur",
                MaxEnergyKwh = 200
            };
            db.AddRange(transaction, reservation);
            db.SaveChanges();

            var decision = MeterEvidenceSettlementGuard.EnsureReady(db, reservation, transaction, "Retry");

            Assert.True(decision.Ready, decision.Reason);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal(10d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Process_OcppUnitMultiplier_IsAppliedToTrustedOfferedPower()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP2.1", "Started",
                    offeredPowerRaw: "22", offeredPowerUnit: "W", offeredPowerMultiplier: 3));

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("12", "kWh", transaction.StartTime.AddMinutes(10), "OCPP2.1", "Ended", terminal: true));

            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(12d, transaction.MeterStop);
        }

        [Fact]
        public void Process_OcppUnitMultiplier_IsAppliedToCumulativeEnergy()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "Wh", transaction.StartTime.AddMinutes(1), "OCPP2.0.1", "Ended", terminal: true, unitMultiplier: 3));

            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(10d, transaction.MeterStop);
        }

        [Fact]
        public void Process_LowerLaterOfferedPower_DoesNotEraseHigherAcceptedCapacityEvidence()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP1.6", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.5", "kWh", transaction.StartTime.AddMinutes(10), "OCPP1.6", "MeterValues", offeredPowerRaw: "11", offeredPowerUnit: "kW"));

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("13.5", "kWh", transaction.StartTime.AddMinutes(20), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(13.5d, transaction.MeterStop);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }

        private static Transaction CreateTransaction(double meterStart = 10, double maxEnergyKwh = 80) => new()
        {
            TransactionId = 12529,
            ChargePointId = "CP-TEST",
            ConnectorId = 1,
            StartTime = new DateTime(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc),
            MeterStart = meterStart,
            MaxEnergyKwh = maxEnergyKwh
        };

        private static MeterEvidenceObservation Observation(
            string raw,
            string unit,
            DateTime timestamp,
            string protocol,
            string source,
            bool terminal = false,
            string? offeredPowerRaw = null,
            string? offeredPowerUnit = null,
            int offeredPowerMultiplier = 0,
            int unitMultiplier = 0) => new()
        {
            RawValue = raw,
            Unit = unit,
            UnitMultiplier = unitMultiplier,
            ObservedAtUtc = timestamp,
            Protocol = protocol,
            Source = source,
            IsTerminal = terminal,
            OfferedPowerRawValue = offeredPowerRaw,
            OfferedPowerUnit = offeredPowerUnit,
            OfferedPowerMultiplier = offeredPowerMultiplier,
            CandidateOfferedPowerRawValue = offeredPowerRaw,
            CandidateOfferedPowerUnit = offeredPowerUnit,
            CandidateOfferedPowerMultiplier = offeredPowerRaw == null && offeredPowerUnit == null ? null : offeredPowerMultiplier
        };
    }
}
