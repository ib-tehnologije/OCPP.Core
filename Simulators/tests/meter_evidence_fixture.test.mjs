import assert from "node:assert/strict";
import test from "node:test";

import {
  SYNTHETIC_MAX_POWER_KW,
  buildOfferedPowerSample16,
  buildOfferedPowerSample2x,
  createPlausibleMeterTimeline,
} from "../lib/meter_evidence_fixture.mjs";

test("builds protocol-valid trustworthy offered-power evidence", () => {
  assert.equal(SYNTHETIC_MAX_POWER_KW, 22);
  assert.deepEqual(buildOfferedPowerSample16(), {
    value: "22000",
    measurand: "Power.Offered",
    unit: "W",
  });
  assert.deepEqual(buildOfferedPowerSample2x(), {
    value: 22000,
    measurand: "Power.Offered",
    unitOfMeasure: { unit: "W" },
  });
});

test("creates a monotonic meter timeline whose energy steps fit the offered capacity", () => {
  const timeline = createPlausibleMeterTimeline(new Date("2026-09-24T12:00:00Z"));

  assert.deepEqual(timeline, {
    startedAtUtc: "2026-09-24T11:40:00Z",
    firstMeterAtUtc: "2026-09-24T11:45:00Z",
    secondMeterAtUtc: "2026-09-24T11:50:00Z",
    liveMeterAtUtc: "2026-09-24T11:55:00Z",
    idleMeterAtUtc: "2026-09-24T11:58:00Z",
    terminalAtUtc: "2026-09-24T12:00:00Z",
  });

  const firstElapsedHours = 5 / 60;
  const secondElapsedHours = 5 / 60;
  assert.ok(0.677 <= SYNTHETIC_MAX_POWER_KW * firstElapsedHours);
  assert.ok(1.234 - 0.677 <= SYNTHETIC_MAX_POWER_KW * secondElapsedHours);
  assert.ok(1.2 - 0.6 <= SYNTHETIC_MAX_POWER_KW * (2 / 60));
});
