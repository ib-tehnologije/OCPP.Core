# Public Portal Design and PWA Spec

Suggested execution order: `4/5`

This is the later-phase design pass for the public portal. It must be applied only after the core behavior and translation fixes are in place, so visual work does not mask correctness issues.

## 1. Design Source of Truth

Use these references during implementation:

- the approved light-theme demo and HTML notes from the Tehnoline feedback bundle
- the current repo public pages after Specs 1-3 are implemented

Do not treat the provided CSS file as a blind overwrite. Merge the design intentionally onto the corrected public behavior.

## 2. Reconciliation With Current State

Current state in the repo:

- public portal is still primarily dark-themed
- local WIP already compacts the mobile header and adds public-code pills
- `Views/Public/Start.cshtml` still contains inline warning color styling such as `color:#fde68a`
- the public portal does not yet expose manifest/meta/installability assets

Carry forward any behaviorally correct pieces from the local WIP, but do the final design pass only after the functional public specs are settled.

## 3. Visual Scope

### 3.1 Theme direction

Adopt a light public theme with:

- white or near-white surfaces
- emerald accent as the primary action color
- dark readable text
- normal, non-filtered Leaflet map tiles
- focus-visible states preserved for accessibility

Do not restyle the public portal by scattering hardcoded inline colors. Centralize appearance in CSS variables.

### 3.2 Layout consistency

Apply the refreshed design consistently to:

- `Public/Map`
- `Public/Start`
- `Payments/PublicStatus`
- `_PublicPortalLayout`

The final layout must preserve:

- compact mobile header
- public EVSE/code pills
- same-page connector switching
- public status stepper and charging state cards

### 3.3 Inline color cleanup

Remove remaining inline hardcoded presentation values from public pages, especially:

- `color:#fde68a`
- `color:#fecdd3`

Replace them with semantic CSS classes and CSS variables such as:

- `--public-warning`
- `--public-danger`
- `--public-surface-muted`

Scan all public Razor views for inline presentational color values before declaring the pass complete.

### 3.4 QR modal and visibility

Keep the QR modal behavior from the current portal, but make the styling robust in the refreshed theme.

Required rule:

- `[hidden]` state for the QR modal and similar public overlays must be enforced with CSS so hidden modals never flash or remain visible after close

## 4. PWA Scope

This pass is installability metadata only. Do not add offline caching or a service worker in this spec.

### 4.1 Required assets and metadata

Add to the public layout:

- `manifest.webmanifest`
- favicon
- Apple touch icon
- `theme-color`
- `apple-mobile-web-app-capable`
- any required mobile/web-app meta tags for installability

Asset location:

- keep PWA assets in `OCPP.Core.Management/wwwroot`
- prefer repo-local icons over remote logo URLs for manifest assets

If final branded icons are not yet available:

- generate a temporary icon set from the current public badge/logo
- keep filenames stable so branded replacements can be dropped in later without another code change

### 4.2 Explicit non-goals

Do not implement in this pass:

- service worker caching
- offline start/stop behavior
- background sync
- push notifications

## 5. Implementation Notes

Main files expected to change:

- `Views/Shared/_PublicPortalLayout.cshtml`
- `wwwroot/css/public-portal.css`
- `Views/Public/Map.cshtml`
- `Views/Public/Start.cshtml`
- `Views/Payments/PublicStatus.cshtml`
- new manifest/icon files under `wwwroot`

Implementation rules:

- keep all functional hooks from Specs 1-3 intact
- do not reintroduce hardcoded English text while doing design cleanup
- keep button hit targets and focus states accessible after the redesign
- verify dark-mode-only assumptions are removed from Leaflet and popup styling

## 6. Validation

Responsive visual checks:

- `320px`, `360px`, `390px`, `430px`, tablet, and desktop
- map card list
- connector chooser
- QR modal
- charging status page

Installability checks:

- Chrome Android shows valid installability metadata
- manifest loads successfully
- icon links resolve
- no console errors from missing manifest/icon references

Regression checks:

- no functional behavior from Specs 1-3 regresses during the restyle
- map, QR scanning, connector switching, and public status updates still work

## 7. Out of Scope

This spec does not include:

- public behavior corrections from Spec 1
- EVSE routing from Spec 2
- i18n completeness from Spec 3
- email notification redesign from Spec 5

