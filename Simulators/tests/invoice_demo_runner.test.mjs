import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

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

test("invoice walkthrough requires exact English result messages", () => {
  assert.deepEqual(invoiceDemoMessages, {
    invalidOib: "Please enter a valid OIB (11 digits).",
    locked: "Buyer details cannot be changed after invoice submission. Contact support for a correction.",
    saved: "R1 details saved successfully.",
  });
});
