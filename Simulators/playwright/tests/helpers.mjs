import fs from "node:fs";
import path from "node:path";
import { execFileSync, spawn } from "node:child_process";
import { expect } from "@playwright/test";
import { createTempFilePath, readRuntimeInfo, sleep } from "../common.mjs";

export const protocolPresets = {
  "1.6": { chargePointId: process.env.CP16_ID ?? "Test1234", connectorId: 1 },
  "2.0.1": { chargePointId: process.env.CP20_ID ?? "TestAAA", connectorId: 2 },
  "2.1": { chargePointId: process.env.CP21_ID ?? process.env.CP20_ID ?? "TestAAA", connectorId: 2 },
};

export function runtimeInfo() {
  return readRuntimeInfo();
}

export async function startBrowserScenario(protocol, scenario) {
  const resultPath = createTempFilePath(`scenario-${protocol.replace(/\./g, "-")}-${scenario}`);
  const child = spawn("node", ["./runner.mjs", "--mode", "scenario", "--protocol", protocol, "--scenario", scenario, "--trigger", "browser", "--json-out", resultPath], {
    cwd: path.resolve(path.dirname(new URL(import.meta.url).pathname), ".."),
    env: {
      ...process.env,
      OCPP_PLAYWRIGHT_USE_EXISTING_STACK: "1",
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  let stdout = "";
  let stderr = "";

  child.stdout.on("data", (chunk) => {
    stdout += chunk.toString();
  });
  child.stderr.on("data", (chunk) => {
    stderr += chunk.toString();
  });

  const startedAt = Date.now();
  while (!stdout.includes("SCENARIO_READY")) {
    if (child.exitCode != null) {
      throw new Error(`Scenario exited before ready.\nSTDOUT:\n${stdout}\nSTDERR:\n${stderr}`);
    }

    if (Date.now() - startedAt > 30_000) {
      child.kill("SIGTERM");
      throw new Error(`Scenario did not become ready.\nSTDOUT:\n${stdout}\nSTDERR:\n${stderr}`);
    }

    await sleep(200);
  }

  return {
    child,
    resultPath,
    async waitForCompletion() {
      const exitCode = await new Promise((resolve) => {
        child.once("exit", resolve);
      });

      if (exitCode !== 0) {
        throw new Error(`Scenario failed with exit code ${exitCode}.\nSTDOUT:\n${stdout}\nSTDERR:\n${stderr}`);
      }

      return JSON.parse(fs.readFileSync(resultPath, "utf8"));
    },
  };
}

export async function startPublicSession(page, { chargePointId, connectorId }) {
  await page.goto(`/Public/Start?cp=${encodeURIComponent(chargePointId)}&conn=${connectorId}`);
  await page.locator("form .btn-primary").click();
  await page.locator("#mock-pay-now").click();
  await page.waitForURL(/\/Payments\/Status\?/);
}

export function currentReservationId(page) {
  const currentUrl = new URL(page.url());
  return currentUrl.searchParams.get("reservationId");
}

export async function waitForNonZeroEnergy(page) {
  await expect.poll(async () => {
    const value = ((await page.locator("#stat-energy").textContent()) ?? "").trim();
    return value;
  }, { timeout: 30_000 }).not.toMatch(/^(-|0(?:\.0)?)$/);
}

export function readLatestSinkEmail(eventName) {
  const runtime = runtimeInfo();
  const files = fs
    .readdirSync(runtime.emailSinkDir)
    .filter((entry) => entry.endsWith(".json"))
    .map((entry) => path.join(runtime.emailSinkDir, entry))
    .sort((left, right) => fs.statSync(left).mtimeMs - fs.statSync(right).mtimeMs);

  const matching = files
    .map((filePath) => JSON.parse(fs.readFileSync(filePath, "utf8")))
    .filter((payload) => payload.eventName === eventName);

  if (matching.length === 0) {
    throw new Error(`No ${eventName} email found in sink ${runtime.emailSinkDir}`);
  }

  return matching.at(-1);
}

function sqlQuote(value) {
  if (value === null || value === undefined) {
    return "NULL";
  }

  return `'${String(value).replaceAll("'", "''")}'`;
}

function executeSqlite(dbPath, sql) {
  return execFileSync("sqlite3", [dbPath, sql], {
    cwd: path.resolve(path.dirname(new URL(import.meta.url).pathname), "..", ".."),
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  }).trim();
}

export function readMockStripeSnapshot() {
  const runtime = runtimeInfo();
  const snapshotPath = runtime.stripeDiagnosticsDir
    ? path.join(runtime.stripeDiagnosticsDir, "mock-stripe-store.json")
    : "";

  if (!snapshotPath || !fs.existsSync(snapshotPath)) {
    return null;
  }

  return JSON.parse(fs.readFileSync(snapshotPath, "utf8"));
}

export function findMockStripeArtifactsByReservationId(reservationId) {
  const snapshot = readMockStripeSnapshot();
  if (!snapshot || !reservationId) {
    return { session: null, paymentIntent: null };
  }

  const session = (snapshot.sessions || []).find((entry) =>
    entry?.metadata?.reservation_id === reservationId);
  const paymentIntent = session?.paymentIntentId
    ? (snapshot.paymentIntents || []).find((entry) => entry?.id === session.paymentIntentId)
    : null;

  return { session: session ?? null, paymentIntent: paymentIntent ?? null };
}

export function readLatestInvoiceSubmissionLog(reservationId) {
  if (!reservationId) {
    return null;
  }

  const runtime = runtimeInfo();
  const sql = `
SELECT json_object(
  'invoiceSubmissionLogId', InvoiceSubmissionLogId,
  'reservationId', ReservationId,
  'transactionId', TransactionId,
  'provider', Provider,
  'mode', Mode,
  'status', Status,
  'invoiceKind', InvoiceKind,
  'stripeCheckoutSessionId', StripeCheckoutSessionId,
  'stripePaymentIntentId', StripePaymentIntentId,
  'httpStatusCode', HttpStatusCode,
  'externalDocumentId', ExternalDocumentId,
  'externalInvoiceNumber', ExternalInvoiceNumber,
  'externalPublicUrl', ExternalPublicUrl,
  'externalPdfUrl', ExternalPdfUrl,
  'providerResponseStatus', ProviderResponseStatus,
  'requestPayloadJson', RequestPayloadJson,
  'responseBody', ResponseBody,
  'error', Error,
  'createdAtUtc', CreatedAtUtc,
  'completedAtUtc', CompletedAtUtc
)
FROM InvoiceSubmissionLog
WHERE UPPER(ReservationId) = UPPER(${sqlQuote(reservationId)})
ORDER BY COALESCE(CompletedAtUtc, CreatedAtUtc) DESC, InvoiceSubmissionLogId DESC
LIMIT 1
`;

  const raw = executeSqlite(runtime.databasePath, sql);
  return raw ? JSON.parse(raw) : null;
}
