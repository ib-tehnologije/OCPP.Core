/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Security.Cryptography;
using System.Text;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    /// <summary>
    /// Signs and verifies the Saferpay success/fail notification URLs.
    /// </summary>
    /// <remarks>
    /// Saferpay notifications are plain GET calls without payload, signature or authentication. The URL we
    /// hand to Saferpay therefore carries the reservation id and an HMAC over it, so only URLs we issued are
    /// accepted. Even a valid notification is only a "re-read this checkout" signal: the coordinator always
    /// asserts the payment server-side before changing state.
    /// <para>
    /// Signature: base64url (no padding) of HMAC-SHA256 over <c>saferpay-notify:{reservationId:D}</c>. The key is
    /// <see cref="SaferpayOptions.NotifySecret"/> (UTF-8) or, when it is empty, SHA-256 of
    /// <c>saferpay-notify-key:{ApiPassword}:{CustomerId}</c>.
    /// </para>
    /// </remarks>
    internal sealed class SaferpayNotificationSigner
    {
        /// <summary>Route below <see cref="SaferpayOptions.NotifyBaseUrl"/> (the server <c>/API</c> base) that receives notifications.</summary>
        public const string NotifyPath = "/Payments/Webhook/Saferpay";

        public const string ReservationIdParameter = "reservationId";
        public const string SignatureParameter = "sig";
        public const string OutcomeParameter = "outcome";
        public const string OutcomeSuccess = "success";
        public const string OutcomeFail = "fail";

        private const string SignedPayloadPrefix = "saferpay-notify:";
        private const string DerivedKeyPrefix = "saferpay-notify-key:";

        private readonly byte[] _key;
        private readonly string _notifyBaseUrl;

        public SaferpayNotificationSigner(SaferpayOptions options)
        {
            options ??= new SaferpayOptions();
            _key = DeriveKey(options);
            _notifyBaseUrl = string.IsNullOrWhiteSpace(options.NotifyBaseUrl)
                ? null
                : options.NotifyBaseUrl.Trim().TrimEnd('/');
        }

        /// <summary>True when <see cref="SaferpayOptions.NotifyBaseUrl"/> is configured and notification URLs are sent.</summary>
        public bool CanBuildNotifyUrls => _notifyBaseUrl != null;

        public string Sign(Guid reservationId)
        {
            using var hmac = new HMACSHA256(_key);
            byte[] signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(SignedPayloadPrefix + reservationId.ToString("D")));
            return Base64UrlEncode(signature);
        }

        /// <summary>Constant-time check of a signature taken from a notification URL.</summary>
        public bool Verify(Guid reservationId, string signature)
        {
            if (reservationId == Guid.Empty || string.IsNullOrWhiteSpace(signature) ||
                !TryBase64UrlDecode(signature.Trim(), out var provided))
            {
                return false;
            }

            using var hmac = new HMACSHA256(_key);
            byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(SignedPayloadPrefix + reservationId.ToString("D")));
            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }

        /// <summary>Success notification URL, or null when <see cref="SaferpayOptions.NotifyBaseUrl"/> is empty.</summary>
        public string BuildSuccessNotifyUrl(Guid reservationId) => BuildNotifyUrl(reservationId, fail: false);

        /// <summary>Fail notification URL, or null when <see cref="SaferpayOptions.NotifyBaseUrl"/> is empty.</summary>
        public string BuildFailNotifyUrl(Guid reservationId) => BuildNotifyUrl(reservationId, fail: true);

        private string BuildNotifyUrl(Guid reservationId, bool fail)
        {
            if (_notifyBaseUrl == null)
            {
                return null;
            }

            string url = $"{_notifyBaseUrl}{NotifyPath}?{ReservationIdParameter}={reservationId:D}&{SignatureParameter}={Uri.EscapeDataString(Sign(reservationId))}";
            return fail ? $"{url}&{OutcomeParameter}={OutcomeFail}" : url;
        }

        private static byte[] DeriveKey(SaferpayOptions options)
        {
            if (!string.IsNullOrEmpty(options.NotifySecret))
            {
                return Encoding.UTF8.GetBytes(options.NotifySecret);
            }

            return SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{DerivedKeyPrefix}{options.ApiPassword ?? string.Empty}:{options.CustomerId ?? string.Empty}"));
        }

        internal static string Base64UrlEncode(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool TryBase64UrlDecode(string value, out byte[] decoded)
        {
            decoded = null;
            if (value.IndexOfAny(new[] { '+', '/', '=' }) >= 0)
            {
                return false;
            }

            string base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
                case 1:
                    return false;
            }

            var buffer = new byte[base64.Length * 3 / 4];
            if (!Convert.TryFromBase64String(base64, buffer, out int written))
            {
                return false;
            }

            decoded = buffer.AsSpan(0, written).ToArray();
            return true;
        }
    }
}
