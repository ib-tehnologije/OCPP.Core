import { execFile } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

function sqlQuote(value) {
  if (value === null || value === undefined) {
    return "NULL";
  }

  return `'${String(value).replaceAll("'", "''")}'`;
}

async function runSqlite(dbPath, sql) {
  await execFileAsync("sqlite3", [dbPath, sql]);
}

export function buildWindowAroundNowUtc(spanMinutes = 5) {
  const now = new Date();
  const start = new Date(now.getTime() - spanMinutes * 60 * 1000);
  const end = new Date(now.getTime() + spanMinutes * 60 * 1000);
  const pad = (value) => String(value).padStart(2, "0");
  return `${pad(start.getUTCHours())}:${pad(start.getUTCMinutes())}-${pad(end.getUTCHours())}:${pad(end.getUTCMinutes())}`;
}

export async function seedTestStack(dbPath, { cp16Id = "Test1234", cp20Id = "TestAAA", cp21Id = "TestBBB" } = {}) {
  const now = new Date().toISOString();
  const sql = `
BEGIN TRANSACTION;
DELETE FROM PublicPortalSettings;
INSERT INTO PublicPortalSettings (
  BrandName,
  Tagline,
  SupportEmail,
  CanonicalBaseUrl,
  QrScannerEnabled,
  IdleFeeExcludedWindowEnabled,
  IdleFeeExcludedWindow,
  CreatedAtUtc,
  UpdatedAtUtc
) VALUES (
  'OCPP Core',
  'Automated browser and simulator testing',
  'support@example.test',
  'http://127.0.0.1:8082',
  1,
  0,
  NULL,
  ${sqlQuote(now)},
  ${sqlQuote(now)}
);
DELETE FROM ChargePoint WHERE ChargePointId IN (${sqlQuote(cp16Id)}, ${sqlQuote(cp20Id)}, ${sqlQuote(cp21Id)});
INSERT INTO ChargePoint (
  ChargePointId,
  Name,
  FreeChargingEnabled,
  PricePerKwh,
  UserSessionFee,
  OwnerSessionFee,
  OwnerCommissionPercent,
  OwnerCommissionFixedPerKwh,
  MaxSessionKwh,
  StartUsageFeeAfterMinutes,
  MaxUsageFeeMinutes,
  ConnectorUsageFeePerMinute,
  UsageFeeAfterChargingEnds
) VALUES
(
  ${sqlQuote(cp16Id)},
  'Simulator OCPP 1.6',
  0,
  0.35,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  0,
  180,
  0.20,
  1
),
(
  ${sqlQuote(cp20Id)},
  'Simulator OCPP 2.0.1',
  0,
  0.35,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  0,
  180,
  0.20,
  1
),
(
  ${sqlQuote(cp21Id)},
  'Simulator OCPP 2.1',
  0,
  0.35,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  0,
  180,
  0.20,
  1
);
COMMIT;`;

  await runSqlite(dbPath, sql);
}

export async function setIdleWindow(dbPath, { enabled, window }) {
  const now = new Date().toISOString();
  const sql = `
UPDATE PublicPortalSettings
SET IdleFeeExcludedWindowEnabled = ${enabled ? 1 : 0},
    IdleFeeExcludedWindow = ${enabled ? sqlQuote(window) : "NULL"},
    UpdatedAtUtc = ${sqlQuote(now)};
`;

  await runSqlite(dbPath, sql);
}

export async function setIdleWindowForScenario(dbPath, scenario) {
  if (!dbPath) {
    return;
  }

  if (scenario === "quiet_hours_idle_excluded") {
    await setIdleWindow(dbPath, {
      enabled: true,
      window: buildWindowAroundNowUtc(),
    });
    return;
  }

  await setIdleWindow(dbPath, {
    enabled: false,
    window: null,
  });
}
