/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public interface IPaymentCoordinator
    {
        bool IsEnabled { get; }
        PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request);
        PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId);
        void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason);
        void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId);
        void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction);
        void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader);
    }

    public class PaymentSessionRequest
    {
        public string ChargePointId { get; set; }
        public int ConnectorId { get; set; }
        public string ChargeTagId { get; set; }
        public string Origin { get; set; }
        public string ReturnBaseUrl { get; set; }
    }

    public class PaymentSessionResult
    {
        public ChargePaymentReservation Reservation { get; set; }
        public string CheckoutUrl { get; set; }
    }

    public class PaymentConfirmRequest
    {
        public Guid ReservationId { get; set; }
        public string CheckoutSessionId { get; set; }
    }

    public class PaymentCancelRequest
    {
        public Guid ReservationId { get; set; }
        public string Reason { get; set; }
    }

    public class PaymentConfirmationResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public ChargePaymentReservation Reservation { get; set; }
    }

    public static class PaymentReservationStatus
    {
        public const string Pending = "PendingPayment";
        public const string Authorized = "Authorized";
        public const string StartRequested = "StartRequested";
        public const string Charging = "Charging";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";
        public const string Failed = "Failed";
    }
}
