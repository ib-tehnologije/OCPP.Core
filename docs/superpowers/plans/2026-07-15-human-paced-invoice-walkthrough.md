# Human-Paced Invoice Walkthrough Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the rapid local invoice-demo montage with reusable, human-paced recordings of the real UI flow and billing rules.

**Architecture:** Keep the existing loopback-only .NET stack, disposable SQLite fixtures, mock Stripe boundary, and Playwright recording. Add deterministic presentation helpers for visible cursor movement, readable captions, human-paced form entry, and duration enforcement; emit separate UI-walkthrough and billing-explainer WebM files plus an updated manifest.

**Tech Stack:** Node.js ESM, Node test runner, Playwright/Chromium, .NET 8 ASP.NET Core, EF Core SQLite, WebM/VP8, ffprobe.

## Global Constraints

- Record the actual local portal at normal speed; do not synthesize a slideshow or time-lapse.
- Keep the UI walkthrough between 3 and 6 minutes and the billing explainer between 1 and 3 minutes.
- Show visible cursor movement, clicks, navigation, field entry, resulting states, and 3-5 second pauses on important results.
- Cover the public charging page, Company invoice choice, checkout handoff explanation, Czech entry/review/save, Croatian invalid and valid OIB paths, and post-issuance locked controls.
- Explain below 1 kWh suppression, normal billing at or above 1 kWh, and the provider-minimum defensive guard, distinguishing backend behavior from visible UI.
- Preserve loopback-only HTTP, disposable SQLite, mock Stripe, disabled invoices/email/Sentry, and synthetic data.
- Do not access production, live providers, credentials, customer data, or deployed systems.

---

### Task 1: Recording contract and pacing helpers

**Files:**
- Modify: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `Simulators/playwright/invoice_demo.mjs`

**Interfaces:**
- Produces: `invoiceDemoRecordingContract` with duration bounds and required artifact names.
- Produces: `buildInteractionTimeline()` with ordered UI checkpoints and minimum dwell times.
- Produces: `verifyRecordingDurations(durations)` that rejects out-of-range recordings.

- [ ] **Step 1: Write failing tests** for two video names, 180-360 second UI bounds, 60-180 second billing bounds, required checkpoints, and duration rejection.
- [ ] **Step 2: Run `node --test Simulators/tests/invoice_demo_runner.test.mjs`** and confirm failure because the contract and verification exports do not exist.
- [ ] **Step 3: Implement the minimal exported contract, timeline, and duration verifier.**
- [ ] **Step 4: Re-run the focused test** and confirm it passes.

### Task 2: Human-paced browser recording

**Files:**
- Modify: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `Simulators/playwright/invoice_demo.mjs`

**Interfaces:**
- Produces: visible in-page cursor overlay, caption/title cards, paced field entry, and two Playwright video recordings.
- Produces: `ui-walkthrough.webm` and `billing-rules-explainer.webm`.

- [ ] **Step 1: Add failing source-contract tests** requiring visible-cursor setup, per-character typing, navigation/click captions, billing-rule sections, and separate video outputs.
- [ ] **Step 2: Run the focused test** and verify it fails on the missing recording behavior.
- [ ] **Step 3: Implement the actual UI sequence** with visible cursor movement, clicks, sequential field entry, and 3-5 second result pauses while preserving real controller/API saves.
- [ ] **Step 4: Implement the billing explainer** as a separately recorded real local page with readable captions that clearly labels backend-only rules.
- [ ] **Step 5: Re-run the focused tests** and confirm they pass.

### Task 3: Manifest, runbook, and end-to-end verification

**Files:**
- Modify: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `Simulators/playwright/invoice_demo.mjs`
- Modify: `docs/local-company-invoice-demo.md`

**Interfaces:**
- Consumes: both completed WebM paths and measured ffprobe durations.
- Produces: manifest viewing order, duration/readability checks, and operator instructions for replacement recordings.

- [ ] **Step 1: Add failing tests** for both artifact names, viewing order, duration verification, and superseded legacy filename metadata.
- [ ] **Step 2: Run the focused tests** and confirm the manifest contract fails before implementation.
- [ ] **Step 3: Update artifact verification and manifest writing** to require both videos and their accepted durations.
- [ ] **Step 4: Update the public-safe runbook** with pacing, output, playback, and duration checks.
- [ ] **Step 5: Run the local demo end to end** into a private external artifact directory and inspect both complete videos at 1x speed.
- [ ] **Step 6: Run Node tests, focused/full .NET tests, solution build, migration guard, `git diff --check`, and clean-status inspection.**
- [ ] **Step 7: Commit, push, and open a reviewable PR** because the reusable recording tooling changed.
