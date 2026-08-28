using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Payments.Invoices
{
    /// <summary>
    /// Serializes buyer-snapshot edits with invoice lineage acquisition in this process.
    /// A fixed stripe set keeps memory bounded while preserving per-reservation exclusion.
    /// </summary>
    internal static class InvoiceBuyerMutationGate
    {
        private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 257)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();

        public static IDisposable Enter(Guid reservationId)
        {
            var gate = GetGate(reservationId);
            gate.Wait();
            return new Releaser(gate);
        }

        public static async Task<IDisposable> EnterAsync(
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            var gate = GetGate(reservationId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(gate);
        }

        private static SemaphoreSlim GetGate(Guid reservationId) =>
            Gates[(int)((uint)reservationId.GetHashCode() % (uint)Gates.Length)];

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _gate;

            public Releaser(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
