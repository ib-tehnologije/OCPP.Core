using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ReservationLockStateTests
    {
        [Theory]
        [InlineData(PaymentReservationStatus.Pending, true)]
        [InlineData(PaymentReservationStatus.Authorized, true)]
        [InlineData(PaymentReservationStatus.StartRequested, true)]
        [InlineData(PaymentReservationStatus.Charging, true)]
        [InlineData(PaymentReservationStatus.Completed, false)]
        [InlineData(PaymentReservationStatus.Cancelled, false)]
        [InlineData(PaymentReservationStatus.Failed, false)]
        [InlineData(PaymentReservationStatus.StartRejected, false)]
        [InlineData(PaymentReservationStatus.StartTimeout, false)]
        [InlineData(PaymentReservationStatus.Abandoned, false)]
        public void LocksConnector_UsesSharedStatusTruthTable(string status, bool expected)
        {
            Assert.Equal(expected, PaymentReservationStatus.LocksConnector(status));
            Assert.Equal(expected, ChargePaymentReservationState.LocksConnector(status));
        }

        [Theory]
        [InlineData(PaymentReservationStatus.Completed, true)]
        [InlineData(PaymentReservationStatus.Cancelled, true)]
        [InlineData(PaymentReservationStatus.Failed, true)]
        [InlineData(PaymentReservationStatus.StartRejected, true)]
        [InlineData(PaymentReservationStatus.StartTimeout, true)]
        [InlineData(PaymentReservationStatus.Abandoned, true)]
        [InlineData(PaymentReservationStatus.Pending, false)]
        [InlineData(PaymentReservationStatus.Authorized, false)]
        [InlineData(PaymentReservationStatus.StartRequested, false)]
        [InlineData(PaymentReservationStatus.Charging, false)]
        public void IsTerminal_MatchesSharedStatusTruthTable(string status, bool expected)
        {
            Assert.Equal(expected, PaymentReservationStatus.IsTerminal(status));
            Assert.Equal(expected, ChargePaymentReservationState.IsTerminal(status));
        }
    }
}
