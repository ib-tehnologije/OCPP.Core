/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

namespace OCPP.Core.Server.Messages_OCPP16
{
    public enum ResetRequestType
    {
        Hard = 0,
        Soft = 1
    }

    public enum ResetStatus
    {
        Accepted = 0,
        Rejected = 1
    }

    public class ResetRequest
    {
        public ResetRequestType Type { get; set; }
    }

    public class ResetResponse
    {
        public ResetStatus Status { get; set; }
    }
}
