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
        public void Process_PlausibleMaxEnergyOvershoot_SettlesAtAuthorizedMaximum()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 5);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP2.1", "Started", offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("16", "kWh", transaction.StartTime.AddHours(1), "OCPP2.1", "Ended", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, result.Outcome);
            Assert.Equal(15d, result.SettlementMeterKwh);
            Assert.Equal(15d, transaction.MeterStop);
            Assert.Equal(15d, transaction.AcceptedMeterKwh);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            var anomaly = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal(MeterEvidenceReason.AuthorizationLimitExceeded, anomaly.Reason);
            Assert.Equal(16d, anomaly.NormalizedMeterKwh);
            Assert.True(MeterEvidenceSettlementGuard.Assess(null, transaction).Ready);
        }

        [Fact]
        public void Process_HighTerminalSampleWithoutPowerEvidenceAndFallbackDisabled_RequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", transaction.StartTime, "OCPP1.6", "MeterValues", fallbackMaximumPowerKw: 0));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", transaction.StartTime.AddSeconds(587), "OCPP1.6", "StopTransaction", terminal: true, fallbackMaximumPowerKw: 0));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Equal(2, db.MeterEvidenceAnomalies.Count());
            Assert.All(db.MeterEvidenceAnomalies, anomaly =>
                Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, anomaly.Reason));
        }

        [Fact]
        public void Process_PositiveTerminalIncreaseBelowAuthorizationLimitWithoutPowerEvidenceAndFallbackDisabled_RequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP1.6", "MeterValues", fallbackMaximumPowerKw: 0));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.1", "kWh", transaction.StartTime.AddMinutes(10), "OCPP1.6", "StopTransaction", terminal: true, fallbackMaximumPowerKw: 0));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Process_CapacityFreeIntermediateWithFallbackDisabledThenEqualTerminal_RequiresReviewWithoutAdvancingProjection()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var intermediate = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.1", "kWh", transaction.StartTime.AddMinutes(5), "OCPP2.0.1", "MeterValues", fallbackMaximumPowerKw: 0));
            var terminal = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.1", "kWh", transaction.StartTime.AddMinutes(10), "OCPP2.0.1", "TransactionEvent.Ended", terminal: true, fallbackMaximumPowerKw: 0));

            Assert.Equal(MeterEvidenceOutcome.Rejected, intermediate.Outcome);
            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, terminal.Outcome);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Equal(transaction.StartTime, transaction.AcceptedMeterAtUtc);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.ReviewRequired, transaction.MeterEvidenceState);
            var anomalies = db.MeterEvidenceAnomalies
                .OrderBy(anomaly => anomaly.MeterEvidenceAnomalyId)
                .ToList();
            Assert.All(anomalies, anomaly =>
                Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, anomaly.Reason));
            Assert.Equal(2, anomalies.Count);
            Assert.Equal("10.1", anomalies[0].RawValue);
            Assert.Equal("MeterValues", anomalies[0].Source);
            Assert.Equal(MeterEvidenceOutcome.Rejected, anomalies[0].Outcome);
            Assert.Equal(10d, anomalies[0].AcceptedMeterKwh);
        }

        [Fact]
        public void Process_CapacityFreeZeroEnergyIntermediate_RemainsAccepted()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime.AddMinutes(5), "OCPP2.0.1", "MeterValues"));

            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Equal(transaction.StartTime.AddMinutes(5), transaction.AcceptedMeterAtUtc);
            Assert.Equal(MeterEvidenceSettlementState.Accepted, transaction.MeterEvidenceState);
            Assert.Empty(db.MeterEvidenceAnomalies);
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
                Observation("10.5", "kWh", transaction.StartTime.AddMinutes(1), "OCPP2.0.1", "MeterValues",
                    offeredPowerRaw: "50", offeredPowerUnit: "kW"));

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
                Observation("12", "kWh", acceptedAt, "OCPP2.1", "MeterValues",
                    offeredPowerRaw: "100", offeredPowerUnit: "kW"));

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
        public void Process_UncorroboratedHigherTerminalPowerWithFallbackDisabledRequiresReviewWithoutReplacingCapacity()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375, maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var acceptedAt = transaction.StartTime.AddSeconds(10);
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", acceptedAt, "OCPP2.0.1", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW", fallbackMaximumPowerKw: 0));

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", acceptedAt.AddSeconds(587), "OCPP2.0.1", "TransactionEvent.Ended", terminal: true,
                    offeredPowerRaw: "1000000", offeredPowerUnit: "kW", fallbackMaximumPowerKw: 0));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(MeterEvidenceReason.PhysicalCapacityUnavailable, result.Reason);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
            var anomaly = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal("1000000", anomaly.CandidateOfferedPowerRawValue);
            Assert.Equal("kW", anomaly.CandidateOfferedPowerUnit);
            Assert.Equal("0", anomaly.CandidateOfferedPowerMultiplier);
        }

[Fact]
        public void Process_InflatedTerminalOfferedPower_IsCappedByFallbackCeilingAndFallsBack()
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

            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, result.Outcome);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, result.Reason);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
            Assert.Equal("1000000", Assert.Single(db.MeterEvidenceAnomalies).CandidateOfferedPowerRawValue);
        }

        [Fact]
        public void Process_InflatedIntermediateOfferedPower_CannotJustifyOrLatchAnAbsurdJump()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375, maxEnergyKwh: 0);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var acceptedAt = transaction.StartTime.AddSeconds(10);
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", acceptedAt, "OCPP1.6", "MeterValues", offeredPowerRaw: "22", offeredPowerUnit: "kW"));

            var glitch = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", acceptedAt.AddSeconds(587), "OCPP1.6", "MeterValues",
                    offeredPowerRaw: "1000000", offeredPowerUnit: "kW"));
            var terminal = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6136500", "Wh", acceptedAt.AddSeconds(650), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Rejected, glitch.Outcome);
            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, terminal.Outcome);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, terminal.Reason);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(17.30859375d, transaction.AcceptedMeterKwh);
            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
        }

        [Fact]
        public void Process_RisingOfferedPowerOnIntermediateReadingWithFallbackDisabled_RequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP2.1", "Started", offeredPowerRaw: "3", offeredPowerUnit: "kW",
                    fallbackMaximumPowerKw: 0));

            var intermediate = MeterEvidenceProcessor.Process(db, transaction,
                Observation("13", "kWh", transaction.StartTime.AddMinutes(10), "OCPP2.1", "Updated",
                    offeredPowerRaw: "22", offeredPowerUnit: "kW", fallbackMaximumPowerKw: 0));

            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, intermediate.Outcome);
            Assert.Equal(3d, transaction.TrustedMaximumPowerKw);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
        }

        [Fact]
        public void Process_PersistentRegisterOffset_IsNeverAbsorbedByElapsedTimeAndRequiresReview()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 80);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            // 11 kW session sampled every minute; from minute 6 the register is offset by +30 kWh.
            MeterEvidenceResult last = null!;
            for (var minute = 1; minute <= 30; minute++)
            {
                var realKwh = 10 + 11d * minute / 60;
                var reportedWh = Math.Round((realKwh + (minute >= 6 ? 30 : 0)) * 1000);
                last = MeterEvidenceProcessor.Process(db, transaction,
                    Observation(reportedWh.ToString(CultureInfo.InvariantCulture), "Wh", transaction.StartTime.AddMinutes(minute),
                        "OCPP1.6", minute == 30 ? "StopTransaction" : "MeterValues", terminal: minute == 30));
            }

            // The offset is never billed: the session stops for review at the last good reading.
            Assert.Equal(MeterEvidenceOutcome.ReviewRequired, last.Outcome);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, last.Reason);
            Assert.Null(transaction.MeterStop);
            Assert.Equal(10.917d, transaction.AcceptedMeterKwh);
            Assert.False(MeterEvidenceSettlementGuard.Assess(null, transaction).Ready);
        }

        [Fact]
        public void Process_TransientSpikeThenNormalReadings_ResumesAcceptance()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10200", "Wh", transaction.StartTime.AddMinutes(1), "OCPP1.6", "MeterValues"));
            var spike = MeterEvidenceProcessor.Process(db, transaction,
                Observation("90000", "Wh", transaction.StartTime.AddMinutes(2), "OCPP1.6", "MeterValues"));
            var resumed = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10550", "Wh", transaction.StartTime.AddMinutes(3), "OCPP1.6", "MeterValues"));
            var terminal = MeterEvidenceProcessor.Process(db, transaction,
                Observation("11000", "Wh", transaction.StartTime.AddMinutes(5), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Rejected, spike.Outcome);
            Assert.Equal(MeterEvidenceOutcome.Accepted, resumed.Outcome);
            Assert.Equal(MeterEvidenceOutcome.Accepted, terminal.Outcome);
            Assert.Equal(11d, transaction.MeterStop);
        }

        [Fact]
        public void Process_RecoveredKwhWithCoarsePrecisionAboveLimit_SettlesAtAuthorizedMaximum()
        {
            using var db = CreateContext();
            // "31" re-formatted from a kWh double carries a 0.5 kWh string precision; it must not
            // let 20.4 kWh be billed against a 20 kWh authorization boundary.
            var transaction = CreateTransaction(meterStart: 10.6, maxEnergyKwh: 20);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("31", "kWh", transaction.StartTime.AddHours(1), "Recovery", "Cleanup:LiveConnectorMeter", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, result.Outcome);
            Assert.Equal(MeterEvidenceReason.AuthorizationLimitExceeded, result.Reason);
            Assert.Equal(30.6d, transaction.MeterStop);
            Assert.True(MeterEvidenceSettlementGuard.Assess(null, transaction).Ready);
        }

        [Fact]
        public void AvailableConnectorRecovery_AfterLaterSessionOnConnector_IgnoresConnectorMeters()
        {
            using var db = CreateContext();
            var orphan = CreateTransaction(maxEnergyKwh: 200);
            db.Transactions.Add(orphan);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, orphan,
                Observation("15", "kWh", orphan.StartTime.AddMinutes(30), "OCPP1.6", "MeterValues"));
            db.Transactions.Add(new Transaction
            {
                TransactionId = orphan.TransactionId + 1,
                ChargePointId = orphan.ChargePointId,
                ConnectorId = orphan.ConnectorId,
                StartTime = orphan.StartTime.AddHours(2),
                StopTime = orphan.StartTime.AddHours(5),
                MeterStart = 15,
                MeterStop = 40
            });
            db.SaveChanges();

            var closed = OpenTransactionRecovery.TryCloseForAvailableConnector(
                db,
                orphan,
                orphan.ChargePointId,
                orphan.ConnectorId,
                orphan.StartTime.AddHours(10),
                40,
                NullLogger.Instance,
                "Cleanup");

            Assert.True(closed);
            Assert.Equal(15d, orphan.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.Accepted, orphan.MeterEvidenceState);
        }

                [Fact]
        public void Process_MixedPowerMultiplierCandidatesRemainDistinctAndReplaySafe()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var observedAt = transaction.StartTime.AddMinutes(1);

            foreach (var multipliers in new[] { "3;0", "0;3" })
            {
                var observation = new MeterEvidenceObservation
                {
                    RawValue = "11",
                    Unit = "J",
                    ObservedAtUtc = observedAt,
                    Protocol = "OCPP2.1",
                    Source = "TransactionEvent.Ended",
                    IsTerminal = true,
                    CandidateOfferedPowerRawValue = "7;8",
                    CandidateOfferedPowerUnit = "W;W",
                    CandidateOfferedPowerMultiplier = multipliers
                };

                MeterEvidenceProcessor.Process(db, transaction, observation);
                MeterEvidenceProcessor.Process(db, transaction, observation);
            }

            db.ChangeTracker.Clear();
            var anomalies = db.MeterEvidenceAnomalies
                .AsNoTracking()
                .OrderBy(item => item.CandidateOfferedPowerMultiplier)
                .ToList();
            Assert.Equal(2, anomalies.Count);
            Assert.Equal(new[] { "0;3", "3;0" }, anomalies.Select(item => item.CandidateOfferedPowerMultiplier));
            Assert.Equal(2, anomalies.Select(item => item.EvidenceKey).Distinct().Count());
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

        [Fact]
        public void Process_Tx12529JumpWithoutOfferedPower_FallbackCeilingSettlesFromLastAcceptedReading()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(meterStart: 17.30859375);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var accepted = MeterEvidenceProcessor.Process(db, transaction,
                Observation("17.30859375", "kWh", transaction.StartTime.AddSeconds(10), "OCPP1.6", "MeterValues"));
            var terminal = MeterEvidenceProcessor.Process(db, transaction,
                Observation("6135.992", "kWh", transaction.StartTime.AddSeconds(597), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Accepted, accepted.Outcome);
            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, terminal.Outcome);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Null(transaction.TrustedMaximumPowerKw);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Process_StartAndStopOnlyWithoutOfferedPower_AcceptsPlausibleEnergy()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10500", "Wh", transaction.StartTime.AddMinutes(5), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(10.5d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.Accepted, transaction.MeterEvidenceState);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Process_ZeroEnergyWhStop_NormalizesExactlyToMeterStartEvenWithoutFallback()
        {
            using var db = CreateContext();
            // 1001 * 0.001 != 1001 / 1000 in binary floating point; the stop must still equal the start.
            var transaction = CreateTransaction(meterStart: 1001d / 1000);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("1001", "Wh", transaction.StartTime.AddMinutes(2), "OCPP1.6", "StopTransaction", terminal: true,
                    fallbackMaximumPowerKw: 0));

            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(transaction.MeterStart, transaction.MeterStop);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Process_DecreaseWithinReadingPrecision_KeepsAcceptedProjectionForSettlement()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.43", "kWh", transaction.StartTime.AddMinutes(5), "OCPP2.0.1", "MeterValues"));
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10.4", "kWh", transaction.StartTime.AddMinutes(10), "OCPP2.0.1", "TransactionEvent.Ended", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            Assert.Equal(10.43d, transaction.MeterStop);
            Assert.Equal(10.43d, transaction.AcceptedMeterKwh);
            Assert.True(MeterEvidenceSettlementGuard.Assess(null, transaction).Ready);
        }

        [Theory]
        [InlineData("21", MeterEvidenceOutcome.Accepted)]
        [InlineData("21.2", MeterEvidenceOutcome.Rejected)]
        public void Process_ConfiguredFallbackCeiling_BoundsIncreaseByElapsedTime(string reading, string expectedOutcome)
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            // 22 kW for 30 minutes allows at most 11 kWh above the 10 kWh start.
            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation(reading, "kWh", transaction.StartTime.AddMinutes(30), "OCPP1.6", "MeterValues",
                    fallbackMaximumPowerKw: 22));

            Assert.Equal(expectedOutcome, result.Outcome);
            if (expectedOutcome == MeterEvidenceOutcome.Rejected)
            {
                Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, result.Reason);
                Assert.Equal(10d, transaction.AcceptedMeterKwh);
            }
        }

        [Fact]
        public void Process_RisingOfferedPowerOnIntermediateReading_RaisesTrustedCapacity()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("10", "kWh", transaction.StartTime, "OCPP2.1", "Started", offeredPowerRaw: "3", offeredPowerUnit: "kW"));

            var intermediate = MeterEvidenceProcessor.Process(db, transaction,
                Observation("13", "kWh", transaction.StartTime.AddMinutes(10), "OCPP2.1", "Updated",
                    offeredPowerRaw: "22", offeredPowerUnit: "kW"));
            var terminal = MeterEvidenceProcessor.Process(db, transaction,
                Observation("16", "kWh", transaction.StartTime.AddMinutes(20), "OCPP2.1", "Ended", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.Accepted, intermediate.Outcome);
            Assert.Equal(MeterEvidenceOutcome.Accepted, terminal.Outcome);
            Assert.Equal(22d, transaction.TrustedMaximumPowerKw);
            Assert.Equal(16d, transaction.MeterStop);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Process_OverAuthorizationLimitWithFallbackCapacity_SettlesAtAuthorizedMaximum()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 20);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("31", "kWh", transaction.StartTime.AddHours(1), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.Equal(MeterEvidenceOutcome.FallbackAccepted, result.Outcome);
            Assert.Equal(MeterEvidenceReason.AuthorizationLimitExceeded, result.Reason);
            Assert.Equal(30d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal(31d, Assert.Single(db.MeterEvidenceAnomalies).NormalizedMeterKwh);
            Assert.True(MeterEvidenceSettlementGuard.Assess(null, transaction).Ready);
        }

        [Fact]
        public void Assess_EnergyLimitReachedExactly_IsReadyDespiteFloatingPointRounding()
        {
            using var db = CreateContext();
            // (1024005 / 1000) - (1004005 / 1000) is slightly above 20 in binary floating point.
            var transaction = CreateTransaction(meterStart: 1004005d / 1000, maxEnergyKwh: 20);
            db.Transactions.Add(transaction);
            db.SaveChanges();

            var result = MeterEvidenceProcessor.Process(db, transaction,
                Observation("1024005", "Wh", transaction.StartTime.AddHours(1), "OCPP1.6", "StopTransaction", terminal: true));

            Assert.True(transaction.MeterStop!.Value - transaction.MeterStart > 20d);
            Assert.Equal(MeterEvidenceOutcome.Accepted, result.Outcome);
            var decision = MeterEvidenceSettlementGuard.Assess(null, transaction);
            Assert.True(decision.Ready, decision.Reason);
        }

        [Fact]
        public void AvailableConnectorRecovery_WithoutOfferedPower_SettlesFromAcceptedLiveMeter()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction(maxEnergyKwh: 200);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, transaction,
                Observation("15", "kWh", transaction.StartTime.AddMinutes(15), "OCPP1.6", "MeterValues"));

            var closed = OpenTransactionRecovery.TryCloseForAvailableConnector(
                db,
                transaction,
                transaction.ChargePointId,
                transaction.ConnectorId,
                transaction.StartTime.AddMinutes(20),
                15,
                NullLogger.Instance,
                "Cleanup");

            Assert.True(closed);
            Assert.Equal(15d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.Accepted, transaction.MeterEvidenceState);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Process_SameSecondGlitchAtSessionStart_DoesNotLockTheSession()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            // A Transaction.Begin sample stamped with the start second but 2 Wh above meterStart.
            var begin = MeterEvidenceProcessor.Process(db, transaction,
                Observation("10002", "Wh", transaction.StartTime, "OCPP1.6", "MeterValues"));
            MeterEvidenceResult last = null!;
            for (var minute = 1; minute <= 60; minute++)
            {
                var wh = Math.Round((10 + 22d * minute / 60) * 1000);
                last = MeterEvidenceProcessor.Process(db, transaction,
                    Observation(wh.ToString(CultureInfo.InvariantCulture), "Wh", transaction.StartTime.AddMinutes(minute),
                        "OCPP1.6", minute == 60 ? "StopTransaction" : "MeterValues", terminal: minute == 60));
            }

            Assert.Equal(MeterEvidenceOutcome.Accepted, begin.Outcome);
            Assert.Equal(MeterEvidenceOutcome.Accepted, last.Outcome);
            Assert.Equal(32d, transaction.MeterStop);
        }

        [Fact]
        public void Process_ShortChargerClockStepBack_DoesNotLockTheSession()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            // 22 kW, one sample per real minute; after minute 20 the charger clock steps back 58 s.
            MeterEvidenceResult last = null!;
            for (var minute = 1; minute <= 40; minute++)
            {
                var wh = Math.Round((10 + 22d * minute / 60) * 1000);
                var stamp = transaction.StartTime.AddMinutes(minute).AddSeconds(minute > 20 ? -58 : 0);
                last = MeterEvidenceProcessor.Process(db, transaction,
                    Observation(wh.ToString(CultureInfo.InvariantCulture), "Wh", stamp,
                        "OCPP1.6", minute == 40 ? "StopTransaction" : "MeterValues", terminal: minute == 40));
            }

            Assert.Equal(MeterEvidenceOutcome.Accepted, last.Outcome);
            Assert.Equal(Math.Round((10 + 22d * 40 / 60) * 1000) / 1000, transaction.MeterStop);
        }

        [Fact]
        public void AvailableConnectorRecovery_LaterSessionWithResetChargerClock_IgnoresConnectorMeters()
        {
            using var db = CreateContext();
            var orphan = CreateTransaction(maxEnergyKwh: 200);
            db.Transactions.Add(orphan);
            db.SaveChanges();
            MeterEvidenceProcessor.Process(db, orphan,
                Observation("15", "kWh", orphan.StartTime.AddMinutes(30), "OCPP1.6", "MeterValues"));
            // The later session's charger clock was reset, so its StartTime is before the orphan's.
            db.Transactions.Add(new Transaction
            {
                TransactionId = orphan.TransactionId + 1,
                ChargePointId = orphan.ChargePointId,
                ConnectorId = orphan.ConnectorId,
                StartTime = orphan.StartTime.AddDays(-30),
                StopTime = orphan.StartTime.AddDays(-30).AddHours(3),
                MeterStart = 15,
                MeterStop = 40
            });
            db.SaveChanges();

            var closed = OpenTransactionRecovery.TryCloseForAvailableConnector(
                db,
                orphan,
                orphan.ChargePointId,
                orphan.ConnectorId,
                orphan.StartTime.AddHours(10),
                40,
                NullLogger.Instance,
                "Cleanup");

            Assert.True(closed);
            Assert.Equal(15d, orphan.MeterStop);
        }

        [Fact]
        public void Process_OneHourChargerClockStepBack_RecoversAndBillsFullEnergy()
        {
            using var db = CreateContext();
            var transaction = CreateTransaction();
            db.Transactions.Add(transaction);
            db.SaveChanges();

            // 22 kW, one sample per real minute for 3 hours; after real minute 60 the charger clock
            // steps back one hour (daylight-saving change on a charger that labels local time as UTC).
            MeterEvidenceResult last = null!;
            for (var minute = 1; minute <= 180; minute++)
            {
                var wh = Math.Round((10 + 22d * minute / 60) * 1000);
                var stamp = transaction.StartTime.AddMinutes(minute > 60 ? minute - 60 : minute);
                last = MeterEvidenceProcessor.Process(db, transaction,
                    Observation(wh.ToString(CultureInfo.InvariantCulture), "Wh", stamp,
                        "OCPP1.6", minute == 180 ? "StopTransaction" : "MeterValues", terminal: minute == 180));
            }

            Assert.Equal(MeterEvidenceOutcome.Accepted, last.Outcome);
            Assert.Equal(76d, transaction.MeterStop);
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
            int unitMultiplier = 0,
            double? fallbackMaximumPowerKw = null) => new()
        {
            FallbackMaximumPowerKw = fallbackMaximumPowerKw,
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
            CandidateOfferedPowerMultiplier = offeredPowerRaw == null && offeredPowerUnit == null
                ? null
                : offeredPowerMultiplier.ToString(CultureInfo.InvariantCulture)
        };
    }
}
