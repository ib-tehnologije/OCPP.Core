import fs from "node:fs/promises";
import { httpJson, httpText } from "./lib/test_support.mjs";
import { runApiScenario } from "./lib/protocol_scenarios.mjs";
import { setIdleWindowForScenario } from "./lib/sqlite_helpers.mjs";

const MGMT_HTTP_BASE = process.env.MGMT_HTTP_BASE ?? "http://127.0.0.1:8082";
const SERVER_HTTP_BASE = process.env.SERVER_HTTP_BASE ?? "http://127.0.0.1:8081";
const SERVER_API_BASE = process.env.SERVER_API_BASE ?? `${SERVER_HTTP_BASE}/API`;
const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? "ws://127.0.0.1:8081/OCPP";
const SERVER_API_KEY = process.env.SERVER_API_KEY ?? "36029A5F-B736-4DA9-AE46-D66847C9062C";
const SQLITE_DB_PATH = process.env.SQLITE_DB_PATH ?? "";
const OUTPUT_FILE = process.env.OCPP_SCENARIO_OUTPUT ?? "";

const protocolTargets = [
  { protocol: "1.6", chargePointId: process.env.CP16_ID ?? "Test1234", connectorId: Number(process.env.CP16_CONNECTOR_ID ?? "1"), chargeTagId: process.env.CP16_TAG ?? "PAY16" },
  { protocol: "2.0.1", chargePointId: process.env.CP20_ID ?? "TestAAA", connectorId: Number(process.env.CP20_CONNECTOR_ID ?? "2"), chargeTagId: process.env.CP20_TAG ?? "PAY20" },
  { protocol: "2.1", chargePointId: process.env.CP21_ID ?? "TestBBB", connectorId: Number(process.env.CP21_CONNECTOR_ID ?? "3"), chargeTagId: process.env.CP21_TAG ?? "PAY21" },
];

const scenarios = ["stop_then_unplug", "suspended_idle_then_unplug", "quiet_hours_idle_excluded", "live_meter_progress"];

async function smokeHttp() {
  const mapRes = await httpText(`${MGMT_HTTP_BASE}/Public/Map`);
  const startRes = await httpText(`${MGMT_HTTP_BASE}/Public/Start?cp=${encodeURIComponent(protocolTargets[0].chargePointId)}&conn=1`);
  const statusRes = await httpJson(`${SERVER_API_BASE}/Status`, {
    headers: { "X-API-Key": SERVER_API_KEY },
  });

  return {
    managementMapOk: mapRes.ok,
    managementStartOk: startRes.ok,
    serverStatusOk: statusRes.ok,
    onlineChargePoints: Array.isArray(statusRes.json) ? statusRes.json.length : 0,
  };
}

async function main() {
  const summary = {
    startedAtUtc: new Date().toISOString(),
    smoke: await smokeHttp(),
    scenarios: [],
  };

  for (const target of protocolTargets) {
    for (const scenario of scenarios) {
      if (scenario === "quiet_hours_idle_excluded" && !SQLITE_DB_PATH) {
        throw new Error("SQLITE_DB_PATH is required for quiet_hours_idle_excluded");
      }

      await setIdleWindowForScenario(SQLITE_DB_PATH, scenario);

      const result = await runApiScenario({
        protocol: target.protocol,
        scenario,
        chargePointId: target.chargePointId,
        connectorId: target.connectorId,
        chargeTagId: target.chargeTagId,
        serverApiBase: SERVER_API_BASE,
        serverWsBase: SERVER_WS_BASE,
        mgmtHttpBase: MGMT_HTTP_BASE,
        apiKey: SERVER_API_KEY,
      });

      summary.scenarios.push(result);
    }
  }

  if (SQLITE_DB_PATH) {
    await setIdleWindowForScenario(SQLITE_DB_PATH, "reset");
  }

  summary.finishedAtUtc = new Date().toISOString();

  const serialized = JSON.stringify(summary, null, 2);
  if (OUTPUT_FILE) {
    await fs.writeFile(OUTPUT_FILE, serialized, "utf8");
  }

  console.log(serialized);
}

main().catch((error) => {
  console.error(error?.stack ?? String(error));
  process.exit(1);
});
