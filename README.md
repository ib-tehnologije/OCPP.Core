# OCPP.Core
OCPP.Core is an OCPP ([Open ChargePoint Protocol](https://en.wikipedia.org/wiki/Open_Charge_Point_Protocol)) server written in .NET 8. It includes a management Web-UI for administration of charge points and charge tokens (RFID-Token)

## Status
It currently supports OCPP1.6J, 2.0 and 2.1.

OCPP.Core is currently used with 6 [KEBA P30c/x](https://www.keba.com/de/emobility/products/c-series/c-serie) charge points operating in a load management and OCPP1.6J.
Here's a report of the [first real live experiences](Real_life_Experiences_KEBA.md) from 2021.

<br/>
The OCPP-Server currently handles the following messages:

OCPP V1.6:
* BootNotification
* Heartbeat
* Authorize
* StartTransaction
* StopTransaction
* RemoteStartTransaction
* RemoteStopTransaction
* MeterValues
* StatusNotification
* DataTransfer
* Reset
* UnlockConnector
* SetChargingProfile
* ClearChargingProfile

OCPP V2.0 and V2.1:
* BootNotification
* Heartbeat
* Authorize
* TransactionEvent
* RequestStartTransaction
* RequestStopTransaction
* MeterValues
* StatusNotification
* DataTransfer
* LogStatusNotification
* FirmwareStatusNotification
* ClearedChargingLimit
* NotifyChargingLimit
* NotifyEVChargingSchedule
* Reset
* UnlockConnector
* SetChargingProfile
* ClearChargingProfile

## Management Web-UI
The Web-UI is **localized in English and German**. It has an overview page with all charge stations and their availabilty.

![Overview](images/Overview.png)

Every charge point (or connector!) is displayed as a tile with status information. If a car is charging the tile is red and shows the duration. If the charge station sends data about the current power and state of charge (SoC) itis displayed in the footer of the tile. Our KEBA devices e.g. are only sending the main meter value :-(

![Charging details](images/ChargingDetails.png)

If you click on a charge point/connector tile you get a list of the latest transactions and can download them as CSV.

![Overview](images/Transactions.png)

The Web-UI has two different roles. A normal user can see the charge points and transactions (screenshots above). An Administrator can also create and edit the charge stations, charge tags and connectors.

### Multi connector behavior
At first, I didn't pay much attention to multiple connectors. Charge points with multiple connectors are not very common. But then we got the charge points in our home installed and it turned out that our devices (with load management) operate as a single charge point with a connector for each charge point (see [here](Real_life_Experiences_KEBA.md)).

I added multi connector support with V1.1. When the server receives an OCPP message with a connector number (status & transaction messages) the OCPP server dynamically builds a list of connectors in the database (table "ConnectorStatus"). Admin users can see this list and can optionally configure custom names.   

This results in different scenarios for displaying charge points:
* A charge point **without** known connectors
		=> display charge point with its name
* A charge point **with one known connector** without a specific name
 		=> display one charge point with the charge point name
 * A charge point **with 2+ connectors** without specific names
 		=> display each connector with a default name "*charge point name:connector no.*"

If a connector has a name specified this name overrides the charge point name or default scheme (see above). This allows you to define custom names for every connector (like left / right).

## System Requirements
OCPP.Core is written in .NET 8 and therefore runs on different plattforms. I also installed it in Azure for testing purposes.
The storage is based on the EntityFramework-Core and supports different databases. The project contains script for SQL-Server (SQL-Express) and SQLite.

Referenced Packages:
* Microsoft.EntityFrameworkCore
* Microsoft.EntityFrameworkCore.Sqlite
* Microsoft.EntityFrameworkCore.SqlServer
* Newtonsoft.Json
* Karambolo.Extensions.Logging.File


## Build, Configuration and Installation
The steps to build, configure and install OCPP.Core are described **[here](Installation.md)** in Detail.
<br/>


## Testing it...

#### Web-UI
Open the configured URL in a browser. The debug URL in the project is "http://localhost:8082/".
You should see the login screen. Enter the configured admin account.

![Login](images/Login.png)


Open the "Administration" menu and create a chargepoint with ID "station42" and a charge tag.

Administration menu:

![Menu](images/Menu.png)

Create new chargepoint:

![NewChargePoint](images/NewChargePoint.png)

Optionally, you can add authentication data for this chargepoint: Username/Passwort for basic authentication and/or a certificate thumbprint for a client certificate.
When you're editing a chargepoint you can send restart or unlock commands to the chargepoint here.

Create new charge tag:

![NewChargeTag](images/NewChargeTag.png)


The Web-UI is localized in English and German language

#### OCPP-Server
An easy way to test the OCPP-Server are simulators:
* [OCPP1.6 Simple Chargebox Simulator](https://github.com/victormunoz/OCPP-1.6-Chargebox-Simulator)
* [OCPP-2.0-CP-Simulator](https://github.com/JavaIsJavaScript/OCPP-2.0-CP-Simulator)

Attention: Both simulators have minor and major bugs in certain actions. That's why I modified them both and included copies in this project.
There also is an extended version of the 1.6 simulator with two connectors:
* [OCPP1.6 Multi-Connector Simulator](Simulators/simple%20simulator1.6_multi_connector.html)

Open one of the simulators in the browser and enter "ws://localhost:8081/OCPP/station42" as the central station URL.
"station42" is the ID of the chargepoint you created in the previous step.

Enter the charge tag ID you entered before as "Tag".

Click "Connect"

Now the simulator hopefully  show a success message at the bottom.
When you start a transaction and refresh the Web-UI you should see the corresponding status in the charge point tiles.

## Container builds
The repository includes separate Dockerfiles for the WebSocket server and the management UI so you can publish them as independent services in Kubernetes. Both images default to running in `Production` and listen on the ports used in local development, but you can override the port binding and environment with runtime environment variables.

### OCPP.Core.Server
```
docker build -f OCPP.Core.Server/Dockerfile -t ocpp-core-server .
docker run -p 8081:8081 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8081 \
  -v $(pwd)/OCPP.Core.Server/appsettings.Production.json:/app/appsettings.Production.json:ro \
  ocpp-core-server
```

### OCPP.Core.Management
```
docker build -f OCPP.Core.Management/Dockerfile -t ocpp-core-management .
docker run -p 8082:8082 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8082 \
  -v $(pwd)/OCPP.Core.Management/appsettings.Production.json:/app/appsettings.Production.json:ro \
  ocpp-core-management
```

Supply your own certificates or secrets at runtime (for example by mounting Kestrel certificate files or injecting Azure credentials as environment variables) so sensitive values do not get baked into the image. The build stage honors the `BUILD_CONFIGURATION` argument if you need to publish a different configuration.

## Stripe Payments
OCPP.Core can optionally reserve charging costs via Stripe Checkout before a remote start is sent. Configure the `Stripe` section in `OCPP.Core.Server/appsettings.json` with your API key, webhook secret, currency, and an HTTPS return URL to the management site (for example `http://localhost:8082`). Pricing now lives on each charge point (price per kWh, maximum kWh per session, and optional connector usage fees); the hold amount uses those values, and the server captures the actual energy/usage at stop, releasing the remainder automatically.
