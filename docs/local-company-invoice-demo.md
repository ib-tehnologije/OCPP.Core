# Local Company Invoice Demo

This walkthrough records the real local management and server applications while they save synthetic Czech and Croatian company invoice details. It also demonstrates Croatian OIB validation and the locked state after a synthetic invoice marker exists. The runner uses a disposable SQLite database and writes the recording outside the repository.

## Prerequisites

- A .NET SDK that can run the repository's `net8.0` projects (`dotnet`).
- Node.js and npm. CI uses Node.js 20; use a currently supported release locally.
- The `sqlite3` command-line tool.
- Playwright dependencies and its managed Chromium/FFmpeg binaries.

Install the JavaScript dependencies and browser binaries from the repository root:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
```

The browser installation is per user and may download several hundred megabytes. The demo can fall back to Google Chrome on macOS when managed Chromium is absent, but installing the managed browser is the repeatable path and also supplies Playwright's video tooling.

## Record the walkthrough

Choose a private artifact directory outside the repository. Do not use a path below the checkout: the runner rejects repository paths, including external symlinks that resolve back into the repository.

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

- `walkthrough.webm` — the 1440 by 900 browser recording.
- `01-company-invoice-choice.png` through `06-issued-invoice-locked.png` — numbered, full-page screenshots of each checkpoint.
- `server.log` and `management.log` — local application logs for diagnosis.
- `manifest.json` — creation time, loopback runtime URLs, output filenames, synthetic fixture reservation IDs, and the privacy mode summary.

Existing files with these names may be replaced. Treat the directory as private even though the fixtures are synthetic because application logs are diagnostic artifacts and are not intended for Git.

## Safety controls

The runner supplies an isolated environment to both applications and removes inherited invoice, Stripe, SMTP, email, and owner-report settings before launch. Its explicit controls are:

- SQL Server is disabled with an empty `ConnectionStrings__SqlServer`; a new temporary SQLite file is used instead.
- Invoice integration is disabled with `Invoices__Enabled=false`, and inherited `Invoices__ERacuni*` settings are removed.
- Stripe uses the repository's mock services with `Stripe__UseMockServices=true`, a `mock_test_key`, synthetic `.example.test` email, local diagnostics, and a loopback return URL. Inherited Stripe settings are removed.
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

Open the PNG files at natural resolution and play `walkthrough.webm` to verify captions and form values are readable. Confirm that the recording duration is greater than zero with a media inspector such as `ffprobe` when available. The artifact directory is outside the checkout, so no generated artifact should appear in `git status`.
