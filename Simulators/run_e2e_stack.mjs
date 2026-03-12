import fs from "node:fs";
import fsPromises from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { httpText, waitFor } from "./lib/test_support.mjs";
import { seedTestStack } from "./lib/sqlite_helpers.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..");

const SERVER_HTTP_BASE = process.env.SERVER_HTTP_BASE ?? "http://127.0.0.1:8081";
const MGMT_HTTP_BASE = process.env.MGMT_HTTP_BASE ?? "http://127.0.0.1:8082";
const SERVER_API_BASE = process.env.SERVER_API_BASE ?? `${SERVER_HTTP_BASE}/API`;
const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? "ws://127.0.0.1:8081/OCPP";
const SERVER_API_KEY = process.env.SERVER_API_KEY ?? "36029A5F-B736-4DA9-AE46-D66847C9062C";
const RUN_SIMULATORS = process.env.RUN_SIMULATORS !== "0";
const RUN_PLAYWRIGHT = process.env.RUN_PLAYWRIGHT !== "0";

async function createRuntimePaths() {
  const root = await fsPromises.mkdtemp(path.join(os.tmpdir(), "ocpp-e2e-"));
  return {
    root,
    sqlitePath: path.join(root, "ocpp-e2e.sqlite"),
    emailSinkDir: path.join(root, "emails"),
    logsDir: path.join(root, "logs"),
    scenarioSummaryPath: path.join(root, "scenario-summary.json"),
  };
}

function spawnLoggedProcess(command, args, { env, logFile, cwd = repoRoot }) {
  const child = spawn(command, args, {
    cwd,
    env,
    stdio: ["ignore", "pipe", "pipe"],
  });

  const logStream = fs.createWriteStream(logFile, { flags: "a" });
  child.stdout.pipe(logStream);
  child.stderr.pipe(logStream);

  child.on("exit", () => {
    logStream.end();
  });

  return child;
}

async function waitForUrl(url, label) {
  await waitFor(async () => {
    try {
      const response = await httpText(url);
      return response.ok ? response : null;
    } catch {
      return null;
    }
  }, {
    timeoutMs: 90000,
    intervalMs: 1000,
    errorMessage: `Timed out waiting for ${label} at ${url}`,
  });
}

async function runCommand(command, args, env, cwd = repoRoot) {
  await new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd,
      env,
      stdio: "inherit",
    });

    child.on("exit", (code) => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`${command} ${args.join(" ")} failed with exit code ${code}`));
      }
    });
  });
}

async function main() {
  const runtime = await createRuntimePaths();
  await fsPromises.mkdir(runtime.logsDir, { recursive: true });
  await fsPromises.mkdir(runtime.emailSinkDir, { recursive: true });

  const commonEnv = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: "Development",
    ConnectionStrings__SqlServer: "",
    ConnectionStrings__SQLite: `Filename=${runtime.sqlitePath};foreign keys=True`,
    SERVER_API_KEY,
  };

  const serverEnv = {
    ...commonEnv,
    Stripe__Enabled: "true",
    Stripe__UseMockServices: "true",
    Stripe__ApiKey: "test",
    Stripe__ReturnBaseUrl: MGMT_HTTP_BASE,
    Stripe__MockCustomerEmail: "driver@example.test",
    Notifications__EnableCustomerEmails: "true",
    Notifications__SinkDirectory: runtime.emailSinkDir,
    Invoices__Enabled: "false",
    Payments__IdleFeeExcludedTimeZoneId: "UTC",
    Kestrel__Endpoints__Http__Url: SERVER_HTTP_BASE,
  };

  const managementEnv = {
    ...commonEnv,
    ServerApiUrl: SERVER_API_BASE,
    Kestrel__Endpoints__Http__Url: MGMT_HTTP_BASE,
  };

  const serverProcess = spawnLoggedProcess("dotnet", ["run", "--project", "OCPP.Core.Server"], {
    env: serverEnv,
    logFile: path.join(runtime.logsDir, "server.log"),
  });

  const cleanup = () => {
    serverProcess.kill("SIGTERM");
    managementProcess?.kill("SIGTERM");
  };

  let managementProcess = null;
  process.on("exit", cleanup);
  process.on("SIGINT", () => {
    cleanup();
    process.exit(130);
  });
  process.on("SIGTERM", () => {
    cleanup();
    process.exit(143);
  });

  try {
    await waitForUrl(`${SERVER_HTTP_BASE}/`, "server");
    await seedTestStack(runtime.sqlitePath, {
      cp16Id: process.env.CP16_ID ?? "Test1234",
      cp20Id: process.env.CP20_ID ?? "TestAAA",
      cp21Id: process.env.CP21_ID ?? "TestBBB",
    });

    managementProcess = spawnLoggedProcess("dotnet", ["run", "--project", "OCPP.Core.Management"], {
      env: managementEnv,
      logFile: path.join(runtime.logsDir, "management.log"),
    });

    await waitForUrl(`${MGMT_HTTP_BASE}/Public/Map`, "management");

    if (RUN_SIMULATORS) {
      await runCommand("node", ["Simulators/e2e_smoke_test.mjs"], {
        ...commonEnv,
        SERVER_HTTP_BASE,
        MGMT_HTTP_BASE,
        SERVER_API_BASE,
        SERVER_WS_BASE,
        SERVER_API_KEY,
        SQLITE_DB_PATH: runtime.sqlitePath,
        OCPP_SCENARIO_OUTPUT: runtime.scenarioSummaryPath,
      });
    }

    if (RUN_PLAYWRIGHT) {
      await runCommand("npm", ["test", "--prefix", "Simulators/playwright"], {
        ...commonEnv,
        SERVER_HTTP_BASE,
        MGMT_HTTP_BASE,
        SERVER_API_BASE,
        SERVER_WS_BASE,
        SERVER_API_KEY,
        SQLITE_DB_PATH: runtime.sqlitePath,
        EMAIL_SINK_DIR: runtime.emailSinkDir,
      });
    }

    console.log(JSON.stringify({
      runtimeRoot: runtime.root,
      sqlitePath: runtime.sqlitePath,
      emailSinkDir: runtime.emailSinkDir,
      logsDir: runtime.logsDir,
      scenarioSummaryPath: runtime.scenarioSummaryPath,
    }, null, 2));
  } finally {
    cleanup();
  }
}

main().catch((error) => {
  console.error(error?.stack ?? String(error));
  process.exit(1);
});
