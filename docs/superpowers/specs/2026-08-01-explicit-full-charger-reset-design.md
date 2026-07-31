# Explicit Full Charger Reset Design

## Goal

Make the authenticated operator Reset action request a full, immediate charging-station restart without changing the behavior of legacy callers that omit a reset mode.

## Current Behavior and Root Cause

The management Reset action calls `GET /API/Reset/{chargePointId}` without a mode. The server treats the optional fifth path segment as the OCPP 1.6 reset type, so an omitted value serializes as `Soft`. The OCPP 2.0.1 and 2.1 paths ignore the optional segment and always serialize `OnIdle`.

The checked protocol DTOs expose different wire values for the same operator intent:

- OCPP 1.6: `Hard` or `Soft`
- OCPP 2.0.1: `Immediate` or `OnIdle`
- OCPP 2.1: `Immediate`, `OnIdle`, or `ImmediateAndResume`

The operator action therefore needs an explicit common intent at the HTTP boundary and a protocol-specific mapping before serialization.

## Considered Approaches

### Reinterpret the no-mode route as a full reset

This would make the management action work without changing its URI, but it would silently make every legacy no-mode caller disruptive. It is rejected for backward-compatibility and safety reasons.

### Pass the raw route value into every protocol method

This would be a small signature change, but each protocol method would need its own string parsing and validation. That duplicates policy, makes malformed-value handling inconsistent, and risks sending an unintended default. It is rejected.

### Parse a protocol-neutral reset intent once

This is the selected approach. The API boundary recognizes the established explicit values `Hard` and `Soft`, represents them internally as full or graceful intent, and keeps omission as a separate compatibility-default intent. Invalid explicit values fail before any charger message is queued.

## API Contract

The server keeps the existing route shape:

```text
GET /API/Reset/{chargePointId}/{mode?}
```

Accepted mode values are case-insensitive:

- omitted: use the existing compatibility default
- `Hard`: request a full, immediate charging-station reset
- `Soft`: request the existing graceful reset behavior

Any other explicit value returns HTTP 400 with a bounded JSON status and sends no OCPP message. API-key authentication, live-charge-point lookup, and the existing 404 behavior for offline charge points remain unchanged.

The management Reset action appends `/Hard`, so an authenticated administrator click always requests the full-reset intent. Its existing result handling for `Accepted`, `Rejected`, `Scheduled`, `Timeout`, malformed responses, and offline charge points remains unchanged.

## Protocol Mapping

| HTTP intent | OCPP 1.6 | OCPP 2.0.1 | OCPP 2.1 |
| --- | --- | --- | --- |
| omitted compatibility default | `Soft` | `OnIdle` | `OnIdle` |
| `Hard` full reset | `Hard` | `Immediate` | `Immediate` |
| `Soft` graceful reset | `Soft` | `OnIdle` | `OnIdle` |

`ImmediateAndResume` is not selected because the operator action requests a restart, not a transaction-resume policy change.

## Components and Data Flow

1. `ApiController.Reset` builds the server URI with the explicit `Hard` segment after the escaped charge-point ID.
2. `OCPPMiddleware` parses the optional segment into one internal reset intent before protocol dispatch.
3. Invalid explicit values return HTTP 400 before live-session lookup or message queue mutation.
4. The middleware maps the intent to the checked enum for the connected protocol.
5. The protocol-specific reset method serializes and sends that enum using the existing request queue, timeout, response, and controller handling.
6. One structured information log records charge-point ID, connected protocol, requested mode, and effective protocol value without including location, customer, or deployment context.

## Error Handling

- Missing charge-point ID remains HTTP 400.
- Malformed explicit reset mode becomes HTTP 400 with `InvalidResetMode`.
- Offline or unknown live charge points remain HTTP 404.
- Charger `Accepted`, `Rejected`, `Scheduled`, and timeout responses remain unchanged.
- Exceptions retain the existing HTTP 500 server behavior and management-facing localized error behavior.

## Test Strategy

Use strict red-green TDD for each observable boundary:

- the management action requests `/Reset/{escapedId}/Hard` and preserves the API key and accepted-result handling
- OCPP 1.6 full reset serializes `Hard`
- OCPP 1.6 explicit `Soft` and omitted mode serialize `Soft`
- malformed explicit mode returns 400 without sending a WebSocket message
- OCPP 2.0.1 full reset serializes `Immediate`, while omitted mode serializes `OnIdle`
- OCPP 2.1 full reset serializes `Immediate`, while omitted mode serializes `OnIdle`
- the existing reset response handling remains green

Run focused reset and management-controller tests first, followed by the complete server test project, a Release solution build, and `git diff --check`.

## Documentation and Operational Impact

Update the feature and operations documentation with the optional mode route, accepted values, protocol mapping, and compatibility default. The change introduces no database migration, configuration key, credential change, deployment action, or production operation.
