using System;
using System.Linq;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public class ReservationLinkService
    {
        private readonly StartChargingMediator _startMediator;
        public ReservationLinkService(StartChargingMediator mediator)
        {
            _startMediator = mediator;
        }

        public void LinkReservation(OCPPCoreContext dbContext, string chargePointId, int connectorId, string idTag, int transactionId, DateTime startTime)
        {
            var reservation = dbContext.ChargePaymentReservations
                .Where(r =>
                    r.ChargePointId == chargePointId &&
                    r.ConnectorId == connectorId &&
                    (r.OcppIdTag == idTag || r.ChargeTagId == idTag) &&
                    (r.Status == PaymentReservationStatus.Authorized || r.Status == PaymentReservationStatus.StartRequested))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();

            if (reservation == null) return;

            reservation.TransactionId = transactionId;
            reservation.StartTransactionId = transactionId;
            reservation.StartTransactionAtUtc = startTime;
            reservation.Status = PaymentReservationStatus.Charging;
            reservation.UpdatedAtUtc = DateTime.UtcNow;
            dbContext.SaveChanges();
        }
    }
}
