import { URL } from "node:url";
import {
  OcppClient,
  assert,
  guidLike,
  httpJson,
  httpText,
  nowIsoUtc,
  sleep,
  waitFor,
} from "./test_support.mjs";

const PROTOCOL_CONFIG = {
  "1.6": {
    subProtocols: ["ocpp1.6", "ocpp1.5"],
    remoteStartAction: "RemoteStartTransaction",
    remoteStopAction: "RemoteStopTransaction",
  },
  "2.0.1": {
    subProtocols: ["ocpp2.0.1"],
    remoteStartAction: "RequestStartTransaction",
    remoteStopAction: "RequestStopTransaction",
  },
  "2.1": {
    subProtocols: ["ocpp2.1"],
    remoteStartAction: "RequestStartTransaction",
    remoteStopAction: "RequestStopTransaction",
  },
};

function normalizeProtocol(protocol) {
  const value = String(protocol || "").trim();
  if (value === "ocpp1.6") return "1.6";
  if (value === "ocpp2.0.1") return "2.0.1";
  if (value === "ocpp2.1") return "2.1";
  if (PROTOCOL_CONFIG[value]) return value;
  throw new Error(`Unsupported protocol '${protocol}'`);
}

function toUtcMillis(value) {
  if (!value) {
    return null;
  }

  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

async function sendAvailable({ protocol, client, connectorId }) {
  if (protocol === "1.6") {
    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: "Available",
      timestamp: nowIsoUtc(),
    });
    return;
  }

  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Available",
    evseId: connectorId,
    connectorId,
  });
}

async function sendPreparing({ protocol, client, connectorId }) {
  if (protocol === "1.6") {
    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: "Preparing",
      timestamp: nowIsoUtc(),
    });
    return;
  }

  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Preparing",
    evseId: connectorId,
    connectorId,
  });
}

async function sendChargingState({ protocol, client, connectorId, tag, transactionRef, state, energyKwh, powerKw, seqNo, timestampIso }) {
  const eventTimestamp = timestampIso ?? nowIsoUtc();
  if (protocol === "1.6") {
    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: state,
      timestamp: eventTimestamp,
    });

    if (energyKwh != null) {
      await client.call("MeterValues", {
        connectorId,
        transactionId: transactionRef.transactionId,
        meterValue: [
          {
            timestamp: eventTimestamp,
            sampledValue: [
              { value: String(energyKwh), measurand: "Energy.Active.Import.Register", unit: "kWh" },
              ...(powerKw != null ? [{ value: String(powerKw * 1000), measurand: "Power.Active.Import", unit: "W" }] : []),
            ],
          },
        ],
      });
    }

    return;
  }

  const chargingState = state === "SuspendedEV" ? "SuspendedEV" : state === "Charging" ? "Charging" : "EVConnected";
  await client.call("TransactionEvent", {
    eventType: "Updated",
    timestamp: eventTimestamp,
    triggerReason: "MeterValuePeriodic",
    seqNo,
    idToken: { idToken: tag, type: "Central" },
    evse: { id: connectorId, connectorId },
    transactionInfo: { transactionId: transactionRef.transactionUid, remoteStartId: transactionRef.remoteStartId ?? 0, chargingState },
    meterValue: energyKwh == null
      ? []
      : [
          {
            timestamp: eventTimestamp,
            sampledValue: [
              { value: energyKwh, measurand: "Energy.Active.Import.Register", unitOfMeasure: { unit: "kWh" } },
              ...(powerKw != null ? [{ value: powerKw * 1000, measurand: "Power.Active.Import", unitOfMeasure: { unit: "W" } }] : []),
            ],
          },
        ],
  });

  if (state === "Charging") {
    await client.call("StatusNotification", {
      timestamp: eventTimestamp,
      connectorStatus: "Occupied",
      evseId: connectorId,
      connectorId,
    });
  }
}

async function startTransaction({ protocol, client, connectorId, tag, transactionRef }) {
  if (protocol === "1.6") {
    const startResponse = await client.call("StartTransaction", {
      connectorId,
      idTag: tag,
      meterStart: 0,
      timestamp: nowIsoUtc(),
    });

    transactionRef.transactionId = startResponse?.transactionId ?? startResponse?.TransactionId ?? null;
    await sendChargingState({
      protocol,
      client,
      connectorId,
      tag,
      transactionRef,
      state: "Charging",
      energyKwh: 0.6,
      powerKw: 7.2,
      seqNo: 1,
    });
    return;
  }

  transactionRef.transactionUid = guidLike();
  await client.call("TransactionEvent", {
    eventType: "Started",
    timestamp: nowIsoUtc(),
    triggerReason: "Authorized",
    seqNo: 0,
    idToken: { idToken: tag, type: "Central" },
    evse: { id: connectorId, connectorId },
    transactionInfo: { transactionId: transactionRef.transactionUid, remoteStartId: transactionRef.remoteStartId ?? 0, chargingState: "EVConnected" },
    meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 0, measurand: "Energy.Active.Import.Register", unitOfMeasure: { unit: "kWh" } }] }],
  });

  await sendChargingState({
    protocol,
    client,
    connectorId,
    tag,
    transactionRef,
    state: "Charging",
    energyKwh: 0.6,
    powerKw: 7.2,
    seqNo: 1,
  });
}

async function stopTransaction({ protocol, client, connectorId, tag, transactionRef }) {
  if (protocol === "1.6") {
    await client.call("StopTransaction", {
      transactionId: transactionRef.transactionId,
      meterStop: 1200,
      timestamp: nowIsoUtc(),
      reason: "Local",
    });
    await client.call("StatusNotification", {
      connectorId,
      errorCode: "NoError",
      status: "Finishing",
      timestamp: nowIsoUtc(),
    });
    return;
  }

  await client.call("TransactionEvent", {
    eventType: "Ended",
    timestamp: nowIsoUtc(),
    triggerReason: "RemoteStop",
    seqNo: 99,
    idToken: { idToken: tag, type: "Central" },
    evse: { id: connectorId, connectorId },
    transactionInfo: { transactionId: transactionRef.transactionUid, remoteStartId: transactionRef.remoteStartId ?? 0 },
    meterValue: [{ timestamp: nowIsoUtc(), sampledValue: [{ value: 1.2, measurand: "Energy.Active.Import.Register", unitOfMeasure: { unit: "kWh" } }] }],
  });

  await client.call("StatusNotification", {
    timestamp: nowIsoUtc(),
    connectorStatus: "Occupied",
    evseId: connectorId,
    connectorId,
  });
}

export function createProtocolScenarioDriver({
  protocol,
  chargePointId,
  connectorId,
  chargeTagId,
  serverWsBase,
  scenario,
  unplugDelayMs = 2500,
  idleTransitionDelayMs = 700,
}) {
  const normalizedProtocol = normalizeProtocol(protocol);
  const config = PROTOCOL_CONFIG[normalizedProtocol];
  const client = new OcppClient({
    url: `${serverWsBase}/${encodeURIComponent(chargePointId)}`,
    protocols: config.subProtocols,
    label: `${normalizedProtocol}:${chargePointId}`,
  });

  const state = {
    protocol: normalizedProtocol,
    scenario,
    chargePointId,
    connectorId,
    chargeTagId,
    activeTag: chargeTagId,
    remoteStartId: 0,
    remoteStartCount: 0,
    remoteStopCount: 0,
    startedAtUtc: null,
    stoppedAtUtc: null,
    unpluggedAtUtc: null,
    transactionId: null,
    transactionUid: null,
    summary: [],
  };

  let startedResolve;
  let finishedResolve;
  let idleResolve;
  const startedPromise = new Promise((resolve) => { startedResolve = resolve; });
  const finishedPromise = new Promise((resolve) => { finishedResolve = resolve; });
  const idlePromise = new Promise((resolve) => { idleResolve = resolve; });

  let idleTransitionSent = false;
  let stopHandled = false;
  let preparingSent = false;

  async function maybeEnterIdleState() {
    if (idleTransitionSent || (scenario !== "suspended_idle_then_unplug" && scenario !== "quiet_hours_idle_excluded")) {
      return;
    }

    idleTransitionSent = true;
    await sleep(idleTransitionDelayMs);
    const idleTimestamp = new Date(Date.now() - 90_000).toISOString().replace(/\.\d{3}Z$/, "Z");
    await sendChargingState({
      protocol: normalizedProtocol,
      client,
      connectorId,
      tag: state.activeTag,
      transactionRef: state,
      state: "SuspendedEV",
      energyKwh: 0.6,
      powerKw: 0.0,
      seqNo: 2,
      timestampIso: idleTimestamp,
    });
    state.summary.push({ event: "idle_entered", atUtc: idleTimestamp });
    idleResolve(state);
  }

  async function emitLiveProgress() {
    if (scenario !== "live_meter_progress") {
      return;
    }

    await sleep(800);
    await sendChargingState({
      protocol: normalizedProtocol,
      client,
      connectorId,
      tag: state.activeTag,
      transactionRef: state,
      state: "Charging",
      energyKwh: 1.2,
      powerKw: 11.0,
      seqNo: 3,
    });
    state.summary.push({ event: "live_progress", energyKwh: 1.2, atUtc: nowIsoUtc() });
  }

  client.onCall(config.remoteStartAction, async (_messageId, payload) => {
    const requestedTag = normalizedProtocol === "1.6"
      ? payload?.idTag
      : payload?.idToken?.idToken;
    if (requestedTag) {
      state.activeTag = requestedTag;
    }

    if (normalizedProtocol !== "1.6" && Number.isFinite(Number(payload?.remoteStartId))) {
      state.remoteStartId = Number(payload.remoteStartId);
    }

    state.remoteStartCount += 1;
    await startTransaction({
      protocol: normalizedProtocol,
      client,
      connectorId,
      tag: state.activeTag,
      transactionRef: state,
    });
    state.startedAtUtc = nowIsoUtc();
    startedResolve(state);
    await Promise.all([maybeEnterIdleState(), emitLiveProgress()]);
    return { status: "Accepted" };
  });

  client.onCall(config.remoteStopAction, async () => {
    if (stopHandled) {
      return { status: "Accepted" };
    }

    stopHandled = true;
    state.remoteStopCount += 1;
    await stopTransaction({
      protocol: normalizedProtocol,
      client,
      connectorId,
      tag: state.activeTag,
      transactionRef: state,
    });
    state.stoppedAtUtc = nowIsoUtc();
    state.summary.push({ event: "stopped", atUtc: state.stoppedAtUtc });

    void (async () => {
      await sleep(unplugDelayMs);
      try {
        await sendAvailable({ protocol: normalizedProtocol, client, connectorId });
      } catch (error) {
        if (!String(error?.message ?? "").includes("websocket not open")) {
          throw error;
        }
      }
      state.unpluggedAtUtc = nowIsoUtc();
      state.summary.push({ event: "unplugged", atUtc: state.unpluggedAtUtc });
      finishedResolve(state);
    })();

    return { status: "Accepted" };
  });

  client.onCall("UnlockConnector", async () => ({ status: "Unlocked" }));
  client.onCall("Reset", async () => ({ status: "Accepted" }));
  client.onCall("SetChargingProfile", async () => ({ status: "Accepted" }));
  client.onCall("ClearChargingProfile", async () => ({ status: "Accepted" }));

  return {
    state,
    async connect() {
      await client.connect();

      if (normalizedProtocol === "1.6") {
        const boot = await client.call("BootNotification", {
          chargePointVendor: "TestVendor",
          chargePointModel: "ScenarioDriver",
        });
        assert(boot?.status === "Accepted", `OCPP 1.6 BootNotification not accepted for ${chargePointId}`);
      } else {
        const boot = await client.call("BootNotification", {
          chargingStation: { model: "ScenarioDriver", vendorName: "TestVendor", firmwareVersion: "0.0.0" },
          reason: "PowerUp",
        });
        assert(boot?.status === "Accepted", `${normalizedProtocol} BootNotification not accepted for ${chargePointId}`);
      }

      await sendAvailable({ protocol: normalizedProtocol, client, connectorId });
    },
    waitUntilStarted(timeoutMs = 30000) {
      return waitFor(async () => {
        try {
          return await Promise.race([
            startedPromise,
            sleep(250).then(() => null),
          ]);
        } catch {
          return null;
        }
      }, {
        timeoutMs,
        errorMessage: `Timed out waiting for remote start on ${chargePointId}`,
      });
    },
    waitUntilFinished(timeoutMs = 30000) {
      return waitFor(async () => {
        try {
          return await Promise.race([
            finishedPromise,
            sleep(250).then(() => null),
          ]);
        } catch {
          return null;
        }
      }, {
        timeoutMs,
        errorMessage: `Timed out waiting for remote stop/unplug on ${chargePointId}`,
      });
    },
    waitUntilIdle(timeoutMs = 30000) {
      return waitFor(async () => {
        try {
          return await Promise.race([
            idlePromise,
            sleep(250).then(() => null),
          ]);
        } catch {
          return null;
        }
      }, {
        timeoutMs,
        errorMessage: `Timed out waiting for idle transition on ${chargePointId}`,
      });
    },
    close() {
      client.close();
    },
    async signalPluggedIn() {
      if (preparingSent) {
        return;
      }

      preparingSent = true;
      await sendPreparing({ protocol: normalizedProtocol, client, connectorId });
      state.summary.push({ event: "preparing", atUtc: nowIsoUtc() });
    },
  };
}

export async function queryReservationStatus({ serverApiBase, apiKey, reservationId }) {
  return httpJson(`${serverApiBase}/Payments/Status?reservationId=${encodeURIComponent(reservationId)}`, {
    headers: { "X-API-Key": apiKey },
  });
}

export async function runApiScenario({
  protocol,
  scenario,
  chargePointId,
  connectorId,
  chargeTagId,
  serverApiBase,
  serverWsBase,
  mgmtHttpBase,
  apiKey,
}) {
  const driver = createProtocolScenarioDriver({
    protocol,
    scenario,
    chargePointId,
    connectorId,
    chargeTagId,
    serverWsBase,
  });

  try {
    await driver.connect();

    const createResponse = await httpJson(`${serverApiBase}/Payments/Create`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-API-Key": apiKey,
      },
      body: JSON.stringify({
        chargePointId,
        connectorId,
        chargeTagId,
        origin: "public",
        returnBaseUrl: mgmtHttpBase,
      }),
    });

    assert(createResponse.ok, `Payments/Create failed for ${protocol}/${scenario}: ${createResponse.status}`);
    assert(createResponse.json?.status === "Redirect", `Expected Redirect for ${protocol}/${scenario}, got ${createResponse.json?.status}`);

    const reservationId = createResponse.json?.reservationId;
    const checkoutUrl = createResponse.json?.checkoutUrl;
    assert(typeof reservationId === "string", `Missing reservationId for ${protocol}/${scenario}`);
    assert(typeof checkoutUrl === "string" && checkoutUrl.startsWith("http"), `Missing checkoutUrl for ${protocol}/${scenario}`);

    const mockCheckoutResponse = await httpText(checkoutUrl);
    assert(mockCheckoutResponse.ok, `Mock checkout page failed for ${protocol}/${scenario}`);

    const successUrl = new URL(checkoutUrl).searchParams.get("successUrl");
    assert(successUrl, `Missing successUrl in mock checkout for ${protocol}/${scenario}`);

    const successResponse = await httpText(successUrl);
    assert(successResponse.ok, `Payments/Success failed for ${protocol}/${scenario}`);

    await driver.signalPluggedIn();
    await driver.waitUntilStarted();

    const chargingStatus = await waitFor(async () => {
      const response = await queryReservationStatus({ serverApiBase, apiKey, reservationId });
      return response.ok && response.json?.sessionStage === "charging" ? response.json : null;
    }, {
      timeoutMs: 30000,
      errorMessage: `Charging state not reached for ${protocol}/${scenario}`,
    });

    if (scenario === "suspended_idle_then_unplug" || scenario === "quiet_hours_idle_excluded") {
      await driver.waitUntilIdle();
    }

    const stopResponse = await httpJson(`${serverApiBase}/Payments/Stop`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-API-Key": apiKey,
      },
      body: JSON.stringify({ reservationId }),
    });
    assert(stopResponse.ok, `Payments/Stop failed for ${protocol}/${scenario}: ${stopResponse.status}`);

    const postStopStatus = await waitFor(async () => {
      const response = await queryReservationStatus({ serverApiBase, apiKey, reservationId });
      if (!response.ok) {
        return null;
      }

      const stage = response.json?.sessionStage;
      return stage === "waitingForDisconnect" || stage === "done" ? response.json : null;
    }, {
      timeoutMs: 30000,
      errorMessage: `Post-stop terminal transition not reached for ${protocol}/${scenario}`,
    });

    const waitingForDisconnectStatus = postStopStatus.sessionStage === "waitingForDisconnect"
      ? postStopStatus
      : null;

    if (scenario === "stop_then_unplug") {
      assert(waitingForDisconnectStatus, `WaitingForDisconnect state not reached for ${protocol}/${scenario}`);
    }

    await driver.waitUntilFinished();

    const finalStatus = await waitFor(async () => {
      const response = await queryReservationStatus({ serverApiBase, apiKey, reservationId });
      return response.ok && response.json?.sessionStage === "done" ? response.json : null;
    }, {
      timeoutMs: 30000,
      errorMessage: `Done state not reached for ${protocol}/${scenario}`,
    });

    const stopAt = toUtcMillis(finalStatus.stopTransactionAtUtc);
    const disconnectedAt = toUtcMillis(finalStatus.disconnectedAtUtc);
    assert(stopAt != null, `Missing stopTransactionAtUtc for ${protocol}/${scenario}`);
    assert(disconnectedAt != null, `Missing disconnectedAtUtc for ${protocol}/${scenario}`);
    assert(disconnectedAt >= stopAt, `Disconnect precedes stop for ${protocol}/${scenario}`);

    if (scenario === "suspended_idle_then_unplug") {
      const idleMinutes = Number(finalStatus.transactionIdleFeeMinutes ?? 0);
      const idleAmount = Number(finalStatus.transactionIdleFeeAmount ?? 0);
      assert(idleMinutes > 0 || idleAmount > 0, `Idle billing missing for ${protocol}/${scenario}`);
    }

    if (scenario === "quiet_hours_idle_excluded") {
      const idleMinutes = Number(finalStatus.transactionIdleFeeMinutes ?? 0);
      const idleAmount = Number(finalStatus.transactionIdleFeeAmount ?? 0);
      assert(finalStatus.idleBillingPausedByWindow === true, `Quiet-hours flag missing for ${protocol}/${scenario}`);
      assert(idleMinutes === 0 && idleAmount === 0, `Quiet-hours idle billing should be suppressed for ${protocol}/${scenario}`);
    }

    if (scenario === "live_meter_progress") {
      const liveEnergy = Number(chargingStatus.liveSessionEnergyKwh ?? 0);
      assert(liveEnergy > 0, `Live session energy did not progress for ${protocol}/${scenario}`);
    }

    return {
      protocol: normalizeProtocol(protocol),
      scenario,
      chargePointId,
      connectorId,
      reservationId,
      checkoutUrl,
      chargingStatus,
      waitingForDisconnectStatus,
      finalStatus,
      emulator: driver.state,
    };
  } finally {
    driver.close();
  }
}
