# Guarded financial recovery design

**Date:** 2026-08-15

**Status:** Approved for implementation

**Scope:** Operator-initiated recovery for terminal payment reservations and missing invoices

## Problem

Rare partial failures can leave one of three durable inconsistencies:

1. a terminal charging transaction has authoritative meter evidence while its reservation is missing the final energy and billable breakdown;
2. a terminal, unused reservation still has a provider authorization that requires release; or
3. a captured reservation has no locally confirmed invoice even though a provider submission may already have succeeded.

Normal completion and maintenance flows must remain unchanged for healthy rows. Recovery must be allowlisted, dry-run first, safe after restart and concurrent execution, and must fail closed whenever evidence or provider state is ambiguous. The repository must never contain real customer, payment-provider, invoice-provider, or transaction identifiers.

## Chosen approach

Add an operator-only console project that consumes an explicit JSON manifest. The command defaults to dry-run and emits a sanitized report. Mutation requires both `--execute` and the SHA-256 digest of the exact manifest supplied through `--confirm-sha256`. The command reloads every row immediately before any mutation and evaluates the same guards again.

The implementation has three independently guarded operations:

- `recover-settlement` derives the final energy and billable breakdown from the linked persisted transaction, reservation pricing snapshot, timestamps, and fee configuration. It refuses missing meter-stop, negative or reversed meter readings, missing or contradictory reservation linkage, incomplete timing evidence, prior capture inconsistencies, or any value that would need estimation.
- `release-authorization` accepts only a terminal `Abandoned` or `Failed` reservation with no transaction, no delivered energy, no captured amount, no invoice evidence, and a provider payment intent currently reporting a positive capturable amount. Execution arms and invokes the existing durable authorization-release state machine; the recovery layer does not bypass its ownership, idempotency, retry, or provider-state guards.
- `recover-invoice` accepts only a completed, captured reservation with a complete persisted billing breakdown and linked terminal transaction. It uses a deterministic submission key and exact provider transaction reference. Local history is checked first; an existing submitted/external result is returned, while an unfinished or unknown attempt triggers provider lookup before any create.

No mode automatically discovers candidates. Each reservation must appear in the manifest, and an operation can only affect that reservation.

## Alternatives considered

### Authenticated recovery HTTP endpoint

Rejected because it would create a long-lived network mutation surface and require new authorization, abuse protection, and deployment controls unrelated to the incident recovery.

### Scheduled background reconciliation

Rejected because implicit discovery and recurring execution make the scope harder to prove. Historical financial rows must remain untouched unless an operator explicitly allowlists them.

## Evidence and settlement calculation

The settlement assessor receives a reservation, its exact linked transaction, and the same payment-flow options used by normal completion. It returns either a complete immutable calculation or a list of blocking reasons; it never returns a partial calculation.

Required evidence includes:

- the transaction ID matches the reservation link;
- transaction start and stop evidence are present and ordered;
- meter start and stop are non-negative and stop is not below start;
- reservation pricing and fee snapshots are non-negative and currency is present;
- the reservation is not already captured for a contradictory amount;
- idle/usage fee inputs required by the configured anchor are present.

The calculation code used by recovery is shared with normal completion so the same persisted input produces the same energy, session-fee, usage-fee, idle-fee, and total values. Recovery cannot substitute a maximum, average, configured estimate, or operator-entered amount for missing domain evidence.

## Authorization release

Dry-run validates local evidence and reports that provider state still requires a just-in-time check. Execution reloads the row, repeats the local checks, then delegates to `ReconcileTerminalPaymentAuthorization`. The existing coordinator owns the provider retrieval, metadata ownership check, `requires_capture` validation, deterministic idempotency key, durable attempt record, retry budget, and indeterminate-state handling.

Recovery may arm only an otherwise eligible terminal row. It does not cancel checkout sessions, payment intents that are not positively `requires_capture`, active transactions, reservations with consumption, or reservations with invoice evidence.

## Duplicate-safe invoice recovery

`InvoiceSubmissionLog.SubmissionKey` is a deterministic value scoped to provider and reservation. A filtered unique database index makes a single local submission lineage authoritative without rewriting historical logs.

Submit-mode processing follows this order:

1. build and validate the invoice draft;
2. derive the exact provider transaction reference and submission key;
3. check local submitted/external history;
4. acquire the unique local submission lineage and a time-bounded database lease before a create attempt;
5. for an existing unfinished, failed, or provider-unknown lineage, perform an exact provider lookup;
6. if exactly one provider record matches, persist its identifiers and mark the lineage submitted;
7. if the lookup is definitively not found, a recovery execution may attempt create using the same deterministic provider reference;
8. if lookup fails, is ambiguous, or returns an unrecognized response, mark the lineage `ProviderUnknown` and stop.

Any exception after a provider call may have crossed the network boundary. Such an attempt is `ProviderUnknown`, never automatically safe to retry. A current lease blocks concurrent creation; an expired lease can only be reacquired atomically after provider lookup. Repeated calls, concurrent processes, process restart, a successful provider response followed by local persistence failure, and provider timeout all converge on lookup-before-create.

## Provider lookup boundary

The e-racuni client queries `SalesInvoiceList` by the invoice draft's deterministic `orderReference`. The adapter recognizes only the documented root-array response and treats transport errors, non-success status, object/error envelopes, schema drift, duplicate exact matches, and missing required identifiers as `Unknown`. Only one exact `orderReference` match is `Found`; an empty root array is `NotFound`. Request and response logging stays sanitized according to the existing invoice integration rules.

## Operator command and reporting

The manifest contains a schema version and entries with only operation type plus reservation identifier. The operator keeps real manifests outside the repository. A repository example uses synthetic identifiers.

Dry-run exits non-zero if any allowlisted item is blocked or ambiguous and writes no database or provider state. Execute mode prints the manifest digest, operation, sanitized reservation identifier, decision, and blocking reason. It never prints provider secrets, payment tokens, customer data, raw invoice payloads, or private handoff details.

## Testing and verification

Tests use synthetic in-memory data and fake provider boundaries. They cover:

- exact settlement derivation and every fail-closed evidence boundary;
- dry-run zero-mutation behavior and execute confirmation mismatch;
- authorization release refusal for active, consumed, invoiced, captured, or linked rows;
- invoice local preflight, deterministic uniqueness, repeated execution, concurrent acquisition, restart, provider-found, provider-not-found, ambiguous lookup, timeout, and response/persistence partial failure;
- migration metadata and filtered unique-index shape;
- the full solution test suite and build.

No verification step uses live payment, invoice, customer, or production database data.
