using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ControllerBaseValidationTests
    {
        [Fact]
        public void DeserializeMessage_WithSchemaErrors_LogsAndDeserializesWithoutThrowing()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["ValidateMessages"] = "true"
                })
                .Build();

            using var context = CreateContext();
            var controller = new TestControllerOCPP16(
                configuration,
                NullLoggerFactory.Instance,
                new ChargePointStatus { Id = "CP-VALIDATION", Protocol = "ocpp1.6" },
                context);

            var request = controller.Deserialize<BootNotificationRequest>(new OCPPMessage
            {
                MessageType = "2",
                UniqueId = Guid.NewGuid().ToString("N"),
                Action = "BootNotification",
                JsonPayload = "{\"chargePointVendor\":\"123456789012345678901\",\"chargePointModel\":\"Model\"}"
            });

            Assert.NotNull(request);
            Assert.Equal("123456789012345678901", request.ChargePointVendor);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new OCPPCoreContext(options);
        }

        private sealed class TestControllerOCPP16 : ControllerOCPP16
        {
            public TestControllerOCPP16(
                IConfiguration config,
                Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
                ChargePointStatus chargePointStatus,
                OCPPCoreContext dbContext)
                : base(config, loggerFactory, chargePointStatus, dbContext)
            {
            }

            public T Deserialize<T>(OCPPMessage message)
            {
                return DeserializeMessage<T>(message);
            }
        }
    }
}
