using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Sends idle-fee warning emails shortly before idle billing starts.
    /// </summary>
    public class IdleFeeWarningEmailService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<IdleFeeWarningEmailService> _logger;
        private readonly NotificationOptions _notificationOptions;
        private readonly StripeOptions _stripeOptions;
        private readonly TimeSpan _interval;
        private readonly int _warningLeadMinutes;
        private readonly IStripeSessionService _sessionService;
        private readonly Func<DateTime> _utcNow;
        private readonly ConcurrentDictionary<int, DateTime> _sentWarnings = new();

        public IdleFeeWarningEmailService(
            IServiceScopeFactory scopeFactory,
            ILogger<IdleFeeWarningEmailService> logger,
            IConfiguration configuration,
            IOptions<NotificationOptions> notificationOptions,
            IOptions<StripeOptions> stripeOptions)
            : this(scopeFactory, logger, configuration, notificationOptions, stripeOptions, null)
        {
        }

        public IdleFeeWarningEmailService(
            IServiceScopeFactory scopeFactory,
            ILogger<IdleFeeWarningEmailService> logger,
            IConfiguration configuration,
            IOptions<NotificationOptions> notificationOptions,
            IOptions<StripeOptions> stripeOptions,
            Func<DateTime> utcNow)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _notificationOptions = notificationOptions?.Value ?? new NotificationOptions();
            _stripeOptions = stripeOptions?.Value ?? new StripeOptions();
            _sessionService = new StripeSessionServiceWrapper();
            _utcNow = utcNow ?? (() => DateTime.UtcNow);

            _warningLeadMinutes = Math.Max(1, _notificationOptions.IdleWarningLeadMinutes);
            int intervalSeconds = configuration.GetValue<int?>("Maintenance:IdleWarningSweepSeconds") ?? 60;
            _interval = TimeSpan.FromSeconds(Math.Max(20, intervalSeconds));

            if (!string.IsNullOrWhiteSpace(_stripeOptions.ApiKey))
            {
                StripeConfiguration.ApiKey = _stripeOptions.ApiKey;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // application shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IdleFeeWarningEmailService => sweep failed");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // application shutting down
                }
            }
        }

        protected virtual async Task SweepAsync(CancellationToken token)
        {
            if (!_notificationOptions.EnableCustomerEmails || string.IsNullOrWhiteSpace(_stripeOptions.ApiKey))
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
            var emailService = scope.ServiceProvider.GetService<IEmailNotificationService>();
            if (emailService == null)
            {
                return;
            }

            var reservations = await db.ChargePaymentReservations
                .Where(r =>
                    r.Status == PaymentReservationStatus.Charging &&
                    r.TransactionId.HasValue &&
                    r.UsageFeeAnchorMinutes == 1 &&
                    r.UsageFeePerMinute > 0 &&
                    r.StartUsageFeeAfterMinutes > 0)
                .OrderBy(r => r.UpdatedAtUtc)
                .ToListAsync(token);

            if (!reservations.Any())
            {
                return;
            }

            var txIds = reservations
                .Where(r => r.TransactionId.HasValue)
                .Select(r => r.TransactionId.Value)
                .Distinct()
                .ToList();

            var transactions = await db.Transactions
                .Where(t => txIds.Contains(t.TransactionId))
                .ToDictionaryAsync(t => t.TransactionId, token);

            var chargePointIds = reservations
                .Select(r => r.ChargePointId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            var chargePoints = await db.ChargePoints
                .Where(cp => chargePointIds.Contains(cp.ChargePointId))
                .ToDictionaryAsync(cp => cp.ChargePointId, token);

            var now = _utcNow();
            foreach (var reservation in reservations)
            {
                if (!reservation.TransactionId.HasValue)
                {
                    continue;
                }

                if (!transactions.TryGetValue(reservation.TransactionId.Value, out var transaction))
                {
                    continue;
                }

                if (transaction.StopTime.HasValue)
                {
                    _sentWarnings.TryRemove(transaction.TransactionId, out _);
                    continue;
                }

                if (!transaction.ChargingEndedAtUtc.HasValue)
                {
                    continue;
                }

                if (_sentWarnings.ContainsKey(transaction.TransactionId))
                {
                    continue;
                }

                var idleFeeStartsAtUtc = transaction.ChargingEndedAtUtc.Value.AddMinutes(reservation.StartUsageFeeAfterMinutes);
                if (now >= idleFeeStartsAtUtc)
                {
                    _sentWarnings.TryAdd(transaction.TransactionId, now);
                    continue;
                }

                int leadMinutes = Math.Min(_warningLeadMinutes, reservation.StartUsageFeeAfterMinutes);
                var warningAtUtc = idleFeeStartsAtUtc.AddMinutes(-leadMinutes);
                if (now < warningAtUtc)
                {
                    continue;
                }

                var checkoutSession = GetCheckoutSession(reservation.StripeCheckoutSessionId, reservation.ReservationId);
                var recipientEmail = checkoutSession?.CustomerDetails?.Email;
                if (string.IsNullOrWhiteSpace(recipientEmail))
                {
                    _sentWarnings.TryAdd(transaction.TransactionId, now);
                    _logger.LogDebug("IdleFeeWarningEmailService => Missing recipient email reservation={ReservationId}", reservation.ReservationId);
                    continue;
                }

                chargePoints.TryGetValue(reservation.ChargePointId, out var chargePoint);
                try
                {
                    emailService.SendIdleFeeWarning(
                        recipientEmail,
                        reservation,
                        transaction,
                        chargePoint,
                        idleFeeStartsAtUtc,
                        idleFeeStartsAtUtc - now,
                        BuildStatusUrl(reservation.ReservationId));
                    _sentWarnings.TryAdd(transaction.TransactionId, now);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IdleFeeWarningEmailService => Failed to send idle warning reservation={ReservationId}", reservation.ReservationId);
                }
            }

            PruneSentWarnings(now);
        }

        protected virtual Session GetCheckoutSession(string checkoutSessionId, Guid reservationId)
        {
            if (string.IsNullOrWhiteSpace(checkoutSessionId))
            {
                return null;
            }

            try
            {
                return _sessionService.Get(checkoutSessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "IdleFeeWarningEmailService => Unable to load checkout session reservation={ReservationId} session={SessionId}",
                    reservationId,
                    checkoutSessionId);
                return null;
            }
        }

        private string BuildStatusUrl(Guid reservationId)
        {
            if (reservationId == Guid.Empty || string.IsNullOrWhiteSpace(_stripeOptions.ReturnBaseUrl))
            {
                return null;
            }

            return $"{_stripeOptions.ReturnBaseUrl.TrimEnd('/')}/Payments/Status?reservationId={reservationId}&origin=public";
        }

        private void PruneSentWarnings(DateTime nowUtc)
        {
            var cutoff = nowUtc.AddDays(-2);
            var stale = _sentWarnings
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var txId in stale)
            {
                _sentWarnings.TryRemove(txId, out _);
            }
        }
    }
}
