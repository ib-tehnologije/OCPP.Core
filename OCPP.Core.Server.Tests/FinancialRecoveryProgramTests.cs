using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Recovery;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    [CollectionDefinition(nameof(FinancialRecoveryProgramCollection), DisableParallelization = true)]
    public sealed class FinancialRecoveryProgramCollection : ICollectionFixture<FinancialRecoveryProgramFixture>
    {
    }

    [Collection(nameof(FinancialRecoveryProgramCollection))]
    public class FinancialRecoveryProgramTests
    {
        private readonly FinancialRecoveryProgramFixture _fixture;

        public FinancialRecoveryProgramTests(FinancialRecoveryProgramFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void Main_DryRunWithEmptyManifest_ReportsSuccessfulManifestValidation()
        {
            using var scenario = _fixture.CreateScenario("""
                { "schemaVersion": 1, "entries": [] }
                """);

            var result = scenario.Run();

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("mode=dry-run manifestSha256=", result.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("Financial recovery stopped", result.StdErr, StringComparison.Ordinal);
        }

        [Fact]
        public void Main_DryRunWithEligibleInvoice_ReportsProviderCreateReplayRequired()
        {
            const string reservationId = "66666666-6666-6666-6666-666666666666";
            using var scenario = _fixture.CreateScenario($$"""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "recover-invoice", "reservationId": "{{reservationId}}" }
                  ]
                }
                """);
            scenario.SeedInvoiceRecovery(Guid.Parse(reservationId));

            var result = scenario.Run();

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("operation=recover-invoice", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("eligible=True", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("outcome=DryRunEligibleProviderCreateReplayRequired", result.StdOut, StringComparison.Ordinal);
            using var verificationContext = scenario.CreateContext();
            Assert.Empty(verificationContext.InvoiceSubmissionLogs);
        }

        [Fact]
        public void Main_ExecuteReplaysSalesInvoiceCreateWithStableIdWithoutLookup()
        {
            const string reservationId = "77777777-7777-7777-7777-777777777777";
            using var provider = new SyntheticInvoiceProviderServer(
                "{\"status\":\"ok\",\"result\":{\"documentId\":\"doc-replayed\",\"number\":\"INV-REPLAYED\"}}");
            using var scenario = _fixture.CreateScenario($$"""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "recover-invoice", "reservationId": "{{reservationId}}" }
                  ]
                }
                """);
            scenario.SeedInvoiceRecovery(Guid.Parse(reservationId));

            var result = scenario.RunExecute(provider.BaseUrl);

            Assert.Equal(0, result.ExitCode);
            Assert.Single(provider.RequestBodies);
            Assert.Contains("\"method\":\"SalesInvoiceCreate\"", provider.RequestBodies[0], StringComparison.Ordinal);
            Assert.Contains(
                "\"apiTransactionId\":\"77777777777777777777777777777777\"",
                provider.RequestBodies[0],
                StringComparison.Ordinal);
            Assert.Contains("\"orderReference\":\"EVSE-66\"", provider.RequestBodies[0], StringComparison.Ordinal);
            Assert.DoesNotContain("SalesInvoiceList", provider.RequestBodies[0], StringComparison.Ordinal);
            using var verificationContext = scenario.CreateContext();
            var audit = Assert.Single(verificationContext.InvoiceSubmissionLogs);
            Assert.Equal("Submitted", audit.Status);
            Assert.Equal("SalesInvoiceCreate", audit.ProviderOperation);
            Assert.Equal("77777777777777777777777777777777", audit.ApiTransactionId);
            Assert.Equal(200, audit.HttpStatusCode);
            Assert.Equal("doc-replayed", audit.ExternalDocumentId);
            Assert.Equal("INV-REPLAYED", audit.ExternalInvoiceNumber);
        }
    }

    public sealed class FinancialRecoveryProgramFixture
    {
        public FinancialRecoveryProgramScenario CreateScenario(string manifestJson) =>
            new(manifestJson);
    }

    public sealed class FinancialRecoveryProgramScenario : IDisposable
    {
        private const string SentinelSqlServerConnectionString = "Server=127.0.0.1,1;Database=financial_recovery_sentinel;User Id=sentinel;Password=sentinel;Connect Timeout=1";
        private const string SqlServerConnectionStringVariable = "ConnectionStrings__SqlServer";
        private const string SqliteConnectionStringVariable = "ConnectionStrings__SQLite";
        private const string SqlServerConnectionStringOverride = " ";
        private const string StripeEnabledVariable = "Stripe__Enabled";
        private const string StripeUseMockServicesVariable = "Stripe__UseMockServices";
        private const string StripeMockDiagnosticsDirectoryVariable = "Stripe__MockDiagnosticsDirectory";
        private const string InvoicesEnabledVariable = "Invoices__Enabled";
        private const string InvoicesProviderVariable = "Invoices__Provider";
        private const string InvoicesModeVariable = "Invoices__Mode";
        private const string InvoicesApiBaseUrlVariable = "Invoices__ERacuni__ApiBaseUrl";
        private const string InvoicesApiPathVariable = "Invoices__ERacuni__ApiPath";
        private const string InvoicesUsernameVariable = "Invoices__ERacuni__Username";
        private const string InvoicesSecretKeyVariable = "Invoices__ERacuni__SecretKey";
        private const string InvoicesTokenVariable = "Invoices__ERacuni__Token";
        private const string InvoicesRequestIntervalVariable = "Invoices__ERacuni__MinimumRequestIntervalMilliseconds";
        private static readonly string[] InvoiceProductCodeVariables =
        {
            "Invoices__ERacuni__LineItems__Energy__ProductCode",
            "Invoices__ERacuni__LineItems__SessionFee__ProductCode",
            "Invoices__ERacuni__LineItems__UsageFee__ProductCode",
            "Invoices__ERacuni__LineItems__IdleFee__ProductCode"
        };
        private readonly string _temporaryDirectory;
        private readonly string _databasePath;
        private readonly string _manifestPath;
        private bool _disposed;

        public FinancialRecoveryProgramScenario(string manifestJson)
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ocpp-financial-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_temporaryDirectory);
            _databasePath = Path.Combine(_temporaryDirectory, "recovery.sqlite");
            _manifestPath = Path.Combine(_temporaryDirectory, "manifest.json");
            File.WriteAllText(_manifestPath, manifestJson);
            File.WriteAllText(
                Path.Combine(_temporaryDirectory, "appsettings.json"),
                $$"""
                {
                  "ConnectionStrings": {
                    "SqlServer": "{{SentinelSqlServerConnectionString}}"
                  }
                }
                """);

            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={_databasePath}")
                .Options;
            return new OCPPCoreContext(options);
        }

        public void SeedInvoiceRecovery(Guid reservationId)
        {
            const string checkoutSessionId = "mock_sess_invoice_recovery";
            const string paymentIntentId = "mock_pi_invoice_recovery";
            using var context = CreateContext();
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = "CP-SYNTHETIC"
            });
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-SYNTHETIC",
                ConnectorId = 1,
                ChargeTagId = "TAG-SYNTHETIC",
                OcppIdTag = "TAG-SYNTHETIC",
                TransactionId = 66,
                Status = PaymentReservationStatus.Completed,
                StripeCheckoutSessionId = checkoutSessionId,
                StripePaymentIntentId = paymentIntentId,
                CapturedAtUtc = new DateTime(2026, 1, 1, 11, 1, 0, DateTimeKind.Utc),
                CapturedAmountCents = 300,
                Currency = "EUR"
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = 66,
                ChargePointId = "CP-SYNTHETIC",
                ConnectorId = 1,
                StartTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
                Currency = "EUR",
                EnergyKwh = 5,
                EnergyCost = 2.50m,
                UserSessionFeeAmount = 0.50m
            });
            context.SaveChanges();
            File.WriteAllText(
                Path.Combine(_temporaryDirectory, "mock-stripe-store.json"),
                $$"""
                {
                  "sessions": [
                    {
                      "id": "{{checkoutSessionId}}",
                      "url": "https://example.test/mock-checkout",
                      "paymentIntentId": "{{paymentIntentId}}",
                      "status": "complete",
                      "paymentStatus": "paid",
                      "metadata": {}
                    }
                  ],
                  "paymentIntents": [
                    {
                      "id": "{{paymentIntentId}}",
                      "status": "succeeded",
                      "amount": 300,
                      "amountReceived": 300,
                      "metadata": {}
                    }
                  ]
                }
                """);
        }

        public FinancialRecoveryProgramResult Run() => RunCore(execute: false, providerBaseUrl: null);

        public FinancialRecoveryProgramResult RunExecute(string providerBaseUrl) =>
            RunCore(execute: true, providerBaseUrl);

        private FinancialRecoveryProgramResult RunCore(bool execute, string? providerBaseUrl)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var originalSqlServerConnectionString = Environment.GetEnvironmentVariable(SqlServerConnectionStringVariable);
            var originalSqliteConnectionString = Environment.GetEnvironmentVariable(SqliteConnectionStringVariable);
            var originalStripeEnabled = Environment.GetEnvironmentVariable(StripeEnabledVariable);
            var originalStripeUseMockServices = Environment.GetEnvironmentVariable(StripeUseMockServicesVariable);
            var originalStripeMockDiagnosticsDirectory = Environment.GetEnvironmentVariable(StripeMockDiagnosticsDirectoryVariable);
            var originalInvoicesEnabled = Environment.GetEnvironmentVariable(InvoicesEnabledVariable);
            var invoiceVariables = new[]
            {
                InvoicesProviderVariable,
                InvoicesModeVariable,
                InvoicesApiBaseUrlVariable,
                InvoicesApiPathVariable,
                InvoicesUsernameVariable,
                InvoicesSecretKeyVariable,
                InvoicesTokenVariable,
                InvoicesRequestIntervalVariable
            };
            var originalInvoiceValues = new Dictionary<string, string?>();
            foreach (var variable in invoiceVariables)
            {
                originalInvoiceValues[variable] = Environment.GetEnvironmentVariable(variable);
            }
            foreach (var variable in InvoiceProductCodeVariables)
            {
                originalInvoiceValues[variable] = Environment.GetEnvironmentVariable(variable);
            }
            var originalCurrentDirectory = Environment.CurrentDirectory;
            using var standardOut = new StringWriter(CultureInfo.InvariantCulture);
            using var standardError = new StringWriter(CultureInfo.InvariantCulture);

            try
            {
                Console.SetOut(standardOut);
                Console.SetError(standardError);
                Directory.SetCurrentDirectory(_temporaryDirectory);
                Assert.Equal(
                    SentinelSqlServerConnectionString,
                    new ConfigurationBuilder()
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("appsettings.json")
                        .Build()
                        .GetConnectionString("SqlServer"));

                Environment.SetEnvironmentVariable(SqlServerConnectionStringVariable, SqlServerConnectionStringOverride);
                Environment.SetEnvironmentVariable(SqliteConnectionStringVariable, $"Data Source={_databasePath}");
                Environment.SetEnvironmentVariable(StripeEnabledVariable, "false");
                Environment.SetEnvironmentVariable(StripeUseMockServicesVariable, "true");
                Environment.SetEnvironmentVariable(StripeMockDiagnosticsDirectoryVariable, _temporaryDirectory);
                Environment.SetEnvironmentVariable(InvoicesEnabledVariable, execute ? "true" : "false");
                if (execute)
                {
                    Environment.SetEnvironmentVariable(InvoicesProviderVariable, "ERacuni");
                    Environment.SetEnvironmentVariable(InvoicesModeVariable, "Submit");
                    Environment.SetEnvironmentVariable(InvoicesApiBaseUrlVariable, providerBaseUrl);
                    Environment.SetEnvironmentVariable(InvoicesApiPathVariable, "/api");
                    Environment.SetEnvironmentVariable(InvoicesUsernameVariable, "synthetic-user");
                    Environment.SetEnvironmentVariable(InvoicesSecretKeyVariable, "synthetic-secret");
                    Environment.SetEnvironmentVariable(InvoicesTokenVariable, "synthetic-token");
                    Environment.SetEnvironmentVariable(InvoicesRequestIntervalVariable, "0");
                    foreach (var variable in InvoiceProductCodeVariables)
                    {
                        Environment.SetEnvironmentVariable(variable, "SYNTHETIC-PRODUCT");
                    }
                }

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .AddEnvironmentVariables()
                    .Build();
                Assert.Equal(SqlServerConnectionStringOverride, configuration.GetConnectionString("SqlServer"));
                Assert.Equal($"Data Source={_databasePath}", configuration.GetConnectionString("SQLite"));
                Assert.False(configuration.GetValue<bool>("Stripe:Enabled"));
                Assert.Equal(execute, configuration.GetValue<bool>("Invoices:Enabled"));

                var arguments = new List<string> { "--manifest", _manifestPath };
                if (execute)
                {
                    var digest = FinancialRecoveryManifest.Parse(File.ReadAllText(_manifestPath)).Sha256;
                    arguments.Add("--execute");
                    arguments.Add("--confirm-sha256");
                    arguments.Add(digest);
                }

                var exitCode = OCPP.Core.Recovery.Program.Main(arguments.ToArray());
                return new FinancialRecoveryProgramResult(exitCode, standardOut.ToString(), standardError.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Directory.SetCurrentDirectory(originalCurrentDirectory);
                Environment.SetEnvironmentVariable(SqlServerConnectionStringVariable, originalSqlServerConnectionString);
                Environment.SetEnvironmentVariable(SqliteConnectionStringVariable, originalSqliteConnectionString);
                Environment.SetEnvironmentVariable(StripeEnabledVariable, originalStripeEnabled);
                Environment.SetEnvironmentVariable(StripeUseMockServicesVariable, originalStripeUseMockServices);
                Environment.SetEnvironmentVariable(StripeMockDiagnosticsDirectoryVariable, originalStripeMockDiagnosticsDirectory);
                Environment.SetEnvironmentVariable(InvoicesEnabledVariable, originalInvoicesEnabled);
                foreach (var original in originalInvoiceValues)
                {
                    Environment.SetEnvironmentVariable(original.Key, original.Value);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    public sealed record FinancialRecoveryProgramResult(int ExitCode, string StdOut, string StdErr);

    public sealed class SyntheticInvoiceProviderServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;
        private readonly string _responseBody;
        private readonly ConcurrentQueue<string> _requestBodies = new();

        public SyntheticInvoiceProviderServer(string responseBody)
        {
            _responseBody = responseBody;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _serverTask = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }
        public IReadOnlyList<string> RequestBodies => _requestBodies.ToArray();

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            _cancellation.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(_cancellation.Token) is { } header && header.Length > 0)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(header["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                    }
                }

                var body = new char[contentLength];
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await reader.ReadAsync(body.AsMemory(offset), _cancellation.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }
                _requestBodies.Enqueue(new string(body, 0, offset));

                var responseBytes = Encoding.UTF8.GetBytes(_responseBody);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, _cancellation.Token);
                await stream.WriteAsync(responseBytes, _cancellation.Token);
            }
        }
    }
}
