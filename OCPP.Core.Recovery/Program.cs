using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Recovery;

namespace OCPP.Core.Recovery;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var arguments = RecoveryArguments.Parse(args);
            var json = File.ReadAllText(arguments.ManifestPath);
            var manifest = FinancialRecoveryManifest.Parse(json);
            manifest.RequireExecutionConfirmation(arguments.Execute, arguments.ConfirmationSha256);

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Production.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            new OCPP.Core.Server.Startup(configuration).ConfigureServices(services);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var recovery = new FinancialRecoveryService(
                scope.ServiceProvider.GetRequiredService<IPaymentCoordinator>(),
                scope.ServiceProvider.GetRequiredService<IInvoiceIntegrationService>(),
                scope.ServiceProvider.GetRequiredService<IStripeCheckoutSessionReader>());
            var report = recovery.Run(
                scope.ServiceProvider.GetRequiredService<OCPPCoreContext>(),
                manifest,
                arguments.Execute,
                arguments.ConfirmationSha256);

            Console.WriteLine($"mode={(report.Executed ? "execute" : "dry-run")} manifestSha256={report.ManifestSha256}");
            foreach (var item in report.Items)
            {
                Console.WriteLine(
                    $"operation={item.Operation} reservation={Redact(item.ReservationId)} eligible={item.Eligible} outcome={item.Outcome}");
            }

            return report.Succeeded ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Financial recovery stopped ({ex.GetType().Name}). Review private operator logs for details.");
            return 1;
        }
    }

    private static string Redact(Guid reservationId) => reservationId.ToString("N")[..8] + "...";

    private sealed record RecoveryArguments(
        string ManifestPath,
        bool Execute,
        string? ConfirmationSha256)
    {
        public static RecoveryArguments Parse(string[] args)
        {
            string? manifestPath = null;
            string? confirmation = null;
            var execute = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--manifest" when index + 1 < args.Length:
                        manifestPath = args[++index];
                        break;
                    case "--execute":
                        execute = true;
                        break;
                    case "--confirm-sha256" when index + 1 < args.Length:
                        confirmation = args[++index];
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported or incomplete argument '{args[index]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new InvalidOperationException("--manifest <path> is required.");
            }

            return new RecoveryArguments(manifestPath, execute, confirmation);
        }
    }
}
