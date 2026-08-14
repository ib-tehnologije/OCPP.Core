using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OCPP.Core.Server
{
    /// <summary>
    /// Writes optional raw OCPP message dumps and removes expired dump files.
    /// </summary>
    public sealed class OcppMessageDumpService : BackgroundService
    {
        private readonly ILogger<OcppMessageDumpService> _logger;
        private readonly string _dumpDirectory;
        private readonly TimeSpan? _retention;
        private readonly TimeSpan? _cleanupInterval;

        public OcppMessageDumpService(
            IConfiguration configuration,
            ILogger<OcppMessageDumpService> logger)
        {
            _logger = logger;

            string configuredDirectory = configuration.GetValue<string>("MessageDumpDir");
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                try
                {
                    _dumpDirectory = Path.GetFullPath(configuredDirectory);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid OCPP message dump directory");
                }
            }

            int retentionHours = configuration.GetValue<int?>("MessageDumpRetentionHours") ?? 24;
            if (retentionHours > 0)
            {
                _retention = TimeSpan.FromHours(retentionHours);
            }
            else
            {
                _logger.LogWarning(
                    "OCPP message dump cleanup disabled because MessageDumpRetentionHours is not positive");
            }

            int cleanupIntervalMinutes =
                configuration.GetValue<int?>("MessageDumpCleanupIntervalMinutes") ?? 15;
            if (cleanupIntervalMinutes > 0)
            {
                _cleanupInterval = TimeSpan.FromMinutes(cleanupIntervalMinutes);
            }
            else
            {
                _logger.LogWarning(
                    "OCPP message dump cleanup disabled because MessageDumpCleanupIntervalMinutes is not positive");
            }
        }

        public void DumpMessage(string nameSuffix, string message)
        {
            if (string.IsNullOrWhiteSpace(_dumpDirectory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_dumpDirectory);
                string fileName = $"{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss-ffff}_{nameSuffix}.txt";
                File.WriteAllText(Path.Combine(_dumpDirectory, fileName), message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dumping OCPP message {Suffix}", nameSuffix);
            }
        }

        internal int CleanupExpiredFiles(DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(_dumpDirectory) ||
                !_retention.HasValue ||
                !Directory.Exists(_dumpDirectory))
            {
                return 0;
            }

            DateTime cutoff = utcNow - _retention.Value;
            int deleted = 0;

            try
            {
                var dumpDirectory = new DirectoryInfo(_dumpDirectory);
                foreach (var file in dumpDirectory.EnumerateFiles("*.txt", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                            file.LastWriteTimeUtc >= cutoff)
                        {
                            continue;
                        }

                        file.Delete();
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to remove expired OCPP message dump {Path}", file.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to enumerate OCPP message dump directory {Path}", _dumpDirectory);
            }

            return deleted;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_dumpDirectory) ||
                !_retention.HasValue ||
                !_cleanupInterval.HasValue)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                CleanupExpiredFiles(DateTime.UtcNow);

                try
                {
                    await Task.Delay(_cleanupInterval.Value, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Application is shutting down.
                }
            }
        }
    }
}
