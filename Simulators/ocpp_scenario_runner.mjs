import { execFileSync } from "node:child_process";

const SERVER_HTTP_BASE = process.env.SERVER_HTTP_BASE ?? "http://localhost:8081";
const SERVER_API_BASE = process.env.SERVER_API_BASE ?? `${SERVER_HTTP_BASE}/API`;
const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? "ws://localhost:8081/OCPP";
const SERVER_API_KEY = process.env.SERVER_API_KEY ?? "36029A5F-B736-4DA9-AE46-D66847C9062C";
const SQLITE_DB_PATH = process.env.SQLITE_DB_PATH ?? "";

const CP16_ID = process.env.CP16_ID ?? "Test1234";
const CP20_ID = process.env.CP20_ID ?? "TestAAA";
const CP21_ID = process.env.CP21_ID ?? "Test21A";
const TAG = process.env.TAG ?? "SCENARIO";

function nowIsoUtc()
{
  return new Date().toISOString().replace(/\.\d{3}Z$/, "Z");
}

function sleep(ms)
{
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function assert(condition, message)
{
  if (!condition) {
    throw new Error(message);
  }
}

function newId(prefix = "")
{
  return `${prefix}${Math.random().toString(16).slice(2)}${Math.random().toString(16).slice(2)}`.slice(0, 32);
}

function parseArgs(argv)
{
  const args = {};
  for (const arg of argv) {
    if (!arg.startsWith("--")) {
      continue;
    }

    const [key, rawValue] = arg.slice(2).split("=", 2);
    args[key] = rawValue ?? "true";
  }

  return args;
}

async function httpJson(url, { method = "GET", headers = {}, body } = {})
{
  const res = await fetch(url, { method, headers, body });
  const text = await res.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }

  return { ok: res.ok, status: res.status, text, json };
}

class OcppClient
{
  constructor({ url, protocols, label })
  {
    this.url = url;
    this.protocols = protocols;
    this.label = label;
    this.ws = null;
    this.pending = new Map();
    this.callHandlers = new Map();
  }

  onCall(action, handler)
  {
    this.callHandlers.set(action, handler);
  }

  async connect({ timeoutMs = 8000 } = {})
  {
    assert(typeof WebSocket === "function", "Global WebSocket is not available in this Node runtime.");
    this.ws = new WebSocket(this.url, this.protocols);

    const ws = this.ws;
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error(`[${this.label}] connect timeout`)), timeoutMs);
      ws.onopen = () => {
        clearTimeout(timeout);
        resolve();
      };
      ws.onerror = () => {
        clearTimeout(timeout);
        reject(new Error(`[${this.label}] websocket error`));
      };
    });

    ws.onmessage = (event) => this.#handleMessage(event.data);
  }

  close(code = 3001, reason = "")
  {
    try {
      this.ws?.close(code, reason);
    } catch {
      // Ignore close races.
    }
  }

  sendRaw(obj)
  {
    assert(this.ws && this.ws.readyState === 1, `[${this.label}] websocket not open`);
    this.ws.send(JSON.stringify(obj));
  }

  async call(action, payload, { timeoutMs = 15000 } = {})
  {
    const id = newId("c");
    const ws = this.ws;
    assert(ws && ws.readyState === 1, `[${this.label}] websocket not open`);

    const pending = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`[${this.label}] ${action} timed out`));
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timeout, action });
    });

    ws.send(JSON.stringify([2, id, action, payload ?? {}]));
    return pending;
  }

  async #handleMessage(raw)
  {
    const msg = JSON.parse(raw);
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
        this.sendRaw([4, uniqueId, "NotImplemented", `No handler for ${action}`, {}]);
        return;
      }

      try {
        const responsePayload = await handler(uniqueId, payload);
        this.sendRaw([3, uniqueId, responsePayload ?? {}]);
      } catch (error) {
        this.sendRaw([4, uniqueId, "InternalError", String(error?.message ?? error), {}]);
      }
    }
  }
}

function addTimeline(results, event, details = {})
{
  results.timeline.push({
    event,
    atUtc: nowIsoUtc(),
    ...details,
  });
}

function normalizeScenario(rawScenario)
{
  const scenario = String(rawScenario ?? "stop_then_unplug").trim().toLowerCase();
  if (["stop_then_unplug", "suspended_idle_then_unplug", "quiet_hours_idle_excluded", "live_meter_progress"].includes(scenario)) {
    return scenario;
  }

  throw new Error(`Unsupported scenario '${rawScenario}'.`);
}

async function queryDbSummary(chargePointId, connectorId)
{
  if (!SQLITE_DB_PATH) {
    return null;
  }

  try {
    const sql = `
      SELECT
        TransactionId,
        ChargePointId,
        ConnectorId,
        StartTagId,
        StartTime,
        StopTime,
        StopReason,
        MeterStart,
        MeterStop,
        ChargingEndedAtUtc,
        EnergyKwh,
        IdleUsageFeeMinutes,
        IdleUsageFeeAmount
      FROM Transactions
      WHERE ChargePointId = '${String(chargePointId).replace(/'/g, "''")}'
        AND ConnectorId = ${Number(connectorId)}
      ORDER BY TransactionId DESC
      LIMIT 1
    `;

    const raw = execFileSync("sqlite3", [SQLITE_DB_PATH, "-json", sql], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();

    if (!raw) {
      return null;
    }

    const rows = JSON.parse(raw);
    return Array.isArray(rows) && rows.length > 0 ? rows[0] : null;
  } catch {
    return null;
  }
}

async function queryServerStatus(chargePointId)
{
  const statusRes = await httpJson(`${SERVER_API_BASE}/Status`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });

  if (!statusRes.ok || !Array.isArray(statusRes.json)) {
    return { containsChargePoint: false, raw: statusRes };
  }

  return {
    containsChargePoint: statusRes.json.some((item) => item.id === chargePointId),
    raw: statusRes.json,
  };
}

async function runOcpp16Scenario(scenario)
{
  const results = {
    protocol: "ocpp1.6",
    scenario,
    chargePointId: CP16_ID,
    connectorId: 1,
    tag: TAG,
    timeline: [],
  };

  const client = new OcppClient({
    url: `${SERVER_WS_BASE}/${encodeURIComponent(CP16_ID)}`,
    protocols: ["ocpp1.6", "ocpp1.5"],
    label: `SCENARIO16:${CP16_ID}`,
  });

  await client.connect();
  addTimeline(results, "connected");

  const boot = await client.call("BootNotification", {
    chargePointVendor: "ScenarioVendor",
    chargePointModel: "ScenarioModel16",
  });
  results.bootStatus = boot?.status ?? null;
  addTimeline(results, "boot", { status: results.bootStatus });
  assert(results.bootStatus === "Accepted", `OCPP16 boot rejected: ${results.bootStatus}`);

  const auth = await client.call("Authorize", { idTag: TAG });
  results.authorizeStatus = auth?.idTagInfo?.status ?? null;
  addTimeline(results, "authorize", { status: results.authorizeStatus });
  assert(results.authorizeStatus === "Accepted", `OCPP16 authorize rejected: ${results.authorizeStatus}`);

  await client.call("StatusNotification", {
    connectorId: 1,
    errorCode: "NoError",
    status: "Available",
    timestamp: nowIsoUtc(),
  });
  addTimeline(results, "status", { status: "Available" });

  const start = await client.call("StartTransaction", {
    connectorId: 1,
    idTag: TAG,
    meterStart: 0,
    timestamp: nowIsoUtc(),
  });
  results.transactionId = start?.transactionId ?? start?.TransactionId ?? null;
  addTimeline(results, "transactionStarted", { transactionId: results.transactionId });

  await client.call("StatusNotification", {
    connectorId: 1,
    errorCode: "NoError",
    status: "Charging",
    timestamp: nowIsoUtc(),
  });
  addTimeline(results, "status", { status: "Charging" });

  const meterValueSamples = scenario === "live_meter_progress"
    ? [
        { value: "1.2", measurand: "Energy.Active.Import.Register", unit: "kWh" },
        { value: "7200", measurand: "Power.Active.Import", unit: "W" },
      ]
    : [
        { value: "3.5", measurand: "Energy.Active.Import.Register", unit: "kWh" },
      ];

  await client.call("MeterValues", {
    connectorId: 1,
    transactionId: results.transactionId,
    meterValue: [
      {
        timestamp: nowIsoUtc(),
        sampledValue: meterValueSamples,
      },
    ],
  });
  addTimeline(results, "meterValues", { sampleCount: meterValueSamples.length });

  if (scenario === "suspended_idle_then_unplug" || scenario === "quiet_hours_idle_excluded") {
    await client.call("StatusNotification", {
      connectorId: 1,
      errorCode: "NoError",
      status: "SuspendedEV",
      timestamp: nowIsoUtc(),
    });
    addTimeline(results, "status", { status: "SuspendedEV" });
  }

  await sleep(1000);
  await client.call("StopTransaction", {
    transactionId: results.transactionId,
    meterStop: scenario === "live_meter_progress" ? 6.8 : 3.5,
    timestamp: nowIsoUtc(),
    reason: "Local",
  });
  addTimeline(results, "transactionStopped", { reason: "Local" });

  await sleep(1000);
  await client.call("StatusNotification", {
    connectorId: 1,
    errorCode: "NoError",
    status: "Available",
    timestamp: nowIsoUtc(),
  });
  addTimeline(results, "status", { status: "Available" });

  results.serverStatusAfterScenario = await queryServerStatus(CP16_ID);
  client.close();
  await sleep(250);
  results.dbTransaction = await queryDbSummary(CP16_ID, 1);

  return results;
}

async function runOcpp2xScenario({ protocol, chargePointId, subProtocol, evseId, connectorId, scenario })
{
  const results = {
    protocol,
    scenario,
    chargePointId,
    connectorId,
    evseId,
    tag: TAG,
    timeline: [],
  };

  const client = new OcppClient({
    url: `${SERVER_WS_BASE}/${encodeURIComponent(chargePointId)}`,
    protocols: [subProtocol],
    label: `SCENARIO:${protocol}:${chargePointId}`,
  });

  await client.connect();
  addTimeline(results, "connected");

  const boot = await client.call("BootNotification", {
    chargingStation: {
      model: `ScenarioModel-${protocol}`,
      vendorName: "ScenarioVendor",
      firmwareVersion: "1.0.0",
    },
    reason: "PowerUp",
  });
  results.bootStatus = boot?.status ?? null;
  addTimeline(results, "boot", { status: results.bootStatus });
  assert(results.bootStatus === "Accepted", `${protocol} boot rejected: ${results.bootStatus}`);

  const auth = await client.call("Authorize", {
    idToken: { idToken: TAG, type: "Central" },
  });
  results.authorizeStatus = auth?.idTokenInfo?.status ?? null;
  addTimeline(results, "authorize", { status: results.authorizeStatus });
  assert(results.authorizeStatus === "Accepted", `${protocol} authorize rejected: ${results.authorizeStatus}`);

  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Available",
    evseId,
    connectorId,
  });
  addTimeline(results, "status", { status: "Available" });

  const transactionId = newId("tx-");
  results.transactionId = transactionId;

  await client.call("TransactionEvent", {
    eventType: "Started",
    timestamp: nowIsoUtc(),
    triggerReason: "Authorized",
    seqNo: 0,
    idToken: { idToken: TAG, type: "Central" },
    evse: { id: evseId, connectorId },
    transactionInfo: { transactionId, remoteStartId: 0, chargingState: "EVConnected" },
    meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 0 }] }],
  });
  addTimeline(results, "transactionStarted", { transactionId });

  await client.call("TransactionEvent", {
    eventType: "Updated",
    timestamp: nowIsoUtc(),
    triggerReason: "MeterValuePeriodic",
    seqNo: 1,
    idToken: { idToken: TAG, type: "Central" },
    evse: { id: evseId, connectorId },
    transactionInfo: { transactionId, remoteStartId: 0, chargingState: "Charging" },
    meterValue: [
      {
        timestamp: nowIsoUtc(),
        sampledValue: scenario === "live_meter_progress"
          ? [
              { value: 6.6, measurand: "Power.Active.Import", unitOfMeasure: { unit: "kW" } },
              { value: 4.2, measurand: "Energy.Active.Import.Register", unitOfMeasure: { unit: "kWh" } },
            ]
          : [{ value: 3.5, measurand: "Energy.Active.Import.Register", unitOfMeasure: { unit: "kWh" } }],
      },
    ],
  });
  addTimeline(results, "transactionUpdated", { chargingState: "Charging" });

  if (scenario === "suspended_idle_then_unplug" || scenario === "quiet_hours_idle_excluded") {
    await client.call("TransactionEvent", {
      eventType: "Updated",
      timestamp: nowIsoUtc(),
      triggerReason: "ChargingStateChanged",
      seqNo: 2,
      idToken: { idToken: TAG, type: "Central" },
      evse: { id: evseId, connectorId },
      transactionInfo: { transactionId, remoteStartId: 0, chargingState: "SuspendedEV" },
    });
    addTimeline(results, "transactionUpdated", { chargingState: "SuspendedEV" });
  }

  await sleep(1000);
  await client.call("TransactionEvent", {
    eventType: "Ended",
    timestamp: nowIsoUtc(),
    triggerReason: "Deauthorized",
    seqNo: scenario === "suspended_idle_then_unplug" || scenario === "quiet_hours_idle_excluded" ? 3 : 2,
    idToken: { idToken: TAG, type: "Central" },
    evse: { id: evseId, connectorId },
    transactionInfo: { transactionId, remoteStartId: 0 },
    meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: scenario === "live_meter_progress" ? 6.8 : 3.5 }] }],
  });
  addTimeline(results, "transactionStopped", { reason: "Local" });

  await sleep(1000);
  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Available",
    evseId,
    connectorId,
  });
  addTimeline(results, "status", { status: "Available" });

  results.serverStatusAfterScenario = await queryServerStatus(chargePointId);
  client.close();
  await sleep(250);
  results.dbTransaction = await queryDbSummary(chargePointId, connectorId);

  return results;
}

async function main()
{
  const args = parseArgs(process.argv.slice(2));
  const protocol = String(args.protocol ?? "1.6");
  const scenario = normalizeScenario(args.scenario);

  let result;
  if (protocol === "1.6") {
    result = await runOcpp16Scenario(scenario);
  } else if (protocol === "2.0.1") {
    result = await runOcpp2xScenario({
      protocol: "ocpp2.0.1",
      chargePointId: CP20_ID,
      subProtocol: "ocpp2.0.1",
      evseId: 2,
      connectorId: 2,
      scenario,
    });
  } else if (protocol === "2.1") {
    result = await runOcpp2xScenario({
      protocol: "ocpp2.1",
      chargePointId: CP21_ID,
      subProtocol: "ocpp2.1",
      evseId: 3,
      connectorId: 3,
      scenario,
    });
  } else {
    throw new Error(`Unsupported protocol '${protocol}'.`);
  }

  console.log(JSON.stringify(result, null, 2));
}

main().catch((error) => {
  console.error(error?.stack ?? String(error));
  process.exitCode = 1;
});
