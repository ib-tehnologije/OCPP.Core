# 0004 - e-racuni invoice lookup by date window and order reference

Status: accepted

## Context

Before an invoice create is retried, and before every `recover-invoice` create, the server looks for an invoice the provider may already have issued for the same lineage. The first design called `SalesInvoiceList` with `parameters: { apiTransactionId }`.

The public e-racuni `SalesInvoiceList` documentation lists these filters: `number`, `dateFrom`/`dateTo`, `expirationDateFrom`/`expirationDateTo`, `dateOfSupplyFrom`/`dateOfSupplyUntil`, `deliveryAddress`, `buyer`, `costPosition`, `totalAmount`, `totalCurrency`, `article`, and `status`. `apiTransactionId` is not among them. A read-only check showed that the provider ignores the unknown parameter, returns HTTP 200 `{ "response": { "status": "ok", "result": [...] } }` with its newest 500 invoices, and that list rows carry no `apiTransactionId` field. The lookup therefore never found a match, classified every non-empty list as unknown, and recovery could not succeed. The behavior was fail-closed, so it created no duplicates, but the pre-check proved nothing.

Invoices created by this application send `date` as the reservation capture date in `Invoices:ERacuni:TimeZoneId`, `orderReference` as `<OrderReferencePrefix>-<TransactionId>` (`EVSE-<TransactionId>` by default), `reference` as `<ReferencePrefix>-<payment identifier>` (`STRIPE-...` by default), and the line items that make up the persisted billing total. Unlike `orderReference`, `reference` is a documented `SalesInvoiceList` output field.

## Decision

The lookup sends only the documented `dateFrom` and `dateTo` filters. The window is the create request's invoice `date` minus `Invoices:ERacuni:LookupWindowDaysBefore` through that date plus `Invoices:ERacuni:LookupWindowDaysAfter`. Both default to `1`. A negative offset, an offset of 31 days or more, or a window wider than 31 days stops the lookup before any provider call.

Rows are matched locally, never by a provider filter, so a misread provider filter cannot hide a match. The adapter accepts a root array, a root `result` array, or `response.result`. A `status` other than `ok` is unknown. Each outcome then depends on the page:

- A page of 500 or more rows is treated as truncated and is unknown, even if it contains a match.
- Every row must be an object with a parseable `yyyy-MM-dd` `date` inside the requested window and an `orderReference` property. Otherwise absence cannot be proven, or the provider did not apply the date filter, and the result is unknown.
- A candidate is a row whose trimmed `orderReference` or `reference` equals the expected value, ignoring case. The secondary `reference` check means a row the provider returns with a changed `orderReference` still cannot prove absence. More than one candidate is unknown.
- A candidate matched only by `reference`, or a candidate whose non-empty `reference` contradicts the expected value, is unknown. An empty or missing `reference` does not block a match on `orderReference`.
- Exactly one candidate is found only if its `totalAmount` equals the draft total rounded to cents, its `totalCurrency` equals the document currency, and it has a `number` or document identifier. A candidate with a different or unreadable amount or currency is unknown.
- A complete page in which no row has the expected `orderReference` or `reference` is not found. This is the only outcome that may lead to create.

`totalAmount`, `status`, and other documented filters are not sent, because their exact matching semantics are not documented. A wrong interpretation could produce a false not-found result.

## Consequences

- Retries and `recover-invoice` can now adopt an existing provider invoice or prove its absence within the configured window.
- Ambiguity, duplicates, schema drift, truncation, an ignored date filter, and amount or currency disagreement remain `ProviderUnknown`. They need manual reconciliation and never trigger create.
- The public documentation does not list `orderReference` among `SalesInvoiceList` output fields. If the provider omits it from list rows, every non-empty window is unknown, which is safe but blocks automated recovery. Before relying on recovery, confirm with a read-only provider check that list rows return `orderReference`, `reference`, and `date` exactly as sent.
- If the provider stored a date other than the requested `date`, an invoice outside the window would be invisible. Widen the window rather than bypass the lookup.
- A busy account with 500 or more invoices inside the window cannot prove absence and needs manual reconciliation or a narrower window.
