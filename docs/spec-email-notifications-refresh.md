# Email Notifications Refresh Spec

Suggested execution order: `5/5`

This is a standalone server-side presentation refresh for charging emails. It changes the HTML output only and must not alter the payment, reservation, invoice, or receipt business flow.

## 1. Current State

`OCPP.Core.Server/Payments/EmailNotificationService.cs` currently:

- builds all customer emails through the shared `BuildHtmlBody` method
- uses generic `OCPP Core Charging` branding
- uses one simple card layout for all email types
- already has per-event methods that supply the detail rows
- already supports JSON sink output for preview/debug

This means the implementation should replace the shared template output, not the notification triggers.

## 2. Scope and Invariants

Keep unchanged:

- `IEmailNotificationService` method signatures
- all existing call sites and event triggers
- invoice generation logic
- Stripe/payment business rules
- email sink behavior

Change only:

- shared email HTML structure
- brand styling and copy presentation
- optional notification-brand config values

## 3. Branding and Configuration

Extend `Notifications` options with optional presentation settings:

- `BrandName`
- `BrandLogoUrl`
- `PrimaryColor`
- `SecondaryColor`

Defaults:

- `BrandName`: keep current `FromName` if explicit brand name is not set
- `BrandLogoUrl`: empty by default
- `PrimaryColor`: EV.Charge green used by the approved previews
- `SecondaryColor`: warning amber for idle-fee warning states

Logo rule:

- use a publicly reachable HTTPS logo URL in the rendered email
- if no logo URL is configured, fall back to a text-only branded header without broken images

## 4. Template Architecture

Replace the current generic `BuildHtmlBody` output with a shared branded template builder that supports at least these variants:

- standard success/info emails
- warning email for idle-fee warning

Structure requirements:

- branded header with logo or brand text
- clear title and intro section
- fact/details table
- optional CTA button
- optional footer/help copy
- footer note for automated notification

Keep the per-event data assembly methods as the source for detail rows:

- payment authorized
- charging completed
- idle fee warning
- session receipt
- R1 invoice requested
- R1 invoice ready

Do not split these into separate templates unless a variant truly needs structural differences. Shared layout plus event-specific data is preferred.

## 5. Copy and bilingual treatment

The approved previews and notes mention bilingual labels. Implement this as follows:

- support bilingual inline labels within the new template only if the approved preview still requires it at implementation time
- do not change the notification trigger API to carry language
- if bilingual copy is retained, keep it inside the template dictionaries, not scattered across event methods

Decision:

- keep event methods supplying semantic detail rows
- centralize any bilingual or branded label formatting inside the shared template builder

## 6. Validation and Preview Workflow

Use the existing JSON sink plus automated tests to validate the new HTML.

Required checks:

- snapshot or approval-style coverage for all six email types
- assert the rendered HTML contains:
  - brand header
  - title
  - expected detail rows
  - correct CTA link when present
  - warning styling on the idle-fee warning email
- assert the template does not emit broken logo markup when `BrandLogoUrl` is empty

Reference material:

- use the approved Tehnoline preview bundle as the visual/content reference during implementation
- preserve enough test fixtures or snapshots in the repo so later changes do not depend on a local Downloads folder

## 7. Rollout Notes

Server config follow-up:

- add the new optional `Notifications` branding settings to config samples
- do not require them for boot; keep safe defaults

Deployment behavior:

- rebuilding and redeploying `ocpp-server` is sufficient
- no management UI deploy is required for this spec unless shared assets are intentionally reused there

## 8. Out of Scope

This spec does not include:

- invoice logic changes
- payment/reservation logic changes
- public portal UI redesign
- public portal translations outside email copy

