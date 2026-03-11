using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class StopTransactionControllerTests
    {
        [Fact]
        public void ProcessRequest_AcceptsUnknownStopTransaction()
        {
            using var db = CreateContext(Guid.NewGuid().ToString());
            var controller = CreateController(db);

            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "msg-unknown",
                Action = "StopTransaction",
                JsonPayload = "{\"meterStop\":6400,\"timestamp\":\"2026-03-09T10:15:00Z\",\"transactionId\":999}"
            }, null);

            Assert.Equal("3", response.MessageType);
            Assert.Null(response.ErrorCode);
            Assert.Equal("Accepted", JObject.Parse(response.JsonPayload)["idTagInfo"]?["status"]?.Value<string>());
        }

        [Fact]
        public void ProcessRequest_AcceptsAlreadyClosedStopTransaction()
        {
            using var db = CreateContext(Guid.NewGuid().ToString());
            db.Transactions.Add(new Transaction
            {
                TransactionId = 7,
                ChargePointId = "CP-TEST",
                ConnectorId = 1,
                StartTagId = "TAG-1",
                StartTime = DateTime.UtcNow.AddMinutes(-30),
                StopTime = DateTime.UtcNow.AddMinutes(-1),
                MeterStart = 1.0,
                MeterStop = 2.0
            });
            db.SaveChanges();

            var controller = CreateController(db);

            var response = controller.ProcessRequest(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = "msg-closed",
                Action = "StopTransaction",
                JsonPayload = "{\"meterStop\":6400,\"timestamp\":\"2026-03-09T10:16:00Z\",\"transactionId\":7}"
            }, null);

            Assert.Equal("3", response.MessageType);
            Assert.Null(response.ErrorCode);
            Assert.Equal("Accepted", JObject.Parse(response.JsonPayload)["idTagInfo"]?["status"]?.Value<string>());
        }

        [Fact]
        public void HandleStatusNotification_PersistsRawSuspendedEvStatus()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status16-{Guid.NewGuid():N}.sqlite");

            try
            {
                using var db = CreateSqliteContext(databasePath);
                db.ChargePoints.Add(new ChargePoint
                {
                    ChargePointId = "CP-TEST",
                    Name = "CP-TEST"
                });
                db.SaveChanges();

                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP-TEST"
                };
                var controller = CreateController(db, chargePointStatus);
                var response = new OCPPMessage
                {
                    MessageType = "3",
                    UniqueId = "msg-suspended-ev"
                };

                var errorCode = controller.HandleStatusNotification(new OCPPMessage
                {
                    MessageType = "2",
                    UniqueId = "msg-suspended-ev",
                    JsonPayload = "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"SuspendedEV\",\"timestamp\":\"2026-03-09T10:17:00Z\"}"
                }, response, null);

                Assert.Null(errorCode);
                Assert.NotNull(response.JsonPayload);

                db.ChangeTracker.Clear();
                var persisted = db.ConnectorStatuses.Find("CP-TEST", 1);
                Assert.NotNull(persisted);
                Assert.Equal("SuspendedEV", persisted.LastStatus);

                Assert.True(chargePointStatus.OnlineConnectors.ContainsKey(1));
                Assert.Equal(ConnectorStatusEnum.Occupied, chargePointStatus.OnlineConnectors[1].Status);
                Assert.Equal("SuspendedEV", chargePointStatus.OnlineConnectors[1].OcppStatus);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void HandleStatusNotification_AccumulatesIdleMinutesAcrossSuspendedEvIntervals()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-idle-intervals-{Guid.NewGuid():N}.sqlite");

            try
            {
                using var db = CreateSqliteContext(databasePath);
                db.ChargePoints.Add(new ChargePoint
                {
                    ChargePointId = "CP-TEST",
                    Name = "CP-TEST"
                });
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 42,
                    ChargePointId = "CP-TEST",
                    ConnectorId = 1,
                    StartTagId = "TAG-1",
                    StartTime = DateTime.Parse("2026-03-09T10:00:00Z").ToUniversalTime(),
                    MeterStart = 1.0
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP-TEST",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-1",
                    OcppIdTag = "TAG-1",
                    TransactionId = 42,
                    Status = PaymentReservationStatus.Charging,
                    UsageFeeAnchorMinutes = 1,
                    UsageFeePerMinute = 0.50m,
                    StartUsageFeeAfterMinutes = 0,
                    MaxUsageFeeMinutes = 120,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.Parse("2026-03-09T09:55:00Z").ToUniversalTime(),
                    UpdatedAtUtc = DateTime.Parse("2026-03-09T10:00:00Z").ToUniversalTime()
                });
                db.SaveChanges();

                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP-TEST"
                };
                chargePointStatus.OnlineConnectors[1] = new OnlineConnectorStatus
                {
                    Status = ConnectorStatusEnum.Occupied,
                    OcppStatus = "Charging"
                };

                var controller = CreateController(db, chargePointStatus);
                var statusResponse = new OCPPMessage
                {
                    MessageType = "3",
                    UniqueId = "msg-idle"
                };

                controller.HandleStatusNotification(new OCPPMessage
                {
                    MessageType = "2",
                    UniqueId = "msg-idle-open-1",
                    JsonPayload = "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"SuspendedEV\",\"timestamp\":\"2026-03-09T10:05:00Z\"}"
                }, statusResponse, null);

                db.ChangeTracker.Clear();
                var afterFirstSuspend = db.Transactions.Find(42);
                Assert.NotNull(afterFirstSuspend);
                Assert.Equal(DateTime.Parse("2026-03-09T10:05:00Z").ToUniversalTime(), afterFirstSuspend!.ChargingEndedAtUtc);
                Assert.Equal(0, afterFirstSuspend.IdleUsageFeeMinutes);

                controller.HandleStatusNotification(new OCPPMessage
                {
                    MessageType = "2",
                    UniqueId = "msg-idle-resume",
                    JsonPayload = "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"Charging\",\"timestamp\":\"2026-03-09T10:08:00Z\"}"
                }, statusResponse, null);

                db.ChangeTracker.Clear();
                var afterResume = db.Transactions.Find(42);
                Assert.NotNull(afterResume);
                Assert.Null(afterResume!.ChargingEndedAtUtc);
                Assert.Equal(3, afterResume.IdleUsageFeeMinutes);
                Assert.Equal(1.5m, afterResume.IdleUsageFeeAmount);

                controller.HandleStatusNotification(new OCPPMessage
                {
                    MessageType = "2",
                    UniqueId = "msg-idle-open-2",
                    JsonPayload = "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"SuspendedEV\",\"timestamp\":\"2026-03-09T10:12:00Z\"}"
                }, statusResponse, null);

                var stopResponse = controller.ProcessRequest(new OCPPMessage
                {
                    MessageType = "2",
                    UniqueId = "msg-stop",
                    Action = "StopTransaction",
                    JsonPayload = "{\"idTag\":\"TAG-1\",\"meterStop\":6400,\"timestamp\":\"2026-03-09T10:17:00Z\",\"transactionId\":42}"
                }, null);

                Assert.Equal("3", stopResponse.MessageType);
                Assert.Null(stopResponse.ErrorCode);

                db.ChangeTracker.Clear();
                var afterStop = db.Transactions.Find(42);
                Assert.NotNull(afterStop);
                Assert.Equal(8, afterStop!.IdleUsageFeeMinutes);
                Assert.Equal(4.0m, afterStop.IdleUsageFeeAmount);
                Assert.Null(afterStop.ChargingEndedAtUtc);
                Assert.NotNull(afterStop.StopTime);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void HandleStatusNotification_SuspendedEvseDoesNotOpenIdleInterval()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-idle-evse-{Guid.NewGuid():N}.sqlite");

            try
            {
                using var db = CreateSqliteContext(databasePath);
                db.ChargePoints.Add(new ChargePoint
                {
                    ChargePointId = "CP-TEST",
                    Name = "CP-TEST"
                });
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 21,
                    ChargePointId = "CP-TEST",
                    ConnectorId = 1,
                    StartTagId = "TAG-1",
                    StartTime = DateTime.Parse("2026-03-09T10:00:00Z").ToUniversalTime(),
                    MeterStart = 1.0
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP-TEST",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-1",
                    OcppIdTag = "TAG-1",
                    TransactionId = 21,
                    Status = PaymentReservationStatus.Charging,
                    UsageFeeAnchorMinutes = 1,
                    UsageFeePerMinute = 0.50m,
                    StartUsageFeeAfterMinutes = 0,
                    MaxUsageFeeMinutes = 120,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.Parse("2026-03-09T09:55:00Z").ToUniversalTime(),
                    UpdatedAtUtc = DateTime.Parse("2026-03-09T10:00:00Z").ToUniversalTime()
                });
                db.SaveChanges();

                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP-TEST"
                };
                chargePointStatus.OnlineConnectors[1] = new OnlineConnectorStatus
                {
                    Status = ConnectorStatusEnum.Occupied,
                    OcppStatus = "Charging"
                };

                var controller = CreateController(db, chargePointStatus);
                var response = new OCPPMessage
                {
                    MessageType = "3",
                    UniqueId = "msg-suspended-evse"
                };

                var errorCode = controller.HandleStatusNotification(new OCPPMessage
                {
                    MessageType = "2",
                    UniqueId = "msg-suspended-evse",
                    JsonPayload = "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"SuspendedEVSE\",\"timestamp\":\"2026-03-09T10:05:00Z\"}"
                }, response, null);

                Assert.Null(errorCode);

                db.ChangeTracker.Clear();
                var transaction = db.Transactions.Find(21);
                Assert.NotNull(transaction);
                Assert.Null(transaction!.ChargingEndedAtUtc);
                Assert.Equal(0, transaction.IdleUsageFeeMinutes);
                Assert.Equal(0m, transaction.IdleUsageFeeAmount);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static ControllerOCPP16 CreateController(OCPPCoreContext dbContext, ChargePointStatus? chargePointStatus = null)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();

            return new ControllerOCPP16(
                configuration,
                NullLoggerFactory.Instance,
                chargePointStatus ?? new ChargePointStatus
                {
                    Id = "CP-TEST"
                },
                dbContext);
        }

        private static OCPPCoreContext CreateContext(string databaseName)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
            return new OCPPCoreContext(options);
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

        private static void TryDelete(string databasePath)
        {
            try
            {
                File.Delete(databasePath);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
