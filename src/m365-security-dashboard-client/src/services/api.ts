import { PublicClientApplication } from "@azure/msal-browser";
import { createContext, useContext } from "react";
import { AlertPolicy, TriggeredAlert, NotificationSettings, NotificationLogEntry, AuthInfo, SuppressionRule } from "./types";

export const apiBase = import.meta.env.VITE_API_BASE ?? "";

let _msalInstance: PublicClientApplication | null = null;
let _msalScopes: string[] = [];

export function initMsal(instance: PublicClientApplication, scopes: string[]) {
  _msalInstance = instance;
  _msalScopes = scopes;
}

export async function getAccessToken(): Promise<string | null> {
  if (!_msalInstance) return null;
  const account = _msalInstance.getActiveAccount() ?? _msalInstance.getAllAccounts()[0];
  if (!account) return null;
  try {
    const result = await _msalInstance.acquireTokenSilent({ scopes: _msalScopes, account });
    return result.accessToken;
  } catch (e) {
    console.warn("acquireTokenSilent failed:", e);
    return null;
  }
}

// ─── Client tenant selection (MSP) ─────────────────────────────────────────────
// Which client tenant every API call is scoped to. Sent as X-Vigil-Tenant; the
// server refuses (403) a client the user may not see, and with no header falls
// back to the install's sole tenant, so a single-tenant install never needs this set.
const TENANT_KEY = "vigil365-tenant";

// Edition mode from /api/auth/config. Outside MSP mode there is no client to
// select, so the selection reads as empty everywhere: no X-Vigil-Tenant header,
// and every MSP-only control that keys off a selected client stays hidden.
let _mspMode = false;
export function setEditionMode(mode?: string | null): void { _mspMode = mode === "Msp"; }
export function isMspMode(): boolean { return _mspMode; }

// Name of the client the whole UI is scoped to (MSP mode), set by ClientGate.
// Shown in the header, prefixed to toasts and export filenames, so nobody acts
// on — or sends — the wrong client's data.
let _activeClientName: string | null = null;
export function setActiveClientName(name: string | null): void { _activeClientName = name; }
export function getActiveClientName(): string | null { return _mspMode ? _activeClientName : null; }
export function clientFileName(filename: string): string {
  const client = getActiveClientName();
  if (!client) return filename;
  const slug = client.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  return slug ? `${slug}-${filename}` : filename;
}

export function getSelectedTenantId(): string | null {
  if (!_mspMode) return null;
  try { return localStorage.getItem(TENANT_KEY); } catch { return null; }
}

export function setSelectedTenantId(id: string | null): void {
  try { if (id) localStorage.setItem(TENANT_KEY, id); else localStorage.removeItem(TENANT_KEY); } catch { /* storage blocked */ }
}

// Calls that say who the user is and which clients they may pick are never
// scoped. A stored selection goes stale when its client is deactivated or
// purged, or the user's access to it is revoked, and the server answers any
// call naming it with 403. Scoping these too would sign an Admin in as a Viewer
// and hide the client list ClientGate needs to drop the stale choice.
const UNSCOPED_PATHS = ["/api/auth/me", "/api/tenants/me"];

export async function apiFetch(url: string, init?: RequestInit): Promise<Response> {
  const token = await getAccessToken();
  const headers = new Headers(init?.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  // An explicit header wins: the cross-client queue acts on an alert in ITS
  // client, which may not be the one selected (the server checks permission).
  const tenant = getSelectedTenantId();
  const path = url.split("?")[0];
  if (tenant && !headers.has("X-Vigil-Tenant") && !UNSCOPED_PATHS.some(p => path.endsWith(p))) headers.set("X-Vigil-Tenant", tenant);
  return fetch(url, { ...init, headers });
}

export const AuthContext = createContext<AuthInfo>({
  email: "", name: "", role: "Viewer", isAdmin: false, canMutate: false,
});

export function useAuth(): AuthInfo {
  return useContext(AuthContext);
}

// Standing suppression rules. Reads are Analyst; mutations are Admin-only
// server-side — the UI hides the controls, the API enforces it.
export const suppressionApi = {
  /** Throws on failure: an unreadable list is not "no rules", while rules may be hiding alerts. */
  async list(): Promise<SuppressionRule[]> {
    const r = await apiFetch(`${apiBase}/api/suppression-rules`);
    if (!r.ok) throw new Error(`Suppression rules request failed (${r.status})`);
    return await r.json();
  },
  async create(rule: Partial<SuppressionRule>): Promise<{ ok: boolean; error?: string }> {
    try {
      const r = await apiFetch(`${apiBase}/api/suppression-rules`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(rule),
      });
      if (r.ok) return { ok: true };
      const body = await r.json().catch(() => ({}));
      return { ok: false, error: body.error ?? "Could not create the suppression rule." };
    } catch { return { ok: false, error: "Could not reach the API." }; }
  },
  async update(id: string, rule: Partial<SuppressionRule>): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/suppression-rules/${id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(rule) }); return r.ok; } catch { return false; }
  },
  async remove(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/suppression-rules/${id}`, { method: "DELETE" }); return r.ok; } catch { return false; }
  },
};

/** A refused write carries the server's own reason ({error} or {message}) when
 *  it gave one (e.g. the 403 for an install-wide policy), so the UI can say why. */
type WriteResult = { ok: boolean; error?: string };
async function writeResult(request: Promise<Response>): Promise<WriteResult> {
  try {
    const r = await request;
    if (r.ok) return { ok: true };
    const body = await r.json().catch(() => ({})) as { error?: string; message?: string };
    return { ok: false, error: body.error ?? body.message };
  } catch { return { ok: false, error: "Could not reach the API." }; }
}

/** Acknowledge/resolve: a 409 means the alert is already resolved — closed, not still open. */
export type AlertActionResult = WriteResult & { alreadyResolved?: boolean };
async function alertActionResult(request: Promise<Response>): Promise<AlertActionResult> {
  try {
    const r = await request;
    if (r.ok) return { ok: true };
    const body = await r.json().catch(() => ({})) as { error?: string; message?: string };
    return { ok: false, alreadyResolved: r.status === 409, error: body.error ?? body.message };
  } catch { return { ok: false, error: "Could not reach the API." }; }
}

export const acApi = {
  /** Throws on failure: the caller keeps what is on screen rather than showing an empty list. */
  async getPolicies(): Promise<AlertPolicy[]> {
    const r = await apiFetch(`${apiBase}/api/alert-policies`);
    if (!r.ok) throw new Error(`Alert policies request failed (${r.status})`);
    return await r.json();
  },
  /** scope "tenant" (MSP) makes the policy belong to the currently selected client only. */
  async createPolicy(p: Partial<AlertPolicy>, scope: "default" | "tenant" = "default"): Promise<WriteResult> {
    return writeResult(apiFetch(`${apiBase}/api/alert-policies${scope === "tenant" ? "?scope=tenant" : ""}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p) }));
  },
  async getTenantOverrides(): Promise<{ policyId: string; enabled: boolean | null; threshold: number | null; notifyEmail: string | null }[]> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies/tenant-overrides`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async setTenantOverride(policyId: string, o: { enabled?: boolean | null; threshold?: number | null; notifyEmail?: string | null }): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies/${policyId}/tenant-override`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(o) }); return r.ok; } catch { return false; }
  },
  async updatePolicy(p: AlertPolicy): Promise<WriteResult> {
    return writeResult(apiFetch(`${apiBase}/api/alert-policies/${p.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p) }));
  },
  async deletePolicy(id: string): Promise<WriteResult> {
    return writeResult(apiFetch(`${apiBase}/api/alert-policies/${id}`, { method: "DELETE" }));
  },
  /** Throws on failure, like getPolicies: a failed read is not an empty queue. */
  async getTriggered(): Promise<TriggeredAlert[]> {
    const r = await apiFetch(`${apiBase}/api/triggered-alerts`);
    if (!r.ok) throw new Error(`Triggered alerts request failed (${r.status})`);
    return await r.json();
  },
  /** 409 = the alert was resolved meanwhile (by someone else, or automatically): alreadyResolved, with the server's reason. */
  async acknowledge(id: string): Promise<AlertActionResult> {
    return alertActionResult(apiFetch(`${apiBase}/api/triggered-alerts/${id}/acknowledge`, { method: "POST" }));
  },
  async resolve(id: string): Promise<AlertActionResult> {
    return alertActionResult(apiFetch(`${apiBase}/api/triggered-alerts/${id}/resolve`, { method: "POST" }));
  },
  async evaluate(): Promise<number> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies/evaluate`, { method: "POST" }); return r.ok ? (await r.json()).fired ?? 0 : 0; } catch { return 0; }
  },
  // Throws on failure — callers must show an error state. Falling back to empty
  // settings drew a form with every channel off, and saving it wiped the real ones.
  async getSettings(): Promise<NotificationSettings> {
    const r = await apiFetch(`${apiBase}/api/notification-settings`);
    if (!r.ok) throw new Error(`Notification settings request failed (${r.status})`);
    return await r.json();
  },
  async saveSettings(s: NotificationSettings): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/notification-settings`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(s) }); return r.ok; } catch { return false; }
  },
  async testNotifications(): Promise<{ ok: boolean; message?: string; results?: { channel: string; success: boolean; error?: string }[] }> {
    try { const r = await apiFetch(`${apiBase}/api/notification-settings/test`, { method: "POST" }); return r.ok ? await r.json() : { ok: false, message: `Test request failed (${r.status})` }; } catch { return { ok: false, message: "Could not reach the API." }; }
  },
  /** Throws on failure, like getSettings: an unreadable log is not "nothing sent". */
  async getLog(): Promise<NotificationLogEntry[]> {
    const r = await apiFetch(`${apiBase}/api/notification-log`);
    if (!r.ok) throw new Error(`Notification history request failed (${r.status})`);
    return await r.json();
  },
  async getHealth(): Promise<import("./types").NotificationHealth | null> {
    try { const r = await apiFetch(`${apiBase}/api/notification-health`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async snooze(id: string, durationHours: 4 | 24 | 168): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/snooze`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ durationHours }) }); return r.ok; } catch { return false; }
  },
  async unsnooze(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/unsnooze`, { method: "POST" }); return r.ok; } catch { return false; }
  },
  async assign(id: string, assignedTo: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/assign`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ assignedTo }) }); return r.ok; } catch { return false; }
  },
  /** Undo for acknowledge/resolve — returns the alert to "new". */
  async reopen(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/reopen`, { method: "POST" }); return r.ok; } catch { return false; }
  },
};

// ─── Alert workbench: local triage state + analyst notes ──────────────────────
export const wbApi = {
  /** Assign / set disposition on a collected M365 security alert. */
  async workbench(alertId: number, body: { assignedTo?: string; disposition?: string }): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/alerts/${alertId}/workbench`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }); return r.ok; } catch { return false; }
  },
  async listNotes(kind: "security" | "policy", targetId: string): Promise<import("./types").AlertNote[]> {
    try { const r = await apiFetch(`${apiBase}/api/alert-notes/${kind}/${targetId}`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async addNote(kind: "security" | "policy", targetId: string, text: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/alert-notes/${kind}/${targetId}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ text }) }); return r.ok; } catch { return false; }
  },
};

export const recApi = {
  // Throws on failure — callers must show an error state. Swallowing errors here
  // made a dead backend indistinguishable from "no recommendations, all healthy".
  async getRecommendations(): Promise<import("./types").SecurityRecommendation[]> {
    const r = await apiFetch(`${apiBase}/api/recommendations`);
    if (!r.ok) throw new Error(`Recommendations request failed (${r.status})`);
    return await r.json();
  },
  async getAlertCoverage(): Promise<import("./types").AlertCoverageScorecard | null> {
    try { const r = await apiFetch(`${apiBase}/api/alert-coverage`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async enableCoverageRule(id: string): Promise<import("./types").AlertCoverageScorecard | null> {
    try { const r = await apiFetch(`${apiBase}/api/alert-coverage/enable/${id}`, { method: "POST" }); return r.ok ? await r.json() : null; } catch { return null; }
  },
};

// ─── Scheduled reports (executive digest) ─────────────────────────────────────
export const reportApi = {
  /** Throws on failure: an unreadable list is not "no schedules". */
  async list(): Promise<import("./types").ReportSchedule[]> {
    const r = await apiFetch(`${apiBase}/api/report-schedules`);
    if (!r.ok) throw new Error(`Report schedules request failed (${r.status})`);
    return await r.json();
  },
  async preview(windowDays = 7): Promise<import("./types").DigestPreview | null> {
    try { const r = await apiFetch(`${apiBase}/api/reports/exec-digest/preview?windowDays=${windowDays}`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  /** MSP mode answers 400 {error} with no client selected; the reason is passed on. */
  async create(s: Partial<import("./types").ReportSchedule>): Promise<WriteResult> {
    return writeResult(apiFetch(`${apiBase}/api/report-schedules`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(s) }));
  },
  async update(s: import("./types").ReportSchedule): Promise<WriteResult> {
    return writeResult(apiFetch(`${apiBase}/api/report-schedules/${s.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(s) }));
  },
  async remove(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/report-schedules/${id}`, { method: "DELETE" }); return r.ok; } catch { return false; }
  },
  /** A refusal carries the server's status too (e.g. 400 for a schedule with no client in MSP mode). */
  async runNow(id: string): Promise<{ ok: boolean; status?: string }> {
    try {
      const r = await apiFetch(`${apiBase}/api/report-schedules/${id}/run-now`, { method: "POST" });
      if (r.ok) return await r.json();
      const body = await r.json().catch(() => ({})) as { status?: string; error?: string };
      return { ok: false, status: body.status ?? body.error ?? `request refused (${r.status})` };
    } catch { return { ok: false, status: "could not reach the API" }; }
  },
};

// ─── API tokens for SIEM/read-only machine integrations ──────────────────────
export const apiTokenApi = {
  /** Throws on failure: an unreadable list is not "no tokens" — an Admin may be looking for one to revoke. */
  async list(): Promise<import("./types").ApiTokenInfo[]> {
    const r = await apiFetch(`${apiBase}/api/api-tokens`);
    if (!r.ok) throw new Error(`API tokens request failed (${r.status})`);
    return await r.json();
  },
  async create(input: { name: string; scopes: string; expiresAt?: string | null; tenantId?: string | null }): Promise<import("./types").ApiTokenCreated | null> {
    try {
      const r = await apiFetch(`${apiBase}/api/api-tokens`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input),
      });
      return r.ok ? await r.json() : null;
    } catch { return null; }
  },
  async revoke(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/api-tokens/${id}/revoke`, { method: "POST" }); return r.ok; } catch { return false; }
  },
};

// ─── Conditional Access gap analysis ──────────────────────────────────────────
export const caApi = {
  async getGaps(): Promise<import("./types").CaGapAnalysis | null> {
    try { const r = await apiFetch(`${apiBase}/api/dashboard/ca-gaps`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async getSharingPosture(): Promise<import("./types").SharingPosture | null> {
    try { const r = await apiFetch(`${apiBase}/api/dashboard/sharing-posture`); return r.ok ? await r.json() : null; } catch { return null; }
  },
};

// ─── Entity investigation profile (drill-down) ────────────────────────────────
export const entityApi = {
  async getProfile(kind: "user" | "device", id: string): Promise<import("./types").EntityProfile | null> {
    try {
      const r = await apiFetch(`${apiBase}/api/entity/${kind}/${encodeURIComponent(id)}`);
      return r.ok ? await r.json() : null;
    } catch { return null; }
  },
};

// ─── Tenant baseline & drift ─────────────────────────────────────────────────
export type BaselineDriftRow = { metric: string; baseline: string; current: string; drift: string; tone: "good" | "neutral" | "warn" };
export type BaselineResponse = { captured: { at: string; by: string } | null; canCapture: boolean; driftedCount?: number; drift: BaselineDriftRow[] };
export const baselineApi = {
  async get(): Promise<BaselineResponse | null> {
    try { const r = await apiFetch(`${apiBase}/api/baseline`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  /** Capture the newest snapshot as the baseline (admin-only server-side). */
  async capture(): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/baseline/capture`, { method: "POST" }); return r.ok; } catch { return false; }
  },
};

// ─── System / operational metrics (real, measured) ──────────────────────────
export type PromRow = { metric: string; value: string; meaning: string };
export type SystemMetrics = {
  collectorUptimePct: number; runsWindow: number;
  graphCallsLastRun: number | null; graphCallsTotal: number; graphThrottledTotal: number;
  evalP95Ms: number | null; evalLastMs: number | null; evalSamples: number;
  dbSizeBytes: number | null; activeAlerts: number; policiesEnabled: number;
  retentionDays: number; lastRunDurationMs: number | null;
  throttleTrend: number[]; prometheus: PromRow[];
};
export const metricsApi = {
  async get(): Promise<SystemMetrics | null> {
    try { const r = await apiFetch(`${apiBase}/api/metrics`); return r.ok ? await r.json() : null; } catch { return null; }
  },
};

// ─── In-app cross-navigation ────────────────────────────────────────────────────
// Lets one page deep-link into another with a search/filter seed (e.g. Alert Center
// "view user in Identity"). App registers the page-setter; pages read & consume the
// pending seed on mount.
export type CrossNavTarget = { page: string; search?: string; tab?: string };
let _navHandler: ((target: CrossNavTarget) => void) | null = null;
let _pendingSeed: Record<string, string> = {};
// Several pages host their own inner tab bar. Without this, cross-navigation
// could only reach a page's default tab, so "take me to the collection runs"
// dumped the user on the page and left them to find the tab themselves.
let _pendingTab: Record<string, string> = {};

export function registerNavHandler(handler: (target: CrossNavTarget) => void): () => void {
  _navHandler = handler;
  return () => { if (_navHandler === handler) _navHandler = null; };
}

export function crossNavigate(target: CrossNavTarget): void {
  if (target.search != null) _pendingSeed[target.page] = target.search;
  if (target.tab != null) _pendingTab[target.page] = target.tab;
  _navHandler?.(target);
  if (typeof window !== "undefined") {
    window.dispatchEvent(new CustomEvent("nav-seed-update", { detail: target }));
  }
}

// Navigate to the entity investigation drill-down. Sets the hash; App's
// hashchange listener renders the EntityPage overlay.
export function openEntity(kind: "user" | "device", id: string, fromAlertId?: number): void {
  if (typeof window !== "undefined" && id) {
    let hash = `#/entity/${kind}/${encodeURIComponent(id)}`;
    if (fromAlertId) hash += `?fromAlert=${fromAlertId}`;
    window.location.hash = hash;
  }
}

// A page calls this on mount to pick up (and clear) any seed left for it.
export function consumeNavSeed(page: string): string | null {
  const v = _pendingSeed[page];
  if (v == null) return null;
  delete _pendingSeed[page];
  return v;
}

// Global data refresh. An error state that cannot be retried leaves the user
// with only F5 — App owns the fetch loop, so it registers the trigger here and
// any card deep in the tree can offer "Retry".
let _refreshHandler: (() => void) | null = null;

export function registerRefreshHandler(handler: () => void): () => void {
  _refreshHandler = handler;
  return () => { if (_refreshHandler === handler) _refreshHandler = null; };
}

/** Re-runs the dashboard data load. No-op if App has not mounted yet. */
export function requestRefresh(): void { _refreshHandler?.(); }

/** Same contract as consumeNavSeed, for pages with an inner tab bar. */
export function consumeNavTab(page: string): string | null {
  const v = _pendingTab[page];
  if (v == null) return null;
  delete _pendingTab[page];
  return v;
}

export const SEVERITIES = ["Critical", "High", "Medium", "Low", "Informational"];
export const SERVICES = ["EntraId", "Intune", "DefenderXdr", "ExchangeOnline", "ServiceHealth", "SharePoint"];
export const AUTO_REFRESH_SEC = 15 * 60; // 15 minutes
