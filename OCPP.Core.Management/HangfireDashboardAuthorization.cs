using System.Collections.Generic;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace OCPP.Core.Management
{
    public class HangfireDashboardAuthorization : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            return true;
        }
    }
}
