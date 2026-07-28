# 0002 - Foreign VAT validation and VIES verification

Status: accepted

## Context

The confirmed company-invoice snapshot historically stored Croatian OIB values with strict checksum validation and foreign tax identifiers as trimmed free-form text. A value explicitly marked as a VAT registration needs a stable provider-facing representation, country-aware local rejection before checkout, and optional evidence from the European Commission VAT Information Exchange System (VIES).

VIES is an external dependency. It can time out, return an unavailable member-state response, or be globally unavailable. Checkout and charging must not depend on that availability.

## Decision

1. Keep Croatian OIB behavior unchanged.
2. Keep foreign identifiers not marked as VAT registrations unchanged apart from existing surrounding-whitespace trimming.
3. Validate marked foreign VAT registrations against a project-owned table derived from the European Commission's published formats. Require the selected-country prefix, remove presentation spaces/dots/hyphens, uppercase the result, and retain both original and canonical values.
4. Use `EL` for a Greek company selected as `GR`, and `XI` for Northern Ireland selected as `GB`.
5. Use the canonical VAT value in Stripe metadata and e-racuni invoice drafting.
6. Make VIES verification explicitly configurable and disabled by default. When enabled, call the official REST `check-vat-number` operation after local validation and before Stripe.
7. Send only `countryCode` and the prefix-free `vatNumber`. Do not send company, address, billing, or requester data.
8. Persist local validation status plus VIES `Valid`, `Invalid`, `Unavailable`, or disabled `NotChecked`, with a nullable checked time and a reference bounded to 100 characters.
9. Treat every remote outcome as non-blocking. Show a warning for `Invalid`, an informational message for `Unavailable`, and no alert for `Valid` or `NotChecked`.
10. Keep the VIES reference and VAT identifiers out of the anonymous public status response.

Sources:

- [European Commission VIES technical information](https://ec.europa.eu/taxation_customs/vies/#/technical-information)
- [European Commission public VIES REST schema](https://ec.europa.eu/assets/taxud/vow-information/swagger_publicVAT.yaml)
- [European Commission VIES FAQ and VAT-number formats](https://ec.europa.eu/taxation_customs/vies/faq.html)

## Package evaluation

[`vies-dotnet-api` 3.1.0](https://www.nuget.org/packages/vies-dotnet-api/3.1.0) was evaluated but not adopted. It supports current .NET targets and offers a broad VIES client/validator surface, but its country validators implement checksum algorithms beyond the structure published by the European Commission and version 3.1.0 adds a `Polyfill` dependency. This application only needs a small auditable format table and one bounded REST operation, so a direct adapter keeps the trust and dependency surface narrower.

The project-owned validator deliberately claims structural validation, not tax-authority or checksum proof. VIES remains the optional registry evidence source.

## Consequences

- Invalid local VAT input is rejected before VIES and Stripe.
- VIES outages cannot stop payment or charging.
- Enabling VIES adds one bounded outbound request per locally valid foreign VAT checkout.
- Existing reservations and generic identifiers remain readable without backfill.
- Format changes published by the European Commission require a reviewed update to the local table and its fixtures.
- Tests must never contact live VIES; HTTP behavior is exercised through fake handlers and coordinator fakes.
