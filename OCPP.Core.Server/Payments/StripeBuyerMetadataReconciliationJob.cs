using System;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Server.Extensions.Hangfire;

namespace OCPP.Core.Server.Payments
{
    public sealed class StripeBuyerMetadataReconciliationJob : IStripeBuyerMetadataReconciliationJob
    {
        private readonly OCPPCoreContext _dbContext;
        private readonly IPaymentCoordinator _paymentCoordinator;
        private readonly ILogger<StripeBuyerMetadataReconciliationJob> _logger;

        public StripeBuyerMetadataReconciliationJob(
            OCPPCoreContext dbContext,
            IPaymentCoordinator paymentCoordinator,
            ILogger<StripeBuyerMetadataReconciliationJob> logger)
        {
            _dbContext = dbContext;
            _paymentCoordinator = paymentCoordinator;
            _logger = logger;
        }

        public void Reconcile(Guid reservationId)
        {
            if (reservationId == Guid.Empty)
            {
                throw new ArgumentException("Reservation id is required.", nameof(reservationId));
            }

            _logger.LogInformation(
                "Stripe/BuyerMetadataReconciliation => Starting reservation={ReservationId}",
                reservationId);
            if (!_paymentCoordinator.ReconcileR1BuyerMetadata(_dbContext, reservationId))
            {
                throw new InvalidOperationException(
                    $"Stripe buyer metadata reconciliation did not converge for reservation '{reservationId}'.");
            }

            _logger.LogInformation(
                "Stripe/BuyerMetadataReconciliation => Completed reservation={ReservationId}",
                reservationId);
        }
    }
}
