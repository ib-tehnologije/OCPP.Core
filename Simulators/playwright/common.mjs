import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import os from "node:os";

export const packageDir = path.dirname(fileURLToPath(import.meta.url));
export const repoRoot = path.resolve(packageDir, "..", "..");
export const runtimeDir = path.join(packageDir, ".runtime");
export const runtimeInfoPath = path.join(runtimeDir, "stack.json");
export const defaultServerPort = Number(process.env.OCPP_SERVER_PORT ?? "18081");
export const defaultManagementPort = Number(process.env.OCPP_MANAGEMENT_PORT ?? "18082");

export function nowIsoUtc() {
  return new Date().toISOString().replace(/\.\d{3}Z$/, "Z");
}

export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

export async function waitForUrl(url, timeoutMs = 120_000) {
  const startedAt = Date.now();
  let lastError = null;

  while (Date.now() - startedAt < timeoutMs) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }

      lastError = new Error(`Unexpected status ${response.status} for ${url}`);
    } catch (error) {
      lastError = error;
    }

    await sleep(500);
  }

  throw lastError ?? new Error(`Timed out waiting for ${url}`);
}

export function ensureCleanDirectory(dirPath) {
  fs.rmSync(dirPath, { recursive: true, force: true });
  fs.mkdirSync(dirPath, { recursive: true });
}

export function readRuntimeInfo() {
  return JSON.parse(fs.readFileSync(runtimeInfoPath, "utf8"));
}

export function writeRuntimeInfo(runtime) {
  fs.mkdirSync(runtimeDir, { recursive: true });
  fs.writeFileSync(runtimeInfoPath, JSON.stringify(runtime, null, 2));
}

export function createTempFilePath(prefix, extension = ".json") {
  fs.mkdirSync(runtimeDir, { recursive: true });
  return path.join(runtimeDir, `${prefix}-${Date.now()}-${Math.random().toString(16).slice(2)}${extension}`);
}

export function clearDirectoryContents(dirPath) {
  fs.mkdirSync(dirPath, { recursive: true });
  for (const entry of fs.readdirSync(dirPath)) {
    fs.rmSync(path.join(dirPath, entry), { recursive: true, force: true });
  }
}

export function runSqlite(dbPath, sql) {
  execFileSync("sqlite3", [dbPath, sql], {
    cwd: repoRoot,
    stdio: "pipe",
  });
}

export function setQuietHours(dbPath, enabled, window = null) {
  const escapedWindow = window ? window.replace(/'/g, "''") : null;
  const timestamp = new Date().toISOString().replace("T", " ").replace("Z", "");

  const statements = [
    "BEGIN TRANSACTION",
    "INSERT INTO PublicPortalSettings (PublicPortalSettingsId, BrandName, Tagline, SupportEmail, CreatedAtUtc, UpdatedAtUtc, IdleFeeExcludedWindowEnabled, IdleFeeExcludedWindow) " +
      `SELECT 1, 'OCPP Core', 'E2E portal', 'support@example.test', '${timestamp}', '${timestamp}', ${enabled ? 1 : 0}, ${escapedWindow ? `'${escapedWindow}'` : "NULL"} ` +
      "WHERE NOT EXISTS (SELECT 1 FROM PublicPortalSettings)",
    `UPDATE PublicPortalSettings SET UpdatedAtUtc='${timestamp}', IdleFeeExcludedWindowEnabled=${enabled ? 1 : 0}, IdleFeeExcludedWindow=${escapedWindow ? `'${escapedWindow}'` : "NULL"}`,
    "COMMIT",
  ].join(";");

  runSqlite(dbPath, statements);
}

export function resolveStackDefaults() {
  const serverBaseUrl = process.env.SERVER_HTTP_BASE ?? `http://127.0.0.1:${defaultServerPort}`;
  const managementBaseUrl = process.env.MGMT_HTTP_BASE ?? `http://127.0.0.1:${defaultManagementPort}`;

  return {
    serverBaseUrl,
    managementBaseUrl,
    serverApiBaseUrl: process.env.SERVER_API_BASE ?? `${serverBaseUrl}/API`,
    serverWsBaseUrl: process.env.SERVER_WS_BASE ?? `ws://127.0.0.1:${defaultServerPort}/OCPP`,
    apiKey: process.env.SERVER_API_KEY ?? "36029A5F-B736-4DA9-AE46-D66847C9062C",
    runtimeDir,
  };
}

export function createRuntimeMetadata() {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "ocpp-playwright-"));
  const databasePath = path.join(tempRoot, "OCPP.Core.playwright.sqlite");
  const emailSinkDir = path.join(tempRoot, "email-sink");
  const stripeDiagnosticsDir = path.join(tempRoot, "stripe-diagnostics");
  ensureCleanDirectory(emailSinkDir);
  ensureCleanDirectory(stripeDiagnosticsDir);

  return {
    ...resolveStackDefaults(),
    tempRoot,
    databasePath,
    emailSinkDir,
    stripeDiagnosticsDir,
  };
}
