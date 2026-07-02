import { Tone } from "./types";

export function fmtService(s: string): string {
  return ({ EntraId: "Entra ID", Intune: "Intune", DefenderXdr: "Defender XDR", ExchangeOnline: "Exchange Online", ServiceHealth: "Service Health" } as Record<string, string>)[s] ?? s;
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

export function fmtDate(iso?: string): string {
  if (!iso) return "–";
  return new Date(iso).toLocaleString("en-US", { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function fmtShort(iso?: string): string {
  if (!iso) return "–";
  return new Date(iso).toLocaleDateString("en-US", { month: "short", day: "numeric" });
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
  return `sev-${(s || "info").toLowerCase()}`;
}

export function pctTone(p: number, goodThresh = 90, warnThresh = 70): Tone {
  return p >= goodThresh ? "good" : p >= warnThresh ? "warning" : p > 0 ? "error" : "neutral";
}

export function fmtCountdown(sec: number): string {
  const m = Math.floor(sec / 60), s = sec % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

export function countryFlag(country?: string): string {
  if (!country) return "🌐";
  const map: Record<string, string> = {
    "united states": "🇺🇸", "usa": "🇺🇸", "us": "🇺🇸",
    "united kingdom": "🇬🇧", "uk": "🇬🇧", "gb": "🇬🇧",
    "india": "🇮🇳", "in": "🇮🇳", "canada": "🇨🇦", "ca": "🇨🇦",
    "australia": "🇦🇺", "au": "🇦🇺", "germany": "🇩🇪", "de": "🇩🇪",
    "france": "🇫🇷", "fr": "🇫🇷", "netherlands": "🇳🇱", "nl": "🇳🇱",
    "ireland": "🇮🇪", "ie": "🇮🇪", "china": "🇨🇳", "cn": "🇨🇳",
    "russia": "🇷🇺", "ru": "🇷🇺", "brazil": "🇧🇷", "br": "🇧🇷",
    "japan": "🇯🇵", "jp": "🇯🇵", "singapore": "🇸🇬", "sg": "🇸🇬",
    "spain": "🇪🇸", "es": "🇪🇸", "italy": "🇮🇹", "it": "🇮🇹",
    "nigeria": "🇳🇬", "ng": "🇳🇬", "pakistan": "🇵🇰", "pk": "🇵🇰",
    "united arab emirates": "🇦🇪", "uae": "🇦🇪", "ae": "🇦🇪",
    "south africa": "🇿🇦", "za": "🇿🇦", "mexico": "🇲🇽", "mx": "🇲🇽",
    "sweden": "🇸🇪", "se": "🇸🇪", "switzerland": "🇨🇭", "ch": "🇨🇭",
  };
  return map[country.trim().toLowerCase()] ?? "🌐";
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
  return d.toLocaleDateString();
}

export function fmtFullTime(iso?: string | null): string {
  if (!iso) return "–";
  const rel = relTime(iso);
  const abs = fmtDate(iso);
  return rel ? `${rel} (${abs})` : abs;
}

export function downloadCsv(rows: Record<string, unknown>[], filename: string): void {
  if (!rows.length) return;
  const keys = Object.keys(rows[0]);
  const escape = (v: unknown) => {
    const s = String(v ?? "").replace(/"/g, '""');
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
