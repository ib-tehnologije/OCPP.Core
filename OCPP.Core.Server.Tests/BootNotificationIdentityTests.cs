using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// BootNotification identity (vendor, model, serial numbers, firmware, modem ICCID/IMSI, meter)
    /// stored on the ChargePoint row, next to the raw request in MessageLog.
    /// </summary>
    public class BootNotificationIdentityTests
    {
        private const string ChargePointId = "CP-BOOT";

        private const string Boot16Payload =
            "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargePointSerialNumber\":\"SN-0001\"," +
            "\"chargeBoxSerialNumber\":\"BOX-0001\",\"firmwareVersion\":\"1.2.3\",\"iccid\":\"89000000000000000001\"," +
            "\"imsi\":\"001010000000001\",\"meterType\":\"TestMeter\",\"meterSerialNumber\":\"M-0001\"}";

        private const string Boot2xPayload =
            "{\"reason\":\"PowerUp\",\"chargingStation\":{\"model\":\"AC22-TEST\",\"vendorName\":\"ExampleVendor\"," +
            "\"serialNumber\":\"SN-0001\",\"firmwareVersion\":\"1.2.3\"," +
            "\"modem\":{\"iccid\":\"89000000000000000001\",\"imsi\":\"001010000000001\"}}}";

        [Fact]
        public void Ocpp16Boot_StoresReportedIdentityOnChargePoint()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var response = Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification", Boot16Payload), null);

                Assert.Equal("3", response.MessageType);
                var cp = ReadChargePoint(path);
                Assert.Equal("ExampleVendor", cp.ChargePointVendor);
                Assert.Equal("AC22-TEST", cp.ChargePointModel);
                Assert.Equal("SN-0001", cp.ChargePointSerialNumber);
                Assert.Equal("BOX-0001", cp.ChargeBoxSerialNumber);
                Assert.Equal("1.2.3", cp.FirmwareVersion);
                Assert.Equal("89000000000000000001", cp.Iccid);
                Assert.Equal("001010000000001", cp.Imsi);
                Assert.Equal("TestMeter", cp.MeterType);
                Assert.Equal("M-0001", cp.MeterSerialNumber);
                // The raw request is still kept in MessageLog as history.
                Assert.Equal(Boot16Payload, Assert.Single(db.MessageLogs.Where(m => m.Message == "BootNotification")).Result);
            });
        }

        [Fact]
        public void Ocpp20Boot_StoresStationAndModemIdentity()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var response = new ControllerOCPP20(configuration, NullLoggerFactory.Instance, status, db)
                    .ProcessRequest(Request("BootNotification", Boot2xPayload), null);

                Assert.Equal("3", response.MessageType);
                AssertStationIdentity(ReadChargePoint(path));
            });
        }

        [Fact]
        public void Ocpp21Boot_StoresStationAndModemIdentity()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var response = new ControllerOCPP21(configuration, NullLoggerFactory.Instance, status, db)
                    .ProcessRequest(Request("BootNotification", Boot2xPayload), null);

                Assert.Equal("3", response.MessageType);
                AssertStationIdentity(ReadChargePoint(path));
            });
        }

        [Fact]
        public void LaterBoot_UpdatesSentValues_KeepsOmittedAndEmptyOnes()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear(); // as the middleware does before every message

                // Firmware update; the modem was not ready, so ICCID is empty and IMSI is missing.
                var response = controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"firmwareVersion\":\"1.3.0\",\"iccid\":\"\"}"), null);

                Assert.Equal("3", response.MessageType);
                var cp = ReadChargePoint(path);
                Assert.Equal("1.3.0", cp.FirmwareVersion);
                Assert.Equal("89000000000000000001", cp.Iccid);
                Assert.Equal("001010000000001", cp.Imsi);
                Assert.Equal("SN-0001", cp.ChargePointSerialNumber);
                Assert.Equal("BOX-0001", cp.ChargeBoxSerialNumber);
                Assert.Equal(2, db.MessageLogs.Count(m => m.Message == "BootNotification"));
            });
        }

        [Fact]
        public void PaddedValuesAreTrimmed_WhitespaceOnlyValuesAreIgnored()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\" AC22-TEST \",\"firmwareVersion\":\"  1.3.0  \",\"iccid\":\"   \"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("AC22-TEST", cp.ChargePointModel);
                Assert.Equal("1.3.0", cp.FirmwareVersion);
                Assert.Equal("89000000000000000001", cp.Iccid);
            });
        }

        [Fact]
        public void NewSimInSameCharger_ReplacesIccidAndImsi_KeepsTheRest()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargePointSerialNumber\":\"SN-0001\"," +
                    "\"iccid\":\"89000000000000000002\",\"imsi\":\"001010000000002\"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("89000000000000000002", cp.Iccid);
                Assert.Equal("001010000000002", cp.Imsi);
                Assert.Equal("BOX-0001", cp.ChargeBoxSerialNumber);
                Assert.Equal("1.2.3", cp.FirmwareVersion);
                Assert.Equal("M-0001", cp.MeterSerialNumber);
            });
        }

        [Fact]
        public void DifferentSerialNumber_IsAnotherCharger_ClearsValuesTheBootDidNotSend()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                // Replacement charger of the same type under the same ID, without a modem and meter data.
                var response = controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargePointSerialNumber\":\"SN-0002\",\"firmwareVersion\":\"2.0.0\"}"), null);

                Assert.Equal("3", response.MessageType);
                var cp = ReadChargePoint(path);
                Assert.Equal("ExampleVendor", cp.ChargePointVendor);
                Assert.Equal("AC22-TEST", cp.ChargePointModel);
                Assert.Equal("SN-0002", cp.ChargePointSerialNumber);
                Assert.Equal("2.0.0", cp.FirmwareVersion);
                Assert.Null(cp.ChargeBoxSerialNumber);
                Assert.Null(cp.Iccid);
                Assert.Null(cp.Imsi);
                Assert.Null(cp.MeterType);
                Assert.Null(cp.MeterSerialNumber);
            });
        }

        [Fact]
        public void DifferentModelWithoutSerialNumber_IsAlsoAnotherCharger()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"iccid\":\"89000000000000000001\"}"), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"DC50-TEST\"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("DC50-TEST", cp.ChargePointModel);
                Assert.Null(cp.Iccid);
            });
        }

        [Fact]
        public void DifferentVendorWithoutSerialNumber_IsAnotherCharger()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"iccid\":\"89000000000000000001\"}"), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"OtherVendor\",\"chargePointModel\":\"AC22-TEST\"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("OtherVendor", cp.ChargePointVendor);
                Assert.Null(cp.Iccid);
            });
        }

        [Fact]
        public void DifferentChargeBoxSerialNumber_IsAnotherCharger()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargeBoxSerialNumber\":\"BOX-0001\",\"iccid\":\"89000000000000000001\"}"), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargeBoxSerialNumber\":\"BOX-0002\"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("BOX-0002", cp.ChargeBoxSerialNumber);
                Assert.Null(cp.Iccid);
            });
        }

        [Fact]
        public void SerialNumberSentForTheFirstTime_IsTheSameCharger()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"iccid\":\"89000000000000000001\"}"), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargePointSerialNumber\":\"SN-0001\"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("SN-0001", cp.ChargePointSerialNumber);
                Assert.Equal("89000000000000000001", cp.Iccid);
            });
        }

        [Fact]
        public void SameSerialNumber_WithVendorAndModelWrittenDifferently_IsTheSameCharger()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                // After a move to OCPP 2.0.1 the charger reports its full vendor name and a longer model name.
                new ControllerOCPP20(configuration, NullLoggerFactory.Instance, status, db).ProcessRequest(Request("BootNotification",
                    "{\"reason\":\"PowerUp\",\"chargingStation\":{\"model\":\"AC22-TEST Rev B\",\"vendorName\":\"ExampleVendor Charging Solutions\"," +
                    "\"serialNumber\":\"SN-0001\",\"firmwareVersion\":\"2.0.0\"}}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("ExampleVendor Charging Solutions", cp.ChargePointVendor);
                Assert.Equal("AC22-TEST Rev B", cp.ChargePointModel);
                Assert.Equal("2.0.0", cp.FirmwareVersion);
                Assert.Equal("BOX-0001", cp.ChargeBoxSerialNumber);
                Assert.Equal("89000000000000000001", cp.Iccid);
                Assert.Equal("M-0001", cp.MeterSerialNumber);
            });
        }

        [Fact]
        public void VendorCaseChangeWithoutSerialNumber_IsTheSameCharger()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"iccid\":\"89000000000000000001\"}"), null);
                db.ChangeTracker.Clear();

                // Firmware update writes the vendor in upper case; the modem is not ready yet.
                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"EXAMPLEVENDOR\",\"chargePointModel\":\"AC22-TEST\",\"firmwareVersion\":\"1.3.0\"}"), null);

                var cp = ReadChargePoint(path);
                Assert.Equal("EXAMPLEVENDOR", cp.ChargePointVendor);
                Assert.Equal("1.3.0", cp.FirmwareVersion);
                Assert.Equal("89000000000000000001", cp.Iccid);
            });
        }

        [Fact]
        public void AnotherCharger_IsLoggedWithDifferingValuesAndClearedColumns_WithoutSimValues()
        {
            var logs = new RecordingLoggerProvider();
            using var loggerFactory = new LoggerFactory(new[] { logs });
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = new ControllerOCPP16(configuration, loggerFactory, status, db);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification",
                    "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargePointSerialNumber\":\"SN-0002\",\"firmwareVersion\":\"2.0.0\"}"), null);
            });

            string warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning).Message;
            Assert.Contains("ChargePointSerialNumber 'SN-0001' -> 'SN-0002'", warning);
            Assert.Contains("cleared ChargeBoxSerialNumber, Iccid, Imsi, MeterType, MeterSerialNumber", warning);
            Assert.DoesNotContain("89000000000000000001", warning);
            Assert.DoesNotContain("001010000000001", warning);
        }

        [Fact]
        public void Ocpp16BootWithoutPayload_IsAcceptedAndKeepsIdentity()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                var response = controller.ProcessRequest(Request("BootNotification", "null"), null);

                Assert.Equal("3", response.MessageType);
                Assert.Contains("Accepted", response.JsonPayload);
                Assert.Equal("89000000000000000001", ReadChargePoint(path).Iccid);
            });
        }

        [Fact]
        public void SameChargerSwitchingToOcpp20_KeepsOcpp16OnlyValues()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification", Boot16Payload), null);
                db.ChangeTracker.Clear();

                var response = new ControllerOCPP20(configuration, NullLoggerFactory.Instance, status, db)
                    .ProcessRequest(Request("BootNotification", Boot2xPayload), null);

                Assert.Equal("3", response.MessageType);
                var cp = ReadChargePoint(path);
                Assert.Equal("BOX-0001", cp.ChargeBoxSerialNumber);
                Assert.Equal("TestMeter", cp.MeterType);
                Assert.Equal("M-0001", cp.MeterSerialNumber);
            });
        }

        [Fact]
        public void RepeatedBootWithSameValues_DoesNotUpdateChargePoint()
        {
            var counter = new ChargePointUpdateCounter();
            RunWithDatabase((db, status, configuration, path) =>
            {
                var controller = Ocpp16(db, status, configuration);
                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);
                Assert.Equal(1, counter.Updates);
                db.ChangeTracker.Clear();

                controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);

                Assert.Equal(1, counter.Updates);
            }, interceptor: counter);
        }

        [Fact]
        public void OverlongValues_AreCutToColumnLength()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                string vendor = new string('V', 60);
                string iccid = "8900000000000000000123";
                var response = Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification",
                    $"{{\"chargePointVendor\":\"{vendor}\",\"chargePointModel\":\"AC22-TEST\",\"iccid\":\"{iccid}\"}}"), null);

                Assert.Equal("3", response.MessageType);
                var cp = ReadChargePoint(path);
                Assert.Equal(vendor.Substring(0, 50), cp.ChargePointVendor);
                Assert.Equal(iccid.Substring(0, 20), cp.Iccid);
            });
        }

        [Fact]
        public void OverlongValue_IsNotCutInsideASurrogatePair()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                string vendor = new string('V', 49) + "😀"; // 51 UTF-16 units, the cut at 50 would split the emoji
                Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification",
                    $"{{\"chargePointVendor\":\"{vendor}\",\"chargePointModel\":\"AC22-TEST\"}}"), null);

                Assert.Equal(new string('V', 49), ReadChargePoint(path).ChargePointVendor);
            });
        }

        [Fact]
        public void Identity_IsStoredWhenMessageLogIsOff()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                var response = Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification", Boot16Payload), null);

                Assert.Equal("3", response.MessageType);
                Assert.Equal("ExampleVendor", ReadChargePoint(path).ChargePointVendor);
                Assert.Empty(db.MessageLogs);
            }, dbMessageLog: 0);
        }

        [Fact]
        public void DatabaseErrorOnIdentityUpdate_StillAcceptsBootAndWritesMessageLog()
        {
            var failing = new FailChargePointUpdateCommand();
            RunWithDatabase((db, status, configuration, path) =>
            {
                var response = Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification", Boot16Payload), null);

                Assert.Equal("3", response.MessageType);
                Assert.Contains("Accepted", response.JsonPayload);
                Assert.Equal(1, failing.Attempts);
                Assert.Null(ReadChargePoint(path).ChargePointVendor);
                Assert.Equal(Boot16Payload, Assert.Single(ReadMessageLogs(path)).Result);
            }, interceptor: failing);
        }

        [Fact]
        public void ManagementSaveAfterBoot_KeepsIdentity()
        {
            RunWithDatabase((db, status, configuration, path) =>
            {
                // The management app loaded the charge point before the boot and saves its own fields afterwards.
                using var management = CreateSqliteContext(path);
                var edited = management.ChargePoints.Single(cp => cp.ChargePointId == ChargePointId);

                Ocpp16(db, status, configuration).ProcessRequest(Request("BootNotification", Boot16Payload), null);

                edited.Name = "Renamed";
                edited.Comment = "Edited in management";
                management.SaveChanges();

                var cp = ReadChargePoint(path);
                Assert.Equal("Renamed", cp.Name);
                Assert.Equal("ExampleVendor", cp.ChargePointVendor);
                Assert.Equal("89000000000000000001", cp.Iccid);
            });
        }

        private static ControllerOCPP16 Ocpp16(OCPPCoreContext db, ChargePointStatus status, IConfiguration configuration)
        {
            return new ControllerOCPP16(configuration, NullLoggerFactory.Instance, status, db);
        }

        private static void AssertStationIdentity(ChargePoint cp)
        {
            Assert.Equal("ExampleVendor", cp.ChargePointVendor);
            Assert.Equal("AC22-TEST", cp.ChargePointModel);
            Assert.Equal("SN-0001", cp.ChargePointSerialNumber);
            Assert.Equal("1.2.3", cp.FirmwareVersion);
            Assert.Equal("89000000000000000001", cp.Iccid);
            Assert.Equal("001010000000001", cp.Imsi);
            // OCPP 2.x has no charge box serial number or meter fields.
            Assert.Null(cp.ChargeBoxSerialNumber);
            Assert.Null(cp.MeterType);
            Assert.Null(cp.MeterSerialNumber);
        }

        private static OCPPMessage Request(string action, string payload)
        {
            return new OCPPMessage
            {
                MessageType = "2",
                UniqueId = $"{action}-{Guid.NewGuid():N}".Substring(0, 36),
                Action = action,
                JsonPayload = payload
            };
        }

        // Reads through a separate context, so the assertions see what reached the database.
        private static ChargePoint ReadChargePoint(string databasePath)
        {
            using var db = CreateSqliteContext(databasePath);
            return db.ChargePoints.AsNoTracking().Single(cp => cp.ChargePointId == ChargePointId);
        }

        private static List<MessageLog> ReadMessageLogs(string databasePath)
        {
            using var db = CreateSqliteContext(databasePath);
            return db.MessageLogs.AsNoTracking().ToList();
        }

        private static void RunWithDatabase(Action<OCPPCoreContext, ChargePointStatus, IConfiguration, string> test,
            int dbMessageLog = 2, IInterceptor? interceptor = null)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-boot-identity-{Guid.NewGuid():N}.sqlite");
            try
            {
                using (var seed = CreateSqliteContext(databasePath))
                {
                    seed.ChargePoints.Add(new ChargePoint { ChargePointId = ChargePointId, Name = ChargePointId });
                    seed.SaveChanges();
                }

                using var db = CreateSqliteContext(databasePath, interceptor);
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["DbMessageLog"] = dbMessageLog.ToString() })
                    .Build();

                test(db, new ChargePointStatus { Id = ChargePointId }, configuration, databasePath);
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

        private static OCPPCoreContext CreateSqliteContext(string databasePath, IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False");
            if (interceptor != null)
            {
                builder.AddInterceptors(interceptor);
            }
            var context = new OCPPCoreContext(builder.Options);
            context.Database.EnsureCreated();
            return context;
        }

        private static bool IsChargePointUpdate(DbCommand command)
        {
            return command.CommandText.Contains("UPDATE \"ChargePoint\"", StringComparison.Ordinal);
        }

        /// <summary>Fails the UPDATE of the ChargePoint row in the database command layer, like a real database error.</summary>
        private sealed class FailChargePointUpdateCommand : DbCommandInterceptor
        {
            public int Attempts { get; private set; }

            public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                Fail(command);
                return result;
            }

            public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
            {
                Fail(command);
                return result;
            }

            private void Fail(DbCommand command)
            {
                if (IsChargePointUpdate(command))
                {
                    Attempts++;
                    throw new SqliteException("Simulated database failure", 19);
                }
            }
        }

        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

            public void Dispose()
            {
            }

            private sealed class RecordingLogger : ILogger
            {
                private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries;

                public RecordingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries)
                {
                    _entries = entries;
                }

                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    _entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }

        /// <summary>Counts UPDATE commands on the ChargePoint row.</summary>
        private sealed class ChargePointUpdateCounter : DbCommandInterceptor
        {
            public int Updates { get; private set; }

            public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                if (IsChargePointUpdate(command))
                {
                    Updates++;
                }
                return result;
            }

            public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
            {
                if (IsChargePointUpdate(command))
                {
                    Updates++;
                }
                return result;
            }
        }
    }
}
