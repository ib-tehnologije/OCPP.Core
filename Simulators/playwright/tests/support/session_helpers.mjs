import fs from "node:fs/promises";
import path from "node:path";
import { createProtocolScenarioDriver } from "../../../lib/protocol_scenarios.mjs";
import { setIdleWindow } from "../../../lib/sqlite_helpers.mjs";

const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? "ws://127.0.0.1:8081/OCPP";
const SQLITE_DB_PATH = process.env.SQLITE_DB_PATH ?? "";
const EMAIL_SINK_DIR = process.env.EMAIL_SINK_DIR ?? "";

export function targetForProtocol(protocol) {
  switch (protocol) {
    case "1.6":
      return {
        protocol,
        chargePointId: process.env.CP16_ID ?? "Test1234",
        connectorId: Number(process.env.CP16_CONNECTOR_ID ?? "1"),
        chargeTagId: process.env.CP16_TAG ?? "PAY16",
      };
    case "2.0.1":
      return {
        protocol,
        chargePointId: process.env.CP20_ID ?? "TestAAA",
        connectorId: Number(process.env.CP20_CONNECTOR_ID ?? "2"),
        chargeTagId: process.env.CP20_TAG ?? "PAY20",
      };
    case "2.1":
      return {
        protocol,
        chargePointId: process.env.CP21_ID ?? "TestBBB",
        connectorId: Number(process.env.CP21_CONNECTOR_ID ?? "3"),
        chargeTagId: process.env.CP21_TAG ?? "PAY21",
      };
    default:
      throw new Error(`Unsupported protocol '${protocol}'`);
  }
}

export async function withDriver(target, scenario, callback) {
  const driver = createProtocolScenarioDriver({
    protocol: target.protocol,
    scenario,
    chargePointId: target.chargePointId,
    connectorId: target.connectorId,
    chargeTagId: target.chargeTagId,
    serverWsBase: SERVER_WS_BASE,
  });

  await driver.connect();
  try {
    return await callback(driver);
  } finally {
    if (driver.state.remoteStopCount > 0 && !driver.state.unpluggedAtUtc) {
      try {
        await driver.waitUntilFinished(10_000);
      } catch {
        // Best-effort cleanup for tests that assert the waiting state before unplug.
      }
    }
    driver.close();
  }
}

export async function startPublicSession(page, target) {
  await page.goto(`/Public/Start?cp=${encodeURIComponent(target.chargePointId)}&conn=${encodeURIComponent(target.connectorId)}`);
  await page.getByRole("button", { name: /Start charging/i }).click();
  await page.locator("#mock-pay-now").click();
  await page.waitForURL(/\/Payments\/Status\?/);
  const currentUrl = new URL(page.url());
  return currentUrl.searchParams.get("reservationId");
}

export async function setQuietWindowAroundNow(enabled) {
  if (!SQLITE_DB_PATH) {
    throw new Error("SQLITE_DB_PATH is required for quiet-hours browser tests");
  }

  if (!enabled) {
    await setIdleWindow(SQLITE_DB_PATH, { enabled: false, window: null });
    return;
  }

  const now = new Date();
  const start = new Date(now.getTime() - 5 * 60 * 1000);
  const end = new Date(now.getTime() + 5 * 60 * 1000);
  const pad = (value) => String(value).padStart(2, "0");
  const window = `${pad(start.getUTCHours())}:${pad(start.getUTCMinutes())}-${pad(end.getUTCHours())}:${pad(end.getUTCMinutes())}`;
  await setIdleWindow(SQLITE_DB_PATH, { enabled: true, window });
}

export async function readLatestEmail({ eventName, reservationId }) {
  if (!EMAIL_SINK_DIR) {
    throw new Error("EMAIL_SINK_DIR is required for email browser tests");
  }

  const fileNames = (await fs.readdir(EMAIL_SINK_DIR))
    .filter((name) => name.endsWith(".json"))
    .sort();

  for (const fileName of fileNames.reverse()) {
    const filePath = path.join(EMAIL_SINK_DIR, fileName);
    const payload = JSON.parse(await fs.readFile(filePath, "utf8"));
    if (eventName && payload.eventName !== eventName) {
      continue;
    }

    if (reservationId && payload.reservationId !== reservationId) {
      continue;
    }

    return payload;
  }

  return null;
}
