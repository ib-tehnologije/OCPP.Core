using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentAuthorizationEmailJobTests
    {
        [Fact]
        public void SendPaymentAuthorized_SendsEmail_WhenReservationExists()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                Status = PaymentReservationStatus.Authorized,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var emailService = new FakeEmailNotificationService();
            var job = new PaymentAuthorizationEmailJob(
                context,
                emailService,
                NullLogger<PaymentAuthorizationEmailJob>.Instance);

            job.SendPaymentAuthorized(reservationId, "payer@example.com", "sess_1");

            Assert.Equal(1, emailService.PaymentAuthorizedCount);
            Assert.Equal("payer@example.com", emailService.LastToEmail);
        }

        [Fact]
        public void SendPaymentAuthorized_DoesNothing_WhenReservationIdIsEmpty()
        {
            using var context = CreateContext();
            var emailService = new FakeEmailNotificationService();
            var job = new PaymentAuthorizationEmailJob(
                context,
                emailService,
                NullLogger<PaymentAuthorizationEmailJob>.Instance);

            job.SendPaymentAuthorized(Guid.Empty, "payer@example.com", "sess_2");

            Assert.Equal(0, emailService.PaymentAuthorizedCount);
        }

        [Fact]
        public void SendPaymentAuthorized_DoesNothing_WhenReservationMissing()
        {
            using var context = CreateContext();
            var emailService = new FakeEmailNotificationService();
            var job = new PaymentAuthorizationEmailJob(
                context,
                emailService,
                NullLogger<PaymentAuthorizationEmailJob>.Instance);

            job.SendPaymentAuthorized(Guid.NewGuid(), "payer@example.com", "sess_3");

            Assert.Equal(0, emailService.PaymentAuthorizedCount);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }
    }
}
