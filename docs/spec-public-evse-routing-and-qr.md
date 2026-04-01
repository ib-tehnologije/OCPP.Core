# Public EVSE Routing and QR Compatibility Spec

Suggested execution order: `2/5`

This spec introduces stable public EVSE routes and QR compatibility without changing the existing `/cp/...` public start flow. It assumes the dedicated `ChargePoint.PublicDisplayCode` field is the long-term public identifier.

## 1. Reconciliation With Current Local WIP

Current local WIP already adds:

- `ChargePoint.PublicDisplayCode`
- admin editing for that field
- public display of that code on map/start pages

Current gaps that still need implementation:

- there is no `/evse/...` route in `OCPP.Core.Management/Startup.cs`
- `PublicController` cannot resolve a charge point by public code
- the QR scanner in `wwwroot/js/public-portal.js` still accepts only `/cp/...` and `/Public/Start?cp=...`
- admin QR/public link previews still target the old `/cp/{ChargePointId}` route only

Harvest the existing `PublicDisplayCode` field work, but treat all routing and scanner behavior as still open.

## 2. Canonical Public Identifier Rules

### 2.1 Source of truth

Use `ChargePoint.PublicDisplayCode` as the canonical public EVSE/station identifier.

Rules:

- Store the base public code only, for example `HR*TTK*052009*01`.
- Do not append connector suffixes into the stored field.
- The connector-specific public code is derived as `{PublicDisplayCode}*{ConnectorId}` only for display and connector-level QR routing.

### 2.2 Legacy fallback

`ChargePoint.Comment` remains a temporary compatibility source only.

Lookup order for `/evse/...`:

1. case-insensitive match on `ChargePoint.PublicDisplayCode`
2. if nothing matched, case-insensitive match on `ChargePoint.Comment`

Display order:

- always display `PublicDisplayCode`
- never display `Comment` publicly once `PublicDisplayCode` exists

Backfill goal:

- after production backfill is complete, legacy `Comment` lookup may remain for a grace period but should no longer be needed for newly issued QR codes

## 3. Route and Controller Behavior

### 3.1 New routes

Add two named routes before the default route in `OCPP.Core.Management/Startup.cs`:

- `public-evse-charge-point`: `evse/{publicDisplayCode}`
- `public-evse-path`: `evse/{publicDisplayCode}/{conn:int}`

These routes must stay ahead of the default route so they do not fall through to `Home`.

### 3.2 Controller action

Add a new action on `PublicController`:

- `GET /evse/{publicDisplayCode}`
- `GET /evse/{publicDisplayCode}/{conn}`

Behavior:

1. Trim the incoming `publicDisplayCode`.
2. Lookup by case-insensitive `PublicDisplayCode`, then fallback to case-insensitive `Comment`.
3. If no charge point is found:
   - return HTTP `404`
   - render a small public-layout page with a friendly not-found message, support contact, and a link back to the map
4. If `conn` is not provided:
   - redirect `302` to the existing named `/cp/{cp}` route
5. If `conn` is provided:
   - if the charge point has connector rows and `conn` does not exist, return the same public 404 page with a connector-specific message
   - if the charge point has no connector rows, allow only connector `1`
   - redirect `302` to the existing named `/cp/{cp}/{conn}` route

Redirect behavior:

- preserve the original query string on redirect
- do not rewrite or normalize the visible `PublicDisplayCode`; only the target `/cp/...` route changes

## 4. QR Scanner Compatibility

Update `buildNavigationTarget` in `wwwroot/js/public-portal.js` so scanned QR codes support all approved patterns:

- `/cp/{cp}`
- `/cp/{cp}/{conn}`
- `/evse/{publicDisplayCode}`
- `/evse/{publicDisplayCode}/{conn}`
- `/Public/Start?cp={cp}`

Accepted inputs:

- relative paths
- absolute `http://` or `https://` URLs

Rejected inputs:

- unsupported path shapes
- non-URL data payloads

Behavior:

- if the scanned string resolves to one of the supported shapes, navigate to that URL
- otherwise show the existing unsupported-QR error

Do not remove support for old `/cp/...` stickers.

## 5. Admin and Backfill Changes

### 5.1 Admin preview and QR link generation

Update the public-link/QR preview in `Views/Home/ChargePointDetail.cshtml`:

- if `PublicDisplayCode` exists, the primary public link should be `/evse/{PublicDisplayCode}`
- if it is empty, keep the existing `/cp/{ChargePointId}` fallback

For multi-connector previews, use:

- `/evse/{PublicDisplayCode}/1`
- `/evse/{PublicDisplayCode}/2`

when a public code exists.

### 5.2 Data migration and backfill

Do not manually generate EF migrations in this repo.

Required follow-up outside the code patch:

1. maintainer migration flow adds/applies the `PublicDisplayCode` column where needed
2. one backfill pass copies `Comment -> PublicDisplayCode` for rows where:
   - `PublicDisplayCode` is empty
   - `Comment` contains the approved public EVSE/station code
3. a manual review list cleans inconsistent Tehnoline chargers, especially Delmar-family records

Do not delete or overwrite `Comment` during the rollout phase.

## 6. Tests and Validation

Automated coverage:

- controller tests for:
  - `/evse/{code}` redirecting to `/cp/{chargePointId}`
  - `/evse/{code}/{conn}` redirecting to `/cp/{chargePointId}/{conn}`
  - case-insensitive public-code lookup
  - fallback to `Comment` when `PublicDisplayCode` is empty
  - connector-not-found returning 404
  - public-code-not-found returning 404
- JS tests for QR scanner path parsing of all approved route shapes

Manual validation:

- scan a new `/evse/...` QR
- scan an old `/cp/...` QR
- scan a legacy absolute `/Public/Start?cp=...` QR
- open the admin detail page and confirm the visible public link/QR preview prefers `/evse/...`

## 7. Out of Scope

This spec does not change:

- the existing `/cp/...` routes
- the payment start POST contracts
- public translation completeness
- the public visual redesign/PWA pass

