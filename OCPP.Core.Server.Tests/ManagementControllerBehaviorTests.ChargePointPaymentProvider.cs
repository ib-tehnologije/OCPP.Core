using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public partial class ManagementControllerBehaviorTests
    {
        [Theory]
        [InlineData("Saferpay", "Saferpay")]
        [InlineData("stripe", "Stripe")]
        [InlineData("", null)]
        public void ChargePointEdit_SavesPaymentProviderOverride(string posted, string? expected)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-cp-provider-{Guid.NewGuid():N}.sqlite");
            try
            {
                using (var setup = CreateContext(databasePath))
                {
                    setup.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-PROVIDER",
                        Name = "Provider test",
                        PricePerKwh = 0.40m,
                        MaxSessionKwh = 10,
                        PaymentProvider = "Stripe"
                    });
                    setup.SaveChanges();
                }

                using (var actionContext = CreateContext(databasePath))
                {
                    var controller = CreateChargePointPostController(actionContext);
                    var result = controller.ChargePoint("CP-PROVIDER", PostedChargePoint(posted));
                    Assert.IsType<RedirectToActionResult>(result);
                }

                using var verify = CreateContext(databasePath);
                Assert.Equal(expected, verify.ChargePoints.Single(cp => cp.ChargePointId == "CP-PROVIDER").PaymentProvider);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void ChargePointEdit_RejectsUnknownPaymentProviderAndKeepsTheStoredOne()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-cp-provider-{Guid.NewGuid():N}.sqlite");
            try
            {
                using (var setup = CreateContext(databasePath))
                {
                    setup.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-PROVIDER",
                        Name = "Provider test",
                        PricePerKwh = 0.40m,
                        MaxSessionKwh = 10,
                        PaymentProvider = "Saferpay"
                    });
                    setup.SaveChanges();
                }

                using (var actionContext = CreateContext(databasePath))
                {
                    var controller = CreateChargePointPostController(actionContext);
                    var result = controller.ChargePoint("CP-PROVIDER", PostedChargePoint("PayPal"));
                    var view = Assert.IsType<ViewResult>(result);
                    Assert.Equal("ChargePointDetail", view.ViewName);
                    Assert.Contains("Unknown payment provider", (string)controller.ViewBag.ErrorMsg);
                }

                using var verify = CreateContext(databasePath);
                Assert.Equal("Saferpay", verify.ChargePoints.Single(cp => cp.ChargePointId == "CP-PROVIDER").PaymentProvider);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void ChargePointDisplay_ShowsStoredPaymentProvider()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-cp-provider-{Guid.NewGuid():N}.sqlite");
            try
            {
                using (var setup = CreateContext(databasePath))
                {
                    setup.ChargePoints.Add(new ChargePoint { ChargePointId = "CP-PROVIDER", Name = "Provider test", PaymentProvider = "Saferpay" });
                    setup.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateHomeController(actionContext, new Dictionary<string, string?>());
                ((DefaultHttpContext)controller.ControllerContext.HttpContext).Request.Method = "GET";

                var view = Assert.IsType<ViewResult>(controller.ChargePoint("CP-PROVIDER", new ChargePointViewModel()));
                Assert.Equal("Saferpay", Assert.IsType<ChargePointViewModel>(view.Model).PaymentProvider);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static OCPP.Core.Management.Controllers.HomeController CreateChargePointPostController(OCPPCoreContext dbContext)
        {
            var controller = CreateHomeController(dbContext, new Dictionary<string, string?>());
            var request = ((DefaultHttpContext)controller.ControllerContext.HttpContext).Request;
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["action"] = "Save"
            });
            return controller;
        }

        private static ChargePointViewModel PostedChargePoint(string paymentProvider) => new()
        {
            ChargePointId = "CP-PROVIDER",
            Name = "Provider test",
            PricePerKwh = 0.40m,
            MaxSessionKwh = 10,
            PaymentProvider = paymentProvider
        };
    }
}
