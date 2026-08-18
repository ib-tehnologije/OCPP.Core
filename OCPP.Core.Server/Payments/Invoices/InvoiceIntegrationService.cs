using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.EntityFrameworkCore;
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
        void RecoverCompletedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession) =>
            HandleCompletedReservation(dbContext, reservation, transaction, checkoutSession);
    }

    public class InvoiceIntegrationService : IInvoiceIntegrationService
    {
        private static readonly TimeSpan SubmissionLeaseDuration = TimeSpan.FromMinutes(5);
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
            HandleCompletedReservationCore(
                dbContext,
                reservation,
                transaction,
                checkoutSession,
                requireProviderPreflight: false);
        }

        public void RecoverCompletedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession)
        {
            if (!_options.Enabled)
            {
                throw new InvalidOperationException("Invoice integration is disabled.");
            }

            if (!string.Equals((_options.Mode ?? string.Empty).Trim(), "Submit", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Invoice recovery requires submit mode.");
            }

            HandleCompletedReservationCore(
                dbContext,
                reservation,
                transaction,
                checkoutSession,
                requireProviderPreflight: true);
        }

        private void HandleCompletedReservationCore(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            Transaction transaction,
            Session checkoutSession,
            bool requireProviderPreflight)
        {
            if (!_options.Enabled || reservation == null || transaction == null)
            {
                return;
            }

            var draft = _draftBuilder.Build(reservation, transaction, checkoutSession);
            var mode = (_options.Mode ?? "LogOnly").Trim();
            var provider = (_options.Provider ?? "ERacuni").Trim();
            var auditLog = CreateAuditLog(draft, reservation, provider, mode);
            var providerCallStarted = false;
            var providerLookupCompleted = false;

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

                ValidateSubmitModeConfiguration(draft, provider, mode, auditLog);

                var request = _eracuniRequestFactory.BuildCreateSalesInvoiceRequest(draft);
                auditLog.ProviderOperation = request.Method;
                auditLog.ApiTransactionId = TryGetApiTransactionId(request);
                auditLog.SubmissionKey = BuildSubmissionKey(provider, auditLog.ApiTransactionId);

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

                if (dbContext == null)
                {
                    throw new InvalidOperationException("Submit mode requires a durable invoice submission database context.");
                }

                if (HasSubmittedOrExternalHistory(dbContext, auditLog))
                {
                    return;
                }

                var existing = FindOrAdoptExistingLineage(dbContext, auditLog);
                if (existing != null)
                {
                    auditLog = existing;
                    if (string.Equals(auditLog.Status, "Submitted", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(auditLog.ExternalDocumentId) ||
                        !string.IsNullOrWhiteSpace(auditLog.ExternalInvoiceNumber))
                    {
                        return;
                    }

                    if (HasActiveSubmissionLease(auditLog, DateTime.UtcNow))
                    {
                        throw new InvoiceSubmissionInProgressException();
                    }

                    var lookup = _eracuniApiClient.LookupSalesInvoiceByApiTransactionId(
                        BuildLookupRequest(request, auditLog.ApiTransactionId));
                    providerLookupCompleted = true;
                    ApplyProviderLookupEvidence(auditLog, lookup);
                    if (lookup.Outcome == ERacuniInvoiceLookupOutcome.Found)
                    {
                        ApplyProviderResult(auditLog, lookup.ProviderResult);
                        auditLog.Status = "Submitted";
                        auditLog.CompletedAtUtc = DateTime.UtcNow;
                        auditLog.Error = null;
                        ClearSubmissionLease(auditLog);
                        PersistAuditLog(dbContext, auditLog);
                        return;
                    }

                    if (lookup.Outcome != ERacuniInvoiceLookupOutcome.NotFound)
                    {
                        auditLog.Status = "ProviderUnknown";
                        auditLog.CompletedAtUtc = DateTime.UtcNow;
                        auditLog.Error = Truncate(lookup.Error, 4000);
                        ClearSubmissionLease(auditLog);
                        PersistAuditLog(dbContext, auditLog);
                        throw new InvalidOperationException(
                            "Invoice provider state is unknown; create was not attempted.");
                    }

                    if (!TryAcquireSubmissionLease(dbContext, auditLog, DateTime.UtcNow))
                    {
                        throw new InvoiceSubmissionInProgressException();
                    }
                }
                else
                {
                    auditLog.Status = "Submitting";
                    auditLog.CompletedAtUtc = null;
                    auditLog.Error = null;
                    SetSubmissionLease(auditLog, DateTime.UtcNow);
                    try
                    {
                        PersistAuditLog(dbContext, auditLog);
                    }
                    catch (DbUpdateException)
                    {
                        dbContext.Entry(auditLog).State = EntityState.Detached;
                        var winner = dbContext.InvoiceSubmissionLogs
                            .AsNoTracking()
                            .SingleOrDefault(log => log.SubmissionKey == auditLog.SubmissionKey);
                        if (winner != null &&
                            (string.Equals(winner.Status, "Submitted", StringComparison.OrdinalIgnoreCase) ||
                             !string.IsNullOrWhiteSpace(winner.ExternalDocumentId) ||
                             !string.IsNullOrWhiteSpace(winner.ExternalInvoiceNumber)))
                        {
                            return;
                        }

                        throw new InvoiceSubmissionInProgressException();
                    }
                }

                if (requireProviderPreflight && !providerLookupCompleted)
                {
                    var lookup = _eracuniApiClient.LookupSalesInvoiceByApiTransactionId(
                        BuildLookupRequest(request, auditLog.ApiTransactionId));
                    ApplyProviderLookupEvidence(auditLog, lookup);
                    if (lookup.Outcome == ERacuniInvoiceLookupOutcome.Found)
                    {
                        ApplyProviderResult(auditLog, lookup.ProviderResult);
                        auditLog.Status = "Submitted";
                        auditLog.CompletedAtUtc = DateTime.UtcNow;
                        auditLog.Error = null;
                        ClearSubmissionLease(auditLog);
                        PersistAuditLog(dbContext, auditLog);
                        return;
                    }

                    if (lookup.Outcome != ERacuniInvoiceLookupOutcome.NotFound)
                    {
                        auditLog.Status = "ProviderUnknown";
                        auditLog.CompletedAtUtc = DateTime.UtcNow;
                        auditLog.Error = Truncate(lookup.Error, 4000);
                        ClearSubmissionLease(auditLog);
                        PersistAuditLog(dbContext, auditLog);
                        throw new InvalidOperationException(
                            "Invoice provider state is unknown; create was not attempted.");
                    }
                }

                auditLog.Status = "Submitting";
                auditLog.CompletedAtUtc = null;
                auditLog.Error = null;
                auditLog.ProviderOperation = request.Method;
                auditLog.HttpStatusCode = null;
                auditLog.ProviderResponseStatus = null;
                PersistAuditLog(dbContext, auditLog);

                providerCallStarted = true;
                var result = _eracuniApiClient.CreateSalesInvoice(request);
                ApplyProviderResult(auditLog, result);

                if (!IsSuccessStatusCode(result.StatusCode))
                {
                    auditLog.Status = "Failed";
                    auditLog.CompletedAtUtc = DateTime.UtcNow;
                    auditLog.Error = BuildFailureMessage(result);
                    ClearSubmissionLease(auditLog);
                    PersistAuditLog(dbContext, auditLog);
                    throw new InvalidOperationException(auditLog.Error);
                }

                auditLog.Status = "Submitted";
                auditLog.CompletedAtUtc = DateTime.UtcNow;
                ClearSubmissionLease(auditLog);
                PersistAuditLog(dbContext, auditLog);

                _logger.LogInformation(
                    "Invoice/Integration => Provider submitted reservation={ReservationId} statusCode={StatusCode} response={Response}",
                    draft.ReservationId,
                    (int)result.StatusCode,
                    result.Body);
            }
            catch (InvoiceSubmissionInProgressException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (providerCallStarted &&
                    !string.Equals(auditLog.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(auditLog.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
                {
                    auditLog.Status = "ProviderUnknown";
                }
                else if (!string.Equals(auditLog.Status, "ProviderUnknown", StringComparison.OrdinalIgnoreCase))
                {
                    auditLog.Status = "Failed";
                }

                auditLog.CompletedAtUtc ??= DateTime.UtcNow;
                if (!string.Equals(auditLog.Status, "ProviderUnknown", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(auditLog.Error))
                {
                    auditLog.Error = Truncate(ex.ToString(), 4000);
                }
                ClearSubmissionLease(auditLog);
                PersistAuditLog(dbContext, auditLog);
                throw;
            }
        }

        private void ValidateSubmitModeConfiguration(
            InvoiceDraft draft,
            string provider,
            string mode,
            InvoiceSubmissionLog auditLog)
        {
            if (!string.Equals(provider, "ERacuni", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(mode, "Submit", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var eracuni = _options.ERacuni ?? new ERacuniInvoiceOptions();
            var lineItemSummaries = BuildLineItemSummaries(eracuni);
            var missingProductCodes = lineItemSummaries
                .Where(summary => string.IsNullOrWhiteSpace(summary.ProductCode))
                .ToList();

            if (missingProductCodes.Count == 0)
            {
                return;
            }

            auditLog.RequestPayloadJson = SerializeLogPayload(new
            {
                validation = "ERacuniSubmitPreflight",
                provider,
                mode,
                reservationId = draft?.ReservationId,
                transactionId = draft?.TransactionId,
                invoiceKind = draft?.InvoiceKind,
                lineItemProductCodes = lineItemSummaries.Select(summary => new
                {
                    summary.LineType,
                    summary.ProductCode,
                    summary.EnvironmentVariable,
                    summary.ConfigPath,
                    isConfigured = !string.IsNullOrWhiteSpace(summary.ProductCode)
                }),
                missingLineItemProductCodes = missingProductCodes.Select(summary => new
                {
                    summary.LineType,
                    summary.EnvironmentVariable,
                    summary.ConfigPath
                })
            });

            var missingNames = string.Join(", ", missingProductCodes.Select(summary => summary.LineType));
            var missingEnvVars = string.Join(", ", missingProductCodes.Select(summary => summary.EnvironmentVariable));
            throw new InvalidOperationException(
                $"e-racuni submit mode requires configured product codes for line items: {missingNames}. " +
                $"Set {missingEnvVars} before submitting invoices.");
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

        private static string BuildSubmissionKey(string provider, string apiTransactionId)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(apiTransactionId))
            {
                throw new InvalidOperationException("Invoice submission requires a deterministic provider reference.");
            }

            return $"{provider.Trim()}:{apiTransactionId.Trim()}";
        }

        private static bool HasSubmittedOrExternalHistory(
            OCPPCoreContext dbContext,
            InvoiceSubmissionLog candidate)
        {
            return dbContext.InvoiceSubmissionLogs.AsNoTracking().Any(log =>
                (log.ReservationId == candidate.ReservationId ||
                 (!string.IsNullOrWhiteSpace(candidate.ApiTransactionId) &&
                  log.ApiTransactionId == candidate.ApiTransactionId)) &&
                (log.Status == "Submitted" ||
                 log.ExternalDocumentId != null ||
                 log.ExternalInvoiceNumber != null ||
                 log.ExternalPublicUrl != null ||
                 log.ExternalPdfUrl != null));
        }

        private static InvoiceSubmissionLog FindOrAdoptExistingLineage(
            OCPPCoreContext dbContext,
            InvoiceSubmissionLog candidate)
        {
            var exact = dbContext.InvoiceSubmissionLogs
                .OrderByDescending(log => log.CreatedAtUtc)
                .FirstOrDefault(log => log.SubmissionKey == candidate.SubmissionKey);
            if (exact != null)
            {
                return exact;
            }

            var historical = dbContext.InvoiceSubmissionLogs
                .Where(log => log.SubmissionKey == null &&
                    (log.ReservationId == candidate.ReservationId ||
                     (!string.IsNullOrWhiteSpace(candidate.ApiTransactionId) &&
                      log.ApiTransactionId == candidate.ApiTransactionId)))
                .OrderByDescending(log => log.CreatedAtUtc)
                .Take(2)
                .ToList();
            if (historical.Count > 1)
            {
                throw new InvalidOperationException(
                    "Multiple historical invoice submission lineages require manual reconciliation.");
            }

            var adopted = historical.SingleOrDefault();
            if (adopted == null)
            {
                return null;
            }

            adopted.SubmissionKey = candidate.SubmissionKey;
            adopted.ApiTransactionId ??= candidate.ApiTransactionId;
            PersistAuditLog(dbContext, adopted);
            return adopted;
        }

        private static bool HasActiveSubmissionLease(InvoiceSubmissionLog auditLog, DateTime nowUtc) =>
            !string.IsNullOrWhiteSpace(auditLog?.SubmissionLeaseId) &&
            auditLog.SubmissionLeaseExpiresAtUtc.HasValue &&
            auditLog.SubmissionLeaseExpiresAtUtc.Value > nowUtc;

        private static bool TryAcquireSubmissionLease(
            OCPPCoreContext dbContext,
            InvoiceSubmissionLog auditLog,
            DateTime nowUtc)
        {
            var leaseId = Guid.NewGuid().ToString("N");
            var leaseExpiresAtUtc = nowUtc.Add(SubmissionLeaseDuration);
            if (!dbContext.Database.IsRelational())
            {
                if (HasActiveSubmissionLease(auditLog, nowUtc))
                {
                    return false;
                }

                auditLog.SubmissionLeaseId = leaseId;
                auditLog.SubmissionLeaseExpiresAtUtc = leaseExpiresAtUtc;
                auditLog.Status = "Submitting";
                auditLog.CompletedAtUtc = null;
                auditLog.Error = null;
                PersistAuditLog(dbContext, auditLog);
                return true;
            }

            var affected = dbContext.InvoiceSubmissionLogs
                .Where(log => log.InvoiceSubmissionLogId == auditLog.InvoiceSubmissionLogId &&
                    (log.SubmissionLeaseId == null ||
                     !log.SubmissionLeaseExpiresAtUtc.HasValue ||
                     log.SubmissionLeaseExpiresAtUtc <= nowUtc))
                .ExecuteUpdate(setters => setters
                    .SetProperty(log => log.SubmissionLeaseId, leaseId)
                    .SetProperty(log => log.SubmissionLeaseExpiresAtUtc, leaseExpiresAtUtc)
                    .SetProperty(log => log.Status, "Submitting")
                    .SetProperty(log => log.CompletedAtUtc, (DateTime?)null)
                    .SetProperty(log => log.Error, (string)null));
            if (affected != 1)
            {
                return false;
            }

            dbContext.Entry(auditLog).Reload();
            return string.Equals(auditLog.SubmissionLeaseId, leaseId, StringComparison.Ordinal);
        }

        private static void SetSubmissionLease(InvoiceSubmissionLog auditLog, DateTime nowUtc)
        {
            auditLog.SubmissionLeaseId = Guid.NewGuid().ToString("N");
            auditLog.SubmissionLeaseExpiresAtUtc = nowUtc.Add(SubmissionLeaseDuration);
        }

        private static void ClearSubmissionLease(InvoiceSubmissionLog auditLog)
        {
            if (auditLog == null) return;
            auditLog.SubmissionLeaseId = null;
            auditLog.SubmissionLeaseExpiresAtUtc = null;
        }

        private static ERacuniApiRequestEnvelope BuildLookupRequest(
            ERacuniApiRequestEnvelope createRequest,
            string apiTransactionId)
        {
            return new ERacuniApiRequestEnvelope
            {
                Username = createRequest.Username,
                SecretKey = createRequest.SecretKey,
                Token = createRequest.Token,
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters
                {
                    ApiTransactionId = apiTransactionId
                }
            };
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

        private static void ApplyProviderLookupEvidence(
            InvoiceSubmissionLog auditLog,
            ERacuniInvoiceLookupResult lookup)
        {
            if (auditLog == null || lookup == null)
            {
                return;
            }

            var diagnostics = lookup.Diagnostics ?? new ERacuniInvoiceLookupDiagnostics(
                RequestAttempted: false,
                FailureCategory: ERacuniInvoiceLookupFailureCategory.UnrecognizedResponse,
                HttpStatusCode: null,
                ResponseShape: ERacuniInvoiceLookupResponseShape.NotAvailable);
            auditLog.ProviderOperation = "SalesInvoiceList";
            auditLog.HttpStatusCode = diagnostics.HttpStatusCode;
            auditLog.ResponseBody = null;
            auditLog.ProviderResponseStatus = Truncate(
                $"{lookup.Outcome}:{diagnostics.FailureCategory}:{diagnostics.ResponseShape}:" +
                (diagnostics.RequestAttempted ? "attempted" : "not-attempted"),
                100);
            if (lookup.Outcome == ERacuniInvoiceLookupOutcome.Unknown &&
                !string.IsNullOrWhiteSpace(lookup.Error))
            {
                auditLog.Error = Truncate(lookup.Error, 4000);
            }
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

        private static IReadOnlyList<LineItemProductCodeSummary> BuildLineItemSummaries(ERacuniInvoiceOptions options)
        {
            options ??= new ERacuniInvoiceOptions();
            return RequiredLineItems
                .Select(item =>
                {
                    var lineOptions = TryResolveLineItemOptions(options, item.LineType);
                    return new LineItemProductCodeSummary(
                        item.LineType,
                        lineOptions?.ProductCode,
                        item.EnvironmentVariable,
                        item.ConfigPath);
                })
                .ToList();
        }

        private static ERacuniLineItemOptions TryResolveLineItemOptions(ERacuniInvoiceOptions options, string lineType)
        {
            if (options?.LineItems == null || options.LineItems.Count == 0 || string.IsNullOrWhiteSpace(lineType))
            {
                return null;
            }

            if (options.LineItems.TryGetValue(lineType, out var lineOptions))
            {
                return lineOptions;
            }

            return options.LineItems
                .FirstOrDefault(entry => string.Equals(entry.Key, lineType, StringComparison.OrdinalIgnoreCase))
                .Value;
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

        private static readonly (string LineType, string EnvironmentVariable, string ConfigPath)[] RequiredLineItems =
        {
            ("Energy", "INVOICES_ERACUNI_LINEITEM_ENERGY_PRODUCT_CODE", "Invoices:ERacuni:LineItems:Energy:ProductCode"),
            ("SessionFee", "INVOICES_ERACUNI_LINEITEM_SESSION_PRODUCT_CODE", "Invoices:ERacuni:LineItems:SessionFee:ProductCode"),
            ("UsageFee", "INVOICES_ERACUNI_LINEITEM_USAGE_PRODUCT_CODE", "Invoices:ERacuni:LineItems:UsageFee:ProductCode"),
            ("IdleFee", "INVOICES_ERACUNI_LINEITEM_IDLE_PRODUCT_CODE", "Invoices:ERacuni:LineItems:IdleFee:ProductCode")
        };

        private sealed record LineItemProductCodeSummary(
            string LineType,
            string ProductCode,
            string EnvironmentVariable,
            string ConfigPath);

        private sealed class InvoiceSubmissionInProgressException : InvalidOperationException
        {
            public InvoiceSubmissionInProgressException()
                : base("Invoice submission is already in progress for this deterministic lineage.")
            {
            }
        }
    }
}
