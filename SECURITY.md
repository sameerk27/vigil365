# Security & API Status

## Reporting a Security Vulnerability

If you find a security issue (e.g. credentials being logged, an endpoint leaking data), please **do not open a public issue**. Open a [GitHub Security Advisory](https://github.com/sameerk27/vigil365/security/advisories/new) instead so it can be fixed before public disclosure.

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
