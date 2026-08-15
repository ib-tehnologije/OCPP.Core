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

    internal class MessageLogRetentionRunner
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MessageLogRetentionRunner> _logger;

        internal MessageLogRetentionRunner(
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

            int candidateCount;
            DateTime? oldestCandidateUtc = null;
            DateTime? newestCandidateUtc = null;
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
                : (candidateCount + options.BatchSize - 1) / options.BatchSize;
            if (options.DryRun)
            {
                stopwatch.Stop();
                return new MessageLogRetentionSweepResult(
                    MessageLogRetentionSweepStatus.DryRun,
                    cutoffUtc,
                    candidateCount,
                    0,
                    0,
                    estimatedBatchCount,
                    oldestCandidateUtc,
                    newestCandidateUtc,
                    stopwatch.Elapsed);
            }

            int batchCount = 0;
            int deletedCount = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                MessageLogRetentionBatchResult batch;
                using (IServiceScope scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
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
                        break;
                    }

                    var batchStopwatch = Stopwatch.StartNew();
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
                candidateCount,
                deletedCount,
                batchCount,
                estimatedBatchCount,
                oldestCandidateUtc,
                newestCandidateUtc,
                stopwatch.Elapsed);
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
