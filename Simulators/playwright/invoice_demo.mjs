import { spawn } from "node:child_process";
import { once } from "node:events";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import {
  buildInvoiceDemoMockStripeSnapshot,
  invoiceDemoFixtures,
  seedInvoiceDemoFixtures,
} from "../lib/invoice_demo_fixtures.mjs";
import { seedTestStack } from "../lib/sqlite_helpers.mjs";
import { waitForUrl } from "./common.mjs";

const packageDir = path.dirname(fileURLToPath(import.meta.url));
const defaultRepoRoot = path.resolve(packageDir, "..", "..");
const sensitiveInheritedSettingPattern = /^(?:Invoices__ERacuni|Stripe__|Notifications__(?:Smtp|FromAddress|ReplyToAddress|BccAddress)|Email__|OwnerReportSchedule__)/i;

export const invoiceDemoMessages = Object.freeze({
  invalidOib: "Please enter a valid OIB (11 digits).",
  locked: "Buyer details cannot be changed after invoice submission. Contact support for a correction.",
  saved: "R1 details saved successfully.",
});

function realPathIncludingMissingSegments(inputPath) {
  let existingAncestor = path.resolve(inputPath);
  const missingSegments = [];
  while (!fs.existsSync(existingAncestor)) {
    const parent = path.dirname(existingAncestor);
    if (parent === existingAncestor) break;
    missingSegments.unshift(path.basename(existingAncestor));
    existingAncestor = parent;
  }

  const canonicalAncestor = fs.realpathSync(existingAncestor);
  return path.join(canonicalAncestor, ...missingSegments);
}

export function assertPrivateArtifactDirectory(repoRoot, artifactDir) {
  if (!artifactDir || !String(artifactDir).trim()) {
    throw new Error("INVOICE_DEMO_ARTIFACT_DIR must name a private directory outside the repository.");
  }

  const resolvedArtifactDir = path.resolve(artifactDir);
  const canonicalRepoRoot = realPathIncludingMissingSegments(repoRoot);
  const canonicalArtifactDir = realPathIncludingMissingSegments(resolvedArtifactDir);
  const relative = path.relative(canonicalRepoRoot, canonicalArtifactDir);
  if (relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative))) {
    throw new Error("Invoice demo artifacts must be written outside the repository.");
  }

  return resolvedArtifactDir;
}

export function buildDemoEnvironment(runtime, application = "server") {
  const targetUrl = application === "management" ? runtime.managementBaseUrl : runtime.serverBaseUrl;
  const environment = {
    ASPNETCORE_ENVIRONMENT: "Development",
    AutoMigrateDB: "true",
    ApiKey: runtime.apiKey,
    ConnectionStrings__SQLite: `Filename=${runtime.databasePath};foreign keys=True`,
    ConnectionStrings__SqlServer: "",
    DOTNET_ROLL_FORWARD: "Major",
    Invoices__Enabled: "false",
    Notifications__EnableCustomerEmails: "false",
    Notifications__FromAddress: "",
    Notifications__ReplyToAddress: "",
    Notifications__BccAddress: "",
    Notifications__Smtp__Host: "",
    Notifications__Smtp__Username: "",
    Notifications__Smtp__Password: "",
    Notifications__SinkDirectory: runtime.emailSinkDir,
    Email__EnableOwnerReportEmails: "false",
    Email__FromAddress: "",
    Email__ReplyToAddress: "",
    Email__Smtp__Host: "",
    Email__Smtp__Username: "",
    Email__Smtp__Password: "",
    OwnerReportSchedule__Enabled: "false",
    OwnerReportSchedule__SendTestTo: "",
    Stripe__Enabled: "true",
    Stripe__UseMockServices: "true",
    Stripe__MockCustomerEmail: "invoice-demo@example.test",
    Stripe__MockDiagnosticsDirectory: runtime.stripeDiagnosticsDir,
    Stripe__ApiKey: "mock_test_key",
    Stripe__ReturnBaseUrl: runtime.managementBaseUrl,
    Kestrel__Endpoints__Http__Url: targetUrl,
    Payments__IdleFeeExcludedWindow: "",
    Payments__IdleFeeExcludedTimeZoneId: "UTC",
  };

  if (application === "management") {
    environment.ServerApiUrl = runtime.serverApiBaseUrl;
  }

  return environment;
}

export function buildChromiumLaunchOptions({ bundledExists, chromeExists, headless = true }) {
  if (bundledExists) return { headless };
  if (chromeExists) return { channel: "chrome", headless };
  throw new Error("Playwright Chromium is not installed and no system Chrome fallback is available.");
}

export function installSignalAbortHandlers(processTarget = process) {
  const controller = new AbortController();
  const handlers = new Map(
    ["SIGINT", "SIGTERM"].map((signalName) => [signalName, () => {
      if (!controller.signal.aborted) {
        controller.abort(new Error(`Invoice demo interrupted by ${signalName}.`));
      }
    }]),
  );
  for (const [signalName, handler] of handlers) processTarget.once(signalName, handler);
  return {
    signal: controller.signal,
    dispose() {
      for (const [signalName, handler] of handlers) processTarget.removeListener(signalName, handler);
    },
  };
}

export function abortable(promise, signal) {
  if (!signal) return Promise.resolve(promise);
  if (signal.aborted) return Promise.reject(signal.reason);
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(signal.reason);
    signal.addEventListener("abort", onAbort, { once: true });
    Promise.resolve(promise).then(
      (value) => {
        signal.removeEventListener("abort", onAbort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener("abort", onAbort);
        reject(error);
      },
    );
  });
}

function processGroupExists(groupPid, kill) {
  try {
    kill(groupPid, 0);
    return true;
  } catch (error) {
    if (error?.code === "ESRCH") return false;
    throw error;
  }
}

async function waitForProcessGroupToDisappear(groupPid, kill, timeoutMs, pollIntervalMs) {
  const deadline = Date.now() + timeoutMs;
  while (processGroupExists(groupPid, kill)) {
    if (Date.now() >= deadline) return false;
    await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));
  }
  return true;
}

export async function stopProcessGroup(child, {
  kill = process.kill,
  termTimeoutMs = 3_000,
  killTimeoutMs = 3_000,
  pollIntervalMs = 50,
} = {}) {
  if (!child || !child.pid) return;
  const groupPid = process.platform === "win32" ? child.pid : -child.pid;
  try {
    kill(groupPid, "SIGTERM");
  } catch (error) {
    if (error?.code === "ESRCH") return;
    throw error;
  }
  if (await waitForProcessGroupToDisappear(groupPid, kill, termTimeoutMs, pollIntervalMs)) return;

  try {
    kill(groupPid, "SIGKILL");
  } catch (error) {
    if (error?.code === "ESRCH") return;
    throw error;
  }
  if (!await waitForProcessGroupToDisappear(groupPid, kill, killTimeoutMs, pollIntervalMs)) {
    throw new Error(`Process group ${groupPid} did not exit after SIGKILL.`);
  }
}

export function publishCompletedVideo(sourcePath, targetPath, {
  copyFileSync = fs.copyFileSync,
  removeSync = fs.rmSync,
  renameSync = fs.renameSync,
} = {}) {
  removeSync(targetPath, { force: true });
  try {
    renameSync(sourcePath, targetPath);
  } catch (error) {
    if (error?.code !== "EXDEV") throw error;
    copyFileSync(sourcePath, targetPath);
    removeSync(sourcePath, { force: true });
  }
}

function sanitizedParentEnvironment() {
  return Object.fromEntries(
    Object.entries(process.env).filter(([key]) => !sensitiveInheritedSettingPattern.test(key)),
  );
}

async function unusedLoopbackPort() {
  const server = net.createServer();
  server.unref();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : null;
  await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  if (!port) throw new Error("Unable to allocate a loopback port for the invoice demo.");
  return port;
}

async function createRuntime(artifactDir) {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "ocpp-invoice-demo-"));
  const serverPort = await unusedLoopbackPort();
  const managementPort = await unusedLoopbackPort();
  const serverBaseUrl = `http://127.0.0.1:${serverPort}`;
  const managementBaseUrl = `http://127.0.0.1:${managementPort}`;
  const runtime = {
    apiKey: "invoice-demo-local-api-key",
    artifactDir,
    databasePath: path.join(tempRoot, "OCPP.Core.invoice-demo.sqlite"),
    emailSinkDir: path.join(tempRoot, "email-sink"),
    managementBaseUrl,
    serverApiBaseUrl: `${serverBaseUrl}/API`,
    serverBaseUrl,
    stripeDiagnosticsDir: path.join(tempRoot, "stripe-diagnostics"),
    tempRoot,
  };
  fs.mkdirSync(runtime.emailSinkDir, { recursive: true });
  fs.mkdirSync(runtime.stripeDiagnosticsDir, { recursive: true });
  fs.writeFileSync(
    path.join(runtime.stripeDiagnosticsDir, "mock-stripe-store.json"),
    `${JSON.stringify(buildInvoiceDemoMockStripeSnapshot(), null, 2)}\n`,
  );
  return runtime;
}

function spawnApplication(repoRoot, artifactDir, projectPath, environment, logName) {
  const log = fs.createWriteStream(path.join(artifactDir, logName), { flags: "w" });
  const child = spawn("dotnet", ["run", "--project", projectPath], {
    cwd: repoRoot,
    detached: process.platform !== "win32",
    env: { ...sanitizedParentEnvironment(), ...environment },
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stdout.pipe(log);
  child.stderr.pipe(log);
  child.once("exit", () => log.end());
  return child;
}

async function waitForApplication(url, child, name) {
  await Promise.race([
    waitForUrl(url),
    once(child, "exit").then(([code, signal]) => {
      throw new Error(`${name} exited before becoming ready (code ${code ?? "none"}, signal ${signal ?? "none"}).`);
    }),
  ]);
}

async function stopChildren(children) {
  await Promise.all(children.map((child) => stopProcessGroup(child)));
}

async function addCaption(page, text) {
  await page.evaluate((caption) => {
    let element = document.getElementById("invoice-demo-caption");
    if (!element) {
      element = document.createElement("div");
      element.id = "invoice-demo-caption";
      Object.assign(element.style, {
        position: "fixed",
        left: "50%",
        bottom: "24px",
        transform: "translateX(-50%)",
        zIndex: "2147483647",
        maxWidth: "80%",
        padding: "12px 18px",
        borderRadius: "10px",
        background: "rgba(12, 18, 28, 0.9)",
        color: "white",
        font: "600 18px/1.35 system-ui, sans-serif",
        boxShadow: "0 4px 18px rgba(0, 0, 0, 0.35)",
        textAlign: "center",
      });
      document.body.appendChild(element);
    }
    element.textContent = caption;
  }, text);
  await page.waitForTimeout(700);
}

async function waitForExactText(page, selector, expectedText) {
  await page.waitForFunction(
    ({ expected, target }) => document.querySelector(target)?.textContent?.trim() === expected,
    { expected: expectedText, target: selector },
  );
}

async function recordBrowserWalkthrough(runtime, signal) {
  if (signal?.aborted) throw signal.reason;
  const { chromium } = await import("playwright");
  const chromeExecutablePath = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
  const browser = await chromium.launch(buildChromiumLaunchOptions({
    bundledExists: fs.existsSync(chromium.executablePath()),
    chromeExists: fs.existsSync(chromeExecutablePath),
    headless: process.env.INVOICE_DEMO_HEADLESS !== "0",
  }));
  if (signal?.aborted) {
    await browser.close();
    throw signal.reason;
  }
  const context = await browser.newContext({
    baseURL: runtime.managementBaseUrl,
    locale: "en-US",
    recordVideo: { dir: runtime.tempRoot, size: { width: 1440, height: 900 } },
    viewport: { width: 1440, height: 900 },
  });
  const closeOnAbort = () => {
    void context.close().catch(() => {}).finally(() => browser.close().catch(() => {}));
  };
  signal?.addEventListener("abort", closeOnAbort, { once: true });
  const page = await context.newPage();
  const screenshots = [];
  let screenshotNumber = 0;
  const capture = async (slug) => {
    screenshotNumber += 1;
    const filename = `${String(screenshotNumber).padStart(2, "0")}-${slug}.png`;
    await page.screenshot({ path: path.join(runtime.artifactDir, filename), fullPage: true });
    screenshots.push(filename);
  };
  const statusUrl = (fixture) => `/Payments/Status?reservationId=${fixture.reservationId}&origin=public&lang=en`;
  const fillBuyer = async (buyer) => {
    await page.locator("#r1-country").selectOption(buyer.country);
    await page.locator("#r1-company").fill(buyer.companyName);
    await page.locator("#r1-street").fill(buyer.street);
    await page.locator("#r1-postal-code").fill(buyer.postalCode);
    await page.locator("#r1-city").fill(buyer.city);
    await page.locator("#r1-email").fill(buyer.email);
    await page.locator("#r1-tax-identifier").fill(buyer.taxIdentifier);
    if (buyer.registrationNumber) await page.locator("#r1-registration-number").fill(buyer.registrationNumber);
    if (buyer.identifierIsVatRegistration) await page.locator("#r1-vat-registration").check();
    await page.locator("#r1-confirm").check();
  };

  try {
    await page.goto(`/Public/Start?cp=${encodeURIComponent(invoiceDemoFixtures.foreignEditable.chargePointId)}&conn=1`);
    await page.locator("#wantsR1").check();
    await addCaption(page, "Choose Company invoice before starting the charging session.");
    await capture("company-invoice-choice");

    const foreign = invoiceDemoFixtures.foreignEditable;
    await page.goto(statusUrl(foreign));
    await fillBuyer(foreign.buyer);
    await addCaption(page, "Review Czech company details exactly as they will appear on the invoice.");
    await capture("czech-company-review");
    await page.locator("#r1-submit").click();
    await waitForExactText(page, "#r1-result", invoiceDemoMessages.saved);
    await addCaption(page, "The reviewed Czech company details are saved to the reservation.");
    await capture("czech-company-saved");

    const croatian = invoiceDemoFixtures.croatianEditable;
    await page.goto(statusUrl(croatian));
    await fillBuyer({ ...croatian.buyer, taxIdentifier: croatian.buyer.invalidOib });
    await page.locator("#r1-submit").click();
    await waitForExactText(page, "#r1-result", invoiceDemoMessages.invalidOib);
    await addCaption(page, "An invalid Croatian OIB is rejected before invoice details are saved.");
    await capture("croatian-invalid-oib");
    await page.locator("#r1-tax-identifier").fill(croatian.buyer.taxIdentifier);
    await page.locator("#r1-submit").click();
    await waitForExactText(page, "#r1-result", invoiceDemoMessages.saved);
    await addCaption(page, "A checksum-valid Croatian OIB passes validation and can be saved.");
    await capture("croatian-valid-oib");

    await page.goto(statusUrl(invoiceDemoFixtures.foreignLocked));
    await page.locator("#r1-submit:disabled").waitFor();
    await waitForExactText(page, "#r1-result", invoiceDemoMessages.locked);
    await addCaption(page, "After invoice issuance, buyer fields are locked against later changes.");
    await capture("issued-invoice-locked");

    const video = page.video();
    await context.close();
    const videoPath = await video.path();
    const walkthroughPath = path.join(runtime.artifactDir, "walkthrough.webm");
    publishCompletedVideo(videoPath, walkthroughPath);
    return { screenshots, video: path.basename(walkthroughPath) };
  } finally {
    signal?.removeEventListener("abort", closeOnAbort);
    await context.close().catch(() => {});
    await browser.close().catch(() => {});
  }
}

export async function runInvoiceDemo({
  repoRoot = defaultRepoRoot,
  artifactDir = process.env.INVOICE_DEMO_ARTIFACT_DIR,
} = {}) {
  const privateArtifactDir = assertPrivateArtifactDirectory(repoRoot, artifactDir);
  fs.mkdirSync(privateArtifactDir, { recursive: true });
  const runtime = await createRuntime(privateArtifactDir);
  const children = [];
  const signalHandlers = installSignalAbortHandlers();
  const { signal } = signalHandlers;

  try {
    const server = spawnApplication(
      repoRoot,
      privateArtifactDir,
      "OCPP.Core.Server/OCPP.Core.Server.csproj",
      buildDemoEnvironment(runtime, "server"),
      "server.log",
    );
    const management = spawnApplication(
      repoRoot,
      privateArtifactDir,
      "OCPP.Core.Management/OCPP.Core.Management.csproj",
      buildDemoEnvironment(runtime, "management"),
      "management.log",
    );
    children.push(server, management);
    await abortable(Promise.all([
      waitForApplication(`${runtime.serverBaseUrl}/`, server, "server"),
      waitForApplication(`${runtime.managementBaseUrl}/Public/Map`, management, "management"),
    ]), signal);
    await abortable(seedTestStack(runtime.databasePath), signal);
    await abortable(seedInvoiceDemoFixtures(runtime.databasePath), signal);

    const browserArtifacts = await abortable(recordBrowserWalkthrough(runtime, signal), signal);
    const manifest = {
      createdAtUtc: new Date().toISOString(),
      privacy: "local-only; mock Stripe; invoices and customer email disabled",
      runtime: {
        managementBaseUrl: runtime.managementBaseUrl,
        serverBaseUrl: runtime.serverBaseUrl,
      },
      artifacts: {
        ...browserArtifacts,
        logs: ["server.log", "management.log"],
      },
      fixtures: Object.fromEntries(
        Object.entries(invoiceDemoFixtures).map(([name, fixture]) => [name, fixture.reservationId]),
      ),
    };
    fs.writeFileSync(path.join(privateArtifactDir, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
    return manifest;
  } finally {
    signalHandlers.dispose();
    await stopChildren(children);
    fs.rmSync(runtime.tempRoot, { recursive: true, force: true });
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isMain) {
  runInvoiceDemo()
    .then((manifest) => console.log(JSON.stringify(manifest, null, 2)))
    .catch((error) => {
      console.error(error);
      process.exitCode = 1;
    });
}
