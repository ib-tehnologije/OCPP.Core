# Pre-checkout Walkthrough Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Regenerate the local-only company-invoice evidence package so the recording visibly proves pre-checkout confirmation, blocked invalid/unconfirmed submissions, mock Stripe handoff, browser-stateless behavior, and read-only post-checkout status.

**Architecture:** Extend the existing Playwright demo runner rather than the product flow. The first recorded segment exercises the foreign and Croatian form paths while counting persisted mock Stripe sessions; a second fresh browser context proves storage and form emptiness. Concatenate the two real-portal WebM segments into the existing 3-6 minute UI walkthrough, retain the separate billing explainer, and surface every proof as manifest verification fields.

**Tech Stack:** Node.js ESM, Node test runner, Playwright/Chromium, .NET 8 local applications, SQLite, mock Stripe diagnostics JSON, FFmpeg/ffprobe, WebM/VP8.

## Global Constraints

- Use merged `origin/main` at or after `b591db601bf7e0942d1fa835917dfc75a523b6eb`.
- Use loopback listeners, disposable SQLite, mock Stripe, disabled invoice/email/Sentry integrations, and synthetic `.example.test` data only.
- Keep `ui-walkthrough.webm` between 180 and 360 seconds and `billing-rules-explainer.webm` between 60 and 180 seconds.
- Show visible cursor movement, sequential field entry, explicit confirmation, and readable pauses.
- Do not change payment, invoice, tax, no-charge, provider-minimum, production, deployment, secret, or customer behavior.
- Keep committed files public-safe; owner-only artifact links stay outside the repository.

---

### Task 1: Evidence helpers and manifest contract

**Files:**
- Modify: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `Simulators/playwright/invoice_demo.mjs`

**Interfaces:**
- Consumes: `mock-stripe-store.json`, Playwright page storage, fresh-start buyer field values, recorded WebM segment paths.
- Produces: `readMockStripeSnapshotCounts()`, `verifyNoMockStripeCreateCall()`, `inspectBuyerBrowserState()`, `concatenateRecordedVideos()`, and manifest flags for every acceptance proof.

- [x] **Step 1: Add failing tests for the missing evidence helpers.**

```js
test("verifyNoMockStripeCreateCall rejects any new mock session", () => {
  assert.deepEqual(verifyNoMockStripeCreateCall({ before: 3, after: 3, reason: "unconfirmed" }), {
    blockedBeforeMockStripe: true,
    mockStripeSessionCount: 3,
  });
  assert.throws(
    () => verifyNoMockStripeCreateCall({ before: 3, after: 4, reason: "invalid OIB" }),
    /invalid OIB.*created 1 mock Stripe session/i,
  );
});

test("inspectBuyerBrowserState requires empty storage and fields", async () => {
  const result = await inspectBuyerBrowserState(fakeEmptyPage);
  assert.equal(result.buyerBrowserStorageAbsent, true);
  assert.equal(result.freshStartBuyerFieldsEmpty, true);
});

test("concatenateRecordedVideos invokes ffmpeg concat copy", () => {
  concatenateRecordedVideos(["/tmp/part-1.webm", "/tmp/part-2.webm"], "/tmp/ui.webm", fixtureDependencies);
  assert.deepEqual(ffmpegArgs, ["-y", "-f", "concat", "-safe", "0", "-i", concatList, "-c", "copy", "/tmp/ui.webm"]);
});
```

- [x] **Step 2: Run `node --test Simulators/tests/invoice_demo_runner.test.mjs` and verify RED.** Expected: imports or assertions fail because the four helpers and new manifest fields do not exist.
- [x] **Step 3: Implement the helpers with bounded JSON parsing, explicit counts, buyer-specific storage matching, exact empty-field checks, and FFmpeg exit validation.**
- [x] **Step 4: Extend `buildArtifactManifest()` so `verification` includes `unconfirmedAttemptBlockedBeforeMockStripe`, `invalidOibAttemptBlockedBeforeMockStripe`, `mockStripeHandoffShown`, `buyerBrowserStorageAbsent`, and `freshStartBuyerFieldsEmpty`.**
- [x] **Step 5: Re-run the focused Node test and verify GREEN.**
- [x] **Step 6: Commit the helper slice.**

```sh
git add Simulators/tests/invoice_demo_runner.test.mjs Simulators/playwright/invoice_demo.mjs
git commit -m "test: define pre-checkout walkthrough evidence"
```

### Task 2: Real browser evidence, documentation, and replacement package

**Files:**
- Modify: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `Simulators/playwright/invoice_demo.mjs`
- Modify: `docs/local-company-invoice-demo.md`

**Interfaces:**
- Consumes: Task 1 helpers, existing synthetic Czech/Croatian fixtures, local mock checkout, and owner-only artifact directory.
- Produces: a two-segment concatenated `ui-walkthrough.webm`, seven ordered PNG checkpoints, unchanged separate billing explainer, updated manifest, and public-safe runbook.

- [x] **Step 1: Add failing source-contract tests for the ordered checkpoint names and fresh-context evidence segment.** The expected screenshots are `01-company-invoice-choice.png`, `02-foreign-unconfirmed-blocked.png`, `03-mock-stripe-handoff.png`, `04-croatian-invalid-oib-rejected.png`, `05-croatian-valid-oib-ready.png`, `06-new-browser-session-empty.png`, and `07-issued-invoice-read-only.png`.
- [x] **Step 2: Run the focused Node test and verify RED.** Expected: the old six-file screenshot contract and single-context recording fail.
- [x] **Step 3: Record the foreign flow.** Enter every field, visibly unconfirm and submit, verify mock-session count unchanged and fields preserved, explicitly reconfirm, submit, wait for `/Payments/MockCheckout`, and capture the mock secure-payment handoff.
- [x] **Step 4: Record the Croatian flow.** Enter an invalid synthetic OIB, submit, require the server validation message and preserved values, verify no mock session was created, correct to checksum-valid `69435151530`, reconfirm, and pause on the accepted pre-checkout state.
- [x] **Step 5: Record a second fresh browser context.** Navigate to the same local start page, assert buyer-specific `localStorage`/`sessionStorage` content is absent, reveal the company form, assert all buyer fields are empty, then navigate to the locked result and verify no late editing controls. Concatenate both real-browser segments into `ui-walkthrough.webm`.
- [x] **Step 6: Update the runbook output list and operator review checklist for the seven screenshots, two-segment UI recording, blocked-attempt proofs, mock handoff, storage proof, and fresh-session proof.**
- [x] **Step 7: Run `node --test Simulators/tests/invoice_demo_runner.test.mjs Simulators/tests/invoice_demo_fixtures.test.mjs` and verify all tests pass.**
- [x] **Step 8: Run the full local demo into a private directory outside the worktree and inspect the generated manifest, logs, screenshots, durations, and both complete videos at 1x speed.**
- [x] **Step 9: Run `dotnet build OCPP.Core.sln -c Release`, `dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj -c Release --no-build`, `bash ./scripts/check-mssql-migration-metadata.sh`, `git diff --check`, and `git status --short`.**
- [x] **Step 10: Commit and push the reusable-tooling update, open a narrowly scoped draft PR, replace the owner-only package files, mark the prior post-checkout package superseded, and return the queue row to `Ready for QA` with exact links, durations, and validation evidence.**

```sh
git add Simulators/tests/invoice_demo_runner.test.mjs Simulators/playwright/invoice_demo.mjs docs/local-company-invoice-demo.md
git commit -m "fix: record complete pre-checkout invoice walkthrough"
git push -u origin req/2026-07-18-001-precheckout-walkthrough-refresh
```
