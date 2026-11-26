using OCPP.Core.Database;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicConnectorCodesTests
    {
        [Theory]
        [InlineData("HR*ABC*E1234501", 1, "1A")]
        [InlineData("HR*ABC*E1234501", 2, "1B")]
        [InlineData("HR*ABC*E1234502", 1, "2A")]
        [InlineData("HR*ABC*E1234504", 2, "4B")]
        [InlineData("HR*ABC*E1234503", 3, "3C")]
        [InlineData("HR*ABC*E1234512", 1, "12A")]
        [InlineData("HR*ABC*E1234500", 2, "1B")]
        [InlineData("HR*ABC*052009*01", 2, "1B")]
        [InlineData("HR-ABC-051-006", 1, "6A")]
        [InlineData("123", 1, "23A")]
        [InlineData("7", 2, "7B")]
        public void BuildConnectorLabel_UsesLastTwoDigitsAndConnectorLetter(string code, int connectorId, string expected)
        {
            Assert.Equal(expected, PublicConnectorCodes.BuildConnectorLabel(code, connectorId));
        }

        [Theory]
        [InlineData("HR*ABC*STATION", 2, "2")]
        [InlineData("HR*ABC*E1234501", 27, "27")]
        public void BuildConnectorLabel_FallsBackToConnectorNumber(string code, int connectorId, string expected)
        {
            Assert.Equal(expected, PublicConnectorCodes.BuildConnectorLabel(code, connectorId));
        }

        [Theory]
        [InlineData(null, 1)]
        [InlineData("  ", 1)]
        [InlineData("HR*ABC*E1234501", 0)]
        public void BuildConnectorLabel_ReturnsNullWithoutCodeOrConnector(string code, int connectorId)
        {
            Assert.Null(PublicConnectorCodes.BuildConnectorLabel(code, connectorId));
            Assert.Null(PublicConnectorCodes.BuildEvseId(code, connectorId));
        }

        [Fact]
        public void BuildEvseId_AppendsConnectorLabel()
        {
            Assert.Equal("HR*ABC*E1234502*2B", PublicConnectorCodes.BuildEvseId(" HR*ABC*E1234502 ", 2));
        }

        [Theory]
        [InlineData("HRABCE1234501", "HR*ABC*E1234501")]
        [InlineData("hrabce1234501", "HR*ABC*E1234501")]
        [InlineData("DEA12E9", "DE*A12*E9")]
        [InlineData("ACE0784639", null)]
        [InlineData("22290064", null)]
        [InlineData("24-M2-01-21", null)]
        [InlineData("HR*ABC*E1234501", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void DeriveChargePointCode_OnlyForEvseShapedIds(string chargePointId, string expected)
        {
            Assert.Equal(expected, PublicConnectorCodes.DeriveChargePointCode(chargePointId));
        }

        [Fact]
        public void ResolveChargePointCode_PrefersConfiguredCode()
        {
            Assert.Equal("HR*XYZ*E9", PublicConnectorCodes.ResolveChargePointCode(" HR*XYZ*E9 ", "HRABCE1234501"));
            Assert.Equal("HR*ABC*E1234501", PublicConnectorCodes.ResolveChargePointCode(null, "HRABCE1234501"));
            Assert.Equal("HR*ABC*E1234501", PublicConnectorCodes.ResolveChargePointCode("  ", "HRABCE1234501"));
            Assert.Null(PublicConnectorCodes.ResolveChargePointCode(null, "ACE0784639"));
        }
    }
}
