/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments
{
    public static class ViesVerificationStatus
    {
        public const string NotChecked = "NotChecked";
        public const string Valid = "Valid";
        public const string Invalid = "Invalid";
        public const string Unavailable = "Unavailable";
    }

    public static class VatValidationStatus
    {
        public const string NotApplicable = "NotApplicable";
        public const string Valid = "Valid";
    }

    public sealed class ViesOptions
    {
        public bool Enabled { get; set; }
        public int TimeoutSeconds { get; set; } = 3;
    }

    public sealed class ViesVerificationResult
    {
        public string Status { get; set; }
        public DateTime? CheckedAtUtc { get; set; }
        public string Reference { get; set; }
    }

    public interface IViesVerificationService
    {
        Task<ViesVerificationResult> VerifyAsync(
            string countryCode,
            string vatNumber,
            CancellationToken cancellationToken);
    }

    public sealed class ViesVerificationService : IViesVerificationService
    {
        public const string DefaultBaseAddress =
            "https://ec.europa.eu/taxation_customs/vies/rest-api/";
        public const string ProviderReference = "EC-VIES-REST";
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

        private readonly HttpClient _httpClient;
        private readonly ILogger<ViesVerificationService> _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _timeout;
        private readonly bool _enabled;

        public ViesVerificationService(
            HttpClient httpClient,
            ILogger<ViesVerificationService> logger,
            IOptions<ViesOptions> options)
            : this(
                httpClient,
                logger,
                () => DateTime.UtcNow,
                ResolveTimeout(options?.Value),
                options?.Value?.Enabled ?? false)
        {
        }

        internal ViesVerificationService(
            HttpClient httpClient,
            ILogger<ViesVerificationService> logger,
            Func<DateTime> utcNow,
            TimeSpan timeout,
            bool enabled = true)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
            _enabled = enabled;
        }

        public async Task<ViesVerificationResult> VerifyAsync(
            string countryCode,
            string vatNumber,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(countryCode))
            {
                throw new ArgumentException("VIES country code is required.", nameof(countryCode));
            }
            if (string.IsNullOrWhiteSpace(vatNumber))
            {
                throw new ArgumentException("VAT number is required.", nameof(vatNumber));
            }
            if (!_enabled)
            {
                return new ViesVerificationResult
                {
                    Status = ViesVerificationStatus.NotChecked
                };
            }

            var checkedAtUtc = _utcNow();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    countryCode,
                    vatNumber
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, "check-vat-number")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "VIES verification unavailable country={CountryCode} httpStatus={HttpStatus}",
                        countryCode,
                        (int)response.StatusCode);
                    return Unavailable(checkedAtUtc);
                }

                var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                var json = JObject.Parse(responseBody);
                var validToken = json["valid"];
                if (validToken == null || validToken.Type != JTokenType.Boolean)
                {
                    _logger.LogWarning(
                        "VIES verification returned no boolean validity country={CountryCode}",
                        countryCode);
                    return Unavailable(checkedAtUtc);
                }

                var reference = BoundReference(json.Value<string>("requestIdentifier"));
                return new ViesVerificationResult
                {
                    Status = validToken.Value<bool>()
                        ? ViesVerificationStatus.Valid
                        : ViesVerificationStatus.Invalid,
                    CheckedAtUtc = checkedAtUtc,
                    Reference = reference
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "VIES verification timed out country={CountryCode}",
                    countryCode);
                return Unavailable(checkedAtUtc);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "VIES verification request failed country={CountryCode}",
                    countryCode);
                return Unavailable(checkedAtUtc);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "VIES verification response was invalid country={CountryCode}",
                    countryCode);
                return Unavailable(checkedAtUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "VIES verification failed unexpectedly country={CountryCode}",
                    countryCode);
                return Unavailable(checkedAtUtc);
            }
        }

        private static string BoundReference(string reference)
        {
            var normalized = string.IsNullOrWhiteSpace(reference)
                ? ProviderReference
                : new string(reference
                    .Trim()
                    .Where(ch => !char.IsControl(ch))
                    .ToArray());
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = ProviderReference;
            }
            return normalized.Length <= 100
                ? normalized
                : normalized.Substring(0, 100);
        }

        private static ViesVerificationResult Unavailable(DateTime checkedAtUtc) =>
            new ViesVerificationResult
            {
                Status = ViesVerificationStatus.Unavailable,
                CheckedAtUtc = checkedAtUtc,
                Reference = ProviderReference
            };

        private static TimeSpan ResolveTimeout(ViesOptions options)
        {
            var seconds = options?.TimeoutSeconds ?? 3;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 10));
        }
    }

    internal sealed class UnavailableViesVerificationService : IViesVerificationService
    {
        private readonly Func<DateTime> _utcNow;

        public UnavailableViesVerificationService(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public Task<ViesVerificationResult> VerifyAsync(
            string countryCode,
            string vatNumber,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ViesVerificationResult
            {
                Status = ViesVerificationStatus.Unavailable,
                CheckedAtUtc = _utcNow(),
                Reference = ViesVerificationService.ProviderReference
            });
        }
    }
}
