using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Maintenance;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MessageLogRetentionRunnerTests
    {
        private static readonly DateTime Now =
            new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task RunAsync_DryRunReportsOnlyStrictlyOlderRows()
        {
            using var database = new RetentionDatabase();
            await database.SeedAsync(
                NewLog(Now.AddDays(-30).AddTicks(-1)),
                NewLog(Now.AddDays(-30)),
                NewLog(Now.AddDays(-30).AddTicks(1)));

            var result = await database.CreateRunner().RunAsync(
                EnabledOptions(dryRun: true, batchSize: 2),
                Now,
                default);

            Assert.Equal(MessageLogRetentionSweepStatus.DryRun, result.Status);
            Assert.Equal(Now.AddDays(-30), result.CutoffUtc);
            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(0, result.DeletedCount);
            Assert.Equal(0, result.BatchCount);
            Assert.Equal(1, result.EstimatedBatchCount);
            Assert.Equal(Now.AddDays(-30).AddTicks(-1), result.OldestCandidateUtc);
            Assert.Equal(Now.AddDays(-30).AddTicks(-1), result.NewestCandidateUtc);
            Assert.Equal(3, await database.CountAsync());
        }

        [Fact]
        public async Task RunAsync_ExecutionDeletesOnlyStrictlyOlderRowsAndReportsBounds()
        {
            using var database = new RetentionDatabase();
            DateTime oldestEligible = Now.AddDays(-60);
            DateTime newestEligible = Now.AddDays(-30).AddTicks(-1);
            DateTime exactCutoff = Now.AddDays(-30);
            DateTime newer = exactCutoff.AddTicks(1);
            await database.SeedAsync(
                NewLog(oldestEligible),
                NewLog(newestEligible),
                NewLog(exactCutoff),
                NewLog(newer));

            var result = await database.CreateRunner().RunAsync(
                EnabledOptions(dryRun: false, batchSize: 10),
                Now,
                default);

            Assert.Equal(MessageLogRetentionSweepStatus.Completed, result.Status);
            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(2, result.DeletedCount);
            Assert.Equal(oldestEligible, result.OldestCandidateUtc);
            Assert.Equal(newestEligible, result.NewestCandidateUtc);
            Assert.Equal(
                new[] { exactCutoff, newer },
                (await database.ReadAllAsync()).Select(log => log.LogTime));
        }

        [Fact]
        public async Task RunAsync_DeletesEligibleRowsInBoundedStableBatches()
        {
            using var database = new RetentionDatabase();
            DateTime timestamp = Now.AddDays(-45);
            await database.SeedAsync(Enumerable.Range(0, 5)
                .Select(_ => NewLog(timestamp))
                .ToArray());
            var runner = database.CreateRecordingRunner();

            var result = await runner.RunAsync(
                EnabledOptions(dryRun: false, batchSize: 2),
                Now,
                default);

            Assert.Equal(MessageLogRetentionSweepStatus.Completed, result.Status);
            Assert.Equal(new[] { 2, 2, 1 }, runner.BatchSelectedCounts);
            Assert.Equal(new[] { (1, 2), (3, 4), (5, 5) }, runner.BatchIdBounds);
            Assert.Equal(5, result.CandidateCount);
            Assert.Equal(5, result.DeletedCount);
            Assert.Equal(3, result.BatchCount);
            Assert.Equal(0, await database.CountAsync());
        }

        [Fact]
        public async Task RunAsync_CancellationLeavesCommittedBatchesForRestart()
        {
            using var database = new RetentionDatabase();
            await database.SeedAsync(Enumerable.Range(0, 5)
                .Select(offset => NewLog(Now.AddDays(-45).AddMinutes(offset)))
                .ToArray());
            using var cancellation = new CancellationTokenSource();
            var interrupted = database.CreateRecordingRunner(
                cancelAfterBatch: 1,
                cancellation: cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                interrupted.RunAsync(
                    EnabledOptions(dryRun: false, batchSize: 2),
                    Now,
                    cancellation.Token));

            Assert.Equal(3, await database.CountAsync());

            var resumed = await database.CreateRunner().RunAsync(
                EnabledOptions(dryRun: false, batchSize: 2),
                Now,
                default);

            Assert.Equal(3, resumed.CandidateCount);
            Assert.Equal(3, resumed.DeletedCount);
            Assert.Equal(2, resumed.BatchCount);
            Assert.Equal(0, await database.CountAsync());
        }

        [Fact]
        public async Task RunAsync_PreservesRowLoggedDuringSweep()
        {
            using var database = new RetentionDatabase();
            await database.SeedAsync(Enumerable.Range(0, 3)
                .Select(offset => NewLog(Now.AddDays(-45).AddMinutes(offset)))
                .ToArray());
            var runner = database.CreateRecordingRunner(
                rowToInsertAfterFirstBatch: NewLog(Now));

            var result = await runner.RunAsync(
                EnabledOptions(dryRun: false, batchSize: 2),
                Now,
                default);

            Assert.Equal(3, result.DeletedCount);
            MessageLog remaining = Assert.Single(await database.ReadAllAsync());
            Assert.Equal(Now, remaining.LogTime);
        }

        [Fact]
        public async Task RunAsync_IsNoOpForAlreadyCleanAndRepeatedSweeps()
        {
            using var database = new RetentionDatabase();
            await database.SeedAsync(NewLog(Now.AddDays(-31)));
            var runner = database.CreateRunner();

            var first = await runner.RunAsync(
                EnabledOptions(dryRun: false, batchSize: 10),
                Now,
                default);
            var repeated = await runner.RunAsync(
                EnabledOptions(dryRun: false, batchSize: 10),
                Now,
                default);

            Assert.Equal(1, first.DeletedCount);
            Assert.Equal(MessageLogRetentionSweepStatus.Completed, repeated.Status);
            Assert.Equal(0, repeated.CandidateCount);
            Assert.Equal(0, repeated.DeletedCount);
            Assert.Equal(0, repeated.BatchCount);
        }

        [Fact]
        public async Task RunAsync_LogsAssessmentBeforeDestructiveBatches()
        {
            using var database = new RetentionDatabase();
            await database.SeedAsync(NewLog(Now.AddDays(-45)));
            var logger = new RecordingLogger<MessageLogRetentionRunner>();

            await database.CreateRunner(logger).RunAsync(
                EnabledOptions(dryRun: false, batchSize: 10),
                Now,
                default);

            Assert.True(logger.Entries.Count >= 2);
            Assert.Contains("assessment", logger.Entries[0].Message);
            Assert.Contains("dryRun=False", logger.Entries[0].Message);
            Assert.Contains("candidates=1", logger.Entries[0].Message);
            Assert.Contains("batch 1", logger.Entries[1].Message);
        }

        [Fact]
        public async Task RunAsync_FailureCarriesSanitizedCompletedBatchProgress()
        {
            using var database = new RetentionDatabase();
            await database.SeedAsync(Enumerable.Range(0, 3)
                .Select(offset => NewLog(Now.AddDays(-45).AddMinutes(offset)))
                .ToArray());
            var runner = database.CreateRecordingRunner(throwAfterBatch: 1);

            var exception = await Assert.ThrowsAsync<MessageLogRetentionSweepException>(
                () => runner.RunAsync(
                    EnabledOptions(dryRun: false, batchSize: 2),
                    Now,
                    default));

            Assert.Equal(Now.AddDays(-30), exception.CutoffUtc);
            Assert.False(exception.DryRun);
            Assert.Equal(2, exception.BatchSize);
            Assert.Equal(3, exception.CandidateCount);
            Assert.Equal(1, exception.CompletedBatchCount);
            Assert.Equal(2, exception.DeletedCount);
            Assert.Equal(nameof(InvalidOperationException), exception.ErrorType);
            Assert.DoesNotContain("private failure marker", exception.Message);
        }

        private static MessageLogRetentionOptions EnabledOptions(
            bool dryRun,
            int batchSize)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:MessageLogRetention:Enabled"] = "true",
                    ["Maintenance:MessageLogRetention:DryRun"] = dryRun.ToString(),
                    ["Maintenance:MessageLogRetention:RetentionDays"] = "30",
                    ["Maintenance:MessageLogRetention:BatchSize"] = batchSize.ToString(),
                    ["Maintenance:MessageLogRetention:CleanupIntervalMinutes"] = "60"
                })
                .Build();

            Assert.True(MessageLogRetentionOptions.TryRead(
                configuration,
                out var options,
                out string error), error);
            return options;
        }

        private static MessageLog NewLog(DateTime logTime)
        {
            return new MessageLog
            {
                LogTime = logTime,
                ChargePointId = "CP-RETENTION",
                Message = "Heartbeat",
                Result = "Accepted",
                ErrorCode = string.Empty
            };
        }

        private sealed class RetentionDatabase : IDisposable
        {
            private readonly string _databasePath;
            private readonly ServiceProvider _provider;

            internal RetentionDatabase()
            {
                _databasePath = Path.Combine(
                    Path.GetTempPath(),
                    $"message-log-retention-{Guid.NewGuid():N}.sqlite");
                var services = new ServiceCollection();
                services.AddDbContext<OCPPCoreContext>(options =>
                    options.UseSqlite($"Data Source={_databasePath}"));
                _provider = services.BuildServiceProvider();

                using var scope = _provider.CreateScope();
                scope.ServiceProvider
                    .GetRequiredService<OCPPCoreContext>()
                    .Database
                    .EnsureCreated();
            }

            internal MessageLogRetentionRunner CreateRunner(
                Microsoft.Extensions.Logging.ILogger<MessageLogRetentionRunner>? logger = null)
            {
                return new MessageLogRetentionRunner(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    logger ?? NullLogger<MessageLogRetentionRunner>.Instance);
            }

            internal RecordingRunner CreateRecordingRunner(
                int? cancelAfterBatch = null,
                CancellationTokenSource? cancellation = null,
                MessageLog? rowToInsertAfterFirstBatch = null,
                int? throwAfterBatch = null)
            {
                return new RecordingRunner(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    this,
                    cancelAfterBatch,
                    cancellation,
                    rowToInsertAfterFirstBatch,
                    throwAfterBatch);
            }

            internal async Task SeedAsync(params MessageLog[] logs)
            {
                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.MessageLogs.AddRange(logs);
                await db.SaveChangesAsync();
            }

            internal async Task<int> CountAsync()
            {
                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                return await db.MessageLogs.CountAsync();
            }

            internal async Task<List<MessageLog>> ReadAllAsync()
            {
                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                return await db.MessageLogs.AsNoTracking().OrderBy(log => log.LogId).ToListAsync();
            }

            public void Dispose()
            {
                _provider.Dispose();
                TryDelete(_databasePath);
                TryDelete(_databasePath + "-wal");
                TryDelete(_databasePath + "-shm");
            }

            private static void TryDelete(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup after the provider releases the file.
                }
            }
        }

        private sealed class RecordingRunner : MessageLogRetentionRunner
        {
            private readonly RetentionDatabase _database;
            private readonly int? _cancelAfterBatch;
            private readonly CancellationTokenSource? _cancellation;
            private readonly MessageLog? _rowToInsertAfterFirstBatch;
            private readonly int? _throwAfterBatch;
            private bool _inserted;

            internal RecordingRunner(
                IServiceScopeFactory scopeFactory,
                RetentionDatabase database,
                int? cancelAfterBatch,
                CancellationTokenSource? cancellation,
                MessageLog? rowToInsertAfterFirstBatch,
                int? throwAfterBatch)
                : base(scopeFactory, NullLogger<MessageLogRetentionRunner>.Instance)
            {
                _database = database;
                _cancelAfterBatch = cancelAfterBatch;
                _cancellation = cancellation;
                _rowToInsertAfterFirstBatch = rowToInsertAfterFirstBatch;
                _throwAfterBatch = throwAfterBatch;
            }

            internal List<int> BatchSelectedCounts { get; } = new List<int>();
            internal List<(int First, int Last)> BatchIdBounds { get; } =
                new List<(int First, int Last)>();

            protected override async Task OnBatchCompletedAsync(
                MessageLogRetentionBatchResult batch,
                CancellationToken token)
            {
                BatchSelectedCounts.Add(batch.SelectedCount);
                BatchIdBounds.Add((batch.FirstLogId, batch.LastLogId));

                if (_throwAfterBatch == BatchSelectedCounts.Count)
                {
                    throw new InvalidOperationException("private failure marker");
                }

                if (!_inserted && _rowToInsertAfterFirstBatch != null)
                {
                    _inserted = true;
                    await _database.SeedAsync(_rowToInsertAfterFirstBatch);
                }

                if (_cancelAfterBatch == BatchSelectedCounts.Count)
                {
                    _cancellation!.Cancel();
                }
            }
        }
    }
}
