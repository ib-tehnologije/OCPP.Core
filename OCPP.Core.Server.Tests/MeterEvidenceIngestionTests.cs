using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;
using Ocpp16Measurand = OCPP.Core.Server.Messages_OCPP16.SampledValueMeasurand;
using Ocpp16Sample = OCPP.Core.Server.Messages_OCPP16.SampledValue;
using Ocpp16Unit = OCPP.Core.Server.Messages_OCPP16.SampledValueUnit;
using Ocpp20Measurand = OCPP.Core.Server.Messages_OCPP20.MeasurandEnumType;
using Ocpp20Phase = OCPP.Core.Server.Messages_OCPP20.PhaseEnumType;
using Ocpp20Sample = OCPP.Core.Server.Messages_OCPP20.SampledValueType;
using Ocpp20Unit = OCPP.Core.Server.Messages_OCPP20.UnitOfMeasureType;
using Ocpp21Measurand = OCPP.Core.Server.Messages_OCPP21.MeasurandEnumType;
using Ocpp21Phase = OCPP.Core.Server.Messages_OCPP21.PhaseEnumType;
using Ocpp21Sample = OCPP.Core.Server.Messages_OCPP21.SampledValueType;
using Ocpp21Unit = OCPP.Core.Server.Messages_OCPP21.UnitOfMeasureType;

namespace OCPP.Core.Server.Tests
{
    public class MeterEvidenceIngestionTests
    {
        [Fact]
        public void Ocpp16_OfferedPowerAdapterPreservesRawCandidateEncoding()
        {
            var observation = new MeterEvidenceObservation();

            ControllerOCPP16.AddOfferedPower(observation, new[]
            {
                new Ocpp16Sample { Value = "22000", Measurand = Ocpp16Measurand.Power_Offered, Unit = Ocpp16Unit.W }
            });

            Assert.Equal("22", observation.OfferedPowerRawValue);
            Assert.Equal("kW", observation.OfferedPowerUnit);
            Assert.Equal("22000", observation.CandidateOfferedPowerRawValue);
            Assert.Equal("W", observation.CandidateOfferedPowerUnit);
            Assert.Equal("0", observation.CandidateOfferedPowerMultiplier);
        }

        [Fact]
        public void Ocpp201_OfferedPowerAdapterPreservesRawCandidateEncoding()
        {
            var observation = new MeterEvidenceObservation();

            ControllerOCPP20.AddOfferedPower(observation, new[]
            {
                new Ocpp20Sample
                {
                    Value = 22,
                    Measurand = Ocpp20Measurand.Power_Offered,
                    UnitOfMeasure = new Ocpp20Unit { Unit = "W", Multiplier = 3 }
                }
            });

            Assert.Equal("22", observation.OfferedPowerRawValue);
            Assert.Equal("kW", observation.OfferedPowerUnit);
            Assert.Equal("22", observation.CandidateOfferedPowerRawValue);
            Assert.Equal("W", observation.CandidateOfferedPowerUnit);
            Assert.Equal("3", observation.CandidateOfferedPowerMultiplier);
        }

        [Fact]
        public void Ocpp21_OfferedPowerAdapterPreservesRawCandidateEncoding()
        {
            var observation = new MeterEvidenceObservation();

            ControllerOCPP21.AddOfferedPower(observation, new[]
            {
                new Ocpp21Sample
                {
                    Value = 22,
                    Measurand = Ocpp21Measurand.Power_Offered,
                    UnitOfMeasure = new Ocpp21Unit { Unit = "W", Multiplier = 3 }
                }
            });

            Assert.Equal("22", observation.OfferedPowerRawValue);
            Assert.Equal("kW", observation.OfferedPowerUnit);
            Assert.Equal("22", observation.CandidateOfferedPowerRawValue);
            Assert.Equal("W", observation.CandidateOfferedPowerUnit);
            Assert.Equal("3", observation.CandidateOfferedPowerMultiplier);
        }

        [Fact]
        public void Ocpp201_PhaseOfferedPowerAdapterPreservesEachRawMultiplier()
        {
            var observation = new MeterEvidenceObservation();

            ControllerOCPP20.AddOfferedPower(observation, new[]
            {
                new Ocpp20Sample
                {
                    Value = 7,
                    Measurand = Ocpp20Measurand.Power_Offered,
                    Phase = Ocpp20Phase.L1,
                    UnitOfMeasure = new Ocpp20Unit { Unit = "W", Multiplier = 3 }
                },
                new Ocpp20Sample
                {
                    Value = 8,
                    Measurand = Ocpp20Measurand.Power_Offered,
                    Phase = Ocpp20Phase.L2,
                    UnitOfMeasure = new Ocpp20Unit { Unit = "W", Multiplier = 0 }
                }
            });

            Assert.Equal("7;8", observation.CandidateOfferedPowerRawValue);
            Assert.Equal("W;W", observation.CandidateOfferedPowerUnit);
            Assert.Equal("3;0", observation.CandidateOfferedPowerMultiplier);
        }

        [Fact]
        public void Ocpp21_PhaseOfferedPowerAdapterPreservesEachRawMultiplier()
        {
            var observation = new MeterEvidenceObservation();

            ControllerOCPP21.AddOfferedPower(observation, new[]
            {
                new Ocpp21Sample
                {
                    Value = 7,
                    Measurand = Ocpp21Measurand.Power_Offered,
                    Phase = Ocpp21Phase.L1,
                    UnitOfMeasure = new Ocpp21Unit { Unit = "W", Multiplier = 3 }
                },
                new Ocpp21Sample
                {
                    Value = 8,
                    Measurand = Ocpp21Measurand.Power_Offered,
                    Phase = Ocpp21Phase.L2,
                    UnitOfMeasure = new Ocpp21Unit { Unit = "W", Multiplier = 0 }
                }
            });

            Assert.Equal("7;8", observation.CandidateOfferedPowerRawValue);
            Assert.Equal("W;W", observation.CandidateOfferedPowerUnit);
            Assert.Equal("3;0", observation.CandidateOfferedPowerMultiplier);
        }

        [Fact]
        public void Ocpp16_MeterValuesThenImpossibleStop_PreservesProjectionAndRawTerminalEvidence()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 12529, uid: null);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP16(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);

            var meterResponse = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "meter-16",
                Action = "MeterValues",
                JsonPayload = "{\"connectorId\":1,\"transactionId\":12529,\"meterValue\":[{\"timestamp\":\"2026-09-10T10:00:10Z\",\"sampledValue\":[{\"value\":\"17308.59375\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\"},{\"value\":\"22000\",\"measurand\":\"Power.Offered\",\"unit\":\"W\"}]}]}"
            }, null);
            var stopResponse = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "stop-16",
                Action = "StopTransaction",
                JsonPayload = "{\"meterStop\":6135992,\"timestamp\":\"2026-09-10T10:09:57Z\",\"transactionId\":12529,\"reason\":\"EVDisconnected\"}"
            }, null);

            Assert.Equal("3", meterResponse.MessageType);
            Assert.Equal("3", stopResponse.MessageType);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            var anomaly = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal("6135992", anomaly.RawValue);
            Assert.Equal("Wh", anomaly.RawUnit);
            Assert.Equal("OCPP1.6", anomaly.Protocol);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, anomaly.Reason);
        }

        [Fact]
        public void Ocpp16_NegativeStartMeter_IsPreservedAsRejectedEvidence()
        {
            using var db = CreateContext();
            db.ChargeTags.Add(new ChargeTag { TagId = "TAG-METER" });
            db.SaveChanges();
            var controller = new ControllerOCPP16(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);

            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "start-negative-16",
                Action = "StartTransaction",
                JsonPayload = "{\"connectorId\":1,\"idTag\":\"TAG-METER\",\"meterStart\":-1,\"timestamp\":\"2026-09-10T10:00:00Z\"}"
            }, null);

            Assert.Equal("3", response.MessageType);
            var transaction = Assert.Single(db.Transactions);
            Assert.Null(transaction.AcceptedMeterKwh);
            var anomaly = Assert.Single(db.MeterEvidenceAnomalies);
            Assert.Equal("-1", anomaly.RawValue);
            Assert.Equal("Wh", anomaly.RawUnit);
            Assert.Equal("OCPP1.6", anomaly.Protocol);
            Assert.Equal("StartTransaction", anomaly.Source);
            Assert.Equal(MeterEvidenceReason.Negative, anomaly.Reason);
        }

        [Fact]
        public void Ocpp201_EndedEventWithImpossibleMeter_UsesAcceptedProjection()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 20, uid: "tx-20");
            SeedAcceptedProjection(transaction);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP20(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);

            var response = controller.ProcessRequest(EndedEvent("ended-20", "tx-20"), null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal("OCPP2.0.1", Assert.Single(db.MeterEvidenceAnomalies).Protocol);
        }

        [Fact]
        public void Ocpp21_EndedEventWithImpossibleMeter_UsesAcceptedProjection()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 21, uid: "tx-21");
            SeedAcceptedProjection(transaction);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP21(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);

            var response = controller.ProcessRequest(EndedEvent("ended-21", "tx-21"), null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal("OCPP2.1", Assert.Single(db.MeterEvidenceAnomalies).Protocol);
        }

        [Theory]
        [InlineData("2.0.1")]
        [InlineData("2.1")]
        public void Ocpp2_EndedEventWithoutMeterValue_FallsBackAndPreservesMissingEvidence(string protocol)
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: protocol == "2.0.1" ? 201 : 21, uid: $"tx-missing-{protocol}");
            SeedAcceptedProjection(transaction);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var message = new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"missing-{protocol}",
                Action = "TransactionEvent",
                JsonPayload = $"{{\"eventType\":\"Ended\",\"timestamp\":\"2026-09-10T10:09:57Z\",\"triggerReason\":\"Authorized\",\"seqNo\":2,\"evse\":{{\"id\":1,\"connectorId\":1}},\"transactionInfo\":{{\"transactionId\":\"{transaction.Uid}\",\"stoppedReason\":\"EVDisconnected\"}}}}"
            };

            var response = protocol == "2.0.1"
                ? new ControllerOCPP20(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db).ProcessRequest(message, null)
                : new ControllerOCPP21(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db).ProcessRequest(message, null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal(MeterEvidenceReason.Malformed, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Ocpp201_EndedEventWithOnlyNonEnergySamples_FallsBackAsMissingEvidence()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 203, uid: "tx-nonenergy-20");
            SeedAcceptedProjection(transaction);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP20(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);
            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "nonenergy-20",
                Action = "TransactionEvent",
                JsonPayload = "{\"eventType\":\"Ended\",\"timestamp\":\"2026-09-10T10:09:57Z\",\"triggerReason\":\"Authorized\",\"seqNo\":2,\"evse\":{\"id\":1,\"connectorId\":1},\"transactionInfo\":{\"transactionId\":\"tx-nonenergy-20\",\"stoppedReason\":\"EVDisconnected\"},\"meterValue\":[{\"timestamp\":\"2026-09-10T10:09:57Z\",\"sampledValue\":[{\"value\":22,\"measurand\":\"Power.Active.Import\",\"unitOfMeasure\":{\"unit\":\"kW\"}}]}]}"
            }, null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(17.30859375d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceReason.Malformed, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Theory]
        [InlineData("2.0.1")]
        [InlineData("2.1")]
        public void Ocpp2_StartedEvent_UsesMultiplierNormalizedMeterAsBillingBaseline(string protocol)
        {
            using var db = CreateContext();
            db.ChargeTags.Add(new ChargeTag { TagId = "TAG-METER" });
            db.SaveChanges();
            var message = new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"started-multiplier-{protocol}",
                Action = "TransactionEvent",
                JsonPayload = $"{{\"eventType\":\"Started\",\"timestamp\":\"2026-09-10T10:00:00Z\",\"triggerReason\":\"Authorized\",\"seqNo\":1,\"evse\":{{\"id\":1,\"connectorId\":1}},\"transactionInfo\":{{\"transactionId\":\"tx-start-{protocol}\"}},\"idToken\":{{\"idToken\":\"TAG-METER\",\"type\":\"Central\"}},\"meterValue\":[{{\"timestamp\":\"2026-09-10T10:00:00Z\",\"sampledValue\":[{{\"value\":10,\"measurand\":\"Energy.Active.Import.Register\",\"unitOfMeasure\":{{\"unit\":\"Wh\",\"multiplier\":3}}}}]}}]}}"
            };

            var response = protocol == "2.0.1"
                ? new ControllerOCPP20(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db).ProcessRequest(message, null)
                : new ControllerOCPP21(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db).ProcessRequest(message, null);

            Assert.Equal("3", response.MessageType);
            var transaction = Assert.Single(db.Transactions);
            Assert.Equal(10d, transaction.MeterStart);
            Assert.Equal(10d, transaction.AcceptedMeterKwh);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Ocpp16_MeterValues_PrefersOverallEnergyToFollowingPhaseValues()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 16, uid: null);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP16(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);

            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "phase-16",
                Action = "MeterValues",
                JsonPayload = "{\"connectorId\":1,\"transactionId\":16,\"meterValue\":[{\"timestamp\":\"2026-09-10T10:01:00Z\",\"sampledValue\":[{\"value\":\"18000\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\"},{\"value\":\"6000\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\",\"phase\":\"L1\"},{\"value\":\"6000\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\",\"phase\":\"L2\"},{\"value\":\"6000\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\",\"phase\":\"L3\"}]}]}"
            }, null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(18d, transaction.AcceptedMeterKwh);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Theory]
        [InlineData("2.0.1")]
        [InlineData("2.1")]
        public void Ocpp2_MeterValues_SumsPhaseOnlyEnergy(string protocol)
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: protocol == "2.0.1" ? 220 : 221, uid: $"tx-phase-{protocol}");
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var message = new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"phase-{protocol}",
                Action = "MeterValues",
                JsonPayload = "{\"evseId\":1,\"meterValue\":[{\"timestamp\":\"2026-09-10T10:01:00Z\",\"sampledValue\":[{\"value\":6,\"measurand\":\"Energy.Active.Import.Register\",\"phase\":\"L1\",\"unitOfMeasure\":{\"unit\":\"kWh\"}},{\"value\":6,\"measurand\":\"Energy.Active.Import.Register\",\"phase\":\"L2\",\"unitOfMeasure\":{\"unit\":\"kWh\"}},{\"value\":6,\"measurand\":\"Energy.Active.Import.Register\",\"phase\":\"L3\",\"unitOfMeasure\":{\"unit\":\"kWh\"}}]}]}"
            };

            var response = protocol == "2.0.1"
                ? new ControllerOCPP20(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db).ProcessRequest(message, null)
                : new ControllerOCPP21(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db).ProcessRequest(message, null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(18d, transaction.AcceptedMeterKwh);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        [Fact]
        public void Ocpp201_TerminalBatch_UsesEarlierAcceptedGroupForImpossibleFinalFallback()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 202, uid: "tx-batch-20");
            SeedAcceptedProjection(transaction);
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP20(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);
            var message = new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "batch-20",
                Action = "TransactionEvent",
                JsonPayload = "{\"eventType\":\"Ended\",\"timestamp\":\"2026-09-10T10:09:57Z\",\"triggerReason\":\"Authorized\",\"seqNo\":2,\"evse\":{\"id\":1,\"connectorId\":1},\"transactionInfo\":{\"transactionId\":\"tx-batch-20\",\"stoppedReason\":\"EVDisconnected\"},\"meterValue\":[{\"timestamp\":\"2026-09-10T10:09:00Z\",\"sampledValue\":[{\"value\":18,\"measurand\":\"Energy.Active.Import.Register\",\"unitOfMeasure\":{\"unit\":\"kWh\"}},{\"value\":22,\"measurand\":\"Power.Offered\",\"unitOfMeasure\":{\"unit\":\"kW\"}}]},{\"timestamp\":\"2026-09-10T10:09:57Z\",\"sampledValue\":[{\"value\":6135.992,\"measurand\":\"Energy.Active.Import.Register\",\"unitOfMeasure\":{\"unit\":\"kWh\"}}]}]}"
            };

            var response = controller.ProcessRequest(message, null);

            Assert.Equal("3", response.MessageType);
            Assert.Equal(18d, transaction.AcceptedMeterKwh);
            Assert.Equal(18d, transaction.MeterStop);
            Assert.Equal(MeterEvidenceSettlementState.FallbackAccepted, transaction.MeterEvidenceState);
            Assert.Equal(MeterEvidenceReason.PhysicallyImpossibleIncrease, Assert.Single(db.MeterEvidenceAnomalies).Reason);
        }

        [Fact]
        public void Ocpp16_MeterValuesForAnotherStation_DoesNotMutateTransactionEvidence()
        {
            using var db = CreateContext();
            var transaction = CreateOpenTransaction(transactionId: 160, uid: null);
            transaction.ChargePointId = "OTHER-CP";
            db.Transactions.Add(transaction);
            db.SaveChanges();
            var controller = new ControllerOCPP16(Configuration(), NullLoggerFactory.Instance, ChargePointStatus(), db);

            controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "wrong-owner-16",
                Action = "MeterValues",
                JsonPayload = "{\"connectorId\":1,\"transactionId\":160,\"meterValue\":[{\"timestamp\":\"2026-09-10T10:01:00Z\",\"sampledValue\":[{\"value\":\"18000\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\"}]}]}"
            }, null);

            Assert.Null(transaction.AcceptedMeterKwh);
            Assert.Empty(db.MeterEvidenceAnomalies);
        }

        private static OCPPMessage EndedEvent(string uniqueId, string transactionUid) => new()
        {
            MessageType = "2",
            UniqueId = uniqueId,
            Action = "TransactionEvent",
            JsonPayload = $"{{\"eventType\":\"Ended\",\"timestamp\":\"2026-09-10T10:09:57Z\",\"triggerReason\":\"Authorized\",\"seqNo\":2,\"evse\":{{\"id\":1,\"connectorId\":1}},\"transactionInfo\":{{\"transactionId\":\"{transactionUid}\",\"stoppedReason\":\"EVDisconnected\"}},\"meterValue\":[{{\"timestamp\":\"2026-09-10T10:09:57Z\",\"sampledValue\":[{{\"value\":6135.992,\"measurand\":\"Energy.Active.Import.Register\",\"unitOfMeasure\":{{\"unit\":\"kWh\"}}}}]}}]}}"
        };

        private static Transaction CreateOpenTransaction(int transactionId, string? uid) => new()
        {
            TransactionId = transactionId,
            Uid = uid,
            ChargePointId = "CP-METER",
            ConnectorId = 1,
            StartTagId = "TAG-METER",
            StartTime = new DateTime(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc),
            MeterStart = 17.30859375,
            MaxEnergyKwh = 80
        };

        private static void SeedAcceptedProjection(Transaction transaction)
        {
            transaction.AcceptedMeterKwh = 17.30859375;
            transaction.AcceptedMeterAtUtc = new DateTime(2026, 9, 10, 10, 0, 10, DateTimeKind.Utc);
            transaction.AcceptedMeterToleranceKwh = 0.000000005;
            transaction.TrustedMaximumPowerKw = 22;
            transaction.TrustedMaximumPowerToleranceKw = 0.5;
            transaction.TrustedMaximumPowerAtUtc = transaction.AcceptedMeterAtUtc;
            transaction.TrustedMaximumPowerSource = "OCPP:MeterValues:Power.Offered";
            transaction.MeterEvidenceState = MeterEvidenceSettlementState.Accepted;
            transaction.MeterEvidenceReason = MeterEvidenceReason.Accepted;
        }

        private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection().Build();

        private static ChargePointStatus ChargePointStatus() => new() { Id = "CP-METER" };

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }
    }
}
