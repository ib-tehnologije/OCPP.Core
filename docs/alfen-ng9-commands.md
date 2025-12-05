Alfen ACE NG9 – OCPP Configuration Keys

Conventions

For each key:
•	Category / Component – from the Alfen table.
•	OCPP 1.6 key – the name you should use with GetConfiguration / ChangeConfiguration.
•	OCPP 1.5 key – if different.
•	OCPP 2.0.1 variable – when the table maps it.
•	Type – conceptual type; over the wire it’s always a string.
•	Access – RO (read‑only) / RW / WO.
•	Default – as given by Alfen.
•	Notes – shortened, dev‑oriented description.

Remember: in OCPP 1.6 all config values are strings; booleans are "true" / "false", integers/decimals are decimal strings using ".".

⸻

1. General / Core Profiles

1.1 SupportedFeatureProfiles
•	Category / Component: General / none
•	OCPP 1.6 key: SupportedFeatureProfiles
•	Type: string (comma‑separated)
•	Access: RO
•	Default: “Determined per order”
•	Notes: Comma‑separated list of supported feature profiles, e.g.
Core,FirmwareManagement,Reservation,LocalAuthListManagement,RemoteTrigger,SmartCharging.

Valid items: Core, FirmwareManagement, LocalAuthListManagement, Reservation, SmartCharging, RemoteTrigger. ￼

1.2 SupportedFeatureProfilesMaxLength
•	Category / Component: General / none
•	OCPP 1.6 key: SupportedFeatureProfilesMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: “Determined per order”
•	Notes: Maximum number of items allowed in SupportedFeatureProfiles.

⸻

2. Transaction / Connection timeouts

2.1 EVConnectionTimeOut
•	Category / Component: Power settings / TxCtrlr
•	OCPP 1.6 key: EVConnectionTimeOut
•	OCPP 1.5 key: ConnectionTimeOut
•	Type: integer seconds (0–32767)
•	Access: RW
•	Default: 120
•	Notes: Max time between presenting an authorized token and physically connecting the EV before the authorization expires. ￼

⸻

3. Smart Charging / Load Balancing (OCPP‑standard keys)

3.1 ChargeProfileMaxStackLevel
•	Category / Component: Load balancing / SmartChargingCtrlr
•	OCPP 1.6 key: ChargeProfileMaxStackLevel
•	OCPP 2.0.1 variable: ProfileStackLevel
•	Type: integer
•	Access: RO
•	Default: 5
•	Notes: Max stack level for charging profiles. Also effectively the max number of profiles per “charging profile purpose” the station will accept. ￼

3.2 ChargingScheduleAllowedChargingRateUnit
•	Category / Component: Load balancing / none
•	OCPP 1.6 key: ChargingScheduleAllowedChargingRateUnit
•	Type: string
•	Access: RO
•	Default: Current
•	Notes: List of supported ChargingRateUnitType values for ChargingSchedule. For these chargers only Current is supported. (So power‑based schedules are not accepted.) ￼

3.3 ChargingScheduleMaxPeriods
•	Category / Component: Load balancing / SmartChargingCtrlr
•	OCPP 1.6 key: ChargingScheduleMaxPeriods
•	OCPP 2.0.1 variable: PeriodsPerSchedule
•	Type: integer
•	Access: RO
•	Default: 10
•	Notes: Maximum number of schedule periods per ChargingSchedule.

3.4 MaxChargingProfilesInstalled
•	Category / Component: Load balancing / none
•	OCPP 1.6 key: MaxChargingProfilesInstalled
•	Type: string (numeric)
•	Access: RO
•	Default: 20
•	Notes: Maximum number of charging profiles that can be installed overall.

3.5 ConnectorSwitch3to1PhaseSupported
•	Category / Component: Load balancing / SmartChargingCtrlr
•	OCPP 1.6 key: ConnectorSwitch3to1PhaseSupported
•	OCPP 2.0.1 variable: Phases3to1
•	Type: boolean
•	Access: RO
•	Default: false
•	Notes: Indicates if the charger can switch between 3‑phase and 1‑phase during a transaction. ￼

3.6 ConnectorPhaseRotation
•	Category / Component: Load balancing / none
•	OCPP 1.6 key: ConnectorPhaseRotation
•	Type: string (comma‑separated list)
•	Access: RW
•	Default: 0.RST
•	Notes: Phase rotation per connector relative to its energy meter or grid connection.
•	Index 0 may represent the main meter; 1, 2, … are connectors.
•	Values: NotApplicable, Unknown, RST, RTS, SRT, STR, TRS, TSR.
•	Examples: 0.RST,1.RST,2.RTS. ￼

3.7 ConnectorPhaseRotationMaxLength
•	Category / Component: Load balancing / none
•	OCPP 1.6 key: ConnectorPhaseRotationMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 0 (these chargers don’t seem to cap this beyond internal limits)
•	Notes: Maximum number of items allowed in ConnectorPhaseRotation.

⸻

4. Authorization / Local Auth / Transactions

4.1 UnlockConnectorOnEVSideDisconnect
•	Category / Component: Authorization / OCPPCommCtrlr
•	OCPP 1.6 key: UnlockConnectorOnEVSideDisconnect
•	OCPP 2.0.1 variable: UnlockOnEVSideDisconnect
•	Type: boolean
•	Access: RW
•	Default: false
•	Notes: If true, the CS unlocks the cable on the CS side when unplugged at the EV side (subject to DisconnectAction). ￼

4.2 AllowOfflineTxForUnknownId
•	Category / Component: Authorization / AuthCtrlr
•	OCPP 1.6 key: AllowOfflineTxForUnknownId
•	OCPP 2.0.1 variable: OfflineTxForUnknownIdEnabled
•	Type: boolean
•	Access: RW
•	Default: true
•	Notes: If true, unknown identifiers are allowed to start transactions while the CS is offline. When online again, the CSMS will validate/update them.

4.3 AuthorizationCacheEnabled
•	Category / Component: Authorization / AuthCacheCtrlr
•	OCPP 1.6 key: AuthorizationCacheEnabled
•	OCPP 2.0.1 variable: Enabled
•	Type: boolean
•	Access: RW
•	Default: true
•	Notes: Enables/disables use of the Authorization Cache.

4.4 AuthorizeRemoteTxRequests
•	Category / Component: Authorization / AuthCtrlr
•	OCPP 1.6 key: AuthorizeRemoteTxRequests
•	OCPP 2.0.1 variable: AuthorizeRemoteStart
•	Type: boolean
•	Access: RW
•	Default: false
•	Notes: If true, a RemoteStartTransaction must be pre‑authorized; the CS sends Authorize.req before starting.

4.5 LocalAuthListEnabled
•	Category / Component: Authorization / LocalAuthListCtrlr
•	OCPP 1.6 key: LocalAuthListEnabled
•	OCPP 2.0.1 variable: Enabled
•	Type: boolean
•	Access: RW
•	Default: true
•	Notes: Enables/disables the Local Authorization List.

4.6 LocalAuthListMaxLength
•	Category / Component: Authorization / none
•	OCPP 1.6 key: LocalAuthListMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 782
•	Notes: Max number of identifiers that can be stored in the local auth list.

4.7 LocalAuthorizeOffline
•	Category / Component: Authorization / AuthCtrlr
•	OCPP 1.6 key: LocalAuthorizeOffline
•	Type: boolean
•	Access: RW
•	Default: true
•	Notes: If true, when offline the CS will start transactions for locally authorized IDs.

4.8 LocalPreAuthorize
•	Category / Component: Authorization / AuthCtrlr
•	OCPP 1.6 key: LocalPreAuthorize
•	Type: boolean (table lists “String”, but spec says boolean)
•	Access: RW
•	Default: false
•	Notes: If true, when online the CS start transactions immediately for locally authorized IDs; the backend still verifies via StartTransaction.

4.9 MaxEnergyOnInvalidId
•	Category / Component: Authorization / TxCtrlr
•	OCPP 1.6 key: MaxEnergyOnInvalidId
•	Type: integer Wh (0–4 294 967 295)
•	Access: RW
•	Default: 0
•	Notes: Max energy (Wh) that may be delivered once an identifier is marked invalid by the CSMS.

4.10 SendLocalListMaxLength
•	Category / Component: Authorization / none
•	OCPP 1.6 key: SendLocalListMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 22
•	Notes: Max number of IDs that may be sent in a single SendLocalList.req.

4.11 StopTransactionOnEVSideDisconnect
•	Category / Component: Authorization / TxCtrlr
•	OCPP 1.6 key: StopTransactionOnEVSideDisconnect
•	OCPP 2.0.1 variable: StopTxOnEVSideDisconnect
•	Type: boolean
•	Access: RW
•	Default: false
•	Notes: If true, unplugging from the EV side stops the transaction (again combined with DisconnectAction). ￼

4.12 StopTransactionOnInvalidId
•	Category / Component: Authorization / TxCtrlr
•	OCPP 1.6 key: StopTransactionOnInvalidId
•	OCPP 2.0.1 variable: StopTxOnInvalidId
•	Type: boolean
•	Access: RW
•	Default: false
•	Notes: If true, an ongoing transaction is stopped when the IdTag becomes invalid in the CSMS.

4.13 MasterPassGroupId
•	Category / Component: Authorization / AuthCtrlr
•	OCPP 1.6 key: MasterPassGroupId
•	Type: string (UUID‑ish, up to 36 chars)
•	Access: RW
•	Default: empty
•	Notes: IdTokens with this groupId form the “Master Pass” group: they can stop any ongoing transaction but cannot start transactions themselves. For tow trucks / authorities, etc. ￼

⸻

5. Connectivity / Messaging / Meter Values

5.1 TransactionMessageAttempts
•	Category / Component: Connectivity / OCPPCommCtrlr
•	OCPP 1.6 key: TransactionMessageAttempts
•	OCPP 2.0.1 variable: MessageAttempts
•	Type: integer (0–65535)
•	Access: RW
•	Default: 0
•	Notes: Max number of times to retry sending transaction‑related messages if the CSMS doesn’t process them successfully.

5.2 TransactionMessageRetryInterval
•	Category / Component: Connectivity / OCPPCommCtrlr
•	OCPP 1.6 key: TransactionMessageRetryInterval
•	OCPP 2.0.1 variable: MessageAttemptInterval
•	Type: integer seconds (0–2 147 483 647)
•	Access: RW
•	Default: 60
•	Notes: Wait time between retry attempts for transaction messages.

5.3 WebSocketPingInterval
•	Category / Component: Connectivity / OCPPCommCtrlr
•	OCPP 1.6 key: WebSocketPingInterval
•	Type: integer seconds (0–2 147 483 647)
•	Access: RW
•	Default: 120
•	Notes: Interval between client‑initiated ping frames on the WebSocket. 0 disables client‑side ping/pong. ￼

5.4 HeartbeatInterval
•	Category / Component: Connectivity / OCPPCommCtrlr
•	OCPP 1.6 key: HeartbeatInterval (in table: heartbeatInterval)
•	Type: integer seconds (30–2 147 483 647)
•	Access: RW
•	Default: 900
•	Notes: Max time since last successful CSMS message after which a Heartbeat.req is sent. 0 disables periodic heartbeats.

5.5 ClockAlignedDataInterval
•	Category / Component: Connectivity / AlignedDataCtrlr
•	OCPP 1.6 key: ClockAlignedDataInterval
•	OCPP 2.0.1 variable: Interval
•	Type: integer seconds (0–999 999)
•	Access: RW
•	Default: 0 (disabled)
•	Notes: Interval for sending clock‑aligned meter values (aligned to real clock time: 00:15, 00:30,…). If 0, clock‑aligned data is disabled. ￼

5.6 MeterValuesAlignedData
•	Category / Component: Connectivity / AlignedDataCtrlr
•	OCPP 1.6 key: MeterValuesAlignedData
•	OCPP 2.0.1 variable: Measurands
•	Type: member list (comma‑separated)
•	Access: RW
•	Default: None
•	Notes: Which measurands to include in clock‑aligned meter values. Up to 9 items. Same measurands as MeterValuesSampledData.

5.7 MeterValuesAlignedDataMaxLength
•	Category / Component: Connectivity / none
•	OCPP 1.6 key: MeterValuesAlignedDataMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 9
•	Notes: Max number of measurands allowed for MeterValuesAlignedData.

5.8 MeterValueSampleInterval
•	Category / Component: Connectivity / SampledDataCtrlr
•	OCPP 1.6 key: MeterValueSampleInterval
•	OCPP 2.0.1 variable: TxUpdatedInterval
•	Type: integer seconds (0–2 147 483 647)
•	Access: RW
•	Default: 900
•	Notes: Interval for sampled meter values relative to the transaction start. 0 disables sampled meter values. ￼

5.9 MeterValuesSampledData
•	Category / Component: Connectivity / SampledDataCtrlr
•	OCPP 1.6 key: MeterValuesSampledData
•	OCPP 2.0.1 variable: TxUpdatedMeasurands
•	Type: member list (comma‑separated)
•	Access: RW
•	Default: Energy.Active.Import.Register
•	Notes: Measurands to include in each sampled MeterValues message. Up to 9 entries.
Supported base measurands include e.g.:
Energy.Active.Import.Register, Power.Active.Import, Current.Import, Voltage, Temperature, Current.Offered, Frequency, Power.Factor etc., optionally with phase suffixes (L1-N, L2, etc.).

5.10 MeterValuesSampledDataMaxLength
•	Category / Component: Connectivity / none
•	OCPP 1.6 key: MeterValuesSampledDataMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 9
•	Notes: Max number of measurands allowed for MeterValuesSampledData.

5.11 StopTxnAlignedData
•	Category / Component: Connectivity / AlignedDataCtrlr
•	OCPP 1.6 key: StopTxnAlignedData
•	OCPP 2.0.1 variable: TxEndedMeasurands
•	Type: member list
•	Access: RW
•	Default: empty
•	Notes: If set, the CS includes a clock‑aligned MeterValue in StopTransaction.req. Only Energy.Active.Import.Register is actually supported here.

5.12 StopTxnAlignedDataMaxLength
•	Category / Component: Connectivity / none
•	OCPP 1.6 key: StopTxnAlignedDataMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 0 (currently not supported; always 0)
•	Notes: Max items allowed in StopTxnAlignedData (the charger reports 0).

5.13 StopTxnSampledData
•	Category / Component: Connectivity / SampledDataCtrlr
•	OCPP 1.6 key: StopTxnSampledData
•	OCPP 2.0.1 variable: TxEndedMeasurands
•	Type: member list
•	Access: RW
•	Default: empty
•	Notes: If set, the CS includes a sampled MeterValue (relative to transaction start) in StopTransaction.req. Again only Energy.Active.Import.Register is actually supported.

5.14 StopTxnSampledDataMaxLength
•	Category / Component: Connectivity / none
•	OCPP 1.6 key: StopTxnSampledDataMaxLength
•	Type: string (numeric)
•	Access: RO
•	Default: 0 (not supported)

5.15 SupportedFileTransferProtocols
•	Category / Component: Connectivity / OCPPCommCtrlr
•	OCPP 1.6 key: SupportedFileTransferProtocols
•	OCPP 2.0.1 variable: FileTransferProtocols
•	Type: member list
•	Access: RO
•	Default: FTP
•	Notes: List of transfer protocols supported for GetDiagnostics / UpdateFirmware URLs. On these chargers you only get FTP. ￼

5.16 OCPPStackVersion
•	Category / Component: Connectivity / AlfenStation
•	OCPP 1.6 key: OCPPStackVersion
•	Type: string
•	Access: RO
•	Default: 4.7.0 (per this document version)
•	Notes: Internal version of the OCPP stack implementation; mainly for certification / support.

5.17 RegisterMeterValuesIncludePhases
•	Category / Component: Connectivity / SampledDataCtrlr
•	OCPP 1.6 key: RegisterMeterValuesIncludePhases
•	Type: boolean
•	Access: RW
•	Default: false
•	Notes: If true, individual phase measurands are included in MeterValues messages (per‑phase current/voltage/power instead of aggregated). ￼

5.18 MinimumStatusDuration
•	Category / Component: Connectivity / OCPPCommCtrlr
•	OCPP 1.6 key: MinimumStatusDuration
•	Type: integer seconds (0–65535)
•	Access: RW
•	Default: 0
•	Notes: Minimum time a CP/connector status has to stay unchanged before a StatusNotification.req is sent.

⸻

6. UI / Interface

6.1 LightIntensity
•	Category / Component: Interface / none
•	OCPP 1.6 key: LightIntensity
•	Type: integer (0–100)
•	Access: RW
•	Default: 100
•	Notes: Light intensity (%) of LEDs / display.

⸻

7. Eichrecht / Signed Metering

These keys are only relevant if an Eichrecht / IVU adapter is present.

7.1 SignedDataEnabled
•	Category / Component: Eichrecht / AlignedDataCtrlr
•	OCPP 1.5 key: SignedDataEnabled
•	OCPP 2.0.1 variable: SignReadings
•	Type: boolean
•	Access: RO
•	Default: false
•	Notes: If true, signed meter values are sent at start/stop of transactions (clock‑aligned). ￼

7.2 SignReadings (SampledDataCtrlr)
•	Category / Component: Eichrecht / SampledDataCtrlr
•	OCPP 1.6 key: SignReadings
•	Type: boolean
•	Access: RO
•	Default: false
•	Notes: Same functional meaning as above but tied to the sampled‑data controller; again, signed meter values at start/stop when an IVU adapter is present.

⸻

8. Security / Certificates

8.1 CertificateSignedMaxChain
•	Category / Component: Security / none
•	OCPP 1.6 key: CertificateSignedMaxChain
•	Type: integer
•	Access: RO
•	Default: 2
•	Notes: Maximum certificate chain length (number of certs) that can be installed on the device. ￼

8.2 CertificateStoreMaxLength
•	Category / Component: Security / none
•	OCPP 1.6 key: CertificateStoreMaxLength
•	Type: integer
•	Access: RO
•	Default: 4
•	Notes: Max number of Root / CA certificates that can be stored.

8.3 CertificateSignedMaxChainSize
•	Category / Component: Security / SecurityCtrlr
•	OCPP 1.6 key: CertificateSignedMaxChainSize
•	OCPP 2.0.1 variable: MaxCertificateChainSize
•	Type: integer (bytes)
•	Access: RO
•	Default: 7000
•	Notes: Max size (bytes) of a certificate chain that can be installed via CertificateSigned.

8.4 CpoName
•	Category / Component: Security / SecurityCtrlr
•	OCPP 1.6 key: CpoName
•	OCPP 2.0.1 variable: OrganizationName
•	Type: string (max 50 chars)
•	Access: RW
•	Default: empty
•	Notes: CPO or organization name used in the Charge Point certificate (subject CN/OU, depending on profile).

⸻

9. Other / Misc

9.1 GetConfigurationMaxKeys
•	Category / Component: Other / none
•	OCPP 1.6 key: GetConfigurationMaxKeys
•	Type: integer
•	Access: RO
•	Default: 35
•	Notes: Max number of keys the CS will accept in a single GetConfiguration.req. ￼

9.2 NumberOfConnectors
•	Category / Component: Other / none
•	OCPP 1.6 key: NumberOfConnectors
•	Type: string (numeric)
•	Access: RO
•	Default: “Determined per order” (e.g. 1 for Eve Single, 2 for Eve Double / Twin)
•	Notes: Total number of connectors.

9.3 ReserveConnectorZeroSupported
•	Category / Component: Other / ReservationCtrlr
•	OCPP 1.6 key: ReserveConnectorZeroSupported
•	OCPP 2.0.1 variable: NonEvseSpecific
•	Type: boolean
•	Access: RO
•	Default: true
•	Notes: If true, the CS supports reservations addressing connector 0 (whole station).

9.4 ResetRetries
•	Category / Component: Other / OCPPCommCtrlr
•	OCPP 1.6 key: ResetRetries
•	Type: integer
•	Access: RO
•	Default: 0
•	Notes: Number of times the CS will retry an unsuccessful reset before giving up.