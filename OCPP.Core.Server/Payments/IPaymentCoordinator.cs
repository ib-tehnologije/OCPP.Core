/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public interface IPaymentCoordinator
    {
        bool IsEnabled { get; }
        PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request);
        PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId);
        PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId);
        PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request);
        void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason);
        void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason);
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

        // Optional buyer info (used for R1/business invoicing in some jurisdictions, e.g. Croatia).
        public bool RequestR1Invoice { get; set; }
        public string BuyerCompanyName { get; set; }
        public string BuyerOib { get; set; }
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

    public class PaymentR1InvoiceRequest
    {
        public Guid ReservationId { get; set; }
        public string BuyerCompanyName { get; set; }
        public string BuyerOib { get; set; }
    }

    public class PaymentR1InvoiceResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string BuyerCompanyName { get; set; }
        public string BuyerOib { get; set; }
        public ChargePaymentReservation Reservation { get; set; }
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

    public class PaymentResumeResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string CheckoutUrl { get; set; }
        public ChargePaymentReservation Reservation { get; set; }
    }

    public static class PaymentReservationStatus
    {
        public const string Pending = ChargePaymentReservationState.Pending;
        public const string Authorized = ChargePaymentReservationState.Authorized;
        public const string StartRequested = ChargePaymentReservationState.StartRequested;
        public const string StartRejected = ChargePaymentReservationState.StartRejected;
        public const string StartTimeout = ChargePaymentReservationState.StartTimeout;
        public const string Abandoned = ChargePaymentReservationState.Abandoned;
        public const string Charging = ChargePaymentReservationState.Charging;
        public const string Completed = ChargePaymentReservationState.Completed;
        public const string Cancelled = ChargePaymentReservationState.Cancelled;
        public const string Failed = ChargePaymentReservationState.Failed;

        public static readonly string[] InactiveStatuses = ChargePaymentReservationState.InactiveStatuses;
        public static readonly string[] ConnectorLockStatuses = ChargePaymentReservationState.ConnectorLockStatuses;

        public static bool IsActive(string status)
        {
            return ChargePaymentReservationState.IsActive(status);
        }

        public static bool IsCancelable(string status)
        {
            return ChargePaymentReservationState.IsCancelable(status);
        }

        public static bool IsTerminal(string status)
        {
            return ChargePaymentReservationState.IsTerminal(status);
        }

        public static bool LocksConnector(string status)
        {
            return ChargePaymentReservationState.LocksConnector(status);
        }
    }
}
