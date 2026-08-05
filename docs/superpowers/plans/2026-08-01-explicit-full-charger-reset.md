# Explicit Full Charger Reset Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the authenticated management Reset action request an explicit full charger reset while preserving legacy no-mode reset behavior for every supported OCPP protocol.

**Architecture:** Parse the optional reset route segment once into a private protocol-neutral intent in `OCPPMiddleware`. Map that intent to the checked protocol enums at dispatch time, pass the resolved enum into the existing send/wait methods, and reject malformed explicit modes before any WebSocket send. The management action requests the existing explicit `Hard` route value so its click maps to OCPP 1.6 `Hard` and OCPP 2.x `Immediate`.

**Tech Stack:** .NET 8, ASP.NET Core MVC, Newtonsoft.Json, xUnit, in-process `HttpListener`, fake WebSocket transport.

## Global Constraints

- Preserve API-key authentication, administrator authorization, live charge-point lookup, response handling, and timeout behavior.
- Preserve no-mode compatibility: OCPP 1.6 `Soft`; OCPP 2.0.1 and 2.1 `OnIdle`.
- Map the explicit full-reset intent to OCPP 1.6 `Hard` and OCPP 2.0.1/2.1 `Immediate`.
- Reject malformed explicit reset modes without sending a charger command.
- Keep logs and committed documentation public-safe and free of client, location, deployment, credential, or private queue context.
- Do not change payments, invoices, transactions, database schema, configuration, credentials, deployment, or unrelated commands.

---

### Task 1: Make the management Reset click explicit

**Files:**

- Modify: `OCPP.Core.Server.Tests/ManagementControllerBehaviorTests.cs`
- Modify: `OCPP.Core.Management/Controllers/ApiController.Reset.cs`

**Interfaces:**

- Consumes: existing `ApiController.Reset(string Id)` action and `ServerApiUrl`/`ApiKey` configuration.
- Produces: an authenticated `GET /Reset/{escapedChargePointId}/Hard` request with unchanged localized result handling.

- [ ] **Step 1: Write the failing management-boundary test**

Add a real in-process HTTP-server test that seeds `CP-RESET`, invokes the administrator action, and asserts the exact request path and API key:

```csharp
[Fact]
public async Task Reset_RequestsExplicitHardMode()
{
    // Seed CP-RESET, then have TestHttpServer assert:
    Assert.Equal("GET", request.Method);
    Assert.Equal("/Reset/CP-RESET/Hard", request.Path);
    Assert.Equal("test-api-key", request.Headers["x-api-key"]);

    // Return {"status":"Accepted"} and assert the localized action result.
}
```

The production change this test catches is removal of the explicit `/Hard` segment from the management URI.

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --configuration Release --no-restore \
  --filter FullyQualifiedName~ManagementControllerBehaviorTests.Reset_RequestsExplicitHardMode
```

Expected: FAIL because the request path is `/Reset/CP-RESET`.

- [ ] **Step 3: Append the explicit full-reset mode**

Change only the management URI construction:

```csharp
uri = new Uri(uri, $"Reset/{Uri.EscapeDataString(Id)}/Hard");
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Step 2 command again. Expected: PASS for all parameterized result rows with no warnings/errors from the action.

- [ ] **Step 5: Re-run existing management-controller behavior tests**

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --configuration Release --no-restore \
  --filter FullyQualifiedName~ManagementControllerBehaviorTests
```

Expected: all management-controller behavior tests pass.

### Task 2: Validate and map the reset intent across protocols

**Files:**

- Modify: `OCPP.Core.Server.Tests/OCPPMiddlewareTests.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.OCPP16.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.OCPP20.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.OCPP21.cs`

**Interfaces:**

- Consumes: optional fifth route segment from `GET /API/Reset/{chargePointId}/{mode?}`.
- Produces: private `ResetIntent` values `CompatibilityDefault`, `Full`, and `Soft`; resolved protocol enums passed to the existing reset send/wait methods.

- [ ] **Step 1: Add failing real-boundary tests**

Extend `OCPPMiddlewareTests` with literal payload assertions for:

```csharp
// OCPP 1.6
/API/Reset/CP-RESET             -> {"type":"Soft"}
/API/Reset/CP-RESET/Soft        -> {"type":"Soft"}
/API/Reset/CP-RESET/Hard        -> {"type":"Hard"}

// OCPP 2.0.1
/API/Reset/CP-RESET             -> {"type":"OnIdle"}
/API/Reset/CP-RESET/Hard        -> {"type":"Immediate"}

// OCPP 2.1
/API/Reset/CP-RESET             -> {"type":"OnIdle"}
/API/Reset/CP-RESET/Hard        -> {"type":"Immediate"}
```

Add a malformed-mode regression:

```csharp
/API/Reset/CP-RESET/RestartNow  -> HTTP 400, {"status":"InvalidResetMode"}, zero WebSocket sends
```

Each expected payload is a hand-derived literal. The production mutations caught are a wrong protocol enum, loss of the no-mode default, accepting an unknown value, or queuing a message after failed validation.

- [ ] **Step 2: Run the new reset tests and verify RED**

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --configuration Release --no-restore \
  --filter FullyQualifiedName~OCPPMiddlewareTests.Invoke_ResetApi
```

Expected failures: OCPP 2.x full reset remains `OnIdle`; invalid mode does not return 400; missing coverage is added before implementation.

- [ ] **Step 3: Add the private intent parser and enum mappings**

In `OCPPMiddleware.cs`, add:

```csharp
private enum ResetIntent
{
    CompatibilityDefault,
    Full,
    Soft
}

private static bool TryParseResetIntent(string requestedMode, out ResetIntent intent)
{
    if (string.IsNullOrWhiteSpace(requestedMode))
    {
        intent = ResetIntent.CompatibilityDefault;
        return true;
    }

    if (string.Equals(requestedMode.Trim(), "Hard", StringComparison.OrdinalIgnoreCase))
    {
        intent = ResetIntent.Full;
        return true;
    }

    if (string.Equals(requestedMode.Trim(), "Soft", StringComparison.OrdinalIgnoreCase))
    {
        intent = ResetIntent.Soft;
        return true;
    }

    intent = default;
    return false;
}
```

Add one resolver per checked protocol enum. `Full` maps to `Hard` or `Immediate`; both other intents map to `Soft` or `OnIdle`.

- [ ] **Step 4: Validate before protocol dispatch and log the effective value**

At the Reset API branch:

1. Parse the optional mode before live-session lookup.
2. On failure, return HTTP 400 with `{"status":"InvalidResetMode"}` and stop.
3. Resolve the protocol enum for the connected session.
4. Emit one structured information log with `{ChargePointId}`, `{Protocol}`, `{RequestedMode}`, and `{EffectiveMode}`.
5. Pass the resolved enum to `Reset16`, `Reset20`, or `Reset21`.

- [ ] **Step 5: Update protocol methods to serialize only resolved enums**

Change the reset method signatures to accept their checked enum types and assign them directly to `ResetRequest.Type`. Keep queueing, WebSocket send, timeout, response status, content type, and response body logic byte-for-byte equivalent outside the enum assignment and structured trace.

- [ ] **Step 6: Run reset tests and verify GREEN**

Run the Step 2 command again. Expected: every reset API test passes.

- [ ] **Step 7: Run controller response tests that cover reset result handling**

Run the reset controller and middleware focused filters, confirming `Accepted`, `Rejected`, `Scheduled`, and timeout behavior remain represented by existing code/tests.

### Task 3: Document and verify the complete change

**Files:**

- Modify: `docs/features.md`
- Modify: `docs/operations.md`
- Verify: all changed production, test, design, and plan files

**Interfaces:**

- Consumes: the implemented route and protocol mapping.
- Produces: collaborator-neutral feature and operator documentation plus a reviewable exact branch head.

- [ ] **Step 1: Update public feature documentation**

Document `GET /API/Reset/{chargePointId}/{mode?}`, accepted `Hard`/`Soft` modes, invalid-mode HTTP 400 behavior, and the compatibility-default mapping.

- [ ] **Step 2: Update public operations documentation**

Add a compact protocol matrix and state that the management action requests `Hard`, which maps to OCPP 2.x `Immediate`; no configuration key or deployment step is added.

- [ ] **Step 3: Run focused tests from a fresh build**

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --configuration Release --no-restore \
  --filter 'FullyQualifiedName~OCPPMiddlewareTests.Invoke_ResetApi|FullyQualifiedName~ManagementControllerBehaviorTests.Reset_RequestsExplicitHardMode'
```

- [ ] **Step 4: Run full Release verification**

```bash
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln --configuration Release --no-restore
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --configuration Release --no-build --no-restore
git diff --check
```

Expected: build exits 0; all server tests pass; whitespace check exits 0.

- [ ] **Step 5: Review scope and public safety**

Inspect `git diff --stat`, the exact diff, and `git status`. Confirm there is no database, payment, invoice, transaction, credential, configuration, deployment, client/location, or unrelated-command change.

- [ ] **Step 6: Commit and publish for independent QA**

Commit the implementation and documentation, push `req/2026-07-31-012-explicit-full-charger-reset`, open one draft PR against `main`, and add `codex` and `codex-automation` only when those labels exist. Do not merge or deploy.
