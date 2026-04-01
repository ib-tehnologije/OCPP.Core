using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Management.Controllers;
using OCPP.Core.Management.Models;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicControllerTests
    {
        [Fact]
        public async Task Start_SingleConnector_KeepsCurrentSingleConnectorBehavior()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-single-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-SINGLE", "Single test");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-SINGLE",
                        ConnectorId = 1,
                        ConnectorName = "Main cable",
                        LastStatus = "Available",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-SINGLE", null);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.False(model.ShowConnectorSelector);
                Assert.Single(model.Connectors);
                Assert.Equal(1, model.ConnectorId);
                Assert.Equal("Main cable", model.ConnectorName);
                Assert.True(model.Connectors.Single().IsSelected);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_MultiConnector_SelectsRequestedConnectorAndUsesFriendlyLabels()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-multi-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-MULTI", "Multi test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-MULTI",
                            ConnectorId = 1,
                            LastStatus = "Occupied",
                            LastStatusTime = DateTime.UtcNow.AddMinutes(-2)
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-MULTI",
                            ConnectorId = 2,
                            ConnectorName = "Right side",
                            LastStatus = "Available",
                            LastStatusTime = DateTime.UtcNow.AddMinutes(-1)
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-MULTI", 2);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.True(model.ShowConnectorSelector);
                Assert.Equal(2, model.ConnectorId);
                Assert.Equal("Right side", model.ConnectorName);
                Assert.Null(model.PublicDisplayCode);
                Assert.Null(model.PublicConnectorCode);
                Assert.Null(model.PublicConnectorShortCode);
                Assert.Collection(
                    model.Connectors.OrderBy(c => c.ConnectorId),
                    connector =>
                    {
                        Assert.Equal(1, connector.ConnectorId);
                        Assert.Equal("Connector 1", connector.Label);
                        Assert.Equal("Connector 1", connector.DisplayName);
                        Assert.Null(connector.PublicConnectorCode);
                        Assert.Null(connector.PublicConnectorShortCode);
                        Assert.False(connector.IsSelected);
                    },
                    connector =>
                    {
                        Assert.Equal(2, connector.ConnectorId);
                        Assert.Equal("Right side", connector.Label);
                        Assert.Equal("Right side", connector.DisplayName);
                        Assert.Null(connector.PublicConnectorCode);
                        Assert.Null(connector.PublicConnectorShortCode);
                        Assert.True(connector.IsSelected);
                    });
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_InvalidConnector_FallsBackToFirstAvailableConnector()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-fallback-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-FALLBACK", "Fallback test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-FALLBACK",
                            ConnectorId = 1,
                            LastStatus = "Occupied",
                            LastStatusTime = DateTime.UtcNow
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-FALLBACK",
                            ConnectorId = 2,
                            LastStatus = "Available",
                            LastStatusTime = DateTime.UtcNow
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-FALLBACK", 99);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal(2, model.ConnectorId);
                Assert.Equal("Connector 2", model.ConnectorName);
                Assert.True(model.Connectors.Single(c => c.ConnectorId == 2).IsSelected);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_GenericChargePointUrl_SelectsFirstAvailableConnector()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-generic-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-GENERIC", "Generic test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-GENERIC",
                            ConnectorId = 1,
                            LastStatus = "Occupied",
                            LastStatusTime = DateTime.UtcNow
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-GENERIC",
                            ConnectorId = 2,
                            LastStatus = "Available",
                            LastStatusTime = DateTime.UtcNow
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-GENERIC", null);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal(2, model.ConnectorId);
                Assert.Equal("Connector 2", model.ConnectorName);
                Assert.True(model.ShowConnectorSelector);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_PublicDisplayCode_UsesDerivedPublicConnectorLabels()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-public-code-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-PUBLIC-CODE", "Delmar Emotion", "HR*TTK*052009*01");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-PUBLIC-CODE",
                            ConnectorId = 1,
                            ConnectorName = "Lijevi",
                            LastStatus = "Available"
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-PUBLIC-CODE",
                            ConnectorId = 2,
                            ConnectorName = "Desni",
                            LastStatus = "Available"
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-PUBLIC-CODE", 2);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal("HR*TTK*052009*01", model.PublicDisplayCode);
                Assert.Equal("HR*TTK*052009*01*2", model.PublicConnectorCode);
                Assert.Equal("01*2", model.PublicConnectorShortCode);
                Assert.Equal("Desni", model.ConnectorName);
                Assert.Collection(
                    model.Connectors.OrderBy(c => c.ConnectorId),
                    connector =>
                    {
                        Assert.Equal(1, connector.ConnectorId);
                        Assert.Equal("01*1", connector.Label);
                        Assert.Equal("Lijevi", connector.DisplayName);
                        Assert.Equal("HR*TTK*052009*01*1", connector.PublicConnectorCode);
                        Assert.Equal("01*1", connector.PublicConnectorShortCode);
                    },
                    connector =>
                    {
                        Assert.Equal(2, connector.ConnectorId);
                        Assert.Equal("01*2", connector.Label);
                        Assert.Equal("Desni", connector.DisplayName);
                        Assert.Equal("HR*TTK*052009*01*2", connector.PublicConnectorCode);
                        Assert.Equal("01*2", connector.PublicConnectorShortCode);
                    });
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_PendingReservation_ShowsReservedAvailabilityMessage()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-reserved-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-RESERVED", "Reserved test");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-RESERVED",
                        ConnectorId = 1,
                        LastStatus = "Available",
                        LastStatusTime = DateTime.UtcNow.AddMinutes(-1)
                    });
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = Guid.NewGuid(),
                        ChargePointId = "CP-RESERVED",
                        ConnectorId = 1,
                        ChargeTagId = "WEB-RESERVED",
                        Status = PaymentReservationStatus.Pending,
                        Currency = "eur",
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-RESERVED", 1);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal("Occupied", model.LastStatus);
                Assert.Contains("temporarily reserved", model.AvailabilityMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("ActiveReservation", model.Connectors.Single().OccupancyReason);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_SuspendedEv_ShowsInUseAvailabilityMessage()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-suspendedev-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-SUSPENDED-EV", "Suspended EV test");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-SUSPENDED-EV",
                        ConnectorId = 1,
                        LastStatus = "SuspendedEV",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-SUSPENDED-EV", 1);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal("Occupied", model.LastStatus);
                Assert.Contains("currently in use", model.AvailabilityMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_SuspendedEvse_ShowsInUseAvailabilityMessage()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-suspendedevse-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-SUSPENDED-EVSE", "Suspended EVSE test");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-SUSPENDED-EVSE",
                        ConnectorId = 1,
                        LastStatus = "SuspendedEVSE",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-SUSPENDED-EVSE", 1);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal("Occupied", model.LastStatus);
                Assert.Contains("currently in use", model.AvailabilityMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("LiveStatus:SuspendedEV")]
        [InlineData("LiveStatus:SuspendedEVSE")]
        [InlineData("PersistedStatus:SuspendedEV")]
        [InlineData("PersistedStatus:SuspendedEVSE")]
        [InlineData("LiveStatus:Finishing")]
        [InlineData("PersistedStatus:Finishing")]
        public void BuildBusyMessage_MapsPausedAndFinishingReasonsToInUseMessage(string reason)
        {
            string message = InvokeBuildBusyMessage(reason);

            Assert.Contains("currently in use", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Start_FallsBackToLowestConnector_WhenNoConnectorIsAvailable()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-no-available-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-NO-AVAILABLE", "No available test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-NO-AVAILABLE",
                            ConnectorId = 2,
                            LastStatus = "Faulted"
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-NO-AVAILABLE",
                            ConnectorId = 3,
                            LastStatus = "Occupied"
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-NO-AVAILABLE", null);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal(2, model.ConnectorId);
                Assert.Equal("Connector 2", model.ConnectorName);
                Assert.True(model.Connectors.Single(c => c.ConnectorId == 2).IsSelected);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void Map_MultiConnectorChargePoint_ReportsConnectorCountForChooserFlow()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-map-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-MAP", "Map test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-MAP",
                            ConnectorId = 1,
                            LastStatus = "Available",
                            LastStatusTime = DateTime.UtcNow
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-MAP",
                            ConnectorId = 2,
                            LastStatus = "Occupied",
                            LastStatusTime = DateTime.UtcNow
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = controller.Map();
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicMapViewModel>(viewResult.Model);
                var chargePoint = Assert.Single(model.ChargePoints);

                Assert.Equal("CP-MAP", chargePoint.ChargePointId);
                Assert.Equal(2, chargePoint.ConnectorCount);
                Assert.Equal(1, chargePoint.AvailableConnectorCount);
                Assert.Equal(1, chargePoint.OccupiedConnectorCount);
                Assert.Equal(0, chargePoint.OfflineConnectorCount);
                Assert.True(chargePoint.HasMultipleConnectors);
                Assert.Equal("Available", chargePoint.Status);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void Map_UnknownOrStaleStatuses_AreRenderedAsOffline()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-map-offline-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-OFFLINE", "Offline test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-OFFLINE",
                            ConnectorId = 1,
                            LastStatus = "Unknown",
                            LastStatusTime = DateTime.UtcNow
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-OFFLINE",
                            ConnectorId = 2,
                            LastStatus = "Available",
                            LastStatusTime = DateTime.UtcNow.AddMinutes(-20)
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = controller.Map();
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicMapViewModel>(viewResult.Model);
                var chargePoint = Assert.Single(model.ChargePoints);

                Assert.Equal("Offline", chargePoint.Status);
                Assert.Equal(0, chargePoint.AvailableConnectorCount);
                Assert.Equal(0, chargePoint.OccupiedConnectorCount);
                Assert.Equal(2, chargePoint.OfflineConnectorCount);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void Map_PublicDisplayCode_IsExposedForPublicPortalRendering()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-map-public-code-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-MAP-CODE", "Delmar Banjole", "HR*TTK*052009*02");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-MAP-CODE",
                        ConnectorId = 1,
                        LastStatus = "Available"
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = controller.Map();
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicMapViewModel>(viewResult.Model);
                var chargePoint = Assert.Single(model.ChargePoints);

                Assert.Equal("Delmar Banjole", chargePoint.Name);
                Assert.Equal("HR*TTK*052009*02", chargePoint.PublicDisplayCode);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void Map_ChargePointIdCaseMismatch_StillUsesConnectorStatus()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-map-case-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "ns2571148979", "Huawei test");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "ns2571148979",
                        ConnectorId = 1,
                        LastStatus = "Available",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using (var connection = new SqliteConnection($"Data Source={databasePath}"))
                {
                    connection.Open();

                    using var disableCommand = connection.CreateCommand();
                    disableCommand.CommandText = "PRAGMA foreign_keys = OFF;";
                    disableCommand.ExecuteNonQuery();

                    using var updateCommand = connection.CreateCommand();
                    updateCommand.CommandText = "UPDATE ConnectorStatus SET ChargePointId = 'NS2571148979' WHERE ChargePointId = 'ns2571148979';";
                    updateCommand.ExecuteNonQuery();

                    using var enableCommand = connection.CreateCommand();
                    enableCommand.CommandText = "PRAGMA foreign_keys = ON;";
                    enableCommand.ExecuteNonQuery();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = controller.Map();
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicMapViewModel>(viewResult.Model);
                var chargePoint = Assert.Single(model.ChargePoints);

                Assert.Equal("ns2571148979", chargePoint.ChargePointId);
                Assert.Equal("Available", chargePoint.Status);
                Assert.Equal(1, chargePoint.AvailableConnectorCount);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_UnknownConnectorStatus_IsNormalizedToOffline()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-start-offline-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-START-OFFLINE", "Offline start test");
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-START-OFFLINE",
                        ConnectorId = 1,
                        LastStatus = "Unknown",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start("CP-START-OFFLINE", 1);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.Equal("Offline", model.LastStatus);
                Assert.Equal("Offline", model.Connectors.Single().LastStatus);
                Assert.Contains("offline", model.AvailabilityMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Start_PostRejectsConnectorOutsideChargePoint()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"public-controller-post-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    SeedChargePoint(setupContext, "CP-POST", "Post test");
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-POST",
                            ConnectorId = 1,
                            LastStatus = "Available"
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-POST",
                            ConnectorId = 2,
                            LastStatus = "Available"
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = await controller.Start(new PublicStartViewModel
                {
                    ChargePointId = "CP-POST",
                    ConnectorId = 99
                });

                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);
                Assert.Contains("Selected connector is not available", model.ErrorMessage);
                Assert.Equal(1, model.ConnectorId);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static PublicController CreateController(OCPPCoreContext dbContext)
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            IDataProtectionProvider dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ocpp-public-controller-tests")));

            var controller = new PublicController(null, NullLoggerFactory.Instance, config, dataProtectionProvider, dbContext)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            return controller;
        }

        private static OCPPCoreContext CreateContext(string databasePath)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private static void SeedChargePoint(OCPPCoreContext context, string chargePointId, string chargePointName, string? publicDisplayCode = null)
        {
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = chargePointId,
                Name = chargePointName,
                PublicDisplayCode = publicDisplayCode,
                PricePerKwh = 0.45m,
                UserSessionFee = 0.50m,
                MaxSessionKwh = 20,
                StartUsageFeeAfterMinutes = 15,
                MaxUsageFeeMinutes = 120,
                ConnectorUsageFeePerMinute = 0.10m
            });
        }

        private static void TryDelete(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch
            {
                // Best effort cleanup for temp test DBs.
            }
        }

        private static string InvokeBuildBusyMessage(string reason)
        {
            MethodInfo? method = typeof(PublicController).GetMethod("BuildBusyMessage", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            return Assert.IsType<string>(method!.Invoke(null, new object[] { reason }));
        }
    }
}
