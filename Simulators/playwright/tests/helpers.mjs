import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
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
