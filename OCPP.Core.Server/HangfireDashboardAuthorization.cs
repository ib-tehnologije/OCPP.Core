using System.Net;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace OCPP.Core.Server
{
    public class HangfireDashboardAuthorization : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext == null)
            {
                return false;
            }

            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp == null)
            {
                return false;
            }

            if (IPAddress.IsLoopback(remoteIp))
            {
                return true;
            }

            var localIp = httpContext.Connection.LocalIpAddress;
            return localIp != null && remoteIp.Equals(localIp);
        }
    }
}
