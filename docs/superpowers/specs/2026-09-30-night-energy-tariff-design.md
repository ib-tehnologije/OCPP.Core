# Selected-station night energy tariff — design recommendation

> Status: design only (30 September 2026). Not implemented, not enabled and no rates or stations approved. Implementation requires the business decisions listed below and a separate maintainer decision to build it.

Source and detailed evidence: [research](2026-09-30-night-energy-tariff-research.md). Reviewed OCPP.Core `cb9f098cf86ab07d269d9b8a251e3965e95db9c5` and the operator's private deployment configuration, read-only. This is a design, not an approved tariff or implementation.

## Finding

Current station `PricePerKwh` is copied to `ChargePaymentReservation.PricePerKwh` and used for the entire settled session. Accepted meter endpoints are guarded, but a durable accepted interval history and tariff allocation are absent. The current live display and invoice likewise assume one rate. A rate chosen only at session start cannot satisfy the requested active-session 22:00 change.

## Recommended smallest coherent change

Use an optional station-level, versioned energy schedule for selected stations. At reservation creation freeze the rule, day/night rates and fee inputs; preserve legacy single-rate reservations. Classify instants in Europe/Zagreb with the half-open local window [22:00, 07:00): 22:00 is night and 07:00 is day. Store accepted meter intervals, provenance, allocation method and estimate flags with immutable day/night quantities and cents. Reuse one pricing calculation for live estimate, guarded settlement and financial recovery. Compute the hold using the highest applicable rate across authorized energy plus existing fees; reconcile capture, final receipt/invoice lines and owner reporting to the same frozen ledger. Preserve the current below-1-kWh no-charge rule, session fee and separate idle-fee window unless specifically changed. Non-enabled stations and prior reservations keep single-rate behavior. Do not add resident eligibility from the informal use case.

A meter delta whose samples straddle a tariff boundary does not reveal the true energy at that instant. Linear time allocation is an estimate, not a measurement. Alternative approved rules could favor day, favor night, or stop for review. Set an approved maximum gap and handling for late, missing, duplicate and out-of-order samples before billable implementation.

## Worked examples and verification scope

The detailed table in the research document uses illustrative, unapproved EUR 0.40 day / EUR 0.25 night: 21:00–23:00 with a true 22:00 boundary reading gives 1 day + 1 night kWh = EUR 0.65; 06:00–08:00 gives the same split; a full ordinary night gives 9 night kWh = EUR 2.25. Two nights at 1 kWh/hour give 18 night + 17 day kWh. The 2026 spring and autumn DST nights give 8 and 10 elapsed night hours respectively. A 2 kWh gap from 21:50 to 22:10 could be EUR 0.50, 0.65 or 0.80 under different allocation policies, none proven by the two endpoint readings.

Implementation/QA scope after decisions: selected-station opt-in and frozen-version migration; exact 22:00/07:00 boundaries, midnight, multiple nights and both DST changes; accepted/rejected/fallback meter evidence for OCPP 1.6/2.0.1/2.1; ambiguous gaps and replay after config edits; authorization/capture, threshold and fixed-fee regression; day/night API, Map/Start/Status, receipt/email and invoice cent reconciliation; legacy sessions and non-enabled stations. Test SQL Server migration and local SQLite behavior. Deployment is a separate, explicitly approved stage.

## Decisions required before implementation or activation

1. Exact station IDs, day/night rates, currency, tax-inclusive/exclusive meaning and VAT/invoice product treatment.
2. Boundary-gap allocation/fallback, maximum accepted gap, and handling of late evidence.
3. Customer-facing pre-start, provisional live and final breakdown wording, including estimate and DST explanation.
4. Effective date/time, policy for sessions already running at activation, rollout/rollback owner and station sequence.

Confirm that existing below-1-kWh and fixed/idle-fee behavior remains. These are business choices; no rates, station list or allocation policy were inferred from source.

## Checks and limits

Source and deployment configuration were inspected read-only; Python `zoneinfo` checked DST elapsed durations. A focused `dotnet test --no-restore` returned exit 0 without test results, so no passing test suite is claimed. No live charger cadence, production version, provider behavior, tax validation or deployment was verified. The PR title search was narrow. Next step: collect the business decisions if implementation is requested, then implement with independent QA; a later exact-artifact production release requires separate maintainer approval.