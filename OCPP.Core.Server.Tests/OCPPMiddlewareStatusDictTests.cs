using System.Collections.Concurrent;
using OCPP.Core.Server;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class OCPPMiddlewareStatusDictTests
    {
        [Fact]
        public void TryRemoveStatusIfSameInstance_RemovesWhenInstanceMatches()
        {
            var statusDict = new ConcurrentDictionary<string, ChargePointStatus>();
            var status = new ChargePointStatus { Id = "ACE0816130" };
            statusDict[status.Id] = status;

            bool removed = OCPPMiddleware.TryRemoveStatusIfSameInstance(statusDict, status);

            Assert.True(removed);
            Assert.False(statusDict.ContainsKey(status.Id));
        }

        [Fact]
        public void TryRemoveStatusIfSameInstance_DoesNotRemoveWhenInstanceWasReplaced()
        {
            var statusDict = new ConcurrentDictionary<string, ChargePointStatus>();
            var staleStatus = new ChargePointStatus { Id = "ACE0816130" };
            var reconnectedStatus = new ChargePointStatus { Id = "ACE0816130" };

            statusDict[staleStatus.Id] = staleStatus;
            statusDict[staleStatus.Id] = reconnectedStatus;

            bool removed = OCPPMiddleware.TryRemoveStatusIfSameInstance(statusDict, staleStatus);

            Assert.False(removed);
            Assert.True(statusDict.TryGetValue(staleStatus.Id, out var current));
            Assert.Same(reconnectedStatus, current);
        }

        [Fact]
        public void TryRemoveStatusIfSameInstance_ReturnsFalseForInvalidInput()
        {
            var statusDict = new ConcurrentDictionary<string, ChargePointStatus>();

            Assert.False(OCPPMiddleware.TryRemoveStatusIfSameInstance(null, null));
            Assert.False(OCPPMiddleware.TryRemoveStatusIfSameInstance(statusDict, null));
            Assert.False(OCPPMiddleware.TryRemoveStatusIfSameInstance(statusDict, new ChargePointStatus()));
        }
    }
}
