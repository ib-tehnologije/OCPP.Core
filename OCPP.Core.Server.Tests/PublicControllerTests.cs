using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Management.Controllers;
using OCPP.Core.Management.Models;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicControllerTests
    {
        [Fact]
        public void Start_SingleConnector_KeepsCurrentSingleConnectorBehavior()
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

                var result = controller.Start("CP-SINGLE", null);
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
        public void Start_MultiConnector_SelectsRequestedConnectorAndUsesFriendlyLabels()
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

                var result = controller.Start("CP-MULTI", 2);
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<PublicStartViewModel>(viewResult.Model);

                Assert.True(model.ShowConnectorSelector);
                Assert.Equal(2, model.ConnectorId);
                Assert.Equal("Right side", model.ConnectorName);
                Assert.Collection(
                    model.Connectors.OrderBy(c => c.ConnectorId),
                    connector =>
                    {
                        Assert.Equal(1, connector.ConnectorId);
                        Assert.Equal("Connector 1", connector.Label);
                        Assert.False(connector.IsSelected);
                    },
                    connector =>
                    {
                        Assert.Equal(2, connector.ConnectorId);
                        Assert.Equal("Right side", connector.Label);
                        Assert.True(connector.IsSelected);
                    });
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void Start_InvalidConnector_FallsBackToFirstAvailableConnector()
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
                            LastStatus = "Occupied"
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-FALLBACK",
                            ConnectorId = 2,
                            LastStatus = "Available"
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = controller.Start("CP-FALLBACK", 99);
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
        public void Start_GenericChargePointUrl_SelectsFirstAvailableConnector()
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
                            LastStatus = "Occupied"
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-GENERIC",
                            ConnectorId = 2,
                            LastStatus = "Available"
                        });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var controller = CreateController(actionContext);

                var result = controller.Start("CP-GENERIC", null);
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
        public void Start_FallsBackToLowestConnector_WhenNoConnectorIsAvailable()
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

                var result = controller.Start("CP-NO-AVAILABLE", null);
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
                            LastStatus = "Available"
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-MAP",
                            ConnectorId = 2,
                            LastStatus = "Occupied"
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
                Assert.True(chargePoint.HasMultipleConnectors);
                Assert.Equal("Occupied", chargePoint.Status);
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

            return new PublicController(null, NullLoggerFactory.Instance, config, dbContext);
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

        private static void SeedChargePoint(OCPPCoreContext context, string chargePointId, string chargePointName)
        {
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = chargePointId,
                Name = chargePointName,
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
    }
}
