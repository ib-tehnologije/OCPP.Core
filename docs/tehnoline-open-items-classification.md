# Tehnoline Open Items Classification

Purpose: classify the remaining Tehnoline-related docs and notes into:

- `Bug / correction debt`
- `Feature / enhancement`
- `Ops / maintenance / hardening`
- `Reference / already mostly handled`

This is meant to answer a practical question: which items are likely still "our miss" versus which items are simply later product expansion or operational maturity work.

## Summary

The repo does not contain one single authoritative "remaining client requests" file.

The closest backlog-like material lives under `docs/`, but it is a mix of:

- actual public-portal correction work
- later UX/design improvements
- operational hardening notes
- implementation audits that are mostly informational, not outstanding obligations

The most important distinction is:

- `Bug / correction debt`: behavior that is wrong, inconsistent, or visibly incomplete in already-shipped flows
- `Feature / enhancement`: new routes, redesigns, branding refreshes, optional compatibility work, or extra product capability
- `Ops / maintenance / hardening`: production safety and reliability work, not original end-user feature scope

## File-by-file classification

### Bug / correction debt

These are the closest thing to "real misses" or correction work in the current docs.

- `docs/spec-public-portal-core-corrections.md`
  - Classification: `Bug / correction debt`
  - Why:
    - public subtitle source mismatch
    - incorrect aggregate availability rules
    - raw `Unknown` leaking publicly
    - narrow-screen overflow and mobile clipping
    - incomplete idle-fee disclosure
  - Commercial treatment:
    - reasonable to treat as correction backlog for the existing public portal

- `docs/spec-public-portal-i18n-completeness.md`
  - Classification: `Mostly bug/correction debt, partly polish`
  - Why:
    - mixed-language strings in a portal that already exposes supported languages is a real completeness issue
    - hardcoded English fallback labels and messages are not ideal in a shipped multilingual flow
  - Commercial treatment:
    - fixing clearly visible mixed-language output fits correction work
    - full translation cleanup and exhaustive inventory checks are closer to polish

### Feature / enhancement

These read as legitimate next-phase improvements, not corrections to a broken base scope.

- `docs/spec-public-evse-routing-and-qr.md`
  - Classification: `Feature / compatibility enhancement`
  - Why:
    - introduces new `/evse/...` routes
    - adds public-code lookup and broader QR compatibility
    - keeps existing `/cp/...` flow intact, so this is additive
  - Commercial treatment:
    - new scope

- `docs/spec-public-portal-design-and-pwa.md`
  - Classification: `Feature / UX polish`
  - Why:
    - light-theme redesign
    - visual cleanup
    - manifest/installability assets
    - explicitly described as a later-phase design pass
  - Commercial treatment:
    - new scope

- `docs/spec-email-notifications-refresh.md`
  - Classification: `Feature / branding refresh`
  - Why:
    - changes presentation only
    - no business-flow correction
    - brand/logo/styling refresh is optional product polish
  - Commercial treatment:
    - new scope

### Ops / maintenance / hardening

These are valuable and important, but they are not the same thing as missing business features.

- `docs/ocpp-payments-reservations-hardening-spec.md`
  - Classification: `Ops / reliability hardening`
  - Why:
    - production resilience for real chargers
    - stronger reservation lifecycle, idempotency, remote-start correlation, timeout recovery
    - this is the kind of work that becomes important once the system is live at scale
  - Commercial treatment:
    - maintenance / hardening scope
    - if a concrete production bug maps to one of these items, that bug can be handled specifically

- `docs/GoLive-Rollout-Checklist.md`
  - Classification: `Ops / rollout checklist`
  - Why:
    - DNS/TLS/proxy/smoke-test/rollback process
    - not a user-facing feature backlog
  - Commercial treatment:
    - maintenance / operations

### Reference / already mostly handled

These docs are useful for orientation, but they should not be mistaken for an unpaid open-feature list.

- `docs/payments-and-reservations-overview.md`
  - Classification: `Reference`
  - Why:
    - explains implemented lifecycle and current behavior
    - not a backlog

- `docs/stripe-payments-fit-gap.md`
  - Classification: `Technical audit / partial follow-up list`
  - Why:
    - compares implementation to a stricter spec
    - some items were already implemented later
    - remaining gaps are mostly engineering hardening, not explicit client-facing obligations

- `docs/invoicing/e-racuni-gap-analysis.md`
  - Classification: `Historical analysis / partly outdated`
  - Why:
    - captures a broader fiscal/compliance architecture gap than what Tehnoline initially needed
    - actual e-racuni API integration has since been implemented
    - any remaining deeper fiscal modeling would be additional compliance/product work

- `docs/requirements-status/getting-started.md`
  - Classification: `Audit note, mostly done`

- `docs/requirements-status/public-payments-and-qr.md`
  - Classification: `Audit note, mostly done with small additive gaps`
  - Notable gaps:
    - manual QR generation
    - path-style QR compatibility if specifically desired

- `docs/requirements-status/connector-occupancy.md`
  - Classification: `Audit note, mostly done`
  - Notable gaps:
    - richer logging
    - configurable freshness window

- `docs/requirements-status/test-checklist-public-start.md`
  - Classification: `Audit note, mostly done`
  - Notable gaps:
    - manual-only checklist
    - no automated simulator verification

- `docs/requirements-status/razlike.md`
  - Classification: `Not a real backlog`
  - Why:
    - only points out that a path-style QR note existed
    - does not define a substantial requirement set

- `docs/razlike.md`
  - Classification: `Loose note, not a backlog`

## Practical grouping for client conversations

### Reasonable to treat as corrections

- public portal values or statuses that are visibly wrong
- mixed-language strings in supported public languages
- mobile clipping/overflow in already-live public pages
- any invoice/payment behavior that fails in an already-supported production flow

### Reasonable to treat as new scope

- EVSE public-code route system and QR compatibility expansion
- public portal redesign / light theme / PWA
- branded email redesign
- new UX refinements like moving connector selection deeper into the flow
- connector naming/localization enhancements beyond basic correctness

### Reasonable to treat as maintenance / support contract work

- production rollout process
- staging / preprod strategy
- DB backups and restore drills
- monitoring / alerting / dashboards
- payment/reservation hardening for real-world charger behavior
- rollback/runbook/disaster-recovery work

## Recommended stance

If this classification is used commercially, the cleanest message is:

- the base product has been substantially delivered
- some correction debt remains in the public portal and live operational edge cases
- many of the remaining documents describe either future enhancements or production hardening, not unpaid missing scope

That means future asks should be triaged as one of:

1. `Bug fix`
2. `Feature request`
3. `Maintenance / operations`

instead of automatically treating everything as part of the original fixed-price build.
