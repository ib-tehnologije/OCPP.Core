/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Compare-and-set reservation status transitions. The browser return, provider notifications, the cleanup
    /// sweep and cancellations can act on the same reservation at the same time; a status change is persisted
    /// only when the row still has the status the caller decided on, so the loser sees the winner's state instead
    /// of overwriting it (for example authorizing a reservation whose hold the sweep has just released).
    /// </summary>
    public partial class PaymentCoordinator
    {
        private const string ActiveConnectorKeyProperty = "ActiveConnectorKey";

        private static readonly MethodInfo SetPropertyValueMethod = typeof(SetPropertyCalls<ChargePaymentReservation>)
            .GetMethods()
            .Single(method =>
                method.Name == nameof(SetPropertyCalls<ChargePaymentReservation>.SetProperty) &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[1].ParameterType.IsGenericParameter);

        private static readonly MethodInfo SetPropertyExpressionMethod = typeof(SetPropertyCalls<ChargePaymentReservation>)
            .GetMethods()
            .Single(method =>
                method.Name == nameof(SetPropertyCalls<ChargePaymentReservation>.SetProperty) &&
                method.GetParameters().Length == 2 &&
                !method.GetParameters()[1].ParameterType.IsGenericParameter);

        /// <summary>
        /// Persists the pending changes of <paramref name="reservation"/> (its new status and the fields set with
        /// it) only if the row still has <paramref name="expectedStatus"/> and matches
        /// <paramref name="additionalCondition"/>. Returns false when another request changed the row first; the
        /// entity is then reloaded with the winner's values and the caller handles that state.
        /// </summary>
        /// <remarks>
        /// On relational providers this is one conditional UPDATE. It keeps the SQLite active-connector key in step
        /// (SQL Server computes it), and with <paramref name="ocppIdTagCandidate"/> claims the charge tag in the same
        /// statement while keeping a tag that is already stored. Other changes tracked by the context are not saved.
        /// Non-relational providers (the in-memory test provider) have no conditional update: the caller's
        /// in-memory status check applies and the changes are saved with SaveChanges unless
        /// <paramref name="saveWhenNotRelational"/> is false.
        /// </remarks>
        internal static bool TryCommitReservationTransition(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string expectedStatus,
            Expression<Func<ChargePaymentReservation, bool>> additionalCondition = null,
            string ocppIdTagCandidate = null,
            bool saveWhenNotRelational = true)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));

            var claimTag = !string.IsNullOrWhiteSpace(ocppIdTagCandidate);
            if (!dbContext.Database.IsRelational())
            {
                if (claimTag && string.IsNullOrWhiteSpace(reservation.OcppIdTag))
                {
                    reservation.OcppIdTag = ocppIdTagCandidate;
                }

                if (saveWhenNotRelational)
                {
                    dbContext.SaveChanges();
                }

                return true;
            }

            var entry = dbContext.Entry(reservation);
            if (entry.State == EntityState.Detached)
            {
                throw new InvalidOperationException("Reservation status transitions require a tracked reservation.");
            }

            entry.DetectChanges();
            var setters = Expression.Parameter(typeof(SetPropertyCalls<ChargePaymentReservation>), "setters");
            Expression body = setters;
            var written = new List<PropertyEntry>();
            foreach (var property in entry.Properties)
            {
                var metadata = property.Metadata;
                if (metadata.IsPrimaryKey() ||
                    metadata.PropertyInfo == null ||
                    string.Equals(metadata.Name, ActiveConnectorKeyProperty, StringComparison.Ordinal) ||
                    (claimTag && string.Equals(metadata.Name, nameof(ChargePaymentReservation.OcppIdTag), StringComparison.Ordinal)))
                {
                    continue;
                }

                var alwaysWritten = string.Equals(metadata.Name, nameof(ChargePaymentReservation.Status), StringComparison.Ordinal) ||
                                    string.Equals(metadata.Name, nameof(ChargePaymentReservation.UpdatedAtUtc), StringComparison.Ordinal);
                if (!property.IsModified && !alwaysWritten)
                {
                    continue;
                }

                body = AppendSetProperty(body, metadata, property.CurrentValue);
                written.Add(property);
            }

            string activeKey = null;
            var syncActiveKey = ChargePaymentReservationActiveKey.RequiresManualSync(dbContext.Database.ProviderName) &&
                                entry.Metadata.FindProperty(ActiveConnectorKeyProperty) != null;
            if (syncActiveKey)
            {
                // ExecuteUpdate bypasses the SaveChanges synchronization of the SQLite active-connector key.
                activeKey = ChargePaymentReservationActiveKey.Compute(reservation.ReservationId, reservation.Status);
                body = AppendSetProperty(body, entry.Metadata.FindProperty(ActiveConnectorKeyProperty), activeKey);
            }

            if (claimTag)
            {
                var candidate = new TransitionValue<string>(ocppIdTagCandidate);
                Expression<Func<ChargePaymentReservation, string>> tagSelector = row => row.OcppIdTag;
                Expression<Func<ChargePaymentReservation, string>> tagValue = row =>
                    row.OcppIdTag == null || row.OcppIdTag == "" ? candidate.Value : row.OcppIdTag;
                body = Expression.Call(body, SetPropertyExpressionMethod.MakeGenericMethod(typeof(string)), tagSelector, tagValue);
            }

            var reservationId = reservation.ReservationId;
            var query = dbContext.ChargePaymentReservations
                .Where(row => row.ReservationId == reservationId && row.Status == expectedStatus);
            if (additionalCondition != null)
            {
                query = query.Where(additionalCondition);
            }

            var affected = query.ExecuteUpdate(
                Expression.Lambda<Func<SetPropertyCalls<ChargePaymentReservation>, SetPropertyCalls<ChargePaymentReservation>>>(body, setters));
            if (affected == 0)
            {
                entry.Reload();
                return false;
            }

            foreach (var property in written)
            {
                property.OriginalValue = property.CurrentValue;
                property.IsModified = false;
            }

            if (syncActiveKey)
            {
                var keyProperty = entry.Property(ActiveConnectorKeyProperty);
                keyProperty.CurrentValue = activeKey;
                keyProperty.OriginalValue = activeKey;
                keyProperty.IsModified = false;
            }

            if (claimTag)
            {
                var persistedTag = dbContext.ChargePaymentReservations
                    .AsNoTracking()
                    .Where(row => row.ReservationId == reservationId)
                    .Select(row => row.OcppIdTag)
                    .FirstOrDefault();
                var tagProperty = entry.Property(row => row.OcppIdTag);
                tagProperty.CurrentValue = persistedTag;
                tagProperty.OriginalValue = persistedTag;
                tagProperty.IsModified = false;
            }

            return true;
        }

        /// <summary>
        /// Moves a pending reservation to Authorized (fields already set by the caller) and claims its charge tag in
        /// the same conditional update. The tag row is created first, apart from the reservation's changes, so an
        /// authorized reservation always has its tag row and a failure in between leaves the reservation pending.
        /// False when another request ended or authorized the reservation first; the entity then holds the
        /// winner's values.
        /// </summary>
        private static bool TryCommitAuthorization(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            var candidate = string.IsNullOrWhiteSpace(reservation.OcppIdTag) ? GenerateOcppIdTag() : null;
            if (!dbContext.Database.IsRelational())
            {
                reservation.OcppIdTag ??= candidate;
                EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
                dbContext.SaveChanges();
                return true;
            }

            var preparedTag = candidate ?? reservation.OcppIdTag;
            EnsureChargeTagExistsApart(dbContext, preparedTag);
            if (!TryCommitReservationTransition(
                    dbContext,
                    reservation,
                    PaymentReservationStatus.Pending,
                    ocppIdTagCandidate: candidate))
            {
                RemoveUnusedChargeTag(dbContext, candidate);
                return false;
            }

            if (!string.Equals(reservation.OcppIdTag, preparedTag, StringComparison.Ordinal))
            {
                // The row already held a tag from an earlier authorization attempt: that tag stays.
                EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
                RemoveUnusedChargeTag(dbContext, candidate);
            }

            return true;
        }

        /// <summary>
        /// Creates a charge tag row through a separate context of the same database, so pending reservation changes
        /// of <paramref name="dbContext"/> are not saved with it.
        /// </summary>
        private static void EnsureChargeTagExistsApart(OCPPCoreContext dbContext, string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId)) return;

            if (dbContext.GetService<IDbContextOptions>() is not DbContextOptions<OCPPCoreContext> options)
            {
                throw new InvalidOperationException("The database context options are not available to create the charge tag.");
            }

            using var tagContext = new OCPPCoreContext(options);
            EnsureChargeTagExists(tagContext, tagId);
        }

        /// <summary>Removes a tag row created for an authorization that another request decided first (best effort).</summary>
        private static void RemoveUnusedChargeTag(OCPPCoreContext dbContext, string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId)) return;

            try
            {
                if (dbContext.ChargePaymentReservations.AsNoTracking().Any(row => row.OcppIdTag == tagId))
                {
                    return;
                }

                dbContext.ChargeTags.Where(tag => tag.TagId == tagId).ExecuteDelete();
            }
            catch (Exception)
            {
                // An unused random tag is harmless; nobody knows it.
            }
        }

        /// <summary>Status the tracked reservation had when it was loaded (or last saved).</summary>
        private static string PersistedStatus(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            var entry = dbContext.Entry(reservation);
            return entry.State == EntityState.Detached || entry.State == EntityState.Added
                ? reservation.Status
                : entry.Property(row => row.Status).OriginalValue;
        }

        private static void ReloadQuietly(OCPPCoreContext dbContext, object entity)
        {
            if (entity == null) return;
            try
            {
                var entry = dbContext.Entry(entity);
                if (entry.State != EntityState.Detached && entry.State != EntityState.Added)
                {
                    entry.Reload();
                }
            }
            catch (Exception)
            {
                // Best effort: the caller is already handling a failure.
            }
        }

        private static Expression AppendSetProperty(Expression setters, IProperty property, object value)
        {
            var row = Expression.Parameter(typeof(ChargePaymentReservation), "row");
            Expression access = property.PropertyInfo != null
                ? Expression.Property(row, property.PropertyInfo)
                : Expression.Call(
                    typeof(EF),
                    nameof(EF.Property),
                    new[] { property.ClrType },
                    row,
                    Expression.Constant(property.Name));
            var selector = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(ChargePaymentReservation), property.ClrType),
                access,
                row);

            // A member of a constant object is sent as a parameter (like a captured variable), not inlined.
            var holder = Activator.CreateInstance(typeof(TransitionValue<>).MakeGenericType(property.ClrType), new[] { value });
            var valueAccess = Expression.Field(Expression.Constant(holder), nameof(TransitionValue<object>.Value));
            return Expression.Call(setters, SetPropertyValueMethod.MakeGenericMethod(property.ClrType), selector, valueAccess);
        }

        private sealed class TransitionValue<T>
        {
            public TransitionValue(T value)
            {
                Value = value;
            }

            public readonly T Value;
        }
    }
}
