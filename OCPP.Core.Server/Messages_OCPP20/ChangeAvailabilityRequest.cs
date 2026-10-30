namespace OCPP.Core.Server.Messages_OCPP20
{
#pragma warning disable // Disable all warnings

    public enum OperationalStatusEnumType
    {
        [System.Runtime.Serialization.EnumMember(Value = @"Inoperative")]
        Inoperative = 0,

        [System.Runtime.Serialization.EnumMember(Value = @"Operative")]
        Operative = 1
    }

    public partial class ChangeAvailabilityRequest
    {
        [Newtonsoft.Json.JsonProperty("customData", Required = Newtonsoft.Json.Required.DisallowNull, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public CustomDataType? CustomData { get; set; }

        /// <summary>Omitted when the availability change applies to the whole charging station.
        /// </summary>
        [Newtonsoft.Json.JsonProperty("evse", Required = Newtonsoft.Json.Required.DisallowNull, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public EVSEType? Evse { get; set; }

        [Newtonsoft.Json.JsonProperty("operationalStatus", Required = Newtonsoft.Json.Required.Always)]
        [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = true)]
        [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public OperationalStatusEnumType OperationalStatus { get; set; }
    }
}
