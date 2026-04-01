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
  const invoiceCp16Id = "INVOICE-R1-16";
  const invoiceCp20Id = "INVOICE-R1-20";
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
DELETE FROM ChargePoint WHERE ChargePointId IN (${sqlQuote(cp16Id)}, ${sqlQuote(cp20Id)}, ${sqlQuote(cp21Id)}, ${sqlQuote(invoiceCp16Id)}, ${sqlQuote(invoiceCp20Id)}, 'MAP-MIXED-01', 'MAP-OFFLINE-01', 'map-case-01');
INSERT INTO ChargePoint (
  ChargePointId,
  Name,
  PublicDisplayCode,
  Description,
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
  UsageFeeAfterChargingEnds,
  Latitude,
  Longitude,
  LocationDescription
) VALUES
(
  ${sqlQuote(cp16Id)},
  'Simulator OCPP 1.6',
  'HR*TTK*900001*01',
  'Simulator-backed 1.6 charger',
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
  1,
  44.8666,
  13.8496,
  'Simulator 1.6 location'
),
(
  ${sqlQuote(cp20Id)},
  'Simulator OCPP 2.0.1',
  'HR*TTK*900001*02',
  'Simulator-backed 2.0.1 charger',
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
  1,
  44.8680,
  13.8510,
  'Simulator 2.0.1 location'
),
(
  ${sqlQuote(cp21Id)},
  'Simulator OCPP 2.1',
  'HR*TTK*900001*03',
  'Simulator-backed 2.1 charger',
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
  1,
  44.8694,
  13.8524,
  'Simulator 2.1 location'
),
(
  ${sqlQuote(invoiceCp16Id)},
  'Invoice R1 OCPP 1.6',
  'HR*TTK*900001*11',
  'Dedicated invoice validation charger for public start',
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
  1,
  44.8650,
  13.8480,
  'Invoice validation 1.6 location'
),
(
  ${sqlQuote(invoiceCp20Id)},
  'Invoice R1 OCPP 2.0.1',
  'HR*TTK*900001*12',
  'Dedicated invoice validation charger for public status',
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
  1,
  44.8640,
  13.8470,
  'Invoice validation 2.0.1 location'
),
(
  'MAP-MIXED-01',
  'Mixed availability',
  'HR*TTK*900099*01',
  'One connector free, one busy',
  0,
  0.45,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  1,
  120,
  0.20,
  1,
  44.8708,
  13.8538,
  'Mixed status validation station'
),
(
  'MAP-OFFLINE-01',
  'Offline validation',
  'HR*TTK*900099*02',
  'Offline public rendering sample',
  0,
  0.45,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  1,
  120,
  0.20,
  1,
  44.8722,
  13.8552,
  'Offline status validation station'
),
(
  'map-case-01',
  'Case-insensitive map',
  'HR*TTK*900099*03',
  'Case mismatch rendering sample',
  0,
  0.45,
  0.50,
  0.00,
  0.00,
  0.00,
  80.0,
  1,
  120,
  0.20,
  1,
  44.8736,
  13.8566,
  'Case-insensitive status validation station'
);
DELETE FROM ConnectorStatus WHERE ChargePointId IN (${sqlQuote(cp16Id)}, ${sqlQuote(cp20Id)}, ${sqlQuote(cp21Id)}, ${sqlQuote(invoiceCp16Id)}, ${sqlQuote(invoiceCp20Id)}, 'MAP-MIXED-01', 'MAP-OFFLINE-01', 'map-case-01', 'MAP-CASE-01');
INSERT INTO ConnectorStatus (ChargePointId, ConnectorId, ConnectorName, LastStatus, LastStatusTime) VALUES
  (${sqlQuote(cp16Id)}, 1, 'Connector 1', 'Available', datetime('now')),
  (${sqlQuote(cp20Id)}, 2, 'Connector 2', 'Available', datetime('now')),
  (${sqlQuote(cp21Id)}, 3, 'Connector 3', 'Available', datetime('now')),
  (${sqlQuote(invoiceCp16Id)}, 1, 'Invoice connector 1', 'Available', datetime('now')),
  (${sqlQuote(invoiceCp20Id)}, 2, 'Invoice connector 2', 'Available', datetime('now')),
  ('MAP-MIXED-01', 1, 'Left connector', 'Available', datetime('now')),
  ('MAP-MIXED-01', 2, 'Right connector', 'Occupied', datetime('now')),
  ('MAP-OFFLINE-01', 1, 'Offline connector', 'Unknown', datetime('now')),
  ('MAP-OFFLINE-01', 2, 'Stale connector', 'Available', datetime('now', '-45 minutes')),
  ('map-case-01', 1, 'Case connector', 'Available', datetime('now'));
PRAGMA foreign_keys = OFF;
UPDATE ConnectorStatus
SET ChargePointId = 'MAP-CASE-01'
WHERE ChargePointId = 'map-case-01' AND ConnectorId = 1;
PRAGMA foreign_keys = ON;
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
