import fs from "node:fs";
import path from "node:path";
import { spawn, execFileSync } from "node:child_process";
import WebSocket from "ws";
import {
  assert,
  clearDirectoryContents,
  createTempFilePath,
  nowIsoUtc,
  readRuntimeInfo,
  resolveStackDefaults,
  setQuietHours,
  sleep,
} from "./common.mjs";

const protocolPresets = {
  "1.6": {
    chargePointId: process.env.CP16_ID ?? "Test1234",
    connectorId: 1,
    wsProtocols: ["ocpp1.6", "ocpp1.5"],
    kind: "1.6",
  },
  "2.0.1": {
    chargePointId: process.env.CP20_ID ?? "TestAAA",
    connectorId: 2,
    wsProtocols: ["ocpp2.0.1"],
    kind: "2.x",
  },
  "2.1": {
    chargePointId: process.env.CP21_ID ?? process.env.CP20_ID ?? "TestAAA",
    connectorId: 2,
    wsProtocols: ["ocpp2.1"],
    kind: "2.x",
  },
};

const scenarioNames = [
  "stop_then_unplug",
  "suspended_idle_then_unplug",
  "quiet_hours_idle_excluded",
  "live_meter_progress",
];

function parseArgs(argv) {
  const parsed = {};
  for (let index = 0; index < argv.length; index++) {
    const value = argv[index];
    if (!value.startsWith("--")) {
      continue;
    }

    const [key, inlineValue] = value.slice(2).split("=", 2);
    parsed[key] = inlineValue ?? argv[index + 1];
    if (inlineValue == null) {
      index++;
    }
  }
  return parsed;
}

function newId(prefix = "") {
  return `${prefix}${Math.random().toString(16).slice(2)}${Math.random().toString(16).slice(2)}`.slice(0, 32);
}

function guidLike() {
  const s4 = () => Math.floor((1 + Math.random()) * 0x10000).toString(16).slice(1);
  return `${s4()}${s4()}-${s4()}-${s4()}-${s4()}-${s4()}${s4()}${s4()}`;
}

async function httpJson(url, { method = "GET", headers = {}, body } = {}) {
  const response = await fetch(url, { method, headers, body });
  const text = await response.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  return { ok: response.ok, status: response.status, text, json };
}

async function httpText(url, options) {
  const response = await fetch(url, options);
  return { ok: response.ok, status: response.status, text: await response.text() };
}

class OcppClient {
  constructor({ url, protocols, label }) {
    this.url = url;
    this.protocols = protocols;
    this.label = label;
    this.ws = null;
    this.pending = new Map();
    this.callHandlers = new Map();
  }

  onCall(action, handler) {
    this.callHandlers.set(action, handler);
  }

  async connect(timeoutMs = 8_000) {
    this.ws = new WebSocket(this.url, this.protocols);

    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error(`[${this.label}] connect timeout`)), timeoutMs);
      this.ws.once("open", () => {
        clearTimeout(timeout);
        resolve();
      });
      this.ws.once("error", () => {
        clearTimeout(timeout);
        reject(new Error(`[${this.label}] websocket error`));
      });
    });

    this.ws.on("message", (buffer) => {
      this.#handleMessage(buffer.toString()).catch((error) => {
        throw error;
      });
    });
  }

  close(code = 3001, reason = "") {
    try {
      this.ws?.close(code, reason);
    } catch {
      // ignore
    }
  }

  sendRaw(payload) {
    assert(this.ws && this.ws.readyState === WebSocket.OPEN, `[${this.label}] websocket not open`);
    this.ws.send(JSON.stringify(payload));
  }

  async call(action, payload, timeoutMs = 15_000) {
    const id = newId("c");
    assert(this.ws && this.ws.readyState === WebSocket.OPEN, `[${this.label}] websocket not open`);

    const result = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`[${this.label}] ${action} timed out`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timeout, action });
    });

    this.ws.send(JSON.stringify([2, id, action, payload ?? {}]));
    return result;
  }

  async #handleMessage(raw) {
    const message = JSON.parse(raw);
    const type = message?.[0];
    const uniqueId = message?.[1];

    if (type === 3) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.resolve(message[2]);
      }
      return;
    }

    if (type === 4) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.reject(new Error(`[${this.label}] CALLERROR for ${pending.action}: ${message[2]} ${message[3]}`));
      }
      return;
    }

    if (type !== 2) {
      throw new Error(`[${this.label}] unexpected OCPP frame ${raw}`);
    }

    const action = message?.[2];
    const payload = message?.[3] ?? {};
    const handler = this.callHandlers.get(action);
    if (!handler) {
      this.sendRaw([4, uniqueId, "NotImplemented", `No handler for ${action}`, {}]);
      return;
    }

    try {
      const response = await handler(uniqueId, payload);
      this.sendRaw([3, uniqueId, response ?? {}]);
    } catch (error) {
      this.sendRaw([4, uniqueId, "InternalError", String(error?.message ?? error), {}]);
    }
  }
}

function resolveRuntime() {
  if (fs.existsSync(path.join(path.dirname(new URL(import.meta.url).pathname), ".runtime", "stack.json"))) {
    return readRuntimeInfo();
  }

  return resolveStackDefaults();
}

function scenarioTag(protocol, scenario) {
  const sanitizedScenario = scenario.replace(/[^a-z0-9]+/gi, "").slice(0, 10).toUpperCase() || "SCENARIO";
  const sanitizedProtocol = protocol.replace(/[^0-9]+/g, "") || "PROTO";
  return `PW${sanitizedProtocol}${sanitizedScenario}`.slice(0, 16);
}

function redactCheckoutUrl(payload) {
  if (!payload || typeof payload !== "object") {
    return payload ?? null;
  }

  const clone = { ...payload };
  if (typeof clone.checkoutUrl === "string") {
    clone.checkoutUrl = "<redacted>";
  }
  return clone;
}

async function confirmMockCheckout(checkoutUrl) {
  const url = new URL(checkoutUrl);
  const successUrl = url.searchParams.get("successUrl");
  assert(successUrl, `Mock checkout url missing successUrl: ${checkoutUrl}`);
  const response = await httpText(successUrl);
  assert(response.ok, `Mock checkout success redirect failed: ${response.status}`);
  return {
    successUrl,
    sessionId: url.searchParams.get("session_id"),
  };
}

async function createAndConfirmReservation(runtime, preset, tag) {
  const createResponse = await httpJson(`${runtime.serverApiBaseUrl}/Payments/Create`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-API-Key": runtime.apiKey,
    },
    body: JSON.stringify({
      chargePointId: preset.chargePointId,
      connectorId: preset.connectorId,
      chargeTagId: tag,
      origin: "public",
      returnBaseUrl: runtime.managementBaseUrl,
    }),
  });

  assert(createResponse.ok, `Payments/Create failed: ${createResponse.status} ${createResponse.text.slice(0, 200)}`);
  assert(createResponse.json?.status === "Redirect", `Expected Redirect from Payments/Create, got ${createResponse.json?.status ?? "null"}`);
  assert(typeof createResponse.json?.reservationId === "string", "Payments/Create missing reservationId");
  assert(typeof createResponse.json?.checkoutUrl === "string", "Payments/Create missing checkoutUrl");

  const confirmation = await confirmMockCheckout(createResponse.json.checkoutUrl);
  return {
    reservationId: createResponse.json.reservationId,
    checkout: redactCheckoutUrl(createResponse.json),
    confirmation,
  };
}

async function fetchPaymentStatus(runtime, reservationId) {
  const response = await httpJson(`${runtime.serverApiBaseUrl}/Payments/Status?reservationId=${encodeURIComponent(reservationId)}`, {
    headers: {
      "X-API-Key": runtime.apiKey,
    },
  });

  assert(response.ok, `Payments/Status failed for ${reservationId}: ${response.status} ${response.text.slice(0, 200)}`);
  return response.json;
}

async function bootChargePoint(client, preset, tag) {
  if (preset.kind === "1.6") {
    const boot = await client.call("BootNotification", {
      chargePointVendor: "Codex",
      chargePointModel: "Playwright16",
    });
    assert(boot?.status === "Accepted", `BootNotification rejected for ${preset.chargePointId}`);
    const authorize = await client.call("Authorize", { idTag: tag });
    assert(authorize?.idTagInfo?.status === "Accepted", `Authorize rejected for ${preset.chargePointId}`);
    await client.call("StatusNotification", {
      connectorId: preset.connectorId,
      errorCode: "NoError",
      status: "Available",
      timestamp: nowIsoUtc(),
    });
    return;
  }

  const boot = await client.call("BootNotification", {
    chargingStation: {
      model: "Playwright2x",
      vendorName: "Codex",
      firmwareVersion: "0.0.0",
    },
    reason: "PowerUp",
  });
  assert(boot?.status === "Accepted", `BootNotification rejected for ${preset.chargePointId}`);
  const authorize = await client.call("Authorize", { idToken: { idToken: tag, type: "Central" } });
  assert(authorize?.idTokenInfo?.status === "Accepted", `Authorize rejected for ${preset.chargePointId}`);
  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Available",
    evseId: preset.connectorId,
    connectorId: preset.connectorId,
  });
}

async function sendMeterValue16(client, connectorId, transactionId, kwh) {
  await client.call("MeterValues", {
    connectorId,
    transactionId,
    meterValue: [
      {
        timestamp: nowIsoUtc(),
        sampledValue: [
          { value: String(kwh), measurand: "Energy.Active.Import.Register", unit: "kWh" },
          { value: String((kwh / 3).toFixed(3)), measurand: "Energy.Active.Import.Register", phase: "L1", unit: "kWh" },
          { value: String((kwh / 3).toFixed(3)), measurand: "Energy.Active.Import.Register", phase: "L2", unit: "kWh" },
          { value: String((kwh / 3).toFixed(3)), measurand: "Energy.Active.Import.Register", phase: "L3", unit: "kWh" },
        ],
      },
    ],
  });
}

async function sendMeterValue2x(client, connectorId, kwh, chargingState, seqNo, transactionId, tag) {
  await client.call("TransactionEvent", {
    eventType: "Updated",
    timestamp: nowIsoUtc(),
    triggerReason: "MeterValuePeriodic",
    seqNo,
    idToken: { idToken: tag, type: "Central" },
    evse: { id: connectorId, connectorId },
    transactionInfo: { transactionId, chargingState },
    meterValue: [
      {
        timestamp: nowIsoUtc(),
        sampledValue: [
          { value: kwh, measurand: "Energy.Active.Import.Register", unitOfMeasure: { unit: "kWh" } },
          { value: Number((kwh / 3).toFixed(3)), measurand: "Energy.Active.Import.Register", phase: "L1", unitOfMeasure: { unit: "kWh" } },
          { value: Number((kwh / 3).toFixed(3)), measurand: "Energy.Active.Import.Register", phase: "L2", unitOfMeasure: { unit: "kWh" } },
          { value: Number((kwh / 3).toFixed(3)), measurand: "Energy.Active.Import.Register", phase: "L3", unitOfMeasure: { unit: "kWh" } },
          { value: 7200, measurand: "Power.Active.Import", unitOfMeasure: { unit: "W" } },
        ],
      },
    ],
  });
}

async function executeScenario16(client, preset, scenario, tag, timings, onResult) {
  let transactionId = null;

  client.onCall("RemoteStartTransaction", async (_uniqueId, payload) => {
    const connectorId = payload?.connectorId ?? preset.connectorId;
    const idTag = payload?.idTag ?? tag;
    const startResponse = await client.call("StartTransaction", {
      connectorId,
      idTag,
      meterStart: 0,
      timestamp: nowIsoUtc(),
    });
    transactionId = startResponse?.transactionId ?? startResponse?.TransactionId;
    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: "Charging",
      timestamp: nowIsoUtc(),
    });

    await sendMeterValue16(client, connectorId, transactionId, 0.677);
    await sleep(timings.betweenMeterValuesMs);
    await sendMeterValue16(client, connectorId, transactionId, 1.234);

    if (scenario === "live_meter_progress") {
      await sleep(timings.extraChargeMs);
      await sendMeterValue16(client, connectorId, transactionId, 2.468);
    }

    if (scenario === "suspended_idle_then_unplug" || scenario === "quiet_hours_idle_excluded") {
      await client.call("StatusNotification", {
        connectorId,
        errorCode: "NoError",
        status: "SuspendedEV",
        timestamp: nowIsoUtc(),
      });
      await sleep(timings.suspendedBeforeStopMs);
    } else {
      await sleep(timings.activeBeforeStopMs);
    }

    await client.call("StopTransaction", {
      transactionId,
      meterStop: scenario === "live_meter_progress" ? 2.468 : 1.234,
      timestamp: nowIsoUtc(),
      reason: "Local",
    });

    if (scenario === "stop_then_unplug" || scenario === "quiet_hours_idle_excluded" || scenario === "suspended_idle_then_unplug") {
      await client.call("StatusNotification", {
        connectorId,
        errorCode: "NoError",
        status: scenario === "stop_then_unplug" ? "Finishing" : "SuspendedEV",
        timestamp: nowIsoUtc(),
      });
      await sleep(timings.afterStopBeforeUnplugMs);
    }

    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: "Available",
      timestamp: nowIsoUtc(),
    });

    onResult({ transactionId });
    return { status: "Accepted" };
  });
}

async function executeScenario2x(client, preset, scenario, tag, timings, onResult) {
  let transactionId = null;

  client.onCall("RequestStartTransaction", async (_uniqueId, payload) => {
    const connectorId = payload?.evseId ?? preset.connectorId;
    transactionId = guidLike();

    await client.call("TransactionEvent", {
      eventType: "Started",
      timestamp: nowIsoUtc(),
      triggerReason: "Authorized",
      seqNo: 0,
      idToken: { idToken: tag, type: "Central" },
      evse: { id: connectorId, connectorId },
      transactionInfo: { transactionId, remoteStartId: payload?.remoteStartId ?? 0, chargingState: "EVConnected" },
      meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 0 }] }],
    });

    await client.call("StatusNotification", {
      timestamp: nowIsoUtc(),
      connectorStatus: "Occupied",
      evseId: connectorId,
      connectorId,
    });

    await sendMeterValue2x(client, connectorId, 0.677, "Charging", 1, transactionId, tag);
    await sleep(timings.betweenMeterValuesMs);
    await sendMeterValue2x(client, connectorId, 1.234, "Charging", 2, transactionId, tag);

    if (scenario === "live_meter_progress") {
      await sleep(timings.extraChargeMs);
      await sendMeterValue2x(client, connectorId, 2.468, "Charging", 3, transactionId, tag);
    }

    if (scenario === "suspended_idle_then_unplug" || scenario === "quiet_hours_idle_excluded") {
      await sendMeterValue2x(client, connectorId, 1.234, "SuspendedEV", 4, transactionId, tag);
      await sleep(timings.suspendedBeforeStopMs);
    } else {
      await sleep(timings.activeBeforeStopMs);
    }

    await client.call("TransactionEvent", {
      eventType: "Ended",
      timestamp: nowIsoUtc(),
      triggerReason: "RemoteStop",
      seqNo: scenario === "live_meter_progress" ? 5 : 4,
      idToken: { idToken: tag, type: "Central" },
      evse: { id: connectorId, connectorId },
      transactionInfo: { transactionId },
      meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: scenario === "live_meter_progress" ? 2.468 : 1.234 }] }],
    });

    if (scenario === "stop_then_unplug" || scenario === "quiet_hours_idle_excluded" || scenario === "suspended_idle_then_unplug") {
      await sleep(timings.afterStopBeforeUnplugMs);
    }

    await client.call("StatusNotification", {
      timestamp: nowIsoUtc(),
      connectorStatus: "Available",
      evseId: connectorId,
      connectorId,
    });

    onResult({ transactionId });
    return { status: "Accepted" };
  });
}

function applyScenarioDatabaseState(runtime, scenario) {
  if (!runtime?.databasePath) {
    return;
  }

  if (scenario === "quiet_hours_idle_excluded") {
    setQuietHours(runtime.databasePath, true, "00:00-23:59");
  } else {
    setQuietHours(runtime.databasePath, false, null);
  }

  if (runtime.emailSinkDir) {
    clearDirectoryContents(runtime.emailSinkDir);
  }
}

export async function runProtocolScenario({
  protocol,
  scenario,
  triggerMode = "api",
  tag = scenarioTag(protocol, scenario),
  resultPath = null,
  runtime = resolveRuntime(),
} = {}) {
  const preset = protocolPresets[protocol];
  assert(preset, `Unsupported protocol ${protocol}`);
  assert(scenarioNames.includes(scenario), `Unsupported scenario ${scenario}`);

  applyScenarioDatabaseState(runtime, scenario);

  const results = {
    protocol,
    scenario,
    triggerMode,
    chargePointId: preset.chargePointId,
    connectorId: preset.connectorId,
    tag,
    startedAtUtc: nowIsoUtc(),
  };

  const url = `${runtime.serverWsBaseUrl}/${encodeURIComponent(preset.chargePointId)}`;
  const client = new OcppClient({ url, protocols: preset.wsProtocols, label: `${protocol}:${preset.chargePointId}` });
  const timings = triggerMode === "browser"
    ? {
        betweenMeterValuesMs: 1_000,
        extraChargeMs: 1_200,
        activeBeforeStopMs: 1_500,
        suspendedBeforeStopMs: 1_500,
        afterStopBeforeUnplugMs: 6_500,
      }
    : {
        betweenMeterValuesMs: 400,
        extraChargeMs: 500,
        activeBeforeStopMs: 500,
        suspendedBeforeStopMs: 750,
        afterStopBeforeUnplugMs: 1_200,
      };
  let finishScenario;
  const scenarioFinished = new Promise((resolve) => {
    finishScenario = resolve;
  });

  for (const action of ["UnlockConnector", "Reset", "SetChargingProfile", "ClearChargingProfile", "RemoteStopTransaction", "RequestStopTransaction"]) {
    client.onCall(action, async () => ({ status: "Accepted" }));
  }

  if (preset.kind === "1.6") {
    await executeScenario16(client, preset, scenario, tag, timings, (data) => finishScenario(data));
  } else {
    await executeScenario2x(client, preset, scenario, tag, timings, (data) => finishScenario(data));
  }

  await client.connect();
  await bootChargePoint(client, preset, tag);
  results.readyAtUtc = nowIsoUtc();

  if (triggerMode === "browser") {
    console.log("SCENARIO_READY");
  } else {
    results.reservation = await createAndConfirmReservation(runtime, preset, tag);
  }

  const scenarioData = await Promise.race([
    scenarioFinished,
    sleep(90_000).then(() => {
      throw new Error(`Scenario timed out: ${protocol} ${scenario}`);
    }),
  ]);

  results.transactionId = scenarioData?.transactionId ?? null;

  if (results.reservation?.reservationId) {
    await sleep(1_000);
    results.finalStatus = await fetchPaymentStatus(runtime, results.reservation.reservationId);
  }

  results.finishedAtUtc = nowIsoUtc();
  client.close();

  if (resultPath) {
    fs.writeFileSync(resultPath, JSON.stringify(results, null, 2));
  }

  return results;
}

export async function runSmokeMatrix() {
  const runtime = resolveRuntime();
  const results = { startedAtUtc: nowIsoUtc(), scenarios: [] };
  for (const protocol of ["1.6", "2.0.1", "2.1"]) {
    results.scenarios.push(await runProtocolScenario({
      protocol,
      scenario: "stop_then_unplug",
      triggerMode: "api",
      runtime,
    }));
  }
  results.finishedAtUtc = nowIsoUtc();
  return results;
}

export async function runScenarioMatrix() {
  const runtime = resolveRuntime();
  const results = { startedAtUtc: nowIsoUtc(), scenarios: [] };
  for (const protocol of ["1.6", "2.0.1", "2.1"]) {
    for (const scenario of scenarioNames) {
      results.scenarios.push(await runProtocolScenario({
        protocol,
        scenario,
        triggerMode: "api",
        runtime,
      }));
    }
  }
  results.finishedAtUtc = nowIsoUtc();
  return results;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const mode = args.mode ?? "smoke";
  const resultPath = args["json-out"] ?? args.jsonOut ?? null;

  let result;
  if (mode === "matrix") {
    result = await runScenarioMatrix();
  } else if (mode === "scenario") {
    result = await runProtocolScenario({
      protocol: args.protocol ?? "1.6",
      scenario: args.scenario ?? "stop_then_unplug",
      triggerMode: args.trigger ?? "api",
      resultPath,
    });
  } else {
    result = await runSmokeMatrix();
  }

  if (resultPath && !fs.existsSync(resultPath)) {
    fs.writeFileSync(resultPath, JSON.stringify(result, null, 2));
  }

  console.log(JSON.stringify(result, null, 2));
}

const executedDirectly = process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname);
if (executedDirectly) {
  main().catch((error) => {
    console.error(error?.stack ?? String(error));
    process.exit(1);
  });
}
