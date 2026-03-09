using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
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

        private static ControllerOCPP16 CreateController(OCPPCoreContext dbContext)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();

            return new ControllerOCPP16(
                configuration,
                NullLoggerFactory.Instance,
                new ChargePointStatus
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
    }
}
