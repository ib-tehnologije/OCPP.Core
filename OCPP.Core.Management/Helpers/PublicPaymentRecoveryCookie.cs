using System;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace OCPP.Core.Management.Helpers
{
    public sealed class PublicPaymentRecoveryPayload
    {
        public Guid ReservationId { get; set; }
        public string ChargePointId { get; set; }
        public int ConnectorId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public static class PublicPaymentRecoveryCookie
    {
        public const string CookieName = "OCPP.PublicPaymentRecovery";
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        public static CookieOptions BuildCookieOptions(bool isHttps)
        {
            return new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = isHttps,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.Add(Lifetime)
            };
        }

        public static bool TryRead(
            IRequestCookieCollection cookies,
            IDataProtector protector,
            out PublicPaymentRecoveryPayload payload)
        {
            payload = null;
            if (cookies == null || protector == null) return false;
            if (!cookies.TryGetValue(CookieName, out var protectedValue) || string.IsNullOrWhiteSpace(protectedValue))
            {
                return false;
            }

            try
            {
                var json = protector.Unprotect(protectedValue);
                payload = JsonConvert.DeserializeObject<PublicPaymentRecoveryPayload>(json);
                if (payload == null || payload.ReservationId == Guid.Empty || string.IsNullOrWhiteSpace(payload.ChargePointId))
                {
                    payload = null;
                    return false;
                }

                if (payload.CreatedAtUtc == default || payload.CreatedAtUtc < DateTime.UtcNow.Subtract(Lifetime))
                {
                    payload = null;
                    return false;
                }

                return true;
            }
            catch
            {
                payload = null;
                return false;
            }
        }

        public static string Protect(IDataProtector protector, PublicPaymentRecoveryPayload payload)
        {
            if (protector == null) throw new ArgumentNullException(nameof(protector));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var json = JsonConvert.SerializeObject(payload);
            return protector.Protect(json);
        }
    }
}
