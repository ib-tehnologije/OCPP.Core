using System;

namespace OCPP.Core.Server.Extensions.Hangfire
{
    public interface IStripeBuyerMetadataReconciliationJob
    {
        void Reconcile(Guid reservationId);
    }
}
