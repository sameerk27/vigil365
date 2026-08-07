import { Tone } from "./types";

export function fmtService(s: string): string {
  return ({ EntraId: "Entra ID", Intune: "Intune", DefenderXdr: "Defender XDR", ExchangeOnline: "Exchange Online", ServiceHealth: "Service Health", SharePoint: "SharePoint" } as Record<string, string>)[s] ?? s;
}

export function fmtDefenderSource(s: string): string {
  const map: Record<string, string> = {
    microsoftDefenderForCloudApps: "Defender for Cloud Apps",
    microsoftDefenderForEndpoint:  "Defender for Endpoint",
    microsoftDefenderForOffice365: "Defender for Office 365",
    microsoftDefenderForIdentity:  "Defender for Identity",
    microsoftDefenderSmartScreen:  "Defender SmartScreen",
    microsoftSentinel:             "Microsoft Sentinel",
    azureAdIdentityProtection:     "Entra ID Protection",
    microsoft365Defender:          "M365 Defender",
    unknown:                       "Unknown",
  };
  return map[s] ?? s.replace(/([A-Z])/g, " $1").replace(/^./, c => c.toUpperCase()).trim();
}

// Timezone preference. Storage access is guarded because this runs at MODULE
// LOAD: an unguarded localStorage read threw in any environment without it and
// took the whole module down — utils is imported almost everywhere, so that is
// a blank app, not a missing preference. (It already did exactly that to three
// test files.) Same defensive shape as public/display-prefs.js.
function readStoredUtcMode(): boolean {
  try {
    return typeof localStorage !== "undefined" && localStorage.getItem("vigil365-tz-pref") === "utc";
  } catch {
    return false; // storage blocked (private mode, sandboxed iframe)
  }
}

let _utcMode = readStoredUtcMode();
export function isUtcMode() { return _utcMode; }
export function setUtcMode(utc: boolean) {
  _utcMode = utc;
  try {
    localStorage.setItem("vigil365-tz-pref", utc ? "utc" : "local");
  } catch {
    /* preference won't persist, but the toggle must still work this session */
  }
  if (typeof window !== "undefined") window.dispatchEvent(new Event("timezone-changed"));
}

// Date formatting: exactly two formats app-wide — fmtDate (date + time) and
// fmtShort (date only). Both add the year automatically when it isn't the
// current year, and both pin en-US so output never varies by system locale.
export function fmtDate(iso?: string): string {
  if (!iso) return "–";
  const d = new Date(iso);
  const sameYear = _utcMode 
    ? d.getUTCFullYear() === new Date().getUTCFullYear() 
    : d.getFullYear() === new Date().getFullYear();
  if (_utcMode) {
    return d.toLocaleString("en-US", { timeZone: "UTC", month: "short", day: "numeric", ...(sameYear ? {} : { year: "numeric" }), hour: "2-digit", minute: "2-digit" });
  }
  return d.toLocaleString("en-US", { month: "short", day: "numeric", ...(sameYear ? {} : { year: "numeric" }), hour: "2-digit", minute: "2-digit" });
}

export function fmtShort(iso?: string): string {
  if (!iso) return "–";
  const d = new Date(iso);
  const sameYear = _utcMode 
    ? d.getUTCFullYear() === new Date().getUTCFullYear() 
    : d.getFullYear() === new Date().getFullYear();
  if (_utcMode) {
    return d.toLocaleDateString("en-US", { timeZone: "UTC", month: "short", day: "numeric", ...(sameYear ? {} : { year: "numeric" }) });
  }
  return d.toLocaleDateString("en-US", { month: "short", day: "numeric", ...(sameYear ? {} : { year: "numeric" }) });
}

/**
 * All timestamps render in the viewer's local zone. In a security timeline
 * "14:32" is meaningless without knowing the zone, so the app states it once
 * (in the header) rather than suffixing every row. Returns e.g. "IST (UTC+5:30)".
 */
export function tzLabel(): string {
  if (_utcMode) return "UTC";
  const d = new Date();
  const name = new Intl.DateTimeFormat("en-US", { timeZoneName: "short" })
    .formatToParts(d).find(p => p.type === "timeZoneName")?.value ?? "local";
  // getTimezoneOffset is minutes *behind* UTC, so the sign is inverted.
  const mins = -d.getTimezoneOffset();
  const sign = mins < 0 ? "-" : "+";
  const abs = Math.abs(mins);
  const offset = `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
  // Many zones have no real abbreviation and Intl just echoes the offset
  // ("GMT+5:30"). Showing "GMT+5:30 (UTC+05:30)" says the same thing twice —
  // only pair them when the name is a genuine abbreviation (IST, PDT, JST).
  return /^(GMT|UTC)/i.test(name) ? offset : `${name} (${offset})`;
}

/** Exact UTC instant for tooltips — the unambiguous form for evidence. */
export function fmtUtc(iso?: string): string {
  if (!iso) return "–";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "–" : `${d.toISOString().slice(0, 19).replace("T", " ")} UTC`;
}

/**
 * Conditional Access grant/session controls arrive from Graph as raw camelCase
 * tokens ("mfa", "compliantDevice", "approvedApplication") and were rendered
 * straight into badges, so the page read like an API response rather than a
 * policy summary. Unknown tokens fall back to a de-camelCased form rather than
 * being hidden — a control we cannot name is still a control that applies.
 */
const CA_CONTROL_LABELS: Record<string, string> = {
  mfa: "Require MFA",
  compliantDevice: "Require compliant device",
  domainJoinedDevice: "Require hybrid Entra joined device",
  approvedApplication: "Require approved client app",
  compliantApplication: "Require app protection policy",
  passwordChange: "Require password change",
  block: "Block access",
  unknownFutureValue: "Unknown control",
};

export function fmtCaControl(token: string): string {
  if (!token) return "—";
  const known = CA_CONTROL_LABELS[token];
  if (known) return known;
  // e.g. "someNewControl" -> "Some new control"
  const spaced = token.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

export function sevColor(s: string): string {
  const key = s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  return ({
    Critical:      "var(--dot-critical)",
    High:          "var(--dot-high)",
    Medium:        "var(--dot-medium)",
    Low:           "var(--dot-low)",
    Informational: "var(--dot-info)",
  } as Record<string, string>)[key] ?? "var(--dot-info)";
}

export function sevClass(s?: string): string {
  // Includes the sev-dot base class — styles.css only styles the compound
  // selector (.sev-dot.sev-high), so the bare sev-* class renders nothing.
  return `sev-dot sev-${(s || "informational").toLowerCase()}`;
}

/** Single app-wide severity → badge-tone mapping. Use this everywhere a
 *  severity string becomes a Badge/KpiTile tone so colors stay consistent. */
export function sevTone(s?: string): Tone {
  const k = (s || "").toLowerCase();
  return k === "critical" || k === "high" ? "error"
    : k === "medium" ? "warning"
    : k === "low" ? "info"
    : "neutral";
}

export function pctTone(p: number, goodThresh = 90, warnThresh = 70): Tone {
  return p >= goodThresh ? "good" : p >= warnThresh ? "warning" : p > 0 ? "error" : "neutral";
}

export function fmtCountdown(sec: number): string {
  const m = Math.floor(sec / 60), s = sec % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

/** 2-letter ISO code for a country name. Flag emoji render as bare letter
 *  pairs on Windows, so the UI shows a neutral code chip instead. */
/**
 * Common names and abbreviations Intl does not return, so a sign-in from "USA"
 * or "UAE" still resolves. Everything else comes from the generated table below.
 */
const COUNTRY_ALIASES: Record<string, string> = {
  "usa": "US", "u.s.": "US", "u.s.a.": "US", "united states of america": "US",
  // Pinned rather than left to the generated table: Intl resolves both "GB" and
  // the non-standard "UK" to this name, so the answer must not depend on
  // iteration order.
  "united kingdom": "GB",
  "uk": "GB", "u.k.": "GB", "great britain": "GB", "england": "GB",
  "scotland": "GB", "wales": "GB", "northern ireland": "GB",
  "uae": "AE", "russia": "RU", "south korea": "KR", "north korea": "KP",
  "vietnam": "VN", "laos": "LA", "syria": "SY", "iran": "IR",
  "tanzania": "TZ", "bolivia": "BO", "venezuela": "VE", "moldova": "MD",
  "czech republic": "CZ", "czechia": "CZ", "turkey": "TR", "türkiye": "TR",
  "ivory coast": "CI", "cape verde": "CV", "swaziland": "SZ", "burma": "MM",
};

/**
 * Every ISO 3166-1 alpha-2 code keyed by its English name, generated once from
 * Intl rather than hand-maintained. The previous hardcoded list covered ~25
 * countries, so a sign-in from anywhere else rendered as "—" — indistinguishable
 * from missing data when the data was perfectly good.
 */
const COUNTRY_CODE_BY_NAME: Record<string, string> = (() => {
  const out: Record<string, string> = {};
  try {
    const names = new Intl.DisplayNames(["en"], { type: "region" });
    for (let a = 65; a <= 90; a++) {
      for (let b = 65; b <= 90; b++) {
        const code = String.fromCharCode(a, b);
        const name = names.of(code);
        // Intl echoes the code back for unassigned regions.
        if (!name || name === code) continue;

        // Skip deprecated aliases. Several historical codes resolve to a current
        // country's name — DD (East Germany) -> "Germany", UK -> "United
        // Kingdom", SU -> "Russia" — and picking one by iteration order handed
        // back codes that are not valid ISO 3166-1. Canonicalising the region
        // subtag rewrites an alias to its modern code, so a mismatch means the
        // code is not the canonical one for that country.
        if (!Intl.getCanonicalLocales(`und-${code}`)[0].endsWith(`-${code}`)) continue;

        out[name.toLowerCase()] = code;
      }
    }
  } catch {
    /* Intl.DisplayNames unavailable — aliases and the 2-letter passthrough still work. */
  }
  return out;
})();

/**
 * Country name -> ISO 3166-1 alpha-2 code for the location chip. Returns "" when
 * the country cannot be resolved; callers render nothing rather than a dash,
 * because the full country name is already displayed beside the chip.
 */
export function countryFlag(country?: string): string {
  if (!country) return "";
  const key = country.trim().toLowerCase();
  if (!key) return "";
  return COUNTRY_ALIASES[key]
    ?? COUNTRY_CODE_BY_NAME[key]
    ?? (key.length === 2 ? key.toUpperCase() : "");
}

export function expiryChip(days: number): { label: string; cls: string } {
  if (days < 0) return { label: `Expired ${Math.abs(days)}d ago`, cls: "expiry-expired" };
  if (days === 0) return { label: "Expires today", cls: "expiry-critical" };
  if (days <= 3) return { label: `Expires in ${days}d`, cls: "expiry-critical" };
  if (days <= 14) return { label: `Expires in ${days}d`, cls: "expiry-soon" };
  return { label: `${days}d left`, cls: "expiry-ok" };
}

export function relTime(iso?: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  const diff = Date.now() - d.getTime();
  if (diff < 60000) return "just now";
  if (diff < 3600000) return `${Math.floor(diff / 60000)}m ago`;
  if (diff < 86400000) return `${Math.floor(diff / 3600000)}h ago`;
  if (diff < 604800000) return `${Math.floor(diff / 86400000)}d ago`;
  return fmtShort(iso); // consistent en-US format, not system locale
}

export function fmtFullTime(iso?: string | null): string {
  if (!iso) return "–";
  const rel = relTime(iso);
  const abs = fmtDate(iso);
  return rel ? `${rel} (${abs})` : abs;
}

/**
 * Neutralises spreadsheet formula injection. Alert titles, display names and
 * audit actors are tenant-controlled: a value beginning =, +, - or @ is executed
 * as a formula when the export is opened in Excel/Sheets. Prefixing an
 * apostrophe forces the cell to text without changing what the reader sees.
 */
export function csvSafe(s: string): string {
  return /^[=+\-@\t\r]/.test(s) ? `'${s}` : s;
}

export function downloadCsv(rows: Record<string, unknown>[], filename: string): void {
  if (!rows.length) return;
  const keys = Object.keys(rows[0]);
  const escape = (v: unknown) => {
    const s = csvSafe(String(v ?? "")).replace(/"/g, '""');
    return s.includes(",") || s.includes("\n") || s.includes('"') ? `"${s}"` : s;
  };
  const csv = [keys.join(","), ...rows.map(r => keys.map(k => escape(r[k])).join(","))].join("\n");
  const a = Object.assign(document.createElement("a"), {
    href: URL.createObjectURL(new Blob([csv], { type: "text/csv" })),
    download: filename,
  });
  a.click(); URL.revokeObjectURL(a.href);
}

export function copyToClipboard(rows: Record<string, unknown>[]): Promise<void> {
  if (!rows.length) return Promise.resolve();
  return navigator.clipboard.writeText(JSON.stringify(rows, null, 2));
}

export function getSevCount(dict: Record<string, number> | undefined, sev: string): number {
  return dict ? (dict[sev] ?? dict[sev.toLowerCase()] ?? 0) : 0;
}
