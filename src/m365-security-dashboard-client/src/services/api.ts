import { PublicClientApplication } from "@azure/msal-browser";
import { createContext, useContext } from "react";
import { AlertPolicy, TriggeredAlert, NotificationSettings, NotificationLogEntry, AuthInfo } from "./types";

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

export async function apiFetch(url: string, init?: RequestInit): Promise<Response> {
  const token = await getAccessToken();
  const headers = new Headers(init?.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  return fetch(url, { ...init, headers });
}

export const AuthContext = createContext<AuthInfo>({
  email: "", name: "", role: "Viewer", isAdmin: false, canMutate: false,
});

export function useAuth(): AuthInfo {
  return useContext(AuthContext);
}

export const acApi = {
  async getPolicies(): Promise<AlertPolicy[]> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async createPolicy(p: Partial<AlertPolicy>): Promise<AlertPolicy | null> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p) }); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async updatePolicy(p: AlertPolicy): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies/${p.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p) }); return r.ok; } catch { return false; }
  },
  async deletePolicy(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies/${id}`, { method: "DELETE" }); return r.ok; } catch { return false; }
  },
  async getTriggered(): Promise<TriggeredAlert[]> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async acknowledge(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/acknowledge`, { method: "POST" }); return r.ok; } catch { return false; }
  },
  async resolve(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/resolve`, { method: "POST" }); return r.ok; } catch { return false; }
  },
  async evaluate(): Promise<number> {
    try { const r = await apiFetch(`${apiBase}/api/alert-policies/evaluate`, { method: "POST" }); return r.ok ? (await r.json()).fired ?? 0 : 0; } catch { return 0; }
  },
  async getSettings(): Promise<NotificationSettings | null> {
    try { const r = await apiFetch(`${apiBase}/api/notification-settings`); return r.ok ? await r.json() : null; } catch { return null; }
  },
  async saveSettings(s: NotificationSettings): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/notification-settings`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(s) }); return r.ok; } catch { return false; }
  },
  async testNotifications(): Promise<{ ok: boolean; results?: { channel: string; success: boolean; error?: string }[] }> {
    try { const r = await apiFetch(`${apiBase}/api/notification-settings/test`, { method: "POST" }); return r.ok ? await r.json() : { ok: false }; } catch { return { ok: false }; }
  },
  async getLog(): Promise<NotificationLogEntry[]> {
    try { const r = await apiFetch(`${apiBase}/api/notification-log`); return r.ok ? await r.json() : []; } catch { return []; }
  },
  async snooze(id: string, durationHours: 4 | 24 | 168): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/snooze`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ durationHours }) }); return r.ok; } catch { return false; }
  },
  async unsnooze(id: string): Promise<boolean> {
    try { const r = await apiFetch(`${apiBase}/api/triggered-alerts/${id}/unsnooze`, { method: "POST" }); return r.ok; } catch { return false; }
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

// ─── In-app cross-navigation ────────────────────────────────────────────────────
// Lets one page deep-link into another with a search/filter seed (e.g. Alert Center
// "view user in Identity"). App registers the page-setter; pages read & consume the
// pending seed on mount.
export type CrossNavTarget = { page: string; search?: string };
let _navHandler: ((target: CrossNavTarget) => void) | null = null;
let _pendingSeed: Record<string, string> = {};

export function registerNavHandler(handler: (target: CrossNavTarget) => void): () => void {
  _navHandler = handler;
  return () => { if (_navHandler === handler) _navHandler = null; };
}

export function crossNavigate(target: CrossNavTarget): void {
  if (target.search != null) _pendingSeed[target.page] = target.search;
  _navHandler?.(target);
  if (typeof window !== "undefined") {
    window.dispatchEvent(new CustomEvent("nav-seed-update", { detail: target }));
  }
}

// A page calls this on mount to pick up (and clear) any seed left for it.
export function consumeNavSeed(page: string): string | null {
  const v = _pendingSeed[page];
  if (v == null) return null;
  delete _pendingSeed[page];
  return v;
}

export const SEVERITIES = ["Critical", "High", "Medium", "Low", "Informational"];
export const SERVICES = ["EntraId", "Intune", "DefenderXdr", "ExchangeOnline", "ServiceHealth"];
export const AUTO_REFRESH_SEC = 15 * 60; // 15 minutes
