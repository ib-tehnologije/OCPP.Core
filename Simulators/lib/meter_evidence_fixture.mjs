export const SYNTHETIC_MAX_POWER_KW = 22;

function isoUtcAtOffset(referenceAt, offsetMinutes) {
  const timestamp = new Date(referenceAt.getTime() + offsetMinutes * 60_000);
  return timestamp.toISOString().replace(/\.\d{3}Z$/, "Z");
}

export function createPlausibleMeterTimeline(referenceAt = new Date()) {
  if (!(referenceAt instanceof Date) || !Number.isFinite(referenceAt.getTime())) {
    throw new TypeError("referenceAt must be a valid Date");
  }

  return {
    startedAtUtc: isoUtcAtOffset(referenceAt, -20),
    firstMeterAtUtc: isoUtcAtOffset(referenceAt, -15),
    secondMeterAtUtc: isoUtcAtOffset(referenceAt, -10),
    liveMeterAtUtc: isoUtcAtOffset(referenceAt, -5),
    idleMeterAtUtc: isoUtcAtOffset(referenceAt, -2),
    terminalAtUtc: isoUtcAtOffset(referenceAt, 0),
  };
}

export function buildOfferedPowerSample16(maximumPowerKw = SYNTHETIC_MAX_POWER_KW) {
  return {
    value: String(maximumPowerKw * 1000),
    measurand: "Power.Offered",
    unit: "W",
  };
}

export function buildOfferedPowerSample2x(maximumPowerKw = SYNTHETIC_MAX_POWER_KW) {
  return {
    value: maximumPowerKw * 1000,
    measurand: "Power.Offered",
    unitOfMeasure: { unit: "W" },
  };
}
