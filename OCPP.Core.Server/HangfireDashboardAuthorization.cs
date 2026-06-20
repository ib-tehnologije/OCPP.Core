using Hangfire.Dashboard;

namespace OCPP.Core.Server
{
    public class HangfireDashboardAuthorization : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            return true;
        }
    }
}
