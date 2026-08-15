using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Maintenance
{
    internal enum MessageLogRetentionSweepStatus
    {
        Disabled,
        InvalidConfiguration,
        DryRun,
        Completed
    }

    internal sealed class MessageLogRetentionSweepResult
    {
        internal MessageLogRetentionSweepResult(
            MessageLogRetentionSweepStatus status,
            DateTime? cutoffUtc,
            int candidateCount,
            int deletedCount,
            int batchCount,
            int estimatedBatchCount,
            DateTime? oldestCandidateUtc,
            DateTime? newestCandidateUtc,
            TimeSpan elapsed)
        {
            Status = status;
            CutoffUtc = cutoffUtc;
            CandidateCount = candidateCount;
            DeletedCount = deletedCount;
            BatchCount = batchCount;
            EstimatedBatchCount = estimatedBatchCount;
            OldestCandidateUtc = oldestCandidateUtc;
            NewestCandidateUtc = newestCandidateUtc;
            Elapsed = elapsed;
        }

        internal MessageLogRetentionSweepStatus Status { get; }
        internal DateTime? CutoffUtc { get; }
        internal int CandidateCount { get; }
        internal int DeletedCount { get; }
        internal int BatchCount { get; }
        internal int EstimatedBatchCount { get; }
        internal DateTime? OldestCandidateUtc { get; }
        internal DateTime? NewestCandidateUtc { get; }
        internal TimeSpan Elapsed { get; }

        internal static MessageLogRetentionSweepResult Skipped(
            MessageLogRetentionSweepStatus status)
        {
            return new MessageLogRetentionSweepResult(
                status,
                null,
                0,
                0,
                0,
                0,
                null,
                null,
                TimeSpan.Zero);
        }
    }

    internal sealed class MessageLogRetentionBatchResult
    {
        internal MessageLogRetentionBatchResult(
            int batchNumber,
            int selectedCount,
            int deletedCount,
            int firstLogId,
            int lastLogId,
            DateTime oldestCandidateUtc,
            DateTime newestCandidateUtc,
            TimeSpan elapsed)
        {
            BatchNumber = batchNumber;
            SelectedCount = selectedCount;
            DeletedCount = deletedCount;
            FirstLogId = firstLogId;
            LastLogId = lastLogId;
            OldestCandidateUtc = oldestCandidateUtc;
            NewestCandidateUtc = newestCandidateUtc;
            Elapsed = elapsed;
        }

        internal int BatchNumber { get; }
        internal int SelectedCount { get; }
        internal int DeletedCount { get; }
        internal int FirstLogId { get; }
        internal int LastLogId { get; }
        internal DateTime OldestCandidateUtc { get; }
        internal DateTime NewestCandidateUtc { get; }
        internal TimeSpan Elapsed { get; }
    }

    internal sealed class MessageLogRetentionSweepException : Exception
    {
        internal MessageLogRetentionSweepException(
            DateTime cutoffUtc,
            bool dryRun,
            int batchSize,
            int? candidateCount,
            int completedBatchCount,
            int deletedCount,
            TimeSpan elapsed,
            Exception innerException)
            : base("MessageLog retention sweep failed.", innerException)
        {
            CutoffUtc = cutoffUtc;
            DryRun = dryRun;
            BatchSize = batchSize;
            CandidateCount = candidateCount;
            CompletedBatchCount = completedBatchCount;
            DeletedCount = deletedCount;
            Elapsed = elapsed;
            ErrorType = innerException.GetType().Name;
        }

        internal DateTime CutoffUtc { get; }
        internal bool DryRun { get; }
        internal int BatchSize { get; }
        internal int? CandidateCount { get; }
        internal int CompletedBatchCount { get; }
        internal int DeletedCount { get; }
        internal TimeSpan Elapsed { get; }
        internal string ErrorType { get; }
    }

    internal class MessageLogRetentionRunner
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MessageLogRetentionRunner> _logger;

        public MessageLogRetentionRunner(
            IServiceScopeFactory scopeFactory,
            ILogger<MessageLogRetentionRunner> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        internal async Task<MessageLogRetentionSweepResult> RunAsync(
            MessageLogRetentionOptions options,
            DateTime utcNow,
            CancellationToken token)
        {
            if (!options.Enabled)
            {
                return MessageLogRetentionSweepResult.Skipped(
                    MessageLogRetentionSweepStatus.Disabled);
            }

            token.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            DateTime cutoffUtc = utcNow.AddDays(-options.RetentionDays);
            int? candidateCount = null;
            DateTime? oldestCandidateUtc = null;
            DateTime? newestCandidateUtc = null;
            int batchCount = 0;
            int deletedCount = 0;
            try
            {
                using (IServiceScope scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                    IQueryable<MessageLog> eligible = db.MessageLogs
                        .AsNoTracking()
                        .Where(log => log.LogTime < cutoffUtc);

                    candidateCount = await eligible.CountAsync(token);
                    if (candidateCount > 0)
                    {
                        oldestCandidateUtc = await eligible.MinAsync(
                            log => (DateTime?)log.LogTime,
                            token);
                        newestCandidateUtc = await eligible.MaxAsync(
                            log => (DateTime?)log.LogTime,
                            token);
                    }
                }

                int estimatedBatchCount = candidateCount == 0
                    ? 0
                    : (candidateCount.Value + options.BatchSize - 1) / options.BatchSize;
                _logger.LogInformation(
                    "MessageLog retention assessment: cutoff={Cutoff:u} dryRun={DryRun} batchSize={BatchSize} candidates={CandidateCount} estimatedBatches={EstimatedBatchCount} oldest={Oldest:u} newest={Newest:u} durationMs={DurationMs}",
                    cutoffUtc,
                    options.DryRun,
                    options.BatchSize,
                    candidateCount,
                    estimatedBatchCount,
                    oldestCandidateUtc,
                    newestCandidateUtc,
                    stopwatch.Elapsed.TotalMilliseconds);

                if (options.DryRun)
                {
                    stopwatch.Stop();
                    return new MessageLogRetentionSweepResult(
                        MessageLogRetentionSweepStatus.DryRun,
                        cutoffUtc,
                        candidateCount.Value,
                        0,
                        0,
                        estimatedBatchCount,
                        oldestCandidateUtc,
                        newestCandidateUtc,
                        stopwatch.Elapsed);
                }

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    MessageLogRetentionBatchResult batch;
                    using (IServiceScope scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                        var batchStopwatch = Stopwatch.StartNew();
                        List<MessageLogRetentionCandidate> candidates = await db.MessageLogs
                            .AsNoTracking()
                            .Where(log => log.LogTime < cutoffUtc)
                            .OrderBy(log => log.LogTime)
                            .ThenBy(log => log.LogId)
                            .Select(log => new MessageLogRetentionCandidate
                            {
                                LogId = log.LogId,
                                LogTime = log.LogTime
                            })
                            .Take(options.BatchSize)
                            .ToListAsync(token);

                        if (candidates.Count == 0)
                        {
                            batchStopwatch.Stop();
                            break;
                        }

                        List<int> identifiers = candidates
                            .Select(candidate => candidate.LogId)
                            .ToList();
                        int batchDeletedCount = await db.MessageLogs
                            .Where(log => identifiers.Contains(log.LogId))
                            .ExecuteDeleteAsync(token);
                        batchStopwatch.Stop();

                        batchCount++;
                        deletedCount += batchDeletedCount;
                        batch = new MessageLogRetentionBatchResult(
                            batchCount,
                            candidates.Count,
                            batchDeletedCount,
                            candidates[0].LogId,
                            candidates[candidates.Count - 1].LogId,
                            candidates[0].LogTime,
                            candidates[candidates.Count - 1].LogTime,
                            batchStopwatch.Elapsed);
                    }

                    await OnBatchCompletedAsync(batch, token);
                    token.ThrowIfCancellationRequested();
                }

                stopwatch.Stop();
                return new MessageLogRetentionSweepResult(
                    MessageLogRetentionSweepStatus.Completed,
                    cutoffUtc,
                    candidateCount.Value,
                    deletedCount,
                    batchCount,
                    estimatedBatchCount,
                    oldestCandidateUtc,
                    newestCandidateUtc,
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                throw new MessageLogRetentionSweepException(
                    cutoffUtc,
                    options.DryRun,
                    options.BatchSize,
                    candidateCount,
                    batchCount,
                    deletedCount,
                    stopwatch.Elapsed,
                    ex);
            }
        }

        protected virtual Task OnBatchCompletedAsync(
            MessageLogRetentionBatchResult batch,
            CancellationToken token)
        {
            _logger.LogInformation(
                "MessageLog retention batch {BatchNumber}: selected={SelectedCount} deleted={DeletedCount} firstLogId={FirstLogId} lastLogId={LastLogId} oldest={Oldest:u} newest={Newest:u} durationMs={DurationMs}",
                batch.BatchNumber,
                batch.SelectedCount,
                batch.DeletedCount,
                batch.FirstLogId,
                batch.LastLogId,
                batch.OldestCandidateUtc,
                batch.NewestCandidateUtc,
                batch.Elapsed.TotalMilliseconds);
            return Task.CompletedTask;
        }

        private sealed class MessageLogRetentionCandidate
        {
            internal int LogId { get; set; }
            internal DateTime LogTime { get; set; }
        }
    }
}
