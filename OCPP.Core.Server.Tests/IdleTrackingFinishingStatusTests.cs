using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Idle (occupancy) fee tracking for OCPP 1.6 chargers that report Finishing instead of
    /// SuspendedEV for an open transaction, and that flap Charging/Finishing while no energy flows.
    /// </summary>
    public class IdleTrackingFinishingStatusTests
    {
        private const int TransactionId = 4242;

        [Fact]
        public void Finishing_WithOpenTransaction_OpensIdleIntervalAndBillsOnStop()
        {
            RunWithDatabase((db, chargePointStatus, controller) =>
            {
                SendStatus(controller, "Finishing", "2026-03-09T22:00:00Z");

                db.ChangeTracker.Clear();
                Assert.Equal(Utc("2026-03-09T22:00:00Z"), db.Transactions.Find(TransactionId)!.ChargingEndedAtUtc);

                SendStop(controller, "2026-03-09T23:30:00Z");

                db.ChangeTracker.Clear();
                var transaction = db.Transactions.Find(TransactionId)!;
                // 90 idle minutes minus 60 minutes grace.
                Assert.Equal(30, transaction.IdleUsageFeeMinutes);
                Assert.Equal(1.5m, transaction.IdleUsageFeeAmount);
            });
        }

        [Fact]
        public void ChargingFinishingFlap_WithFrozenMeter_KeepsSingleIdleInterval()
        {
            RunWithDatabase((db, chargePointStatus, controller) =>
            {
                SendStatus(controller, "Finishing", "2026-03-09T22:00:00Z");
                SendStatus(controller, "Charging", "2026-03-09T22:20:00Z");
                SendStatus(controller, "Finishing", "2026-03-09T22:20:01Z");
                SendStatus(controller, "Charging", "2026-03-09T22:45:00Z");
                SendStatus(controller, "Finishing", "2026-03-09T22:45:01Z");
                SendStatus(controller, "Charging", "2026-03-09T23:10:00Z");

                db.ChangeTracker.Clear();
                var open = db.Transactions.Find(TransactionId)!;
                Assert.Equal(Utc("2026-03-09T22:00:00Z"), open.ChargingEndedAtUtc);
                Assert.Equal(0, open.IdleUsageFeeMinutes);

                SendStop(controller, "2026-03-10T00:00:00Z");

                db.ChangeTracker.Clear();
                var transaction = db.Transactions.Find(TransactionId)!;
                // One continuous 120-minute interval minus 60 minutes grace.
                Assert.Equal(60, transaction.IdleUsageFeeMinutes);
                Assert.Equal(3.0m, transaction.IdleUsageFeeAmount);
            });
        }

        [Fact]
        public void Charging_FollowedByMeterIncrease_ClosesIdleIntervalAtResumeTime()
        {
            RunWithDatabase((db, chargePointStatus, controller) =>
            {
                SendStatus(controller, "Finishing", "2026-03-09T22:00:00Z");
                SendStatus(controller, "Charging", "2026-03-09T23:30:00Z");

                db.ChangeTracker.Clear();
                Assert.NotNull(db.Transactions.Find(TransactionId)!.ChargingEndedAtUtc);

                SendMeter(controller, "2026-03-09T23:35:00Z", 12500);

                db.ChangeTracker.Clear();
                var transaction = db.Transactions.Find(TransactionId)!;
                Assert.Null(transaction.ChargingEndedAtUtc);
                // Interval 22:00 -> 23:30 (resume time, not meter time) minus 60 minutes grace.
                Assert.Equal(30, transaction.IdleUsageFeeMinutes);
                Assert.Null(chargePointStatus.OnlineConnectors[1].IdleIntervalMeterKwh);
            });
        }

        [Fact]
        public void Charging_WithoutKnownMeter_ClosesIdleIntervalImmediately()
        {
            RunWithDatabase((db, chargePointStatus, controller) =>
            {
                chargePointStatus.OnlineConnectors[1].MeterKWH = null;

                SendStatus(controller, "Finishing", "2026-03-09T22:00:00Z");
                SendStatus(controller, "Charging", "2026-03-09T23:30:00Z");

                db.ChangeTracker.Clear();
                var transaction = db.Transactions.Find(TransactionId)!;
                Assert.Null(transaction.ChargingEndedAtUtc);
                Assert.Equal(30, transaction.IdleUsageFeeMinutes);
            });
        }

        private static void RunWithDatabase(Action<OCPPCoreContext, ChargePointStatus, ControllerOCPP16> test)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-idle-finishing-{Guid.NewGuid():N}.sqlite");
            try
            {
                using var db = CreateSqliteContext(databasePath);
                db.ChargePoints.Add(new ChargePoint { ChargePointId = "CP-TEST", Name = "CP-TEST" });
                db.Transactions.Add(new Transaction
                {
                    TransactionId = TransactionId,
                    ChargePointId = "CP-TEST",
                    ConnectorId = 1,
                    StartTagId = "TAG-1",
                    StartTime = Utc("2026-03-09T20:48:00Z"),
                    MeterStart = 0.0
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP-TEST",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-1",
                    OcppIdTag = "TAG-1",
                    TransactionId = TransactionId,
                    Status = PaymentReservationStatus.Charging,
                    UsageFeeAnchorMinutes = 1,
                    UsageFeePerMinute = 0.05m,
                    StartUsageFeeAfterMinutes = 60,
                    MaxUsageFeeMinutes = 360,
                    Currency = "eur",
                    CreatedAtUtc = Utc("2026-03-09T20:47:00Z"),
                    UpdatedAtUtc = Utc("2026-03-09T20:48:00Z")
                });
                db.SaveChanges();

                var chargePointStatus = new ChargePointStatus { Id = "CP-TEST" };
                chargePointStatus.OnlineConnectors[1] = new OnlineConnectorStatus
                {
                    Status = ConnectorStatusEnum.Occupied,
                    OcppStatus = "Charging",
                    MeterKWH = 12.0,
                    MeterValueDate = DateTimeOffset.Parse("2026-03-09T21:55:00Z")
                };

                var controller = new ControllerOCPP16(
                    new ConfigurationBuilder().AddInMemoryCollection().Build(),
                    NullLoggerFactory.Instance,
                    chargePointStatus,
                    db);

                test(db, chargePointStatus, controller);
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }

        private static void SendStatus(ControllerOCPP16 controller, string status, string timestamp)
        {
            var response = new OCPPMessage { MessageType = "3", UniqueId = $"status-{timestamp}" };
            var errorCode = controller.HandleStatusNotification(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"status-{timestamp}",
                JsonPayload = $"{{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"{status}\",\"timestamp\":\"{timestamp}\"}}"
            }, response, null);
            Assert.Null(errorCode);
        }

        private static void SendMeter(ControllerOCPP16 controller, string timestamp, int meterWh)
        {
            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"meter-{timestamp}",
                Action = "MeterValues",
                JsonPayload = $"{{\"connectorId\":1,\"transactionId\":{TransactionId},\"meterValue\":[{{\"timestamp\":\"{timestamp}\",\"sampledValue\":[{{\"value\":\"{meterWh}\",\"measurand\":\"Energy.Active.Import.Register\",\"unit\":\"Wh\"}}]}}]}}"
            }, null);
            Assert.Equal("3", response.MessageType);
        }

        private static void SendStop(ControllerOCPP16 controller, string timestamp)
        {
            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"stop-{timestamp}",
                Action = "StopTransaction",
                JsonPayload = $"{{\"idTag\":\"TAG-1\",\"meterStop\":12000,\"timestamp\":\"{timestamp}\",\"transactionId\":{TransactionId}}}"
            }, null);
            Assert.Equal("3", response.MessageType);
            Assert.Null(response.ErrorCode);
        }

        private static DateTime Utc(string value)
        {
            return DateTime.Parse(value).ToUniversalTime();
        }

        private static OCPPCoreContext CreateSqliteContext(string databasePath)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }
    }
}
