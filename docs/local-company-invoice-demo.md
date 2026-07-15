# Local Company Invoice Demo

This walkthrough records the real local management and server applications while they save synthetic Czech and Croatian company invoice details. It also demonstrates Croatian OIB validation and the locked state after a synthetic invoice marker exists. The runner uses a disposable SQLite database, visible cursor movement, human-paced field entry, and readable captions, then writes the recordings outside the repository.

## Prerequisites

- A .NET SDK that can run the repository's `net8.0` projects (`dotnet`).
- Node.js and npm. CI uses Node.js 20; use a currently supported release locally.
- The `sqlite3` command-line tool.
- Playwright dependencies and its managed Chromium/FFmpeg binaries.
- `ffprobe` for mandatory recording-duration verification.

Install the JavaScript dependencies and browser binaries from the repository root:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
```

The browser installation is per user and may download several hundred megabytes. The demo can fall back to Google Chrome on macOS when managed Chromium is absent, but installing the managed browser is the repeatable path and also supplies Playwright's video tooling.

## Record the walkthrough

Choose a private artifact directory outside every Git repository or worktree. The runner rejects paths below any Git checkout, including missing child directories and external symlinks that resolve into a checkout.

```sh
export INVOICE_DEMO_ARTIFACT_DIR=/absolute/private/path/invoice-demo
npm run demo:invoice --prefix Simulators/playwright
```

The browser is headless by default. To watch the run in a visible browser:

```sh
INVOICE_DEMO_HEADLESS=0 \
INVOICE_DEMO_ARTIFACT_DIR=/absolute/private/path/invoice-demo \
npm run demo:invoice --prefix Simulators/playwright
```

The command starts both applications on unused `127.0.0.1` ports, creates and seeds a temporary SQLite database, records the walkthrough, then stops the complete application process groups and deletes the temporary runtime directory.

## Outputs

The private artifact directory contains:

- `ui-walkthrough.webm` — the 1440 by 900, 3-6 minute real-time UI walkthrough. It shows the public Company invoice choice, secure status-page handoff, field-by-field Czech entry and review, save result, invalid and valid Croatian OIB paths, and visibly locked post-issuance controls.
- `billing-rules-explainer.webm` — the separate 1-3 minute explainer for below-1-kWh suppression, normal billing at or above 1 kWh, and the defensive provider-minimum guard. It distinguishes visible UI behavior from backend decisions.
- `01-company-invoice-choice.png` through `06-issued-invoice-locked.png` — numbered, full-page screenshots of each checkpoint.
- `server.log` and `management.log` — local application logs for diagnosis.
- `manifest.json` — viewing order, creation time, loopback runtime URLs, output filenames, measured recording durations, synthetic fixture reservation IDs, privacy mode, and explicit successful checks for the persisted buyer snapshots, all locked controls, and nonempty PNG/WebM files. It identifies the legacy `walkthrough.webm` rapid montage as superseded.

Existing files with these names may be replaced. Treat the directory as private even though the fixtures are synthetic because application logs are diagnostic artifacts and are not intended for Git.

## Safety controls

The runner supplies an isolated `InvoiceDemo` environment to both applications, which prevents .NET's development-only project user secrets from loading. It removes inherited invoice, Stripe, Sentry, SMTP, email, and owner-report settings before launch. Its explicit controls are:

- SQL Server is disabled with an empty `ConnectionStrings__SqlServer`; a new temporary SQLite file is used instead.
- Invoice integration is disabled with `Invoices__Enabled=false`, and inherited `Invoices__ERacuni*` settings are removed.
- Stripe uses the repository's mock services with `Stripe__UseMockServices=true`, a `mock_test_key`, synthetic `.example.test` email, local diagnostics, and a loopback return URL. Inherited Stripe settings are removed.
- Sentry is disabled with empty `SENTRY_DSN` and `Sentry__Dsn` values after every inherited `SENTRY_DSN` and `Sentry__*` setting is removed.
- Customer email is disabled with `Notifications__EnableCustomerEmails=false`; SMTP credentials and from, reply-to, and BCC addresses are cleared. Any notification sink is temporary.
- Owner-report email and scheduling are disabled with `Email__EnableOwnerReportEmails=false` and `OwnerReportSchedule__Enabled=false`; their SMTP and recipient settings are cleared.
- Both HTTP listeners and all manifest URLs use dynamically allocated `127.0.0.1` endpoints.
- Seeded companies, addresses, emails, tax identifiers, payment references, and reservation IDs are fixed synthetic fixture values.

Mock Stripe can restore sessions and payment intents from `mock-stripe-store.json` in its configured diagnostics directory. The demo seeds that file inside the temporary runtime directory so its fixed reservations cross the real save boundary without a live provider. This restore path is local mock/test support: it is constructed only when `Stripe__UseMockServices=true` and does not change live Stripe or invoice-provider behavior.

After a successful run, inspect the manifest before sharing any evidence:

```sh
node -e 'const fs=require("fs"); const p=process.env.INVOICE_DEMO_ARTIFACT_DIR+"/manifest.json"; const m=JSON.parse(fs.readFileSync(p)); for (const u of Object.values(m.runtime)) { const x=new URL(u); if (x.hostname!=="127.0.0.1") throw new Error(`non-loopback URL: ${u}`); } console.log(m)'
git status --short
```

Open the PNG files at natural resolution, then play `ui-walkthrough.webm` followed by `billing-rules-explainer.webm` at 1x speed. Confirm that cursor movement, clicks, typed values, captions, result pauses, and locked controls are understandable without implementation context. The runner fails unless `ffprobe` measures the UI walkthrough between 180 and 360 seconds and the billing explainer between 60 and 180 seconds. The artifact directory is outside the checkout, so no generated artifact should appear in `git status`.
