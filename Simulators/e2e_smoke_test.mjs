/*
 * End-to-end smoke tests against a running OCPP.Core instance.
 *
 * What it tests:
 * - Public management pages respond (Map / Start)
 * - Server API auth + /API/Status
 * - OCPP 1.6 websocket flow (Boot, Authorize, Status, RemoteStart via /API, StartTx, MeterValues, RemoteStop via /API, StopTx)
 * - OCPP 2.0.1 websocket flow (Boot, Authorize, Status, RequestStart via /API, TransactionEvent Started/Updated, RequestStop via /API, TransactionEvent Ended)
 * - Payments API returns an expected status (typically "Disabled" when Stripe is not enabled)
 *
 * Requires:
 * - OCPP.Core.Server listening on SERVER_HTTP_BASE (default http://localhost:8081)
 * - OCPP.Core.Management listening on MGMT_HTTP_BASE (default http://localhost:8082)
 *
 * Notes:
 * - This creates and ends test transactions in the configured database.
 * - We intentionally avoid printing API keys to stdout.
 */

const MGMT_HTTP_BASE = process.env.MGMT_HTTP_BASE ?? "http://localhost:8082";
const SERVER_HTTP_BASE = process.env.SERVER_HTTP_BASE ?? "http://localhost:8081";
const SERVER_API_BASE = process.env.SERVER_API_BASE ?? `${SERVER_HTTP_BASE}/API`;
const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? "ws://localhost:8081/OCPP";
const SERVER_API_KEY = process.env.SERVER_API_KEY ?? "36029A5F-B736-4DA9-AE46-D66847C9062C";

const CP16_ID = process.env.CP16_ID ?? "Test1234";
const CP20_ID = process.env.CP20_ID ?? "TestAAA";
const TAG = process.env.TAG ?? "B4A63CDF";

function nowIsoUtc() {
  return new Date().toISOString().replace(/\.\d{3}Z$/, "Z");
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function httpJson(url, { method = "GET", headers = {}, body } = {}) {
  const res = await fetch(url, {
    method,
    headers,
    body,
  });
  const text = await res.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  return { ok: res.ok, status: res.status, text, json, headers: res.headers };
}

async function httpText(url, { method = "GET", headers = {}, body } = {}) {
  const res = await fetch(url, { method, headers, body });
  const text = await res.text();
  return { ok: res.ok, status: res.status, text, headers: res.headers };
}

function newId(prefix = "") {
  // OCPP uniqueId is just a string; use a small random.
  return `${prefix}${Math.random().toString(16).slice(2)}${Math.random().toString(16).slice(2)}`.slice(0, 32);
}

class OcppClient {
  constructor({ url, protocols, label }) {
    this.url = url;
    this.protocols = protocols;
    this.label = label;
    this.ws = null;
    this.pending = new Map(); // uniqueId -> { resolve, reject, timeout }
    this.callHandlers = new Map(); // action -> async (uniqueId, payload) => responsePayload
  }

  onCall(action, handler) {
    this.callHandlers.set(action, handler);
  }

  async connect({ timeoutMs = 8000 } = {}) {
    assert(typeof WebSocket === "function", "Global WebSocket is not available in this Node runtime.");
    this.ws = new WebSocket(this.url, this.protocols);

    const ws = this.ws;
    await new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error(`[${this.label}] connect timeout`)), timeoutMs);
      ws.onopen = () => {
        clearTimeout(t);
        resolve();
      };
      ws.onerror = (e) => {
        clearTimeout(t);
        reject(new Error(`[${this.label}] websocket error`));
      };
    });

    ws.onmessage = (ev) => this.#handleMessage(ev.data);
  }

  close(code = 3001, reason = "") {
    if (!this.ws) return;
    try {
      this.ws.close(code, reason);
    } catch {
      // ignore
    }
  }

  sendRaw(obj) {
    assert(this.ws && this.ws.readyState === 1, `[${this.label}] websocket not open`);
    this.ws.send(JSON.stringify(obj));
  }

  async call(action, payload, { timeoutMs = 15000 } = {}) {
    const id = newId("c");
    const msg = [2, id, action, payload ?? {}];
    const ws = this.ws;
    assert(ws && ws.readyState === 1, `[${this.label}] websocket not open`);

    const p = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`[${this.label}] ${action} timed out`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timeout, action });
    });

    ws.send(JSON.stringify(msg));
    return p;
  }

  async #handleMessage(raw) {
    let msg;
    try {
      msg = JSON.parse(raw);
    } catch {
      throw new Error(`[${this.label}] invalid JSON from server: ${String(raw).slice(0, 200)}`);
    }

    // OCPP framing:
    // [2, uniqueId, action, payload]  => CALL
    // [3, uniqueId, payload]          => CALLRESULT
    // [4, uniqueId, errorCode, errorDescription, errorDetails] => CALLERROR
    const type = msg?.[0];
    const uniqueId = msg?.[1];

    if (type === 3) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.resolve(msg[2]);
      }
      return;
    }

    if (type === 4) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.reject(new Error(`[${this.label}] CALLERROR for ${pending.action}: ${msg[2]} ${msg[3]}`));
      }
      return;
    }

    if (type === 2) {
      const action = msg?.[2];
      const payload = msg?.[3] ?? {};
      const handler = this.callHandlers.get(action);
      if (!handler) {
        // Respond with "NotImplemented" / generic error to avoid hanging server-side API calls.
        this.sendRaw([4, uniqueId, "NotImplemented", `No handler for ${action}`, {}]);
        return;
      }

      try {
        const responsePayload = await handler(uniqueId, payload);
        this.sendRaw([3, uniqueId, responsePayload ?? {}]);
      } catch (e) {
        this.sendRaw([4, uniqueId, "InternalError", String(e?.message ?? e), {}]);
      }
      return;
    }

    throw new Error(`[${this.label}] unexpected OCPP message: ${raw}`);
  }
}

async function smokeHttp() {
  const results = {};

  // Management public pages
  const mapRes = await httpText(`${MGMT_HTTP_BASE}/Public/Map`);
  results.mgmt_map_200 = mapRes.ok;
  assert(mapRes.ok, `GET /Public/Map failed: ${mapRes.status}`);

  const startRes = await httpText(`${MGMT_HTTP_BASE}/Public/Start?cp=${encodeURIComponent(CP16_ID)}&conn=1`);
  results.mgmt_start_200 = startRes.ok;
  assert(startRes.ok, `GET /Public/Start failed: ${startRes.status}`);

  // Server root + authenticated /API/Status
  const rootRes = await httpText(`${SERVER_HTTP_BASE}/`);
  results.server_root_200 = rootRes.ok;
  assert(rootRes.ok, `GET server root failed: ${rootRes.status}`);

  const statusRes = await httpJson(`${SERVER_API_BASE}/Status`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.server_api_status_200 = statusRes.ok;
  assert(statusRes.ok, `GET /API/Status failed: ${statusRes.status}`);
  assert(Array.isArray(statusRes.json), `/API/Status did not return JSON array`);

  // Payments endpoint sanity (may be Disabled if Stripe is not configured)
  const payCreateRes = await httpJson(`${SERVER_API_BASE}/Payments/Create`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-API-Key": SERVER_API_KEY,
    },
    body: JSON.stringify({
      chargePointId: CP16_ID,
      connectorId: 1,
      chargeTagId: TAG,
      origin: "public",
      returnBaseUrl: MGMT_HTTP_BASE,
    }),
  });

  results.server_payments_create_status = payCreateRes.status;
  results.server_payments_create_body_status = payCreateRes.json?.status ?? null;

  return results;
}

async function testPaymentsCreateCancel({ chargePointId, connectorId, chargeTagId }) {
  const results = { chargePointId, connectorId };

  const createRes = await httpJson(`${SERVER_API_BASE}/Payments/Create`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-API-Key": SERVER_API_KEY,
    },
    body: JSON.stringify({
      chargePointId,
      connectorId,
      chargeTagId,
      origin: "public",
      returnBaseUrl: MGMT_HTTP_BASE,
    }),
  });

  results.create_http_status = createRes.status;
  if (createRes.json && typeof createRes.json === "object") {
    // Avoid leaking Stripe Checkout URLs into logs.
    const clone = { ...createRes.json };
    if (typeof clone.checkoutUrl === "string") {
      clone.checkoutUrl = "<redacted>";
    }
    results.create_payload = clone;
  } else {
    results.create_payload = createRes.json ?? null;
  }

  // Non-2xx outcomes are meaningful (busy/offline/disabled); only treat unexpected errors as failures.
  if (!createRes.ok) {
    const status = createRes.json?.status;
    if (createRes.status === 409 && status === "ConnectorBusy") {
      results.skipped = "ConnectorBusy";
      return results;
    }
    if (createRes.status === 404 && status === "ChargerOffline") {
      results.skipped = "ChargerOffline";
      return results;
    }
    if (createRes.status === 400 && status === "Disabled") {
      results.skipped = "Disabled";
      return results;
    }
    throw new Error(`Payments/Create unexpected failure: ${createRes.status} ${createRes.text.slice(0, 200)}`);
  }

  assert(createRes.json && typeof createRes.json.status === "string", "Payments/Create did not return a status string");

  // For paid chargepoints we expect a redirect to Stripe Checkout.
  const status = createRes.json.status;
  results.status = status;

  if (status === "Redirect") {
    results.reservationId = createRes.json.reservationId ?? null;
    results.checkoutUrl_present = typeof createRes.json.checkoutUrl === "string" && createRes.json.checkoutUrl.startsWith("http");
    assert(results.checkoutUrl_present, "Missing checkoutUrl");
    assert(typeof results.reservationId === "string" && results.reservationId.length > 20, "Missing reservationId");

    const statusRes = await httpJson(`${SERVER_API_BASE}/Payments/Status?reservationId=${encodeURIComponent(results.reservationId)}`, {
      headers: { "X-API-Key": SERVER_API_KEY },
    });
    results.status_http_status_before_cancel = statusRes.status;
    results.status_payload_before_cancel = statusRes.json ?? null;
    assert(statusRes.ok, `Payments/Status failed: ${statusRes.status}`);

    const cancelRes = await httpJson(`${SERVER_API_BASE}/Payments/Cancel`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-API-Key": SERVER_API_KEY,
      },
      body: JSON.stringify({
        reservationId: results.reservationId,
        reason: "e2e_smoke_test",
      }),
    });
    results.cancel_http_status = cancelRes.status;
    results.cancel_payload = cancelRes.json ?? null;
    assert(cancelRes.ok, `Payments/Cancel failed: ${cancelRes.status} ${cancelRes.text.slice(0, 200)}`);

    const statusAfter = await httpJson(`${SERVER_API_BASE}/Payments/Status?reservationId=${encodeURIComponent(results.reservationId)}`, {
      headers: { "X-API-Key": SERVER_API_KEY },
    });
    results.status_http_status_after_cancel = statusAfter.status;
    results.status_payload_after_cancel = statusAfter.json ?? null;
    assert(statusAfter.ok, `Payments/Status(after cancel) failed: ${statusAfter.status}`);
  }

  return results;
}

async function testOcpp16RemoteStartStop() {
  const results = {
    protocol: "ocpp1.6",
    cpId: CP16_ID,
    connectorId: 1,
    idTag: TAG,
  };

  const url = `${SERVER_WS_BASE}/${encodeURIComponent(CP16_ID)}`;
  const client = new OcppClient({ url, protocols: ["ocpp1.6", "ocpp1.5"], label: `OCPP16:${CP16_ID}` });

  let transactionId = null;

  client.onCall("RemoteStartTransaction", async (_uniqueId, payload) => {
    // Accept remote start and actively start the transaction.
    const connectorId = payload?.connectorId ?? 1;
    const idTag = payload?.idTag ?? TAG;

    const startResp = await client.call("StartTransaction", {
      connectorId,
      idTag,
      meterStart: 0,
      timestamp: nowIsoUtc(),
    });

    transactionId = startResp?.transactionId ?? startResp?.TransactionId ?? null;
    results.start_transaction_id = transactionId;

    // Simulate some charging progress.
    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: "Charging",
      timestamp: nowIsoUtc(),
    });

    await client.call("MeterValues", {
      connectorId,
      transactionId,
      meterValue: [
        {
          timestamp: nowIsoUtc(),
          sampledValue: [{ value: "1", measurand: "Energy.Active.Import.Register", unit: "kWh" }],
        },
      ],
    });

    return { status: "Accepted" };
  });

  client.onCall("RemoteStopTransaction", async (_uniqueId, payload) => {
    const txId = payload?.transactionId ?? transactionId;
    await client.call("StopTransaction", {
      transactionId: txId,
      meterStop: 2,
      timestamp: nowIsoUtc(),
      reason: "Remote",
    });

    await client.call("StatusNotification", {
      connectorId: 1,
      errorCode: "NoError",
      status: "Available",
      timestamp: nowIsoUtc(),
    });

    return { status: "Accepted" };
  });

  client.onCall("UnlockConnector", async () => ({ status: "Unlocked" }));
  client.onCall("Reset", async () => ({ status: "Accepted" }));
  client.onCall("SetChargingProfile", async () => ({ status: "Accepted" }));
  client.onCall("ClearChargingProfile", async () => ({ status: "Accepted" }));

  await client.connect();

  const bn = await client.call("BootNotification", { chargePointVendor: "TestVendor", chargePointModel: "TestModel" });
  results.boot_status = bn?.status ?? null;
  results.heartbeat_interval = bn?.interval ?? null;
  assert(results.boot_status === "Accepted", `OCPP16 BootNotification not accepted: ${results.boot_status}`);

  const auth = await client.call("Authorize", { idTag: TAG });
  results.authorize_status = auth?.idTagInfo?.status ?? null;
  assert(results.authorize_status === "Accepted", `OCPP16 Authorize not accepted: ${results.authorize_status}`);

  await client.call("StatusNotification", {
    connectorId: 1,
    errorCode: "NoError",
    status: "Preparing",
    timestamp: nowIsoUtc(),
  });

  // Exercise a few server API commands that use async websocket request/response.
  const apiUnlock = await httpJson(`${SERVER_API_BASE}/UnlockConnector/${encodeURIComponent(CP16_ID)}/1`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_unlock_status = apiUnlock.status;
  results.api_unlock_body = apiUnlock.json ?? null;
  assert(apiUnlock.ok, `API UnlockConnector failed: ${apiUnlock.status}`);

  const apiSetLimit = await httpJson(`${SERVER_API_BASE}/SetChargingLimit/${encodeURIComponent(CP16_ID)}/1/6A`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_set_limit_status = apiSetLimit.status;
  results.api_set_limit_body = apiSetLimit.json ?? null;
  assert(apiSetLimit.ok, `API SetChargingLimit failed: ${apiSetLimit.status}`);

  const apiClearLimit = await httpJson(`${SERVER_API_BASE}/ClearChargingLimit/${encodeURIComponent(CP16_ID)}/1`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_clear_limit_status = apiClearLimit.status;
  results.api_clear_limit_body = apiClearLimit.json ?? null;
  assert(apiClearLimit.ok, `API ClearChargingLimit failed: ${apiClearLimit.status}`);

  const apiReset = await httpJson(`${SERVER_API_BASE}/Reset/${encodeURIComponent(CP16_ID)}`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_reset_status = apiReset.status;
  results.api_reset_body = apiReset.json ?? null;
  assert(apiReset.ok, `API Reset failed: ${apiReset.status}`);

  // Payments: create a checkout session while the charger is online, then cancel it.
  // This validates the server-side API integration (including Stripe if configured).
  results.payments = await testPaymentsCreateCancel({
    chargePointId: CP16_ID,
    connectorId: 1,
    chargeTagId: TAG,
  });

  // Ensure /API/Status shows the CP connected.
  const statusBefore = await httpJson(`${SERVER_API_BASE}/Status`, { headers: { "X-API-Key": SERVER_API_KEY } });
  assert(statusBefore.ok, `GET /API/Status failed: ${statusBefore.status}`);
  results.server_status_contains_cp_before = Array.isArray(statusBefore.json) && statusBefore.json.some((s) => s.id === CP16_ID);

  // Trigger remote start via server API; this will send RemoteStartTransaction to us.
  const apiStart = await httpJson(
    `${SERVER_API_BASE}/StartTransaction/${encodeURIComponent(CP16_ID)}/1/${encodeURIComponent(TAG)}`,
    { headers: { "X-API-Key": SERVER_API_KEY } },
  );
  results.api_start_status = apiStart.status;
  results.api_start_body = apiStart.json ?? null;
  assert(apiStart.ok, `API StartTransaction failed: ${apiStart.status} ${apiStart.text.slice(0, 200)}`);

  // Give server time to persist the started tx before we request stop.
  await sleep(500);

  // Trigger remote stop via server API; server will find the open transaction and send RemoteStopTransaction.
  const apiStop = await httpJson(`${SERVER_API_BASE}/StopTransaction/${encodeURIComponent(CP16_ID)}/1`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_stop_status = apiStop.status;
  results.api_stop_body = apiStop.json ?? null;
  assert(apiStop.ok, `API StopTransaction failed: ${apiStop.status} ${apiStop.text.slice(0, 200)}`);

  // Disconnect
  await sleep(250);
  client.close();

  // Confirm CP disappears from /API/Status after disconnect (best-effort; server removes on close).
  await sleep(250);
  const statusAfter = await httpJson(`${SERVER_API_BASE}/Status`, { headers: { "X-API-Key": SERVER_API_KEY } });
  results.server_status_contains_cp_after = Array.isArray(statusAfter.json) && statusAfter.json.some((s) => s.id === CP16_ID);

  return results;
}

function guidLike() {
  // Simple GUID-like string for transactionId in OCPP2.0.
  const s4 = () => Math.floor((1 + Math.random()) * 0x10000).toString(16).slice(1);
  return `${s4()}${s4()}-${s4()}-${s4()}-${s4()}-${s4()}${s4()}${s4()}`;
}

async function testOcpp20RequestStartStop() {
  const results = {
    protocol: "ocpp2.0.1",
    cpId: CP20_ID,
    evseId: 2,
    connectorId: 2,
    idTag: TAG,
  };

  const url = `${SERVER_WS_BASE}/${encodeURIComponent(CP20_ID)}`;
  const client = new OcppClient({ url, protocols: ["ocpp2.0.1"], label: `OCPP20:${CP20_ID}` });

  let transactionUid = null;

  client.onCall("RequestStartTransaction", async (_uniqueId, payload) => {
    const evseId = payload?.evseId ?? 2;
    transactionUid = guidLike();

    await client.call("TransactionEvent", {
      eventType: "Started",
      timestamp: nowIsoUtc(),
      triggerReason: "Authorized",
      seqNo: 0,
      idToken: { idToken: TAG, type: "Central" },
      evse: { id: evseId, connectorId: evseId },
      transactionInfo: { transactionId: transactionUid, remoteStartId: 0, chargingState: "EVConnected" },
      meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 0 }] }],
    });

    // Some periodic updates + SoC/meter values
    await client.call("MeterValues", {
      evseId,
      meterValue: [
        {
          timestamp: nowIsoUtc(),
          sampledValue: [
            { value: 0, measurand: "Energy.Active.Import.Register" },
            { value: 7200, measurand: "Power.Active.Import" },
          ],
        },
        { timestamp: nowIsoUtc(), sampledValue: [{ value: 42, measurand: "SoC" }] },
      ],
    });

    await client.call("TransactionEvent", {
      eventType: "Updated",
      timestamp: nowIsoUtc(),
      triggerReason: "MeterValuePeriodic",
      seqNo: 1,
      idToken: { idToken: TAG, type: "Central" },
      evse: { id: evseId, connectorId: evseId },
      transactionInfo: { transactionId: transactionUid, remoteStartId: 0, chargingState: "Charging" },
      meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 10 }] }],
    });

    return { status: "Accepted" };
  });

  client.onCall("RequestStopTransaction", async (_uniqueId, payload) => {
    const txId = payload?.transactionId ?? transactionUid;
    await client.call("TransactionEvent", {
      eventType: "Ended",
      timestamp: nowIsoUtc(),
      triggerReason: "Deauthorized",
      seqNo: 2,
      idToken: { idToken: TAG, type: "Central" },
      evse: { id: 2, connectorId: 2 },
      transactionInfo: { transactionId: txId, remoteStartId: 0 },
      meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 20 }] }],
    });
    return { status: "Accepted" };
  });

  client.onCall("UnlockConnector", async () => ({ status: "Unlocked" }));
  client.onCall("Reset", async () => ({ status: "Accepted" }));
  client.onCall("SetChargingProfile", async () => ({ status: "Accepted" }));
  client.onCall("ClearChargingProfile", async () => ({ status: "Accepted" }));

  await client.connect();

  const bn = await client.call("BootNotification", {
    chargingStation: { model: "TestModel", vendorName: "TestVendor", firmwareVersion: "0.0.0" },
    reason: "PowerUp",
  });
  results.boot_status = bn?.status ?? null;
  results.heartbeat_interval = bn?.interval ?? null;
  assert(results.boot_status === "Accepted", `OCPP20 BootNotification not accepted: ${results.boot_status}`);

  const auth = await client.call("Authorize", { idToken: { idToken: TAG, type: "Central" } });
  results.authorize_status = auth?.idTokenInfo?.status ?? null;
  assert(results.authorize_status === "Accepted", `OCPP20 Authorize not accepted: ${results.authorize_status}`);

  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Available",
    evseId: 2,
    connectorId: 2,
  });

  const apiUnlock = await httpJson(`${SERVER_API_BASE}/UnlockConnector/${encodeURIComponent(CP20_ID)}/2`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_unlock_status = apiUnlock.status;
  results.api_unlock_body = apiUnlock.json ?? null;
  assert(apiUnlock.ok, `API UnlockConnector (OCPP20) failed: ${apiUnlock.status}`);

  const apiSetLimit = await httpJson(`${SERVER_API_BASE}/SetChargingLimit/${encodeURIComponent(CP20_ID)}/2/6A`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_set_limit_status = apiSetLimit.status;
  results.api_set_limit_body = apiSetLimit.json ?? null;
  assert(apiSetLimit.ok, `API SetChargingLimit (OCPP20) failed: ${apiSetLimit.status}`);

  const apiClearLimit = await httpJson(`${SERVER_API_BASE}/ClearChargingLimit/${encodeURIComponent(CP20_ID)}/2`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_clear_limit_status = apiClearLimit.status;
  results.api_clear_limit_body = apiClearLimit.json ?? null;
  assert(apiClearLimit.ok, `API ClearChargingLimit (OCPP20) failed: ${apiClearLimit.status}`);

  const apiReset = await httpJson(`${SERVER_API_BASE}/Reset/${encodeURIComponent(CP20_ID)}`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_reset_status = apiReset.status;
  results.api_reset_body = apiReset.json ?? null;
  assert(apiReset.ok, `API Reset (OCPP20) failed: ${apiReset.status}`);

  // Payments: attempt a checkout creation while this OCPP2.0 charger is online.
  // We cancel immediately to avoid leaving an active reservation.
  results.payments = await testPaymentsCreateCancel({
    chargePointId: CP20_ID,
    connectorId: 2,
    chargeTagId: TAG,
  });

  const statusBefore = await httpJson(`${SERVER_API_BASE}/Status`, { headers: { "X-API-Key": SERVER_API_KEY } });
  results.server_status_contains_cp_before = Array.isArray(statusBefore.json) && statusBefore.json.some((s) => s.id === CP20_ID);

  const apiStart = await httpJson(`${SERVER_API_BASE}/StartTransaction/${encodeURIComponent(CP20_ID)}/2/${encodeURIComponent(TAG)}`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_start_status = apiStart.status;
  results.api_start_body = apiStart.json ?? null;
  assert(apiStart.ok, `API StartTransaction (OCPP20) failed: ${apiStart.status} ${apiStart.text.slice(0, 200)}`);

  await sleep(500);

  const apiStop = await httpJson(`${SERVER_API_BASE}/StopTransaction/${encodeURIComponent(CP20_ID)}/2`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });
  results.api_stop_status = apiStop.status;
  results.api_stop_body = apiStop.json ?? null;
  assert(apiStop.ok, `API StopTransaction (OCPP20) failed: ${apiStop.status} ${apiStop.text.slice(0, 200)}`);

  await sleep(250);
  client.close();

  await sleep(250);
  const statusAfter = await httpJson(`${SERVER_API_BASE}/Status`, { headers: { "X-API-Key": SERVER_API_KEY } });
  results.server_status_contains_cp_after = Array.isArray(statusAfter.json) && statusAfter.json.some((s) => s.id === CP20_ID);

  return results;
}

async function main() {
  const summary = { startedAtUtc: nowIsoUtc() };
  summary.http = await smokeHttp();
  summary.ocpp16 = await testOcpp16RemoteStartStop();
  summary.ocpp20 = await testOcpp20RequestStartStop();
  summary.finishedAtUtc = nowIsoUtc();

  // Emit machine-readable summary for humans and CI.
  // Do not include API keys.
  console.log(JSON.stringify(summary, null, 2));
}

main().catch((err) => {
  console.error(err?.stack ?? String(err));
  process.exitCode = 1;
});
