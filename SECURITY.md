# Security & API Status

## Reporting a Security Vulnerability

If you find a security issue (e.g. credentials being logged, an endpoint leaking data), please **do not open a public issue**. Open a [GitHub Security Advisory](https://github.com/sameerk27/vigil365/security/advisories/new) instead so it can be fixed before public disclosure.

Please include the affected version/commit, reproduction steps, and potential impact. We aim to acknowledge reports within a few business days and ask for reasonable time to remediate before public disclosure.

---

## Supported Versions

Actively developed; security fixes target the latest `master`. Pin to a released commit for production and review changes before upgrading.

---

## Design & Deployment Model

Vigil365 is a **self-hosted, single-tenant** application meant to run on infrastructure the operating organisation controls — **not** a public multi-tenant SaaS.

- **Read-only against your tenant** — Graph access is app-only (client credentials) with `*.Read.All` permissions only; the app never writes to the M365 tenant.
- **Data stays in-tenant** — collected data is stored in the operator's own SQL database; nothing is sent to any third-party service.
- **Network isolation is a primary control** — designed to sit on a private network / behind a reverse proxy, not exposed directly to the internet.

---

## Current Security Controls

- **Secrets encrypted at rest** — SMTP password, webhook URLs, and the Graph client secret are DPAPI-encrypted; secrets are never returned by the API.
- **Database transport encryption** — SQL connections use `Encrypt=True`.
- **TLS in production** — HSTS + HTTPS redirection enforced outside Development; TLS via reverse proxy or Kestrel certificate (see README "HTTPS / TLS").
- **Safe error handling** — API errors return generic messages; detail goes to server logs only.
- **Security headers** — `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`.
- **Least privilege** — read-only Graph permission set scoped to the monitored services.

## Hardening In Progress

Implemented on a development branch and rolling into releases: identity sign-in (Entra ID / MSAL), role-based access control (Admin/Analyst/Viewer), an append-only audit trail, and certificate-based Graph auth. See `docs/PROJECT_SUMMARY.md`.

## Operator Responsibilities

- Keep the app registration's client secret/certificate in a secret store; never commit credentials; rotate anything that may have been exposed.
- Serve over HTTPS in production and restrict network exposure.
- Apply OS, .NET, SQL Server, and dependency updates.

---

## Reporting a Broken Graph API Endpoint

Microsoft occasionally changes, deprecates, or moves Graph API endpoints. If a dashboard card stops working or shows a permission error, please open an issue using this format:

**Title:** `[BROKEN API] <page name> — <endpoint>`

**Include:**
- Which page/card is affected (e.g. "Identity page — Risk Detections card")
- The error shown on screen (e.g. "403 Forbidden" or "404 Not Found")
- Your approximate date when it broke
- Link to the Microsoft changelog entry if you found one

---

## Known API Stability

| Endpoint area | Stability | Notes |
|--------------|-----------|-------|
| Risky users / sign-ins | ✅ Stable | v1.0, unchanged since 2021 |
| MFA registration details | ✅ Stable | v1.0 |
| Intune device compliance | ✅ Stable | v1.0 |
| Conditional Access policies | ✅ Stable | v1.0 |
| Defender XDR alerts (`alerts_v2`) | ✅ Stable | v1.0, replaced `alerts` in 2022 |
| Defender XDR incidents | ✅ Stable | v1.0 |
| Service health | ✅ Stable | v1.0 |
| Audit logs / sign-ins | ✅ Stable | v1.0 |
| Attack simulation | ⚠️ Watch | v1.0 but feature-flagged by license |
| Insider Risk (IRM) | ⚠️ Watch | Requires Microsoft Purview license |
| Identity health issues | ⚠️ Beta | `/beta/` endpoint — may change without notice |
| MCAS alerts | ⚠️ Watch | Merging into Defender XDR over time |

---

## Staying Ahead of Changes

Subscribe to the official Microsoft Graph changelog to get notified of breaking changes:

🔗 [https://developer.microsoft.com/en-us/graph/changelog](https://developer.microsoft.com/en-us/graph/changelog)

---

## How the App Handles Broken Endpoints

Each dashboard card fetches independently. If one Graph endpoint returns an error (403, 404, 429), that card shows an inline error message — **all other pages and cards keep working**. No single API change can break the whole dashboard.
