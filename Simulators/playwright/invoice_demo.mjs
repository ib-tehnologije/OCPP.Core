import { spawn } from "node:child_process";
import { once } from "node:events";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { invoiceDemoFixtures, seedInvoiceDemoFixtures } from "../lib/invoice_demo_fixtures.mjs";
import { seedTestStack } from "../lib/sqlite_helpers.mjs";
import { waitForUrl } from "./common.mjs";

const packageDir = path.dirname(fileURLToPath(import.meta.url));
const defaultRepoRoot = path.resolve(packageDir, "..", "..");
const liveProviderKeyPattern = /^(?:Invoices__ERacuni|Stripe__(?:SecretKey|WebhookSecret))/i;

export function assertPrivateArtifactDirectory(repoRoot, artifactDir) {
  if (!artifactDir || !String(artifactDir).trim()) {
    throw new Error("INVOICE_DEMO_ARTIFACT_DIR must name a private directory outside the repository.");
  }

  const resolvedRepoRoot = path.resolve(repoRoot);
  const resolvedArtifactDir = path.resolve(artifactDir);
  const relative = path.relative(resolvedRepoRoot, resolvedArtifactDir);
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
    Notifications__SinkDirectory: runtime.emailSinkDir,
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

export function buildChromiumLaunchOptions({ bundledExists, chromeExists }) {
  if (bundledExists) return { headless: true };
  if (chromeExists) return { channel: "chrome", headless: true };
  throw new Error("Playwright Chromium is not installed and no system Chrome fallback is available.");
}

function sanitizedParentEnvironment() {
  return Object.fromEntries(
    Object.entries(process.env).filter(([key]) => !liveProviderKeyPattern.test(key)),
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
  return runtime;
}

function spawnApplication(repoRoot, artifactDir, projectPath, environment, logName) {
  const log = fs.createWriteStream(path.join(artifactDir, logName), { flags: "w" });
  const child = spawn("dotnet", ["run", "--project", projectPath], {
    cwd: repoRoot,
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
  for (const child of children) {
    if (child.exitCode === null && child.signalCode === null) child.kill("SIGTERM");
  }
  await Promise.all(children.map(async (child) => {
    if (child.exitCode !== null || child.signalCode !== null) return;
    await Promise.race([once(child, "exit"), new Promise((resolve) => setTimeout(resolve, 3_000))]);
    if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
  }));
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

async function recordBrowserWalkthrough(runtime) {
  const { chromium } = await import("playwright");
  const chromeExecutablePath = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
  const browser = await chromium.launch(buildChromiumLaunchOptions({
    bundledExists: fs.existsSync(chromium.executablePath()),
    chromeExists: fs.existsSync(chromeExecutablePath),
  }));
  const context = await browser.newContext({
    baseURL: runtime.managementBaseUrl,
    locale: "en-US",
    recordVideo: { dir: runtime.tempRoot, size: { width: 1440, height: 900 } },
    viewport: { width: 1440, height: 900 },
  });
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
    await page.locator("#r1-result").filter({ hasText: "saved" }).waitFor();
    await addCaption(page, "The reviewed Czech company details are saved to the reservation.");
    await capture("czech-company-saved");

    const croatian = invoiceDemoFixtures.croatianEditable;
    await page.goto(statusUrl(croatian));
    await fillBuyer({ ...croatian.buyer, taxIdentifier: croatian.buyer.invalidOib });
    await page.locator("#r1-submit").click();
    await page.locator("#r1-result.field-help--error").waitFor();
    await addCaption(page, "An invalid Croatian OIB is rejected before invoice details are saved.");
    await capture("croatian-invalid-oib");
    await page.locator("#r1-tax-identifier").fill(croatian.buyer.taxIdentifier);
    await page.locator("#r1-submit").click();
    await page.locator("#r1-result").filter({ hasText: "saved" }).waitFor();
    await addCaption(page, "A checksum-valid Croatian OIB passes validation and can be saved.");
    await capture("croatian-valid-oib");

    await page.goto(statusUrl(invoiceDemoFixtures.foreignLocked));
    await page.locator("#r1-submit:disabled").waitFor();
    await addCaption(page, "After invoice issuance, buyer fields are locked against later changes.");
    await capture("issued-invoice-locked");

    const video = page.video();
    await context.close();
    const videoPath = await video.path();
    const walkthroughPath = path.join(runtime.artifactDir, "walkthrough.webm");
    fs.rmSync(walkthroughPath, { force: true });
    fs.renameSync(videoPath, walkthroughPath);
    return { screenshots, video: path.basename(walkthroughPath) };
  } finally {
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
  let interruptedSignal = null;
  const onSignal = (signal) => {
    interruptedSignal = signal;
    void stopChildren(children);
  };
  process.once("SIGINT", onSignal);
  process.once("SIGTERM", onSignal);

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
    await Promise.all([
      waitForApplication(`${runtime.serverBaseUrl}/`, server, "server"),
      waitForApplication(`${runtime.managementBaseUrl}/Public/Map`, management, "management"),
    ]);
    await seedTestStack(runtime.databasePath);
    await seedInvoiceDemoFixtures(runtime.databasePath);
    if (interruptedSignal) throw new Error(`Invoice demo interrupted by ${interruptedSignal}.`);

    const browserArtifacts = await recordBrowserWalkthrough(runtime);
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
    process.removeListener("SIGINT", onSignal);
    process.removeListener("SIGTERM", onSignal);
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
