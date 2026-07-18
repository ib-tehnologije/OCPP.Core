using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentAuthorizationReleasePersistenceTests
    {
        [Fact]
        public void Model_MapsReleaseAuditWithoutArmingExistingReservations()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new OCPPCoreContext(options);
            var attempt = context.Model.FindEntityType(typeof(PaymentAuthorizationReleaseAttempt));
            var reservation = context.Model.FindEntityType(typeof(ChargePaymentReservation));

            Assert.NotNull(attempt);
            Assert.Equal("PaymentAuthorizationReleaseAttempt", attempt!.GetTableName());
            Assert.Equal(200, attempt.FindProperty(nameof(PaymentAuthorizationReleaseAttempt.StripePaymentIntentId))!.GetMaxLength());
            Assert.Equal(50, attempt.FindProperty(nameof(PaymentAuthorizationReleaseAttempt.Trigger))!.GetMaxLength());
            Assert.Equal(50, attempt.FindProperty(nameof(PaymentAuthorizationReleaseAttempt.Outcome))!.GetMaxLength());
            Assert.Equal(100, attempt.FindProperty(nameof(PaymentAuthorizationReleaseAttempt.ErrorCode))!.GetMaxLength());
            Assert.Equal(500, attempt.FindProperty(nameof(PaymentAuthorizationReleaseAttempt.ErrorMessage))!.GetMaxLength());

            var uniqueAttemptIndex = attempt.GetIndexes().Single(index =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(new[]
                    {
                        nameof(PaymentAuthorizationReleaseAttempt.ReservationId),
                        nameof(PaymentAuthorizationReleaseAttempt.AttemptNumber)
                    }));
            Assert.True(uniqueAttemptIndex.IsUnique);

            var foreignKey = attempt.GetForeignKeys().Single();
            Assert.Equal(nameof(ChargePaymentReservation), foreignKey.PrincipalEntityType.ClrType.Name);
            Assert.Equal(nameof(PaymentAuthorizationReleaseAttempt.ReservationId), foreignKey.Properties.Single().Name);

            var releaseState = reservation!.FindProperty(nameof(ChargePaymentReservation.AuthorizationReleaseState));
            Assert.NotNull(releaseState);
            Assert.True(releaseState!.IsNullable);
            Assert.Null(releaseState.GetDefaultValue());
            Assert.Null(releaseState.GetDefaultValueSql());
            Assert.Null(reservation.FindProperty(nameof(ChargePaymentReservation.AuthorizationReleaseNextAttemptAtUtc))!.GetDefaultValue());
            Assert.Null(reservation.FindProperty(nameof(ChargePaymentReservation.AuthorizationReleasedAtUtc))!.GetDefaultValue());
        }
    }
}
