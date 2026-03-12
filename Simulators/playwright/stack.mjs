import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import {
  assert,
  createRuntimeMetadata,
  nowIsoUtc,
  runSqlite,
  waitForUrl,
  writeRuntimeInfo,
  packageDir,
  repoRoot,
} from "./common.mjs";

const runtime = createRuntimeMetadata();
writeRuntimeInfo(runtime);

const childProcesses = [];

function spawnDotnet(projectPath, extraEnv) {
  const child = spawn("dotnet", ["run", "--project", projectPath], {
    cwd: repoRoot,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      ConnectionStrings__SQLite: runtime.databasePath,
      ConnectionStrings__SqlServer: "",
      AutoMigrateDB: "true",
      ApiKey: runtime.apiKey,
      Notifications__EnableCustomerEmails: "true",
      Notifications__SinkDirectory: runtime.emailSinkDir,
      Stripe__Enabled: "true",
      Stripe__UseMockServices: "true",
      Stripe__MockCustomerEmail: "driver@example.test",
      Stripe__ApiKey: "mock_test_key",
      Stripe__ReturnBaseUrl: runtime.managementBaseUrl,
      Invoices__Enabled: "false",
      Payments__IdleFeeExcludedWindow: "",
      Payments__IdleFeeExcludedTimeZoneId: "UTC",
      ...extraEnv,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  const logFile = path.join(runtime.tempRoot, `${path.basename(projectPath)}.log`);
  const writeStream = fs.createWriteStream(logFile, { flags: "a" });
  child.stdout.pipe(writeStream);
  child.stderr.pipe(writeStream);
  childProcesses.push(child);
  return child;
}

function shutdown(exitCode = 0) {
  for (const child of childProcesses) {
    if (!child.killed) {
      child.kill("SIGTERM");
    }
  }

  setTimeout(() => {
    for (const child of childProcesses) {
      if (!child.killed) {
        child.kill("SIGKILL");
      }
    }
    process.exit(exitCode);
  }, 2_000).unref();
}

function seedDatabase() {
  const sql = [
    "BEGIN TRANSACTION",
    "INSERT OR REPLACE INTO ChargePoint (ChargePointId, Name, FreeChargingEnabled, PricePerKwh, UserSessionFee, OwnerSessionFee, OwnerCommissionPercent, OwnerCommissionFixedPerKwh, MaxSessionKwh, StartUsageFeeAfterMinutes, MaxUsageFeeMinutes, ConnectorUsageFeePerMinute, UsageFeeAfterChargingEnds) VALUES " +
      "('Test1234', 'Playwright OCPP 1.6', 0, 0.38, 0.50, 0.00, 0.00, 0.00, 80.0, 1, 120, 0.20, 1)," +
      "('TestAAA', 'Playwright OCPP 2.x', 0, 0.38, 0.50, 0.00, 0.00, 0.00, 80.0, 1, 120, 0.20, 1)",
    "INSERT OR REPLACE INTO ConnectorStatus (ChargePointId, ConnectorId, ConnectorName, LastStatus, LastStatusTime) VALUES " +
      "('Test1234', 1, 'Connector 1', 'Available', datetime('now'))," +
      "('TestAAA', 2, 'Connector 2', 'Available', datetime('now'))",
    "INSERT OR REPLACE INTO PublicPortalSettings (PublicPortalSettingsId, BrandName, Tagline, SupportEmail, QrScannerEnabled, CreatedAtUtc, UpdatedAtUtc, IdleFeeExcludedWindowEnabled, IdleFeeExcludedWindow) VALUES " +
      "(1, 'OCPP Core', 'Local E2E portal', 'support@example.test', 1, datetime('now'), datetime('now'), 0, NULL)",
    "COMMIT",
  ].join(";");

  runSqlite(runtime.databasePath, sql);
}

process.on("SIGINT", () => shutdown(0));
process.on("SIGTERM", () => shutdown(0));

for (const signal of ["uncaughtException", "unhandledRejection"]) {
  process.on(signal, (error) => {
    console.error(error);
    shutdown(1);
  });
}

const server = spawnDotnet("OCPP.Core.Server/OCPP.Core.Server.csproj", {
  Kestrel__Endpoints__Http__Url: runtime.serverBaseUrl,
});

const management = spawnDotnet("OCPP.Core.Management/OCPP.Core.Management.csproj", {
  Kestrel__Endpoints__Http__Url: runtime.managementBaseUrl,
  ServerApiUrl: runtime.serverApiBaseUrl,
});

for (const child of [server, management]) {
  child.once("exit", (code) => {
    console.error(`Test stack child exited early with code ${code ?? -1}`);
    shutdown(code ?? 1);
  });
}

await waitForUrl(`${runtime.serverBaseUrl}/`);
await waitForUrl(`${runtime.managementBaseUrl}/Public/Map`);
seedDatabase();

const readyPayload = {
  ...runtime,
  startedAtUtc: nowIsoUtc(),
  packageDir,
};
writeRuntimeInfo(readyPayload);
console.log("OCPP_PLAYWRIGHT_STACK_READY");
console.log(JSON.stringify(readyPayload, null, 2));

await new Promise(() => {});
