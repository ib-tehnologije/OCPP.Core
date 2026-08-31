using System;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Invoices
{
    /// <summary>
    /// Uses the reservation row as a cross-process mutex while buyer data or invoice lineage is acquired.
    /// </summary>
    internal sealed class InvoiceBuyerDatabaseMutationLock : IDisposable
    {
        private readonly IDbContextTransaction _transaction;
        private bool _committed;

        private InvoiceBuyerDatabaseMutationLock(IDbContextTransaction transaction)
        {
            _transaction = transaction;
        }

        public static InvoiceBuyerDatabaseMutationLock Acquire(
            OCPPCoreContext dbContext,
            Guid reservationId)
        {
            if (dbContext == null || reservationId == Guid.Empty || !dbContext.Database.IsRelational())
            {
                return null;
            }

            var transaction = dbContext.Database.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var affected = dbContext.ChargePaymentReservations
                    .Where(reservation => reservation.ReservationId == reservationId)
                    .ExecuteUpdate(setters => setters
                        .SetProperty(reservation => reservation.UpdatedAtUtc, reservation => reservation.UpdatedAtUtc));
                if (affected != 1)
                {
                    throw new InvalidOperationException(
                        $"Reservation '{reservationId}' could not be locked for invoice buyer mutation.");
                }

                return new InvoiceBuyerDatabaseMutationLock(transaction);
            }
            catch
            {
                transaction.Dispose();
                throw;
            }
        }

        public void Commit()
        {
            if (_transaction == null || _committed)
            {
                return;
            }

            _transaction.Commit();
            _committed = true;
        }

        public void Dispose()
        {
            _transaction?.Dispose();
        }
    }
}
