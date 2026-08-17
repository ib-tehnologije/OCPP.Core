using System;
using System.Globalization;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
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
        public void Main_DryRunWithEligibleInvoice_RequiresOnlyProviderLookup()
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
            Assert.Contains("outcome=DryRunEligibleProviderLookupRequired", result.StdOut, StringComparison.Ordinal);
            using var verificationContext = scenario.CreateContext();
            Assert.Empty(verificationContext.InvoiceSubmissionLogs);
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
        private const string InvoicesEnabledVariable = "Invoices__Enabled";
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
        }

        public FinancialRecoveryProgramResult Run()
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var originalSqlServerConnectionString = Environment.GetEnvironmentVariable(SqlServerConnectionStringVariable);
            var originalSqliteConnectionString = Environment.GetEnvironmentVariable(SqliteConnectionStringVariable);
            var originalStripeEnabled = Environment.GetEnvironmentVariable(StripeEnabledVariable);
            var originalStripeUseMockServices = Environment.GetEnvironmentVariable(StripeUseMockServicesVariable);
            var originalInvoicesEnabled = Environment.GetEnvironmentVariable(InvoicesEnabledVariable);
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
                Environment.SetEnvironmentVariable(InvoicesEnabledVariable, "false");

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .AddEnvironmentVariables()
                    .Build();
                Assert.Equal(SqlServerConnectionStringOverride, configuration.GetConnectionString("SqlServer"));
                Assert.Equal($"Data Source={_databasePath}", configuration.GetConnectionString("SQLite"));
                Assert.False(configuration.GetValue<bool>("Stripe:Enabled"));
                Assert.False(configuration.GetValue<bool>("Invoices:Enabled"));

                var exitCode = OCPP.Core.Recovery.Program.Main(new[] { "--manifest", _manifestPath });
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
                Environment.SetEnvironmentVariable(InvoicesEnabledVariable, originalInvoicesEnabled);
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
}
