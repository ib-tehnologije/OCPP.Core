import assert from "node:assert/strict";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  assertPrivateArtifactDirectory,
  buildChromiumLaunchOptions,
  buildDemoEnvironment,
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
