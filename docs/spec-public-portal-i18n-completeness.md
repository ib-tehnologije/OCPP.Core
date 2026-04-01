# Public Portal I18N Completeness Spec

Suggested execution order: `3/5`

This spec removes the remaining mixed-language and hardcoded-English behavior from the public charging flow. It applies to `Public/Map`, `Public/Start`, and `Payments/PublicStatus`.

## 1. Problem Statement

The current public portal already has a language switcher, but several important strings still bypass it:

- hardcoded labels in Razor views
- controller-generated English availability/error messages
- raw status words such as `Unknown`, `Available`, `Occupied`
- connector fallback labels such as `Connector 1`
- status-page messages while charging is in progress
- start-page microcopy such as `Tap to switch`, `Selected connector`, and idle-fee fragments

The result is mixed-language pages, especially on German and other non-English language selections.

## 2. Translation Model

### 2.1 Supported languages

Keep the existing supported public languages:

- `hr`
- `en`
- `sl`
- `it`
- `de`
- `fr`

### 2.2 One normalized public status vocabulary

Normalize public portal state to these codes only:

- `available`
- `free`
- `occupied`
- `reserved`
- `offline`
- `faulted`
- `waiting`
- `charging`
- `paused_vehicle`
- `paused_charger`
- `completed`
- `error`

Mapping rules:

- `Available` and `Preparing` -> `available`
- `Available` plus `FreeChargingEnabled` in the specific free-charging banner context -> `free`
- `Occupied`, `Charging`, `SuspendedEV`, `SuspendedEVSE`, `Finishing`, open transaction lock -> `occupied`
- reservation lock -> `reserved`
- `Faulted` -> `faulted`
- `Unavailable`, empty, stale, or `Unknown` -> `offline`

Do not render raw OCPP strings directly in public UI text.

## 3. Implementation Rules

### 3.1 No localized English literals from controllers

Controllers and public view models must stop carrying final user-facing English sentences as the primary state representation.

Required pattern:

- return machine-readable status/reason/message keys
- translate those keys in the public UI layer

Apply this to:

- `PublicController.BuildAvailabilityMessage`
- `PublicController` start-page validation errors
- `PaymentsController` public R1 and stop/start-facing messages
- any public-status error and hint payload derived from upstream reservation state

Minimum model additions where needed:

- `StatusCode`
- `AvailabilityMessageKey`
- `ErrorMessageKey`
- optional numeric/string interpolation values for counts, minutes, or windows

### 3.2 Static Razor text

Every user-visible public string in these views must go through the public translation system:

- `Views/Public/Map.cshtml`
- `Views/Public/Start.cshtml`
- `Views/Payments/PublicStatus.cshtml`
- `_PublicPortalLayout.cshtml`

Rules:

- use `data-i18n` for static text nodes
- use `data-i18n-placeholder` for placeholders
- do not leave fallback English literals in the visible markup except for safe non-user-facing technical values like URLs or GUIDs

### 3.3 Dynamic text and interpolation

Extend `wwwroot/js/public-portal.js` with interpolation support.

Required helper behavior:

- `textFor(lang, key)` remains for simple keys
- add `formatText(lang, key, values)` for templated strings using token replacement such as `{free}`, `{total}`, `{minutes}`, `{window}`, `{connector}`

Use interpolation for:

- `{free}/{total} available`
- `grace {minutes} min`
- `no idle fee during {window}`
- fallback connector labels such as `Connector {connector}`

### 3.4 Language switching after page load

Changing the language in the current page must also update dynamic text that was already rendered before the switch.

Required approach:

- dynamic elements carry normalized codes or interpolation data in `data-*` attributes
- the public translation script has one pass that reapplies both static and dynamic translations whenever language changes

Apply this to:

- selected connector badge and helper text
- map card availability summaries
- map popup status labels
- public status page badges, hints, stop-state text, and invoice panel labels

## 4. Required Translation Inventory

At minimum, add or normalize keys for:

- common connector fallback labels
- public status labels listed in section 2.2
- map card status summary and idle-fee fragments
- start-page connector-selection helper text
- start-page pricing and idle-fee fragments
- start-page free-charging notice
- start-page validation and availability errors
- public status lifecycle steps, badges, hints, stop button states, invoice section, and R1 form result messages

Do not create ad-hoc keys for raw English phrases that represent the same concept. Use one key per concept and reuse it everywhere.

## 5. Automated and Manual Validation

Automated coverage:

- unit tests for raw-status -> normalized-status-code mapping
- unit tests for availability/error code mapping
- JS tests for interpolation and dynamic re-translation on language change
- a string-inventory check that fails if `Views/Public`, `Views/Payments/PublicStatus.cshtml`, or `public-portal.js` contain newly introduced hardcoded user-facing English strings outside the translation dictionaries

Manual validation:

- switch between `HR`, `EN`, `SL`, `IT`, `DE`, and `FR` on:
  - `Public/Map`
  - `Public/Start`
  - `Payments/PublicStatus`
- confirm there are no mixed English fragments during:
  - normal available start flow
  - occupied connector flow
  - offline/error flow
  - active charging flow
  - completed session flow
  - R1 invoice request flow

## 6. Out of Scope

This spec does not include:

- EVSE routing/QR compatibility
- the visual redesign/PWA pass
- email notification translation work outside the public portal

