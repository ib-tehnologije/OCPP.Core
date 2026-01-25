using System;
using System.Threading.Tasks;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Lightweight mediator to allow non-middleware components (e.g., Stripe webhook processing) to trigger TryStartCharging.
    /// </summary>
    public class StartChargingMediator
    {
        public Func<OCPPCoreContext, ChargePaymentReservation, string, Task<(string Status, string Reason)>> TryStartAsync { get; set; }

        public Task<(string Status, string Reason)> TryStart(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string caller)
        {
            if (TryStartAsync != null)
            {
                return TryStartAsync(dbContext, reservation, caller);
            }

            return Task.FromResult<(string Status, string Reason)>(("NotAvailable", "MediatorNotSet"));
        }
    }
}
