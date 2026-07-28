using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ViesVerificationServiceTests
    {
        private static readonly DateTime CheckedAtUtc =
            new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task VerifyAsync_ReturnsValid_WithBoundedReference()
        {
            var service = CreateService(
                HttpStatusCode.OK,
                """{"countryCode":"DE","vatNumber":"123456789","valid":true,"requestIdentifier":"abc-123"}""");

            var result = await service.VerifyAsync("DE", "123456789", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.Valid, result.Status);
            Assert.Equal("abc-123", result.Reference);
            Assert.Equal(CheckedAtUtc, result.CheckedAtUtc);
        }

        [Fact]
        public async Task VerifyAsync_ReturnsInvalid_WhenViesRejectsRegistration()
        {
            var service = CreateService(
                HttpStatusCode.OK,
                """{"countryCode":"DE","vatNumber":"123456789","valid":false}""");

            var result = await service.VerifyAsync("DE", "123456789", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.Invalid, result.Status);
            Assert.Equal(ViesVerificationService.ProviderReference, result.Reference);
            Assert.Equal(CheckedAtUtc, result.CheckedAtUtc);
        }

        [Fact]
        public async Task VerifyAsync_ReturnsUnavailable_ForMemberStateFailure()
        {
            var service = CreateService(
                HttpStatusCode.InternalServerError,
                """{"actionSucceed":false,"errorWrappers":[{"error":"MS_UNAVAILABLE"}]}""");

            var result = await service.VerifyAsync("SI", "12345678", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.Unavailable, result.Status);
            Assert.Equal(ViesVerificationService.ProviderReference, result.Reference);
            Assert.Equal(CheckedAtUtc, result.CheckedAtUtc);
        }

        [Fact]
        public async Task VerifyAsync_ReturnsUnavailable_ForMalformedSuccessResponse()
        {
            var service = CreateService(HttpStatusCode.OK, """{"valid":"not-a-boolean"}""");

            var result = await service.VerifyAsync("CZ", "12345678", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.Unavailable, result.Status);
        }

        [Fact]
        public async Task VerifyAsync_ReturnsUnavailable_ForUnexpectedTransportFailure()
        {
            var handler = new StubHttpMessageHandler((_, _) =>
                throw new InvalidOperationException("simulated handler failure"));
            var service = new ViesVerificationService(
                new HttpClient(handler) { BaseAddress = new Uri(ViesVerificationService.DefaultBaseAddress) },
                NullLogger<ViesVerificationService>.Instance,
                () => CheckedAtUtc,
                TimeSpan.FromSeconds(1));

            var result = await service.VerifyAsync("DE", "123456789", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.Unavailable, result.Status);
            Assert.Equal(CheckedAtUtc, result.CheckedAtUtc);
        }

        [Fact]
        public async Task VerifyAsync_ReturnsUnavailable_WhenRequestTimesOut()
        {
            var service = new ViesVerificationService(
                new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }))
                {
                    BaseAddress = new Uri(ViesVerificationService.DefaultBaseAddress)
                },
                NullLogger<ViesVerificationService>.Instance,
                () => CheckedAtUtc,
                TimeSpan.FromMilliseconds(10));

            var result = await service.VerifyAsync("DE", "123456789", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.Unavailable, result.Status);
            Assert.Equal(CheckedAtUtc, result.CheckedAtUtc);
        }

        [Fact]
        public async Task VerifyAsync_PreservesCallerCancellation()
        {
            var service = new ViesVerificationService(
                new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }))
                {
                    BaseAddress = new Uri(ViesVerificationService.DefaultBaseAddress)
                },
                NullLogger<ViesVerificationService>.Instance,
                () => CheckedAtUtc,
                TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.VerifyAsync("DE", "123456789", cancellation.Token));
        }

        [Fact]
        public async Task VerifyAsync_DoesNotCallVies_WhenVerificationIsDisabled()
        {
            var handler = new StubHttpMessageHandler((_, _) =>
                throw new InvalidOperationException("HTTP must not be called when VIES is disabled."));
            var service = new ViesVerificationService(
                new HttpClient(handler) { BaseAddress = new Uri(ViesVerificationService.DefaultBaseAddress) },
                NullLogger<ViesVerificationService>.Instance,
                Options.Create(new ViesOptions
                {
                    Enabled = false,
                    TimeoutSeconds = 3
                }));

            var result = await service.VerifyAsync("DE", "123456789", CancellationToken.None);

            Assert.Equal(ViesVerificationStatus.NotChecked, result.Status);
            Assert.Null(result.CheckedAtUtc);
            Assert.Null(result.Reference);
        }

        [Theory]
        [InlineData("DE", "123456789", "DE", "123456789")]
        [InlineData("EL", "123456789", "EL", "123456789")]
        [InlineData("XI", "123456789", "XI", "123456789")]
        public async Task VerifyAsync_SendsOnlyCountryAndVatNumber(
            string country,
            string vatNumber,
            string expectedCountry,
            string expectedVatNumber)
        {
            HttpMethod? capturedMethod = null;
            string? capturedJson = null;
            var handler = new StubHttpMessageHandler(async (request, _) =>
            {
                capturedMethod = request.Method;
                capturedJson = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"valid":true}""")
                };
            });
            var service = new ViesVerificationService(
                new HttpClient(handler) { BaseAddress = new Uri(ViesVerificationService.DefaultBaseAddress) },
                NullLogger<ViesVerificationService>.Instance,
                () => CheckedAtUtc,
                TimeSpan.FromSeconds(1));

            await service.VerifyAsync(country, vatNumber, CancellationToken.None);

            Assert.Equal(HttpMethod.Post, capturedMethod);
            var sentJson = Assert.IsType<string>(capturedJson);
            Assert.Contains($"\"countryCode\":\"{expectedCountry}\"", sentJson);
            Assert.Contains($"\"vatNumber\":\"{expectedVatNumber}\"", sentJson);
            Assert.DoesNotContain("traderName", sentJson);
            Assert.DoesNotContain("requesterMemberStateCode", sentJson);
        }

        private static ViesVerificationService CreateService(HttpStatusCode statusCode, string body)
        {
            var handler = new StubHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body)
                }));
            return new ViesVerificationService(
                new HttpClient(handler) { BaseAddress = new Uri(ViesVerificationService.DefaultBaseAddress) },
                NullLogger<ViesVerificationService>.Instance,
                () => CheckedAtUtc,
                TimeSpan.FromSeconds(1));
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public StubHttpMessageHandler(
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                _handler(request, cancellationToken);
        }
    }
}
