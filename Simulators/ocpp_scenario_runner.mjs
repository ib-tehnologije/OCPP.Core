import { runApiScenario } from "./lib/protocol_scenarios.mjs";
import { setIdleWindowForScenario } from "./lib/sqlite_helpers.mjs";

const SERVER_HTTP_BASE = process.env.SERVER_HTTP_BASE ?? "http://localhost:8081";
const SERVER_API_BASE = process.env.SERVER_API_BASE ?? `${SERVER_HTTP_BASE}/API`;
const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? "ws://localhost:8081/OCPP";
const MGMT_HTTP_BASE = process.env.MGMT_HTTP_BASE ?? "http://127.0.0.1:8082";
const SERVER_API_KEY = process.env.SERVER_API_KEY ?? "36029A5F-B736-4DA9-AE46-D66847C9062C";
const SQLITE_DB_PATH = process.env.SQLITE_DB_PATH ?? "";

const PROTOCOL_TARGETS = {
  "1.6": {
    chargePointId: process.env.CP16_ID ?? "Test1234",
    connectorId: Number(process.env.CP16_CONNECTOR_ID ?? "1"),
    chargeTagId: process.env.CP16_TAG ?? "PAY16",
  },
  "2.0.1": {
    chargePointId: process.env.CP20_ID ?? "TestAAA",
    connectorId: Number(process.env.CP20_CONNECTOR_ID ?? "2"),
    chargeTagId: process.env.CP20_TAG ?? "PAY20",
  },
  "2.1": {
    chargePointId: process.env.CP21_ID ?? "TestBBB",
    connectorId: Number(process.env.CP21_CONNECTOR_ID ?? "3"),
    chargeTagId: process.env.CP21_TAG ?? "PAY21",
  },
};

function parseArgs(argv) {
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

function normalizeProtocol(protocol) {
  const value = String(protocol ?? "1.6").trim();
  if (value === "ocpp1.6") return "1.6";
  if (value === "ocpp2.0.1") return "2.0.1";
  if (value === "ocpp2.1") return "2.1";
  if (PROTOCOL_TARGETS[value]) return value;
  throw new Error(`Unsupported protocol '${protocol}'.`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const protocol = normalizeProtocol(args.protocol);
  const scenario = String(args.scenario ?? "stop_then_unplug").trim();
  const target = PROTOCOL_TARGETS[protocol];

  if (scenario === "quiet_hours_idle_excluded" && !SQLITE_DB_PATH) {
    throw new Error("SQLITE_DB_PATH is required for quiet_hours_idle_excluded");
  }

  await setIdleWindowForScenario(SQLITE_DB_PATH, scenario);

  try {
    const result = await runApiScenario({
      protocol,
      scenario,
      chargePointId: target.chargePointId,
      connectorId: target.connectorId,
      chargeTagId: target.chargeTagId,
      serverApiBase: SERVER_API_BASE,
      serverWsBase: SERVER_WS_BASE,
      mgmtHttpBase: MGMT_HTTP_BASE,
      apiKey: SERVER_API_KEY,
    });

    console.log(JSON.stringify(result, null, 2));
  } finally {
    await setIdleWindowForScenario(SQLITE_DB_PATH, "reset");
  }
}

main().catch((error) => {
  console.error(error?.stack ?? String(error));
  process.exit(1);
});
