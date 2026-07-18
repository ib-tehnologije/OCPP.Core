import { spawn, spawnSync } from "node:child_process";
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
import { createProtocolScenarioDriver } from "../lib/protocol_scenarios.mjs";
import { querySqliteJson, seedTestStack } from "../lib/sqlite_helpers.mjs";
import { waitForUrl } from "./common.mjs";

const packageDir = path.dirname(fileURLToPath(import.meta.url));
const defaultRepoRoot = path.resolve(packageDir, "..", "..");
const sensitiveInheritedSettingPattern = /^(?:SENTRY_DSN|Sentry__|Invoices__ERacuni|Stripe__|Notifications__(?:Smtp|FromAddress|ReplyToAddress|BccAddress)|Email__|OwnerReportSchedule__)/i;
const expectedScreenshotNames = Object.freeze([
  "01-company-invoice-choice.png",
  "02-foreign-unconfirmed-blocked.png",
  "03-mock-stripe-handoff.png",
  "04-croatian-invalid-oib-rejected.png",
  "05-croatian-valid-oib-ready.png",
  "06-new-browser-session-empty.png",
  "07-issued-invoice-read-only.png",
]);

const buyerFormFields = Object.freeze({
  buyerCity: "#buyerCity",
  buyerCompanyName: "#buyerCompanyName",
  buyerEmail: "#buyerEmail",
  buyerPostalCode: "#buyerPostalCode",
  buyerRegistrationNumber: "#buyerRegistrationNumber",
  buyerStreet: "#buyerStreet",
  buyerTaxIdentifier: "#buyerTaxIdentifier",
});

export const invoiceDemoMessages = Object.freeze({
  checkoutReady: "Invoice buyer details are confirmed before Stripe checkout.",
  postCheckoutReadOnly: "The status page shows invoice results without late buyer editing.",
});

export const invoiceDemoRecordingContract = Object.freeze({
  billingExplainer: Object.freeze({
    filename: "billing-rules-explainer.webm",
    maximumDurationSeconds: 180,
    minimumDurationSeconds: 60,
  }),
  uiWalkthrough: Object.freeze({
    filename: "ui-walkthrough.webm",
    maximumDurationSeconds: 360,
    minimumDurationSeconds: 180,
  }),
});

export const invoiceDemoOverlayIds = Object.freeze({
  caption: "invoice-demo-caption",
  cursor: "invoice-demo-cursor",
  explainer: "invoice-demo-explainer",
});

export function buildBuyerEntrySteps(buyer) {
  const type = (selector, label, value) => ({
    action: "type",
    dwellAfterMs: 2_000,
    label,
    selector,
    typingDelayMs: 80,
    value,
  });
  return [
    { action: "select", dwellAfterMs: 2_000, label: "Country", selector: "#buyerCountry", value: buyer.country },
    type("#buyerCompanyName", "Company name", buyer.companyName),
    type("#buyerStreet", "Street and number", buyer.street),
    type("#buyerPostalCode", "Postal code", buyer.postalCode),
    type("#buyerCity", "City", buyer.city),
    type("#buyerEmail", "Billing email", buyer.email),
    type("#buyerTaxIdentifier", buyer.country === "HR" ? "Croatian OIB" : "VAT or tax identifier", buyer.taxIdentifier),
    type("#buyerRegistrationNumber", "Registration number", buyer.registrationNumber ?? ""),
    { action: "check", dwellAfterMs: 2_000, label: "VAT registration", selector: "#buyerIdentifierIsVatRegistration", value: buyer.identifierIsVatRegistration },
    { action: "check", dwellAfterMs: 2_000, label: "Confirm reviewed details", selector: "#buyerDataConfirmed", value: true },
  ];
}

export function buildBillingExplainerSections() {
  return [
    {
      dwellMs: 15_000,
      id: "billing-context",
      text: "Billing rules explainer — these are backend decisions after a charging session, not extra form fields on this page.",
    },
    {
      dwellMs: 16_000,
      id: "below-one-kwh",
      text: "Example A: below 1 kWh means no charge, no invoice, and no email. The backend suppresses the customer billing flow.",
    },
    {
      dwellMs: 16_000,
      id: "normal-billing",
      text: "Example B: at or above 1 kWh uses the normal billing path. The customer sees the ordinary payment and invoice journey.",
    },
    {
      dwellMs: 18_000,
      id: "provider-minimum-guard",
      text: "Defensive fallback: if a remaining positive amount is below the payment provider minimum, the Stripe minimum-charge guard prevents an invalid capture attempt.",
    },
    {
      dwellMs: 15_000,
      id: "ui-versus-backend",
      text: "Visible UI versus backend: the Company invoice choice and buyer review are visible here; energy thresholds and provider limits are enforced behind the scenes.",
    },
    {
      dwellMs: 12_000,
      id: "billing-recap",
      text: "Recap: under 1 kWh is suppressed, 1 kWh or more is billed normally, and the provider-minimum guard is only a final defensive fallback.",
    },
  ];
}

export async function moveCursorTo(page, locator, { click = false } = {}) {
  await locator.scrollIntoViewIfNeeded();
  const box = await locator.boundingBox();
  if (!box) throw new Error("Cannot move the demo cursor to an element without a visible bounding box.");
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y, { steps: 24 });
  if (click) await page.mouse.click(x, y, { delay: 180 });
}

export async function activateWithVisibleCursor(page, locator) {
  await moveCursorTo(page, locator);
  await locator.click();
}

export async function performBuyerEntry(page, buyer, { onStep = async () => {} } = {}) {
  const steps = buildBuyerEntrySteps(buyer);
  for (const step of steps) {
    const locator = page.locator(step.selector);
    await onStep(step);
    if (step.action === "select") {
      await moveCursorTo(page, locator, { click: true });
      await locator.selectOption(step.value);
    } else if (step.action === "type") {
      await moveCursorTo(page, locator, { click: true });
      await locator.fill("");
      if (step.value) await locator.pressSequentially(step.value, { delay: step.typingDelayMs });
    } else if (step.action === "check") {
      const checked = await locator.isChecked();
      if (Boolean(step.value) !== checked) await activateWithVisibleCursor(page, locator);
    }
    await page.waitForTimeout(step.dwellAfterMs);
  }
  return steps;
}

export function buildInteractionTimeline() {
  return [
    { id: "public-start", minimumDwellMs: 5_000 },
    { id: "company-invoice-choice", minimumDwellMs: 5_000 },
    { id: "czech-entry", minimumDwellMs: 5_000 },
    { id: "czech-review", minimumDwellMs: 8_000 },
    { id: "czech-confirmed", minimumDwellMs: 8_000 },
    { id: "croatian-invalid-oib", minimumDwellMs: 8_000 },
    { id: "croatian-valid-oib-ready", minimumDwellMs: 8_000 },
    { id: "issued-read-only", minimumDwellMs: 10_000 },
  ];
}

export function verifyRecordingDurations({ billingExplainerSeconds, uiWalkthroughSeconds }) {
  const ui = invoiceDemoRecordingContract.uiWalkthrough;
  if (uiWalkthroughSeconds < ui.minimumDurationSeconds || uiWalkthroughSeconds > ui.maximumDurationSeconds) {
    throw new Error(`UI walkthrough duration must be between ${ui.minimumDurationSeconds} and ${ui.maximumDurationSeconds} seconds.`);
  }
  const billing = invoiceDemoRecordingContract.billingExplainer;
  if (billingExplainerSeconds < billing.minimumDurationSeconds || billingExplainerSeconds > billing.maximumDurationSeconds) {
    throw new Error(`Billing explainer duration must be between ${billing.minimumDurationSeconds} and ${billing.maximumDurationSeconds} seconds.`);
  }
  return {
    billingExplainerDurationAccepted: true,
    billingExplainerSeconds,
    uiWalkthroughDurationAccepted: true,
    uiWalkthroughSeconds,
  };
}

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

function existingAncestor(inputPath) {
  let ancestor = path.resolve(inputPath);
  while (!fs.existsSync(ancestor)) {
    const parent = path.dirname(ancestor);
    if (parent === ancestor) break;
    ancestor = parent;
  }
  return fs.realpathSync(ancestor);
}

export function isInsideGitRepository(inputPath, { run = spawnSync } = {}) {
  const result = run("git", ["-C", existingAncestor(inputPath), "rev-parse", "--git-dir"], {
    encoding: "utf8",
    stdio: "pipe",
  });
  if (result.error) throw result.error;
  return result.status === 0;
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
  if (isInsideGitRepository(canonicalArtifactDir)) {
    throw new Error("Invoice demo artifacts cannot be written inside any Git repository or worktree.");
  }

  return resolvedArtifactDir;
}

export function buildDemoEnvironment(runtime, application = "server") {
  const targetUrl = application === "management" ? runtime.managementBaseUrl : runtime.serverBaseUrl;
  const environment = {
    ASPNETCORE_ENVIRONMENT: "InvoiceDemo",
    DOTNET_ENVIRONMENT: "InvoiceDemo",
    AutoMigrateDB: "true",
    ApiKey: runtime.apiKey,
    ConnectionStrings__SQLite: `Filename=${runtime.databasePath};foreign keys=True`,
    ConnectionStrings__SqlServer: "",
    DOTNET_ROLL_FORWARD: "Major",
    SENTRY_DSN: "",
    Sentry__Dsn: "",
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
  if (headless && chromeExists) return { channel: "chrome", headless };
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

export function sanitizeParentEnvironment(source = process.env) {
  return Object.fromEntries(
    Object.entries(source).filter(([key]) => !sensitiveInheritedSettingPattern.test(key)),
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
    serverWsBase: `ws://127.0.0.1:${serverPort}/OCPP`,
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

export async function connectInvoiceDemoChargePoint(runtime, factory = createProtocolScenarioDriver) {
  const driver = factory({
    chargePointId: invoiceDemoFixtures.foreignEditable.chargePointId,
    chargeTagId: "DEMO-TAG-1",
    connectorId: invoiceDemoFixtures.foreignEditable.connectorId,
    protocol: "1.6",
    scenario: "live_meter_progress",
    serverWsBase: runtime.serverWsBase,
  });
  try {
    await driver.connect();
    return driver;
  } catch (error) {
    driver.close?.();
    throw error;
  }
}

export function buildDotnetRunArgs(projectPath) {
  return ["run", "--no-build", "--project", projectPath];
}

export function buildLocalApplications(repoRoot, { run = spawnSync } = {}) {
  const result = run("dotnet", ["build", "OCPP.Core.sln"], {
    cwd: repoRoot,
    env: { ...sanitizeParentEnvironment(), DOTNET_ROLL_FORWARD: "Major" },
    encoding: "utf8",
    stdio: "inherit",
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Local invoice demo build failed with exit code ${result.status}.`);
}

function spawnApplication(repoRoot, artifactDir, projectPath, environment, logName) {
  const log = fs.createWriteStream(path.join(artifactDir, logName), { flags: "w" });
  const child = spawn("dotnet", buildDotnetRunArgs(projectPath), {
    cwd: repoRoot,
    detached: process.platform !== "win32",
    env: { ...sanitizeParentEnvironment(), ...environment },
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

export async function cleanupRuntime(children, tempRoot, {
  remove = fs.rmSync,
  stop = stopChildren,
} = {}) {
  let stopError;
  let removeError;
  try {
    await stop(children);
  } catch (error) {
    stopError = error;
  }
  try {
    remove(tempRoot, { recursive: true, force: true });
  } catch (error) {
    removeError = error;
  }
  if (stopError && removeError) {
    throw new AggregateError([stopError, removeError], "Child shutdown and runtime deletion both failed.");
  }
  if (stopError) throw stopError;
  if (removeError) throw removeError;
}

export function verifyNoPostCheckoutBuyerControls(controlCount) {
  if (controlCount !== 0) {
    throw new Error(`Expected no post-checkout buyer controls, found ${controlCount}.`);
  }
  return {
    postCheckoutBuyerControlsAbsent: true,
  };
}

export function readMockStripeSnapshotCounts(stripeDiagnosticsDir) {
  const snapshotPath = path.join(stripeDiagnosticsDir, "mock-stripe-store.json");
  const snapshot = JSON.parse(fs.readFileSync(snapshotPath, "utf8"));
  if (!Array.isArray(snapshot.sessions) || !Array.isArray(snapshot.paymentIntents)) {
    throw new Error("Mock Stripe snapshot must contain session and payment intent arrays.");
  }
  return {
    paymentIntentCount: snapshot.paymentIntents.length,
    sessionCount: snapshot.sessions.length,
  };
}

export function verifyNoMockStripeCreateCall({ after, before, reason }) {
  const created = after - before;
  if (created !== 0) {
    throw new Error(`${reason} created ${created} mock Stripe session${Math.abs(created) === 1 ? "" : "s"}; expected the attempt to stop before Stripe.`);
  }
  return {
    blockedBeforeMockStripe: true,
    mockStripeSessionCount: after,
  };
}

export async function inspectBuyerBrowserState(page, { buyerValues = [] } = {}) {
  const storage = await page.evaluate(() => ({
    localStorageEntries: Object.entries(window.localStorage),
    sessionStorageEntries: Object.entries(window.sessionStorage),
  }));
  const normalizedBuyerValues = buyerValues
    .filter((value) => typeof value === "string" && value.trim())
    .map((value) => value.trim().toLocaleLowerCase("en-US"));
  const storageEntries = [
    ...storage.localStorageEntries,
    ...storage.sessionStorageEntries,
  ];
  const retainedStorageEntry = storageEntries.find(([key, value]) => {
    const normalizedKey = String(key).toLocaleLowerCase("en-US");
    const normalizedValue = String(value).toLocaleLowerCase("en-US");
    return /invoice.?buyer|buyer.?(?:company|data|email|tax)/i.test(normalizedKey) ||
      normalizedBuyerValues.some((buyerValue) => normalizedValue.includes(buyerValue));
  });
  if (retainedStorageEntry) {
    throw new Error(`Buyer data remained in browser storage key ${retainedStorageEntry[0]}.`);
  }

  for (const [fieldName, selector] of Object.entries(buyerFormFields)) {
    const value = await page.locator(selector).inputValue();
    if (String(value ?? "").trim()) {
      throw new Error(`Fresh browser session repopulated ${fieldName}.`);
    }
  }

  return {
    buyerBrowserStorageAbsent: true,
    freshStartBuyerFieldsEmpty: true,
    localStorageKeys: storage.localStorageEntries.map(([key]) => key),
    sessionStorageKeys: storage.sessionStorageEntries.map(([key]) => key),
  };
}

export function concatenateRecordedVideos(sourcePaths, targetPath, {
  concatListPath = `${targetPath}.concat.txt`,
  remove = fs.rmSync,
  run = spawnSync,
  write = fs.writeFileSync,
} = {}) {
  if (!Array.isArray(sourcePaths) || sourcePaths.length < 2) {
    throw new Error("At least two recorded video segments are required for concatenation.");
  }
  const concatList = sourcePaths
    .map((sourcePath) => `file '${String(sourcePath).replaceAll("'", "'\\''")}'`)
    .join("\n") + "\n";
  write(concatListPath, concatList);
  try {
    const result = run("ffmpeg", [
      "-y", "-f", "concat", "-safe", "0", "-i", concatListPath,
      "-c", "copy", targetPath,
    ], { encoding: "utf8", stdio: "pipe" });
    if (result.error) throw result.error;
    if (result.status !== 0) {
      throw new Error(`ffmpeg failed to concatenate the UI walkthrough: ${String(result.stderr).trim() || `exit ${result.status}`}`);
    }
  } finally {
    remove(concatListPath, { force: true });
  }
  return targetPath;
}

export function readMediaDurationSeconds(filePath, { run = spawnSync } = {}) {
  const result = run("ffprobe", [
    "-v", "error",
    "-show_entries", "format=duration",
    "-of", "default=noprint_wrappers=1:nokey=1",
    filePath,
  ], { encoding: "utf8", stdio: "pipe" });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`ffprobe failed for ${path.basename(filePath)}: ${String(result.stderr).trim() || `exit ${result.status}`}`);
  }
  const duration = Number.parseFloat(String(result.stdout).trim());
  if (!Number.isFinite(duration) || duration <= 0) {
    throw new Error(`ffprobe returned an invalid duration for ${path.basename(filePath)}.`);
  }
  return duration;
}

export function verifyArtifactFiles(artifactDir, browserArtifacts, { readDuration = readMediaDurationSeconds } = {}) {
  if (browserArtifacts.videos?.uiWalkthrough !== invoiceDemoRecordingContract.uiWalkthrough.filename) {
    throw new Error(`Expected ${invoiceDemoRecordingContract.uiWalkthrough.filename} from the invoice demo recording.`);
  }
  if (browserArtifacts.videos?.billingExplainer !== invoiceDemoRecordingContract.billingExplainer.filename) {
    throw new Error(`Expected ${invoiceDemoRecordingContract.billingExplainer.filename} from the invoice demo recording.`);
  }
  if (browserArtifacts.screenshots.length !== expectedScreenshotNames.length ||
      expectedScreenshotNames.some((name, index) => browserArtifacts.screenshots[index] !== name)) {
    throw new Error(`Expected invoice demo screenshots: ${expectedScreenshotNames.join(", ")}.`);
  }
  for (const screenshot of expectedScreenshotNames) {
    if (fs.statSync(path.join(artifactDir, screenshot)).size <= 0) {
      throw new Error(`Screenshot ${screenshot} must be nonempty.`);
    }
  }
  for (const video of Object.values(browserArtifacts.videos)) {
    if (fs.statSync(path.join(artifactDir, video)).size <= 0) {
      throw new Error(`Video ${video} must be nonempty.`);
    }
  }
  const durationVerification = verifyRecordingDurations({
    billingExplainerSeconds: readDuration(path.join(artifactDir, browserArtifacts.videos.billingExplainer)),
    uiWalkthroughSeconds: readDuration(path.join(artifactDir, browserArtifacts.videos.uiWalkthrough)),
  });
  return {
    ...durationVerification,
    screenshotsNonEmpty: true,
    verifiedScreenshotCount: expectedScreenshotNames.length,
    videosNonEmpty: true,
  };
}

export function buildArtifactManifest({
  artifactFiles,
  browserArtifacts,
  createdAtUtc = new Date().toISOString(),
  persistedBuyerSnapshots,
  runtime,
}) {
  return {
    createdAtUtc,
    privacy: "local-only; mock Stripe; invoices and customer email disabled",
    runtime: {
      managementBaseUrl: runtime.managementBaseUrl,
      serverBaseUrl: runtime.serverBaseUrl,
    },
    artifacts: {
      logs: ["server.log", "management.log"],
      screenshots: browserArtifacts.screenshots,
      videos: browserArtifacts.videos,
      viewingOrder: [
        browserArtifacts.videos.uiWalkthrough,
        browserArtifacts.videos.billingExplainer,
      ],
    },
    supersededArtifacts: [{
      filename: "walkthrough.webm",
      reason: "Rejected rapid montage; not suitable as the primary human walkthrough.",
    }],
    verification: {
      ...persistedBuyerSnapshots,
      ...browserArtifacts.postCheckoutBuyerEntry,
      ...browserArtifacts.evidence,
      ...artifactFiles,
    },
    fixtures: Object.fromEntries(
      Object.entries(invoiceDemoFixtures).map(([name, fixture]) => [name, fixture.reservationId]),
    ),
  };
}

const persistedBuyerColumns = Object.freeze({
  country: "InvoiceBuyerCountry",
  companyName: "InvoiceBuyerCompanyName",
  street: "InvoiceBuyerStreet",
  postalCode: "InvoiceBuyerPostalCode",
  city: "InvoiceBuyerCity",
  email: "InvoiceBuyerEmail",
  taxIdentifier: "InvoiceBuyerTaxIdentifier",
  registrationNumber: "InvoiceBuyerRegistrationNumber",
  identifierIsVatRegistration: "InvoiceBuyerIdentifierIsVatRegistration",
});

export async function verifyPersistedBuyerSnapshots(databasePath, { query = querySqliteJson } = {}) {
  const fixtures = [
    ["Czech", invoiceDemoFixtures.foreignEditable],
    ["Croatian", invoiceDemoFixtures.croatianEditable],
  ];
  const rows = await query(databasePath, `
SELECT ReservationId,
       ConnectorId,
       InvoiceBuyerCountry,
       InvoiceBuyerCompanyName,
       InvoiceBuyerStreet,
       InvoiceBuyerPostalCode,
       InvoiceBuyerCity,
       InvoiceBuyerEmail,
       InvoiceBuyerTaxIdentifier,
       InvoiceBuyerRegistrationNumber,
       InvoiceBuyerIdentifierIsVatRegistration,
       InvoiceBuyerConfirmedAtUtc
FROM ChargePaymentReservation
WHERE ChargePointId = 'INVOICE-DEMO-LOCAL'
  AND ConnectorId IN (1, 2)
  AND InvoiceBuyerConfirmedAtUtc IS NOT NULL
ORDER BY CreatedAtUtc DESC;`);
  const rowsByConnector = new Map();
  for (const row of rows) {
    const connectorId = Number(row.ConnectorId);
    if (!rowsByConnector.has(connectorId)) rowsByConnector.set(connectorId, row);
  }

  for (const [label, fixture] of fixtures) {
    const row = rowsByConnector.get(fixture.connectorId);
    if (!row) throw new Error(`${label} buyer snapshot is missing from SQLite.`);
    for (const [fixtureField, column] of Object.entries(persistedBuyerColumns)) {
      const expected = fixture.buyer[fixtureField] ?? null;
      let actual = row[column] ?? null;
      if (fixtureField === "identifierIsVatRegistration") {
        if (![0, 1, false, true].includes(row[column])) {
          throw new Error(`${label} buyer snapshot ${column} must be persisted as a boolean.`);
        }
        actual = Boolean(row[column]);
      }
      if (actual !== expected) {
        throw new Error(`${label} buyer snapshot ${column} mismatch: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}.`);
      }
    }
    if (!row.InvoiceBuyerConfirmedAtUtc) {
      throw new Error(`${label} buyer snapshot InvoiceBuyerConfirmedAtUtc must be persisted.`);
    }
  }
  return {
    croatianBuyerSnapshotPersisted: true,
    czechBuyerSnapshotPersisted: true,
  };
}

async function ensurePresentationOverlays(page) {
  await page.evaluate((ids) => {
    let element = document.getElementById(ids.caption);
    if (!element) {
      element = document.createElement("div");
      element.id = ids.caption;
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
    let cursor = document.getElementById(ids.cursor);
    if (!cursor) {
      cursor = document.createElement("div");
      cursor.id = ids.cursor;
      Object.assign(cursor.style, {
        position: "fixed",
        left: "24px",
        top: "24px",
        width: "20px",
        height: "20px",
        border: "3px solid white",
        borderRadius: "50%",
        background: "#2563eb",
        boxShadow: "0 2px 8px rgba(0, 0, 0, 0.55)",
        pointerEvents: "none",
        transform: "translate(-50%, -50%)",
        transition: "left 80ms linear, top 80ms linear",
        zIndex: "2147483647",
      });
      document.body.appendChild(cursor);
      document.addEventListener("mousemove", (event) => {
        cursor.style.left = `${event.clientX}px`;
        cursor.style.top = `${event.clientY}px`;
      });
    }
  }, invoiceDemoOverlayIds);
}

async function addCaption(page, text, dwellMs = 4_000) {
  await ensurePresentationOverlays(page);
  await page.evaluate(({ captionId, caption }) => {
    document.getElementById(captionId).textContent = caption;
  }, { captionId: invoiceDemoOverlayIds.caption, caption: text });
  await page.waitForTimeout(dwellMs);
}

async function showExplainerSection(page, section) {
  await ensurePresentationOverlays(page);
  await page.evaluate(({ explainerId, text }) => {
    let element = document.getElementById(explainerId);
    if (!element) {
      element = document.createElement("div");
      element.id = explainerId;
      Object.assign(element.style, {
        position: "fixed",
        left: "50%",
        top: "50%",
        transform: "translate(-50%, -50%)",
        zIndex: "2147483646",
        width: "min(860px, 82vw)",
        padding: "34px 42px",
        border: "2px solid rgba(255, 255, 255, 0.9)",
        borderRadius: "18px",
        background: "rgba(15, 23, 42, 0.95)",
        color: "white",
        font: "700 30px/1.4 system-ui, sans-serif",
        boxShadow: "0 16px 50px rgba(0, 0, 0, 0.55)",
        textAlign: "left",
      });
      document.body.appendChild(element);
    }
    element.textContent = text;
  }, { explainerId: invoiceDemoOverlayIds.explainer, text: section.text });
  await page.waitForTimeout(section.dwellMs);
}

async function waitForExactText(page, selector, expectedText) {
  await page.waitForFunction(
    ({ expected, target }) => document.querySelector(target)?.textContent?.trim() === expected,
    { expected: expectedText, target: selector },
  );
}

async function createRecordedPage(browser, runtime) {
  const context = await browser.newContext({
    baseURL: runtime.managementBaseUrl,
    locale: "en-US",
    recordVideo: { dir: runtime.tempRoot, size: { width: 1440, height: 900 } },
    viewport: { width: 1440, height: 900 },
  });
  const page = await context.newPage();
  return { context, page };
}

async function publishRecordedPage(context, page, runtime, filename) {
  const video = page.video();
  await context.close();
  const videoPath = await video.path();
  const targetPath = path.join(runtime.artifactDir, filename);
  publishCompletedVideo(videoPath, targetPath);
  return path.basename(targetPath);
}

async function recordUiWalkthrough(browser, runtime) {
  const screenshots = [];
  const capture = async (page, filename) => {
    await page.screenshot({ path: path.join(runtime.artifactDir, filename), fullPage: true });
    screenshots.push(filename);
  };
  const statusUrl = (fixture) => `/Payments/Status?reservationId=${fixture.reservationId}&origin=public&lang=en`;
  const firstSegmentFilename = "ui-walkthrough-part-1.webm";
  const secondSegmentFilename = "ui-walkthrough-part-2.webm";
  let unconfirmedAttemptBlockedBeforeMockStripe = false;
  let invalidOibAttemptBlockedBeforeMockStripe = false;
  let mockStripeHandoffShown = false;
  let croatianValidOibAcceptedBeforeMockStripe = false;

  const first = await createRecordedPage(browser, runtime);
  try {
    const { context, page } = first;
    const submit = () => page.getByRole("button", { name: /Start charging/i });

    const foreign = invoiceDemoFixtures.foreignEditable;
    await page.goto(`/Public/Start?cp=${encodeURIComponent(foreign.chargePointId)}&conn=${foreign.connectorId}`);
    await addCaption(page, "We begin on the real local public charging page. The blue pointer shows every interaction at normal speed.", 8_000);
    await addCaption(page, "Before Stripe checkout, the customer chooses Company invoice.", 5_000);
    await activateWithVisibleCursor(page, page.locator("#wantsR1"));
    await addCaption(page, "The complete buyer form opens here, before any payment session is created.", 8_000);
    await capture(page, "01-company-invoice-choice.png");

    await addCaption(page, "Czech company example: we enter every field before continuing to secure payment.", 8_000);
    await performBuyerEntry(page, foreign.buyer, {
      onStep: async ({ label, value }) => addCaption(
        page,
        label === "Confirm reviewed details" ? "Confirm that the visible buyer summary is correct." : `Enter ${label}: ${value || "not applicable"}.`,
        1_500,
      ),
    });
    await addCaption(page, "Pause and review: the visible summary is the exact buyer snapshot that checkout will carry forward.", 10_000);

    const foreignConfirmation = page.locator("#buyerDataConfirmed");
    await addCaption(page, "First we remove confirmation and try to continue. Required, unconfirmed buyer data must stop before any mock Stripe create call.", 8_000);
    await activateWithVisibleCursor(page, foreignConfirmation);
    const beforeUnconfirmed = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    await activateWithVisibleCursor(page, submit());
    await page.waitForTimeout(3_000);
    const afterUnconfirmed = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    const unconfirmedBlock = verifyNoMockStripeCreateCall({
      after: afterUnconfirmed,
      before: beforeUnconfirmed,
      reason: "unconfirmed buyer data",
    });
    for (const [fieldName, selector] of Object.entries(buyerFormFields)) {
      const expected = {
        buyerCity: foreign.buyer.city,
        buyerCompanyName: foreign.buyer.companyName,
        buyerEmail: foreign.buyer.email,
        buyerPostalCode: foreign.buyer.postalCode,
        buyerRegistrationNumber: foreign.buyer.registrationNumber,
        buyerStreet: foreign.buyer.street,
        buyerTaxIdentifier: foreign.buyer.taxIdentifier,
      }[fieldName];
      const actual = await page.locator(selector).inputValue();
      if (actual !== expected) {
        throw new Error(`Unconfirmed submission did not preserve ${fieldName}.`);
      }
    }
    unconfirmedAttemptBlockedBeforeMockStripe = unconfirmedBlock.blockedBeforeMockStripe;
    await addCaption(page, "No mock Stripe session was created, and every entered value remains on the form for correction or confirmation.", 8_000);
    await capture(page, "02-foreign-unconfirmed-blocked.png");

    await addCaption(page, "Now we explicitly confirm the reviewed foreign-company snapshot and continue.", 5_000);
    await activateWithVisibleCursor(page, foreignConfirmation);
    const beforeForeignCheckout = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    await Promise.all([
      page.waitForURL(/\/Payments\/MockCheckout/),
      activateWithVisibleCursor(page, submit()),
    ]);
    const afterForeignCheckout = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    if (afterForeignCheckout !== beforeForeignCheckout + 1) {
      throw new Error("Confirmed foreign buyer did not create exactly one mock Stripe session.");
    }
    await page.locator("#mock-pay-now").waitFor({ state: "visible" });
    mockStripeHandoffShown = true;
    await addCaption(page, "Secure-payment handoff: this is the local Mock Stripe checkout, reached only after the complete buyer snapshot was confirmed.", 10_000);
    await capture(page, "03-mock-stripe-handoff.png");

    const croatian = invoiceDemoFixtures.croatianEditable;
    await page.goto(`/Public/Start?cp=${encodeURIComponent(croatian.chargePointId)}&conn=${croatian.connectorId}`);
    await activateWithVisibleCursor(page, page.locator("#wantsR1"));
    await addCaption(page, "Croatian branch: enter the buyer data with an intentionally invalid OIB first.", 7_000);
    await performBuyerEntry(page, { ...croatian.buyer, taxIdentifier: croatian.buyer.invalidOib }, {
      onStep: async ({ label, value }) => addCaption(
        page,
        label === "Confirm reviewed details" ? "Confirm the entered Croatian buyer details." : `Enter ${label}: ${value || "not applicable"}.`,
        1_200,
      ),
    });
    await addCaption(page, "Submit the invalid OIB now. The server must reject it before creating a mock Stripe session and preserve the entered form.", 7_000);
    const beforeInvalidOib = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    await Promise.all([
      page.waitForNavigation({ waitUntil: "domcontentloaded" }),
      activateWithVisibleCursor(page, submit()),
    ]);
    const validationMessage = page.locator(".alert-box.danger");
    await validationMessage.waitFor({ state: "visible" });
    if (!/OIB|identifier|invalid/i.test(await validationMessage.innerText())) {
      throw new Error("Invalid Croatian OIB submission did not show the expected server validation message.");
    }
    if (await page.locator("#buyerTaxIdentifier").inputValue() !== croatian.buyer.invalidOib) {
      throw new Error("Invalid Croatian OIB submission did not preserve the entered identifier.");
    }
    const afterInvalidOib = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    const invalidOibBlock = verifyNoMockStripeCreateCall({
      after: afterInvalidOib,
      before: beforeInvalidOib,
      reason: "invalid Croatian OIB",
    });
    invalidOibAttemptBlockedBeforeMockStripe = invalidOibBlock.blockedBeforeMockStripe;
    await addCaption(page, "The server rejected the invalid Croatian OIB, created no mock Stripe session, and returned the same buyer values for correction.", 10_000);
    await capture(page, "04-croatian-invalid-oib-rejected.png");

    const oib = page.locator("#buyerTaxIdentifier");
    await addCaption(page, "Correct the OIB with a checksum-valid synthetic example.", 4_000);
    await moveCursorTo(page, oib, { click: true });
    await oib.fill("");
    await oib.pressSequentially(croatian.buyer.taxIdentifier, { delay: 100 });
    await page.waitForTimeout(4_000);
    const croatianConfirmation = page.locator("#buyerDataConfirmed");
    if (!(await croatianConfirmation.isChecked())) {
      await activateWithVisibleCursor(page, croatianConfirmation);
    }
    await addCaption(page, "The corrected checksum-valid OIB is explicitly confirmed before checkout.", 8_000);
    await capture(page, "05-croatian-valid-oib-ready.png");
    const beforeValidOib = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    await Promise.all([
      page.waitForURL(/\/Payments\/MockCheckout/),
      activateWithVisibleCursor(page, submit()),
    ]);
    const afterValidOib = readMockStripeSnapshotCounts(runtime.stripeDiagnosticsDir).sessionCount;
    if (afterValidOib !== beforeValidOib + 1) {
      throw new Error("Checksum-valid Croatian OIB did not create exactly one mock Stripe session.");
    }
    croatianValidOibAcceptedBeforeMockStripe = true;
    await addCaption(page, "The server accepted the valid Croatian OIB and handed the confirmed snapshot to local Mock Stripe.", 8_000);

    await publishRecordedPage(
      context,
      page,
      runtime,
      firstSegmentFilename,
    );
  } finally {
    await first.context.close().catch(() => {});
  }

  const fresh = await createRecordedPage(browser, runtime);
  let browserState;
  let postCheckoutBuyerEntry;
  try {
    const { context, page: freshPage } = fresh;
    const foreign = invoiceDemoFixtures.foreignEditable;
    await freshPage.goto(`/Public/Start?cp=${encodeURIComponent(foreign.chargePointId)}&conn=${foreign.connectorId}`);
    await addCaption(freshPage, "Fresh browser context: no prior cookies, session storage, or page state are reused.", 8_000);
    browserState = await inspectBuyerBrowserState(freshPage, {
      buyerValues: [
        foreign.buyer.companyName,
        foreign.buyer.email,
        foreign.buyer.taxIdentifier,
        invoiceDemoFixtures.croatianEditable.buyer.companyName,
        invoiceDemoFixtures.croatianEditable.buyer.email,
        invoiceDemoFixtures.croatianEditable.buyer.taxIdentifier,
      ],
    });
    await activateWithVisibleCursor(freshPage, freshPage.locator("#wantsR1"));
    await addCaption(freshPage, "The new session opens an empty company form. Buyer data was not retained in localStorage or sessionStorage and is not repopulated.", 12_000);
    await capture(freshPage, "06-new-browser-session-empty.png");

    await freshPage.goto(statusUrl(invoiceDemoFixtures.foreignLocked));
    await freshPage.locator("#done-invoice-section").waitFor({ state: "visible" });
    postCheckoutBuyerEntry = verifyNoPostCheckoutBuyerControls(await freshPage.locator(
      "#r1-submit, #r1-company, #r1-tax-identifier, #buyerCompanyName, #buyerTaxIdentifier",
    ).count());
    await addCaption(freshPage, "After charging, the status page is read-only for buyer data and shows only the invoice outcome.", 10_000);
    await addCaption(freshPage, "There is no late company form here. The legacy endpoint remains server-side compatibility only for older clients.", 12_000);
    await capture(freshPage, "07-issued-invoice-read-only.png");

    await publishRecordedPage(context, freshPage, runtime, secondSegmentFilename);
  } finally {
    await fresh.context.close().catch(() => {});
  }

  const firstSegmentPath = path.join(runtime.artifactDir, firstSegmentFilename);
  const secondSegmentPath = path.join(runtime.artifactDir, secondSegmentFilename);
  const videoPath = path.join(runtime.artifactDir, invoiceDemoRecordingContract.uiWalkthrough.filename);
  concatenateRecordedVideos(
    [firstSegmentPath, secondSegmentPath],
    videoPath,
    { concatListPath: path.join(runtime.tempRoot, "ui-walkthrough-concat.txt") },
  );
  fs.rmSync(firstSegmentPath, { force: true });
  fs.rmSync(secondSegmentPath, { force: true });

  return {
    evidence: {
      buyerBrowserStorageAbsent: browserState.buyerBrowserStorageAbsent,
      croatianValidOibAcceptedBeforeMockStripe,
      freshStartBuyerFieldsEmpty: browserState.freshStartBuyerFieldsEmpty,
      invalidOibAttemptBlockedBeforeMockStripe: invalidOibAttemptBlockedBeforeMockStripe,
      mockStripeHandoffShown: mockStripeHandoffShown,
      unconfirmedAttemptBlockedBeforeMockStripe: unconfirmedAttemptBlockedBeforeMockStripe,
    },
    postCheckoutBuyerEntry,
    screenshots,
    video: path.basename(videoPath),
  };
}

async function recordBillingExplainer(browser, runtime) {
  const { context, page } = await createRecordedPage(browser, runtime);
  try {
    await page.goto(`/Public/Start?cp=${encodeURIComponent(invoiceDemoFixtures.foreignEditable.chargePointId)}&conn=1`);
    await addCaption(page, "Separate billing-rules explainer — the local portal stays visible while we distinguish UI steps from backend decisions.", 8_000);
    for (const section of buildBillingExplainerSections()) {
      await showExplainerSection(page, section);
    }
    return await publishRecordedPage(
      context,
      page,
      runtime,
      invoiceDemoRecordingContract.billingExplainer.filename,
    );
  } finally {
    await context.close().catch(() => {});
  }
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
  const closeOnAbort = () => void browser.close().catch(() => {});
  signal?.addEventListener("abort", closeOnAbort, { once: true });
  try {
    const uiWalkthrough = await recordUiWalkthrough(browser, runtime);
    const billingExplainer = await recordBillingExplainer(browser, runtime);
    return {
      evidence: uiWalkthrough.evidence,
      postCheckoutBuyerEntry: uiWalkthrough.postCheckoutBuyerEntry,
      screenshots: uiWalkthrough.screenshots,
      videos: {
        billingExplainer,
        uiWalkthrough: uiWalkthrough.video,
      },
    };
  } finally {
    signal?.removeEventListener("abort", closeOnAbort);
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
  let chargePointDriver;
  const signalHandlers = installSignalAbortHandlers();
  const { signal } = signalHandlers;

  try {
    buildLocalApplications(repoRoot);
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
    chargePointDriver = await abortable(connectInvoiceDemoChargePoint(runtime), signal);

    const browserArtifacts = await abortable(recordBrowserWalkthrough(runtime, signal), signal);
    const persistedBuyerSnapshots = await abortable(verifyPersistedBuyerSnapshots(runtime.databasePath), signal);
    const artifactFiles = verifyArtifactFiles(privateArtifactDir, browserArtifacts);
    const manifest = buildArtifactManifest({
      artifactFiles,
      browserArtifacts,
      persistedBuyerSnapshots,
      runtime,
    });
    fs.writeFileSync(path.join(privateArtifactDir, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
    return manifest;
  } finally {
    signalHandlers.dispose();
    chargePointDriver?.close();
    await cleanupRuntime(children, runtime.tempRoot);
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
