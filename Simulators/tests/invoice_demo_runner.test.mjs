import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import * as invoiceDemoRunner from "../playwright/invoice_demo.mjs";

import {
  abortable,
  assertPrivateArtifactDirectory,
  buildChromiumLaunchOptions,
  buildDemoEnvironment,
  installSignalAbortHandlers,
  invoiceDemoMessages,
  publishCompletedVideo,
  stopProcessGroup,
} from "../playwright/invoice_demo.mjs";

test("buildDemoEnvironment isolates the demo from live services", () => {
  const runtime = {
    apiKey: "local-demo-api-key",
    databasePath: "/tmp/invoice-demo.sqlite",
    emailSinkDir: "/tmp/invoice-demo-emails",
    managementBaseUrl: "http://127.0.0.1:28182",
    serverApiBaseUrl: "http://127.0.0.1:28181/API",
    serverBaseUrl: "http://127.0.0.1:28181",
    stripeDiagnosticsDir: "/tmp/invoice-demo-stripe",
  };

  const environment = buildDemoEnvironment(runtime);

  assert.equal(environment.ASPNETCORE_URLS, undefined);
  assert.equal(environment.ConnectionStrings__SQLite, "Filename=/tmp/invoice-demo.sqlite;foreign keys=True");
  assert.equal(environment.ConnectionStrings__SqlServer, "");
  assert.equal(environment.DOTNET_ROLL_FORWARD, "Major");
  assert.equal(environment.ASPNETCORE_ENVIRONMENT, "InvoiceDemo");
  assert.equal(environment.DOTNET_ENVIRONMENT, "InvoiceDemo");
  assert.equal(environment.SENTRY_DSN, "");
  assert.equal(environment.Sentry__Dsn, "");
  assert.equal(environment.Invoices__Enabled, "false");
  assert.equal(environment.Notifications__EnableCustomerEmails, "false");
  assert.equal(environment.Notifications__FromAddress, "");
  assert.equal(environment.Notifications__ReplyToAddress, "");
  assert.equal(environment.Notifications__BccAddress, "");
  assert.equal(environment.Notifications__Smtp__Host, "");
  assert.equal(environment.Notifications__Smtp__Username, "");
  assert.equal(environment.Notifications__Smtp__Password, "");
  assert.equal(environment.Email__EnableOwnerReportEmails, "false");
  assert.equal(environment.Email__FromAddress, "");
  assert.equal(environment.Email__ReplyToAddress, "");
  assert.equal(environment.Email__Smtp__Host, "");
  assert.equal(environment.Email__Smtp__Username, "");
  assert.equal(environment.Email__Smtp__Password, "");
  assert.equal(environment.OwnerReportSchedule__Enabled, "false");
  assert.equal(environment.OwnerReportSchedule__SendTestTo, "");
  assert.equal(environment.Stripe__Enabled, "true");
  assert.equal(environment.Stripe__UseMockServices, "true");
  assert.equal(environment.Stripe__ReturnBaseUrl, runtime.managementBaseUrl);
  assert.match(environment.Kestrel__Endpoints__Http__Url, /^http:\/\/127\.0\.0\.1:\d+$/);
  assert.deepEqual(
    Object.entries(environment).filter(([key, value]) =>
      /(ERacuni|Stripe__(ApiKey|SecretKey|WebhookSecret))/.test(key) && value,
    ),
    [["Stripe__ApiKey", "mock_test_key"]],
  );
});

test("buildDemoEnvironment can target each loopback application", () => {
  const runtime = {
    apiKey: "local-demo-api-key",
    databasePath: "/tmp/invoice-demo.sqlite",
    emailSinkDir: "/tmp/invoice-demo-emails",
    managementBaseUrl: "http://127.0.0.1:28182",
    serverApiBaseUrl: "http://127.0.0.1:28181/API",
    serverBaseUrl: "http://127.0.0.1:28181",
    stripeDiagnosticsDir: "/tmp/invoice-demo-stripe",
  };

  assert.equal(buildDemoEnvironment(runtime, "server").Kestrel__Endpoints__Http__Url, runtime.serverBaseUrl);
  assert.equal(buildDemoEnvironment(runtime, "management").Kestrel__Endpoints__Http__Url, runtime.managementBaseUrl);
  assert.equal(buildDemoEnvironment(runtime, "management").ServerApiUrl, runtime.serverApiBaseUrl);
});

test("assertPrivateArtifactDirectory rejects repository paths", () => {
  const repoRoot = "/work/ocpp-core";

  assert.throws(
    () => assertPrivateArtifactDirectory(repoRoot, repoRoot),
    /outside the repository/i,
  );
  assert.throws(
    () => assertPrivateArtifactDirectory(repoRoot, path.join(repoRoot, "artifacts")),
    /outside the repository/i,
  );
});

test("assertPrivateArtifactDirectory accepts an external directory", () => {
  const repoRoot = "/work/ocpp-core";
  const external = path.join(os.tmpdir(), "ocpp-invoice-demo-artifacts");

  assert.equal(assertPrivateArtifactDirectory(repoRoot, external), path.resolve(external));
});

test("assertPrivateArtifactDirectory rejects paths inside any Git repository", (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "invoice-demo-git-test-"));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const repoRoot = path.join(tempRoot, "source-repo");
  const otherRepo = path.join(tempRoot, "other-repo");
  fs.mkdirSync(repoRoot);
  fs.mkdirSync(otherRepo);
  execFileSync("git", ["init", "--quiet", repoRoot]);
  execFileSync("git", ["init", "--quiet", otherRepo]);

  assert.throws(
    () => assertPrivateArtifactDirectory(repoRoot, path.join(otherRepo, "private", "run")),
    /inside any Git repository or worktree/i,
  );
});

test("sanitizeParentEnvironment removes all inherited Sentry settings", () => {
  assert.equal(typeof invoiceDemoRunner.sanitizeParentEnvironment, "function");
  assert.deepEqual(invoiceDemoRunner.sanitizeParentEnvironment({
    PATH: "/usr/bin",
    SENTRY_DSN: "https://secret.example/1",
    Sentry__Dsn: "https://secret.example/2",
    SENTRY__TRACES_SAMPLE_RATE: "1",
  }), { PATH: "/usr/bin" });
});

test("verifyPersistedBuyerSnapshots requires exact Czech and Croatian database snapshots", async () => {
  assert.equal(typeof invoiceDemoRunner.verifyPersistedBuyerSnapshots, "function");
  const rows = [
    {
      ReservationId: "10000000-0000-4000-8000-000000000001",
      InvoiceBuyerCountry: "CZ",
      InvoiceBuyerCompanyName: "Example Praha s.r.o.",
      InvoiceBuyerStreet: "Testovaci 10",
      InvoiceBuyerPostalCode: "110 00",
      InvoiceBuyerCity: "Praha",
      InvoiceBuyerEmail: "invoice-prague@example.test",
      InvoiceBuyerTaxIdentifier: "CZ00000001",
      InvoiceBuyerRegistrationNumber: "00000001",
      InvoiceBuyerIdentifierIsVatRegistration: 1,
      InvoiceBuyerConfirmedAtUtc: "2026-07-15T10:00:00Z",
    },
    {
      ReservationId: "10000000-0000-4000-8000-000000000002",
      InvoiceBuyerCountry: "HR",
      InvoiceBuyerCompanyName: "Primjer Zagreb d.o.o.",
      InvoiceBuyerStreet: "Testna ulica 20",
      InvoiceBuyerPostalCode: "10000",
      InvoiceBuyerCity: "Zagreb",
      InvoiceBuyerEmail: "invoice-zagreb@example.test",
      InvoiceBuyerTaxIdentifier: "69435151530",
      InvoiceBuyerRegistrationNumber: null,
      InvoiceBuyerIdentifierIsVatRegistration: 0,
      InvoiceBuyerConfirmedAtUtc: "2026-07-15T10:01:00Z",
    },
  ];

  assert.deepEqual(
    await invoiceDemoRunner.verifyPersistedBuyerSnapshots("/tmp/demo.sqlite", { query: async () => rows }),
    { croatianBuyerSnapshotPersisted: true, czechBuyerSnapshotPersisted: true },
  );
  await assert.rejects(
    invoiceDemoRunner.verifyPersistedBuyerSnapshots("/tmp/demo.sqlite", {
      query: async () => rows.map((row) => row.ReservationId.endsWith("0001")
        ? { ...row, InvoiceBuyerCity: "Brno" }
        : row),
    }),
    /Czech.*InvoiceBuyerCity/i,
  );
  await assert.rejects(
    invoiceDemoRunner.verifyPersistedBuyerSnapshots("/tmp/demo.sqlite", {
      query: async () => rows.map((row) => row.ReservationId.endsWith("0002")
        ? { ...row, InvoiceBuyerIdentifierIsVatRegistration: null }
        : row),
    }),
    /Croatian.*InvoiceBuyerIdentifierIsVatRegistration.*boolean/i,
  );
});

test("verifyNoPostCheckoutBuyerControls rejects late buyer entry", () => {
  assert.equal(typeof invoiceDemoRunner.verifyNoPostCheckoutBuyerControls, "function");
  assert.deepEqual(invoiceDemoRunner.verifyNoPostCheckoutBuyerControls(0), {
    postCheckoutBuyerControlsAbsent: true,
  });
  assert.throws(() => invoiceDemoRunner.verifyNoPostCheckoutBuyerControls(1), /no post-checkout buyer controls.*1/i);
});

test("verifyArtifactFiles requires every expected PNG and both accepted WebM recordings", (t) => {
  assert.equal(typeof invoiceDemoRunner.verifyArtifactFiles, "function");
  const artifactDir = fs.mkdtempSync(path.join(os.tmpdir(), "invoice-demo-artifacts-test-"));
  t.after(() => fs.rmSync(artifactDir, { recursive: true, force: true }));
  const screenshots = [
    "01-company-invoice-choice.png",
    "02-czech-company-review.png",
    "03-czech-company-remembered.png",
    "04-croatian-invalid-oib.png",
    "05-croatian-valid-oib-ready.png",
    "06-issued-invoice-read-only.png",
  ];
  for (const filename of [...screenshots, "ui-walkthrough.webm", "billing-rules-explainer.webm"]) {
    fs.writeFileSync(path.join(artifactDir, filename), "content");
  }

  assert.deepEqual(invoiceDemoRunner.verifyArtifactFiles(artifactDir, {
    screenshots,
    videos: {
      billingExplainer: "billing-rules-explainer.webm",
      uiWalkthrough: "ui-walkthrough.webm",
    },
  }, {
    readDuration: (filename) => filename.endsWith("ui-walkthrough.webm") ? 210 : 75,
  }), {
    billingExplainerDurationAccepted: true,
    billingExplainerSeconds: 75,
    screenshotsNonEmpty: true,
    uiWalkthroughDurationAccepted: true,
    uiWalkthroughSeconds: 210,
    verifiedScreenshotCount: 6,
    videosNonEmpty: true,
  });
  fs.writeFileSync(path.join(artifactDir, "02-czech-company-review.png"), "");
  assert.throws(
    () => invoiceDemoRunner.verifyArtifactFiles(artifactDir, {
      screenshots,
      videos: {
        billingExplainer: "billing-rules-explainer.webm",
        uiWalkthrough: "ui-walkthrough.webm",
      },
    }, {
      readDuration: () => 210,
    }),
    /02-czech-company-review\.png.*nonempty/i,
  );
});

test("cleanupRuntime deletes runtime files even when child shutdown fails", async () => {
  assert.equal(typeof invoiceDemoRunner.cleanupRuntime, "function");
  const removals = [];
  const shutdownError = new Error("child shutdown failed");

  await assert.rejects(
    invoiceDemoRunner.cleanupRuntime([], "/tmp/invoice-demo-runtime", {
      remove: (...args) => removals.push(args),
      stop: async () => { throw shutdownError; },
    }),
    shutdownError,
  );
  assert.deepEqual(removals, [["/tmp/invoice-demo-runtime", { recursive: true, force: true }]]);
});

test("assertPrivateArtifactDirectory rejects an external symlink into the repository", (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "invoice-demo-path-test-"));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const repoRoot = path.join(tempRoot, "repo");
  const privateRepoDir = path.join(repoRoot, "private-artifacts");
  const externalLink = path.join(tempRoot, "external-link");
  fs.mkdirSync(privateRepoDir, { recursive: true });
  fs.symlinkSync(privateRepoDir, externalLink, "dir");

  assert.throws(
    () => assertPrivateArtifactDirectory(repoRoot, path.join(externalLink, "new-run")),
    /outside the repository/i,
  );
});

test("buildChromiumLaunchOptions falls back to an installed system Chrome", () => {
  assert.deepEqual(buildChromiumLaunchOptions({ bundledExists: true, chromeExists: true }), {
    headless: true,
  });
  assert.deepEqual(buildChromiumLaunchOptions({ bundledExists: false, chromeExists: true }), {
    channel: "chrome",
    headless: true,
  });
  assert.throws(
    () => buildChromiumLaunchOptions({ bundledExists: false, chromeExists: false }),
    /playwright.*chromium/i,
  );
});

test("buildChromiumLaunchOptions supports an optional headed walkthrough", () => {
  assert.deepEqual(buildChromiumLaunchOptions({ bundledExists: true, chromeExists: true, headless: false }), {
    headless: false,
  });
  assert.deepEqual(buildChromiumLaunchOptions({ bundledExists: false, chromeExists: true, headless: false }), {
    channel: "chrome",
    headless: false,
  });
});

test("SIGINT immediately rejects abortable work", async () => {
  const processTarget = new EventEmitter();
  const handlers = installSignalAbortHandlers(processTarget);
  const pending = new Promise(() => {});

  const guarded = abortable(pending, handlers.signal);
  processTarget.emit("SIGINT");

  await assert.rejects(guarded, /interrupted by SIGINT/i);
  assert.equal(handlers.signal.aborted, true);
  handlers.dispose();
});

test("stopProcessGroup terminates the full process group and confirms exit", async () => {
  const child = Object.assign(new EventEmitter(), { exitCode: null, pid: 4312, signalCode: null });
  const calls = [];
  let groupExists = true;
  const kill = (pid, signal) => {
    calls.push([pid, signal]);
    if (signal === "SIGTERM") {
      setImmediate(() => {
        groupExists = false;
        child.signalCode = signal;
        child.emit("exit", null, signal);
      });
      return;
    }
    if (signal === 0 && groupExists) return;
    const error = new Error("no such process group");
    error.code = "ESRCH";
    throw error;
  };

  await stopProcessGroup(child, { kill, termTimeoutMs: 10, killTimeoutMs: 10, pollIntervalMs: 1 });

  assert.deepEqual(calls.filter(([, signal]) => signal !== 0), [[-4312, "SIGTERM"]]);
  assert.ok(calls.some(([, signal]) => signal === 0));
  assert.equal(child.signalCode, "SIGTERM");
});

test("stopProcessGroup escalates to SIGKILL and waits for confirmed exit", async () => {
  const child = Object.assign(new EventEmitter(), { exitCode: null, pid: 4313, signalCode: null });
  const calls = [];
  let groupExists = true;
  const kill = (pid, signal) => {
    calls.push([pid, signal]);
    if (signal === "SIGKILL") {
      setImmediate(() => {
        groupExists = false;
        child.signalCode = signal;
        child.emit("exit", null, signal);
      });
      return;
    }
    if (signal === "SIGTERM") return;
    if (signal === 0 && groupExists) return;
    const error = new Error("no such process group");
    error.code = "ESRCH";
    throw error;
  };

  await stopProcessGroup(child, { kill, termTimeoutMs: 1, killTimeoutMs: 10, pollIntervalMs: 1 });

  assert.deepEqual(calls.filter(([, signal]) => signal !== 0), [
    [-4313, "SIGTERM"],
    [-4313, "SIGKILL"],
  ]);
  assert.ok(calls.some(([, signal]) => signal === 0));
  assert.equal(child.signalCode, "SIGKILL");
});

test("stopProcessGroup does not mistake launcher exit for process-group disappearance", async () => {
  const child = Object.assign(new EventEmitter(), { exitCode: null, pid: 4314, signalCode: null });
  const calls = [];
  let groupExists = true;
  let probesAfterKill = 0;
  const kill = (pid, signal) => {
    calls.push([pid, signal]);
    if (signal === "SIGTERM") {
      setImmediate(() => {
        child.signalCode = signal;
        child.emit("exit", null, signal);
      });
      return;
    }
    if (signal === "SIGKILL") return;
    if (signal === 0 && groupExists) {
      if (calls.some(([, sentSignal]) => sentSignal === "SIGKILL")) {
        probesAfterKill += 1;
        if (probesAfterKill >= 2) groupExists = false;
      }
      if (groupExists) return;
    }
    const error = new Error("no such process group");
    error.code = "ESRCH";
    throw error;
  };

  await stopProcessGroup(child, {
    kill,
    killTimeoutMs: 20,
    pollIntervalMs: 1,
    termTimeoutMs: 2,
  });

  assert.deepEqual(calls.filter(([, signal]) => signal !== 0), [
    [-4314, "SIGTERM"],
    [-4314, "SIGKILL"],
  ]);
  assert.ok(calls.filter(([, signal]) => signal === 0).length >= 3);
  assert.equal(groupExists, false);
});

test("publishCompletedVideo falls back to copy and remove for EXDEV", () => {
  const calls = [];
  publishCompletedVideo("/tmp/source.webm", "/Volumes/private/walkthrough.webm", {
    copyFileSync: (...args) => calls.push(["copy", ...args]),
    removeSync: (...args) => calls.push(["remove", ...args]),
    renameSync: () => {
      const error = new Error("cross-device link");
      error.code = "EXDEV";
      throw error;
    },
  });

  assert.deepEqual(calls, [
    ["remove", "/Volumes/private/walkthrough.webm", { force: true }],
    ["copy", "/tmp/source.webm", "/Volumes/private/walkthrough.webm"],
    ["remove", "/tmp/source.webm", { force: true }],
  ]);
});

test("invoice walkthrough states the pre-checkout and read-only boundaries", () => {
  assert.deepEqual(invoiceDemoMessages, {
    checkoutReady: "Invoice buyer details are confirmed before Stripe checkout.",
    postCheckoutReadOnly: "The status page shows invoice results without late buyer editing.",
  });
});

test("recording contract requires separate human-paced videos", () => {
  assert.deepEqual(invoiceDemoRunner.invoiceDemoRecordingContract, {
    billingExplainer: {
      filename: "billing-rules-explainer.webm",
      maximumDurationSeconds: 180,
      minimumDurationSeconds: 60,
    },
    uiWalkthrough: {
      filename: "ui-walkthrough.webm",
      maximumDurationSeconds: 360,
      minimumDurationSeconds: 180,
    },
  });
});

test("interaction timeline covers the required human-visible UI states", () => {
  const timeline = invoiceDemoRunner.buildInteractionTimeline();
  assert.deepEqual(timeline.map(({ id }) => id), [
    "public-start",
    "company-invoice-choice",
    "czech-entry",
    "czech-review",
    "czech-remember",
    "croatian-invalid-oib",
    "croatian-valid-oib-ready",
    "issued-read-only",
  ]);
  assert.ok(timeline.every(({ minimumDwellMs }) => minimumDwellMs >= 3_000));
});

test("verifyRecordingDurations enforces both acceptance windows", () => {
  assert.deepEqual(invoiceDemoRunner.verifyRecordingDurations({
    billingExplainerSeconds: 75,
    uiWalkthroughSeconds: 210,
  }), {
    billingExplainerDurationAccepted: true,
    billingExplainerSeconds: 75,
    uiWalkthroughDurationAccepted: true,
    uiWalkthroughSeconds: 210,
  });
  assert.throws(
    () => invoiceDemoRunner.verifyRecordingDurations({
      billingExplainerSeconds: 75,
      uiWalkthroughSeconds: 179.9,
    }),
    /UI walkthrough.*180.*360/i,
  );
  assert.throws(
    () => invoiceDemoRunner.verifyRecordingDurations({
      billingExplainerSeconds: 180.1,
      uiWalkthroughSeconds: 210,
    }),
    /billing explainer.*60.*180/i,
  );
});

test("buyer entry plan types every visible field in operator order", () => {
  const steps = invoiceDemoRunner.buildBuyerEntrySteps({
    city: "Praha",
    companyName: "Example Praha s.r.o.",
    country: "CZ",
    email: "invoice-prague@example.test",
    identifierIsVatRegistration: true,
    postalCode: "110 00",
    registrationNumber: "00000001",
    street: "Testovaci 10",
    taxIdentifier: "CZ00000001",
  });
  assert.deepEqual(steps.map(({ selector }) => selector), [
    "#buyerCountry",
    "#buyerCompanyName",
    "#buyerStreet",
    "#buyerPostalCode",
    "#buyerCity",
    "#buyerEmail",
    "#buyerTaxIdentifier",
    "#buyerRegistrationNumber",
    "#buyerIdentifierIsVatRegistration",
    "#buyerDataConfirmed",
  ]);
  assert.ok(steps.every(({ dwellAfterMs }) => dwellAfterMs >= 1_500));
  assert.ok(steps.filter(({ action }) => action === "type").every(({ typingDelayMs }) => typingDelayMs >= 70));
});

test("billing explainer covers all three backend rules at readable pace", () => {
  const sections = invoiceDemoRunner.buildBillingExplainerSections();
  assert.deepEqual(sections.map(({ id }) => id), [
    "billing-context",
    "below-one-kwh",
    "normal-billing",
    "provider-minimum-guard",
    "ui-versus-backend",
    "billing-recap",
  ]);
  assert.match(sections.find(({ id }) => id === "below-one-kwh").text, /below 1 kWh.*no charge.*no invoice.*no email/i);
  assert.match(sections.find(({ id }) => id === "normal-billing").text, /at or above 1 kWh.*normal billing/i);
  assert.match(sections.find(({ id }) => id === "provider-minimum-guard").text, /defensive fallback.*positive amount.*provider minimum/i);
  const plannedSeconds = sections.reduce((sum, { dwellMs }) => sum + dwellMs, 0) / 1_000;
  assert.ok(plannedSeconds >= 60 && plannedSeconds <= 180);
});

test("presentation overlays reserve stable visible cursor and caption elements", () => {
  assert.deepEqual(invoiceDemoRunner.invoiceDemoOverlayIds, {
    caption: "invoice-demo-caption",
    cursor: "invoice-demo-cursor",
    explainer: "invoice-demo-explainer",
  });
});

test("moveCursorTo visibly moves and clicks at the locator centre", async () => {
  const calls = [];
  const page = {
    mouse: {
      click: async (...args) => calls.push(["click", ...args]),
      move: async (...args) => calls.push(["move", ...args]),
    },
  };
  const locator = {
    boundingBox: async () => ({ height: 40, width: 100, x: 20, y: 30 }),
    scrollIntoViewIfNeeded: async () => calls.push(["scroll"]),
  };

  await invoiceDemoRunner.moveCursorTo(page, locator, { click: true });

  assert.deepEqual(calls, [
    ["scroll"],
    ["move", 70, 50, { steps: 24 }],
    ["click", 70, 50, { delay: 180 }],
  ]);
});

test("performBuyerEntry uses paced real interactions for each field", async () => {
  const calls = [];
  const states = new Map();
  const page = {
    locator(selector) {
      return {
        boundingBox: async () => ({ height: 20, width: 80, x: 10, y: 10 }),
        fill: async (value) => calls.push([selector, "fill", value]),
        isChecked: async () => states.get(selector) ?? false,
        pressSequentially: async (value, options) => calls.push([selector, "type", value, options]),
        scrollIntoViewIfNeeded: async () => {},
        selectOption: async (value) => calls.push([selector, "select", value]),
      };
    },
    mouse: {
      click: async () => {},
      move: async () => {},
    },
    waitForTimeout: async (milliseconds) => calls.push(["wait", milliseconds]),
  };
  const buyer = {
    city: "Praha",
    companyName: "Example Praha s.r.o.",
    country: "CZ",
    email: "invoice-prague@example.test",
    identifierIsVatRegistration: true,
    postalCode: "110 00",
    registrationNumber: "00000001",
    street: "Testovaci 10",
    taxIdentifier: "CZ00000001",
  };

  const completed = await invoiceDemoRunner.performBuyerEntry(page, buyer, {
    onStep: async ({ label }) => calls.push(["caption", label]),
  });

  assert.equal(completed.length, 10);
  assert.deepEqual(calls.find((call) => call[0] === "#buyerCountry" && call[1] === "select"), ["#buyerCountry", "select", "CZ"]);
  assert.deepEqual(calls.find((call) => call[0] === "#buyerCompanyName" && call[1] === "type"), ["#buyerCompanyName", "type", "Example Praha s.r.o.", { delay: 80 }]);
  assert.ok(calls.filter((call) => call[0] === "wait").every(([, milliseconds]) => milliseconds >= 1_500));
});

test("readMediaDurationSeconds parses a finite ffprobe duration", () => {
  assert.equal(invoiceDemoRunner.readMediaDurationSeconds("/tmp/demo.webm", {
    run: (command, args) => {
      assert.equal(command, "ffprobe");
      assert.ok(args.includes("/tmp/demo.webm"));
      return { status: 0, stdout: "210.125000\n", stderr: "" };
    },
  }), 210.125);
  assert.throws(
    () => invoiceDemoRunner.readMediaDurationSeconds("/tmp/demo.webm", {
      run: () => ({ status: 1, stdout: "", stderr: "invalid data" }),
    }),
    /ffprobe.*invalid data/i,
  );
});

test("artifact manifest provides replacement viewing order and marks the legacy clip superseded", () => {
  const manifest = invoiceDemoRunner.buildArtifactManifest({
    artifactFiles: {
      billingExplainerDurationAccepted: true,
      billingExplainerSeconds: 75,
      screenshotsNonEmpty: true,
      uiWalkthroughDurationAccepted: true,
      uiWalkthroughSeconds: 210,
      verifiedScreenshotCount: 6,
      videosNonEmpty: true,
    },
    browserArtifacts: {
      postCheckoutBuyerEntry: { postCheckoutBuyerControlsAbsent: true },
      screenshots: ["01-company-invoice-choice.png"],
      videos: {
        billingExplainer: "billing-rules-explainer.webm",
        uiWalkthrough: "ui-walkthrough.webm",
      },
    },
    createdAtUtc: "2026-07-15T20:30:00.000Z",
    persistedBuyerSnapshots: { croatianBuyerSnapshotPersisted: true, czechBuyerSnapshotPersisted: true },
    runtime: {
      managementBaseUrl: "http://127.0.0.1:28082",
      serverBaseUrl: "http://127.0.0.1:28081",
    },
  });

  assert.deepEqual(manifest.artifacts.viewingOrder, [
    "ui-walkthrough.webm",
    "billing-rules-explainer.webm",
  ]);
  assert.deepEqual(manifest.supersededArtifacts, [{
    filename: "walkthrough.webm",
    reason: "Rejected rapid montage; not suitable as the primary human walkthrough.",
  }]);
  assert.equal(manifest.verification.uiWalkthroughSeconds, 210);
  assert.equal(manifest.verification.billingExplainerSeconds, 75);
});

test("demo stack builds once before both no-build application launches", () => {
  const calls = [];
  invoiceDemoRunner.buildLocalApplications("/work/ocpp-core", {
    run: (...args) => {
      calls.push(args);
      return { status: 0, stdout: "", stderr: "" };
    },
  });
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0][0], "dotnet");
  assert.deepEqual(calls[0][1], ["build", "OCPP.Core.sln"]);
  assert.equal(calls[0][2].cwd, "/work/ocpp-core");
  assert.deepEqual(invoiceDemoRunner.buildDotnetRunArgs("OCPP.Core.Server/OCPP.Core.Server.csproj"), [
    "run",
    "--no-build",
    "--project",
    "OCPP.Core.Server/OCPP.Core.Server.csproj",
  ]);
});
