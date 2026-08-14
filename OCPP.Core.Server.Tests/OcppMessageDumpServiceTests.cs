using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class OcppMessageDumpServiceTests
    {
        [Fact]
        public void DumpMessage_WhenDirectoryIsBlank_WritesNoFiles()
        {
            using var fixture = new DumpFixture(new Dictionary<string, string?>
            {
                ["MessageDumpDir"] = ""
            });

            fixture.Service.DumpMessage("ocpp16-in", "[2,\"id\",\"Heartbeat\",{}]");

            Assert.False(Directory.Exists(fixture.DumpDirectory));
        }

        [Fact]
        public void DumpMessage_WhenEnabled_WritesTheExactPayload()
        {
            using var fixture = new DumpFixture();

            fixture.Service.DumpMessage("ocpp16-in", "payload-123");

            string path = Assert.Single(Directory.GetFiles(fixture.DumpDirectory, "*_ocpp16-in.txt"));
            Assert.Equal("payload-123", File.ReadAllText(path));
        }

        [Fact]
        public void CleanupExpiredFiles_RemovesStaleFilesAndPreservesRecentFiles()
        {
            using var fixture = new DumpFixture();
            string stale = fixture.CreateFile(
                "2026-08-12_00-00-00-0000_ocpp16-in.txt",
                new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));
            string recent = fixture.CreateFile(
                "2026-08-14_07-00-00-0000_ocpp16-out.txt",
                new DateTime(2026, 8, 14, 7, 0, 0, DateTimeKind.Utc));

            int deleted = fixture.Service.CleanupExpiredFiles(
                new DateTime(2026, 8, 14, 8, 0, 0, DateTimeKind.Utc));

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(recent));
        }

        [Fact]
        public void CleanupExpiredFiles_DoesNotRecurseIntoChildDirectories()
        {
            using var fixture = new DumpFixture();
            string childDirectory = Path.Combine(fixture.DumpDirectory, "archive");
            Directory.CreateDirectory(childDirectory);
            string nested = Path.Combine(childDirectory, "2026-08-12_00-00-00-0000_ocpp16-in.txt");
            File.WriteAllText(nested, "nested");
            File.SetLastWriteTimeUtc(nested, new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));

            int deleted = fixture.Service.CleanupExpiredFiles(
                new DateTime(2026, 8, 14, 8, 0, 0, DateTimeKind.Utc));

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(nested));
        }

        [Fact]
        public void CleanupExpiredFiles_PreservesNonTextFiles()
        {
            using var fixture = new DumpFixture();
            string binary = fixture.CreateFile(
                "2026-08-12_00-00-00-0000_ocpp16-in.bin",
                new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));

            int deleted = fixture.Service.CleanupExpiredFiles(
                new DateTime(2026, 8, 14, 8, 0, 0, DateTimeKind.Utc));

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(binary));
        }

        [Fact]
        public void CleanupExpiredFiles_WhenRetentionIsNotPositive_PreservesFiles()
        {
            using var fixture = new DumpFixture(new Dictionary<string, string?>
            {
                ["MessageDumpRetentionHours"] = "0"
            });
            string stale = fixture.CreateFile(
                "2026-08-12_00-00-00-0000_ocpp16-in.txt",
                new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));

            int deleted = fixture.Service.CleanupExpiredFiles(
                new DateTime(2026, 8, 14, 8, 0, 0, DateTimeKind.Utc));

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(stale));
        }

        private sealed class DumpFixture : IDisposable
        {
            public DumpFixture(IDictionary<string, string?>? overrides = null)
            {
                DumpDirectory = Path.Combine(Path.GetTempPath(), $"ocpp-message-dumps-{Guid.NewGuid():N}");
                var values = new Dictionary<string, string?>
                {
                    ["MessageDumpDir"] = DumpDirectory,
                    ["MessageDumpRetentionHours"] = "24",
                    ["MessageDumpCleanupIntervalMinutes"] = "15"
                };

                if (overrides != null)
                {
                    foreach (var pair in overrides)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }

                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(values)
                    .Build();
                Service = new OcppMessageDumpService(
                    configuration,
                    NullLogger<OcppMessageDumpService>.Instance);
            }

            public string DumpDirectory { get; }

            public OcppMessageDumpService Service { get; }

            public string CreateFile(string fileName, DateTime lastWriteTimeUtc)
            {
                Directory.CreateDirectory(DumpDirectory);
                string path = Path.Combine(DumpDirectory, fileName);
                File.WriteAllText(path, "fixture");
                File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
                return path;
            }

            public void Dispose()
            {
                Service.Dispose();
                if (Directory.Exists(DumpDirectory))
                {
                    Directory.Delete(DumpDirectory, recursive: true);
                }
            }
        }
    }
}
