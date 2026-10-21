using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// The payment-authorized e-mail (with the live status link) of a provider without a payer-supplied checkout
    /// reference is sent once, whether the browser return or the provider notification authorizes first.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        [Fact]
        public async Task CheckoutNotification_ThatAuthorizesSendsThePaymentAuthorizedEmailOnce()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_notify_email");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_notify_email", "fake_tx_notify_email", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, "notify-email")
            };
            var email = new FakeEmailNotificationService();
            var clock = Now;
            var coordinator = CreateCoordinator(fake, now: () => clock, emailService: email);

            var notification = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, notification.StatusCode);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal(1, email.PaymentAuthorizedCount);
            Assert.Equal("payer@example.com", email.LastToEmail);
            Assert.Equal($"{ReturnBaseUrl}/Payments/Status?reservationId={reservation.ReservationId}&origin=public", email.LastStatusUrl);

            // The browser return after the notification and a repeated notification send nothing more.
            clock = Now.AddMinutes(1);
            var confirmation = coordinator.ConfirmReservation(context, reservation.ReservationId, null);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var repeated = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.True(confirmation.Success);
            Assert.Equal(200, repeated.StatusCode);
            Assert.Equal(1, email.PaymentAuthorizedCount);
        }

        [Fact]
        public async Task ConfirmAndNotification_BothFindingThePendingReservation_SendOneAuthorizationEmail()
        {
            using var keepAlive = new SqliteConnection($"Data Source=email-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            keepAlive.Open();
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite(keepAlive.ConnectionString)
                .Options;
            using var browserContext = new OCPPCoreContext(options);
            browserContext.Database.EnsureCreated();
            var reservation = AddPendingReservation(browserContext, FakeProvider, "fake_token_email_race");
            using var notifyContext = new OCPPCoreContext(options);
            // The notification request loaded the reservation while it was still pending.
            var staleCopy = notifyContext.ChargePaymentReservations.Find(reservation.ReservationId)!;
            Assert.Equal(PaymentReservationStatus.Pending, staleCopy.Status);
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_email_race", "fake_tx_email_race", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, eventId: null)
            };
            var email = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(fake, emailService: email);

            var confirmation = coordinator.ConfirmReservation(browserContext, reservation.ReservationId, null);
            var notification = await coordinator.HandleWebhookAsync(notifyContext, NotifyRequest(), CancellationToken.None);

            Assert.True(confirmation.Success);
            Assert.Equal(200, notification.StatusCode);
            Assert.Equal(PaymentReservationStatus.Authorized, staleCopy.Status);
            Assert.Equal(1, email.PaymentAuthorizedCount);
        }

        [Fact]
        public async Task CheckoutNotification_ForAnAlreadyAuthorizedReservationSendsNoEmail()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_return_first");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_return_first", "fake_tx_return_first", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, "notify-after-return")
            };
            var email = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(fake, emailService: email);

            var confirmation = coordinator.ConfirmReservation(context, reservation.ReservationId, null);
            var notification = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.True(confirmation.Success);
            Assert.Equal(200, notification.StatusCode);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal(1, email.PaymentAuthorizedCount);
        }
    }
}
