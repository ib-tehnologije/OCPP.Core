using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Diagnostic MessageLog content. The BootNotification request is the only message in which a charger
    /// reports vendor, model, serial numbers, firmware, modem ICCID/IMSI and meter; the OCPP 1.6
    /// StatusNotification carries the vendor-specific error code next to the generic one.
    /// </summary>
    public class DiagnosticMessageLogTests
    {
        private const string ChargePointId = "CP-TEST";

        private const string Boot16Payload =
            "{\"chargePointVendor\":\"ExampleVendor\",\"chargePointModel\":\"AC22-TEST\",\"chargePointSerialNumber\":\"SN-0001\"," +
            "\"chargeBoxSerialNumber\":\"BOX-0001\",\"firmwareVersion\":\"1.2.3\",\"iccid\":\"89000000000000000001\"," +
            "\"imsi\":\"001010000000001\",\"meterType\":\"TestMeter\",\"meterSerialNumber\":\"M-0001\"}";

        private const string Boot2xPayload =
            "{\"reason\":\"PowerUp\",\"chargingStation\":{\"model\":\"AC22-TEST\",\"vendorName\":\"ExampleVendor\"," +
            "\"serialNumber\":\"SN-0001\",\"firmwareVersion\":\"1.2.3\"," +
            "\"modem\":{\"iccid\":\"89000000000000000001\",\"imsi\":\"001010000000001\"}}}";

        [Fact]
        public void Ocpp16BootNotification_StoresRawRequestInMessageLog()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP16(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                var response = controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);

                Assert.Equal("3", response.MessageType);
                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "BootNotification"));
                Assert.Equal(Boot16Payload, entry.Result);
                Assert.Null(entry.ErrorCode);
                Assert.Null(entry.ConnectorId);
            });
        }

        [Fact]
        public void Ocpp16BootNotification_IsNotLoggedBelowFullMessageLogLevel()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP16(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                var response = controller.ProcessRequest(Request("BootNotification", Boot16Payload), null);

                Assert.Equal("3", response.MessageType);
                Assert.Empty(db.MessageLogs);
            }, dbMessageLog: 1);
        }

        [Fact]
        public void Ocpp20BootNotification_StoresRawRequestIncludingReason()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP20(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                var response = controller.ProcessRequest(Request("BootNotification", Boot2xPayload), null);

                Assert.Equal("3", response.MessageType);
                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "BootNotification"));
                Assert.Equal(Boot2xPayload, entry.Result);
                Assert.Null(entry.ErrorCode);
            });
        }

        [Fact]
        public void Ocpp21BootNotification_StoresRawRequestIncludingReason()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP21(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                var response = controller.ProcessRequest(Request("BootNotification", Boot2xPayload), null);

                Assert.Equal("3", response.MessageType);
                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "BootNotification"));
                Assert.Equal(Boot2xPayload, entry.Result);
                Assert.Null(entry.ErrorCode);
            });
        }

        [Fact]
        public void Ocpp16StatusNotification_AppendsVendorDiagnosticsAfterStatus()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP16(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                var response = controller.ProcessRequest(Request("StatusNotification",
                    "{\"connectorId\":1,\"errorCode\":\"OtherError\",\"info\":\"Undervoltage\",\"status\":\"Faulted\"," +
                    "\"timestamp\":\"2026-11-22T10:00:00Z\",\"vendorId\":\"ExampleVendor\",\"vendorErrorCode\":\"202\"}"), null);

                Assert.Equal("3", response.MessageType);
                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "StatusNotification"));
                Assert.Equal("Info=Undervoltage / Status=Faulted / VendorId=ExampleVendor / VendorErrorCode=202 / ", entry.Result);
                Assert.Equal("OtherError", entry.ErrorCode);
                Assert.Equal(1, entry.ConnectorId);
                // Readers that parse the status out of this text keep working.
                Assert.Equal("Faulted", Regex.Match(entry.Result, @"Status=(\w+)").Groups[1].Value);
                Assert.Equal("Faulted", Regex.Match(entry.Result, @"Status=([^/\s]+)").Groups[1].Value);
            });
        }

        [Fact]
        public void Ocpp16StatusNotification_VendorErrorCodeWithoutVendorId_IsAppendedAlone()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP16(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                controller.ProcessRequest(Request("StatusNotification",
                    "{\"connectorId\":1,\"errorCode\":\"GroundFailure\",\"status\":\"Faulted\"," +
                    "\"timestamp\":\"2026-11-22T10:00:00Z\",\"vendorErrorCode\":\"0x1F\"}"), null);

                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "StatusNotification"));
                Assert.Equal("Info= / Status=Faulted / VendorErrorCode=0x1F / ", entry.Result);
            });
        }

        [Fact]
        public void Ocpp16StatusNotification_VendorTextDoesNotChangeTheParsedStatus()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP16(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                controller.ProcessRequest(Request("StatusNotification",
                    "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"Available\",\"timestamp\":\"2026-11-22T10:00:00Z\"," +
                    "\"vendorId\":\"Example Charging\",\"vendorErrorCode\":\"E 12/Faulted\"}"), null);

                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "StatusNotification"));
                Assert.Equal("Info= / Status=Available / VendorId=Example Charging / VendorErrorCode=E 12/Faulted / ", entry.Result);
                Assert.Equal("Available", Regex.Match(entry.Result, @"Status=(\w+)").Groups[1].Value);
                Assert.Equal("Available", Regex.Match(entry.Result, @"Status=([^/\s]+)").Groups[1].Value);
            });
        }

        [Fact]
        public void Ocpp16StatusNotification_WithoutVendorFields_KeepsExistingText()
        {
            RunWithDatabase((db, chargePointStatus, configuration) =>
            {
                var controller = new ControllerOCPP16(configuration, NullLoggerFactory.Instance, chargePointStatus, db);

                var response = controller.ProcessRequest(Request("StatusNotification",
                    "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"Available\",\"timestamp\":\"2026-11-22T10:00:00Z\"}"), null);

                Assert.Equal("3", response.MessageType);
                var entry = Assert.Single(db.MessageLogs.Where(m => m.Message == "StatusNotification"));
                Assert.Equal("Info= / Status=Available / ", entry.Result);
                Assert.Equal("NoError", entry.ErrorCode);
            });
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

        private static void RunWithDatabase(Action<OCPPCoreContext, ChargePointStatus, IConfiguration> test, int dbMessageLog = 2)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-diagnostic-log-{Guid.NewGuid():N}.sqlite");
            try
            {
                using var db = CreateSqliteContext(databasePath);
                db.ChargePoints.Add(new ChargePoint { ChargePointId = ChargePointId, Name = ChargePointId });
                db.SaveChanges();

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["DbMessageLog"] = dbMessageLog.ToString() })
                    .Build();

                test(db, new ChargePointStatus { Id = ChargePointId }, configuration);
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

        private static OCPPCoreContext CreateSqliteContext(string databasePath)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }
    }
}
