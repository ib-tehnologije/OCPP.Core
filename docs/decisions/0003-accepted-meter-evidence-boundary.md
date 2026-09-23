# 0003 - Accepted meter-evidence boundary

Status: accepted

## Context

Terminal meter values can be malformed, regress, reset, or jump beyond what a charger could physically deliver. Payment, cleanup, recovery, and invoice retries must not reinterpret the same suspect value differently or turn it into billed energy.

`MaxEnergyKwh` cannot answer whether a meter value is physically possible. It is an authorization and auto-stop boundary, and a real session can exceed it before stopping.

## Decision

All active OCPP 1.6, 2.0.1, and 2.1 meter paths use one shared processor. A transaction stores its latest accepted cumulative meter value, observation time, precision tolerance, and the highest accepted charger-reported `Power.Offered` evidence when available.

The processor rejects malformed, non-finite, negative, unsupported-unit, timestamp-regressing, non-monotonic, and physically impossible observations without replacing the accepted projection. OCPP 2.x unit multipliers are applied before comparison. Timestamp groups are processed chronologically, using the overall energy register when present and otherwise summing coherent phase registers, so an earlier valid group remains available when a later terminal group is suspect. A physically impossible terminal observation can settle only from an existing accepted projection; the unobserved tail is never estimated.

Suspect observations are stored separately in `MeterEvidenceAnomaly` with their raw value, unit and multiplier, candidate offered-power raw value, unit and position-matched multiplier sequence, protocol, source, timestamp, outcome, reason, and projection/capacity context. A deterministic evidence key and unique database index make exact replays idempotent while keeping differently encoded energy or power evidence distinct. Settlement, connector-Available cleanup, cleanup retry, financial recovery, and invoice entry points recheck the durable meter-evidence state.

If a finite terminal value increases at all but there is no previously accepted physical-capacity basis, or if invalid terminal evidence has no accepted projection, automated financial completion stops at `ReviewRequired`. Numeric precision tolerance remains limited to monotonic and capacity calculations; it cannot establish missing physical capacity. The authorization boundary does not make a smaller increase physically trustworthy. A physically plausible authorization overshoot also remains `ReviewRequired`; it is not clamped to `MaxEnergyKwh`.

An offered-power increase observed only with the candidate reading is not assumed to have applied throughout the preceding interval. If the increase is plausible only under that changed power evidence, the sample requires review rather than being mislabeled physically impossible or silently accepted.

Historical completed captures without projection columns retain their immutable persisted invoice breakdown. New captures cannot create that legacy shape because the settlement guard runs before provider capture, and explicit financial recovery requires an `Accepted` or `FallbackAccepted` projection with a timestamp that matches the settlement meter.

## Consequences

- Invalid terminal evidence cannot trigger automatic capture, invoice creation, or completion notification without a safe accepted fallback.
- A safe fallback may bill only energy observed by the last accepted cumulative reading.
- `MaxEnergyKwh` remains separate from physical plausibility.
- Replays reuse the same projection and do not duplicate anomaly rows in the normal retry path.
- SQL Server deployments require migrations `AddMeterEvidenceSafeguard` and `AddMeterEvidencePowerCandidateProvenance`; SQLite test databases must be recreated through their existing `EnsureCreated()` workflow.
- Operators must review sessions whose physical plausibility cannot be established from accepted evidence.
