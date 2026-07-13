# Foreign Company Invoice Buyer Data Design

## Goal

Allow Croatian and foreign companies to confirm invoice buyer data for a charging reservation without making VIES or another registry a prerequisite for payment or invoice issuance.

## Approved Scope

The reservation owns an immutable confirmed buyer snapshot. Croatian buyers continue to provide an OIB that passes the existing checksum validation. Foreign buyers provide an uppercase ISO 3166-1 alpha-2 country, legal name, street, postal code, city, billing email, one free-form tax/VAT/company identifier, an explicit indication of whether that identifier is a VAT registration, and an optional legal registration number. Application limits are 200 characters for legal name and street, 32 for postal code, 100 for city, 64 for tax identifier and registration number, and 254 for email.

VIES and national registries are outside this change. Foreign identifiers are trimmed, preserved as submitted, rejected when they contain control characters or line breaks, and stored as unverified. Stripe remains payment-only. After confirmation, the customer endpoint rejects attempts to change the snapshot; corrections follow the invoice provider's supported correction or reissue process.

## Architecture

Typed nullable fields on `ChargePaymentReservation` store the confirmed snapshot and timestamp. This is preferable to Stripe-only metadata because invoice issuance must not depend on mutable third-party metadata, and preferable to an opaque JSON blob because EF length constraints, migration review, querying, and tests remain explicit.

The request path uses a dedicated validator/normalizer shared by the server endpoint and coordinator. The coordinator validates first, finds the reservation, rejects a conflicting already-confirmed snapshot, persists the snapshot atomically, and only then mirrors compatibility metadata to Stripe. Retrying the identical confirmed request is idempotent.

`InvoiceDraftBuilder` reads confirmed reservation fields first and falls back to legacy Stripe metadata only for old reservations. `ERacuniInvoiceRequestFactory` maps legal name, street, postal code, city, uppercase country, tax identifier, email, and `Registered` versus `Unknown`. It omits `buyerCode`; the optional legal registration number remains internal because the provider contract has no dedicated supported field.

## Customer Flow

The public status page presents country and buyer-type controls, the complete buyer form, a review summary, and an explicit confirmation checkbox. Croatian selection requires OIB. Foreign selection relabels the identifier, allows the customer to say whether it is a VAT registration, and never performs a blocking registry lookup. The controller sends the full confirmed payload and returns sanitized validation/provider errors.

## Error Handling and Safety

Validation errors are field-safe and do not echo credentials or raw provider payloads. Provider failures are sanitized before reaching the browser. A failed Stripe metadata mirror does not erase the durable confirmed snapshot; retries remain idempotent. No live provider call, deployment, credential change, payment mutation, or customer notification is part of implementation.

## Testing

Tests cover Croatian checksum behavior, foreign normalization and control-character rejection, exact length limits, required fields, confirmation, idempotent retry, immutable conflicting retry, reservation persistence, legacy fallback, e-racuni field mapping, VAT-registration semantics, omitted `buyerCode`, controller payload forwarding, Razor review/confirmation hooks, migration metadata, and the full solution build/test suite.
