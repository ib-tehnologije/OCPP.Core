using System;
using System.IO;
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
