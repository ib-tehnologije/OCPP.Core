using System;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Stripe.Checkout;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices.ERacuni;

namespace OCPP.Core.Server.Payments.Invoices
{
    public interface IInvoiceIntegrationService
    {
        void HandleCompletedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession);
    }

    public class InvoiceIntegrationService : IInvoiceIntegrationService
    {
        private readonly InvoiceIntegrationOptions _options;
        private readonly IInvoiceDraftBuilder _draftBuilder;
        private readonly IERacuniInvoiceRequestFactory _eracuniRequestFactory;
        private readonly IERacuniApiClient _eracuniApiClient;
        private readonly ILogger<InvoiceIntegrationService> _logger;

        public InvoiceIntegrationService(
            IOptions<InvoiceIntegrationOptions> options,
            IInvoiceDraftBuilder draftBuilder,
            IERacuniInvoiceRequestFactory eracuniRequestFactory,
            IERacuniApiClient eracuniApiClient,
            ILogger<InvoiceIntegrationService> logger)
        {
            _options = options?.Value ?? new InvoiceIntegrationOptions();
            _draftBuilder = draftBuilder ?? throw new ArgumentNullException(nameof(draftBuilder));
            _eracuniRequestFactory = eracuniRequestFactory ?? throw new ArgumentNullException(nameof(eracuniRequestFactory));
            _eracuniApiClient = eracuniApiClient ?? throw new ArgumentNullException(nameof(eracuniApiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void HandleCompletedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession)
        {
            if (!_options.Enabled || reservation == null || transaction == null)
            {
                return;
            }

            var draft = _draftBuilder.Build(reservation, transaction, checkoutSession);
            var mode = (_options.Mode ?? "LogOnly").Trim();
            var provider = (_options.Provider ?? "ERacuni").Trim();
            var auditLog = CreateAuditLog(draft, reservation, provider, mode);
            PersistAuditLog(dbContext, auditLog);

            _logger.LogInformation(
                "Invoice/Integration => Prepared draft provider={Provider} mode={Mode} reservation={ReservationId} transaction={TransactionId} kind={InvoiceKind} total={TotalAmount} currency={Currency} lines={LineCount}",
                provider,
                mode,
                draft.ReservationId,
                draft.TransactionId,
                draft.InvoiceKind,
                draft.TotalAmount,
                draft.Currency,
                draft.Lines.Count);

            if (draft.Lines.Count == 0)
            {
                auditLog.Status = "SkippedNoLines";
                auditLog.CompletedAtUtc = DateTime.UtcNow;
                PersistAuditLog(dbContext, auditLog);

                _logger.LogInformation(
                    "Invoice/Integration => Skipping provider payload because there are no billable lines reservation={ReservationId}",
                    draft.ReservationId);
                return;
            }

            try
            {
                if (!string.Equals(provider, "ERacuni", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unsupported invoice provider '{provider}'.");
                }

                var request = _eracuniRequestFactory.BuildCreateSalesInvoiceRequest(draft);
                auditLog.ProviderOperation = request.Method;
                auditLog.ApiTransactionId = TryGetApiTransactionId(request);

                var logPayload = _eracuniRequestFactory.BuildSanitizedLogPayload(request);
                auditLog.RequestPayloadJson = SerializeLogPayload(logPayload);

                if (string.Equals(mode, "LogOnly", StringComparison.OrdinalIgnoreCase))
                {
                    auditLog.Status = "LoggedOnly";
                    auditLog.CompletedAtUtc = DateTime.UtcNow;
                    PersistAuditLog(dbContext, auditLog);

                    _logger.LogInformation(
                        "Invoice/Integration => Provider payload reservation={ReservationId} payload={Payload}",
                        draft.ReservationId,
                        auditLog.RequestPayloadJson);
                    return;
                }

                auditLog.Status = "Submitting";
                PersistAuditLog(dbContext, auditLog);

                var result = _eracuniApiClient.CreateSalesInvoice(request);
                ApplyProviderResult(auditLog, result);

                if (!IsSuccessStatusCode(result.StatusCode))
                {
                    auditLog.Status = "Failed";
                    auditLog.CompletedAtUtc = DateTime.UtcNow;
                    auditLog.Error = BuildFailureMessage(result);
                    PersistAuditLog(dbContext, auditLog);
                    throw new InvalidOperationException(auditLog.Error);
                }

                auditLog.Status = "Submitted";
                auditLog.CompletedAtUtc = DateTime.UtcNow;
                PersistAuditLog(dbContext, auditLog);

                _logger.LogInformation(
                    "Invoice/Integration => Provider submitted reservation={ReservationId} statusCode={StatusCode} response={Response}",
                    draft.ReservationId,
                    (int)result.StatusCode,
                    result.Body);
            }
            catch (Exception ex)
            {
                auditLog.Status = "Failed";
                auditLog.CompletedAtUtc ??= DateTime.UtcNow;
                auditLog.Error = Truncate(ex.ToString(), 4000);
                PersistAuditLog(dbContext, auditLog);
                throw;
            }
        }

        private static InvoiceSubmissionLog CreateAuditLog(
            InvoiceDraft draft,
            ChargePaymentReservation reservation,
            string provider,
            string mode)
        {
            return new InvoiceSubmissionLog
            {
                ReservationId = draft.ReservationId,
                TransactionId = draft.TransactionId > 0 ? draft.TransactionId : reservation.TransactionId,
                Provider = provider,
                Mode = mode,
                Status = "Prepared",
                InvoiceKind = draft.InvoiceKind,
                StripeCheckoutSessionId = draft.StripeCheckoutSessionId,
                StripePaymentIntentId = draft.StripePaymentIntentId,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private static string TryGetApiTransactionId(ERacuniApiRequestEnvelope request)
        {
            if (request?.Parameters is ERacuniSalesInvoiceCreateParameters createParameters)
            {
                return createParameters.ApiTransactionId;
            }

            return null;
        }

        private static string SerializeLogPayload(object payload)
        {
            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        private static void ApplyProviderResult(InvoiceSubmissionLog auditLog, ERacuniApiResult result)
        {
            if (auditLog == null || result == null)
            {
                return;
            }

            auditLog.HttpStatusCode = (int)result.StatusCode;
            auditLog.ResponseBody = result.Body;

            var metadata = ERacuniApiResponseMetadataReader.Read(result.ParsedBody);
            auditLog.ExternalDocumentId = metadata.DocumentId;
            auditLog.ExternalInvoiceNumber = metadata.InvoiceNumber;
            auditLog.ExternalPublicUrl = metadata.PublicUrl;
            auditLog.ExternalPdfUrl = metadata.PdfUrl;
            auditLog.ProviderResponseStatus = metadata.Status;
        }

        private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
        {
            var status = (int)statusCode;
            return status >= 200 && status <= 299;
        }

        private static string BuildFailureMessage(ERacuniApiResult result)
        {
            return $"e-racuni request failed with HTTP {(int)result.StatusCode}: {result.Body}";
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private static void PersistAuditLog(OCPPCoreContext dbContext, InvoiceSubmissionLog auditLog)
        {
            if (dbContext == null || auditLog == null)
            {
                return;
            }

            if (auditLog.InvoiceSubmissionLogId == 0)
            {
                dbContext.InvoiceSubmissionLogs.Add(auditLog);
            }

            dbContext.SaveChanges();
        }
    }
}
