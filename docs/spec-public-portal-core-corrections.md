# Public Portal Core Corrections Spec

Suggested execution order: `1/5`

This spec covers the highest-priority public portal fixes that affect charger correctness, mobile usability, and pricing clarity. It is intentionally limited to the current public flow and does not include the later visual redesign/PWA pass or the standalone email redesign.

## 1. Reconciliation With Current Local WIP

There is already uncommitted local work in these areas:

- `OCPP.Core.Database/ChargePoint.cs`
- `OCPP.Core.Management/Controllers/PublicController.cs`
- `OCPP.Core.Management/Views/Public/Start.cshtml`
- `OCPP.Core.Management/Views/Public/Map.cshtml`
- `OCPP.Core.Management/wwwroot/css/public-portal.css`
- `OCPP.Core.Management/wwwroot/js/public-portal.js`
- `OCPP.Core.Server.Tests/PublicControllerTests.cs`

Treat that work as candidate implementation, not as the source of truth. Reconcile it this way:

- Keep the current `PublicDisplayCode` scaffolding, admin edit field, map/start rendering hooks, auto-geolocation pattern, and same-page connector selector pattern.
- Revise the current public subtitle handling. The WIP currently surfaces `ChargePoint.LocationDescription`; the public subtitle must come from `ChargePoint.Description`.
- Revise the map aggregate status logic. The WIP still lets station cards end up as `Occupied` too early and still leaks `Unknown`.
- Revise the idle-fee text. The WIP currently shows only grace plus billable cap and does not surface the configured excluded window.
- Keep the compact-header CSS direction, but complete it across `Public/Map`, `Public/Start`, and `Payments/PublicStatus` and fix any remaining horizontal overflow at narrow mobile widths.

## 2. Scope and Decisions

### 2.1 Public subtitle source

Use these fields consistently:

- `ChargePoint.Name`: public station title.
- `ChargePoint.Description`: public subtitle shown directly under the title on map cards, map popups, and the start page header.
- `ChargePoint.LocationDescription`: internal/admin-only in this phase. Do not show it publicly by default.

Implementation detail:

- Add a public-facing subtitle field to the public view models. Do not overload `LocationDescription` with public meaning.
- If `Description` is empty, omit the subtitle block entirely.

### 2.2 Public connector normalization and station aggregate status

The public portal must expose a normalized user-facing connector state, not raw OCPP or mixed persistence state.

Per connector, derive `NormalizedStatusCode` in this order:

1. If the connector has an open transaction or an active payment reservation lock, status is `occupied`.
2. Else if the latest connector status is `Available` or `Preparing`, status is `available`.
3. Else if the latest connector status is `Faulted`, `Unavailable`, empty, or `Unknown`, status is `offline`.
4. Else any other live non-startable state (`Charging`, `Occupied`, `Reserved`, `SuspendedEV`, `SuspendedEVSE`, `Finishing`) is `occupied`.

Do not show raw `Unknown` publicly.

Per station, aggregate from normalized connector states:

- If at least one connector is `available`, station aggregate status is `available`.
- Else if at least one connector is `occupied`, station aggregate status is `occupied`.
- Else station aggregate status is `offline`.

Required counts on `PublicMapChargePoint`:

- `ConnectorCount`
- `AvailableConnectorCount`
- `OccupiedConnectorCount`
- `OfflineConnectorCount`
- `AggregateStatusCode`

Required public summary copy rules:

- If `AvailableConnectorCount > 0`, show `{available}/{total} available`.
- Else if `OccupiedConnectorCount > 0`, show `0/{total} available`.
- Else show `Offline`.

This rule fixes the client-reported problem where `1 of 2 free` still rendered as `Occupied`.

### 2.3 Start page connector selection

Keep the current route structure:

- `/cp/{cp}`
- `/cp/{cp}/{conn}`

For the normal public start flow:

- Clicking a connector must update the selected connector in place with no full page reload and no jump to the top.
- The hidden `ConnectorId` field, selected connector label, selected public code, badge class, badge text, and availability message must all update from the clicked connector option.
- The URL must update with `history.replaceState`.
- Modifier-click behavior must remain native.

For recovery or error states:

- Keep server navigation. Do not attempt to locally swap connector-specific recovery or error payloads in this phase.

### 2.4 Mobile header and narrow-screen overflow

Apply compact public shell behavior to every page using `_PublicPortalLayout`:

- Hide the tagline below `640px`.
- Keep help, phone, and QR actions in a single horizontally scrollable row.
- Reduce top padding, nav height, and language bar height.
- Prevent horizontal overflow of action buttons, status pills, and map cards at `320px`, `360px`, `390px`, and `430px` widths.

Required layout fixes:

- `.charger-card .cc-top` must allow wrapping instead of forcing right-edge clipping.
- `.cc-status` and connector status pills must shrink within the card width.
- Primary CTA buttons on map cards must stay inside the card width.
- No public page may require horizontal scrolling on common mobile widths.

### 2.5 Auto geolocation

On `Public/Map`:

- Attempt geolocation automatically after map initialization when `window.isSecureContext` is true and `navigator.geolocation` exists.
- If the Permissions API reports `denied`, skip the automatic prompt entirely.
- If geolocation succeeds, center the map on the user and render the user marker.
- If geolocation fails or is unavailable, leave the map usable and keep the manual GPS button.
- Manual GPS must continue to surface errors only in console or non-blocking UI; it must not break the map.

### 2.6 Idle-fee disclosure

The public portal must explain idle-fee billing using both the charge point settings and the public portal excluded-window settings.

Data additions:

- Add `IdleFeeExcludedWindowEnabled` and `IdleFeeExcludedWindow` to the public start and public map view models.
- Add `TotalIdleCapMinutes = MaxUsageFeeMinutes`.
- Add `BillableIdleCapMinutes = max(MaxUsageFeeMinutes - StartUsageFeeAfterMinutes, 0)`.

Read the excluded window from the same resolved public settings already used by the public portal editor:

- `PublicPortalSettingsResolver`
- DB override first
- config fallback second

Start page idle-fee copy rules:

- First fragment: `Charged from session start` or `Charged after charging ends`.
- Second fragment: `grace {StartUsageFeeAfterMinutes} min`.
- Third fragment, only when enabled: `no idle fee during {IdleFeeExcludedWindow}`.
- Cap fragment:
  - if `TotalIdleCapMinutes == BillableIdleCapMinutes`, show `cap {TotalIdleCapMinutes} min`
  - otherwise show `cap {TotalIdleCapMinutes} min total, {BillableIdleCapMinutes} min billable`

Map card and popup idle-fee copy rules:

- Show the rate plus grace.
- Append `no idle fee {IdleFeeExcludedWindow}` when enabled.
- Do not show the cap on dense map cards unless the layout still remains clean after the visual refresh. In this phase, full cap detail is required on the start page, not on every map card.

## 3. Required Code Changes

### 3.1 Models and controller logic

Update the public models and controller so the view layer does not have to re-derive core state:

- `PublicMapChargePoint`: add public subtitle, normalized aggregate status, and connector counts.
- `PublicStartViewModel`: add public subtitle, normalized selected-connector status code, idle excluded-window values, and both idle caps.
- `PublicStartConnectorOption`: add normalized connector status code.

Update `PublicController`:

- Use `ChargePoint.Description` for the public subtitle.
- Stop binding public subtitle to `LocationDescription`.
- Normalize connector status and aggregate station status using the rules in section 2.2.
- Populate connector counts for map cards.
- Populate idle excluded-window and cap values for start/map.

### 3.2 Views and JS

Update:

- `Views/Public/Map.cshtml`
- `Views/Public/Start.cshtml`
- `Views/Payments/PublicStatus.cshtml`
- `wwwroot/js/public-portal.js`
- `wwwroot/css/public-portal.css`

Specific behavior requirements:

- Map cards and popups must show the public subtitle from `Description`.
- Start page header must show title, optional subtitle, then public code pills.
- Start page connector chooser must keep the current local-switch pattern for the normal flow.
- Map search should continue to search by station name, subtitle, and public code.
- Any remaining raw `Unknown` output in public map/start UI must be replaced with the normalized `offline` path.

## 4. Tests and Manual Validation

Automated coverage:

- `PublicControllerTests` for:
  - one free connector plus one occupied connector -> station aggregate `available`
  - all connectors offline/unknown -> station aggregate `offline`
  - public subtitle comes from `ChargePoint.Description`
  - start model idle-fee wording inputs include excluded window and both cap values
  - connector-switching view model data still supports same-page updates

Manual checks:

- `Public/Map`, `Public/Start`, and `Payments/PublicStatus` at `320px`, `360px`, `390px`, and `430px`.
- Android Chrome geolocation allowed: auto-center happens.
- Android Chrome geolocation denied: map still works and GPS button remains.
- No map card content or buttons clip on the right edge.
- Mixed free/busy two-connector stations show `Available` plus `1/2 available`.
- Offline stations never display `Unknown`.

## 5. Out of Scope

This spec does not include:

- `/evse/...` routing and QR compatibility
- full translation completeness
- full light-theme redesign or PWA
- email template redesign

