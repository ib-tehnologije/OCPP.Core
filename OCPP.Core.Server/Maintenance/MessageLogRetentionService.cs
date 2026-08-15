using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OCPP.Core.Server.Maintenance
{
    internal sealed class MessageLogRetentionService : BackgroundService
    {
        private readonly MessageLogRetentionRunner _runner;
        private readonly ILogger<MessageLogRetentionService> _logger;
        private readonly MessageLogRetentionOptions _options;
        private readonly bool _configurationIsValid;

        public MessageLogRetentionService(
            MessageLogRetentionRunner runner,
            ILogger<MessageLogRetentionService> logger,
            IConfiguration configuration)
        {
            _runner = runner;
            _logger = logger;
            _configurationIsValid = MessageLogRetentionOptions.TryRead(
                configuration,
                out _options,
                out string error);

            if (!_configurationIsValid)
            {
                _logger.LogWarning(
                    "MessageLog retention is disabled because configuration is invalid: {ConfigurationError}",
                    error);
            }
        }

        internal async Task<MessageLogRetentionSweepResult> RunOnceAsync(
            DateTime utcNow,
            CancellationToken token)
        {
            if (!_configurationIsValid)
            {
                return MessageLogRetentionSweepResult.Skipped(
                    MessageLogRetentionSweepStatus.InvalidConfiguration);
            }

            if (!_options.Enabled)
            {
                return MessageLogRetentionSweepResult.Skipped(
                    MessageLogRetentionSweepStatus.Disabled);
            }

            MessageLogRetentionSweepResult result = await _runner.RunAsync(
                _options,
                utcNow,
                token);
            _logger.LogInformation(
                "MessageLog retention sweep {Status}: cutoff={Cutoff:u} candidates={CandidateCount} deleted={DeletedCount} batches={BatchCount} estimatedBatches={EstimatedBatchCount} oldest={Oldest:u} newest={Newest:u} durationMs={DurationMs}",
                result.Status,
                result.CutoffUtc,
                result.CandidateCount,
                result.DeletedCount,
                result.BatchCount,
                result.EstimatedBatchCount,
                result.OldestCandidateUtc,
                result.NewestCandidateUtc,
                result.Elapsed.TotalMilliseconds);
            return result;
        }

        internal async Task<MessageLogRetentionSweepResult> RunOnceSafelyAsync(
            DateTime utcNow,
            CancellationToken token)
        {
            try
            {
                return await RunOnceAsync(utcNow, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (MessageLogRetentionSweepException ex)
            {
                _logger.LogWarning(
                    "MessageLog retention sweep failed: cutoff={Cutoff:u} dryRun={DryRun} batchSize={BatchSize} candidates={CandidateCount} completedBatches={CompletedBatchCount} deleted={DeletedCount} durationMs={DurationMs} errorType={ErrorType}",
                    ex.CutoffUtc,
                    ex.DryRun,
                    ex.BatchSize,
                    ex.CandidateCount,
                    ex.CompletedBatchCount,
                    ex.DeletedCount,
                    ex.Elapsed.TotalMilliseconds,
                    ex.ErrorType);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "MessageLog retention sweep failed: cutoff={Cutoff:u} dryRun={DryRun} batchSize={BatchSize} candidates={CandidateCount} completedBatches={CompletedBatchCount} deleted={DeletedCount} durationMs={DurationMs} errorType={ErrorType}",
                    utcNow.AddDays(-_options.RetentionDays),
                    _options.DryRun,
                    _options.BatchSize,
                    null,
                    0,
                    0,
                    0,
                    ex.GetType().Name);
                return null;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configurationIsValid)
            {
                return;
            }

            if (!_options.Enabled)
            {
                _logger.LogInformation("MessageLog retention is disabled");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.CleanupInterval, stoppingToken);
                    await RunOnceSafelyAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
