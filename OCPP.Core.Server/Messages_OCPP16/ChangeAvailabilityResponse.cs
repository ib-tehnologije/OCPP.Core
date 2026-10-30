namespace OCPP.Core.Server.Messages_OCPP16
{
#pragma warning disable // Disable all warnings

    public partial class ChangeAvailabilityResponse
    {
        [Newtonsoft.Json.JsonProperty("status", Required = Newtonsoft.Json.Required.Always)]
        [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = true)]
        [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public ChangeAvailabilityResponseStatus Status { get; set; }
    }

    public enum ChangeAvailabilityResponseStatus
    {
        [System.Runtime.Serialization.EnumMember(Value = @"Accepted")]
        Accepted = 0,

        [System.Runtime.Serialization.EnumMember(Value = @"Rejected")]
        Rejected = 1,

        [System.Runtime.Serialization.EnumMember(Value = @"Scheduled")]
        Scheduled = 2
    }
}
