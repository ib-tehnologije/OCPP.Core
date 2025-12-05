# OCPP.Core Server API
Most messages are initiated by the chargers. But some messages are initiated by the OCPP backend.
OCPP.Core currently supports:
* Reset
* UnlockConnector
* SetChargingProfile (not verified)
* ClearChargingProfile (not verified)

The OCPP.Core.Server offers an API for using these messages. Additionally the server supports a status request for the online status of all connected chargers.
The management UI uses this API as well.

## API format

The REST-API uses the following format:

    /API/<command>[/chargepointId[/connectorId[/parameter]]]

For authentication/authorization purposes the API uses an API-key (like a password). This key must be send as an http header "X-API-Key" - see [here](https://swagger.io/docs/specification/authentication/api-keys/).
The allowed key is configured in the appsettings.json.


## Functions

### Status
Request the status of all connected chargers and connectors

	/API/Status

The answer should be (example):
    [
        {
            "id": "station42",
            "name": "myWallbox",
            "protocol": "ocpp1.6",
            "OnlineConnectors": {
                "1": {
                    "Status": 1,
                    "ChargeRateKW": null,
                    "MeterKWH": null,
                    "SoC": null
                }
            }
        }
    ]

### Reset
Initiates a reset/reboot of the charger.

	/API/Reset/station42

The answer should be:
{"status"="Accepted"} or {"status"="Rejected"}
or with OCPP 2.x {"status"="Scheduled"}


### UnlockConnector
Send an unlock request for a certain connector

	/API/UnlockConnector/station42/1

The answer should be:
{"status"="Unlocked"} or {"status"="UnlockFailed"}
or  
OCPP1.6 {"status"="NotSupported"}
OCPP2.x {"status"="OngoingAuthorizedTransaction"} or {"status"="UnknownConnector"}


### SetChargingProfile
Sets a charging limit (power) for a certain connector. OCPP.Core does not support schedules. It sets the specified limit as a simple daily 24h schedule (=constant limit).

	/API/SetChargingLimit/station42/1/2000W 
or

	/API/SetChargingLimit/station42/1/16A

The answer should be:
{"status"="Accepted"} or {"status"="Rejected"} or 
OCPP1.6 {"status"="NotSupported"}

Comment:
Our Keba chargers are rejecting limits for specific connectors. But they accept connectorId=0 as the setting for all connectors.


### ClearChargingProfile
Clears the charging limit (power) for a certain connector.

	/API/ClearChargingLimit/station42/1"

The answer should be:
 {"status"="Accepted"} or {"status"="Unknown"}

### GetConfiguration (OCPP 1.6)
Returns configuration keys from the charger. When no key is supplied the charger returns all keys it supports (respecting its internal limit).

	/API/GetConfiguration/station42
	/API/GetConfiguration/station42/EVConnectionTimeOut

The answer is the raw OCPP payload, e.g.:

```
{
  "configurationKey": [
    {"key":"EVConnectionTimeOut","readonly":false,"value":"120"}
  ],
  "unknownKey": []
}
```

### ChangeConfiguration (OCPP 1.6)
Changes a single configuration key on the charger.

	/API/ChangeConfiguration/station42/EVConnectionTimeOut/90

The answer should be:
{"status"="Accepted"} or {"status"="Rejected"} or {"status"="RebootRequired"} or {"status"="NotSupported"}



### RemoteStartTransaction
Request the charger to (remotely) start a transaction (simply explained: virtually presenting a specific charge tag to the charger)

	/API/RemoteStartTransaction/station42/1/tag1234

The answer should be:
{"status"="Accepted"} or {"status"="Rejected"}


### RemoteStopTransaction
Request the charger to end a specific transaction.

	/API/RemoteStopTransaction/station42/1

The answer should be:
{"status"="Accepted"} or {"status"="Rejected"}
The server checks the last transaction for the specified connector and return the http code 424 (FailedDependency) when no open transaction was found.

### Payments (Stripe)
When Stripe support is enabled, charging sessions must reserve funds before a remote start is issued.

#### Create payment reservation
Create a Stripe Checkout Session for a charge point.

	POST /API/Payments/Create

Body (JSON):
```
{
  "chargePointId": "station42",
  "connectorId": 1,
  "chargeTagId": "tag1234"
}
```

Response (HTTP 200):
```
{
  "status": "Redirect",
  "checkoutUrl": "https://checkout.stripe.com/...",
  "reservationId": "GUID",
  "currency": "eur",
  "maxAmountCents": 3920,
  "maxEnergyKwh": 80
}
```
Open `checkoutUrl` in the browser to collect payment authorisation.

#### Confirm payment
Called after Stripe redirects back with `session_id`.

	POST /API/Payments/Confirm

Body:
```
{
  "reservationId": "GUID",
  "checkoutSessionId": "cs_test_..."
}
```

Response mirrors the remote-start outcome (e.g. `{"status":"Accepted"}`).

#### Cancel reservation

	POST /API/Payments/Cancel

Body:
```
{
  "reservationId": "GUID",
  "reason": "checkout_cancelled"
}
```

If the reservation was authorised it will be released.

#### Webhook endpoint
Stripe webhooks should target:

	POST /API/Payments/Webhook

Set `Stripe:WebhookSecret` so signatures can be verified.


### In general
These commands means that the server send a request to the charger and the charger needs to answer in a reasonable period
of time. The server can not wait indefinitely and the OCPP server waits for 60 seconds.
After that the API caller will geht the response {"status"="Timeout"}.
