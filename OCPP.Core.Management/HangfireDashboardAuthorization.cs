using System.Collections.Generic;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace OCPP.Core.Management
{
    public class HangfireDashboardAuthorization : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            return httpContext?.User?.Identity?.IsAuthenticated == true && httpContext.User.IsInRole(Constants.AdminRoleName);
        }
    }
}
