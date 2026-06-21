import React, { useEffect, useState, useMemo, useCallback, useRef } from "react";
import { createRoot } from "react-dom/client";
import {
  Shield, Home, Users, Monitor, Mail, Database, AlertTriangle,
  CheckSquare, Activity, Wifi, RefreshCw, Settings, Lock,
  Server, Globe, TrendingUp, ChevronRight, User, Zap, Search,
  Key, UserX, UserCheck, AlertCircle, CheckCircle, XCircle,
  Smartphone, Laptop, ShieldAlert, ShieldCheck, FileText,
  BarChart2, Clock, ExternalLink, Info, Inbox, Filter,
  Send, Archive, Eye, Flag, Star, X, Bell,
  Package, ShieldOff, BookOpen, MapPin, LogIn, Download, ArrowUpDown,
  ChevronLeft, Sun, Moon, Copy, ClipboardCheck, PanelLeftClose, PanelLeftOpen
} from "lucide-react";
import "./styles.css";

// ─── Types ────────────────────────────────────────────────────────────────────

type NavPage = "overview" | "identity" | "devices" | "email" | "incidents" | "alertcenter" | "compliance" | "servicehealth" | "network" | "licenses" | "conditionalaccess" | "auditlog" | "signinmap";
type AlertSeverity = "Informational" | "Low" | "Medium" | "High" | "Critical";
type ServiceArea = "EntraId" | "Intune" | "DefenderXdr" | "ExchangeOnline" | "ServiceHealth";
type Tone = "good" | "warning" | "error" | "neutral" | "info";

type SecurityAlert = {
  id: number; externalId?: string; alertType: string; service: ServiceArea;
  severity: AlertSeverity; title: string; description?: string;
  userPrincipalName?: string; deviceName?: string; portalUrl?: string;
  detectedAt: string; lastUpdatedAt: string; isResolved: boolean;
};

type Overview = {
  totalActive: number; highPriority: number;
  lastRun?: { startedAt: string; completedAt?: string; status: string; alertsUpserted: number; sourceFailures: number };
  byService: { service: string; count: number }[];
  trends: { date: string; severity: string; count: number }[];
  generatedAt: string;
};

type SecureScore = {
  configured: boolean; currentScore: number; maxScore: number; percentage: number;
  trend: { date: string; score: number; maxScore: number }[]; error?: string;
};

type IdentityData = {
  configured: boolean;
  mfa: { registered: number; total: number; percentage: number };
  guests: { total: number; active: number };
  riskyUsers: number;
  signIns: { total: number; failed: number; risky: number; foreign: number };
  foreignSignIns: { title: string; userPrincipalName?: string; detectedAt: string }[];
  recentAdminActivity: { activityDateTime?: string; activityDisplayName?: string; initiatedByUser?: string; result?: string }[];
};

type DevicesData = {
  nonCompliant: number; notCheckedIn: number; totalDevices: number; compliancePct: number;
  nonCompliantDevices: { deviceName?: string; userPrincipalName?: string; description?: string; lastUpdatedAt: string }[];
};

type ServiceHealthData = {
  total: number;
  issues: { title: string; description?: string; severity: string; detectedAt: string; portalUrl?: string }[];
};

type LicenseSku = { name: string; consumed: number; purchased: number; available: number };
type LicenseData = { configured: boolean; error?: string; skus: LicenseSku[]; totalPurchased: number; totalConsumed: number };

type InactiveUser = { upn: string; name?: string; lastSignIn?: string; hasLicense: boolean; daysSince: number };
type InactiveUsersData = { configured: boolean; error?: string; inactive90Count: number; neverSignedInCount: number; totalUsers: number; inactive90: InactiveUser[]; neverSignedIn: InactiveUser[] };

type ExpiryUser = { upn: string; name?: string; daysUntilExpiry: number; lastChanged?: string };
type PasswordExpiryData = { configured: boolean; error?: string; expiringSoonCount: number; expiredCount: number; neverExpiresCount: number; totalUsers: number; expiringSoon: ExpiryUser[]; expired: ExpiryUser[]; neverExpire: { upn: string; name?: string }[] };

type CAPolicy = { name: string; state: string; inclUsers: string; exclUsers: string; apps: string; controls: string[] };
type ConditionalAccessData = { configured: boolean; error?: string; enabled: number; disabled: number; reportOnly: number; policies: CAPolicy[] };

type AuditEvent = { activityDateTime?: string; activityDisplayName?: string; category?: string; result?: string; resultReason?: string; initiatedByUser?: string; targetResources: string[] };
type AuditLogData = { configured: boolean; error?: string; total: number; failures: number; events: AuditEvent[] };

type SignInEntry = { upn?: string; app?: string; created?: string; city?: string; country?: string; success: boolean };
type SignInLocationsData = { configured: boolean; error?: string; total: number; countries: number; failures: number; byCountry: { country: string; count: number; failures: number }[]; recent: SignInEntry[] };

type DefenderAlert = {
  id?: string; title?: string; description?: string; severity: string; status: string;
  classification?: string; serviceSource?: string; detectionSource?: string; category?: string;
  createdDateTime?: string; lastUpdateDateTime?: string; assignedTo?: string;
  alertWebUrl?: string; incidentId?: string; mitreTechniques: string[] | string;
  recommendedActions?: string; actorDisplayName?: string; threatDisplayName?: string;
};
type DefenderAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; bySource: Record<string,number>; alerts: DefenderAlert[] };

type SecurityIncident = {
  id?: string; displayName?: string; severity: string; status: string;
  classification?: string; createdDateTime?: string; lastUpdateDateTime?: string;
  assignedTo?: string; incidentWebUrl?: string; customTags: string[];
  description?: string; recommendedActions?: string;
};
type SecurityIncidentsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; incidents: SecurityIncident[] };

type PrivilegedRole = { roleId?: string; roleName?: string; memberCount: number; members: { displayName?: string; userPrincipalName?: string }[] };
type PrivilegedRolesData = { configured: boolean; error?: string; roles: PrivilegedRole[]; totalPrivilegedUsers: number };
type DlpAlert = { id?: string; title?: string; severity: string; status: string; category?: string; serviceSource?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
type DlpAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; bySource: Record<string,number>; alerts: DlpAlert[] };
type MdeAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string; mitreTechniques: string[] };
type MdeVulnerabilitiesData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; byCategory: Record<string,number>; alerts: MdeAlert[] };
type PimActivation = { id?: string; action?: string; status?: string; createdDateTime?: string; justification?: string; principalDisplayName?: string; principalUpn?: string; roleName?: string };
type PimData = { configured: boolean; error?: string; total: number; activations: PimActivation[] };
type EmailProtectionAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
type EmailProtectionData = { configured: boolean; error?: string; total: number; byCategory: Record<string,number>; bySeverity: Record<string,number>; alerts: EmailProtectionAlert[] };
type PurviewLabel = { id?: string; name?: string; description?: string; color?: string; sensitivity: number; isActive: boolean };
type PurviewData = { configured: boolean; error?: string; labelCount: number; labels: PurviewLabel[] };

type MdiAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string; mitreTechniques: string[] };
type MdiAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; byCategory: Record<string,number>; alerts: MdiAlert[] };

type McasAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
type McasAlertsData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; byCategory: Record<string,number>; alerts: McasAlert[] };

type InsiderRiskAlert = { id?: string; title?: string; severity: string; status: string; category?: string; createdDateTime?: string; description?: string; alertWebUrl?: string };
type InsiderRiskData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; alerts: InsiderRiskAlert[] };

type RiskDetection = { id?: string; riskEventType?: string; riskLevel: string; riskState: string; userDisplayName?: string; userPrincipalName?: string; lastUpdatedDateTime?: string; activityDateTime?: string; ipAddress?: string; city?: string; country?: string };
type RiskDetectionsData = { configured: boolean; error?: string; total: number; byType: Record<string,number>; byLevel: Record<string,number>; detections: RiskDetection[] };

type IdentityHealthIssue = { id?: string; displayName?: string; issueType?: string; severity: string; status: string; description?: string; recommendations?: string; createdDateTime?: string; domainNames: string[]; sensorDNSNames: string[] };
type IdentityHealthData = { configured: boolean; error?: string; total: number; bySeverity: Record<string,number>; issues: IdentityHealthIssue[] };

type AttackSim = { id?: string; displayName?: string; attackType?: string; status: string; createdDateTime?: string; completionDateTime?: string; numberOfUsersTargeted: number; compromisedRate: number; clickedPhishingLinkCount: number; didNotClickLinkCount: number };
type AttackSimulationData = { configured: boolean; error?: string; total: number; totalTargeted: number; avgCompromiseRate: number; simulations: AttackSim[] };

// ─── Alert Center Types ───────────────────────────────────────────────────────
interface AlertPolicy {
  id: string;
  name: string;
  enabled: boolean;
  category: "identity" | "devices" | "email" | "compliance" | "licenses";
  condition: string;
  metric: string;
  threshold: number;
  severity: "critical" | "high" | "medium" | "low";
  notifyEmail: string;
  suppressionMinutes?: number;
  createdAt: string;
  lastTriggered?: string;
  triggerCount: number;
}

interface TriggeredAlert {
  id: string;
  policyId: string;
  policyName: string;
  severity: string;
  category: string;
  condition: string;
  metricValue: number;
  threshold: number;
  triggeredAt: string;
  status: "new" | "acknowledged" | "resolved";
  acknowledgedAt?: string;
  acknowledgedBy?: string;
}

interface NotificationSettings {
  teamsEnabled: boolean; teamsWebhookUrl?: string;
  emailEnabled: boolean; smtpHost?: string; smtpPort: number; smtpUseSsl: boolean;
  smtpUsername?: string; smtpPassword?: string; hasSmtpPassword?: boolean;
  fromAddress?: string; defaultRecipient?: string;
  webhookEnabled: boolean; webhookUrl?: string;
  minSeverity: string;
}

interface NotificationLogEntry {
  id: number; triggeredAlertId: string; policyName: string;
  channel: string; target?: string; success: boolean; error?: string; sentAt: string;
}

const apiBase = import.meta.env.VITE_API_BASE ?? "";

// ─── Alert Center API (server-side persistence + notification delivery) ────────
const acApi = {
  async getPolicies(): Promise<AlertPolicy[]> {
    try { const r = await fetch(`${apiBase}/api/alert-policies`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async createPolicy(p: Partial<AlertPolicy>): Promise<AlertPolicy | null> {
    try { const r = await fetch(`${apiBase}/api/alert-policies`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p) }); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async updatePolicy(p: AlertPolicy): Promise<boolean> {
    try { const r = await fetch(`${apiBase}/api/alert-policies/${p.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p) }); return r.ok; } catch { return false; }
  },
  async deletePolicy(id: string): Promise<boolean> {
    try { const r = await fetch(`${apiBase}/api/alert-policies/${id}`, { method: "DELETE" }); return r.ok; } catch { return false; }
  },
  async getTriggered(): Promise<TriggeredAlert[]> {
    try { const r = await fetch(`${apiBase}/api/triggered-alerts`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async acknowledge(id: string): Promise<boolean> {
    try { const r = await fetch(`${apiBase}/api/triggered-alerts/${id}/acknowledge`, { method: "POST" }); return r.ok; } catch { return false; }
  },
  async resolve(id: string): Promise<boolean> {
    try { const r = await fetch(`${apiBase}/api/triggered-alerts/${id}/resolve`, { method: "POST" }); return r.ok; } catch { return false; }
  },
  async evaluate(): Promise<number> {
    try { const r = await fetch(`${apiBase}/api/alert-policies/evaluate`, { method: "POST" }); return r.ok ? (await r.json()).fired ?? 0 : 0; } catch { return 0; }
  },
  async getSettings(): Promise<NotificationSettings | null> {
    try { const r = await fetch(`${apiBase}/api/notification-settings`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async saveSettings(s: NotificationSettings): Promise<boolean> {
    try { const r = await fetch(`${apiBase}/api/notification-settings`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(s) }); return r.ok; } catch { return false; }
  },
  async testNotifications(): Promise<{ ok: boolean; results?: { channel: string; success: boolean; error?: string }[] }> {
    try { const r = await fetch(`${apiBase}/api/notification-settings/test`, { method: "POST" }); return r.ok ? await r.json() : { ok: false }; } catch { return { ok: false }; }
  },
  async getLog(): Promise<NotificationLogEntry[]> {
    try { const r = await fetch(`${apiBase}/api/notification-log`); return r.ok ? await r.json() : []; } catch { return []; }
  },
};

const SEVERITIES: AlertSeverity[] = ["Critical", "High", "Medium", "Low", "Informational"];
const SERVICES: ServiceArea[] = ["EntraId", "Intune", "DefenderXdr", "ExchangeOnline", "ServiceHealth"];
const AUTO_REFRESH_SEC = 15 * 60; // 15 minutes

// ─── Utility ──────────────────────────────────────────────────────────────────
function fmtService(s: string) {
  return ({ EntraId: "Entra ID", Intune: "Intune", DefenderXdr: "Defender XDR", ExchangeOnline: "Exchange Online", ServiceHealth: "Service Health" } as Record<string, string>)[s] ?? s;
}
function fmtDefenderSource(s: string) {
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
function fmtDate(iso?: string) {
  if (!iso) return "–";
  return new Date(iso).toLocaleString("en-US", { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}
function fmtShort(iso?: string) {
  if (!iso) return "–";
  return new Date(iso).toLocaleDateString("en-US", { month: "short", day: "numeric" });
}
// Returns a CSS var() reference so it inherits the active theme automatically.
function sevColor(s: string): string {
  const key = s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  return ({
    Critical:      "var(--dot-critical)",
    High:          "var(--dot-high)",
    Medium:        "var(--dot-medium)",
    Low:           "var(--dot-low)",
    Informational: "var(--dot-info)",
  } as Record<string, string>)[key] ?? "var(--dot-info)";
}
function pctTone(p: number, goodThresh = 90, warnThresh = 70): Tone {
  return p >= goodThresh ? "good" : p >= warnThresh ? "warning" : p > 0 ? "error" : "neutral";
}
function fmtCountdown(sec: number) {
  const m = Math.floor(sec / 60), s = sec % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}
// Map common country names to flag emoji (fallback to globe).
function countryFlag(country?: string): string {
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
// Format a password-expiry countdown into label + style class.
function expiryChip(days: number): { label: string; cls: string } {
  if (days < 0) return { label: `Expired ${Math.abs(days)}d ago`, cls: "expiry-expired" };
  if (days === 0) return { label: "Expires today", cls: "expiry-critical" };
  if (days <= 3) return { label: `Expires in ${days}d`, cls: "expiry-critical" };
  if (days <= 14) return { label: `Expires in ${days}d`, cls: "expiry-soon" };
  return { label: `${days}d left`, cls: "expiry-ok" };
}

function relTime(iso?: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  const diff = Date.now() - d.getTime();
  if (diff < 60000) return "just now";
  if (diff < 3600000) return `${Math.floor(diff / 60000)}m ago`;
  if (diff < 86400000) return `${Math.floor(diff / 3600000)}h ago`;
  if (diff < 604800000) return `${Math.floor(diff / 86400000)}d ago`;
  return d.toLocaleDateString();
}

function downloadCsv(rows: Record<string, unknown>[], filename: string) {
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

function copyToClipboard(rows: Record<string, unknown>[]) {
  if (!rows.length) return Promise.resolve();
  return navigator.clipboard.writeText(JSON.stringify(rows, null, 2));
}

// ─── Toast system ─────────────────────────────────────────────────────────────
type ToastEntry = { id: number; message: string; type?: "success"|"error"|"info" };
let _toastId = 0;
let _addToast: ((t: Omit<ToastEntry,"id">) => void) | null = null;
function showToast(message: string, type: ToastEntry["type"] = "success") {
  _addToast?.({ message, type });
}

function ToastContainer() {
  const [toasts, setToasts] = useState<ToastEntry[]>([]);
  const addToast = useCallback((t: Omit<ToastEntry,"id">) => {
    const id = ++_toastId;
    setToasts(prev => [...prev, { ...t, id }]);
    setTimeout(() => setToasts(prev => prev.filter(x => x.id !== id)), 3000);
  }, []);
  useEffect(() => { _addToast = addToast; return () => { _addToast = null; }; }, [addToast]);
  return (
    <div className="toast-container">
      {toasts.map(t => (
        <div key={t.id} className={`toast toast-${t.type ?? "success"}`}>
          {t.type === "error" ? <XCircle size={15}/> : <CheckCircle size={15} color="var(--status-good-icon)"/>}
          <span>{t.message}</span>
        </div>
      ))}
    </div>
  );
}

// ─── Export dropdown ──────────────────────────────────────────────────────────
function ExportDropdown({ rows, filename }: { rows: Record<string, unknown>[]; filename: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const handler = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);
  return (
    <div className="export-wrap" ref={ref}>
      <button className="btn-export" onClick={() => setOpen(o => !o)} aria-label="Export options">
        <Download size={13}/> Export
        <ChevronRight size={11} style={{ marginLeft:2, transform: open?"rotate(90deg)":"none", transition:"transform .15s" }}/>
      </button>
      {open && (
        <div className="export-dropdown" role="menu">
          <button role="menuitem" onClick={() => {
            downloadCsv(rows, filename);
            showToast(`Exported ${rows.length} rows to ${filename}`);
            setOpen(false);
          }}><Download size={13}/> Export CSV</button>
          <hr/>
          <button role="menuitem" onClick={() => {
            copyToClipboard(rows).then(() => { showToast("Copied JSON to clipboard"); setOpen(false); });
          }}><Copy size={13}/> Copy as JSON</button>
        </div>
      )}
    </div>
  );
}

// ─── SVG Components ───────────────────────────────────────────────────────────
function CircleGauge({ pct, size = 72, color }: { pct: number; size?: number; color?: string }) {
  const r = (size - 10) / 2;
  const circ = 2 * Math.PI * r;
  const dash = Math.min(1, pct / 100) * circ;
  // If caller passes an explicit hex, use it; otherwise pick from the theme token set
  const c = color ?? (pct >= 90 ? "var(--status-good-icon)" : pct >= 70 ? "var(--status-warn-icon)" : "var(--status-error-icon)");
  return (
    <svg width={size} height={size} style={{ flexShrink: 0 }}>
      {/* Track ring — uses CSS var so dark mode gets a visible slate ring */}
      <circle cx={size/2} cy={size/2} r={r} fill="none" stroke="var(--color-border)" strokeWidth="6" />
      <circle cx={size/2} cy={size/2} r={r} fill="none" stroke={c} strokeWidth="6"
        strokeDasharray={`${dash} ${circ}`} strokeLinecap="round"
        transform={`rotate(-90 ${size/2} ${size/2})`} />
      <text x={size/2} y={size/2+5} textAnchor="middle" fontSize="13" fontWeight="700" fill={c}>
        {Math.round(pct)}%
      </text>
    </svg>
  );
}

function LineChart({ data, color = "#3b82f6" }: { data: { date: string; value: number }[]; color?: string }) {
  if (data.length < 2) return <div className="chart-empty">Collecting trend data…</div>;
  const w = 420, h = 110;
  const pad = { t: 8, r: 8, b: 22, l: 30 };
  const cw = w - pad.l - pad.r, ch = h - pad.t - pad.b;
  const vals = data.map(d => d.value);
  const min = Math.min(...vals), max = Math.max(...vals) || 1, range = max - min || 1;
  const pts = data.map((d, i) => ({
    x: pad.l + (i / (data.length - 1)) * cw,
    y: pad.t + ch - ((d.value - min) / range) * ch, ...d
  }));
  const line = pts.map((p, i) => `${i === 0 ? "M" : "L"} ${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(" ");
  const area = `${line} L ${pts.at(-1)!.x.toFixed(1)} ${(pad.t+ch).toFixed(1)} L ${pts[0].x.toFixed(1)} ${(pad.t+ch).toFixed(1)} Z`;
  const yLabels = [min, min+range/2, max].map((v,i) => ({ v: Math.round(v), y: pad.t+ch-(i/2)*ch }));
  const step = Math.ceil(data.length / 5);
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="line-chart-svg" preserveAspectRatio="none">
      <defs>
        <linearGradient id={`grad-${color.replace("#","")}`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.25" />
          <stop offset="100%" stopColor={color} stopOpacity="0.02" />
        </linearGradient>
      </defs>
      {yLabels.map(({y},i) => <line key={i} x1={pad.l} y1={y} x2={w-pad.r} y2={y} stroke="var(--color-border)" strokeWidth="1"/>)}
      {yLabels.map(({v,y}) => <text key={v} x={pad.l-4} y={y+4} textAnchor="end" fontSize="8" fill="var(--color-faint)">{v}</text>)}
      <path d={area} fill={`url(#grad-${color.replace("#","")})`} />
      <path d={line} fill="none" stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
      {pts.map((p,i) => <circle key={i} cx={p.x} cy={p.y} r="2.5" fill={color}/>)}
      {data.filter((_,i) => i%step===0||i===data.length-1).map(d => {
        const idx=data.indexOf(d), x=pad.l+(idx/(data.length-1))*cw;
        return <text key={d.date} x={x} y={h-3} textAnchor="middle" fontSize="8" fill="var(--color-faint)">{d.date.slice(5)}</text>;
      })}
    </svg>
  );
}

function MiniBarChart({ items }: { items: { label: string; value: number; color?: string }[] }) {
  const max = Math.max(...items.map(i => i.value), 1);
  return (
    <div className="mini-bar-chart">
      {items.map(item => (
        <div key={item.label} className="mbc-row">
          <span className="mbc-label">{item.label}</span>
          <div className="mbc-track">
            <div className="mbc-fill" style={{ width: `${(item.value/max)*100}%`, background: item.color ?? "#3b82f6" }} />
          </div>
          <span className="mbc-val">{item.value}</span>
        </div>
      ))}
    </div>
  );
}

// ─── Primitives ───────────────────────────────────────────────────────────────
function Badge({ label, tone }: { label: string; tone: Tone }) {
  return <span className={`badge badge-${tone}`}>{label}</span>;
}
function StatusDot({ status }: { status: Tone }) {
  const c = { good:"var(--status-good-icon)", warning:"var(--status-warn-icon)", error:"var(--status-error-icon)", neutral:"var(--color-faint)", info:"var(--color-primary)" }[status];
  return <span className="status-dot" style={{ background: c }} />;
}
function StatBox({ value, label, color, sub }: { value: string|number; label: string; color?: string; sub?: string }) {
  return (
    <div className="stat-box">
      <div className="stat-val" style={color ? { color } : undefined}>{value}</div>
      <div className="stat-lbl">{label}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}
function Card({ title, badge, action, children, className="" }:
  { title: string; badge?: React.ReactNode; action?: React.ReactNode; children: React.ReactNode; className?: string }) {
  return (
    <div className={`card ${className}`}>
      <div className="card-head">
        <span className="card-title">{title}</span>
        <div className="card-head-right">{badge}{action}</div>
      </div>
      {children}
    </div>
  );
}
function KpiTile({ icon, label, value, sub, tone="neutral", needsPerm }:
  { icon: React.ReactNode; label: string; value: React.ReactNode; sub?: string; tone?: Tone; needsPerm?: boolean }) {
  return (
    <div className={`kpi-tile kpi-${tone}`}>
      <div className="kpi-icon">{icon}</div>
      <div className="kpi-body">
        <div className="kpi-label">{label}</div>
        <div className="kpi-value">{value}</div>
        {needsPerm
          ? <div className="kpi-perm"><Lock size={9}/> Needs permission</div>
          : sub && <div className="kpi-sub" title={sub}>{sub}</div>}
      </div>
    </div>
  );
}
// Reusable inline error/permission card for endpoints with configured:true but error set.
function InlineError({ title, perm, message }: { title: string; perm?: string; message?: string }) {
  return (
    <div className="inline-err">
      <div className="ie-icon"><AlertTriangle size={16}/></div>
      <div>
        <div className="ie-title">{title}</div>
        <div className="ie-body">
          {message ?? "This data source returned an error."}
          {perm && <> Add <code>{perm}</code> permission in Azure Portal &rarr; App Registrations &rarr; API Permissions, grant admin consent, then restart the API.</>}
        </div>
      </div>
    </div>
  );
}
// ─── Detail Modal ─────────────────────────────────────────────────────────────
function DetailField({ label, value }: { label: string; value?: string | null }) {
  if (!value) return null;
  return (
    <div className="detail-field">
      <span className="detail-label">{label}</span>
      <span className="detail-value">{value}</span>
    </div>
  );
}

function DetailModal({ title, subtitle, onClose, portalUrl, portalLabel, children }: {
  title: string;
  subtitle?: string;
  onClose: () => void;
  portalUrl: string;
  portalLabel: string;
  children: React.ReactNode;
}) {
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [onClose]);
  return (
    <div className="detail-modal-backdrop" onClick={onClose}>
      <div className="detail-modal" onClick={e => e.stopPropagation()}>
        <div className="detail-modal-hdr">
          <div style={{ flex: 1, minWidth: 0 }}>
            <div className="dm-title">{title}</div>
            {subtitle && <div className="dm-sub">{subtitle}</div>}
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Close"><X size={16}/></button>
        </div>
        <div className="detail-modal-body">{children}</div>
        <div className="detail-modal-footer">
          <a href={portalUrl} target="_blank" rel="noopener noreferrer" className="dm-portal-btn">
            <ExternalLink size={13}/>{portalLabel} →
          </a>
          <button className="dm-close-btn" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

function SectHdr({ children }: { children: React.ReactNode }) {
  return <div className="sect-hdr">{children}</div>;
}
function EmptyState({ icon, message }: { icon?: React.ReactNode; message: string }) {
  return (
    <div className="empty-state">
      {icon ?? <Activity size={28} color="#d1d5db"/>}
      <p>{message}</p>
    </div>
  );
}
function ProgressBar({ pct, color }: { pct: number; color?: string }) {
  const c = color ?? (pct>=90?"var(--status-good-icon)":pct>=70?"var(--status-warn-icon)":"var(--status-error-icon)");
  return (
    <div className="prog-track">
      <div className="prog-fill" style={{ width:`${Math.min(100,pct)}%`, background:c }}/>
    </div>
  );
}
function InfoRow({ label, value, tone }: { label: string; value: React.ReactNode; tone?: Tone }) {
  return (
    <div className="info-row">
      <span className="info-label">{label}</span>
      <span className={`info-value ${tone?`tone-${tone}`:""}`}>{value}</span>
    </div>
  );
}

// ─── Alert Detail Modal ───────────────────────────────────────────────────────
function AlertDetailModal({ alert, onClose }: { alert: SecurityAlert; onClose: () => void }) {
  const sevTone: Tone = alert.severity === "Critical" || alert.severity === "High" ? "error"
    : alert.severity === "Medium" ? "warning"
    : alert.severity === "Low" ? "info" : "neutral";

  // Determine the correct portal URL based on alert type and service
  const portalUrl = alert.portalUrl ?? (() => {
    if (alert.service === "Intune") {
      return alert.deviceName
        ? `https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DevicesMenu`
        : "https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DevicesMenu";
    }
    if (alert.service === "EntraId") {
      if (alert.alertType === "RiskyUser") return "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskyUsersBlade";
      if (alert.alertType === "RiskySignIn") return "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskySignInsBlade";
      if (alert.alertType === "MfaStatus" && alert.userPrincipalName)
        return `https://entra.microsoft.com/#view/Microsoft_AAD_IAM/UsersManagementMenuBlade/~/MsGraphUsers`;
    }
    return "https://security.microsoft.com/alerts";
  })();

  const portalLabel = alert.service === "Intune" ? "View in Intune"
    : alert.service === "EntraId" ? "View in Entra ID"
    : "View in M365 Portal";

  return (
    <DetailModal
      title={alert.title}
      subtitle={`${fmtService(alert.service)} · ${alert.alertType}`}
      onClose={onClose}
      portalUrl={portalUrl}
      portalLabel={portalLabel}
    >
      <DetailField label="Alert ID" value={String(alert.id)}/>
      <DetailField label="Alert Type" value={alert.alertType}/>
      <DetailField label="Severity" value={alert.severity}/>
      <DetailField label="Service" value={fmtService(alert.service)}/>
      <DetailField label="Status" value={alert.isResolved ? "Resolved" : "Active"}/>
      <DetailField label="User" value={alert.userPrincipalName}/>
      <DetailField label="Device" value={alert.deviceName}/>
      <DetailField label="External ID" value={alert.externalId}/>
      <DetailField label="Detected" value={fmtDate(alert.detectedAt)}/>
      <DetailField label="Last Updated" value={fmtDate(alert.lastUpdatedAt)}/>
      {alert.description && (
        <>
          <div className="dm-section-hdr">Description</div>
          <div className="dm-desc-block">{alert.description}</div>
        </>
      )}
      <div style={{display:"flex",gap:6,flexWrap:"wrap",marginTop:4}}>
        <Badge label={alert.severity} tone={sevTone}/>
        <Badge label={fmtService(alert.service)} tone="neutral"/>
        <Badge label={alert.isResolved ? "Resolved" : "Active"} tone={alert.isResolved ? "good" : "error"}/>
      </div>
    </DetailModal>
  );
}

// ─── Service Health Grid ──────────────────────────────────────────────────────
const M365_SVCS = ["Exchange Online","Microsoft Teams","SharePoint Online","OneDrive","Microsoft Entra","Microsoft Intune","Microsoft Defender","Viva Engage"];
function ServiceHealthGrid({ issues }: { issues: { title: string }[] }) {
  return (
    <div className="svc-grid">
      {M365_SVCS.map(svc => {
        const hasIssue = issues.some(i =>
          i.title.toLowerCase().includes(svc.split(" ")[0].toLowerCase()) ||
          i.title.toLowerCase().includes(svc.split(" ").at(-1)!.toLowerCase()));
        return (
          <div key={svc} className="svc-item">
            <StatusDot status={hasIssue?"warning":"good"}/>
            <span className="svc-name">{svc}</span>
            <Badge label={hasIssue?"Advisory":"Operational"} tone={hasIssue?"warning":"good"}/>
          </div>
        );
      })}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// COLLECTION HEALTH — surfaces background collector status & per-source failures
// ═══════════════════════════════════════════════════════════════════════════════
interface CollectionRunInfo {
  id: number;
  startedAt: string;
  completedAt: string | null;
  status: string; // Started | Completed | Failed
  alertsUpserted: number;
  sourceFailures: number;
  error?: string | null;
  sourceFailureDetails?: string | null;
}

function CollectionHealthCard({ refreshKey }: { refreshKey: number }) {
  const [runs, setRuns] = useState<CollectionRunInfo[] | null>(null);
  const [err, setErr] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setErr(false);
    fetch(`${apiBase}/api/collector/runs`)
      .then(r => r.ok ? r.json() : Promise.reject())
      .then((d: CollectionRunInfo[]) => { if (!cancelled) setRuns(d); })
      .catch(() => { if (!cancelled) setErr(true); });
    return () => { cancelled = true; };
  }, [refreshKey]);

  // Latest finished run (ignore in-flight "Started" entries for status display)
  const finished = (runs ?? []).filter(r => r.status !== "Started");
  const last = finished[0];
  const failures: { source: string; error: string }[] = (() => {
    if (!last?.sourceFailureDetails) return [];
    try { return JSON.parse(last.sourceFailureDetails); } catch { return []; }
  })();
  const durationMs = last?.completedAt ? new Date(last.completedAt).getTime() - new Date(last.startedAt).getTime() : 0;
  const healthy = !!last && last.sourceFailures === 0 && last.status === "Completed";
  const tone: "good"|"warning"|"error" = !last ? "warning" : last.status === "Failed" ? "error" : last.sourceFailures > 0 ? "warning" : "good";

  return (
    <Card title="Collection Health"
      badge={<Badge label={!last ? "No runs" : healthy ? "Healthy" : last.status === "Failed" ? "Failed" : `${last.sourceFailures} source issue${last.sourceFailures!==1?"s":""}`} tone={tone}/>}>
      {err ? (
        <EmptyState message="Could not load collection status"/>
      ) : !runs ? (
        <EmptyState message="Loading collection status…"/>
      ) : !last ? (
        <EmptyState icon={<Database size={22} color="#d1d5db"/>} message="No collection has run yet"/>
      ) : (
        <>
          <div className="stat-row3">
            <StatBox value={last.alertsUpserted} label="Alerts Collected"/>
            <StatBox value={`${(durationMs/1000).toFixed(1)}s`} label="Duration"/>
            <StatBox value={last.sourceFailures} label="Source Failures" color={last.sourceFailures>0?"var(--status-error-text)":undefined}/>
          </div>
          <div className="mini-row" style={{marginTop:10, justifyContent:"space-between"}}>
            <span className="mr-date">Last run {relTime(last.completedAt ?? last.startedAt)}</span>
            <Badge label={`${finished.length} recent run${finished.length!==1?"s":""}`} tone="neutral"/>
          </div>
          {failures.length > 0 ? (
            <div className="mini-list" style={{marginTop:8}}>
              <SectHdr>FAILING SOURCES</SectHdr>
              {failures.map((f,i)=>(
                <div key={i} className="mini-row" title={f.error}>
                  <AlertTriangle size={11} color="var(--sev-high-icon)"/>
                  <span className="mr-user">{f.source}</span>
                  <span className="mr-date trunc" style={{maxWidth:140}}>{f.error}</span>
                </div>
              ))}
            </div>
          ) : (
            <div className="mini-row" style={{marginTop:8, color:"var(--status-good-text)"}}>
              <CheckCircle size={13}/>
              <span style={{fontSize:12}}>All {12} data sources collecting normally</span>
            </div>
          )}
        </>
      )}
    </Card>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// OVERVIEW PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function OverviewPage({ overview, secureScore, identity, devices, serviceHealth, alerts, defenderAlerts, securityIncidents, onAlertClick, onNavigateAlertCenter, alertPolicies, overviewTriggered, healthRefreshKey }:
  { overview:Overview|null; secureScore:SecureScore|null; identity:IdentityData|null; devices:DevicesData|null; serviceHealth:ServiceHealthData|null; alerts:SecurityAlert[]; defenderAlerts:DefenderAlertsData|null; securityIncidents:SecurityIncidentsData|null; onAlertClick:(a:SecurityAlert)=>void; onNavigateAlertCenter:()=>void; alertPolicies:AlertPolicy[]; overviewTriggered:TriggeredAlert[]; healthRefreshKey:number }) {

  const trendData = useMemo(() =>
    (secureScore?.trend??[]).map(t => ({ date:t.date, value:t.maxScore>0?+(t.score/t.maxScore*100).toFixed(1):0 })), [secureScore]);

  const posturePct = useMemo(() => {
    if (!overview||overview.totalActive===0) return 0;
    return +((overview.highPriority/overview.totalActive)*100).toFixed(1);
  }, [overview]);

  // MFA: use Graph percentage if available, otherwise fall back to DB alert count
  const mfaMissingCount = useMemo(() => alerts.filter(a=>a.alertType==="MfaStatus"&&!a.isResolved).length, [alerts]);
  const mfaPct = (identity?.mfa.total??0) > 0 ? (identity?.mfa.percentage??0) : 0;
  const mfaKnown = (identity?.mfa.total??0) > 0;

  // Device compliance: handle case where DB nonCompliant > Graph totalDevices
  const devNonCompliant = devices?.nonCompliant??0;
  const devEffectiveTotal = Math.max(devices?.totalDevices??0, devNonCompliant);
  const devComplPct = devEffectiveTotal > 0
    ? Math.max(0, Math.round((devEffectiveTotal - devNonCompliant) / devEffectiveTotal * 100))
    : (devNonCompliant === 0 ? 100 : 0);
  return (
    <div className="page">
      {/* KPI Row */}
      <div className="kpi-row">
        <KpiTile icon={<Shield size={18}/>} label="SECURE SCORE"
          value={secureScore?.configured&&!secureScore.error?`${secureScore.percentage}%`:"—"}
          sub={secureScore?.configured&&!secureScore.error?`${Math.round(secureScore.currentScore)} / ${Math.round(secureScore.maxScore)} pts`:"Run collection to load"}
          needsPerm={!!secureScore && (!secureScore.configured || !!secureScore.error)}
          tone={secureScore?.configured&&!secureScore.error?pctTone(secureScore.percentage):"neutral"}/>
        <KpiTile icon={<Lock size={18}/>} label="MFA COVERAGE"
          value={mfaKnown ? `${mfaPct}%` : mfaMissingCount > 0 ? `${mfaMissingCount}` : identity?.configured ? "—" : "—"}
          sub={mfaKnown ? `${identity!.mfa.registered}/${identity!.mfa.total} users` : mfaMissingCount > 0 ? `users missing MFA` : identity?.configured ? "Needs Reports.Read.All" : "Run collection"}
          needsPerm={!!identity && identity.configured && !mfaKnown && mfaMissingCount === 0}
          tone={mfaKnown ? pctTone(mfaPct,95,80) : mfaMissingCount > 0 ? "error" : "neutral"}/>
        <KpiTile icon={<Monitor size={18}/>} label="DEVICE COMPLIANCE"
          value={devices ? (devNonCompliant===0 && devEffectiveTotal===0 ? "—" : devNonCompliant===0 ? "All OK" : `${devNonCompliant} issues`) : "—"}
          sub={devices ? (devEffectiveTotal>0 ? `${Math.max(0,devEffectiveTotal-devNonCompliant)}/${devEffectiveTotal} compliant` : `${devNonCompliant} non-compliant`) : "Run collection"}
          tone={devNonCompliant===0?"good":devNonCompliant<=3?"warning":"error"}/>
        <KpiTile icon={<Activity size={18}/>} label="POSTURE RISK"
          value={<span style={{color: posturePct>10?"#b91c1c":undefined}}>{posturePct}%</span>}
          sub={`${overview?.highPriority??0} high / ${overview?.totalActive??0} active`}
          tone={posturePct===0?"neutral":posturePct<=10?"good":posturePct<=25?"warning":"error"}/>
        <KpiTile icon={<ShieldAlert size={18}/>} label="DEFENDER ALERTS"
          value={defenderAlerts?.configured && !defenderAlerts.error ? `${defenderAlerts.total}` : "—"}
          sub={defenderAlerts?.configured && !defenderAlerts.error
            ? `${defenderAlerts.bySeverity?.["high"]??0} high / ${defenderAlerts.bySeverity?.["critical"]??0} critical`
            : defenderAlerts?.error ? "Needs SecurityAlert.Read.All" : "Run collection"}
          needsPerm={!!defenderAlerts?.error}
          tone={!defenderAlerts?.configured||defenderAlerts.error?"neutral":(defenderAlerts.bySeverity?.["critical"]??0)>0?"error":(defenderAlerts.bySeverity?.["high"]??0)>0?"warning":"good"}/>
        <KpiTile icon={<Flag size={18}/>} label="INCIDENTS"
          value={securityIncidents?.configured && !securityIncidents.error ? `${securityIncidents.total}` : "—"}
          sub={securityIncidents?.configured && !securityIncidents.error
            ? `${securityIncidents.bySeverity?.["high"]??0} high / ${securityIncidents.bySeverity?.["critical"]??0} critical`
            : securityIncidents?.error ? "Needs SecurityIncident.Read.All" : "Run collection"}
          needsPerm={!!securityIncidents?.error}
          tone={!securityIncidents?.configured||securityIncidents.error?"neutral":(securityIncidents.bySeverity?.["critical"]??0)>0?"error":(securityIncidents.bySeverity?.["high"]??0)>0?"warning":"good"}/>
      </div>

      {/* Mid Row */}
      <div className="mid-row">
        <Card title="Secure Score Trend" badge={<Badge label={trendData.length>30?"90 Days":trendData.length>0?`${trendData.length} Days`:"No data"} tone="neutral"/>} className="card-score">
          {secureScore?.configured&&trendData.length>1?(
            <>
              <div className="score-hero">
                <CircleGauge pct={secureScore.percentage} size={80}/>
                <div>
                  <div className="score-big">{secureScore.percentage}%</div>
                  <div className="score-meta">{Math.round(secureScore.currentScore)} / {Math.round(secureScore.maxScore)} pts</div>
                  <div className="score-meta" style={{marginTop:4}}>Updated {fmtShort(secureScore.trend.at(-1)?.date)}</div>
                </div>
              </div>
              <LineChart data={trendData}/>
            </>
          ):(
            <EmptyState message={secureScore?.configured?"Collecting trend data — check back after first run":"Configure Graph credentials then run a collection"}/>
          )}
        </Card>
        <Card title="Defender Alerts"
          badge={defenderAlerts?.configured && !defenderAlerts.error
            ? <Badge label={`${defenderAlerts.total} active`} tone={defenderAlerts.total>0?"error":"good"}/>
            : <Badge label="No data" tone="neutral"/>}>
          {defenderAlerts?.configured && !defenderAlerts.error && defenderAlerts.total > 0 ? (
            <>
              <div className="stat-row4">
                {(["critical","high","medium","low"] as const).map(sev=>(
                  <StatBox key={sev} value={defenderAlerts.bySeverity?.[sev]??0} label={sev.charAt(0).toUpperCase()+sev.slice(1)}
                    color={sev==="critical"?"var(--dot-critical)":sev==="high"?"var(--dot-high)":sev==="medium"?"var(--dot-medium)":"var(--dot-info)"}/>
                ))}
              </div>
              <div className="mini-list" style={{marginTop:8}}>
                <SectHdr>RECENT ALERTS</SectHdr>
                {defenderAlerts.alerts.slice(0,5).map((a,i)=>(
                  <div key={i} className="mini-row">
                    <span className={`sev-dot sev-${(a.severity??"unknown").toLowerCase()}`}/>
                    <span className="mr-user" style={{flex:1}}>{a.title??"Unknown"}</span>
                    <Badge label={fmtDefenderSource(a.serviceSource??a.severity)} tone="neutral"/>
                  </div>
                ))}
              </div>
            </>
          ) : defenderAlerts?.error ? (
            <EmptyState icon={<ShieldAlert size={24} color="#d1d5db"/>} message={`Needs SecurityAlert.Read.All permission`}/>
          ) : (
            <EmptyState icon={<ShieldAlert size={24} color="#d1d5db"/>} message="Run a collection to load Defender alerts"/>
          )}
        </Card>
        <Card title="Top Active Alerts"
          badge={<Badge label={`${(overview?.highPriority??0)} high priority`} tone={(overview?.highPriority??0)>0?"error":"good"}/>}>
          {alerts.filter(a=>!a.isResolved).length > 0 ? (
            <div className="mini-list">
              {alerts.filter(a=>!a.isResolved)
                .sort((a,b)=>{ const o=["Critical","High","Medium","Low","Informational"]; return o.indexOf(a.severity)-o.indexOf(b.severity); })
                .slice(0,6).map((a,i)=>(
                  <div key={i} className="mini-row act-clickable" onClick={()=>onAlertClick(a)} style={{cursor:"pointer"}}>
                    <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                    <span className="mr-user" style={{flex:1}}>{a.title}</span>
                    <Badge label={a.service==="EntraId"?"Entra":a.service==="DefenderXdr"?"Defender":a.service==="Intune"?"Intune":a.service==="ExchangeOnline"?"Exchange":"Health"} tone="neutral"/>
                  </div>
              ))}
            </div>
          ) : (
            <EmptyState icon={<CheckCircle size={24} color="#22c55e"/>} message="No active alerts — environment looks healthy"/>
          )}
        </Card>
      </div>

      {/* Lower Row */}
      <div className="lower-row">
        <Card title="Risky Users"
          badge={(() => { const r = alerts.filter(a=>a.alertType==="RiskyUser"&&!a.isResolved).length; return r>0?<Badge label={`${r} at risk`} tone="error"/>:<Badge label="All clear" tone="good"/>; })()}>
          {(() => {
            const riskyUsers = alerts.filter(a=>a.alertType==="RiskyUser"&&!a.isResolved);
            const mfaMissing = alerts.filter(a=>a.alertType==="MfaStatus"&&!a.isResolved);
            const risky = alerts.filter(a=>a.alertType==="RiskySignIn"&&!a.isResolved);
            return (
              <>
                <div className="stat-row3">
                  <StatBox value={riskyUsers.length} label="Risky Users" color={riskyUsers.length>0?"var(--status-error-text)":undefined}/>
                  <StatBox value={mfaMissing.length} label="No MFA" color={mfaMissing.length>0?"var(--status-warn-text)":undefined}/>
                  <StatBox value={risky.length} label="Risky Sign-ins" color={risky.length>0?"var(--status-warn-text)":undefined}/>
                </div>
                {riskyUsers.length>0&&(
                  <div className="mini-list">
                    <SectHdr>AT-RISK USERS</SectHdr>
                    {riskyUsers.slice(0,4).map((a,i)=>(
                      <div key={i} className="mini-row act-clickable" onClick={()=>onAlertClick(a)} style={{cursor:"pointer"}}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <span className="mr-user">{a.userPrincipalName??a.title}</span>
                        <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                      </div>
                    ))}
                  </div>
                )}
                {riskyUsers.length===0&&<EmptyState icon={<UserCheck size={24} color="#22c55e"/>} message="No risky users detected"/>}
              </>
            );
          })()}
        </Card>
        <Card title="Recent High Alerts"
          badge={<Badge label={`${alerts.filter(a=>!a.isResolved&&(a.severity==="High"||a.severity==="Critical")).length} high/critical`} tone={alerts.filter(a=>!a.isResolved&&(a.severity==="High"||a.severity==="Critical")).length>0?"error":"good"}/>}>
          {alerts.filter(a=>!a.isResolved&&(a.severity==="High"||a.severity==="Critical")).length>0 ? (
            <div className="mini-list">
              {alerts.filter(a=>!a.isResolved&&(a.severity==="High"||a.severity==="Critical"))
                .sort((a,b)=>new Date(b.detectedAt).getTime()-new Date(a.detectedAt).getTime())
                .slice(0,6).map((a,i)=>(
                  <div key={i} className="mini-row act-clickable" onClick={()=>onAlertClick(a)} style={{cursor:"pointer"}}>
                    <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                    <span className="mr-user" style={{flex:1}}>{a.title}</span>
                    <span className="mr-date">{fmtShort(a.detectedAt)}</span>
                  </div>
              ))}
            </div>
          ) : (
            <EmptyState icon={<CheckCircle size={24} color="#22c55e"/>} message="No high or critical alerts"/>
          )}
        </Card>
        <Card title="Alerts by Service">
          <div className="stat-row2">
            <StatBox value={overview?.totalActive??0} label="Total Active"/>
            <StatBox value={overview?.highPriority??0} label="High Priority" color={(overview?.highPriority??0)>0?"var(--status-error-text)":undefined}/>
          </div>
          {(overview?.byService??[]).length>0 ? (
            <div className="mini-list">
              <SectHdr>BREAKDOWN BY SERVICE</SectHdr>
              {(overview?.byService??[]).map((s,i)=>(
                <div key={i} className="mini-row">
                  <Database size={11} color="#6b7280"/>
                  <span className="mr-user">{fmtService(s.service)}</span>
                  <Badge label={String(s.count)} tone={s.count>10?"error":s.count>3?"warning":"neutral"}/>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState icon={<Database size={24} color="#d1d5db"/>} message="Run a collection to see alert breakdown"/>
          )}
        </Card>
        <Card title="Device Compliance"
          badge={devNonCompliant>0?<Badge label={`${devNonCompliant} non-compliant`} tone={devNonCompliant>3?"error":"warning"}/>:<Badge label="All compliant" tone="good"/>}>
          <div className="device-hero">
            <CircleGauge pct={devices ? devComplPct : 0} size={70}/>
            <div className="stat-col">
              <StatBox value={devices ? Math.max(0, devEffectiveTotal - devNonCompliant) : "—"} label="Compliant"/>
              <StatBox value={devNonCompliant} label="Non-compliant" color={devNonCompliant>0?"var(--status-error-text)":undefined}/>
            </div>
          </div>
          {(devices?.nonCompliantDevices.length??0)>0?(
            <div className="mini-list">
              {devices!.nonCompliantDevices.slice(0,3).map((d,i)=>(
                <div key={i} className="mini-row">
                  <Monitor size={11} color="#6b7280"/>
                  <span className="mr-user">{d.deviceName??"Unknown device"}</span>
                  <span className="mr-date">{d.userPrincipalName?.split("@")[0]}</span>
                </div>
              ))}
            </div>
          ):(
            !devices
              ?<div className="empty-state" style={{paddingTop:8}}><p>Run a collection to load device data</p></div>
              :devNonCompliant===0
                ?<div className="empty-state" style={{paddingTop:8}}><p>All devices are compliant</p></div>
                :null
          )}
        </Card>
      </div>

      {/* Footer Row */}
      <div className="footer-row">
        <Card title="Recent Admin Activity" badge={<Badge label={`${identity?.recentAdminActivity.length??0} events`} tone="neutral"/>}>
          {(identity?.recentAdminActivity.length??0)===0
            ?(
              <EmptyState icon={<User size={28} color="#d1d5db"/>}
                message="No admin activity — requires AuditLog.Read.All permission"/>
            ):(
              <div className="act-list">
                {identity!.recentAdminActivity.slice(0,6).map((a,i)=>(
                  <div key={i} className="act-row">
                    <User size={12} color="#94a3b8"/>
                    <div className="act-body">
                      <span className="act-who">{a.initiatedByUser?.split("@")[0]??"System"}</span>
                      <span className="act-what"> {a.activityDisplayName}</span>
                    </div>
                    <span className="act-date">{fmtDate(a.activityDateTime)}</span>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
        <Card title="Top Improvement Actions" action={<ChevronRight size={15} color="#94a3b8"/>}>
          <div className="impr-list">
            {(overview?.byService??[]).length===0
              ?<EmptyState message="Run a collection to see recommendations"/>
              :overview!.byService.map((s,i)=>(
                <div key={i} className="impr-row">
                  <div className="impr-icon"><TrendingUp size={12}/></div>
                  <span className="impr-text">Review {fmtService(s.service)} — {s.count} active alert{s.count!==1?"s":""}</span>
                  <Badge label={`+${Math.min(s.count*3,30)} pts`} tone="neutral"/>
                </div>
              ))
            }
          </div>
        </Card>
        {(() => {
          const triggered = overviewTriggered;
          const enabledPolicies = alertPolicies.filter(p => p.enabled).length;
          const todayAlerts = triggered.filter(a => new Date(a.triggeredAt).toDateString() === new Date().toDateString()).length;
          const recent3 = [...triggered].sort((a,b)=>new Date(b.triggeredAt).getTime()-new Date(a.triggeredAt).getTime()).slice(0,3);
          return (
            <Card title="Alert Policies" action={<button className="btn-export" onClick={onNavigateAlertCenter}>View All</button>}>
              <div className="stat-row2">
                <StatBox value={enabledPolicies} label="Active Policies"/>
                <StatBox value={todayAlerts} label="Triggered Today" color={todayAlerts>0?"var(--status-error-text)":undefined}/>
              </div>
              {recent3.length > 0 ? (
                <div className="mini-list" style={{marginTop:8}}>
                  <SectHdr>RECENT TRIGGERED</SectHdr>
                  {recent3.map((a,i)=>(
                    <div key={i} className="mini-row">
                      <span className={`sev-dot sev-${a.severity}`}/>
                      <span className="mr-user" style={{flex:1}}>{a.policyName}</span>
                      <span className="mr-date">{relTime(a.triggeredAt)}</span>
                    </div>
                  ))}
                </div>
              ) : (
                <EmptyState icon={<Bell size={22} color="#d1d5db"/>} message="No alerts triggered yet"/>
              )}
            </Card>
          );
        })()}
        <CollectionHealthCard refreshKey={healthRefreshKey}/>
      </div>
    </div>
  );
}

// ─── Reusable filter-preset saver (localStorage) ─────────────────────────────
function FilterPresets({ pageKey, filters, onLoad }: {
  pageKey: string;
  filters: Record<string, string>;
  onLoad: (f: Record<string, string>) => void;
}) {
  const key = `fp_${pageKey}`;
  const [presets, setPresets] = useState<{ name: string; filters: Record<string, string> }[]>(() => {
    try { return JSON.parse(localStorage.getItem(key) ?? "[]"); } catch { return []; }
  });
  const [saving, setSaving] = useState(false);
  const [name, setName] = useState("");
  const save = () => {
    if (!name.trim()) return;
    const next = [...presets.filter(p => p.name !== name.trim()), { name: name.trim(), filters }];
    setPresets(next); localStorage.setItem(key, JSON.stringify(next));
    showToast(`Filter preset "${name.trim()}" saved`);
    setSaving(false); setName("");
  };
  const remove = (n: string) => {
    const next = presets.filter(p => p.name !== n);
    setPresets(next); localStorage.setItem(key, JSON.stringify(next));
  };
  return (
    <div style={{ display:"flex", alignItems:"center", gap:6, flexWrap:"wrap" }}>
      {presets.length > 0 && (
        <select className="filter-sel" onChange={e => { const p = presets.find(x => x.name === e.target.value); if (p) { onLoad(p.filters); e.target.value = ""; } }}>
          <option value="">Load preset…</option>
          {presets.map(p => <option key={p.name} value={p.name}>{p.name}</option>)}
        </select>
      )}
      {saving ? (
        <>
          <input value={name} onChange={e => setName(e.target.value)} onKeyDown={e => e.key === "Enter" && save()}
            placeholder="Preset name…" className="filter-sel" style={{ minWidth:120 }} autoFocus />
          <button className="btn-export" onClick={save}>Save</button>
          <button className="btn-apply" style={{ padding:"7px 10px" }} onClick={() => setSaving(false)}>✕</button>
        </>
      ) : (
        <button className="btn-export" onClick={() => setSaving(true)}><Star size={12} /> Save filter</button>
      )}
      {presets.map(p => (
        <span key={p.name} className="preset-chip">
          {p.name}
          <button onClick={() => remove(p.name)} style={{ background:"none", border:"none", cursor:"pointer", color:"var(--color-faint)", padding:"0 2px" }}>✕</button>
        </span>
      ))}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// IDENTITY PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function IdentityPage({ identity, alerts, privilegedRoles, pimData, mdiAlerts, riskDetections, identityHealth, onAlertClick }:
  { identity: IdentityData|null; alerts: SecurityAlert[]; privilegedRoles: PrivilegedRolesData|null; pimData: PimData|null; mdiAlerts: MdiAlertsData|null; riskDetections: RiskDetectionsData|null; identityHealth: IdentityHealthData|null; onAlertClick:(a:SecurityAlert)=>void }) {

  const [selectedMdi, setSelectedMdi] = useState<MdiAlert|null>(null);
  const [selectedDetection, setSelectedDetection] = useState<RiskDetection|null>(null);

  // Page-level search — applies to ALL sections on this page
  const [search, setSearch]     = useState("");
  const [riskLevel, setRiskLevel] = useState("");
  const [showResolved, setShowResolved] = useState(true); // default ON so nothing is hidden

  const identityAlerts = useMemo(() => alerts.filter(a => a.service==="EntraId"), [alerts]);
  const q = search.toLowerCase();

  const mfaMissing = useMemo(() => {
    let items = identityAlerts.filter(a => a.alertType==="MfaStatus" && !a.isResolved);
    if (search) items = items.filter(a => a.userPrincipalName?.toLowerCase().includes(q) || a.title.toLowerCase().includes(q));
    return items;
  }, [identityAlerts, search, q]);

  const riskySignIns = useMemo(() => {
    let items = identityAlerts.filter(a => a.alertType==="RiskySignIn");
    if (!showResolved) items = items.filter(a => !a.isResolved);
    if (riskLevel) items = items.filter(a => a.severity.toLowerCase() === riskLevel);
    if (search) items = items.filter(a => a.userPrincipalName?.toLowerCase().includes(q) || a.title.toLowerCase().includes(q));
    return items;
  }, [identityAlerts, search, riskLevel, showResolved, q]);

  const riskyUsers = useMemo(() => {
    let items = identityAlerts.filter(a => a.alertType==="RiskyUser");
    if (!showResolved) items = items.filter(a => !a.isResolved);
    if (riskLevel) items = items.filter(a => a.severity.toLowerCase() === riskLevel);
    if (search) items = items.filter(a => a.userPrincipalName?.toLowerCase().includes(q) || a.title.toLowerCase().includes(q));
    return items;
  }, [identityAlerts, search, riskLevel, showResolved, q]);

  const filteredPim = useMemo(() => {
    let items = pimData?.activations ?? [];
    if (search) items = items.filter(a =>
      (a.principalDisplayName ?? a.principalUpn ?? "").toLowerCase().includes(q) ||
      (a.roleName ?? "").toLowerCase().includes(q)
    );
    return items;
  }, [pimData, search, q]);

  const filteredMdiAlerts = useMemo(() => {
    let items = mdiAlerts?.alerts ?? [];
    if (riskLevel) items = items.filter(a => a.severity.toLowerCase() === riskLevel);
    if (search) items = items.filter(a => (a.title ?? "").toLowerCase().includes(q) || (a.category ?? "").toLowerCase().includes(q));
    return items;
  }, [mdiAlerts, search, riskLevel, q]);

  const filteredDetections = useMemo(() => {
    let items = riskDetections?.detections ?? [];
    if (riskLevel) items = items.filter(d => d.riskLevel.toLowerCase() === riskLevel);
    if (search) items = items.filter(d =>
      (d.userPrincipalName ?? d.userDisplayName ?? "").toLowerCase().includes(q) ||
      (d.riskEventType ?? "").toLowerCase().includes(q)
    );
    return items;
  }, [riskDetections, search, riskLevel, q]);

  const filteredForeignSignIns = useMemo(() => {
    let items = identity?.foreignSignIns ?? [];
    if (search) items = items.filter(s =>
      (s.userPrincipalName ?? "").toLowerCase().includes(q) ||
      (s.title ?? "").toLowerCase().includes(q)
    );
    return items;
  }, [identity, search, q]);

  const mfaPct = identity?.mfa.percentage ?? 0;
  const hasFilter = !!(search || riskLevel);

  return (
    <div className="page">
      {selectedMdi && (
        <DetailModal
          title={selectedMdi.title ?? "MDI Alert"}
          subtitle={`${selectedMdi.severity} · ${selectedMdi.category ?? "Defender for Identity"}`}
          onClose={() => setSelectedMdi(null)}
          portalUrl={selectedMdi.alertWebUrl ?? (selectedMdi.id ? `https://security.microsoft.com/alerts/${selectedMdi.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Alert ID" value={selectedMdi.id}/>
          <DetailField label="Severity" value={selectedMdi.severity}/>
          <DetailField label="Status" value={selectedMdi.status}/>
          <DetailField label="Category" value={selectedMdi.category}/>
          <DetailField label="Created" value={fmtDate(selectedMdi.createdDateTime)}/>
          {selectedMdi.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMdi.description}</div></>}
          {(selectedMdi.mitreTechniques?.length ?? 0) > 0 && (
            <><div className="dm-section-hdr">MITRE Techniques</div>
            <div className="mitre-tags">{selectedMdi.mitreTechniques.map(t=><a key={t} href={`https://attack.mitre.org/techniques/${t}`} target="_blank" rel="noopener noreferrer" className="mitre-tag">{t}</a>)}</div></>
          )}
        </DetailModal>
      )}
      {selectedDetection && (
        <DetailModal
          title={selectedDetection.riskEventType?.replace(/([A-Z])/g," $1").trim() ?? "Risk Detection"}
          subtitle={`${selectedDetection.userPrincipalName ?? selectedDetection.userDisplayName ?? "Unknown user"}`}
          onClose={() => setSelectedDetection(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_IAM/IdentityProtectionMenuBlade/~/RiskDetections"
          portalLabel="View in Entra ID Protection"
        >
          <DetailField label="User Display Name" value={selectedDetection.userDisplayName}/>
          <DetailField label="User Principal Name" value={selectedDetection.userPrincipalName}/>
          <DetailField label="Detection ID" value={selectedDetection.id}/>
          <DetailField label="Risk Event Type" value={selectedDetection.riskEventType?.replace(/([A-Z])/g," $1").trim()}/>
          <DetailField label="Risk Level" value={selectedDetection.riskLevel}/>
          <DetailField label="Risk State" value={selectedDetection.riskState}/>
          <DetailField label="IP Address" value={selectedDetection.ipAddress}/>
          <DetailField label="Location" value={[selectedDetection.city, selectedDetection.country].filter(Boolean).join(", ")||null}/>
          <DetailField label="Activity DateTime" value={fmtDate(selectedDetection.activityDateTime)}/>
          <DetailField label="Last Updated" value={fmtDate(selectedDetection.lastUpdatedDateTime)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Key size={18}/>} label="MFA COVERAGE" value={`${mfaPct}%`}
          sub={`${identity?.mfa.registered??0} of ${identity?.mfa.total??0} users`} tone={pctTone(mfaPct,95,80)}/>
        <KpiTile icon={<UserX size={18}/>} label="RISKY USERS" value={riskyUsers.length}
          sub="Active risk detections" tone={riskyUsers.length===0?"good":riskyUsers.length<=3?"warning":"error"}/>
        <KpiTile icon={<Globe size={18}/>} label="FOREIGN SIGN-INS" value={identity?.signIns.foreign??0}
          sub="Last 7 days" tone={(identity?.signIns.foreign??0)===0?"good":"warning"}/>
        <KpiTile icon={<Users size={18}/>} label="GUEST ACCOUNTS" value={identity?.guests.total??0}
          sub="External users" tone={((identity?.guests.total??0)>20)?"warning":"good"}/>
      </div>

      <div className="sticky-filter-bar filters-bar">
        <label className="search-box">
          <Search size={15} color="#94a3b8"/>
          <input value={search} onChange={e=>setSearch(e.target.value)}
            placeholder="Search across all identity data — user, alert, role…" className="search-input"/>
        </label>
        <select value={riskLevel} onChange={e=>setRiskLevel(e.target.value)} className="filter-sel">
          <option value="">All risk levels</option>
          <option value="critical">Critical</option>
          <option value="high">High</option>
          <option value="medium">Medium</option>
          <option value="low">Low</option>
          <option value="informational">Informational</option>
        </select>
        <label className="toggle-label">
          <input type="checkbox" checked={showResolved} onChange={e=>setShowResolved(e.target.checked)}/>
          Show resolved
        </label>
        <ExportDropdown rows={[
          ...riskyUsers.map(a=>({ Section:"Risky Users", User:a.userPrincipalName??"", Severity:a.severity, Resolved:String(a.isResolved), Detected:a.detectedAt })),
          ...riskySignIns.map(a=>({ Section:"Risky Sign-ins", User:a.userPrincipalName??"", Severity:a.severity, Resolved:String(a.isResolved), Detected:a.detectedAt })),
          ...filteredMdiAlerts.map(a=>({ Section:"MDI Alerts", User:"", Severity:a.severity, Resolved:"", Detected:a.createdDateTime??"" })),
        ]} filename="identity-export.csv"/>
        {hasFilter&&<button className="btn-apply" onClick={()=>{setSearch("");setRiskLevel("");}}>Clear filters</button>}
        {search && (
          <span className="search-summary">
            {[
              mfaMissing.length > 0 && `${mfaMissing.length} MFA`,
              riskySignIns.length > 0 && `${riskySignIns.length} sign-in`,
              riskyUsers.length > 0 && `${riskyUsers.length} risky user`,
              filteredMdiAlerts.length > 0 && `${filteredMdiAlerts.length} MDI`,
              filteredDetections.length > 0 && `${filteredDetections.length} detection`,
            ].filter(Boolean).join(", ") || "0 matches"} match
          </span>
        )}
        <FilterPresets pageKey="identity" filters={{search,riskLevel}}
          onLoad={({search:s,riskLevel:r})=>{setSearch(s??"");setRiskLevel(r??"");}}/>
      </div>

      {/* Row 1: MFA (left, primary) + Risky Users (right) */}
      <div className="two-col">
        <Card title="MFA Registration Status" badge={<><Badge label={`${mfaPct}% covered`} tone={pctTone(mfaPct,95,80)}/><span className="card-count">{mfaMissing.length} missing</span></>}>
          <div className="mfa-hero">
            <CircleGauge pct={mfaPct} size={90} color={mfaPct>=95?"var(--status-good-icon)":mfaPct>=80?"var(--status-warn-icon)":"var(--status-error-icon)"}/>
            <div className="mfa-stats">
              <InfoRow label="MFA Registered" value={<><CheckCircle size={13} color="var(--status-good-icon)"/> {identity?.mfa.registered??0} users</>} tone="good"/>
              <InfoRow label="MFA Missing" value={<><XCircle size={13} color="#dc2626"/> {mfaMissing.length} users</>} tone="error"/>
              <InfoRow label="Total Users" value={identity?.mfa.total??0}/>
            </div>
          </div>
          <ProgressBar pct={mfaPct}/>
          {mfaMissing.length>0?(
            <div className="mini-list" style={{marginTop:14}}>
              <div className="list-count">{mfaMissing.length} user{mfaMissing.length!==1?"s":""} without MFA</div>
              <SectHdr>USERS WITHOUT MFA — {mfaMissing.length} shown</SectHdr>
              {mfaMissing.slice(0,8).map((a,i)=>(
                <div key={i} className="mini-row al-clickable" onClick={()=>onAlertClick(a)}>
                  <UserX size={12} color="#dc2626"/>
                  <span className="mr-user">{a.userPrincipalName}</span>
                  <Badge label="No MFA" tone="error"/>
                </div>
              ))}
              {mfaMissing.length>8&&<div className="more-link">+{mfaMissing.length-8} more</div>}
            </div>
          ):!identity?(
            <div className="empty-state" style={{paddingTop:8}}><p>Run a collection to load MFA data</p></div>
          ):mfaPct===0&&(identity?.mfa.total??0)===0?(
            <div className="empty-state" style={{paddingTop:8}}><p>No MFA data collected — check AuditLog.Read.All permission</p></div>
          ):(
            <div className="empty-state" style={{paddingTop:8}}><p>All users have MFA registered</p></div>
          )}
        </Card>

        <Card title="Risky Users" badge={<><Badge label={riskyUsers.length===0?"None":"Needs review"} tone={riskyUsers.length===0?"good":"error"}/><span className="card-count">{riskyUsers.length}</span></>}>
          {riskyUsers.length===0
            ?<EmptyState icon={<UserCheck size={28} color="#d1d5db"/>} message="No risky users detected"/>
            :(
              <div className="alert-list">
                {riskyUsers.slice(0,8).map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <span className="sev-dot" style={{background:sevColor(a.severity)}}/>
                    <div className="al-body">
                      <div className="al-title">{a.userPrincipalName??a.title}</div>
                      <div className="al-desc">{a.description}</div>
                    </div>
                    <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
      </div>

      {/* Row 2: Risky Sign-ins + Risk Detections */}
      <div className="two-col">
        <Card title="Risky Sign-ins" badge={<><Badge label={`${riskySignIns.length} risky`} tone={riskySignIns.length>0?"warning":"good"}/><span className="card-count">{riskySignIns.length}</span></>}>
          <div className="stat-row3" style={{marginBottom:14}}>
            <StatBox value={identity?.signIns.total??0} label="Total (24h)"/>
            <StatBox value={riskySignIns.length} label="Risky" color={riskySignIns.length>0?"var(--status-warn-text)":undefined}/>
            <StatBox value={identity?.signIns.foreign??0} label="Foreign" color={(identity?.signIns.foreign??0)>0?"var(--status-error-text)":undefined}/>
          </div>
          {riskySignIns.length>0?(
            <div className="alert-list">
              <div className="list-count">{riskySignIns.length} risky sign-in{riskySignIns.length!==1?"s":""}</div>
              <SectHdr>RECENT RISKY SIGN-INS — click to view</SectHdr>
              {riskySignIns.slice(0,6).map((a,i)=>(
                <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                  <span className="sev-dot" style={{background:sevColor(a.severity)}}/>
                  <div className="al-body">
                    <div className="al-title">{a.userPrincipalName??a.title}</div>
                    <div className="al-desc">{a.description}</div>
                  </div>
                  <span className="al-date">{fmtDate(a.detectedAt)}</span>
                </div>
              ))}
            </div>
          ):<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No risky sign-ins detected"/>}
        </Card>

        <Card title="Risk Detections" badge={<><Badge label={`${riskDetections?.total??0} detections`} tone={(riskDetections?.total??0)>0?"error":"good"}/><span className="card-count">{filteredDetections.length}</span></>}>
          {riskDetections?.error
            ?<EmptyState icon={<AlertTriangle size={28} color="#d1d5db"/>} message="Needs IdentityRiskEvent.Read.All"/>
            :(riskDetections?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No risk detections — no leaked credentials, password spray, or MITM attacks found"/>
              :(
                <>
                  <div className="stat-row3" style={{marginBottom:14}}>
                    <StatBox value={riskDetections!.byLevel?.["high"]??0} label="High" color={(riskDetections!.byLevel?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={riskDetections!.byLevel?.["medium"]??0} label="Medium" color={(riskDetections!.byLevel?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={riskDetections!.byLevel?.["low"]??0} label="Low"/>
                  </div>
                  <div className="mini-list">
                    <SectHdr>BY DETECTION TYPE</SectHdr>
                    {Object.entries(riskDetections!.byType).slice(0,7).map(([type,count])=>(
                      <div key={type} className="mini-row">
                        <AlertCircle size={11} color="var(--status-warn-icon)"/>
                        <span className="mr-user" style={{flex:1}}>{type.replace(/([A-Z])/g," $1").replace(/^./,c=>c.toUpperCase()).trim()}</span>
                        <Badge label={String(count)} tone={count>0?"warning":"neutral"}/>
                      </div>
                    ))}
                  </div>
                  <div className="mini-list" style={{marginTop:10}}>
                    <div className="list-count">{filteredDetections.length} of {riskDetections?.total??0} detection{(riskDetections?.total??0)!==1?"s":""}</div>
                    <SectHdr>DETECTIONS — {filteredDetections.length} shown</SectHdr>
                    {filteredDetections.length===0&&<div className="td-empty" style={{padding:8}}>No detections match the filter.</div>}
                    {filteredDetections.slice(0,6).map((d,i)=>(
                      <div key={d.id??i} className="al-item" onClick={()=>setSelectedDetection(d)}>
                        <span className={`sev-dot sev-${d.riskLevel.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{d.userPrincipalName?.split("@")[0]??d.userDisplayName??"Unknown"}</div>
                          <div className="row-meta">
                            <span className="row-meta-item">{d.riskEventType?.replace(/([A-Z])/g," $1").trim()}</span>
                            {d.city&&<span className="row-meta-item">{[d.city,d.country].filter(Boolean).join(", ")}</span>}
                            <span className="row-meta-item">{relTime(d.activityDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={d.riskLevel} tone={d.riskLevel==="high"?"error":d.riskLevel==="medium"?"warning":"neutral"}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>
      </div>

      {/* Row 3: MDI Alerts + PIM Role Activations */}
      <div className="two-col">
        <Card title="Defender for Identity Alerts" badge={<><Badge label={`${filteredMdiAlerts.length} / ${mdiAlerts?.total??0} alerts`} tone={(mdiAlerts?.total??0)>0?"error":"good"}/><span className="card-count">{filteredMdiAlerts.length}</span></>}>
          {mdiAlerts?.error
            ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All"/>
            :(mdiAlerts?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Defender for Identity alerts — no on-prem AD threats detected"/>
              :(
                <>
                  <div className="stat-row4" style={{marginBottom:14}}>
                    <StatBox value={mdiAlerts!.bySeverity?.["high"]??0} label="High" color={(mdiAlerts!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={mdiAlerts!.bySeverity?.["medium"]??0} label="Medium" color={(mdiAlerts!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={mdiAlerts!.bySeverity?.["low"]??0} label="Low"/>
                    <StatBox value={mdiAlerts!.bySeverity?.["informational"]??0} label="Info"/>
                  </div>
                  <div className="alert-list">
                    <div className="list-count">{filteredMdiAlerts.length} of {mdiAlerts?.total??0} MDI alert{(mdiAlerts?.total??0)!==1?"s":""}</div>
                    <SectHdr>MDI ALERTS — {filteredMdiAlerts.length} shown</SectHdr>
                    {filteredMdiAlerts.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                    {filteredMdiAlerts.slice(0,8).map((a,i)=>(
                      <div key={a.id??i} className="al-item" onClick={()=>setSelectedMdi(a)}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{a.title}</div>
                          <div className="row-meta">
                            {a.category&&<span className="row-meta-item">{a.category}</span>}
                            {a.status&&<Badge label={a.status} tone="neutral"/>}
                            <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":a.severity==="medium"||a.severity==="Medium"?"warning":"neutral"}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>

        <Card title="PIM Role Activations" badge={<Badge label={`${filteredPim.length} / ${pimData?.total??0} recent`} tone="neutral"/>}>
          {pimData?.error
            ?<EmptyState icon={<Key size={28} color="#d1d5db"/>} message="Needs RoleManagement.Read.Directory"/>
            :(pimData?.activations.length??0)===0
              ?<EmptyState icon={<Clock size={28} color="#d1d5db"/>} message="No recent role activations"/>
              :(
                <div className="act-list">
                  {filteredPim.length===0&&<div className="td-empty" style={{padding:12}}>No activations match the filter.</div>}
                  {filteredPim.map((a,i)=>(
                    <div key={a.id??i} className="act-row">
                      <div className={`act-badge act-${a.status==="Provisioned"||a.status==="Completed"?"good":"neutral"}`}>
                        <Key size={12}/>
                      </div>
                      <div className="act-body">
                        <span className="act-who">{a.principalDisplayName??a.principalUpn?.split("@")[0]??"Unknown"}</span>
                        <span className="act-what"> {a.roleName} · {a.action}</span>
                      </div>
                      <span className="act-date">{fmtDate(a.createdDateTime)}</span>
                    </div>
                  ))}
                </div>
              )
          }
        </Card>
      </div>

      {/* Row 4: Privileged Roles + Recent Admin Activity (informational, lower priority) */}
      <div className="two-col">
        <Card title="Privileged Roles" badge={<Badge label={`${privilegedRoles?.totalPrivilegedUsers??0} privileged users`} tone={(privilegedRoles?.totalPrivilegedUsers??0)>0?"warning":"good"}/>}>
          {privilegedRoles?.error
            ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message={`Could not load privileged roles: ${privilegedRoles.error}`}/>
            :(privilegedRoles?.roles.length??0)===0
              ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No high-privilege role members found"/>
              :(
                <div className="mini-list">
                  {privilegedRoles!.roles.map((r,i)=>{
                    const isGA = r.roleName==="Global Administrator";
                    return (
                      <div key={r.roleId??i} style={{marginBottom:12}}>
                        <div className="mini-row">
                          <Lock size={12} color={isGA?"var(--status-error-icon)":"var(--color-faint)"}/>
                          <span className="mr-user">{r.roleName}</span>
                          <Badge label={`${r.memberCount} member${r.memberCount===1?"":"s"}`} tone={isGA&&r.memberCount>0?"error":r.memberCount>0?"warning":"neutral"}/>
                        </div>
                        {r.members.slice(0,5).map((m,j)=>(
                          <div key={j} className="mini-row" style={{paddingLeft:22}}>
                            <User size={11} color="#94a3b8"/>
                            <span className="mr-user">{m.userPrincipalName??m.displayName??"Unknown"}</span>
                          </div>
                        ))}
                        {r.members.length>5&&<div className="more-link">+{r.members.length-5} more</div>}
                      </div>
                    );
                  })}
                </div>
              )
          }
        </Card>

        <Card title="Recent Admin Activity" badge={<Badge label={`${identity?.recentAdminActivity.length??0} events`} tone="neutral"/>}>
          {(identity?.recentAdminActivity.length??0)===0
            ?<EmptyState icon={<Clock size={28} color="#d1d5db"/>} message="Requires AuditLog.Read.All permission"/>
            :(
              <div className="act-list">
                {identity!.recentAdminActivity.map((a,i)=>(
                  <div key={i} className="act-row">
                    <div className={`act-badge act-${a.result==="success"?"good":"neutral"}`}>
                      {a.result==="success"?<CheckCircle size={12}/>:<AlertCircle size={12}/>}
                    </div>
                    <div className="act-body">
                      <span className="act-who">{a.initiatedByUser?.split("@")[0]??"System"}</span>
                      <span className="act-what"> {a.activityDisplayName}</span>
                    </div>
                    <span className="act-date">{fmtDate(a.activityDateTime)}</span>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
      </div>

      <Card title="Identity Sensor Health" badge={<Badge label={(identityHealth?.total??0)===0?"All Healthy":`${identityHealth?.total??0} issues`} tone={(identityHealth?.issues?.length??0)===0?"good":"error"}/>}>
        {identityHealth?.error
          ?<EmptyState icon={<AlertTriangle size={28} color="#d1d5db"/>} message="Needs IdentityBaseline.Read.All — add permission in Azure App Registration"/>
          :(identityHealth?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={36} color="#d1d5db"/>} message="All identity sensors reporting healthy — no MDI sensor gaps detected"/>
            :(
              <div className="alert-list">
                {identityHealth!.issues.map((iss,i)=>(
                  <div key={iss.id??i} className="al-item al-item-noclick">
                    <span className={`sev-dot sev-${iss.severity.toLowerCase()}`}/>
                    <div className="al-body">
                      <div className="al-title">{iss.displayName??iss.issueType??"Health Issue"}</div>
                      {iss.description&&<div className="al-desc">{iss.description}</div>}
                      {iss.sensorDNSNames.length>0&&<div className="al-desc">Sensor: {iss.sensorDNSNames.join(", ")}</div>}
                      {iss.recommendations&&<div className="al-desc tone-info">Fix: {iss.recommendations}</div>}
                    </div>
                    <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:4}}>
                      <Badge label={iss.severity} tone={iss.severity==="high"||iss.severity==="critical"?"error":"warning"}/>
                      <span className="al-date">{fmtDate(iss.createdDateTime)}</span>
                    </div>
                  </div>
                ))}
              </div>
            )
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// DEVICES PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function DevicesPage({ devices, alerts, mdeVulnerabilities, onAlertClick }:
  { devices: DevicesData|null; alerts: SecurityAlert[]; mdeVulnerabilities: MdeVulnerabilitiesData|null; onAlertClick:(a:SecurityAlert)=>void }) {
  const [selectedMde, setSelectedMde] = useState<MdeAlert|null>(null);
  // Per-card search states — each card searches independently
  const [ncSearch, setNcSearch]   = useState("");  // Non-Compliant Devices
  const [ncSev, setNcSev]         = useState("");
  const [staleSearch, setStaleSearch] = useState(""); // Not Checked In
  const [mdeSearch, setMdeSearch] = useState("");
  const [mdeSev, setMdeSev]       = useState("");

  const deviceAlerts = useMemo(() => alerts.filter(a => a.service==="Intune"), [alerts]);

  // Search checks deviceName, userPrincipalName AND title (title = "Non-compliant device: ADMIN")
  const nonCompliant = useMemo(() => {
    let items = deviceAlerts.filter(a => a.alertType==="NonCompliantDevice");
    if (ncSev) items = items.filter(a => a.severity.toLowerCase() === ncSev);
    if (ncSearch) {
      const q = ncSearch.toLowerCase();
      items = items.filter(a =>
        a.deviceName?.toLowerCase().includes(q) ||
        a.userPrincipalName?.toLowerCase().includes(q) ||
        a.title.toLowerCase().includes(q) ||
        a.description?.toLowerCase().includes(q)
      );
    }
    return items;
  }, [deviceAlerts, ncSearch, ncSev]);

  const notCheckedIn = useMemo(() => {
    let items = deviceAlerts.filter(a => a.alertType==="DeviceNotCheckedIn");
    if (staleSearch) {
      const q = staleSearch.toLowerCase();
      items = items.filter(a =>
        a.deviceName?.toLowerCase().includes(q) ||
        a.userPrincipalName?.toLowerCase().includes(q) ||
        a.title.toLowerCase().includes(q)
      );
    }
    return items;
  }, [deviceAlerts, staleSearch]);

  const filteredMde = useMemo(() => {
    let items = mdeVulnerabilities?.alerts ?? [];
    if (mdeSev) items = items.filter(a => a.severity.toLowerCase() === mdeSev);
    if (mdeSearch) { const q = mdeSearch.toLowerCase(); items = items.filter(a => (a.title??'').toLowerCase().includes(q)||(a.category??'').toLowerCase().includes(q)); }
    return items;
  }, [mdeVulnerabilities, mdeSev, mdeSearch]);

  const devComplPct = devices?.compliancePct??(devices?.nonCompliant===0?100:94);

  const complianceData = [
    { label:"Compliant", value: devices?(devices.totalDevices>0?Math.max(0,devices.totalDevices-devices.nonCompliant):0):0, color:"var(--status-good-icon)" },
    { label:"Non-Compliant", value: devices?.nonCompliant??0, color:"var(--status-error-icon)" },
    { label:"Not Checked In", value: devices?.notCheckedIn??0, color:"var(--status-warn-icon)" },
  ];

  return (
    <div className="page">
      {selectedMde && (
        <DetailModal
          title={selectedMde.title ?? "MDE Alert"}
          subtitle={`${selectedMde.severity} · ${selectedMde.category ?? "Defender for Endpoint"}`}
          onClose={() => setSelectedMde(null)}
          portalUrl={selectedMde.alertWebUrl ?? (selectedMde.id ? `https://security.microsoft.com/alerts/${selectedMde.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Alert ID" value={selectedMde.id}/>
          <DetailField label="Severity" value={selectedMde.severity}/>
          <DetailField label="Status" value={selectedMde.status}/>
          <DetailField label="Category" value={selectedMde.category}/>
          <DetailField label="Detected" value={fmtDate(selectedMde.createdDateTime)}/>
          {selectedMde.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMde.description}</div></>}
          {(selectedMde.mitreTechniques?.length ?? 0) > 0 && (
            <><div className="dm-section-hdr">MITRE Techniques</div>
            <div className="mitre-tags">{selectedMde.mitreTechniques.map(t=><a key={t} href={`https://attack.mitre.org/techniques/${t}`} target="_blank" rel="noopener noreferrer" className="mitre-tag">{t}</a>)}</div></>
          )}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Monitor size={18}/>} label="COMPLIANCE RATE" value={`${devComplPct}%`}
          sub={(devices?.totalDevices??0)>0?`${devices!.totalDevices} total devices`:"From collected alerts"}
          tone={pctTone(devComplPct)}/>
        <KpiTile icon={<XCircle size={18}/>} label="NON-COMPLIANT" value={devices?.nonCompliant??0}
          sub="Require immediate action" tone={(devices?.nonCompliant??0)===0?"good":(devices?.nonCompliant??0)<=3?"warning":"error"}/>
        <KpiTile icon={<Clock size={18}/>} label="NOT CHECKED IN" value={devices?.notCheckedIn??0}
          sub={`>${7} days inactive`} tone={(devices?.notCheckedIn??0)===0?"good":"warning"}/>
        <KpiTile icon={<ShieldAlert size={18}/>} label="TOTAL ALERTS" value={deviceAlerts.length}
          sub="Active device alerts" tone={deviceAlerts.length===0?"good":deviceAlerts.length<=5?"warning":"error"}/>
      </div>

      {/* Compliance Overview — full-width summary at top */}
      <Card title="Compliance Overview" badge={<Badge label={`${devComplPct}%`} tone={pctTone(devComplPct)}/>}>
        <div className="compliance-hero">
          <CircleGauge pct={devComplPct} size={100}/>
          <div style={{flex:1}}>
            <MiniBarChart items={complianceData}/>
          </div>
        </div>
        <div className="info-rows" style={{marginTop:12}}>
          <InfoRow label="Last Sync Window" value="7 days"/>
          <InfoRow label="Policy Engine" value="Microsoft Intune"/>
          <InfoRow label="Total Managed" value={(devices?.totalDevices??0)>0?devices!.totalDevices:"Unknown"}/>
        </div>
      </Card>

      {/* Non-Compliant Devices — full-width, search in card header */}
      <Card title="Non-Compliant Devices"
        badge={<Badge label={`${nonCompliant.length} devices`} tone={nonCompliant.length>0?"error":"good"}/>}
        action={
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={13} color="#94a3b8"/>
              <input value={ncSearch} onChange={e=>setNcSearch(e.target.value)}
                placeholder="Search device or user…" className="search-input"/>
            </label>
            <select value={ncSev} onChange={e=>setNcSev(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All severities</option>
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </select>
            <ExportDropdown rows={nonCompliant.map(a=>({ Device:a.deviceName??a.title, User:a.userPrincipalName??"", Severity:a.severity, Detected:a.detectedAt }))} filename="non-compliant-devices.csv"/>
            {(ncSearch||ncSev)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setNcSearch("");setNcSev("");}}>Clear</button>}
          </div>
        }>
        {deviceAlerts.filter(a=>a.alertType==="NonCompliantDevice").length===0
          ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="All devices are compliant"/>
          : nonCompliant.length===0
            ?<div className="td-empty" style={{padding:16}}>No devices match the filter.</div>
            :(
              <div className="alert-list">
                {nonCompliant.map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <Laptop size={14} color="#dc2626"/>
                    <div className="al-body">
                      <div className="al-title">{a.deviceName??a.title}</div>
                      <div className="al-desc">{a.userPrincipalName} · {a.description}</div>
                    </div>
                    <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:4}}>
                      <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                      <span className="al-date">{fmtDate(a.detectedAt)}</span>
                    </div>
                  </div>
                ))}
              </div>
            )
        }
      </Card>

      {/* Devices Not Checked In — search in card header */}
      <Card title="Devices Not Checked In"
        badge={<Badge label={`${notCheckedIn.length} stale`} tone={notCheckedIn.length>0?"warning":"good"}/>}
        action={
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={13} color="#94a3b8"/>
              <input value={staleSearch} onChange={e=>setStaleSearch(e.target.value)}
                placeholder="Search device or user…" className="search-input"/>
            </label>
            <ExportDropdown rows={notCheckedIn.map(a=>({ Device:a.deviceName??a.title, User:a.userPrincipalName??"", LastSeen:a.detectedAt }))} filename="stale-devices.csv"/>
            {staleSearch&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>setStaleSearch("")}>Clear</button>}
          </div>
        }>
        {deviceAlerts.filter(a=>a.alertType==="DeviceNotCheckedIn").length===0
          ?<EmptyState icon={<CheckCircle size={28} color="#d1d5db"/>} message="All devices checked in within the sync window"/>
          : notCheckedIn.length===0
            ?<div className="td-empty" style={{padding:16}}>No devices match the filter.</div>
            :(
              <div className="tbl-wrap">
                <table className="data-tbl">
                  <thead><tr><th>Device</th><th>User</th><th>Last Seen</th><th>Status</th><th></th></tr></thead>
                  <tbody>
                    {notCheckedIn.map((a,i)=>(
                      <tr key={i} className="tbl-row-click" onClick={()=>onAlertClick(a)}>
                        <td><div className="al-title trunc" style={{maxWidth:180}} title={a.deviceName??a.title}>{a.deviceName??a.title}</div></td>
                        <td><div className="trunc" style={{maxWidth:160}} title={a.userPrincipalName??undefined}>{a.userPrincipalName??"—"}</div></td>
                        <td className="al-date">{fmtDate(a.detectedAt)}</td>
                        <td><Badge label="Stale" tone="warning"/></td>
                        <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {notCheckedIn.length > 0 && <div className="tbl-count">{notCheckedIn.length} device{notCheckedIn.length!==1?"s":""}</div>}
              </div>
          )
        }
      </Card>

      <Card title="MDE Endpoint Alerts"
        badge={<><Badge label={`${filteredMde.length} / ${mdeVulnerabilities?.total??0} alerts`} tone={(mdeVulnerabilities?.total??0)>0?"error":"good"}/><span className="card-count">{filteredMde.length}</span></>}
        action={
          (mdeVulnerabilities?.configured && !mdeVulnerabilities?.error && (mdeVulnerabilities?.total??0)>0) ? (
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:180}}>
                <Search size={13} color="#94a3b8"/>
                <input value={mdeSearch} onChange={e=>setMdeSearch(e.target.value)}
                  placeholder="Search title, category…" className="search-input"/>
              </label>
              <select value={mdeSev} onChange={e=>setMdeSev(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All severities</option>
                <option value="high">High</option>
                <option value="medium">Medium</option>
                <option value="low">Low</option>
                <option value="informational">Informational</option>
              </select>
              <ExportDropdown rows={filteredMde.map(a=>({ Title:a.title??"", Severity:a.severity, Category:a.category??"", Status:a.status, Detected:a.createdDateTime??"" }))} filename="mde-alerts.csv"/>
              {(mdeSearch||mdeSev)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setMdeSearch("");setMdeSev("");}}>Clear</button>}
            </div>
          ) : undefined
        }>
        {mdeVulnerabilities?.error
          ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All"/>
          :(mdeVulnerabilities?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Defender for Endpoint alerts"/>
            :(
              <>
                <div className="stat-row4" style={{marginBottom:14}}>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["high"]??0} label="High" color={(mdeVulnerabilities!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["medium"]??0} label="Medium" color={(mdeVulnerabilities!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["low"]??0} label="Low"/>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["informational"]??0} label="Info"/>
                </div>
                <div className="alert-list">
                  <SectHdr>ENDPOINT ALERTS — {filteredMde.length} shown</SectHdr>
                  {filteredMde.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                  {filteredMde.slice(0,10).map((a,i)=>(
                    <div key={a.id??i} className="al-item" onClick={()=>setSelectedMde(a)}>
                      <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                      <div className="al-body">
                        <div className="al-title">{(a.title??"Untitled").length>60?(a.title??"").slice(0,60)+"…":a.title}</div>
                        <div className="row-meta">
                          {a.category&&<span className="row-meta-item">{a.category}</span>}
                          <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                          <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                        </div>
                      </div>
                      <Eye size={13} color="#94a3b8" style={{flexShrink:0}}/>
                    </div>
                  ))}
                  {filteredMde.length>10&&<div className="more-link">{filteredMde.length-10} more results — use search to narrow</div>}
                </div>
              </>
            )
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// EMAIL PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function EmailPage({ alerts, emailProtection, onAlertClick }:
  { alerts: SecurityAlert[]; emailProtection: EmailProtectionData|null; onAlertClick:(a:SecurityAlert)=>void }) {
  const [selectedMdo, setSelectedMdo] = useState<EmailProtectionAlert|null>(null);
  const [search, setSearch] = useState("");
  const [sevFilter, setSevFilter] = useState("");
  const [catFilter, setCatFilter] = useState("");

  const emailAlerts = alerts.filter(a => a.service==="ExchangeOnline");
  const quarantined = emailAlerts.filter(a => a.alertType==="QuarantinedMessage");
  const mailFlow = emailAlerts.filter(a => a.alertType==="MailFlowIssue");
  const malware = alerts.filter(a => a.alertType==="MalwareDetection");

  const mdoCategories = useMemo(()=>
    [...new Set((emailProtection?.alerts??[]).map(a=>a.category).filter((c):c is string=>!!c))].sort(),
  [emailProtection]);

  const filteredMdo = useMemo(() => {
    let items = emailProtection?.alerts ?? [];
    if (sevFilter) items = items.filter(a=>a.severity.toLowerCase()===sevFilter);
    if (catFilter) items = items.filter(a=>a.category===catFilter);
    if (search) { const q=search.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.description??'').toLowerCase().includes(q)); }
    return items;
  }, [emailProtection, sevFilter, catFilter, search]);

  return (
    <div className="page">
      {selectedMdo && (
        <DetailModal
          title={selectedMdo.title ?? "MDO Alert"}
          subtitle={`${selectedMdo.severity} · ${selectedMdo.category ?? "Defender for Office 365"}`}
          onClose={()=>setSelectedMdo(null)}
          portalUrl={selectedMdo.alertWebUrl ?? (selectedMdo.id ? `https://security.microsoft.com/alerts/${selectedMdo.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Alert ID" value={selectedMdo.id}/>
          <DetailField label="Severity" value={selectedMdo.severity}/>
          <DetailField label="Status" value={selectedMdo.status}/>
          <DetailField label="Category" value={selectedMdo.category}/>
          <DetailField label="Detected" value={fmtDate(selectedMdo.createdDateTime)}/>
          {selectedMdo.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMdo.description}</div></>}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Inbox size={18}/>} label="QUARANTINED" value={quarantined.length}
          sub="Messages held in quarantine" tone={quarantined.length===0?"good":quarantined.length<=5?"warning":"error"}/>
        <KpiTile icon={<AlertTriangle size={18}/>} label="MAIL FLOW ISSUES" value={mailFlow.length}
          sub="Active delivery problems" tone={mailFlow.length===0?"good":"error"}/>
        <KpiTile icon={<ShieldAlert size={18}/>} label="MALWARE DETECTED" value={malware.length}
          sub="Email-borne threats" tone={malware.length===0?"good":"error"}/>
        <KpiTile icon={<CheckCircle size={18}/>} label="DEFENDER STATUS" value={mailFlow.length===0?"Active":"Degraded"}
          sub="Office 365 Defender" tone={mailFlow.length===0?"good":"warning"}/>
      </div>

      <div className="two-col">
        <Card title="Quarantined Messages" badge={<Badge label={`${quarantined.length} held`} tone={quarantined.length>0?"warning":"good"}/>}>
          {quarantined.length===0
            ?<EmptyState icon={<Inbox size={28} color="#d1d5db"/>} message="No messages currently quarantined"/>
            :(
              <div className="alert-list">
                {quarantined.slice(0,8).map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <Archive size={13} color="var(--status-warn-icon)"/>
                    <div className="al-body">
                      <div className="al-title">{a.title}</div>
                      <div className="al-desc">{a.userPrincipalName} · {a.description}</div>
                    </div>
                    <span className="al-date">{fmtDate(a.detectedAt)}</span>
                  </div>
                ))}
              </div>
            )
          }
        </Card>

        <Card title="Mail Flow Issues" badge={<Badge label={mailFlow.length===0?"Healthy":"Action needed"} tone={mailFlow.length===0?"good":"error"}/>}>
          {mailFlow.length===0
            ?<EmptyState icon={<Send size={28} color="#d1d5db"/>} message="Mail flow is operating normally"/>
            :(
              <div className="alert-list">
                {mailFlow.map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <XCircle size={13} color="#dc2626"/>
                    <div className="al-body">
                      <div className="al-title">{a.title}</div>
                      <div className="al-desc">{a.description}</div>
                    </div>
                    <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
      </div>

      <Card title="Email Threat Summary">
        <div className="threat-grid">
          {[
            { icon:<ShieldAlert size={18}/>, label:"Malware Detections", value:malware.length, tone:"error" as Tone, desc:"Email-borne malware caught by Defender" },
            { icon:<Filter size={18}/>, label:"Quarantined Messages", value:quarantined.length, tone:"warning" as Tone, desc:"Held for review by Exchange protection" },
            { icon:<AlertTriangle size={18}/>, label:"Mail Flow Issues", value:mailFlow.length, tone:"error" as Tone, desc:"Active delivery disruptions" },
            { icon:<ShieldCheck size={18}/>, label:"MDO Alerts", value:(emailProtection?.configured&&!emailProtection.error)?emailProtection.total:"—", tone:((emailProtection?.total??0)>0?"warning":"good") as Tone, desc:emailProtection?.error?"Needs SecurityAlert.Read.All":"Defender for Office 365 detections" },
          ].map((t,i)=>(
            <div key={i} className="threat-card">
              <div className={`threat-icon tone-bg-${t.tone}`}>{t.icon}</div>
              <div className="threat-body">
                <div className="threat-label">{t.label}</div>
                <div className={`threat-value tone-${t.tone}`}>{t.value}</div>
                <div className="threat-desc">{t.desc}</div>
              </div>
            </div>
          ))}
        </div>
      </Card>

      <Card title="MDO Protection Alerts"
        badge={<Badge label={`${filteredMdo.length} / ${emailProtection?.total??0} alerts`} tone={(emailProtection?.total??0)>0?"warning":"good"}/>}
        action={(emailProtection?.configured && !emailProtection?.error && (emailProtection?.total??0)>0) ? (
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={14} color="#94a3b8"/>
              <input value={search} onChange={e=>setSearch(e.target.value)}
                placeholder="Search alert title, description…" className="search-input"/>
            </label>
            <select value={sevFilter} onChange={e=>setSevFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All severities</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
              <option value="informational">Informational</option>
            </select>
            {mdoCategories.length>0&&(
              <select value={catFilter} onChange={e=>setCatFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All categories</option>
                {mdoCategories.map(c=><option key={c} value={c}>{c}</option>)}
              </select>
            )}
            <ExportDropdown rows={filteredMdo.map(a=>({ Title:a.title??"", Severity:a.severity, Category:a.category??"", Status:a.status, Detected:a.createdDateTime??"" }))} filename="mdo-alerts.csv"/>
            {(search||sevFilter||catFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSearch("");setSevFilter("");setCatFilter("");}}>Clear</button>}
          </div>
        ) : undefined}>
        {emailProtection?.error
          ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All for Defender for Office 365"/>
          :(emailProtection?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Defender for Office 365 alerts"/>
            :(
              <>
                <div className="threat-grid" style={{marginBottom:14}}>
                  {Object.entries(emailProtection!.byCategory??{}).slice(0,4).map(([cat,count])=>(
                    <div key={cat} className="threat-card" style={{cursor:"pointer"}} onClick={()=>setCatFilter(catFilter===cat?"":cat)}>
                      <div className={`threat-icon ${catFilter===cat?"tone-bg-error":"tone-bg-warning"}`}><Flag size={18}/></div>
                      <div className="threat-body">
                        <div className="threat-label">{cat}</div>
                        <div className="threat-value tone-warning">{count}</div>
                      </div>
                    </div>
                  ))}
                </div>
                <div className="alert-list">
                  <SectHdr>MDO ALERTS — {filteredMdo.length} shown</SectHdr>
                  {filteredMdo.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                  {filteredMdo.slice(0,10).map((a,i)=>(
                    <div key={a.id??i} className="al-item" onClick={()=>setSelectedMdo(a)}>
                      <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                      <div className="al-body">
                        <div className="al-title">{a.title}</div>
                        <div className="row-meta">
                          {a.category&&<span className="row-meta-item">{a.category}</span>}
                          <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                          <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                        </div>
                      </div>
                      <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":"warning"}/>
                    </div>
                  ))}
                  {filteredMdo.length>10&&<div className="more-link">{filteredMdo.length-10} more — use filters to narrow</div>}
                </div>
              </>
            )
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// COMPLIANCE PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function CompliancePage({ secureScore, overview, dlpAlerts, purview, mcasAlerts, insiderRisk, attackSimulation }: { secureScore: SecureScore|null; overview: Overview|null; dlpAlerts: DlpAlertsData|null; purview: PurviewData|null; mcasAlerts: McasAlertsData|null; insiderRisk: InsiderRiskData|null; attackSimulation: AttackSimulationData|null }) {
  const [selectedDlp, setSelectedDlp] = useState<DlpAlert|null>(null);
  const [selectedMcas, setSelectedMcas] = useState<McasAlert|null>(null);
  const [selectedIrm, setSelectedIrm] = useState<InsiderRiskAlert|null>(null);
  const [selectedSim, setSelectedSim] = useState<AttackSim|null>(null);
  const [alertSearch, setAlertSearch] = useState("");
  const [alertSev, setAlertSev] = useState("");
  const [alertSource, setAlertSource] = useState("");

  const filteredDlp = useMemo(() => {
    let items = dlpAlerts?.alerts ?? [];
    if (alertSev) items = items.filter(a=>a.severity.toLowerCase()===alertSev);
    if (alertSearch) { const q=alertSearch.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.description??'').toLowerCase().includes(q)); }
    return items;
  }, [dlpAlerts, alertSev, alertSearch]);

  const filteredMcas = useMemo(() => {
    let items = mcasAlerts?.alerts ?? [];
    if (alertSev) items = items.filter(a=>a.severity.toLowerCase()===alertSev);
    if (alertSearch) { const q=alertSearch.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.category??'').toLowerCase().includes(q)); }
    return items;
  }, [mcasAlerts, alertSev, alertSearch]);

  const filteredIrm = useMemo(() => {
    let items = insiderRisk?.alerts ?? [];
    if (alertSev) items = items.filter(a=>a.severity.toLowerCase()===alertSev);
    if (alertSearch) { const q=alertSearch.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.description??'').toLowerCase().includes(q)); }
    return items;
  }, [insiderRisk, alertSev, alertSearch]);

  const hasAlertFilter = !!(alertSearch || alertSev);

  const frameworks = [
    { name:"NIST CSF 2.0", score:72, status:"In Progress" },
    { name:"ISO 27001", score:68, status:"In Progress" },
    { name:"CIS Controls v8", score:81, status:"Good Standing" },
    { name:"SOC 2 Type II", score:0, status:"Not Assessed" },
  ];

  const controls = [
    { area:"Identity & Access", score:secureScore?.configured?Math.round(secureScore.percentage*0.9):0, max:100, items:["MFA enforcement","Conditional Access","Privileged Identity Management"] },
    { area:"Device Health", score:secureScore?.configured?Math.round(secureScore.percentage*0.85):0, max:100, items:["Device compliance policies","Endpoint protection","BitLocker encryption"] },
    { area:"Data Protection", score:secureScore?.configured?Math.round(secureScore.percentage*0.7):0, max:100, items:["DLP policies","Sensitivity labels","Information barriers"] },
    { area:"Email Security", score:secureScore?.configured?Math.round(secureScore.percentage*0.95):0, max:100, items:["Anti-phishing","Safe Attachments","DKIM/DMARC"] },
  ];

  return (
    <div className="page">
      {selectedDlp && (
        <DetailModal title={selectedDlp.title ?? "DLP Alert"} subtitle={`${selectedDlp.severity} · DLP`}
          onClose={()=>setSelectedDlp(null)}
          portalUrl={selectedDlp.alertWebUrl ?? "https://compliance.microsoft.com/datalossprevention/alerts"}
          portalLabel="View DLP Alerts">
          <DetailField label="Alert ID" value={selectedDlp.id}/>
          <DetailField label="Severity" value={selectedDlp.severity}/>
          <DetailField label="Status" value={selectedDlp.status}/>
          <DetailField label="Category" value={selectedDlp.category}/>
          <DetailField label="Service Source" value={selectedDlp.serviceSource}/>
          <DetailField label="Detected" value={fmtDate(selectedDlp.createdDateTime)}/>
          {selectedDlp.description&&<><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedDlp.description}</div></>}
        </DetailModal>
      )}
      {selectedMcas && (
        <DetailModal title={selectedMcas.title ?? "Cloud App Alert"} subtitle={`${selectedMcas.severity} · MCAS`}
          onClose={()=>setSelectedMcas(null)}
          portalUrl={selectedMcas.alertWebUrl ?? (selectedMcas.id ? `https://security.microsoft.com/alerts/${selectedMcas.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR">
          <DetailField label="Alert ID" value={selectedMcas.id}/>
          <DetailField label="Severity" value={selectedMcas.severity}/>
          <DetailField label="Status" value={selectedMcas.status}/>
          <DetailField label="Category" value={selectedMcas.category}/>
          <DetailField label="Detected" value={fmtDate(selectedMcas.createdDateTime)}/>
          {selectedMcas.description&&<><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMcas.description}</div></>}
        </DetailModal>
      )}
      {selectedIrm && (
        <DetailModal title={selectedIrm.title ?? "Insider Risk Alert"} subtitle={`${selectedIrm.severity} · IRM`}
          onClose={()=>setSelectedIrm(null)}
          portalUrl={selectedIrm.alertWebUrl ?? (selectedIrm.id ? `https://security.microsoft.com/alerts/${selectedIrm.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR">
          <DetailField label="Alert ID" value={selectedIrm.id}/>
          <DetailField label="Severity" value={selectedIrm.severity}/>
          <DetailField label="Status" value={selectedIrm.status}/>
          <DetailField label="Category" value={selectedIrm.category}/>
          <DetailField label="Detected" value={fmtDate(selectedIrm.createdDateTime)}/>
          {selectedIrm.description&&<><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedIrm.description}</div></>}
        </DetailModal>
      )}
      {selectedSim && (
        <DetailModal title={selectedSim.displayName ?? "Attack Simulation"} subtitle={selectedSim.attackType ?? "Simulation"}
          onClose={()=>setSelectedSim(null)}
          portalUrl="https://security.microsoft.com/attacksimulator"
          portalLabel="View in Attack Simulator">
          <DetailField label="Simulation ID" value={selectedSim.id}/>
          <DetailField label="Attack Type" value={selectedSim.attackType?.replace(/([A-Z])/g," $1").trim()}/>
          <DetailField label="Status" value={selectedSim.status}/>
          <DetailField label="Users Targeted" value={String(selectedSim.numberOfUsersTargeted)}/>
          <DetailField label="Clicked Phishing" value={String(selectedSim.clickedPhishingLinkCount)}/>
          <DetailField label="Did Not Click" value={String(selectedSim.didNotClickLinkCount)}/>
          <DetailField label="Compromise Rate" value={`${selectedSim.compromisedRate.toFixed(1)}%`}/>
          <DetailField label="Created" value={fmtDate(selectedSim.createdDateTime)}/>
          <DetailField label="Completed" value={fmtDate(selectedSim.completionDateTime)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Shield size={18}/>} label="SECURE SCORE"
          value={secureScore?.configured&&!secureScore.error?`${secureScore.percentage}%`:"—"}
          sub="Microsoft 365 posture" tone={pctTone(secureScore?.percentage??0)}/>
        <KpiTile icon={<FileText size={18}/>} label="FRAMEWORKS" value={`${frameworks.filter(f=>f.score>0).length}/${frameworks.length}`}
          sub="Active assessments" tone="info"/>
        <KpiTile icon={<FileText size={18}/>} label="DLP VIOLATIONS" value={dlpAlerts?.configured&&!dlpAlerts.error?dlpAlerts.total:"—"}
          sub={dlpAlerts?.error?"Needs SecurityAlert.Read.All":"Data loss prevention alerts"} needsPerm={!!dlpAlerts?.error}
          tone={(dlpAlerts?.total??0)===0?"good":"warning"}/>
        <KpiTile icon={<Star size={18}/>} label="BEST PRACTICE SCORE" value={secureScore?.configured&&!secureScore.error?`${secureScore.percentage}%`:"—"}
          sub="vs industry avg ~55%" tone={pctTone(secureScore?.percentage??0)}/>
      </div>

      <div className="two-col">
        <Card title="Security Control Areas">
          <div className="controls-list">
            {controls.map((c,i)=>(
              <div key={i} className="control-item">
                <div className="control-head">
                  <span className="control-name">{c.area}</span>
                  <span className="control-score" style={{color:c.score>=80?"var(--status-good-text)":c.score>=60?"var(--status-warn-text)":"var(--status-error-text)"}}>{c.score}%</span>
                </div>
                <ProgressBar pct={c.score}/>
                <div className="control-items">
                  {c.items.map((item,j)=>(
                    <span key={j} className="control-tag">{item}</span>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </Card>

        <Card title="Compliance Frameworks">
          <div className="frameworks-list">
            {frameworks.map((f,i)=>(
              <div key={i} className="framework-item">
                <div className="fw-head">
                  <span className="fw-name">{f.name}</span>
                  <Badge label={f.status} tone={f.score===0?"neutral":f.score>=80?"good":"warning"}/>
                </div>
                {f.score>0&&<ProgressBar pct={f.score}/>}
                {f.score===0&&<div className="fw-empty">Assessment not started</div>}
              </div>
            ))}
          </div>
        </Card>
      </div>

      <div className="sticky-filter-bar filters-bar">
        <label className="search-box">
          <Search size={15} color="#94a3b8"/>
          <input value={alertSearch} onChange={e=>setAlertSearch(e.target.value)}
            placeholder="Search DLP, MCAS, IRM alerts…" className="search-input"/>
        </label>
        <select value={alertSev} onChange={e=>setAlertSev(e.target.value)} className="filter-sel">
          <option value="">All severities</option>
          <option value="high">High</option>
          <option value="medium">Medium</option>
          <option value="low">Low</option>
          <option value="informational">Informational</option>
        </select>
        <ExportDropdown rows={[
          ...filteredDlp.map(a=>({Source:"DLP",Title:a.title??"",Severity:a.severity,Status:a.status,Detected:a.createdDateTime??""})),
          ...filteredMcas.map(a=>({Source:"MCAS",Title:a.title??"",Severity:a.severity,Status:a.status,Detected:a.createdDateTime??""})),
          ...filteredIrm.map(a=>({Source:"IRM",Title:a.title??"",Severity:a.severity,Status:a.status,Detected:a.createdDateTime??""}))
        ]} filename="compliance-alerts.csv"/>
        {hasAlertFilter&&<button className="btn-apply" onClick={()=>{setAlertSearch("");setAlertSev("");}}>Clear</button>}
        <FilterPresets pageKey="compliance" filters={{alertSearch,alertSev}}
          onLoad={f=>{setAlertSearch(f.alertSearch??"");setAlertSev(f.alertSev??"");}}/>
      </div>

      <div className="two-col">
        <Card title="DLP Alerts" badge={<Badge label={`${filteredDlp.length} / ${dlpAlerts?.total??0} violations`} tone={(dlpAlerts?.total??0)>0?"error":"good"}/>}>
          {dlpAlerts?.error
            ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All"/>
            :(dlpAlerts?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No DLP violations detected"/>
              :(
                <div className="alert-list">
                  {filteredDlp.length===0&&<div className="td-empty" style={{padding:12}}>No violations match the filter.</div>}
                  {filteredDlp.slice(0,8).map((a,i)=>(
                    <div key={a.id??i} className="al-item" onClick={()=>setSelectedDlp(a)}>
                      <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                      <div className="al-body">
                        <div className="al-title">{a.title}</div>
                        <div className="row-meta">
                          <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                          {a.category&&<span className="row-meta-item">{a.category}</span>}
                          <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                        </div>
                      </div>
                      <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":"warning"}/>
                    </div>
                  ))}
                </div>
              )
          }
        </Card>

        <Card title="Sensitivity Labels" badge={<Badge label={`${purview?.labelCount??0} labels`} tone={(purview?.labelCount??0)>0?"info":"neutral"}/>}>
          {purview?.error
            ?<EmptyState icon={<FileText size={28} color="#d1d5db"/>} message="Needs InformationProtectionPolicy.Read permission"/>
            :(purview?.labelCount??0)===0
              ?<EmptyState icon={<FileText size={28} color="#d1d5db"/>} message="No sensitivity labels configured"/>
              :(
                <div className="mini-list">
                  {purview!.labels.map((l,i)=>(
                    <div key={l.id??i} className="mini-row">
                      <span className="status-dot" style={{background:l.color||"#94a3b8"}}/>
                      <span className="mr-user">{l.name}</span>
                      {l.description&&<span className="al-desc" style={{flex:1}}>{l.description}</span>}
                      {l.isActive&&<Badge label="Active" tone="good"/>}
                    </div>
                  ))}
                </div>
              )
          }
        </Card>
      </div>

      <div className="two-col">
        <Card title="Cloud App Anomalies" badge={<Badge label={`${filteredMcas.length} / ${mcasAlerts?.total??0} alerts`} tone={(mcasAlerts?.total??0)>0?"error":"good"}/>}>
          {mcasAlerts?.error
            ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All"/>
            :(mcasAlerts?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Cloud App anomalies — no impossible travel, mass downloads, or suspicious OAuth detected"/>
              :(
                <>
                  <div className="stat-row4" style={{marginBottom:14}}>
                    <StatBox value={mcasAlerts!.bySeverity?.["high"]??0} label="High" color={(mcasAlerts!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={mcasAlerts!.bySeverity?.["medium"]??0} label="Medium" color={(mcasAlerts!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={mcasAlerts!.bySeverity?.["low"]??0} label="Low"/>
                    <StatBox value={mcasAlerts!.bySeverity?.["informational"]??0} label="Info"/>
                  </div>
                  <div className="alert-list">
                    <SectHdr>CLOUD APP ALERTS — {filteredMcas.length} shown</SectHdr>
                    {filteredMcas.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                    {filteredMcas.slice(0,8).map((a,i)=>(
                      <div key={a.id??i} className="al-item" onClick={()=>setSelectedMcas(a)}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{a.title}</div>
                          <div className="row-meta">
                            {a.category&&<span className="row-meta-item">{a.category}</span>}
                            <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                            <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":"warning"}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>

        <Card title="Insider Risk Management" badge={<Badge label={`${filteredIrm.length} / ${insiderRisk?.total??0} alerts`} tone={(insiderRisk?.total??0)>0?"error":"good"}/>}>
          {insiderRisk?.error
            ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All — IRM alerts available if Purview IRM is licensed"/>
            :(insiderRisk?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Insider Risk alerts — no data exfiltration or policy violations detected"/>
              :(
                <>
                  <div className="stat-row3" style={{marginBottom:14}}>
                    <StatBox value={insiderRisk!.bySeverity?.["high"]??0} label="High" color={(insiderRisk!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={insiderRisk!.bySeverity?.["medium"]??0} label="Medium" color={(insiderRisk!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={insiderRisk!.bySeverity?.["low"]??0} label="Low"/>
                  </div>
                  <div className="alert-list">
                    <SectHdr>IRM ALERTS — {filteredIrm.length} shown</SectHdr>
                    {filteredIrm.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                    {filteredIrm.slice(0,8).map((a,i)=>(
                      <div key={a.id??i} className="al-item" onClick={()=>setSelectedIrm(a)}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{a.title}</div>
                          <div className="row-meta">
                            {a.category&&<span className="row-meta-item">{a.category}</span>}
                            <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                            <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":"warning"}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>
      </div>

      <Card title="Attack Simulation & Training" badge={<Badge label={`${attackSimulation?.total??0} simulations`} tone="neutral"/>}>
        {attackSimulation?.error
          ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs AttackSimulation.ReadWrite.All — add permission in Azure App Registration"/>
          :(attackSimulation?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No attack simulations configured — consider running phishing tests to measure user resilience"/>
            :(
              <>
                <div className="stat-row3" style={{marginBottom:14}}>
                  <StatBox value={attackSimulation!.total} label="Simulations Run"/>
                  <StatBox value={attackSimulation!.totalTargeted} label="Users Targeted"/>
                  <StatBox value={`${attackSimulation!.avgCompromiseRate}%`} label="Avg Compromise Rate"
                    color={attackSimulation!.avgCompromiseRate>30?"var(--status-error-text)":attackSimulation!.avgCompromiseRate>10?"var(--status-warn-text)":"var(--status-good-text)"}/>
                </div>
                <div className="tbl-wrap">
                  <table className="data-tbl">
                    <thead><tr><th>Simulation</th><th>Type</th><th>Targeted</th><th>Clicked</th><th>Compromise Rate</th><th>Status</th></tr></thead>
                    <tbody>
                      {attackSimulation!.simulations.slice(0,8).map((s,i)=>(
                        <tr key={s.id??i} className="tbl-row-click" onClick={()=>setSelectedSim(s)}>
                          <td><div className="al-title">{s.displayName??"Unnamed simulation"}</div></td>
                          <td className="al-desc">{s.attackType?.replace(/([A-Z])/g," $1").trim()??s.attackType??"—"}</td>
                          <td>{s.numberOfUsersTargeted}</td>
                          <td style={{color:s.clickedPhishingLinkCount>0?"var(--status-error-text)":"var(--status-good-text)",fontWeight:600}}>{s.clickedPhishingLinkCount}</td>
                          <td>
                            <span style={{color:s.compromisedRate>30?"var(--status-error-text)":s.compromisedRate>10?"var(--status-warn-text)":"var(--status-good-text)",fontWeight:600}}>
                              {s.compromisedRate.toFixed(1)}%
                            </span>
                          </td>
                          <td><Badge label={s.status} tone={s.status==="completed"?"good":s.status==="running"?"info":"neutral"}/></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// SERVICE HEALTH PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function ServiceHealthPage({ serviceHealth }: { serviceHealth: ServiceHealthData|null }) {
  const [selectedIssue, setSelectedIssue] = useState<{title:string;description?:string;severity:string;detectedAt:string;portalUrl?:string}|null>(null);
  const [svcSearch, setSvcSearch] = useState("");
  const [sevFilter, setSevFilter] = useState("");

  const total = serviceHealth?.total??0;

  const filteredIssues = useMemo(() => {
    let items = serviceHealth?.issues ?? [];
    if (sevFilter) items = items.filter(i=>i.severity.toLowerCase()===sevFilter);
    if (svcSearch) { const q=svcSearch.toLowerCase(); items=items.filter(i=>i.title.toLowerCase().includes(q)||(i.description??'').toLowerCase().includes(q)); }
    return items;
  }, [serviceHealth, sevFilter, svcSearch]);

  return (
    <div className="page">
      {selectedIssue && (
        <DetailModal
          title={selectedIssue.title}
          subtitle={`${selectedIssue.severity} · Service Health`}
          onClose={()=>setSelectedIssue(null)}
          portalUrl={selectedIssue.portalUrl ?? "https://admin.microsoft.com/#/servicehealth"}
          portalLabel="View in Admin Center"
        >
          <DetailField label="Title" value={selectedIssue.title}/>
          <DetailField label="Severity" value={selectedIssue.severity}/>
          <DetailField label="Detected" value={fmtDate(selectedIssue.detectedAt)}/>
          {selectedIssue.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedIssue.description}</div></>}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Activity size={18}/>} label="ACTIVE ISSUES" value={total}
          sub="Current advisories" tone={total===0?"good":total<=2?"warning":"error"}/>
        <KpiTile icon={<CheckCircle size={18}/>} label="HEALTHY SERVICES" value={`${Math.max(0, M365_SVCS.length - total)} / ${M365_SVCS.length}`}
          sub="Operating normally" tone={total===0?"good":"warning"}/>
        <KpiTile icon={<Clock size={18}/>} label="LAST CHECKED" value="Live" sub="Real-time from Graph API" tone="info"/>
        <KpiTile icon={<Globe size={18}/>} label="COVERAGE" value="Global" sub="All Microsoft datacenters" tone="neutral"/>
      </div>

      <Card title="Service Status Overview"
        badge={total>0?<Badge label={`${total} issue${total>1?"s":""}`} tone="warning"/>:<Badge label="All services operational" tone="good"/>}>
        <ServiceHealthGrid issues={serviceHealth?.issues??[]}/>
      </Card>

      {total>0?(
        <Card title="Active Advisories & Incidents"
          badge={<Badge label={`${filteredIssues.length} shown`} tone="neutral"/>}
          action={
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:180}}>
                <Search size={14} color="#94a3b8"/>
                <input value={svcSearch} onChange={e=>setSvcSearch(e.target.value)}
                  placeholder="Search service, description…" className="search-input"/>
              </label>
              <select value={sevFilter} onChange={e=>setSevFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All severities</option>
                <option value="high">High</option>
                <option value="medium">Medium</option>
                <option value="low">Low</option>
              </select>
              <ExportDropdown rows={filteredIssues.map(i=>({ Title:i.title, Severity:i.severity, Detected:i.detectedAt, Description:i.description??"" }))} filename="service-health-advisories.csv"/>
              {(svcSearch||sevFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSvcSearch("");setSevFilter("");}}>Clear</button>}
            </div>
          }>
          <div className="alert-list">
            {filteredIssues.length===0&&<div className="td-empty" style={{padding:12}}>No advisories match the filter.</div>}
            {filteredIssues.map((iss,i)=>(
              <div key={i} className="al-item" onClick={()=>setSelectedIssue(iss)}>
                <AlertCircle size={14} color="var(--status-warn-icon)"/>
                <div className="al-body">
                  <div className="al-title">{iss.title}</div>
                  <div className="row-meta">
                    <Badge label={iss.severity} tone={iss.severity==="High"||iss.severity==="Critical"?"error":"warning"}/>
                    <span className="row-meta-item">{relTime(iss.detectedAt) || fmtDate(iss.detectedAt)}</span>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </Card>
      ):(
        <Card title="Service Incident History">
          <EmptyState icon={<ShieldCheck size={36} color="#d1d5db"/>}
            message="No active incidents — all M365 services are operating normally"/>
        </Card>
      )}

      <Card title="Service Response Time Benchmarks">
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead><tr><th>Service</th><th>Status</th><th>SLA Uptime</th><th>Last Incident</th></tr></thead>
            <tbody>
              {M365_SVCS.map(svc=>{
                const hit=serviceHealth?.issues.some(i=>i.title.toLowerCase().includes(svc.split(" ")[0].toLowerCase()));
                return(
                  <tr key={svc}>
                    <td><div className="al-title">{svc}</div></td>
                    <td><Badge label={hit?"Advisory":"Operational"} tone={hit?"warning":"good"}/></td>
                    <td style={{color:"var(--status-good-text)",fontWeight:600}}>99.9%</td>
                    <td className="al-date">{hit?fmtDate(serviceHealth?.issues[0]?.detectedAt):"No recent incidents"}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// M365 CONNECTIVITY PAGE  (replaces static Network page)
// ═══════════════════════════════════════════════════════════════════════════════
function NetworkPage({ serviceHealth, signInLocations }: { serviceHealth: ServiceHealthData|null; signInLocations: SignInLocationsData|null }) {
  const [selectedSignIn, setSelectedSignIn] = useState<SignInEntry|null>(null);
  const svcIssues = serviceHealth?.total??0;

  // top apps by sign-in count
  const topApps = useMemo(()=>{
    if (!signInLocations?.recent.length) return [];
    const counts: Record<string,number> = {};
    signInLocations.recent.forEach(s=>{ if (s.app) counts[s.app]=(counts[s.app]??0)+1; });
    return Object.entries(counts).sort((a,b)=>b[1]-a[1]).slice(0,6).map(([app,count])=>({ label:app.replace("Microsoft ","").slice(0,22), value:count, color:"var(--color-primary)" }));
  },[signInLocations]);

  // per-service status from health data
  const svcStatus = M365_SVCS.map(svc=>{
    const issue = serviceHealth?.issues.find(i=>
      i.title.toLowerCase().includes(svc.split(" ")[0].toLowerCase())||
      i.title.toLowerCase().includes(svc.split(" ").at(-1)!.toLowerCase()));
    return { name:svc, issue, status:issue?"Advisory":"Operational" };
  });

  return (
    <div className="page">
      {selectedSignIn && (
        <DetailModal
          title={selectedSignIn.upn ?? "Sign-in Event"}
          subtitle={`${selectedSignIn.success ? "Successful" : "Failed"} · ${[selectedSignIn.city, selectedSignIn.country].filter(Boolean).join(", ") || "Unknown"}`}
          onClose={()=>setSelectedSignIn(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_IAM/SignInEventsV3Blade"
          portalLabel="View in Entra Sign-ins"
        >
          <DetailField label="User Principal Name" value={selectedSignIn.upn}/>
          <DetailField label="Application" value={selectedSignIn.app}/>
          <DetailField label="Result" value={selectedSignIn.success ? "Success" : "Failure"}/>
          <DetailField label="City" value={selectedSignIn.city}/>
          <DetailField label="Country" value={selectedSignIn.country}/>
          <DetailField label="Date/Time" value={fmtDate(selectedSignIn.created)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Activity size={18}/>} label="M365 SERVICE STATUS"
          value={svcIssues===0?"All Operational":`${svcIssues} Issue${svcIssues>1?"s":""}`}
          sub={svcIssues===0?"No active advisories":"Check advisories below"}
          tone={svcIssues===0?"good":svcIssues<=2?"warning":"error"}/>
        <KpiTile icon={<LogIn size={18}/>} label="SIGN-IN EVENTS" value={signInLocations?.total??"—"}
          sub="Last 100 sign-ins tracked" tone="neutral"/>
        <KpiTile icon={<XCircle size={18}/>} label="SIGN-IN FAILURES" value={signInLocations?.failures??"—"}
          sub="Auth failures in period" tone={(signInLocations?.failures??0)>10?"error":(signInLocations?.failures??0)>3?"warning":"good"}/>
        <KpiTile icon={<Globe size={18}/>} label="COUNTRIES" value={signInLocations?.countries??"—"}
          sub="Sign-in origin countries" tone={(signInLocations?.countries??0)>3?"warning":"good"}/>
      </div>

      <div className="two-col">
        <Card title="M365 Service Endpoint Status"
          badge={svcIssues>0?<Badge label={`${svcIssues} advisory`} tone="warning"/>:<Badge label="All operational" tone="good"/>}>
          <div className="svc-grid">
            {svcStatus.map(s=>(
              <div key={s.name} className="svc-item">
                <StatusDot status={s.issue?"warning":"good"}/>
                <span className="svc-name">{s.name}</span>
                <Badge label={s.status} tone={s.issue?"warning":"good"}/>
              </div>
            ))}
          </div>
          {svcIssues>0&&(
            <div style={{marginTop:12}}>
              <SectHdr>ACTIVE ADVISORIES</SectHdr>
              {serviceHealth!.issues.map((iss,i)=>(
                <div key={i} className="al-item al-item-noclick" style={{marginTop:4}}>
                  <AlertCircle size={13} color="var(--status-warn-icon)"/>
                  <div className="al-body">
                    <div className="al-title">{iss.title}</div>
                    {iss.description&&<div className="al-desc">{iss.description}</div>}
                  </div>
                  <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:4,minWidth:80}}>
                    <Badge label={iss.severity} tone={iss.severity==="High"||iss.severity==="Critical"?"error":"warning"}/>
                    <span className="al-date">{fmtShort(iss.detectedAt)}</span>
                    {iss.portalUrl&&<a href={iss.portalUrl} target="_blank" rel="noopener noreferrer" className="portal-link" onClick={e=>e.stopPropagation()}><ExternalLink size={11}/> Portal</a>}
                  </div>
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card title="Top Apps Accessed" badge={<Badge label="From sign-in log" tone="neutral"/>}>
          {topApps.length===0
            ? <EmptyState icon={<BarChart2 size={28} color="#d1d5db"/>} message="No sign-in data available"/>
            : <>
                <MiniBarChart items={topApps}/>
                <div className="tbl-wrap" style={{marginTop:12}}>
                  <table className="data-tbl">
                    <thead><tr><th>Application</th><th>Sign-in Events</th><th>Status</th></tr></thead>
                    <tbody>
                      {topApps.map((a,i)=>(
                        <tr key={i}>
                          <td><div className="al-title">{a.label}</div></td>
                          <td style={{fontWeight:600}}>{a.value}</td>
                          <td><Badge label="Reachable" tone="good"/></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
          }
        </Card>
      </div>

      <Card title="Recent Sign-in Activity" badge={<Badge label={`${signInLocations?.total??0} events`} tone="neutral"/>}>
        {!signInLocations?.configured||signInLocations.recent.length===0
          ? <EmptyState icon={<LogIn size={28} color="#d1d5db"/>} message="No recent sign-in data"/>
          : <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th>User</th><th>App</th><th>Location</th><th>Time</th><th>Result</th></tr></thead>
                <tbody>
                  {signInLocations.recent.map((s,i)=>(
                    <tr key={i} className="tbl-row-click" onClick={()=>setSelectedSignIn(s)}>
                      <td><div className="al-title">{s.upn?.split("@")[0]??"Unknown"}</div></td>
                      <td className="al-desc">{s.app??"—"}</td>
                      <td className="al-desc">{countryFlag(s.country)} {[s.city,s.country].filter(Boolean).join(", ")||"Unknown"}</td>
                      <td className="al-date">{relTime(s.created) || fmtDate(s.created)}</td>
                      <td><Badge label={s.success?"Success":"Failed"} tone={s.success?"good":"error"}/></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// INCIDENTS PAGE — security alerts + M365 service health advisories merged
// ═══════════════════════════════════════════════════════════════════════════════
type UnifiedItem =
  | { kind: "alert"; data: SecurityAlert }
  | { kind: "defender"; data: DefenderAlert }
  | { kind: "incident"; data: SecurityIncident }
  | { kind: "advisory"; data: { title: string; description?: string; severity: string; detectedAt: string; portalUrl?: string } };

type IncidentFilter = "all" | "alerts" | "defender" | "incidents" | "advisories";

function DefenderAlertModal({ alert, onClose }: { alert: DefenderAlert; onClose: () => void }) {
  const portalUrl = alert.alertWebUrl ?? (alert.id ? `https://security.microsoft.com/alerts/${alert.id}` : "https://security.microsoft.com/alerts");
  const sev = alert.severity.charAt(0).toUpperCase() + alert.severity.slice(1);
  return (
    <DetailModal
      title={alert.title ?? "Defender Alert"}
      subtitle={`${sev} · ${fmtDefenderSource(alert.serviceSource ?? "Defender")}`}
      onClose={onClose}
      portalUrl={portalUrl}
      portalLabel="View in Defender XDR"
    >
      <DetailField label="Alert ID" value={alert.id}/>
      <DetailField label="Severity" value={sev}/>
      <DetailField label="Status" value={alert.status}/>
      <DetailField label="Classification" value={alert.classification}/>
      <DetailField label="Service Source" value={fmtDefenderSource(alert.serviceSource ?? "")}/>
      <DetailField label="Detection Source" value={alert.detectionSource}/>
      <DetailField label="Category" value={alert.category}/>
      <DetailField label="Assigned To" value={alert.assignedTo}/>
      <DetailField label="Threat Actor" value={alert.actorDisplayName}/>
      <DetailField label="Threat" value={alert.threatDisplayName}/>
      <DetailField label="Incident ID" value={alert.incidentId}/>
      <DetailField label="Detected" value={fmtDate(alert.createdDateTime)}/>
      <DetailField label="Last Updated" value={fmtDate(alert.lastUpdateDateTime)}/>
      {alert.description && (
        <>
          <div className="dm-section-hdr">Description</div>
          <div className="dm-desc-block">{alert.description}</div>
        </>
      )}
      {((Array.isArray(alert.mitreTechniques)?alert.mitreTechniques:[]).filter(Boolean)).length>0&&(
        <>
          <div className="dm-section-hdr">MITRE Techniques</div>
          <div className="mitre-tags">
            {(Array.isArray(alert.mitreTechniques)?alert.mitreTechniques:[]).filter(Boolean).map(t=>(
              <a key={t} href={`https://attack.mitre.org/techniques/${t.replace(".","/")||t}`} target="_blank" rel="noopener noreferrer" className="mitre-tag">{t}</a>
            ))}
          </div>
        </>
      )}
      {alert.recommendedActions && (
        <>
          <div className="dm-section-hdr">Recommended Actions</div>
          <div className="dm-desc-block">{alert.recommendedActions}</div>
        </>
      )}
    </DetailModal>
  );
}

function IncidentsPage({ alerts, serviceHealth, defenderAlerts, securityIncidents, onAlertClick }:
  { alerts: SecurityAlert[]; serviceHealth: ServiceHealthData|null; defenderAlerts: DefenderAlertsData|null; securityIncidents: SecurityIncidentsData|null; onAlertClick:(a:SecurityAlert)=>void }) {
  const [search, setSearch] = useState("");
  const [severity, setSeverity] = useState("");
  const [typeFilter, setTypeFilter] = useState<IncidentFilter>("all");
  const [dateRange, setDateRange] = useState<"all"|"24h"|"7d"|"30d">("all");
  const [selectedDefender, setSelectedDefender] = useState<DefenderAlert|null>(null);
  const [selectedIncident, setSelectedIncident] = useState<SecurityIncident|null>(null);

  const unified = useMemo((): UnifiedItem[] => {
    const alertItems: UnifiedItem[] = alerts.map(a=>({ kind:"alert", data:a }));
    const defenderItems: UnifiedItem[] = (defenderAlerts?.alerts??[]).map(d=>({ kind:"defender", data:d }));
    const incidentItems: UnifiedItem[] = (securityIncidents?.incidents??[]).map(i=>({ kind:"incident", data:i }));
    const advisoryItems: UnifiedItem[] = (serviceHealth?.issues??[]).map(i=>({ kind:"advisory", data:i }));
    return [...defenderItems, ...incidentItems, ...alertItems, ...advisoryItems];
  }, [alerts, defenderAlerts, securityIncidents, serviceHealth]);

  const filtered = useMemo(() => {
    let items = unified;
    if (typeFilter==="alerts") items=items.filter(i=>i.kind==="alert");
    if (typeFilter==="defender") items=items.filter(i=>i.kind==="defender");
    if (typeFilter==="incidents") items=items.filter(i=>i.kind==="incident");
    if (typeFilter==="advisories") items=items.filter(i=>i.kind==="advisory");
    if (dateRange!=="all") {
      const cutoff = new Date();
      if (dateRange==="24h") cutoff.setHours(cutoff.getHours()-24);
      else if (dateRange==="7d") cutoff.setDate(cutoff.getDate()-7);
      else if (dateRange==="30d") cutoff.setDate(cutoff.getDate()-30);
      items = items.filter(i => {
        const dt = i.kind==="alert"?i.data.detectedAt:i.kind==="defender"?i.data.createdDateTime:i.kind==="incident"?i.data.createdDateTime:i.data.detectedAt;
        return dt ? new Date(dt)>=cutoff : true;
      });
    }
    if (severity) {
      const sl = severity.toLowerCase();
      items=items.filter(i=>{
        if (i.kind==="alert") return i.data.severity.toLowerCase()===sl;
        if (i.kind==="defender") return i.data.severity.toLowerCase()===sl;
        if (i.kind==="incident") return i.data.severity.toLowerCase()===sl;
        return i.data.severity.toLowerCase()===sl;
      });
    }
    if (search) {
      const q=search.toLowerCase();
      items=items.filter(i=>{
        if (i.kind==="alert") return i.data.title.toLowerCase().includes(q)||(i.data.userPrincipalName?.toLowerCase().includes(q)??false)||(i.data.deviceName?.toLowerCase().includes(q)??false);
        if (i.kind==="defender") return (i.data.title??'').toLowerCase().includes(q)||(i.data.serviceSource??'').toLowerCase().includes(q);
        if (i.kind==="incident") return (i.data.displayName??'').toLowerCase().includes(q);
        return i.data.title.toLowerCase().includes(q);
      });
    }
    return items;
  }, [unified, typeFilter, severity, search, dateRange]);

  const dbBySeverity = useMemo(() =>
    alerts.reduce((acc,a)=>({...acc,[a.severity]:(acc[a.severity]??0)+1}),{} as Record<string,number>),
  [alerts]);

  const defenderCount = defenderAlerts?.total ?? 0;
  const incidentCount = securityIncidents?.total ?? 0;
  const advisoryCount = serviceHealth?.total ?? 0;

  const counts: Record<IncidentFilter,number> = {
    all: unified.length,
    defender: defenderCount,
    incidents: incidentCount,
    alerts: alerts.length,
    advisories: advisoryCount,
  };

  const allSeverities = ["critical","high","medium","low","informational","unknown"];

  return (
    <div className="page">
      {selectedDefender&&<DefenderAlertModal alert={selectedDefender} onClose={()=>setSelectedDefender(null)}/>}
      {selectedIncident && (
        <DetailModal
          title={selectedIncident.displayName ?? "Security Incident"}
          subtitle={`${selectedIncident.severity} · ${selectedIncident.status}`}
          onClose={()=>setSelectedIncident(null)}
          portalUrl={selectedIncident.incidentWebUrl ?? (selectedIncident.id ? `https://security.microsoft.com/incidents/${selectedIncident.id}` : "https://security.microsoft.com/incidents")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Incident ID" value={selectedIncident.id}/>
          <DetailField label="Display Name" value={selectedIncident.displayName}/>
          <DetailField label="Severity" value={selectedIncident.severity}/>
          <DetailField label="Status" value={selectedIncident.status}/>
          <DetailField label="Classification" value={selectedIncident.classification}/>
          <DetailField label="Assigned To" value={selectedIncident.assignedTo}/>
          <DetailField label="Created" value={fmtDate(selectedIncident.createdDateTime)}/>
          <DetailField label="Last Updated" value={fmtDate(selectedIncident.lastUpdateDateTime)}/>
          {(selectedIncident.customTags?.length ?? 0) > 0 && <DetailField label="Tags" value={selectedIncident.customTags.join(", ")}/>}
          {selectedIncident.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedIncident.description}</div></>}
          {selectedIncident.recommendedActions && <><div className="dm-section-hdr">Recommended Actions</div><div className="dm-desc-block">{selectedIncident.recommendedActions}</div></>}
        </DetailModal>
      )}

      <div className="kpi-row kpi-row-5">
        <KpiTile icon={<ShieldAlert size={18}/>} label="DEFENDER ALERTS" value={defenderCount}
          sub={defenderAlerts?.error?"Permission needed":"Active, unresolved"} tone={defenderCount>0?"error":"good"}/>
        <KpiTile icon={<AlertCircle size={18}/>} label="SECURITY INCIDENTS" value={incidentCount}
          sub={securityIncidents?.error?"Permission needed":"Active incidents"} tone={incidentCount>0?"warning":"good"}/>
        <KpiTile icon={<AlertTriangle size={16}/>} label="DB CRITICAL"
          value={dbBySeverity["Critical"]??0}
          sub="Critical severity alerts"
          tone={(dbBySeverity["Critical"]??0)>0?"error":"good"}/>
        <KpiTile icon={<AlertTriangle size={16}/>} label="DB HIGH"
          value={dbBySeverity["High"]??0}
          sub="High severity alerts"
          tone={(dbBySeverity["High"]??0)>0?"warning":"good"}/>
        <KpiTile icon={<Bell size={18}/>} label="M365 ADVISORIES" value={advisoryCount} sub="Active advisories" tone={advisoryCount>0?"warning":"good"}/>
      </div>

      {(defenderAlerts?.configured && !defenderAlerts.error && defenderCount > 0) && (
        <Card title="Defender — By Source" badge={<Badge label={`${defenderCount} alerts`} tone="error"/>}>
          <MiniBarChart items={Object.entries(defenderAlerts.bySource??{}).map(([k,v])=>({ label:fmtDefenderSource(k), value:v, color:"var(--dot-high)" }))}/>
        </Card>
      )}
      {(defenderAlerts?.error || securityIncidents?.error) && (
        <Card title="Missing Permissions" badge={<Badge label="Action Required" tone="error"/>}>
          <div style={{display:"flex",flexDirection:"column",gap:10}}>
            {defenderAlerts?.error && (
              <InlineError title="Defender alerts unavailable" perm="SecurityAlert.Read.All"
                message={`Could not load Microsoft Defender alerts (${defenderAlerts.error}).`}/>
            )}
            {securityIncidents?.error && (
              <InlineError title="Security incidents unavailable" perm="SecurityIncident.Read.All"
                message={`Could not load security incidents (${securityIncidents.error}).`}/>
            )}
          </div>
        </Card>
      )}

      <Card title="All Incidents & Advisories" badge={<Badge label={`${filtered.length} shown`} tone="neutral"/>}>
        <div className="filters-bar">
          <div className="pill-group">
            {(["all","defender","incidents","alerts","advisories"] as IncidentFilter[]).map(t=>(
              <button key={t} className={`pill-btn ${typeFilter===t?"active":""}`} onClick={()=>setTypeFilter(t)}>
                {t==="all"?`All (${counts.all})`:t==="defender"?`Defender (${defenderCount})`:t==="incidents"?`Incidents (${incidentCount})`:t==="alerts"?`Security DB (${alerts.length})`:`Advisories (${advisoryCount})`}
              </button>
            ))}
          </div>
          <label className="search-box">
            <Search size={15} color="#94a3b8"/>
            <input value={search} onChange={e=>setSearch(e.target.value)}
              placeholder="Search title, user, source…" className="search-input"/>
          </label>
          <select value={severity} onChange={e=>setSeverity(e.target.value)} className="filter-sel">
            <option value="">All severities</option>
            {allSeverities.map(s=><option key={s} value={s}>{s.charAt(0).toUpperCase()+s.slice(1)}</option>)}
          </select>
          <div className="pill-group">
            {(["all","24h","7d","30d"] as const).map(r=>(
              <button key={r} className={`pill-btn ${dateRange===r?"active":""}`} onClick={()=>setDateRange(r)}>
                {r==="all"?"All time":r==="24h"?"Last 24h":r==="7d"?"Last 7d":"Last 30d"}
              </button>
            ))}
          </div>
          <ExportDropdown
            rows={filtered.map(i=>{
              if (i.kind==="alert") return { Source:"SecurityDB", Title:i.data.title, Severity:i.data.severity, User:i.data.userPrincipalName??"", Detected:i.data.detectedAt };
              if (i.kind==="defender") return { Source:"Defender", Title:i.data.title??"", Severity:i.data.severity, User:"", Detected:i.data.createdDateTime??"" };
              if (i.kind==="incident") return { Source:"Incident", Title:i.data.displayName??"", Severity:i.data.severity, User:"", Detected:i.data.createdDateTime??"" };
              return { Source:"Advisory", Title:i.data.title, Severity:i.data.severity, User:"", Detected:i.data.detectedAt };
            })}
            filename="incidents.csv"
          />
          {(search||severity||typeFilter!=="all"||dateRange!=="all")&&(
            <button className="btn-apply" onClick={()=>{setSearch("");setSeverity("");setTypeFilter("all");setDateRange("all");}}>Clear</button>
          )}
          <FilterPresets pageKey="incidents" filters={{search,severity,typeFilter,dateRange}}
            onLoad={f=>{setSearch(f.search??"");setSeverity(f.severity??"");setTypeFilter((f.typeFilter as IncidentFilter|undefined)||"all");setDateRange((f.dateRange as "all"|"24h"|"7d"|"30d"|undefined)||"all");}}/>
        </div>

        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead>
              <tr><th>Severity</th><th>Source</th><th>Title</th><th>Details</th><th>Detected</th><th></th></tr>
            </thead>
            <tbody>
              {filtered.length===0&&<tr><td colSpan={6} className="td-empty">No items match current filters.</td></tr>}
              {filtered.map((item,idx)=>{
                if (item.kind==="defender") {
                  const a=item.data;
                  const sev=a.severity.charAt(0).toUpperCase()+a.severity.slice(1);
                  return (
                    <tr key={`def-${idx}`} className="tbl-row-click row-border-defender" onClick={()=>setSelectedDefender(a)}>
                      <td><span className="sev-pill" style={{borderColor:sevColor(sev),color:sevColor(sev)}}>{sev}</span></td>
                      <td><span className="src-badge src-defender"><ShieldAlert size={10}/>{fmtDefenderSource(a.serviceSource??'Defender')}</span></td>
                      <td>
                        <div className="al-title trunc" title={a.title??undefined}>{a.title}</div>
                        <div className="al-desc">{a.category}{a.assignedTo ? ` · ${a.assignedTo}` : ""}</div>
                      </td>
                      <td className="al-desc">{a.status}{a.classification ? ` · ${a.classification}` : ""}</td>
                      <td className="al-date">{relTime(a.createdDateTime) || fmtDate(a.createdDateTime)}</td>
                      <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                    </tr>
                  );
                }
                if (item.kind==="incident") {
                  const i=item.data;
                  const sev=i.severity.charAt(0).toUpperCase()+i.severity.slice(1);
                  return (
                    <tr key={`inc-${idx}`} className="tbl-row-click row-border-incident" onClick={()=>setSelectedIncident(i)}>
                      <td><span className="sev-pill" style={{borderColor:sevColor(sev),color:sevColor(sev)}}>{sev}</span></td>
                      <td><span className="src-badge src-incident"><AlertCircle size={10}/>Incident</span></td>
                      <td>
                        <div className="al-title trunc" title={i.displayName??undefined}>{i.displayName??'Security Incident'}</div>
                        {i.assignedTo&&<div className="al-desc">Assigned: {i.assignedTo}</div>}
                      </td>
                      <td className="al-desc">{i.status} {i.classification ? `· ${i.classification}` : ""}</td>
                      <td className="al-date">{relTime(i.createdDateTime) || fmtDate(i.createdDateTime)}</td>
                      <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                    </tr>
                  );
                }
                if (item.kind==="advisory") {
                  const a=item.data;
                  return (
                    <tr key={`adv-${idx}`} className="row-border-advisory">
                      <td><span className="sev-pill sev-pill-medium">{a.severity}</span></td>
                      <td><span className="src-badge src-advisory"><Bell size={10}/>Advisory</span></td>
                      <td>
                        <div className="al-title trunc" title={a.title}>{a.title}</div>
                        {a.description&&<div className="al-desc">{a.description}</div>}
                      </td>
                      <td className="al-desc">M365 Service Health</td>
                      <td className="al-date">{fmtDate(a.detectedAt)}</td>
                      <td>{a.portalUrl&&<a href={a.portalUrl} target="_blank" rel="noopener noreferrer" className="portal-link"><ExternalLink size={11}/></a>}</td>
                    </tr>
                  );
                }
                const a=item.data;
                return (
                  <tr key={`db-${a.service}-${a.id}`} className="tbl-row-click row-border-db" onClick={()=>onAlertClick(a)}>
                    <td><span className="sev-pill" style={{borderColor:sevColor(a.severity),color:sevColor(a.severity)}}>{a.severity}</span></td>
                    <td><span className="src-badge src-db"><Database size={10}/>{fmtService(a.service)}</span></td>
                    <td>
                      <div className="al-title trunc" title={a.title}>{a.title}</div>
                      {a.description&&<div className="al-desc">{a.description}</div>}
                    </td>
                    <td className="trunc" style={{maxWidth:140}} title={a.userPrincipalName||a.deviceName||undefined}>{a.userPrincipalName||a.deviceName||"—"}</td>
                    <td className="al-date">{fmtDate(a.detectedAt)}</td>
                    <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {filtered.length > 0 && <div className="tbl-count">{filtered.length} of {unified.length} items</div>}
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// LICENSES & USERS PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function LicensesPage({ licenses, inactive, passwords }: {
  licenses: LicenseData|null; inactive: InactiveUsersData|null; passwords: PasswordExpiryData|null
}) {
  const [userSearch, setUserSearch] = useState("");
  const [inactiveSort, setInactiveSort] = useState<"days"|"alpha">("days");
  const [showLicensedOnly, setShowLicensedOnly] = useState(false);
  const [skuSearch, setSkuSearch] = useState("");

  const filteredInactive = useMemo(() => {
    let items = inactive?.inactive90 ?? [];
    if (showLicensedOnly) items = items.filter(u=>u.hasLicense);
    if (userSearch) { const q=userSearch.toLowerCase(); items=items.filter(u=>u.upn.toLowerCase().includes(q)||(u.name??'').toLowerCase().includes(q)); }
    if (inactiveSort==="days") items = [...items].sort((a,b)=>b.daysSince-a.daysSince);
    else items = [...items].sort((a,b)=>a.upn.localeCompare(b.upn));
    return items;
  }, [inactive, userSearch, inactiveSort, showLicensedOnly]);

  const filteredSkus = useMemo(() => {
    let items = licenses?.skus ?? [];
    if (skuSearch) { const q=skuSearch.toLowerCase(); items=items.filter(s=>s.name.toLowerCase().includes(q)); }
    return items;
  }, [licenses, skuSearch]);

  const utilPct = licenses?.totalPurchased ? Math.round(licenses.totalConsumed / licenses.totalPurchased * 100) : 0;
  return (
    <div className="page">
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Package size={18}/>} label="LICENSES PURCHASED" value={licenses?.totalPurchased??"—"}
          sub="Total across all SKUs" tone="neutral"/>
        <KpiTile icon={<CheckCircle size={18}/>} label="CONSUMED" value={licenses?.totalConsumed??"—"}
          sub={`${utilPct}% utilization`} tone={utilPct>95?"warning":utilPct>80?"good":"neutral"}/>
        <KpiTile icon={<UserX size={18}/>} label="INACTIVE USERS (90D)" value={inactive?.inactive90Count??"—"}
          sub="No sign-in in 90+ days" tone={(inactive?.inactive90Count??0)>0?"warning":"good"}/>
        <KpiTile icon={<Key size={18}/>} label="PASSWORDS EXPIRING" value={passwords?.expiringSoonCount??"—"}
          sub="Within next 14 days" tone={(passwords?.expiringSoonCount??0)>0?"warning":"good"}/>
      </div>

      <div className="two-col">
        <Card title="License Usage by SKU"
          badge={<Badge label={`${filteredSkus.length} / ${licenses?.skus.length??0} SKUs`} tone="neutral"/>}
          action={(licenses?.configured && (licenses?.skus.length??0)>0) ? (
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:160}}>
                <Search size={14} color="#94a3b8"/>
                <input value={skuSearch} onChange={e=>setSkuSearch(e.target.value)}
                  placeholder="Search SKU name…" className="search-input"/>
              </label>
              <ExportDropdown rows={filteredSkus.map(s=>({ SKU:s.name, Purchased:s.purchased, Consumed:s.consumed, Available:s.available }))} filename="licenses.csv"/>
              {skuSearch&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>setSkuSearch("")}>Clear</button>}
            </div>
          ) : undefined}>
          {(!licenses?.configured || !licenses.skus.length)
            ? (licenses?.configured && licenses.error
                ? <InlineError title="License data unavailable" perm="Organization.Read.All" message={licenses.error}/>
                : <EmptyState icon={<Package size={28} color="#d1d5db"/>} message={licenses?.error??"Requires Organization.Read.All permission"}/>)
            : <>
                <div className="util-banner">
                  <div className="ub-bar"><ProgressBar pct={utilPct} color={utilPct>95?"var(--status-error-icon)":utilPct>80?"var(--status-good-icon)":"var(--color-primary)"}/></div>
                  <div className="ub-pct">{utilPct}%</div>
                </div>
                <MiniBarChart items={filteredSkus.slice(0,8).map(s=>({ label:s.name.replace(/_/g," ").slice(0,22), value:s.consumed, color:s.available<=5?"#dc2626":"#3b82f6" }))}/>
                <div className="tbl-wrap" style={{marginTop:12}}>
                  <table className="data-tbl">
                    <thead><tr><th>SKU</th><th>Purchased</th><th>Consumed</th><th>Available</th></tr></thead>
                    <tbody>
                      {filteredSkus.length===0&&<tr><td colSpan={4} className="td-empty">No SKUs match.</td></tr>}
                      {filteredSkus.map((s,i)=>(
                        <tr key={i}>
                          <td><div className="al-title">{s.name}</div></td>
                          <td>{s.purchased}</td>
                          <td>{s.consumed}</td>
                          <td style={{color:s.available<=5?"var(--status-error-text)":"var(--status-good-text)",fontWeight:600}}>{s.available}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
          }
        </Card>

        <Card title="Inactive Users — 90+ Days"
          badge={<Badge label={`${filteredInactive.length} / ${inactive?.inactive90Count??0} users`} tone={(inactive?.inactive90Count??0)>0?"warning":"good"}/>}
          action={(inactive?.configured && (inactive?.inactive90Count??0)>0) ? (
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:160}}>
                <Search size={14} color="#94a3b8"/>
                <input value={userSearch} onChange={e=>setUserSearch(e.target.value)}
                  placeholder="Search UPN or name…" className="search-input"/>
              </label>
              <select value={inactiveSort} onChange={e=>setInactiveSort(e.target.value as "days"|"alpha")} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="days">Sort: Most inactive</option>
                <option value="alpha">Sort: A–Z</option>
              </select>
              <label className="toggle-label">
                <input type="checkbox" checked={showLicensedOnly} onChange={e=>setShowLicensedOnly(e.target.checked)}/>
                Licensed only
              </label>
              <ExportDropdown rows={filteredInactive.map(u=>({ UPN:u.upn, Name:u.name??"", LastSignIn:u.lastSignIn??"Never", DaysSince:u.daysSince, HasLicense:u.hasLicense }))} filename="inactive-users.csv"/>
              {(userSearch||showLicensedOnly)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setUserSearch("");setShowLicensedOnly(false);}}>Clear</button>}
            </div>
          ) : undefined}>
          {!inactive?.configured
            ? <EmptyState icon={<UserX size={28} color="#d1d5db"/>} message={inactive?.error??"Requires AuditLog.Read.All + User.Read.All"}/>
            : inactive.inactive90Count===0
              ? <EmptyState icon={<UserCheck size={28} color="#d1d5db"/>} message="No users inactive for 90+ days"/>
              : <>

                  <div className="alert-list">
                    {filteredInactive.length===0&&<div className="td-empty" style={{padding:12}}>No users match the filter.</div>}
                    {filteredInactive.slice(0,15).map((u,i)=>(
                      <div key={i} className="al-item al-item-noclick">
                        <UserX size={14} color="var(--status-warn-icon)"/>
                        <div className="al-body">
                          <div className="al-title">{u.upn}</div>
                          <div className="al-desc">{u.lastSignIn?`Last seen ${fmtDate(u.lastSignIn)}`:"Never signed in"}</div>
                        </div>
                        <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:4}}>
                          {u.hasLicense&&<Badge label="Licensed" tone="warning"/>}
                          <span className={`al-date days-chip ${u.daysSince>90?"over90":""}`}>{u.daysSince>=0?`${u.daysSince}d ago`:"Never"}</span>
                        </div>
                      </div>
                    ))}
                    {filteredInactive.length>15&&<div className="more-link">{filteredInactive.length-15} more — use search to narrow</div>}
                  </div>
                </>
          }
        </Card>
      </div>

      <Card title="Password Expiry Status" badge={<Badge label={`${passwords?.expiringSoonCount??0} expiring · ${passwords?.expiredCount??0} expired`} tone={(passwords?.expiringSoonCount??0)+(passwords?.expiredCount??0)>0?"warning":"good"}/>}>
        {!passwords?.configured
          ? <EmptyState message={passwords?.error??"Requires User.Read.All permission"}/>
          : <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th>User</th><th>Days Until Expiry</th><th>Last Changed</th><th>Status</th></tr></thead>
                <tbody>
                  {passwords.expired.slice(0,5).map((u,i)=>{
                    const c=expiryChip(u.daysUntilExpiry);
                    return (
                    <tr key={`exp-${i}`}>
                      <td><div className="al-title">{u.upn}</div></td>
                      <td><span className={`expiry-chip ${c.cls}`}><Clock size={11}/>{c.label}</span></td>
                      <td className="al-date">{fmtDate(u.lastChanged)}</td>
                      <td><Badge label="Expired" tone="error"/></td>
                    </tr>
                  );})}
                  {passwords.expiringSoon.map((u,i)=>{
                    const c=expiryChip(u.daysUntilExpiry);
                    return (
                    <tr key={`soon-${i}`}>
                      <td><div className="al-title">{u.upn}</div></td>
                      <td><span className={`expiry-chip ${c.cls}`}><Clock size={11}/>{c.label}</span></td>
                      <td className="al-date">{fmtDate(u.lastChanged)}</td>
                      <td><Badge label={u.daysUntilExpiry<=3?"Critical":"Expiring Soon"} tone={u.daysUntilExpiry<=3?"error":"warning"}/></td>
                    </tr>
                  );})}
                  {passwords.expired.length===0&&passwords.expiringSoon.length===0&&(
                    <tr><td colSpan={4} className="td-empty">No passwords expiring in the next 14 days</td></tr>
                  )}
                </tbody>
              </table>
              <div className="info-rows" style={{marginTop:12}}>
                <InfoRow label="Never-expire accounts" value={passwords.neverExpiresCount} tone={passwords.neverExpiresCount>10?"warning":"neutral"}/>
                <InfoRow label="Total users checked" value={passwords.totalUsers}/>
              </div>
            </div>
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// CONDITIONAL ACCESS PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function ConditionalAccessPage({ data }: { data: ConditionalAccessData|null }) {
  const [selectedPolicy, setSelectedPolicy] = useState<CAPolicy|null>(null);
  const [policySearch, setPolicySearch] = useState("");
  const [stateFilter, setStateFilter] = useState("");

  const stateTone = (s: string): Tone => s==="enabled"?"good":s==="enabledForReportingButNotEnforced"?"warning":"neutral";
  const stateLabel = (s: string) => s==="enabled"?"Enabled":s==="enabledForReportingButNotEnforced"?"Report Only":"Disabled";
  const total = data?(data.enabled+data.disabled+data.reportOnly):0;

  const filteredPolicies = useMemo(() => {
    let items = data?.policies ?? [];
    if (stateFilter) items = items.filter(p=>p.state===stateFilter);
    if (policySearch) { const q=policySearch.toLowerCase(); items=items.filter(p=>p.name.toLowerCase().includes(q)||p.apps.toLowerCase().includes(q)||p.inclUsers.toLowerCase().includes(q)); }
    return items;
  }, [data, stateFilter, policySearch]);

  return (
    <div className="page">
      {selectedPolicy && (
        <DetailModal
          title={selectedPolicy.name}
          subtitle={`${stateLabel(selectedPolicy.state)} · Conditional Access`}
          onClose={()=>setSelectedPolicy(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade/~/Policies"
          portalLabel="View in Entra CA"
        >
          <DetailField label="Policy Name" value={selectedPolicy.name}/>
          <DetailField label="State" value={stateLabel(selectedPolicy.state)}/>
          <div className="dm-section-hdr">Conditions</div>
          <DetailField label="Users Included" value={selectedPolicy.inclUsers}/>
          <DetailField label="Users Excluded" value={selectedPolicy.exclUsers !== "None" ? selectedPolicy.exclUsers : null}/>
          <DetailField label="Applications" value={selectedPolicy.apps}/>
          {selectedPolicy.controls.length > 0 && (
            <>
              <div className="dm-section-hdr">Grant Controls</div>
              {selectedPolicy.controls.map((c,i)=><DetailField key={i} label={`Control ${i+1}`} value={c}/>)}
            </>
          )}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<ShieldCheck size={18}/>} label="ENABLED POLICIES" value={data?.enabled??"—"}
          sub="Actively enforced" tone={(data?.enabled??0)>0?"good":"warning"}/>
        <KpiTile icon={<Eye size={18}/>} label="REPORT ONLY" value={data?.reportOnly??"—"}
          sub="Not yet enforced" tone={(data?.reportOnly??0)>0?"info":"neutral"}/>
        <KpiTile icon={<ShieldOff size={18}/>} label="DISABLED" value={data?.disabled??"—"}
          sub="Not active" tone={(data?.disabled??0)>0?"warning":"good"}/>
        <KpiTile icon={<Shield size={18}/>} label="TOTAL POLICIES" value={total}
          sub="All CA policies" tone={total>0?"neutral":"warning"}/>
      </div>

      <Card title="Conditional Access Policies"
        badge={<Badge label={`${filteredPolicies.length} / ${data?.policies.length??0} policies`} tone="neutral"/>}
        action={(data?.configured && data.policies.length>0) ? (
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={14} color="#94a3b8"/>
              <input value={policySearch} onChange={e=>setPolicySearch(e.target.value)}
                placeholder="Search policy name, scope, apps…" className="search-input"/>
            </label>
            <select value={stateFilter} onChange={e=>setStateFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All states</option>
              <option value="enabled">Enabled</option>
              <option value="enabledForReportingButNotEnforced">Report Only</option>
              <option value="disabled">Disabled</option>
            </select>
            <ExportDropdown rows={filteredPolicies.map(p=>({ Name:p.name, State:stateLabel(p.state), Scope:p.inclUsers, Apps:p.apps, Controls:p.controls.join("; ") }))} filename="ca-policies.csv"/>
            {(policySearch||stateFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setPolicySearch("");setStateFilter("");}}>Clear</button>}
            <FilterPresets pageKey="ca-policies" filters={{policySearch,stateFilter}}
              onLoad={f=>{setPolicySearch(f.policySearch??"");setStateFilter(f.stateFilter??"");}}/>
          </div>
        ) : undefined}>
        {!data?.configured
          ? <EmptyState message={data?.error??"Requires Policy.Read.All permission"}/>
          : data.policies.length===0
            ? <EmptyState icon={<ShieldOff size={36} color="#d1d5db"/>} message="No Conditional Access policies found. This is a significant security gap."/>
            : <>
                <div className="tbl-wrap">
                  <table className="data-tbl">
                    <thead><tr><th>Policy Name</th><th>State</th><th>Scope</th><th>Applications</th><th>Controls Required</th></tr></thead>
                    <tbody>
                      {filteredPolicies.length===0&&<tr><td colSpan={5} className="td-empty">No policies match the filter.</td></tr>}
                      {filteredPolicies.map((p,i)=>(
                        <tr key={i} className="tbl-row-click" onClick={()=>setSelectedPolicy(p)}>
                          <td>
                            <div className="al-title trunc" style={{maxWidth:200}} title={p.name}>{p.name}</div>
                            <div className="al-desc">{p.inclUsers} → {p.apps}</div>
                          </td>
                          <td><Badge label={stateLabel(p.state)} tone={stateTone(p.state)}/></td>
                          <td>
                            <div className="al-desc trunc" style={{maxWidth:120}} title={p.inclUsers}>{p.inclUsers}</div>
                            {p.exclUsers!=="None"&&<div className="al-desc tone-warning">{p.exclUsers}</div>}
                          </td>
                          <td className="al-desc trunc" style={{maxWidth:120}} title={p.apps}>{p.apps}</td>
                          <td style={{display:"flex",flexWrap:"wrap",gap:4,paddingTop:8}}>
                            {p.controls.length>0?p.controls.map((c,j)=><Badge key={j} label={c} tone="info"/>):<span className="al-desc">None</span>}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {filteredPolicies.length > 0 && <div className="tbl-count">{filteredPolicies.length} of {data?.policies.length??0} policies</div>}
                </div>
              </>
        }
      </Card>

      <Card title="Control Coverage Analysis">
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead><tr><th>Control</th><th>Enforced By</th><th>Best Practice</th></tr></thead>
            <tbody>
              {[
                { control:"mfa", label:"Multi-Factor Authentication", rec:"Should cover all users & all apps at minimum" },
                { control:"compliantDevice", label:"Compliant Device Required", rec:"Apply to sensitive apps and admin portals" },
                { control:"approvedApplication", label:"Approved App Required", rec:"Restrict to managed apps for mobile access" },
              ].map((row,i)=>{
                const count=data?.policies.filter(p=>p.controls.includes(row.control)&&p.state==="enabled").length??0;
                return(
                  <tr key={i}>
                    <td><div className="al-title">{row.label}</div></td>
                    <td style={{fontWeight:600,color:count>0?"var(--status-good-text)":"var(--status-error-text)"}}>{count} active {count===1?"policy":"policies"}</td>
                    <td className="al-desc">{row.rec}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// AUDIT LOG PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function AuditLogPage({ data }: { data: AuditLogData|null }) {
  const [selectedEvent, setSelectedEvent] = useState<AuditEvent|null>(null);
  const [search, setSearch] = useState("");
  const [resultFilter, setResultFilter] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("");

  const filtered = useMemo(()=>{
    let items = data?.events??[];
    if (resultFilter) items=items.filter(e=>e.result===resultFilter);
    if (categoryFilter) items=items.filter(e=>e.category===categoryFilter);
    if (search){
      const q=search.toLowerCase();
      items=items.filter(e=>
        e.activityDisplayName?.toLowerCase().includes(q)||
        e.initiatedByUser?.toLowerCase().includes(q)||
        e.category?.toLowerCase().includes(q)||
        e.targetResources.some(t=>t?.toLowerCase().includes(q))
      );
    }
    return items;
  },[data,search,resultFilter,categoryFilter]);

  const categories = useMemo(()=>
    [...new Set((data?.events??[]).map(e=>e.category).filter((c): c is string => !!c))].sort(),
  [data]);

  return (
    <div className="page">
      {selectedEvent && (
        <DetailModal
          title={selectedEvent.activityDisplayName ?? "Audit Event"}
          subtitle={selectedEvent.initiatedByUser ?? "System"}
          onClose={()=>setSelectedEvent(null)}
          portalUrl="https://compliance.microsoft.com/auditlogsearch"
          portalLabel="View Audit Logs"
        >
          <DetailField label="Operation" value={selectedEvent.activityDisplayName}/>
          <DetailField label="Category" value={selectedEvent.category}/>
          <DetailField label="Result" value={selectedEvent.result}/>
          <DetailField label="Result Reason" value={selectedEvent.resultReason}/>
          <DetailField label="Activity DateTime" value={fmtDate(selectedEvent.activityDateTime)}/>
          <DetailField label="Initiated By" value={selectedEvent.initiatedByUser}/>
          {selectedEvent.targetResources.length > 0 && (
            <>
              <div className="dm-section-hdr">Target Resources</div>
              {selectedEvent.targetResources.map((t,i)=><DetailField key={i} label={`Target ${i+1}`} value={t}/>)}
            </>
          )}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<BookOpen size={18}/>} label="AUDIT EVENTS" value={data?.total??"—"}
          sub="Last 50 operations" tone="neutral"/>
        <KpiTile icon={<XCircle size={18}/>} label="FAILURES" value={data?.failures??"—"}
          sub="Failed operations" tone={(data?.failures??0)>0?"error":"good"}/>
        <KpiTile icon={<Database size={18}/>} label="CATEGORIES" value={categories.length}
          sub="Distinct activity types" tone="neutral"/>
        <KpiTile icon={<Clock size={18}/>} label="DATA FRESHNESS" value="Real-time" sub="Direct from Graph API" tone="good"/>
      </div>

      <Card title="Admin Audit Log"
        badge={<Badge label={`${filtered.length} events`} tone="neutral"/>}
        action={(data?.configured && !data.error) ? (
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={15} color="#94a3b8"/>
              <input value={search} onChange={e=>setSearch(e.target.value)}
                placeholder="Search actor, action, target…" className="search-input"/>
            </label>
            <select value={resultFilter} onChange={e=>setResultFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All results</option>
              <option value="success">Success</option>
              <option value="failure">Failure</option>
            </select>
            {categories.length>0&&(
              <select value={categoryFilter} onChange={e=>setCategoryFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All categories</option>
                {categories.map(c=><option key={c} value={c}>{c}</option>)}
              </select>
            )}
            <ExportDropdown rows={filtered.map(e=>({ Time:e.activityDateTime??"", Actor:e.initiatedByUser??"System", Action:e.activityDisplayName??"", Category:e.category??"", Target:e.targetResources.join("; "), Result:e.result??"" }))} filename="audit-log.csv"/>
            {(search||resultFilter||categoryFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSearch("");setResultFilter("");setCategoryFilter("");}}>Clear</button>}
            <FilterPresets pageKey="auditlog" filters={{search,resultFilter,categoryFilter}}
              onLoad={f=>{setSearch(f.search??"");setResultFilter(f.resultFilter??"");setCategoryFilter(f.categoryFilter??"");}}/>
          </div>
        ) : undefined}>
        {data==null
          ? <EmptyState message="Loading audit events…"/>
          : !data.configured
          ? <EmptyState message={data.error??"Requires AuditLog.Read.All permission"}/>
          : data.error
          ? <EmptyState message={data.error}/>
          : <>
              <div className="tbl-wrap">
                <table className="data-tbl">
                  <thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Target</th><th>Result</th></tr></thead>
                  <tbody>
                    {filtered.length===0&&<tr><td colSpan={5} className="td-empty">No events match the filter.</td></tr>}
                    {filtered.map((e,i)=>(
                      <tr key={i} className="tbl-row-click" onClick={()=>setSelectedEvent(e)}>
                        <td className="al-date">{relTime(e.activityDateTime) || fmtDate(e.activityDateTime)}</td>
                        <td><div className="al-title trunc" style={{maxWidth:120}} title={e.initiatedByUser??undefined}>{e.initiatedByUser?.split("@")[0]??"System"}</div></td>
                        <td>
                          <div className="al-title">{e.activityDisplayName}</div>
                          {e.category&&<div className="al-desc">{e.category}</div>}
                        </td>
                        <td className="al-desc trunc" style={{maxWidth:160}} title={e.targetResources.slice(0,2).join(", ")||undefined}>{e.targetResources.slice(0,2).join(", ")||"—"}</td>
                        <td>
                          <Badge label={e.result??"unknown"} tone={e.result==="success"?"good":e.result==="failure"?"error":"neutral"}/>
                          {e.resultReason&&<div className="al-desc">{e.resultReason}</div>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {filtered.length > 0 && <div className="tbl-count">{filtered.length} of {data?.total??0} events</div>}
              </div>
            </>
        }
      </Card>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// SIGN-IN LOCATIONS PAGE
// ═══════════════════════════════════════════════════════════════════════════════
function SignInLocationsPage({ data }: { data: SignInLocationsData|null }) {
  const [selectedSignIn, setSelectedSignIn] = useState<SignInEntry|null>(null);
  const [signInSearch, setSignInSearch] = useState("");
  const [resultFilter, setResultFilter] = useState<""|"success"|"failure">("");
  const [countryFilter, setCountryFilter] = useState("");

  const allCountries = useMemo(()=>
    [...new Set((data?.recent??[]).map(s=>s.country).filter((c):c is string=>!!c))].sort(),
  [data]);

  const filteredSignIns = useMemo(() => {
    let items = data?.recent ?? [];
    if (resultFilter==="success") items = items.filter(s=>s.success);
    if (resultFilter==="failure") items = items.filter(s=>!s.success);
    if (countryFilter) items = items.filter(s=>s.country===countryFilter);
    if (signInSearch) {
      const q=signInSearch.toLowerCase();
      items=items.filter(s=>
        (s.upn??'').toLowerCase().includes(q)||
        (s.country??'').toLowerCase().includes(q)||
        (s.city??'').toLowerCase().includes(q)||
        (s.app??'').toLowerCase().includes(q)
      );
    }
    return items;
  }, [data, resultFilter, countryFilter, signInSearch]);

  return (
    <div className="page">
      {selectedSignIn && (
        <DetailModal
          title={selectedSignIn.upn ?? "Sign-in Event"}
          subtitle={`${selectedSignIn.success ? "Successful" : "Failed"} · ${[selectedSignIn.city, selectedSignIn.country].filter(Boolean).join(", ") || "Unknown location"}`}
          onClose={()=>setSelectedSignIn(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_IAM/SignInEventsV3Blade"
          portalLabel="View in Entra Sign-ins"
        >
          <DetailField label="User Principal Name" value={selectedSignIn.upn}/>
          <DetailField label="Application" value={selectedSignIn.app}/>
          <DetailField label="Result" value={selectedSignIn.success ? "Success" : "Failure"}/>
          <DetailField label="City" value={selectedSignIn.city}/>
          <DetailField label="Country" value={selectedSignIn.country}/>
          <DetailField label="Date/Time" value={fmtDate(selectedSignIn.created)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<MapPin size={18}/>} label="COUNTRIES DETECTED" value={data?.countries??"—"}
          sub="Distinct sign-in countries" tone={(data?.countries??0)>5?"warning":"good"}/>
        <KpiTile icon={<LogIn size={18}/>} label="TOTAL SIGN-INS" value={data?.total??"—"}
          sub="Last 100 events" tone="neutral"/>
        <KpiTile icon={<XCircle size={18}/>} label="FAILED SIGN-INS" value={data?.failures??"—"}
          sub="Authentication failures" tone={(data?.failures??0)>5?"error":(data?.failures??0)>0?"warning":"good"}/>
        <KpiTile icon={<Globe size={18}/>} label="UNIQUE APPS" value={data?([...new Set(data.recent.map(s=>s.app).filter(Boolean))].length):"—"}
          sub="Apps accessed" tone="info"/>
      </div>

      <div className="two-col">
        <Card title="Sign-ins by Country" badge={<Badge label={`${data?.byCountry.length??0} countries`} tone="neutral"/>}>
          {!data?.configured
            ? <EmptyState message={data?.error??"Requires AuditLog.Read.All permission"}/>
            : data.byCountry.length===0
              ? <EmptyState icon={<Globe size={28} color="#d1d5db"/>} message="No location data available"/>
              : <>
                  <MiniBarChart items={data.byCountry.slice(0,8).map(c=>({
                    label:c.country??"Unknown", value:c.count, color:c.failures>2?"#dc2626":"#3b82f6"
                  }))}/>
                  <div className="tbl-wrap" style={{marginTop:12}}>
                    <table className="data-tbl">
                      <thead><tr><th>Country</th><th>Sign-ins</th><th>Failures</th></tr></thead>
                      <tbody>
                        {data.byCountry.map((c,i)=>(
                          <tr key={i}>
                            <td><span className="flag-cell"><span className="flag-emoji">{countryFlag(c.country)}</span><span className="al-title">{c.country||"Unknown"}</span></span></td>
                            <td>{c.count}</td>
                            <td style={{color:c.failures>0?"var(--status-error-text)":"var(--status-good-text)",fontWeight:600}}>{c.failures}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </>
          }
        </Card>

        <Card title="Recent Sign-in Activity"
          badge={<Badge label={`${filteredSignIns.length} shown`} tone="neutral"/>}
          action={(data?.configured && data.recent.length>0) ? (
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:180}}>
                <Search size={14} color="#94a3b8"/>
                <input value={signInSearch} onChange={e=>setSignInSearch(e.target.value)}
                  placeholder="Search user, country, city, app…" className="search-input"/>
              </label>
              <select value={resultFilter} onChange={e=>setResultFilter(e.target.value as ""|"success"|"failure")} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All results</option>
                <option value="success">Success</option>
                <option value="failure">Failure</option>
              </select>
              {allCountries.length>0&&(
                <select value={countryFilter} onChange={e=>setCountryFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                  <option value="">All countries</option>
                  {allCountries.map(c=><option key={c} value={c}>{countryFlag(c)} {c}</option>)}
                </select>
              )}
              <ExportDropdown rows={filteredSignIns.map(s=>({ User:s.upn??"", App:s.app??"", City:s.city??"", Country:s.country??"", Result:s.success?"Success":"Failure", Time:s.created??"" }))} filename="sign-ins.csv"/>
              {(signInSearch||resultFilter||countryFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSignInSearch("");setResultFilter("");setCountryFilter("");}}>Clear</button>}
              <FilterPresets pageKey="signins" filters={{signInSearch,resultFilter,countryFilter}}
                onLoad={f=>{setSignInSearch(f.signInSearch??"");setResultFilter((f.resultFilter as ""|"success"|"failure"|undefined)??"");setCountryFilter(f.countryFilter??"");}}/>
            </div>
          ) : undefined}>
          {!data?.configured
            ? <EmptyState message="Requires AuditLog.Read.All permission"/>
            : data.recent.length===0
              ? <EmptyState icon={<LogIn size={28} color="#d1d5db"/>} message="No recent sign-in data"/>
              : <>
                  <div className="alert-list">
                    {filteredSignIns.length===0&&<div className="td-empty" style={{padding:12}}>No sign-ins match the filter.</div>}
                    {filteredSignIns.map((s,i)=>(
                      <div key={i} className="al-item" onClick={()=>setSelectedSignIn(s)}>
                        <div style={{width:8,height:8,borderRadius:"50%",background:s.success?"var(--status-good-icon)":"var(--status-error-icon)",flexShrink:0,marginTop:3}}/>
                        <div className="al-body">
                          <div className="al-title">{s.upn?.split("@")[0]??"Unknown"}</div>
                          <div className="row-meta">
                            <span className="row-meta-item">{countryFlag(s.country)} {[s.city,s.country].filter(Boolean).join(", ")||"Unknown"}</span>
                            {s.app&&<span className="row-meta-item">{s.app}</span>}
                            <Badge label={s.success?"Success":"Failed"} tone={s.success?"good":"error"}/>
                            <span className="row-meta-item">{relTime(s.created)}</span>
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                </>
          }
        </Card>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// ALERT CENTER PAGE
// ═══════════════════════════════════════════════════════════════════════════════

type AcTab = "dashboard" | "alerts" | "policies" | "templates" | "notifications";

const POLICY_TEMPLATES_CATALOG = [
  { name: "Critical Alerts Monitor",   desc: "Triggers when any critical security alert is detected",              metric: "criticalAlertCount", threshold: 1, severity: "critical" as const, category: "identity"   as const },
  { name: "MFA Coverage Drop",         desc: "Triggers when more than 5 users are missing MFA",                   metric: "mfaMissingCount",    threshold: 5, severity: "high"     as const, category: "identity"   as const },
  { name: "Risky User Detected",       desc: "Triggers immediately when any user is marked as risky",             metric: "riskyUsersCount",    threshold: 1, severity: "high"     as const, category: "identity"   as const },
  { name: "Device Compliance Breach",  desc: "Triggers when non-compliant devices are found",                     metric: "nonCompliantCount",  threshold: 1, severity: "medium"   as const, category: "devices"    as const },
  { name: "Email Threat Surge",        desc: "Triggers when high-priority email alerts exceed threshold",          metric: "highAlertCount",     threshold: 3, severity: "high"     as const, category: "email"      as const },
  { name: "Stale Device",             desc: "Triggers when devices haven't checked in for 30+ days",             metric: "staleDeviceCount",   threshold: 1, severity: "medium"   as const, category: "devices"    as const },
  { name: "Sign-in Anomaly",          desc: "Triggers when failed sign-ins spike above threshold",               metric: "alertCount",         threshold: 10,severity: "high"     as const, category: "identity"   as const },
  { name: "Insider Risk Alert",       desc: "Triggers on any insider risk management alert",                     metric: "alertCount",         threshold: 1, severity: "high"     as const, category: "compliance" as const },
  { name: "Admin Role Change",        desc: "Tracks privileged role assignments via audit log",                  metric: "alertCount",         threshold: 1, severity: "medium"   as const, category: "identity"   as const },
];

function sevToneAC(s: string): Tone {
  return s === "critical" ? "error" : s === "high" ? "error" : s === "medium" ? "warning" : "info";
}
function statusTone(s: string): Tone {
  return s === "new" ? "error" : s === "acknowledged" ? "warning" : "good";
}

function PolicyModal({ policy, onSave, onClose }: {
  policy: Partial<AlertPolicy> | null;
  onSave: (p: AlertPolicy) => void;
  onClose: () => void;
}) {
  const [form, setForm] = useState<Partial<AlertPolicy>>(policy ?? { enabled: true, severity: "medium", category: "identity", threshold: 1, triggerCount: 0, notifyEmail: "" });
  const set = (k: keyof AlertPolicy, v: unknown) => setForm(f => ({ ...f, [k]: v }));

  const metricOptions: Record<string, { label: string; value: string }[]> = {
    identity:   [{ label: "Critical alert count", value: "criticalAlertCount" }, { label: "Risky users", value: "riskyUsersCount" }, { label: "MFA missing count", value: "mfaMissingCount" }, { label: "High alert count", value: "highAlertCount" }],
    devices:    [{ label: "Non-compliant count", value: "nonCompliantCount" }, { label: "Stale device count", value: "staleDeviceCount" }],
    email:      [{ label: "High alert count", value: "highAlertCount" }, { label: "Critical alert count", value: "criticalAlertCount" }],
    compliance: [{ label: "Alert count", value: "alertCount" }],
    licenses:   [{ label: "Expired license count", value: "expiredLicenseCount" }],
  };

  const handleSave = () => {
    if (!form.name?.trim()) { showToast("Policy name is required", "error"); return; }
    const policy: AlertPolicy = {
      id: form.id ?? crypto.randomUUID(),
      name: form.name!.trim(),
      enabled: form.enabled ?? true,
      category: form.category ?? "identity",
      condition: form.condition ?? `${form.metric} >= ${form.threshold}`,
      metric: form.metric ?? "criticalAlertCount",
      threshold: Number(form.threshold ?? 1),
      severity: form.severity ?? "medium",
      notifyEmail: form.notifyEmail ?? "",
      createdAt: form.createdAt ?? new Date().toISOString(),
      lastTriggered: form.lastTriggered,
      triggerCount: form.triggerCount ?? 0,
    };
    onSave(policy);
  };

  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [onClose]);

  return (
    <div className="detail-modal-backdrop" onClick={onClose}>
      <div className="detail-modal policy-modal" onClick={e => e.stopPropagation()}>
        <div className="detail-modal-hdr">
          <div className="dm-title">{form.id ? "Edit Policy" : "New Policy"}</div>
          <button className="modal-close" onClick={onClose}><X size={16}/></button>
        </div>
        <div className="detail-modal-body">
          <div className="policy-field">
            <label className="policy-label">Policy Name</label>
            <input className="policy-input" value={form.name ?? ""} onChange={e => set("name", e.target.value)} placeholder="e.g. Critical Alert Monitor"/>
          </div>
          <div className="policy-field">
            <label className="policy-label">Category</label>
            <select className="policy-input" value={form.category ?? "identity"} onChange={e => set("category", e.target.value)}>
              <option value="identity">Identity</option>
              <option value="devices">Devices</option>
              <option value="email">Email</option>
              <option value="compliance">Compliance</option>
              <option value="licenses">Licenses</option>
            </select>
          </div>
          <div className="policy-field">
            <label className="policy-label">Metric to Watch</label>
            <select className="policy-input" value={form.metric ?? ""} onChange={e => set("metric", e.target.value)}>
              <option value="">Select metric…</option>
              {(metricOptions[form.category ?? "identity"] ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
          <div className="policy-field">
            <label className="policy-label">Threshold (trigger when metric &ge; this value)</label>
            <input type="number" className="policy-input" min={1} value={form.threshold ?? 1} onChange={e => set("threshold", Number(e.target.value))}/>
          </div>
          <div className="policy-field">
            <label className="policy-label">Severity</label>
            <select className="policy-input" value={form.severity ?? "medium"} onChange={e => set("severity", e.target.value)}>
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </select>
          </div>
          <div className="policy-field">
            <label className="policy-label">Notify Email (overrides global SMTP recipient for this policy)</label>
            <input className="policy-input" type="email" value={form.notifyEmail ?? ""} onChange={e => set("notifyEmail", e.target.value)} placeholder="admin@contoso.com"/>
          </div>
        </div>
        <div className="detail-modal-footer">
          <button className="dm-close-btn" onClick={onClose}>Cancel</button>
          <button className="btn-run" style={{ padding:"7px 18px", fontSize:13 }} onClick={handleSave}>Save Policy</button>
        </div>
      </div>
    </div>
  );
}

function NotificationSettingsTab() {
  const [cfg, setCfg] = useState<NotificationSettings | null>(null);
  const [log, setLog] = useState<NotificationLogEntry[]>([]);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);

  const reload = useCallback(async () => {
    const [s, l] = await Promise.all([acApi.getSettings(), acApi.getLog()]);
    setCfg(s ?? { teamsEnabled:false, emailEnabled:false, smtpPort:587, smtpUseSsl:true, webhookEnabled:false, minSeverity:"low" });
    setLog(l);
  }, []);
  useEffect(() => { reload(); }, [reload]);

  if (!cfg) return <Card title="Notification Channels"><EmptyState icon={<Bell size={28} color="#d1d5db"/>} message="Loading settings…"/></Card>;

  const set = <K extends keyof NotificationSettings>(k: K, v: NotificationSettings[K]) => setCfg(c => c ? { ...c, [k]: v } : c);

  const save = async () => {
    setSaving(true);
    const ok = await acApi.saveSettings(cfg);
    setSaving(false);
    showToast(ok ? "Notification settings saved" : "Failed to save", ok ? "success" : "error");
    if (ok) reload();
  };

  const test = async () => {
    setTesting(true);
    const res = await acApi.testNotifications();
    setTesting(false);
    if (res.results?.length) {
      const summary = res.results.map(r => `${r.channel}: ${r.success ? "✓" : "✗"}`).join("  ");
      showToast(`Test sent — ${summary}`, res.ok ? "success" : "error");
    } else {
      showToast("No channels enabled to test", "error");
    }
    reload();
  };

  return (
    <>
      <div className="two-col">
        <Card title="Microsoft Teams / Slack" badge={<label className="toggle-label"><input type="checkbox" checked={cfg.teamsEnabled} onChange={e=>set("teamsEnabled", e.target.checked)}/> Enabled</label>}>
          <div className="policy-field">
            <span className="policy-label">Incoming Webhook URL</span>
            <input className="policy-input" placeholder="https://outlook.office.com/webhook/…" value={cfg.teamsWebhookUrl ?? ""} onChange={e=>set("teamsWebhookUrl", e.target.value)}/>
          </div>
          <p className="hdr-sub">Paste a Teams channel "Incoming Webhook" connector URL (or a Slack incoming webhook). A formatted alert card is posted on each trigger.</p>
        </Card>

        <Card title="Generic Webhook / SIEM" badge={<label className="toggle-label"><input type="checkbox" checked={cfg.webhookEnabled} onChange={e=>set("webhookEnabled", e.target.checked)}/> Enabled</label>}>
          <div className="policy-field">
            <span className="policy-label">Endpoint URL</span>
            <input className="policy-input" placeholder="https://…  (Sentinel, Splunk HEC, Power Automate)" value={cfg.webhookUrl ?? ""} onChange={e=>set("webhookUrl", e.target.value)}/>
          </div>
          <p className="hdr-sub">Each alert is POSTed as JSON. Use for SIEM ingestion or custom automation.</p>
        </Card>
      </div>

      <Card title="Email (SMTP)" badge={<label className="toggle-label"><input type="checkbox" checked={cfg.emailEnabled} onChange={e=>set("emailEnabled", e.target.checked)}/> Enabled</label>}>
        <div className="settings-grid">
          <div className="policy-field"><span className="policy-label">SMTP Host</span><input className="policy-input" placeholder="smtp.office365.com" value={cfg.smtpHost ?? ""} onChange={e=>set("smtpHost", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">Port</span><input className="policy-input" type="number" value={cfg.smtpPort} onChange={e=>set("smtpPort", Number(e.target.value))}/></div>
          <div className="policy-field"><span className="policy-label">Use SSL/TLS</span><label className="toggle-label" style={{marginTop:8}}><input type="checkbox" checked={cfg.smtpUseSsl} onChange={e=>set("smtpUseSsl", e.target.checked)}/> Enabled</label></div>
          <div className="policy-field"><span className="policy-label">Username</span><input className="policy-input" value={cfg.smtpUsername ?? ""} onChange={e=>set("smtpUsername", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">Password</span><input className="policy-input" type="password" placeholder={cfg.hasSmtpPassword ? "•••••• (unchanged)" : ""} value={cfg.smtpPassword ?? ""} onChange={e=>set("smtpPassword", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">From Address</span><input className="policy-input" placeholder="vigil365@yourdomain.com" value={cfg.fromAddress ?? ""} onChange={e=>set("fromAddress", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">Default Recipient</span><input className="policy-input" placeholder="secops@yourdomain.com" value={cfg.defaultRecipient ?? ""} onChange={e=>set("defaultRecipient", e.target.value)}/></div>
        </div>
      </Card>

      <Card title="Delivery Rules" action={<div style={{display:"flex",gap:8}}>
        <button className="btn-export" onClick={test} disabled={testing}>{testing ? "Testing…" : "Send test"}</button>
        <button className="btn-run" style={{padding:"7px 18px",fontSize:13}} onClick={save} disabled={saving}>{saving ? "Saving…" : "Save settings"}</button>
      </div>}>
        <div className="policy-field" style={{maxWidth:280}}>
          <span className="policy-label">Minimum severity to notify</span>
          <select className="policy-input" value={cfg.minSeverity} onChange={e=>set("minSeverity", e.target.value)}>
            <option value="low">Low and above</option>
            <option value="medium">Medium and above</option>
            <option value="high">High and above</option>
            <option value="critical">Critical only</option>
          </select>
        </div>
      </Card>

      <Card title="Notification History" badge={<Badge label={`${log.length} sent`} tone="neutral"/>}>
        {log.length === 0 ? (
          <EmptyState icon={<Bell size={28} color="#d1d5db"/>} message="No notifications sent yet. They appear here once an alert fires with a channel enabled."/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead><tr><th>Status</th><th>Channel</th><th>Policy</th><th>Target</th><th>Sent</th><th>Detail</th></tr></thead>
              <tbody>
                {log.map(l => (
                  <tr key={l.id}>
                    <td><span className={`ctrl-status ${l.success ? "ctrl-pass" : "ctrl-fail"}`}>{l.success ? "Sent" : "Failed"}</span></td>
                    <td style={{textTransform:"capitalize"}}>{l.channel}</td>
                    <td className="trunc" title={l.policyName}>{l.policyName}</td>
                    <td className="trunc" title={l.target}>{l.target}</td>
                    <td>{relTime(l.sentAt)}</td>
                    <td className="trunc" title={l.error}>{l.error ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}

function AlertCenterPage({ policies, triggeredAlerts, onChanged }: {
  policies: AlertPolicy[];
  triggeredAlerts: TriggeredAlert[];
  onChanged: () => void | Promise<void>;
}) {
  const [tab, setTab] = useState<AcTab>("dashboard");
  const [search, setSearch] = useState("");
  const [sevFilter, setSevFilter] = useState("");
  const [catFilter, setCatFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [editPolicy, setEditPolicy] = useState<Partial<AlertPolicy> | null>(null);
  const [showModal, setShowModal] = useState(false);
  const [selectedTriggered, setSelectedTriggered] = useState<TriggeredAlert | null>(null);

  const refresh = () => { onChanged(); };

  // ── KPI ──────────────────────────────────────────────────────────────────
  const enabledCount = policies.filter(p => p.enabled).length;
  const activeAlertsCount = triggeredAlerts.filter(a => a.status === "new").length;
  const today = new Date().toDateString();
  const triggeredToday = triggeredAlerts.filter(a => new Date(a.triggeredAt).toDateString() === today).length;
  const criticalCount = triggeredAlerts.filter(a => a.severity === "critical" && a.status === "new").length;

  // ── Bar chart: last 7 days ────────────────────────────────────────────────
  const last7 = useMemo(() => {
    const days: { date: string; count: number }[] = [];
    for (let i = 6; i >= 0; i--) {
      const d = new Date(); d.setDate(d.getDate() - i);
      const label = d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
      const ds = d.toDateString();
      days.push({ date: label, count: triggeredAlerts.filter(a => new Date(a.triggeredAt).toDateString() === ds).length });
    }
    return days;
  }, [triggeredAlerts]);

  const barMax = Math.max(...last7.map(d => d.count), 1);

  // ── Donut: by category ────────────────────────────────────────────────────
  const catCounts = useMemo(() => {
    const cats: Record<string, number> = {};
    triggeredAlerts.forEach(a => { cats[a.category] = (cats[a.category] ?? 0) + 1; });
    return Object.entries(cats).sort((a, b) => b[1] - a[1]);
  }, [triggeredAlerts]);

  const catColors: Record<string, string> = { identity: "#3b82f6", devices: "#8b5cf6", email: "#f59e0b", compliance: "#10b981", licenses: "#ec4899" };

  // ── Active alerts filter ──────────────────────────────────────────────────
  const filteredTA = useMemo(() => {
    let items = triggeredAlerts;
    if (search) { const q = search.toLowerCase(); items = items.filter(a => a.policyName.toLowerCase().includes(q) || a.condition.toLowerCase().includes(q)); }
    if (sevFilter) items = items.filter(a => a.severity === sevFilter);
    if (catFilter) items = items.filter(a => a.category === catFilter);
    if (statusFilter) items = items.filter(a => a.status === statusFilter);
    return items.sort((a, b) => new Date(b.triggeredAt).getTime() - new Date(a.triggeredAt).getTime());
  }, [triggeredAlerts, search, sevFilter, catFilter, statusFilter]);

  const acknowledge = async (id: string) => {
    if (await acApi.acknowledge(id)) { showToast("Alert acknowledged"); await onChanged(); }
  };

  const resolve = async (id: string) => {
    if (await acApi.resolve(id)) { showToast("Alert resolved"); await onChanged(); }
  };

  const handleSavePolicy = async (p: AlertPolicy) => {
    const exists = policies.some(x => x.id === p.id);
    const ok = exists ? await acApi.updatePolicy(p) : !!(await acApi.createPolicy(p));
    setShowModal(false);
    if (ok) { showToast("Policy saved"); await onChanged(); }
    else showToast("Failed to save policy", "error");
  };

  const handleDeletePolicy = async (id: string) => {
    if (!confirm("Delete this policy?")) return;
    if (await acApi.deletePolicy(id)) { showToast("Policy deleted"); await onChanged(); }
  };

  const togglePolicy = async (id: string) => {
    const p = policies.find(x => x.id === id);
    if (!p) return;
    if (await acApi.updatePolicy({ ...p, enabled: !p.enabled })) await onChanged();
  };

  const useTemplate = (t: typeof POLICY_TEMPLATES_CATALOG[0]) => {
    setEditPolicy({
      name: t.name,
      category: t.category,
      metric: t.metric,
      threshold: t.threshold,
      severity: t.severity,
      condition: t.desc,
      enabled: true,
      notifyEmail: "",
      triggerCount: 0,
    });
    setShowModal(true);
    setTab("policies");
  };

  return (
    <div className="page">
      {showModal && (
        <PolicyModal
          policy={editPolicy}
          onSave={handleSavePolicy}
          onClose={() => { setShowModal(false); setEditPolicy(null); }}
        />
      )}
      {selectedTriggered && (
        <div className="detail-modal-backdrop" onClick={() => setSelectedTriggered(null)}>
          <div className="detail-modal" onClick={e => e.stopPropagation()}>
            <div className="detail-modal-hdr">
              <div className="dm-title">{selectedTriggered.policyName}</div>
              <button className="modal-close" onClick={() => setSelectedTriggered(null)}><X size={16}/></button>
            </div>
            <div className="detail-modal-body">
              <DetailField label="Policy ID" value={selectedTriggered.policyId}/>
              <DetailField label="Severity" value={selectedTriggered.severity}/>
              <DetailField label="Category" value={selectedTriggered.category}/>
              <DetailField label="Condition" value={selectedTriggered.condition}/>
              <DetailField label="Metric Value" value={String(selectedTriggered.metricValue)}/>
              <DetailField label="Threshold" value={String(selectedTriggered.threshold)}/>
              <DetailField label="Status" value={selectedTriggered.status}/>
              <DetailField label="Triggered" value={fmtDate(selectedTriggered.triggeredAt)}/>
              {selectedTriggered.acknowledgedAt && <DetailField label="Acknowledged" value={fmtDate(selectedTriggered.acknowledgedAt)}/>}
            </div>
            <div className="detail-modal-footer">
              <button className="dm-close-btn" onClick={() => setSelectedTriggered(null)}>Close</button>
            </div>
          </div>
        </div>
      )}

      {/* Tabs */}
      <div className="ac-tabs">
        {(["dashboard","alerts","policies","templates","notifications"] as AcTab[]).map(t => (
          <button key={t} className={`ac-tab${tab===t?" active":""}`} onClick={() => { setTab(t); if (t === "alerts" || t === "dashboard") refresh(); }}>
            {t === "dashboard" ? "Dashboard" : t === "alerts" ? "Active Alerts" : t === "policies" ? "Policies" : t === "templates" ? "Templates" : "Notifications"}
          </button>
        ))}
      </div>

      {/* ── TAB: Notifications ── */}
      {tab === "notifications" && <NotificationSettingsTab/>}

      {/* ── TAB: Dashboard ── */}
      {tab === "dashboard" && (
        <>
          <div className="kpi-row">
            <KpiTile icon={<Bell size={18}/>}         label="ACTIVE POLICIES"   value={enabledCount}      sub={`${policies.length} total policies`}        tone={enabledCount>0?"good":"neutral"}/>
            <KpiTile icon={<AlertCircle size={18}/>}  label="ACTIVE ALERTS"     value={activeAlertsCount} sub="Unacknowledged"                              tone={activeAlertsCount>0?"error":"good"}/>
            <KpiTile icon={<Clock size={18}/>}        label="TRIGGERED TODAY"   value={triggeredToday}    sub={today}                                       tone={triggeredToday>0?"warning":"good"}/>
            <KpiTile icon={<ShieldAlert size={18}/>}  label="CRITICAL ALERTS"   value={criticalCount}     sub="Severity: critical"                          tone={criticalCount>0?"error":"good"}/>
          </div>

          <div className="mid-row">
            <Card title="Alerts Triggered (Last 7 Days)" className="card-score">
              {triggeredAlerts.length === 0 ? (
                <EmptyState icon={<Bell size={28} color="#d1d5db"/>} message="No alerts triggered yet. Policies are monitoring the environment."/>
              ) : (
                <svg viewBox={`0 0 420 110`} style={{ width:"100%", height:110 }}>
                  {last7.map((d, i) => {
                    const barH = barMax > 0 ? Math.max(4, (d.count / barMax) * 80) : 4;
                    const x = 10 + i * 58;
                    return (
                      <g key={d.date}>
                        <rect x={x} y={90 - barH} width={42} height={barH} rx={4} fill="#3b82f6" opacity="0.8"/>
                        {d.count > 0 && <text x={x+21} y={85-barH} textAnchor="middle" fontSize="10" fill="#3b82f6" fontWeight="600">{d.count}</text>}
                        <text x={x+21} y={106} textAnchor="middle" fontSize="9" fill="#94a3b8">{d.date}</text>
                      </g>
                    );
                  })}
                </svg>
              )}
            </Card>

            <Card title="Alerts by Category">
              {catCounts.length === 0 ? (
                <EmptyState icon={<Activity size={28} color="#d1d5db"/>} message="No triggered alerts yet"/>
              ) : (
                <div style={{ display:"flex", alignItems:"center", gap:16 }}>
                  <svg viewBox="0 0 100 100" width={100} height={100} style={{ flexShrink:0 }}>
                    {(() => {
                      const total = catCounts.reduce((s,[,v]) => s+v, 0);
                      let offset = 0;
                      return catCounts.map(([cat, count]) => {
                        const pct = count / total;
                        const circ = 2 * Math.PI * 38;
                        const dash = pct * circ;
                        const el = (
                          <circle key={cat} cx="50" cy="50" r="38" fill="none"
                            stroke={catColors[cat] ?? "#94a3b8"} strokeWidth="18"
                            strokeDasharray={`${dash} ${circ}`}
                            strokeDashoffset={-offset * circ}
                            transform="rotate(-90 50 50)"/>
                        );
                        offset += pct;
                        return el;
                      });
                    })()}
                    <circle cx="50" cy="50" r="29" fill="white"/>
                    <text x="50" y="54" textAnchor="middle" fontSize="12" fontWeight="700" fill="#0f172a">{catCounts.reduce((s,[,v])=>s+v,0)}</text>
                  </svg>
                  <div style={{ flex:1 }}>
                    {catCounts.map(([cat, count]) => (
                      <div key={cat} style={{ display:"flex", alignItems:"center", gap:6, marginBottom:4 }}>
                        <span style={{ width:10, height:10, borderRadius:"50%", background: catColors[cat]??"#94a3b8", flexShrink:0 }}/>
                        <span style={{ fontSize:12, flex:1, textTransform:"capitalize" }}>{cat}</span>
                        <span style={{ fontSize:12, fontWeight:600 }}>{count}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </Card>

            <Card title="Recent Alerts" action={<button className="btn-export" onClick={() => setTab("alerts")}>View All</button>}>
              {triggeredAlerts.length === 0 ? (
                <EmptyState icon={<CheckCircle size={24} color="#22c55e"/>} message="No alerts triggered yet"/>
              ) : (
                <div className="mini-list">
                  {[...triggeredAlerts].sort((a,b) => new Date(b.triggeredAt).getTime()-new Date(a.triggeredAt).getTime()).slice(0,10).map((a,i) => (
                    <div key={i} className="mini-row" style={{ cursor:"pointer" }} onClick={() => setSelectedTriggered(a)}>
                      <span className={`sev-dot sev-${a.severity}`}/>
                      <span className="mr-user" style={{ flex:1 }}>{a.policyName}</span>
                      <Badge label={a.status} tone={statusTone(a.status)}/>
                      <span className="mr-date">{relTime(a.triggeredAt)}</span>
                      {a.status === "new" && (
                        <button className="btn-ack" onClick={e => { e.stopPropagation(); acknowledge(a.id); }}>Ack</button>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </Card>
          </div>
        </>
      )}

      {/* ── TAB: Active Alerts ── */}
      {tab === "alerts" && (
        <Card title="Active Alerts" badge={<Badge label={`${filteredTA.length} shown`} tone="neutral"/>}
          action={
            <div style={{ display:"flex", gap:6, alignItems:"center", flexWrap:"wrap" }}>
              <label className="search-box">
                <Search size={14} color="#94a3b8"/>
                <input className="search-input" value={search} onChange={e=>setSearch(e.target.value)} placeholder="Search policy…"/>
              </label>
              <select className="filter-sel" value={sevFilter} onChange={e=>setSevFilter(e.target.value)}>
                <option value="">All severities</option>
                {["critical","high","medium","low"].map(s=><option key={s} value={s}>{s.charAt(0).toUpperCase()+s.slice(1)}</option>)}
              </select>
              <select className="filter-sel" value={catFilter} onChange={e=>setCatFilter(e.target.value)}>
                <option value="">All categories</option>
                {["identity","devices","email","compliance","licenses"].map(c=><option key={c} value={c}>{c.charAt(0).toUpperCase()+c.slice(1)}</option>)}
              </select>
              <select className="filter-sel" value={statusFilter} onChange={e=>setStatusFilter(e.target.value)}>
                <option value="">All statuses</option>
                <option value="new">New</option>
                <option value="acknowledged">Acknowledged</option>
                <option value="resolved">Resolved</option>
              </select>
              <ExportDropdown rows={filteredTA.map(a=>({ Policy:a.policyName, Severity:a.severity, Category:a.category, Condition:a.condition, MetricValue:a.metricValue, Threshold:a.threshold, Triggered:a.triggeredAt, Status:a.status }))} filename="triggered-alerts.csv"/>
              {(search||sevFilter||catFilter||statusFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSearch("");setSevFilter("");setCatFilter("");setStatusFilter("");}}>Clear</button>}
            </div>
          }>
          {filteredTA.length === 0 ? (
            <EmptyState icon={<CheckCircle size={28} color="#22c55e"/>} message="No alerts triggered yet. Your policies are monitoring the environment."/>
          ) : (
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr>
                    <th>Severity</th><th>Policy Name</th><th>Category</th><th>Condition</th>
                    <th>Value</th><th>Threshold</th><th>Triggered</th><th>Status</th><th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredTA.map(a => (
                    <tr key={a.id} className="clickable" onClick={() => setSelectedTriggered(a)}>
                      <td><Badge label={a.severity} tone={sevToneAC(a.severity)}/></td>
                      <td style={{ fontWeight:500 }}>{a.policyName}</td>
                      <td style={{ textTransform:"capitalize" }}>{a.category}</td>
                      <td style={{ fontSize:12, color:"var(--color-muted)" }}>{a.condition}</td>
                      <td style={{ fontWeight:600 }}>{a.metricValue}</td>
                      <td>{a.threshold}</td>
                      <td className="al-date">{relTime(a.triggeredAt)}</td>
                      <td><Badge label={a.status} tone={statusTone(a.status)}/></td>
                      <td onClick={e=>e.stopPropagation()} style={{ display:"flex", gap:4, alignItems:"center" }}>
                        {a.status === "new" && <button className="btn-ack" onClick={()=>acknowledge(a.id)}>Acknowledge</button>}
                        {a.status !== "resolved" && <button className="btn-resolve" onClick={()=>resolve(a.id)}>Resolve</button>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}

      {/* ── TAB: Policies ── */}
      {tab === "policies" && (
        <Card title="Alert Policies" badge={<Badge label={`${policies.length} policies`} tone="neutral"/>}
          action={<button className="btn-run" style={{ padding:"7px 14px", fontSize:13 }} onClick={() => { setEditPolicy(null); setShowModal(true); }}><Bell size={13}/> New Policy</button>}>
          {policies.length === 0 ? (
            <EmptyState icon={<Bell size={28} color="#d1d5db"/>} message="No policies yet. Create one or use a template."/>
          ) : (
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr><th>Name</th><th>Category</th><th>Condition</th><th>Severity</th><th>Status</th><th>Last Triggered</th><th>Count</th><th>Actions</th></tr>
                </thead>
                <tbody>
                  {policies.map(p => (
                    <tr key={p.id}>
                      <td style={{ fontWeight:500 }}>{p.name}</td>
                      <td style={{ textTransform:"capitalize" }}>{p.category}</td>
                      <td style={{ fontSize:12, color:"#64748b" }}>{p.condition}</td>
                      <td><Badge label={p.severity} tone={sevToneAC(p.severity)}/></td>
                      <td>
                        <button
                          onClick={() => togglePolicy(p.id)}
                          style={{ padding:"2px 10px", borderRadius:5, border:"1px solid", fontSize:11, fontWeight:600, cursor:"pointer",
                            borderColor: p.enabled?"var(--status-good-border)":"var(--color-border)", background: p.enabled?"var(--status-good-bg)":"var(--color-raised)", color: p.enabled?"var(--status-good-text)":"var(--color-muted)" }}>
                          {p.enabled ? "Enabled" : "Disabled"}
                        </button>
                      </td>
                      <td className="al-date">{p.lastTriggered ? relTime(p.lastTriggered) : "Never"}</td>
                      <td style={{ fontWeight:600 }}>{p.triggerCount}</td>
                      <td style={{ display:"flex", gap:4 }}>
                        <button className="btn-export" style={{ padding:"3px 8px" }} onClick={() => { setEditPolicy(p); setShowModal(true); }}>Edit</button>
                        <button className="btn-ack" onClick={() => handleDeletePolicy(p.id)}>Delete</button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}

      {/* ── TAB: Templates ── */}
      {tab === "templates" && (
        <Card title="Policy Templates" badge={<Badge label={`${POLICY_TEMPLATES_CATALOG.length} templates`} tone="neutral"/>}>
          <div className="template-grid">
            {POLICY_TEMPLATES_CATALOG.map((t, i) => (
              <div key={i} className="template-card">
                <div className="template-card-title">{t.name}</div>
                <div className="template-card-desc">{t.desc}</div>
                <div className="template-card-footer">
                  <div style={{ display:"flex", gap:4 }}>
                    <Badge label={t.severity} tone={sevToneAC(t.severity)}/>
                    <Badge label={t.category} tone="neutral"/>
                  </div>
                  <button className="btn-run" style={{ padding:"4px 12px", fontSize:12 }} onClick={() => useTemplate(t)}>Use Template</button>
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// SIDEBAR
// ═══════════════════════════════════════════════════════════════════════════════
const NAV: { id: NavPage; label: string; icon: React.ReactNode; group?: string }[] = [
  { id:"overview",         label:"Overview",             icon:<Home size={17}/> },
  { id:"identity",         label:"Identity",             icon:<Users size={17}/> },
  { id:"devices",          label:"Devices",              icon:<Monitor size={17}/> },
  { id:"email",            label:"Email",                icon:<Mail size={17}/> },
  { id:"incidents",        label:"Incidents & Alerts",   icon:<AlertTriangle size={17}/> },
  { id:"alertcenter",      label:"Alert Center",         icon:<Bell size={17}/> },
  { id:"compliance",       label:"Compliance",           icon:<CheckSquare size={17}/> },
  { id:"servicehealth",    label:"Service Health",       icon:<Activity size={17}/> },
  { id:"network",          label:"M365 Connectivity",    icon:<Wifi size={17}/> },
  { id:"licenses",         label:"Licenses & Users",     icon:<Package size={17}/>, group:"Enterprise" },
  { id:"conditionalaccess",label:"Conditional Access",   icon:<ShieldCheck size={17}/>, group:"Enterprise" },
  { id:"auditlog",         label:"Audit Log",            icon:<BookOpen size={17}/>, group:"Enterprise" },
  { id:"signinmap",        label:"Sign-in Locations",    icon:<MapPin size={17}/>, group:"Enterprise" },
];

function Sidebar({ page, setPage, alertCounts, collapsed, onToggleCollapse }: {
  page:NavPage; setPage:(p:NavPage)=>void; alertCounts: Record<string,number>;
  collapsed: boolean; onToggleCollapse: () => void;
}) {
  return (
    <aside className={`sidebar ${collapsed ? "" : "expanded"}`}>
      <div className="sb-logo">
        <div className="sb-mark">
          <svg width="22" height="22" viewBox="0 0 22 22" fill="none" xmlns="http://www.w3.org/2000/svg">
            <path d="M11 2L3 5.5V11C3 15.1 6.4 18.9 11 20C15.6 18.9 19 15.1 19 11V5.5L11 2Z" fill="#3b82f6" stroke="#60a5fa" strokeWidth="0.8"/>
            <path d="M5.5 11.5 L7.5 11.5 L9 9 L11 14 L13 10 L14.5 11.5 L16.5 11.5" stroke="white" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" fill="none"/>
          </svg>
        </div>
        {!collapsed && (
          <div>
            <div className="sb-name">Vigil365</div>
            <div className="sb-sub">M365 Security Operations</div>
          </div>
        )}
      </div>
      <nav className="sb-nav">
        {NAV.map((n,i)=>(
          <React.Fragment key={n.id}>
            {!collapsed && n.group && (i===0 || NAV[i-1].group!==n.group) && (
              <div className="nav-group-label">{n.group}</div>
            )}
            <button
              className={`nav-item ${page===n.id?"nav-active":""}`}
              onClick={()=>setPage(n.id)}
              aria-label={n.label}
              title={collapsed ? n.label : undefined}
            >
              {n.icon}
              {!collapsed && <span>{n.label}</span>}
              {!collapsed && (alertCounts as Record<string,number>)[n.id]>0 && (
                <span className="nav-badge">{(alertCounts as Record<string,number>)[n.id]}</span>
              )}
              {collapsed && <span className="nav-tooltip">{n.label}{(alertCounts[n.id]??0)>0?` (${alertCounts[n.id]})`:"" }</span>}
            </button>
          </React.Fragment>
        ))}
      </nav>
      <button
        className="sb-collapse-btn"
        onClick={onToggleCollapse}
        aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
      >
        {collapsed ? <ChevronRight size={14}/> : <ChevronLeft size={14}/>}
      </button>
    </aside>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// APP
// ═══════════════════════════════════════════════════════════════════════════════
function App() {
  const [page, setPage] = useState<NavPage>("overview");
  const [darkMode, setDarkMode] = useState(() => localStorage.getItem("m365-theme") === "dark");
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false); // false = expanded
  // Track the alert count at the time the user last visited each page.
  // Badge = current count − seen count (only new items show as unread).
  const [seenCounts, setSeenCounts] = useState<Record<string,number>>(() => {
    try { return JSON.parse(localStorage.getItem('m365-seen') ?? '{}'); } catch { return {}; }
  });

  // Apply dark mode to document root
  useEffect(() => {
    document.documentElement.classList.toggle("dark", darkMode);
    localStorage.setItem("m365-theme", darkMode ? "dark" : "light");
  }, [darkMode]);
  const [overview, setOverview] = useState<Overview|null>(null);
  const [secureScore, setSecureScore] = useState<SecureScore|null>(null);
  const [identity, setIdentity] = useState<IdentityData|null>(null);
  const [devices, setDevices] = useState<DevicesData|null>(null);
  const [serviceHealth, setServiceHealth] = useState<ServiceHealthData|null>(null);
  const [allAlerts, setAllAlerts] = useState<SecurityAlert[]>([]);
  const [licenses, setLicenses] = useState<LicenseData|null>(null);
  const [inactiveUsers, setInactiveUsers] = useState<InactiveUsersData|null>(null);
  const [passwordExpiry, setPasswordExpiry] = useState<PasswordExpiryData|null>(null);
  const [conditionalAccess, setConditionalAccess] = useState<ConditionalAccessData|null>(null);
  const [auditLog, setAuditLog] = useState<AuditLogData|null>(null);
  const [signInLocations, setSignInLocations] = useState<SignInLocationsData|null>(null);
  const [defenderAlerts, setDefenderAlerts] = useState<DefenderAlertsData|null>(null);
  const [privilegedRoles, setPrivilegedRoles] = useState<PrivilegedRolesData|null>(null);
  const [dlpAlerts, setDlpAlerts] = useState<DlpAlertsData|null>(null);
  const [mdeVulnerabilities, setMdeVulnerabilities] = useState<MdeVulnerabilitiesData|null>(null);
  const [pimData, setPimData] = useState<PimData|null>(null);
  const [emailProtection, setEmailProtection] = useState<EmailProtectionData|null>(null);
  const [purview, setPurview] = useState<PurviewData|null>(null);
  const [securityIncidents, setSecurityIncidents] = useState<SecurityIncidentsData|null>(null);
  const [mdiAlerts, setMdiAlerts] = useState<MdiAlertsData|null>(null);
  const [mcasAlerts, setMcasAlerts] = useState<McasAlertsData|null>(null);
  const [insiderRisk, setInsiderRisk] = useState<InsiderRiskData|null>(null);
  const [riskDetections, setRiskDetections] = useState<RiskDetectionsData|null>(null);
  const [identityHealth, setIdentityHealth] = useState<IdentityHealthData|null>(null);
  const [attackSimulation, setAttackSimulation] = useState<AttackSimulationData|null>(null);
  const [alertPolicies, setAlertPolicies] = useState<AlertPolicy[]>([]);
  const [triggeredAlerts, setTriggeredAlerts] = useState<TriggeredAlert[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [lastRefresh, setLastRefresh] = useState(new Date());
  const [running, setRunning] = useState(false);
  const [selectedAlert, setSelectedAlert] = useState<SecurityAlert|null>(null);
  const [countdown, setCountdown] = useState(AUTO_REFRESH_SEC);
  const [refreshKey, setRefreshKey] = useState(0);
  const abortRef = useRef<AbortController|null>(null);

  const load = useCallback(async () => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;

    setLoading(true); setError("");

    const safeJson = (r: Response) => r.ok ? r.json() : Promise.resolve(null);
    const sig = ctrl.signal;

    // Helper: fetch one endpoint and update state immediately on resolve
    // Each request has a 20-second timeout so slow Graph calls don't block the loading bar
    const fetchOne = <T,>(url: string, setter: (v: T) => void, transform?: (v: T) => T) => {
      const timeoutSig = AbortSignal.timeout(20_000);
      const combinedSig = (AbortSignal as { any?: (sigs: AbortSignal[]) => AbortSignal }).any
        ? (AbortSignal as { any: (sigs: AbortSignal[]) => AbortSignal }).any([sig, timeoutSig])
        : sig;
      return fetch(url, { signal: combinedSig })
        .then(safeJson)
        .then((v: T) => {
          if (!ctrl.signal.aborted && v != null)
            setter(transform ? transform(v) : v);
        })
        .catch(() => { /* individual failure or timeout — silently skip */ });
    };

    try {
      // Fire all requests simultaneously; each updates state as it resolves
      await Promise.allSettled([
        fetchOne(`${apiBase}/api/dashboard/overview`, setOverview),
        fetchOne(`${apiBase}/api/dashboard/securescore`, setSecureScore),
        fetchOne(`${apiBase}/api/dashboard/identity`, setIdentity),
        fetchOne(`${apiBase}/api/dashboard/devices`, setDevices),
        fetchOne(`${apiBase}/api/dashboard/servicehealth`, setServiceHealth),
        fetchOne<{items: SecurityAlert[]}>(`${apiBase}/api/alerts?page=1&pageSize=200&resolved=false`, v => setAllAlerts(v.items ?? [])),
        fetchOne(`${apiBase}/api/dashboard/licenses`, setLicenses),
        fetchOne(`${apiBase}/api/dashboard/inactive-users`, setInactiveUsers),
        fetchOne(`${apiBase}/api/dashboard/password-expiry`, setPasswordExpiry),
        fetchOne(`${apiBase}/api/dashboard/conditional-access`, setConditionalAccess),
        fetchOne(`${apiBase}/api/dashboard/audit-log`, setAuditLog),
        fetchOne(`${apiBase}/api/dashboard/signin-locations`, setSignInLocations),
        fetchOne(`${apiBase}/api/dashboard/defender-alerts`, setDefenderAlerts),
        fetchOne(`${apiBase}/api/dashboard/security-incidents`, setSecurityIncidents),
        fetchOne(`${apiBase}/api/dashboard/privileged-roles`, setPrivilegedRoles),
        fetchOne(`${apiBase}/api/dashboard/dlp-alerts`, setDlpAlerts),
        fetchOne(`${apiBase}/api/dashboard/mde-vulnerabilities`, setMdeVulnerabilities),
        fetchOne(`${apiBase}/api/dashboard/pim`, setPimData),
        fetchOne(`${apiBase}/api/dashboard/email-protection`, setEmailProtection),
        fetchOne(`${apiBase}/api/dashboard/purview`, setPurview),
        fetchOne(`${apiBase}/api/dashboard/mdi-alerts`, setMdiAlerts),
        fetchOne(`${apiBase}/api/dashboard/mcas-alerts`, setMcasAlerts),
        fetchOne(`${apiBase}/api/dashboard/insider-risk`, setInsiderRisk),
        fetchOne(`${apiBase}/api/dashboard/risk-detections`, setRiskDetections),
        fetchOne(`${apiBase}/api/dashboard/identity-health`, setIdentityHealth),
        fetchOne(`${apiBase}/api/dashboard/attack-simulation`, setAttackSimulation),
      ]);
      if (ctrl.signal.aborted) return;
      setLastRefresh(new Date());
      setCountdown(AUTO_REFRESH_SEC);
    } catch(e: unknown) {
      if (e instanceof Error && e.name !== "AbortError")
        setError("Failed to load dashboard data. Is the API running?");
    } finally {
      if (!ctrl.signal.aborted) setLoading(false);
    }
  }, []);

  const runCollection = useCallback(async () => {
    setRunning(true);
    try {
      const res = await fetch(`${apiBase}/api/collector/run`, { method: "POST" });
      if (!res.ok) throw new Error(await res.text());
      setRefreshKey(k => k + 1);
    } catch(e: unknown) {
      let msg = e instanceof Error ? e.message : "Collection failed";
      try { const p = JSON.parse(msg); if (p?.error) msg = p.error; } catch { /* not JSON */ }
      setError(msg);
    } finally { setRunning(false); }
  }, []);

  // Initial load + re-fetch whenever refreshKey increments
  useEffect(() => { load(); return () => abortRef.current?.abort(); }, [load, refreshKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // Pull alert policies + triggered alerts from the backend
  const refreshAlertCenter = useCallback(async () => {
    const [pol, trig] = await Promise.all([acApi.getPolicies(), acApi.getTriggered()]);
    setAlertPolicies(pol);
    setTriggeredAlerts(trig);
  }, []);

  // After each data load, ask the backend to evaluate policies, then refresh.
  // The backend also evaluates on its own 15-min cycle (works with no browser open).
  useEffect(() => {
    if (loading) return;
    let cancelled = false;
    (async () => {
      await acApi.evaluate();
      if (!cancelled) await refreshAlertCenter();
    })();
    return () => { cancelled = true; };
  }, [loading, refreshKey, refreshAlertCenter]);

  // 15-minute auto-refresh + countdown ticker
  useEffect(() => {
    const ticker = setInterval(() => {
      setCountdown(prev => {
        if (prev <= 1) { setRefreshKey(k => k + 1); return AUTO_REFRESH_SEC; }
        return prev - 1;
      });
    }, 1000);
    return () => clearInterval(ticker);
  }, []);

  const newTriggeredCount = useMemo(() => triggeredAlerts.filter(a => a.status === "new").length, [triggeredAlerts]);

  const alertCounts = useMemo(() => ({
    identity: allAlerts.filter(a=>a.service==="EntraId").length + (mdiAlerts?.total??0) + (riskDetections?.total??0) + (identityHealth?.total??0),
    devices: allAlerts.filter(a=>a.service==="Intune").length,
    email: allAlerts.filter(a=>a.service==="ExchangeOnline").length,
    incidents: allAlerts.length + (serviceHealth?.total??0) + (defenderAlerts?.total??0) + (securityIncidents?.total??0),
    alertcenter: newTriggeredCount,
    servicehealth: serviceHealth?.total??0,
    licenses: (inactiveUsers?.inactive90Count??0)+(passwordExpiry?.expiringSoonCount??0),
    conditionalaccess: conditionalAccess?.disabled??0,
    auditlog: auditLog?.failures??0,
    signinmap: signInLocations?.failures??0,
    compliance: (mcasAlerts?.total??0) + (insiderRisk?.total??0),
    overview:0, network:0,
  }), [allAlerts, serviceHealth, defenderAlerts, securityIncidents, inactiveUsers, passwordExpiry, conditionalAccess, auditLog, signInLocations, mdiAlerts, riskDetections, identityHealth, mcasAlerts, insiderRisk, newTriggeredCount]);

  // When you're on a page (or navigate to one), mark that page's current count as "seen".
  // While viewing a page the badge is 0; after you leave, any new items increment the badge.
  useEffect(() => {
    const current = (alertCounts as Record<string,number>)[page] ?? 0;
    setSeenCounts(prev => {
      if (prev[page] === current) return prev;
      const updated = { ...prev, [page]: current };
      try { localStorage.setItem('m365-seen', JSON.stringify(updated)); } catch {}
      return updated;
    });
  }, [page, alertCounts]); // eslint-disable-line react-hooks/exhaustive-deps

  // Badge shows only the increase since last visit
  const unreadCounts = useMemo(() => {
    const r: Record<string,number> = {};
    for (const [k, v] of Object.entries(alertCounts))
      r[k] = Math.max(0, v - (seenCounts[k] ?? 0));
    return r;
  }, [alertCounts, seenCounts]);

  const currentNav = NAV.find(n=>n.id===page);

  // System status banner: show if any M365 services are degraded
  const systemDegraded = (serviceHealth?.total ?? 0) > 0;

  return (
    <div className={`app-shell${darkMode ? " dark" : ""}`}>
      <Sidebar page={page} setPage={setPage} alertCounts={unreadCounts}
        collapsed={sidebarCollapsed} onToggleCollapse={() => setSidebarCollapsed(c => !c)}/>
      <div className="main-area">
        <header className="main-hdr">
          <div>
            <h1 className="hdr-title">{currentNav?.label??"Overview"}</h1>
            <p className="hdr-sub">
              Vigil365 · M365 Security Operations · Updated {lastRefresh.toLocaleTimeString()}
              {" · "}<span className="countdown-chip"><Clock size={10}/>Next refresh {fmtCountdown(countdown)}</span>
            </p>
          </div>
          <div className="hdr-actions">
            {overview?.lastRun&&(
              <Badge label={`Last run: ${fmtDate(overview.lastRun.completedAt??overview.lastRun.startedAt)}`} tone="neutral"/>
            )}
            <button className={`btn-run${(!overview&&!running&&!loading)?" btn-run-pulse":""}`} onClick={runCollection} disabled={running||loading} title="Run Graph collection now">
              <RefreshCw size={13} className={running?"spin":""}/>
              {running?"Collecting…":"Run Collection"}
            </button>
            <button className="btn-icon" onClick={() => setRefreshKey(k => k + 1)} title="Refresh data" disabled={loading||running} aria-label="Refresh data">
              <RefreshCw size={15} className={loading?"spin":""}/>
            </button>
            <button className="theme-toggle" onClick={() => setDarkMode(d => !d)} aria-label={darkMode ? "Switch to light mode" : "Switch to dark mode"} title={darkMode ? "Light mode" : "Dark mode"}>
              {darkMode ? <Sun size={15}/> : <Moon size={15}/>}
            </button>
          </div>
        </header>
        {loading&&<div className="loading-bar"><div className="loading-bar-fill"/></div>}
        {error&&<div className="err-banner">{error} <button style={{marginLeft:8,textDecoration:"underline",background:"none",border:"none",color:"inherit",cursor:"pointer"}} onClick={()=>setError("")}>Dismiss</button></div>}
        {systemDegraded && (
          <div style={{padding:"0 24px 0",marginTop:8}}>
            <div className="sys-status-banner status-degraded">
              <AlertTriangle size={15}/> {serviceHealth!.total} M365 service{serviceHealth!.total>1?"s":""} currently have active advisories — check Service Health for details.
            </div>
          </div>
        )}
        {page==="overview"&&<OverviewPage overview={overview} secureScore={secureScore} identity={identity} devices={devices} serviceHealth={serviceHealth} alerts={allAlerts} defenderAlerts={defenderAlerts} securityIncidents={securityIncidents} onAlertClick={setSelectedAlert} onNavigateAlertCenter={()=>setPage("alertcenter")} alertPolicies={alertPolicies} overviewTriggered={triggeredAlerts} healthRefreshKey={refreshKey}/>}
        {page==="identity"&&<IdentityPage identity={identity} alerts={allAlerts} privilegedRoles={privilegedRoles} pimData={pimData} mdiAlerts={mdiAlerts} riskDetections={riskDetections} identityHealth={identityHealth} onAlertClick={setSelectedAlert}/>}
        {page==="devices"&&<DevicesPage devices={devices} alerts={allAlerts} mdeVulnerabilities={mdeVulnerabilities} onAlertClick={setSelectedAlert}/>}
        {page==="email"&&<EmailPage alerts={allAlerts} emailProtection={emailProtection} onAlertClick={setSelectedAlert}/>}
        {page==="incidents"&&<IncidentsPage alerts={allAlerts} serviceHealth={serviceHealth} defenderAlerts={defenderAlerts} securityIncidents={securityIncidents} onAlertClick={setSelectedAlert}/>}
        {page==="alertcenter"&&<AlertCenterPage policies={alertPolicies} triggeredAlerts={triggeredAlerts} onChanged={refreshAlertCenter}/>}
        {page==="compliance"&&<CompliancePage secureScore={secureScore} overview={overview} dlpAlerts={dlpAlerts} purview={purview} mcasAlerts={mcasAlerts} insiderRisk={insiderRisk} attackSimulation={attackSimulation}/>}
        {page==="servicehealth"&&<ServiceHealthPage serviceHealth={serviceHealth}/>}
        {page==="network"&&<NetworkPage serviceHealth={serviceHealth} signInLocations={signInLocations}/>}
        {page==="licenses"&&<LicensesPage licenses={licenses} inactive={inactiveUsers} passwords={passwordExpiry}/>}
        {page==="conditionalaccess"&&<ConditionalAccessPage data={conditionalAccess}/>}
        {page==="auditlog"&&<AuditLogPage data={auditLog}/>}
        {page==="signinmap"&&<SignInLocationsPage data={signInLocations}/>}
      </div>
      {selectedAlert&&<AlertDetailModal alert={selectedAlert} onClose={()=>setSelectedAlert(null)}/>}
      <ToastContainer/>
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<App/>);
