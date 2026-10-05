using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using OCPP.Core.Server;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class OCPPMiddlewarePathSegmentTests
    {
        private const string NetworkProfile =
            "ocppVersion{OCPP16}ocppCsmsUrl{wss://ocpp.example.test/}messageTimeout{10}ocppInterface{Wired0}securityProfile{0}";

        [Fact]
        public void DecodeRawPathSegment_DecodesEncodedSlashesInValue()
        {
            string rawTarget = "/API/ChangeConfiguration/ACE0816130/BackOfficeNetworkProfile3/" + Uri.EscapeDataString(NetworkProfile);
            // Request.Path as Kestrel provides it: decoded except %2F
            string[] pathParts = ("/API/ChangeConfiguration/ACE0816130/BackOfficeNetworkProfile3/" +
                                  NetworkProfile.Replace("/", "%2F")).Split('/');

            string value = OCPPMiddleware.DecodeRawPathSegment(rawTarget, pathParts, 5);

            Assert.Equal(NetworkProfile, value);
        }

        [Fact]
        public void DecodeRawPathSegment_KeepsPlainValueUnchanged()
        {
            string rawTarget = "/API/ChangeConfiguration/ACE0816130/NetworkConfigurationPriority/1,2";

            string value = OCPPMiddleware.DecodeRawPathSegment(rawTarget, rawTarget.Split('/'), 5);

            Assert.Equal("1,2", value);
        }

        [Fact]
        public void DecodeRawPathSegment_PreservesLiteralPercentSequence()
        {
            // Client sends the literal text "a%2Fb" (encoded as a%252Fb); it must not become "a/b"
            string rawTarget = "/API/ChangeConfiguration/ACE0816130/SomeKey/a%252Fb";
            string[] pathParts = "/API/ChangeConfiguration/ACE0816130/SomeKey/a%2Fb".Split('/');

            string value = OCPPMiddleware.DecodeRawPathSegment(rawTarget, pathParts, 5);

            Assert.Equal("a%2Fb", value);
        }

        [Fact]
        public void DecodeRawPathSegment_IgnoresQueryAndPathBase()
        {
            string rawTarget = "/ocpp-server/API/ChangeConfiguration/ACE0816130/Url/wss%3A%2F%2Fhost%2F?trace=1";
            string[] pathParts = "/API/ChangeConfiguration/ACE0816130/Url/wss:%2F%2Fhost%2F".Split('/');

            string value = OCPPMiddleware.DecodeRawPathSegment(rawTarget, pathParts, 5);

            Assert.Equal("wss://host/", value);
        }

        [Fact]
        public void DecodeRawPathSegment_FallsBackToDecodedPathWithoutRawTarget()
        {
            string[] pathParts = "/API/ChangeConfiguration/ACE0816130/Url/wss:%2F%2Fhost%2F".Split('/');

            Assert.Equal("wss://host/", OCPPMiddleware.DecodeRawPathSegment(null, pathParts, 5));
            Assert.Null(OCPPMiddleware.DecodeRawPathSegment(null, pathParts, 6));
        }

        [Fact]
        public async Task DecodeRawPathSegment_RoundTripsThroughKestrel()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.Run(async context =>
            {
                string[] urlParts = context.Request.Path.Value.Split('/');
                string value = OCPPMiddleware.DecodeRawPathSegment(
                    context.Features.Get<IHttpRequestFeature>()?.RawTarget, urlParts, 5);
                await context.Response.WriteAsync(value ?? "<null>");
            });
            await app.StartAsync();

            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>().Addresses.First();
            using var client = new HttpClient();

            string echoed = await client.GetStringAsync(
                baseUrl + "/API/ChangeConfiguration/ACE0816130/BackOfficeNetworkProfile3/" + Uri.EscapeDataString(NetworkProfile));
            string literal = await client.GetStringAsync(
                baseUrl + "/API/ChangeConfiguration/ACE0816130/SomeKey/" + Uri.EscapeDataString("a%2Fb"));

            await app.StopAsync();

            Assert.Equal(NetworkProfile, echoed);
            Assert.Equal("a%2Fb", literal);
        }
    }
}
