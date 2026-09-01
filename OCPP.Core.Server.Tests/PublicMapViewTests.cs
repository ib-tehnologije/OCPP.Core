using System;
using System.IO;
using OCPP.Core.Management.Models;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicMapViewTests
    {
        [Fact]
        public void MapView_UsesStationListAsPrimarySelectionSurface()
        {
            string view = ReadPublicMapView();

            Assert.DoesNotContain("bindPopup", view);
            Assert.DoesNotContain("map-popup-actions", view);
            Assert.Contains("scrollToChargerList", view);
            Assert.Contains("map.on('click'", view);
        }

        [Fact]
        public void MapView_FiltersStationListToSearchAndVisibleMapBounds()
        {
            string view = ReadPublicMapView();

            Assert.Contains("getCurrentBounds", view);
            Assert.Contains("containerPointToLatLng", view);
            Assert.Contains("withinMapBounds", view);
            Assert.Contains("const visible = matchesSearch && withinMapBounds", view);
            Assert.Contains("map.on('moveend', () => filterMapAndList())", view);
            Assert.Contains("map.on('zoomend', () => filterMapAndList())", view);
        }

        [Fact]
        public void MapView_PersistsLastMapSelectionInStationList()
        {
            string view = ReadPublicMapView();

            Assert.Contains("let selectedChargePointId", view);
            Assert.Contains("selectChargerCard", view);
            Assert.Contains("aria-current", view);
            Assert.Contains("updateSelectedMarker", view);
            Assert.DoesNotContain("setTimeout(() => card.classList.remove('active')", view);
            Assert.DoesNotContain("classList.remove('active'), 1200", view);
        }

        [Theory]
        [InlineData(45.815399, 15.966568, "https://www.google.com/maps/dir/?api=1&destination=45.815399,15.966568")]
        [InlineData(-33.8688, 151.2093, "https://www.google.com/maps/dir/?api=1&destination=-33.8688,151.2093")]
        public void ChargePointNavigationUrl_UsesExactStoredCoordinates(
            double latitude,
            double longitude,
            string expectedUrl)
        {
            var chargePoint = new PublicMapChargePoint
            {
                Latitude = latitude,
                Longitude = longitude
            };

            Assert.Equal(expectedUrl, chargePoint.NavigationUrl);
        }

        [Fact]
        public void ChargePointNavigationUrl_IsHiddenForMissingOrInvalidCoordinates()
        {
            var chargePoints = new[]
            {
                new PublicMapChargePoint { Latitude = null, Longitude = 15.966568 },
                new PublicMapChargePoint { Latitude = 45.815399, Longitude = null },
                new PublicMapChargePoint { Latitude = 90.000001, Longitude = 15.966568 },
                new PublicMapChargePoint { Latitude = 45.815399, Longitude = -180.000001 },
                new PublicMapChargePoint { Latitude = double.NaN, Longitude = 15.966568 },
                new PublicMapChargePoint { Latitude = 45.815399, Longitude = double.PositiveInfinity }
            };

            Assert.All(chargePoints, chargePoint => Assert.Null(chargePoint.NavigationUrl));
        }

        private static string ReadPublicMapView()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                var viewPath = Path.Combine(
                    directory.FullName,
                    "OCPP.Core.Management",
                    "Views",
                    "Public",
                    "Map.cshtml");

                if (File.Exists(viewPath))
                {
                    return File.ReadAllText(viewPath);
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not locate Views/Public/Map.cshtml from the test output directory.");
        }
    }
}
