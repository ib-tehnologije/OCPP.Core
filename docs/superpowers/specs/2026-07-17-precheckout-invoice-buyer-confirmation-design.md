# Pre-checkout Invoice Buyer Confirmation Design

## Problem

The public charging start page currently records only an intent to request a company invoice. The complete buyer form is shown on the secure session status page after Stripe Checkout. Invoice integration runs when the charging reservation completes, so a short session can reach capture and invoice submission before the customer confirms buyer details. In that ordering, the invoice draft can be classified as retail even though the customer selected company invoicing.

## Goals

- Collect and explicitly confirm Croatian or foreign company buyer details before redirecting to Stripe Checkout.
- Persist the confirmed buyer snapshot on `ChargePaymentReservation` before checkout succeeds and before charging can complete.
- Set company-invoice compatibility metadata from the start so completed reservations cannot be misclassified as retail because of buyer-entry timing.
- Keep Stripe payment-only and keep the reservation snapshot as the invoice source of truth.
- Allow customers to opt in to reusing buyer details on the same browser and device without creating an account.
- Preserve legacy invoice-building fallback behavior for reservations created by older application versions.

## Non-goals

- Do not add customer accounts, server-side address books, registry lookup, or VIES validation.
- Do not delay invoice issuance with a new pending job, timeout, or retry workflow.
- Do not change e-racuni provider credentials, submission mode, product mappings, or invoice correction behavior.
- Do not add or change payment, capture, refund, or charging rules.
- Do not add a database migration; the typed buyer snapshot columns already exist.

## Customer Flow

The public start page retains the `Company invoice` checkbox. Selecting it expands the complete buyer form:

- company country;
- legal company name;
- street address;
- postal code;
- city;
- billing email;
- OIB, VAT number, tax identifier, or company identifier;
- optional legal registration number;
- whether the supplied identifier is a VAT registration number;
- explicit confirmation that the invoice details are correct;
- optional `Remember company details on this device` consent.

Country-specific behavior remains unchanged. Croatia requires a checksum-valid 11-digit OIB. Foreign buyers require the full legal name, address, billing email, and identifier dataset. Foreign identifiers remain trimmed, bounded, and unverified.

Submitting the start form with company invoicing selected sends the complete confirmed buyer payload to the server. Validation failure returns to the same page, preserves the submitted values in the view model, and does not create a Stripe Checkout session. Successful validation creates the reservation with its confirmed snapshot and then redirects to Stripe.

The secure status page no longer offers an editable company-buyer form for new sessions. It may show the confirmed company-invoice state, but it must not advertise that company details can be added later. The existing request endpoint remains available for compatibility with reservations or clients created by older application versions.

## Device-local Reuse

Device-local reuse is explicit opt-in. The browser stores buyer fields only when `Remember company details on this device` is selected. The stored record:

- uses a versioned key, `ocpp.invoiceBuyer.v1`;
- contains only the company-buyer fields listed above;
- excludes reservation IDs, charge-point details, payment identifiers, Stripe identifiers, confirmation timestamps, and invoice results;
- is read defensively and ignored when missing, malformed, or unavailable;
- is cleared when the customer disables the remember option and submits the form;
- never replaces server-side validation or confirmation.

When a valid saved record exists, the start page pre-populates the buyer form and shows the remember option as selected. The page must explain that the data is stored only in the current browser and may be visible to other users of a shared device.

## Server and Data Flow

`PublicStartViewModel` and `PaymentSessionRequest` carry the complete buyer contract already used by `PaymentR1InvoiceRequest`. The management controller forwards the submitted fields to `Payments/Create` rather than invoking a second post-checkout buyer update.

`CreateCheckoutSession` validates and normalizes the buyer request with the existing `InvoiceBuyerDataValidator` when company invoicing is selected. It creates the reservation and applies the confirmed buyer snapshot before creating or returning a Stripe Checkout session. A validation failure returns a bounded status and message and must not call Stripe.

For a confirmed company invoice, the Stripe Checkout Session and PaymentIntent metadata contain:

- `invoice_type=R1`;
- Croatian `buyer_oib` when applicable;
- `buyer_country`;
- `buyer_tax_identifier`;
- `buyer_company` when present.

The full address, billing email, registration number, and VAT-registration flag remain on the reservation and flow to e-racuni through `InvoiceDraftBuilder`. Stripe metadata is a compatibility mirror, not the source of truth.

For a retail invoice, no buyer snapshot is created and no company-invoice metadata is added.

## Consistency and Failure Handling

No checkout URL may be returned until the confirmed buyer snapshot and Stripe identifiers are associated with the same reservation. If buyer validation fails, Stripe is not called. If Stripe Checkout creation fails, the existing reservation failure handling remains responsible for leaving a recoverable or terminal reservation state; confirmed buyer data must not be silently converted into retail intent.

The post-checkout compatibility endpoint retains its existing immutability, concurrency, and submitted-invoice guards. New public sessions do not depend on that endpoint.

## Compatibility

- Existing reservations with a confirmed snapshot continue to build from that snapshot.
- Existing legacy reservations without a snapshot continue to fall back to Stripe metadata.
- Older clients may continue using the existing post-checkout request endpoint while its contract remains supported.
- The public browser flow no longer permits a new company-invoice request after checkout, eliminating the timing-dependent retail fallback for new sessions.

## Testing

Automated coverage must prove:

- the start-page form exposes the complete country-aware buyer contract, confirmation, and remember controls;
- company-invoice start requests forward every buyer field;
- invalid or unconfirmed company data fails before any Stripe create call;
- valid Croatian and foreign buyer data is normalized and persisted on the reservation before checkout returns;
- company-invoice Checkout Session and PaymentIntent metadata contain `invoice_type=R1` and the bounded compatibility mirror;
- retail checkout remains unchanged;
- completion builds an R1 invoice from the confirmed snapshot without depending on post-checkout timing;
- localStorage read, write, malformed-data handling, and opt-out clearing follow the versioned contract;
- the public status page no longer offers or advertises late buyer entry for new sessions;
- legacy snapshot and Stripe-metadata fallback tests continue to pass.

Verification includes focused xUnit tests, the full server test suite, a Release build, JavaScript syntax validation, Razor/source contract checks, and existing migration metadata validation.

## Documentation

Update the public feature and operations documentation to state that company buyer details are confirmed before Stripe Checkout, stored on the reservation, optionally remembered in the browser with consent, and mirrored only minimally to Stripe.
