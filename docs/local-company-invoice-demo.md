# Local Company Invoice Demo

This walkthrough records the real local management and server applications while they collect synthetic Czech and Croatian company invoice details before Stripe checkout. It also explains Croatian OIB validation, confirms that reusable buyer data is not kept in browser storage, and shows the read-only invoice state after checkout. The runner uses a disposable SQLite database, a connected OCPP 1.6 fixture charger, visible cursor movement, human-paced field entry, and readable captions, then writes the recordings outside the repository. Accepted examples cross the real local `Payments/Create` boundary before the browser reaches Mock Stripe.

## Prerequisites

- A .NET SDK that can run the repository's `net8.0` projects (`dotnet`).
- Node.js and npm. CI uses Node.js 20; use a currently supported release locally.
- The `sqlite3` command-line tool.
- Playwright dependencies and its managed Chromium/FFmpeg binaries.
- `ffmpeg` for joining the two real-browser UI segments and `ffprobe` for mandatory recording-duration verification.

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

- `ui-walkthrough.webm` — the 1440 by 900, 3-6 minute real-time UI walkthrough. It shows the public Company invoice choice, field-by-field foreign-company entry, an unconfirmed submission blocked before mock Stripe, explicit confirmation, the local Mock Stripe handoff, server rejection of an invalid Croatian OIB with values preserved, a checksum-valid Croatian example, a fresh browser context with empty storage/form fields, and the read-only post-checkout invoice result. The runner records two real local-portal segments and joins them without changing playback speed.
- `billing-rules-explainer.webm` — the separate 1-3 minute explainer for below-1-kWh suppression, normal billing at or above 1 kWh, and the defensive provider-minimum guard. It distinguishes visible UI behavior from backend decisions.
- `01-company-invoice-choice.png` through `07-issued-invoice-read-only.png` — numbered, full-page checkpoints for the invoice choice, blocked unconfirmed attempt, mock Stripe handoff, rejected invalid OIB, valid OIB, fresh-browser emptiness, and read-only result.
- `server.log` and `management.log` — local application logs for diagnosis.
- `manifest.json` — viewing order, creation time, loopback runtime URLs, output filenames, measured recording durations, synthetic fixture reservation IDs, privacy mode, and explicit successful checks for persisted buyer snapshots, blocked unconfirmed/invalid attempts before mock Stripe, mock checkout handoff, browser-storage absence, fresh-session empty fields, read-only status, and nonempty PNG/WebM files. It identifies the legacy `walkthrough.webm` rapid montage as superseded.

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

The walkthrough compares mock-session counts immediately before and after the unconfirmed foreign submission and invalid Croatian OIB submission. Both attempts must leave the count unchanged. The confirmed foreign and checksum-valid Croatian submissions must each create exactly one local mock session. A second isolated browser context then checks buyer-specific `localStorage` and `sessionStorage` content and requires every fresh buyer input to be empty.

After a successful run, inspect the manifest before sharing any evidence:

```sh
node -e 'const fs=require("fs"); const p=process.env.INVOICE_DEMO_ARTIFACT_DIR+"/manifest.json"; const m=JSON.parse(fs.readFileSync(p)); for (const u of Object.values(m.runtime)) { const x=new URL(u); if (x.hostname!=="127.0.0.1") throw new Error(`non-loopback URL: ${u}`); } console.log(m)'
git status --short
```

Open the seven PNG files at natural resolution, then play `ui-walkthrough.webm` followed by `billing-rules-explainer.webm` at 1x speed. Confirm that cursor movement, clicks, typed values, blocked attempts, preserved values, mock secure-payment handoff, fresh-context storage/form proof, captions, pre-checkout confirmation, and read-only post-checkout result are understandable without implementation context. The runner fails unless `ffprobe` measures the UI walkthrough between 180 and 360 seconds and the billing explainer between 60 and 180 seconds. The artifact directory is outside the checkout, so no generated artifact should appear in `git status`.
