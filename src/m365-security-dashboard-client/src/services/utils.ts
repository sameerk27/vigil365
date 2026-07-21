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

// Date formatting: exactly two formats app-wide — fmtDate (date + time) and
// fmtShort (date only). Both add the year automatically when it isn't the
// current year, and both pin en-US so output never varies by system locale.
export function fmtDate(iso?: string): string {
  if (!iso) return "–";
  const d = new Date(iso);
  const sameYear = d.getFullYear() === new Date().getFullYear();
  return d.toLocaleString("en-US", { month: "short", day: "numeric", ...(sameYear ? {} : { year: "numeric" }), hour: "2-digit", minute: "2-digit" });
}

export function fmtShort(iso?: string): string {
  if (!iso) return "–";
  const d = new Date(iso);
  const sameYear = d.getFullYear() === new Date().getFullYear();
  return d.toLocaleDateString("en-US", { month: "short", day: "numeric", ...(sameYear ? {} : { year: "numeric" }) });
}

/**
 * All timestamps render in the viewer's local zone. In a security timeline
 * "14:32" is meaningless without knowing the zone, so the app states it once
 * (in the header) rather than suffixing every row. Returns e.g. "IST (UTC+5:30)".
 */
export function tzLabel(): string {
  const d = new Date();
  const name = new Intl.DateTimeFormat("en-US", { timeZoneName: "short" })
    .formatToParts(d).find(p => p.type === "timeZoneName")?.value ?? "local";
  // getTimezoneOffset is minutes *behind* UTC, so the sign is inverted.
  const mins = -d.getTimezoneOffset();
  const sign = mins < 0 ? "-" : "+";
  const abs = Math.abs(mins);
  const offset = `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
  return name === offset ? name : `${name} (${offset})`;
}

/** Exact UTC instant for tooltips — the unambiguous form for evidence. */
export function fmtUtc(iso?: string): string {
  if (!iso) return "–";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "–" : `${d.toISOString().slice(0, 19).replace("T", " ")} UTC`;
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
export function countryFlag(country?: string): string {
  if (!country) return "";
  const map: Record<string, string> = {
    "united states": "US", "usa": "US",
    "united kingdom": "GB", "uk": "GB",
    "india": "IN", "canada": "CA", "australia": "AU", "germany": "DE",
    "france": "FR", "netherlands": "NL", "ireland": "IE", "china": "CN",
    "russia": "RU", "brazil": "BR", "japan": "JP", "singapore": "SG",
    "spain": "ES", "italy": "IT", "nigeria": "NG", "pakistan": "PK",
    "united arab emirates": "AE", "uae": "AE",
    "south africa": "ZA", "mexico": "MX", "sweden": "SE", "switzerland": "CH",
  };
  const key = country.trim().toLowerCase();
  return map[key] ?? (key.length === 2 ? key.toUpperCase() : "");
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
