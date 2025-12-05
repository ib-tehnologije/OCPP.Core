using System.Collections.Generic;

namespace OCPP.Core.Management.Models
{
    public static class AlfenConfigCatalog
    {
        public static List<AlfenConfigOption> All => new List<AlfenConfigOption>
        {
            // 1. General
            new AlfenConfigOption { Key = "SupportedFeatureProfiles", DisplayName = "Supported feature profiles", Category = "General", Type = "string", DefaultValue = "Determined per order", Notes = "Comma-separated list of supported profiles", AllowedValues = new List<string>{ "Core","FirmwareManagement","LocalAuthListManagement","Reservation","SmartCharging","RemoteTrigger"}, Access = "RO" },
            new AlfenConfigOption { Key = "SupportedFeatureProfilesMaxLength", DisplayName = "Supported feature profiles max length", Category = "General", Type = "int", DefaultValue = "Determined per order", Access = "RO" },

            // 2. Transaction / timeouts
            new AlfenConfigOption { Key = "EVConnectionTimeOut", DisplayName = "EV connection timeout", Category = "Transactions", Type = "int", Unit = "s", Min = 0, Max = 32767, DefaultValue = "120", Notes = "Time between auth and plug-in", Access = "RW" },

            // 3. Smart Charging / Load balancing
            new AlfenConfigOption { Key = "ChargeProfileMaxStackLevel", DisplayName = "Charge profile max stack level", Category = "SmartCharging", Type = "int", DefaultValue = "5", Notes = "Max stack level", Access = "RO" },
            new AlfenConfigOption { Key = "ChargingScheduleAllowedChargingRateUnit", DisplayName = "Allowed charging rate unit", Category = "SmartCharging", Type = "string", DefaultValue = "Current", AllowedValues = new List<string>{ "Current" }, Access = "RO" },
            new AlfenConfigOption { Key = "ChargingScheduleMaxPeriods", DisplayName = "Charging schedule max periods", Category = "SmartCharging", Type = "int", DefaultValue = "10", Access = "RO" },
            new AlfenConfigOption { Key = "MaxChargingProfilesInstalled", DisplayName = "Max charging profiles installed", Category = "SmartCharging", Type = "int", DefaultValue = "20", Access = "RO" },
            new AlfenConfigOption { Key = "ConnectorSwitch3to1PhaseSupported", DisplayName = "3-to-1 phase switching supported", Category = "SmartCharging", Type = "bool", DefaultValue = "false", Access = "RO" },
            new AlfenConfigOption { Key = "ConnectorPhaseRotation", DisplayName = "Connector phase rotation", Category = "SmartCharging", Type = "string", DefaultValue = "0.RST", Notes = "Comma list e.g. 0.RST,1.RST,2.RTS", Access = "RW" },
            new AlfenConfigOption { Key = "ConnectorPhaseRotationMaxLength", DisplayName = "Phase rotation max length", Category = "SmartCharging", Type = "int", DefaultValue = "0", Access = "RO" },

            // 4. Authorization / Local Auth / Transactions
            new AlfenConfigOption { Key = "UnlockConnectorOnEVSideDisconnect", DisplayName = "Unlock on EV side disconnect", Category = "Authorization", Type = "bool", DefaultValue = "false", Access = "RW" },
            new AlfenConfigOption { Key = "AllowOfflineTxForUnknownId", DisplayName = "Allow offline tx for unknown id", Category = "Authorization", Type = "bool", DefaultValue = "true", Access = "RW" },
            new AlfenConfigOption { Key = "AuthorizationCacheEnabled", DisplayName = "Authorization cache enabled", Category = "Authorization", Type = "bool", DefaultValue = "true", Access = "RW" },
            new AlfenConfigOption { Key = "AuthorizeRemoteTxRequests", DisplayName = "Authorize remote start requests", Category = "Authorization", Type = "bool", DefaultValue = "false", Access = "RW" },
            new AlfenConfigOption { Key = "LocalAuthListEnabled", DisplayName = "Local auth list enabled", Category = "Authorization", Type = "bool", DefaultValue = "true", Access = "RW" },
            new AlfenConfigOption { Key = "LocalAuthListMaxLength", DisplayName = "Local auth list max length", Category = "Authorization", Type = "int", DefaultValue = "782", Access = "RO" },
            new AlfenConfigOption { Key = "LocalAuthorizeOffline", DisplayName = "Local authorize offline", Category = "Authorization", Type = "bool", DefaultValue = "true", Access = "RW" },
            new AlfenConfigOption { Key = "LocalPreAuthorize", DisplayName = "Local pre-authorize", Category = "Authorization", Type = "bool", DefaultValue = "false", Access = "RW" },
            new AlfenConfigOption { Key = "MaxEnergyOnInvalidId", DisplayName = "Max energy on invalid id", Category = "Authorization", Type = "int", Unit = "Wh", Min = 0, Max = 4294967295L, DefaultValue = "0", Access = "RW" },
            new AlfenConfigOption { Key = "SendLocalListMaxLength", DisplayName = "Send local list max length", Category = "Authorization", Type = "int", DefaultValue = "22", Access = "RO" },
            new AlfenConfigOption { Key = "StopTransactionOnEVSideDisconnect", DisplayName = "Stop tx on EV side disconnect", Category = "Authorization", Type = "bool", DefaultValue = "false", Access = "RW" },
            new AlfenConfigOption { Key = "StopTransactionOnInvalidId", DisplayName = "Stop tx on invalid id", Category = "Authorization", Type = "bool", DefaultValue = "false", Access = "RW" },
            new AlfenConfigOption { Key = "MasterPassGroupId", DisplayName = "Master pass group id", Category = "Authorization", Type = "string", DefaultValue = "", Access = "RW" },

            // 5. Connectivity / Meter values
            new AlfenConfigOption { Key = "TransactionMessageAttempts", DisplayName = "Transaction message attempts", Category = "Connectivity", Type = "int", Min = 0, Max = 65535, DefaultValue = "0", Access = "RW" },
            new AlfenConfigOption { Key = "TransactionMessageRetryInterval", DisplayName = "Transaction message retry interval", Category = "Connectivity", Type = "int", Min = 0, Max = 2147483647, Unit = "s", DefaultValue = "60", Access = "RW" },
            new AlfenConfigOption { Key = "WebSocketPingInterval", DisplayName = "WebSocket ping interval", Category = "Connectivity", Type = "int", Min = 0, Max = 2147483647, Unit = "s", DefaultValue = "120", Access = "RW" },
            new AlfenConfigOption { Key = "HeartbeatInterval", DisplayName = "Heartbeat interval", Category = "Connectivity", Type = "int", Min = 30, Max = 2147483647, Unit = "s", DefaultValue = "900", Access = "RW" },
            new AlfenConfigOption { Key = "ClockAlignedDataInterval", DisplayName = "Clock aligned data interval", Category = "Metering", Type = "int", Min = 0, Max = 999999, Unit = "s", DefaultValue = "0", Notes = "0 disables clock-aligned data", Access = "RW" },
            new AlfenConfigOption { Key = "MeterValuesAlignedData", DisplayName = "Clock-aligned measurands", Category = "Metering", Type = "list", AllowedValues = new List<string>{ "Energy.Active.Import.Register","Power.Active.Import","Current.Import","Voltage","Temperature","Current.Offered","Frequency","Power.Factor" }, DefaultValue = "None", Access = "RW" },
            new AlfenConfigOption { Key = "MeterValuesAlignedDataMaxLength", DisplayName = "Aligned data max length", Category = "Metering", Type = "int", DefaultValue = "9", Access = "RO" },
            new AlfenConfigOption { Key = "MeterValueSampleInterval", DisplayName = "Sampled meter interval", Category = "Metering", Type = "int", Min = 0, Max = 2147483647, Unit = "s", DefaultValue = "900", Access = "RW" },
            new AlfenConfigOption { Key = "MeterValuesSampledData", DisplayName = "Sampled measurands", Category = "Metering", Type = "list", AllowedValues = new List<string>{ "Energy.Active.Import.Register","Power.Active.Import","Current.Import","Voltage","Temperature","Current.Offered","Frequency","Power.Factor" }, DefaultValue = "Energy.Active.Import.Register", Access = "RW" },
            new AlfenConfigOption { Key = "MeterValuesSampledDataMaxLength", DisplayName = "Sampled data max length", Category = "Metering", Type = "int", DefaultValue = "9", Access = "RO" },
            new AlfenConfigOption { Key = "StopTxnAlignedData", DisplayName = "StopTx aligned data", Category = "Metering", Type = "list", DefaultValue = "", Access = "RW" },
            new AlfenConfigOption { Key = "StopTxnAlignedDataMaxLength", DisplayName = "StopTx aligned max length", Category = "Metering", Type = "int", DefaultValue = "0", Access = "RO" },
            new AlfenConfigOption { Key = "StopTxnSampledData", DisplayName = "StopTx sampled data", Category = "Metering", Type = "list", DefaultValue = "", Access = "RW" },
            new AlfenConfigOption { Key = "StopTxnSampledDataMaxLength", DisplayName = "StopTx sampled max length", Category = "Metering", Type = "int", DefaultValue = "0", Access = "RO" },
            new AlfenConfigOption { Key = "SupportedFileTransferProtocols", DisplayName = "Supported file transfer protocols", Category = "Connectivity", Type = "list", AllowedValues = new List<string>{ "FTP" }, DefaultValue = "FTP", Access = "RO" },
            new AlfenConfigOption { Key = "OCPPStackVersion", DisplayName = "OCPP stack version", Category = "Connectivity", Type = "string", DefaultValue = "4.7.0", Access = "RO" },
            new AlfenConfigOption { Key = "RegisterMeterValuesIncludePhases", DisplayName = "Include phases in meter values", Category = "Metering", Type = "bool", DefaultValue = "false", Access = "RW" },
            new AlfenConfigOption { Key = "MinimumStatusDuration", DisplayName = "Minimum status duration", Category = "Connectivity", Type = "int", Min = 0, Max = 65535, Unit = "s", DefaultValue = "0", Access = "RW" },

            // 6. UI
            new AlfenConfigOption { Key = "LightIntensity", DisplayName = "LED/display brightness", Category = "Interface", Type = "int", Min = 0, Max = 100, Unit = "%", DefaultValue = "100", Access = "RW" },

            // 7. Eichrecht / Signed metering
            new AlfenConfigOption { Key = "SignedDataEnabled", DisplayName = "Signed data enabled (aligned)", Category = "Eichrecht", Type = "bool", DefaultValue = "false", Access = "RO" },
            new AlfenConfigOption { Key = "SignReadings", DisplayName = "Signed data enabled (sampled)", Category = "Eichrecht", Type = "bool", DefaultValue = "false", Access = "RO" },

            // 8. Security
            new AlfenConfigOption { Key = "CertificateSignedMaxChain", DisplayName = "Certificate signed max chain", Category = "Security", Type = "int", DefaultValue = "2", Access = "RO" },
            new AlfenConfigOption { Key = "CertificateStoreMaxLength", DisplayName = "Certificate store max length", Category = "Security", Type = "int", DefaultValue = "4", Access = "RO" },
            new AlfenConfigOption { Key = "CertificateSignedMaxChainSize", DisplayName = "Certificate signed max chain size", Category = "Security", Type = "int", Unit = "bytes", DefaultValue = "7000", Access = "RO" },
            new AlfenConfigOption { Key = "CpoName", DisplayName = "CPO/organization name", Category = "Security", Type = "string", DefaultValue = "", Access = "RW" },

            // 9. Other
            new AlfenConfigOption { Key = "GetConfigurationMaxKeys", DisplayName = "GetConfiguration max keys", Category = "Other", Type = "int", DefaultValue = "35", Access = "RO" },
            new AlfenConfigOption { Key = "NumberOfConnectors", DisplayName = "Number of connectors", Category = "Other", Type = "int", DefaultValue = "Determined per order", Access = "RO" },
            new AlfenConfigOption { Key = "ReserveConnectorZeroSupported", DisplayName = "Reserve connector zero supported", Category = "Other", Type = "bool", DefaultValue = "true", Access = "RO" },
            new AlfenConfigOption { Key = "ResetRetries", DisplayName = "Reset retries", Category = "Other", Type = "int", DefaultValue = "0", Access = "RO" },
        };
    }
}
