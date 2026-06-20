using System;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using OCPP.Core.Management.Helpers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicPaymentRecoveryCookieTests
    {
        [Fact]
        public void TryRead_RoundTripsProtectedPayload()
        {
            var protector = CreateProtector();
            var payload = new PublicPaymentRecoveryPayload
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-COOKIE",
                ConnectorId = 2,
                CreatedAtUtc = DateTime.UtcNow
            };

            var protectedValue = PublicPaymentRecoveryCookie.Protect(protector, payload);
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = $"{PublicPaymentRecoveryCookie.CookieName}={protectedValue}";

            var success = PublicPaymentRecoveryCookie.TryRead(context.Request.Cookies, protector, out var recovered);

            Assert.True(success);
            Assert.NotNull(recovered);
            Assert.Equal(payload.ReservationId, recovered.ReservationId);
            Assert.Equal(payload.ChargePointId, recovered.ChargePointId);
            Assert.Equal(payload.ConnectorId, recovered.ConnectorId);
        }

        [Fact]
        public void TryRead_RejectsExpiredPayload()
        {
            var protector = CreateProtector();
            var payload = new PublicPaymentRecoveryPayload
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-COOKIE",
                ConnectorId = 1,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            };

            var protectedValue = PublicPaymentRecoveryCookie.Protect(protector, payload);
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = $"{PublicPaymentRecoveryCookie.CookieName}={protectedValue}";

            var success = PublicPaymentRecoveryCookie.TryRead(context.Request.Cookies, protector, out var recovered);

            Assert.False(success);
            Assert.Null(recovered);
        }

        private static IDataProtector CreateProtector()
        {
            return DataProtectionProvider.Create(
                    new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ocpp-public-recovery-cookie-tests")))
                .CreateProtector("OCPP.Core.Management.PublicPaymentRecovery.v1");
        }
    }
}
