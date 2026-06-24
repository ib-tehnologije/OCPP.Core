import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const startViewPath = path.join(repoRoot, "OCPP.Core.Management/Views/Public/Start.cshtml");
const mapViewPath = path.join(repoRoot, "OCPP.Core.Management/Views/Public/Map.cshtml");
const publicResultViewPath = path.join(repoRoot, "OCPP.Core.Management/Views/Payments/PublicResult.cshtml");
const publicStatusViewPath = path.join(repoRoot, "OCPP.Core.Management/Views/Payments/PublicStatus.cshtml");
const publicPortalScriptPath = path.join(repoRoot, "OCPP.Core.Management/wwwroot/js/public-portal.js");
const publicLayoutPath = path.join(repoRoot, "OCPP.Core.Management/Views/Shared/_PublicPortalLayout.cshtml");
const publicManifestPath = path.join(repoRoot, "OCPP.Core.Management/wwwroot/manifest.webmanifest");
const publicWwwrootPath = path.join(repoRoot, "OCPP.Core.Management/wwwroot");
const publicFaqPath = path.join(publicWwwrootPath, "faq.html");

test("public start view exposes localization hooks for connector and pricing copy", () => {
  const startView = fs.readFileSync(startViewPath, "utf8");
  const requiredSnippets = [
    'data-i18n="start.step.start"',
    'data-i18n="start.step.wait"',
    'data-i18n="start.step.charge"',
    'data-i18n="start.step.done"',
    'data-i18n="start.step.error"',
    "data-i18n-status",
    'data-i18n-default-text="start.tapToSwitch"',
    'data-i18n-selected-text="start.connectorSelected"',
    'data-i18n="start.energy"',
    'data-i18n="start.sessionFee"',
    'data-i18n="start.idleFee"',
    'data-i18n="start.chargedFromSessionStart"',
    'data-i18n="start.grace"',
    'data-i18n="start.max"',
    'data-i18n="start.maxEnergy"',
    'data-i18n="start.preAuthorization"',
    'data-i18n="start.finalAmountNote"',
    'data-i18n-template="status.connectorFallback"',
    "data-i18n-message",
  ];

  for (const snippet of requiredSnippets) {
    expect(startView, `Start.cshtml should contain ${snippet}`).toContain(snippet);
  }
});

test("public result, status, and map views expose localization hooks for dynamic copy", () => {
  const mapView = fs.readFileSync(mapViewPath, "utf8");
  const publicResultView = fs.readFileSync(publicResultViewPath, "utf8");
  const publicStatusView = fs.readFileSync(publicStatusViewPath, "utf8");
  const publicLayout = fs.readFileSync(publicLayoutPath, "utf8");
  const requiredSnippets = [
    [publicLayout, "defaultPublicPortalTagline"],
    [publicLayout, 'data-i18n="@(localizeDefaultTagline ? "brand.tagline" : null)"'],
    [publicLayout, 'data-i18n="@(localizeDefaultFooterLegalLine ? "brand.tagline" : null)"'],
    [publicResultView, 'data-i18n="status.step.start"'],
    [publicResultView, 'data-i18n="@headingKey"'],
    [publicResultView, 'data-i18n-message="@message"'],
    [mapView, 'data-i18n-title="map.centerOnLocation"'],
    [mapView, 'data-i18n-aria-label="map.clearSearch"'],
    [mapView, 'data-i18n-status="@cp.Status"'],
    [mapView, "map.availableCount"],
    [mapView, "map.noUsableCoordinates"],
    [publicStatusView, 'fillTemplate("status.connectorFallback"'],
    [publicStatusView, "translateMessage(data.failureMessage)"],
    [publicStatusView, 't("status.r1.saved")'],
  ];

  for (const [source, snippet] of requiredSnippets) {
    expect(source, `view should contain ${snippet}`).toContain(snippet);
  }
});

test("public portal translations localize default branding copy", async ({ page }) => {
  const publicPortalScript = fs.readFileSync(publicPortalScriptPath, "utf8");

  await page.route("http://public.local/Public/Start?lang=it", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "text/html; charset=utf-8",
      body: `<!doctype html>
        <html lang="en">
          <body>
            <p id="tagline" data-i18n="brand.tagline">Use fast chargers with clear pricing and instant session start.</p>
            <div id="footer-tagline" data-i18n="brand.tagline">Use fast chargers with clear pricing and instant session start.</div>
            <script>${publicPortalScript}</script>
          </body>
        </html>`,
    });
  });

  await page.goto("http://public.local/Public/Start?lang=it");

  await expect(page.locator("#tagline")).toHaveText("Usa le colonnine rapide con prezzi chiari e avvio immediato della sessione.");
  await expect(page.locator("#footer-tagline")).toHaveText("Usa le colonnine rapide con prezzi chiari e avvio immediato della sessione.");
});

test("public portal translations localize Italian start-flow labels", async ({ page }) => {
  const publicPortalScript = fs.readFileSync(publicPortalScriptPath, "utf8");

  await page.route("http://public.local/Public/Start?lang=it", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "text/html; charset=utf-8",
      body: `<!doctype html>
        <html lang="en">
          <body>
            <nav class="public-session-nav">
              <button data-i18n="start.step.start">① Start</button>
              <button data-i18n="start.step.wait">② Wait</button>
              <button data-i18n="start.step.charge">③ Charge</button>
              <button data-i18n="start.step.done">④ Done</button>
              <button data-i18n="start.step.error">⑤ Error</button>
            </nav>
            <span id="connector-status-text" data-i18n-status="Available">Available</span>
            <span class="connector-card-status" data-i18n-status="Available">Available</span>
            <span
              id="connector-meta-default"
              data-i18n-option-state="default"
              data-i18n-default-text="start.tapToSwitch"
              data-i18n-selected-text="start.connectorSelected"
              data-default-text="Tap to switch"
              data-selected-text="Selected connector">Tap to switch</span>
            <span
              id="connector-meta-selected"
              data-i18n-option-state="selected"
              data-i18n-default-text="start.tapToSwitch"
              data-i18n-selected-text="start.connectorSelected"
              data-default-text="Tap to switch"
              data-selected-text="Selected connector">Selected connector</span>
            <div data-i18n="start.energy">Energy</div>
            <div data-i18n="start.sessionFee">Session fee</div>
            <div data-i18n="start.idleFee">Idle fee</div>
            <span data-i18n="start.chargedFromSessionStart">Charged from session start</span>
            <span data-i18n="start.grace">grace</span>
            <span data-i18n="start.max">max</span>
            <div data-i18n="start.maxEnergy">Max. energy</div>
            <div data-i18n="start.preAuthorization">Pre-authorization (est.)</div>
            <div data-i18n="start.finalAmountNote">Final amount = energy + session fee + idle fee (if any). Difference from pre-auth is refunded automatically.</div>
            <script>${publicPortalScript}</script>
          </body>
        </html>`,
    });
  });

  await page.goto("http://public.local/Public/Start?lang=it");

  await expect(page.locator(".public-session-nav button")).toHaveText([
    "① Avvio",
    "② Attesa",
    "③ Ricarica",
    "④ Fine",
    "⑤ Errore",
  ]);
  await expect(page.locator("#connector-status-text")).toHaveText("Disponibile");
  await expect(page.locator(".connector-card-status")).toHaveText("Disponibile");
  await expect(page.locator("#connector-meta-default")).toHaveText("Tocca per cambiare");
  await expect(page.locator("#connector-meta-selected")).toHaveText("Connettore selezionato");
  await expect(page.locator('[data-i18n="start.energy"]')).toHaveText("Energia");
  await expect(page.locator('[data-i18n="start.sessionFee"]')).toHaveText("Costo sessione");
  await expect(page.locator('[data-i18n="start.idleFee"]')).toHaveText("Costo inattività");
  await expect(page.locator('[data-i18n="start.chargedFromSessionStart"]')).toHaveText("Addebitato dall'inizio sessione");
  await expect(page.locator('[data-i18n="start.grace"]')).toHaveText("tolleranza");
  await expect(page.locator('[data-i18n="start.max"]')).toHaveText("max");
  await expect(page.locator('[data-i18n="start.maxEnergy"]')).toHaveText("Energia max");
  await expect(page.locator('[data-i18n="start.preAuthorization"]')).toHaveText("Pre-autorizzazione (stim.)");
  await expect(page.locator('[data-i18n="start.finalAmountNote"]')).toHaveText("Importo finale = energia + costo sessione + costo inattività (se presente). La differenza dalla pre-autorizzazione viene rimborsata automaticamente.");
});

test("public portal translations localize Italian result, status, map, and server messages", async ({ page }) => {
  const publicPortalScript = fs.readFileSync(publicPortalScriptPath, "utf8");

  await page.route("http://public.local/Payments/PublicStatus?lang=it", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "text/html; charset=utf-8",
      body: `<!doctype html>
        <html lang="en">
          <body>
            <button data-i18n="status.step.wait">② Wait</button>
            <span id="status-waiting" data-i18n="status.badge.waiting">Waiting for charger...</span>
            <span id="status-r1" data-i18n="status.r1.subtitle">Need an R1 invoice? You can submit company details now or later using this secure session link.</span>
            <span id="connector-fallback" data-i18n-template="status.connectorFallback" data-i18n-param-id="1">Connector 1</span>
            <span id="result-heading" data-i18n="result.paymentAuthorized">Payment authorized</span>
            <span id="result-message" data-i18n-message="Charging session will start shortly.">Charging session will start shortly.</span>
            <span id="start-message" data-i18n-message="This connector is currently in use. Please stop the active session first or choose another connector.">This connector is currently in use. Please stop the active session first or choose another connector.</span>
            <span id="r1-message" data-i18n-message="For an R1 (company) invoice, please enter your OIB (11 digits).">For an R1 (company) invoice, please enter your OIB (11 digits).</span>
            <span id="map-status" data-i18n-status="Offline">Offline</span>
            <span id="map-grace" data-i18n="map.grace">grace</span>
            <button id="gps" title="Center on your location" data-i18n-title="map.centerOnLocation">GPS</button>
            <button id="clear" aria-label="Clear search" data-i18n-aria-label="map.clearSearch">×</button>
            <script>${publicPortalScript}</script>
          </body>
        </html>`,
    });
  });

  await page.goto("http://public.local/Payments/PublicStatus?lang=it");

  await expect(page.locator('[data-i18n="status.step.wait"]')).toHaveText("② Attesa");
  await expect(page.locator("#status-waiting")).toHaveText("In attesa del caricatore...");
  await expect(page.locator("#status-r1")).toHaveText("Ti serve una fattura R1? Puoi inviare i dati aziendali ora o più tardi usando questo link sicuro della sessione.");
  await expect(page.locator("#connector-fallback")).toHaveText("Connettore 1");
  await expect(page.locator("#result-heading")).toHaveText("Pagamento autorizzato");
  await expect(page.locator("#result-message")).toHaveText("La sessione di ricarica inizierà a breve.");
  await expect(page.locator("#start-message")).toHaveText("Questo connettore è attualmente in uso. Ferma prima la sessione attiva o scegli un altro connettore.");
  await expect(page.locator("#r1-message")).toHaveText("Per una fattura R1 (azienda), inserisci il tuo OIB (11 cifre).");
  await expect(page.locator("#map-status")).toHaveText("Offline");
  await expect(page.locator("#map-grace")).toHaveText("tolleranza");
  await expect(page.locator("#gps")).toHaveAttribute("title", "Centra sulla tua posizione");
  await expect(page.locator("#clear")).toHaveAttribute("aria-label", "Cancella ricerca");
});

test("public portal app icons use only the supplied bitmap favicon surfaces", () => {
  const manifest = JSON.parse(fs.readFileSync(publicManifestPath, "utf8"));
  const layout = fs.readFileSync(publicLayoutPath, "utf8");
  const faq = fs.readFileSync(publicFaqPath, "utf8");

  expect(manifest.name).toBe("EV.Charge");
  expect(manifest.short_name).toBe("EV.Charge");
  expect(manifest.icons.map((icon) => icon.src)).toEqual([
    "/img/public-portal-icon-192.png",
    "/img/public-portal-icon-512.png",
  ]);
  expect(layout).not.toContain("public-portal-icon.svg");
  expect(fs.existsSync(path.join(publicWwwrootPath, "img/public-portal-icon.svg"))).toBe(false);

  const referencedIconPaths = [
    "/favicon.png",
    "/apple-touch-icon.png",
    "/img/public-portal-icon-180.png",
    ...manifest.icons.map((icon) => icon.src),
  ];

  for (const iconPath of referencedIconPaths) {
    expect(fs.existsSync(path.join(publicWwwrootPath, iconPath))).toBe(true);
  }
  expect(faq).not.toContain('href="/apple-touch-icon.png"');
});
