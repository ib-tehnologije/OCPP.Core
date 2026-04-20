# Open Items Classification

Purpose: keep one public-safe, client-neutral view of the main remaining work areas in this repository.

This document is intentionally limited to information that is already visible from the repo itself. It does **not** include private meeting notes, support threads, commercial discussions, customer-specific charger IDs, payment incidents, or internal negotiation context.

## Categories

- `Bug / correction debt`
  - behavior that is wrong, incomplete, or visibly inconsistent in already-shipped flows
- `Feature / enhancement`
  - additive product capability, UX expansion, compatibility work, or redesign
- `Ops / maintenance / hardening`
  - production safety, rollout discipline, reliability, observability, backups, and recovery work
- `Reference / mostly implemented`
  - documentation that explains the current system or captures historical fit-gap analysis, but is not itself an active backlog

## Current Public Backlog View

| Area | Status | Classification | Primary docs |
| --- | --- | --- | --- |
| Public portal correctness | Partially open | `Bug / correction debt` | `docs/spec-public-portal-core-corrections.md` |
| Public portal translation completeness | Partially open | `Bug / correction debt` with some polish | `docs/spec-public-portal-i18n-completeness.md` |
| EVSE routing and QR compatibility expansion | Open | `Feature / enhancement` | `docs/spec-public-evse-routing-and-qr.md` |
| Public portal visual redesign and PWA | Open | `Feature / enhancement` | `docs/spec-public-portal-design-and-pwa.md` |
| Email notification visual refresh | Open | `Feature / enhancement` | `docs/spec-email-notifications-refresh.md` |
| Payments / reservations hardening | Ongoing | `Ops / maintenance / hardening` | `docs/ocpp-payments-reservations-hardening-spec.md` |
| Production rollout / go-live discipline | Ongoing | `Ops / maintenance / hardening` | `docs/GoLive-Rollout-Checklist.md` |
| Stripe public-payments baseline | Mostly implemented | `Reference / mostly implemented` | `docs/Public-Payments-and-QR.md`, `docs/payments-and-reservations-overview.md`, `docs/stripe-payments.md` |
| Invoicing baseline | Mostly implemented, with deeper follow-up possible | `Reference / mostly implemented` | `docs/invoicing/e-racuni-gap-analysis.md`, `docs/invoicing/invoice-fields-inventory.md` |

## What Looks Materially Landed Already

These areas appear to have a meaningful implementation baseline in the current repo and should not be treated as completely greenfield work:

- public connector selection on the start page
- public connector codes and public-display-code rendering
- same-browser Stripe checkout recovery and resume flow
- delayed R1/company-data submission flow
- payment reservation lifecycle baseline
- public map / start / payment status flow as a working shipped path

This does **not** mean there is no correction debt. It means the base product exists and remaining work should be triaged more carefully than “not built yet”.

## What Still Looks Open From Repo State

These items still read as genuinely open or only partially closed when comparing the docs to the current code:

- public subtitle / status normalization / aggregate availability cleanup
- remaining narrow-screen and mobile overflow issues in public pages
- clearer idle-fee disclosure wording in the public flow
- translation and mixed-language cleanup across public pages
- EVSE route and QR compatibility expansion beyond the existing baseline
- larger UX/design refresh work
- rollout, observability, backup, and reliability hardening beyond the current Compose baseline

## Practical Triage Rule

New asks should be classified into one of these buckets before estimating or implementing:

1. `Bug fix`
   - already-supported flow behaves incorrectly
2. `Feature request`
   - additive capability, redesign, compatibility expansion, or new UX
3. `Maintenance / operations`
   - production hardening, deployment safety, monitoring, backup, rollback, or runbook work

This helps avoid treating every future request as unpaid original-scope debt.

## Recommended Reading Order

If someone needs to orient quickly:

1. `docs/spec-public-portal-core-corrections.md`
2. `docs/spec-public-portal-i18n-completeness.md`
3. `docs/Public-Payments-and-QR.md`
4. `docs/payments-and-reservations-overview.md`
5. `docs/ocpp-payments-reservations-hardening-spec.md`
6. `docs/GoLive-Rollout-Checklist.md`

## Notes

- This file is a classification aid, not a contractual statement.
- A document being listed under `Feature / enhancement` or `Ops / maintenance / hardening` does not automatically mean it should be done next; it only means it should not automatically be treated as missing base delivery.
- A document being listed under `Bug / correction debt` means it is a reasonable candidate for correction backlog review, not that every line item inside it must be accepted without prioritization.
