# EVCharge Go-Live Checklist (Host Split + Public Payments)

Datum: 2026-02-21
Scope: `cpo.evcharge.hr` (admin) + `evcharge.hr` (public), QR flow, delayed R1, online/offline race fix.

## 1) Pre-deploy

- Confirm DNS:
  - `cpo.evcharge.hr` -> management ingress/load balancer
  - `evcharge.hr` -> public ingress/load balancer
- Confirm TLS certs exist and are valid for both hosts.
- Confirm management config points to server API:
  - `ServerApiUrl`
  - `ApiKey`
- Confirm Stripe config in server:
  - `Stripe:Enabled=true`
  - `Stripe:ApiKey`
  - `Stripe:ReturnBaseUrl=https://evcharge.hr`
  - `Stripe:WebhookSecret`

## 2) Reverse Proxy Model (recommended)

Goal:
- `cpo.evcharge.hr` serves admin/management.
- `evcharge.hr` serves public portal/map/start/status.
- Temporary fallback redirects can point unknown public paths to legacy system.

### Nginx example

```nginx
server {
    listen 443 ssl;
    server_name cpo.evcharge.hr;

    location / {
        proxy_pass http://management_upstream;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}

server {
    listen 443 ssl;
    server_name evcharge.hr;

    # Public app paths
    location / {
        proxy_pass http://management_upstream;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }

    # Optional temporary fallback to legacy platform (if needed)
    # location /legacy-fallback {
    #     return 302 https://old-system.example.com$request_uri;
    # }
}
```

Notes:
- Public URLs now prefer path-style: `/cp/{chargePointId}/{connectorId}`.
- Legacy query URLs remain compatible: `/Public/Start?cp=...&conn=...`.

## 3) Staging Smoke Tests (mandatory)

### [A] Race reproduction (phantom offline)
- Connect charger on unstable WAN (or simulate short WAN drop 10-15s).
- Reconnect charger.
- Verify charger remains visible in `/API/Status` without server restart.
- Verify dashboard online flag is consistent.

### [B] Status consistency
- Compare:
  - `/API/Status`
  - management dashboard online flag
  - `MessageLog` heartbeat entries
- Expected: no mismatch where heartbeat exists but charger missing in status dict.

### [C] Public payment flow
- Open map: `https://evcharge.hr`
- Start via QR link: `https://evcharge.hr/cp/{id}/{conn}`
- Complete Stripe checkout -> redirected to public status page.
- Verify status page transitions:
  - waiting -> charging -> done

### [D] Delayed R1 flow
- From public status page, submit R1 data after checkout.
- Test invalid OIB (expect validation error).
- Test valid OIB (expect success and persisted metadata update).

### [E] Idle-fee exclusion window
- Run session across configured excluded window and verify billed minutes respect exclusion.

### [F] Host split routing
- `cpo.evcharge.hr` opens admin/login.
- `evcharge.hr` opens public portal/map.
- Verify internal links keep users on correct host.

## 4) Go-live Commands (operational)

- Confirm new path-style route works:
  - `GET https://evcharge.hr/cp/{id}/1`
- Confirm legacy route still works:
  - `GET https://evcharge.hr/Public/Start?cp={id}&conn=1`
- Confirm status polling endpoint:
  - `GET https://evcharge.hr/Payments/Status?reservationId={guid}&origin=public`

## 5) Rollback Plan

- Keep previous proxy config revision ready.
- If public host split fails:
  - restore old proxy mapping
  - keep admin on existing host
  - disable new public entry links temporarily
- If payment regressions occur:
  - disable public campaign QR distribution
  - keep existing/legacy payment funnel active until fixed.
