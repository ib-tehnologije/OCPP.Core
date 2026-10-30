namespace OCPP.Core.Server.Messages_OCPP16
{
#pragma warning disable // Disable all warnings

    public partial class ChangeAvailabilityRequest
    {
        /// <summary>0 addresses the whole charge point, a value above 0 a single connector.
        /// </summary>
        [Newtonsoft.Json.JsonProperty("connectorId", Required = Newtonsoft.Json.Required.Always)]
        public int ConnectorId { get; set; }

        [Newtonsoft.Json.JsonProperty("type", Required = Newtonsoft.Json.Required.Always)]
        [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = true)]
        [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public ChangeAvailabilityRequestType Type { get; set; }
    }

    public enum ChangeAvailabilityRequestType
    {
        [System.Runtime.Serialization.EnumMember(Value = @"Inoperative")]
        Inoperative = 0,

        [System.Runtime.Serialization.EnumMember(Value = @"Operative")]
        Operative = 1
    }
}
