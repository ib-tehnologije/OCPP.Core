# Selected-station night energy tariff: investigation (30 September 2026)

> Status: research supporting the [design recommendation](2026-09-30-night-energy-tariff-design.md). Line numbers refer to the inspected commit below and may have drifted.

## Finding and scope

**Observed:** At the inspected OCPP.Core commit, public charging has one `ChargePoint.PricePerKwh`, copied once into `ChargePaymentReservation.PricePerKwh`. Final energy charge multiplies all delivered kWh by that snapshot. A selected-station rate that changes at 22:00 during an active session is therefore not represented by the current model. This is a design gap, not evidence of a production billing defect. No rate, station, effective date, or allocation policy has been approved here.

**Required outcome:** At selected stations only, price energy by a frozen, auditable day/night rule for the local half-open window [22:00, 07:00) in Europe/Zagreb, including sessions spanning a boundary. Keep existing fixed session, occupancy/idle, meter safeguard, payment and legacy-session behavior coherent. Resident eligibility and the separate 20:00–08:00 idle-fee waiver are outside this request.

## Source versions and checks

- OCPP.Core HEAD `cb9f098cf86ab07d269d9b8a251e3965e95db9c5` (shallow/grafted checkout). No tracked changes made. `gh pr list --repo ib-tehnologije/OCPP.Core --state all --search 'night tariff'` returned `[]` on 30 September 2026; this search is not a complete proof that no related PR exists under another title.
- The operator's private deployment configuration was inspected read-only. Its compose file pins server and management images by digest and supplies payment/invoice configuration. It contains no night energy tariff implementation. Deployment changes will be needed only when a separately approved release is prepared.
- Read source, tests, model, and portal code; checked Europe/Zagreb 2026 DST interval durations with Python `zoneinfo`: spring night 8 elapsed hours, autumn night 10. A focused `dotnet test --no-restore` invocation exited 0 with no test count or results, so it is **not** evidence that tests ran or passed. No live chargers, payment provider, invoice provider, production data, build, deployment or tariff mutation was used.

## Code evidence (observed facts)

| Concern | Evidence and current behavior |
|---|---|
| Station/connector price | `OCPP.Core.Database/ChargePoint.cs` holds a station-level `PricePerKwh`, plus fixed session/usage/owner fields; no connector price or night rule. `OCPP.Core.Management/Controllers/HomeController.ChargePoint.cs` lines 151, 244 edit that price. `OCPP.Core.Management/Views/Home/ChargePointDetail.cshtml` line 197 has one price input. `OCPP.Core.Management/Controllers/PublicController.cs` lines 282, 322 and public Map/Start views show one price. |
| Reservation and hold | `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs` lines 137–200 copies price/fees/limits into reservation and computes `MaxAmountCents` from max kWh times that one price plus usage/session maxima. `OCPP.Core.Management/Controllers/PublicController.cs` lines 333–335 independently estimates the hold. Reservation fields are in `OCPP.Core.Database/ChargePaymentReservation.cs`. |
| Meter timestamps/evidence | OCPP 1.6 `ControllerOCPP16.MeterEvidence.cs` lines 13–49 sorts energy groups by meter timestamp and uses fallback event timestamp; OCPP 2.0.1/2.1 equivalents do likewise. `MeterEvidenceProcessor.cs` lines 70–225 normalizes units, checks timestamp regression, monotonicity, physical maximum and authorization limit, then persists latest `AcceptedMeterKwh/AtUtc`; terminal value becomes `MeterStop`. Rejections/fallback/review are recorded in `MeterEvidenceAnomaly`. `Transaction.cs` stores start/stop/latest accepted values, not a durable accepted interval series. No guaranteed meter cadence or server setting for energy sample interval was found. The BootNotification 300-second setting is heartbeat, not energy-meter cadence. |
| Settlement and fixed fees | `FinancialSettlementCalculator.cs` lines 18–80 calculates total kWh from `MeterStop - MeterStart`, then one price times total; `MinimumSessionFeeKwh` default 1.0 actually suppresses **all** energy, session and usage charges below the threshold or with invalid delivered energy (lines 27–59, 115–165). `PaymentFlowOptions.cs` lines 31–41 states the threshold and minimum capture. `IdleFeeCalculator.cs` separately computes occupancy/idle minutes and an optional local excluded window. Owner reporting uses aggregate `Transaction.EnergyCost` in `OwnerReportService.cs`. |
| Capture, recovery, invoice | `StripePaymentCoordinator.cs` lines 1400–1550 guards meter evidence and computes settlement; lines 1590–1700 captures and persists breakdown. Normal capture may clamp requested amount to PaymentIntent authorization, so hold adequacy matters. `FinancialRecoverySettlementAssessor.cs` lines 72–145 recalculates from reservation price and accepted meter evidence. `InvoiceDraftBuilder.cs` lines 58–84 emits one energy line with total kWh and reservation unit price; it adds session and time fee lines separately. `MeterEvidenceSettlementGuard.cs` blocks review-required/malformed evidence and preserves a legacy completed-row path. Invoice provider VAT/product configuration is deployment-specific; do not infer approved tax treatment from source defaults. |
| Live display/receipt | `OCPPMiddleware.cs` lines 2816–2872 returns one reservation price, live meter/energy and persisted transaction cost. `OCPP.Core.Management/Views/Payments/PublicStatus.cshtml` lines 930–945 estimates live energy as `sessionEnergy * pricePerKwh`, then combines fees; after charging it favors persisted cost. `EmailNotificationService.cs` lines 110–125 presents aggregate energy cost. A split tariff requires the display and receipt to show the same authoritative priced breakdown or explicitly label provisional estimates. |

**Interpretation:** The code has accepted endpoint evidence, but no persisted per-boundary accepted readings. Later recomputation from mutable station price or retained message logs cannot prove a historical split. The exact sampling cadence at selected chargers remains unverified without safe charger configuration/export evidence.

## Smallest coherent design recommendation (proposal, not current behavior)

1. Add optional, selected-station day/night energy tariff configuration with stable rule identity/version, effective start and rates/currency/tax metadata. Leave `PricePerKwh` as the legacy day/default price for unselected stations and old reservations. Do not enable it until station set and terms are approved. Station-level scope matches current pricing; connector-specific targeting would require an additional explicit requirement.
2. At checkout, freeze the applicable rule/version and all prices and fee inputs on the reservation; calculate the energy hold using the highest applicable price for all authorized kWh, plus existing fee maxima. Persist rule snapshot and an immutable accepted-meter interval/allocation ledger tied to reservation and transaction (UTC timestamps, local boundary and offset, start/end cumulative kWh, accepted/rejected provenance, day/night allocated kWh, algorithm/version, rounding and totals). A rejected sample must never become billable evidence. Do not rewrite a captured row when station configuration changes.
3. Use one deterministic pricing component for provisional status, final settlement and recovery. Integrate downstream of existing `MeterEvidenceProcessor`/settlement guard, not around them. Require the allocated kWh sum to equal the guarded delivered total within meter tolerance; if a boundary cannot be defensibly allocated under the approved rule, hold for review or apply the explicitly approved fallback. Preserve current below-1-kWh and minimum capture handling unless the operator explicitly changes policy. Round monetary totals once at the agreed level so invoice lines sum exactly to captured cents.
4. Extend server status API and public Map/Start/Status views to show both rates, the local window and an estimated/current breakdown; keep an explicit provisional label until terminal evidence. Extend invoice draft to distinct day/night energy lines with quantities and amounts from the immutable ledger, or another approved presentation that reconciles exactly to capture. Email/receipt, owner report and recovery paths must consume that ledger. Maintain backward compatibility for single-price reservations and historical invoices.
5. The deployment repository only needs versioned configuration/image rollout after approval, with a disabled-by-default release path and rollback that does not alter already-frozen reservations. No production action is part of this investigation.

## Boundary semantics and unresolved allocation policy

**Proposed rule:** classify each UTC instant by converting it to Europe/Zagreb local civil time. Night means local time >=22:00 or <07:00. 22:00 belongs to night; 07:00 belongs to day. Generate daily boundary instants with time-zone rules and split accepted cumulative-meter intervals at those instants. On spring DST, nonexistent 02:00–02:59 contributes no elapsed energy time; on autumn DST, both occurrences of 02:00–02:59 are night. Never assume a night is always nine elapsed hours. The alternative is a fixed UTC schedule or a fixed elapsed duration; those would produce different customer prices around DST and require a separate business decision.

A meter pair bracketing a boundary measures only its **total delta**, not energy at the boundary. Options requiring operator approval: (A) linear time allocation within the accepted interval, explicitly an estimate; (B) conservative allocation of an ambiguous delta at the higher/day rate (customer-unfavorable where night is cheaper); (C) customer-favorable lower/night rate; (D) defer to review until a trustworthy boundary sample exists. Endpoint timestamp assignment is another possible convention, but misattributes energy across the boundary. Pick a maximum acceptable sample gap and policy for missing/late/out-of-order samples. A clock-aligned meter request might improve evidence but charger support/cadence is unverified and cannot be assumed. Preserve raw/accepted sample provenance and estimated flags for replay and customer explanation.

## Synthetic examples — illustrative unapproved rates

Assume EUR 0.40/kWh day, EUR 0.25/kWh night, no fees, valid >=1 kWh delivered, and exact readings at boundaries unless stated. Values are examples, **not tariffs**. For illustrations with 1 kWh/hour, meter deltas are hypothetical measured interval totals; uniform power within any bracketing interval is an allocation assumption, not a measured fact.

| Case (Europe/Zagreb local) | Allocated energy | Energy amount |
|---|---:|---:|
| Before night, 21:00–22:00 | 1 day | EUR 0.40 |
| Cross 22:00, 21:00–23:00, with boundary reading | 1 day + 1 night | EUR 0.65 |
| Cross 07:00, 06:00–08:00, with boundary reading | 1 night + 1 day | EUR 0.65 |
| Full ordinary night, 22:00–07:00 | 9 night | EUR 2.25 |
| Two nights, day 1 21:00 to day 3 08:00 at 1 kWh/hour | 18 night + 17 day = 35 | EUR 11.30 |
| Spring DST, 2026-03-28 22:00 to 2026-03-29 07:00 | 8 elapsed hours, 8 night | EUR 2.00 |
| Autumn DST, 2026-10-24 22:00 to 2026-10-25 07:00 | 10 elapsed hours, 10 night | EUR 2.50 |

Missing boundary sample: accepted cumulative readings 10 kWh at 21:50 and 12 kWh at 22:10 measure a 2 kWh **total**. Linear time allocation estimates 1 day + 1 night = EUR 0.65. Assigning all 2 kWh to day yields EUR 0.80; all to night yields EUR 0.50. None of these identifies the actual energy consumed before 22:00. A late 21:55 reading arriving after an accepted 22:10 reading is rejected by the current timestamp-regression safeguard and must not silently revise the bill; a review/replay rule is needed if late evidence should be considered. At 0.8 kWh delivered, the existing default threshold makes the **entire** session no-charge, despite a calculated illustrative energy subtotal; keep that result unless separately approved.

## Bounded implementation/verification matrix

| Area | Outcome and focused verification |
|---|---|
| Data/config | Selected stations opt in; unselected and old single-price reservations retain exact outcomes. Migration/snapshot check for SQL Server, SQLite local behavior; freeze effective rule and replay after later config edits. |
| Time/allocation | 21:59:59/22:00 and 06:59:59/07:00; multiple nights; 2026 spring/fall DST and both fall repeated hours; UTC offsets, missing boundary, long gap, late/out-of-order/duplicate samples. Assert allocated total equals accepted meter delta and marks estimates. |
| Meter safeguard | OCPP 1.6, 2.0.1 and 2.1 accepted, fallback and review-required paths; no billing from anomalous values; missing terminal evidence and meter limit guards remain effective. |
| Finance | Worst-case hold, capture <= authorization with no silent truncation, below-1-kWh no-charge, minimum positive capture, fixed session/idle fees unchanged, owner commission/revenue totals, exact cent reconciliation. Test normal completion and financial recovery against the same frozen ledger. |
| Customer/invoice | Map/Start and live Status communicate both rates/window and provisional allocation; final receipt/email and retail/R1 invoice lines reconcile with captured cents, tax treatment and provider rules. Check legacy single-rate invoice and refund/correction behavior. |
| Rollout | Feature disabled until approved rates/stations/date; staged non-production synthetic replay first; separately approved deployment and rollback, with already-started reservations locked to their frozen rule. |

## Decisions and handoff

**Needed from the operator before billable behavior can be implemented:** exact station IDs; day and night rates and currency; tax-inclusive/exclusive meaning and VAT/product treatment; approved boundary-gap allocation/fallback and maximum gap; customer display/receipt wording for estimates and DST; effective date/time and whether a session already in progress at activation inherits the new rule; rollout/rollback owner. Confirm that current below-1-kWh and fixed-fee rules stay as observed.

**Material uncertainty:** no live charger cadence or boundary sample guarantees were inspected; the branch is a shallow historical snapshot, and the PR title search cannot establish all later work. The deployment checkout pins current images but was not used to infer live runtime version. The exact legal invoice representation/tax treatment requires business/provider validation.

**Recommended next step:** obtain those decisions from the operator, then run a bounded implementation task across OCPP.Core (model/API/portal/settlement/invoice/recovery/tests) and a separate governed deployment task when ready. No implementation PR, tariff activation or production action was performed.
