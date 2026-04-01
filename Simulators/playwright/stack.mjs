import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import {
  createRuntimeMetadata,
  nowIsoUtc,
  waitForUrl,
  writeRuntimeInfo,
  packageDir,
  repoRoot,
} from "./common.mjs";
import { seedTestStack } from "../lib/sqlite_helpers.mjs";

const runtime = createRuntimeMetadata();
const invoicesEnabled = process.env.OCPP_PLAYWRIGHT_ENABLE_INVOICES === "1";
runtime.invoicesEnabled = invoicesEnabled;
writeRuntimeInfo(runtime);

const childProcesses = [];
let stopStackResolve = () => {};
const stopStack = new Promise((resolve) => {
  stopStackResolve = resolve;
});

function spawnDotnet(projectPath, extraEnv) {
  const sqliteConnectionString = `Filename=${runtime.databasePath};foreign keys=True`;
  const invoiceEnv = invoicesEnabled
    ? {
        Invoices__Enabled: process.env.Invoices__Enabled ?? "true",
        Invoices__ERacuni__DocumentStatus: process.env.Invoices__ERacuni__DocumentStatus ?? "Draft",
        Invoices__ERacuni__LineItems__Energy__ProductCode: process.env.Invoices__ERacuni__LineItems__Energy__ProductCode ?? "EV-ENERGY",
        Invoices__ERacuni__LineItems__SessionFee__ProductCode: process.env.Invoices__ERacuni__LineItems__SessionFee__ProductCode ?? "EV-SESSION",
        Invoices__ERacuni__LineItems__UsageFee__ProductCode: process.env.Invoices__ERacuni__LineItems__UsageFee__ProductCode ?? "EV-OCCUPANCY",
        Invoices__ERacuni__LineItems__IdleFee__ProductCode: process.env.Invoices__ERacuni__LineItems__IdleFee__ProductCode ?? "EV-IDLE",
      }
    : {
        Invoices__Enabled: "false",
      };

  const child = spawn("dotnet", ["run", "--project", projectPath], {
    cwd: repoRoot,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      ConnectionStrings__SQLite: sqliteConnectionString,
      ConnectionStrings__SqlServer: "",
      AutoMigrateDB: "true",
      ApiKey: runtime.apiKey,
      Notifications__EnableCustomerEmails: "true",
      Notifications__SinkDirectory: runtime.emailSinkDir,
      Stripe__Enabled: "true",
      Stripe__UseMockServices: "true",
      Stripe__MockCustomerEmail: "driver@example.test",
      Stripe__MockDiagnosticsDirectory: runtime.stripeDiagnosticsDir,
      Stripe__ApiKey: "mock_test_key",
      Stripe__ReturnBaseUrl: runtime.managementBaseUrl,
      Payments__IdleFeeExcludedWindow: "",
      Payments__IdleFeeExcludedTimeZoneId: "UTC",
      ...invoiceEnv,
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
  stopStackResolve();

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

async function seedDatabase() {
  await seedTestStack(runtime.databasePath, {
    cp16Id: "Test1234",
    cp20Id: "TestAAA",
    cp21Id: "TestBBB",
  });
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
await seedDatabase();

const readyPayload = {
  ...runtime,
  startedAtUtc: nowIsoUtc(),
  packageDir,
};
writeRuntimeInfo(readyPayload);
console.log("OCPP_PLAYWRIGHT_STACK_READY");
console.log(JSON.stringify(readyPayload, null, 2));

await stopStack;
