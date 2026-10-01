import { apiBase, apiFetch } from "./api";

/** One monitored client tenant as the API describes it. */
export interface ClientTenant {
  id: string;
  name: string;
  microsoftTenantId: string | null;
  isActive: boolean;
  notes: string | null;
  createdAt: string;
  hasOwnCredentials: boolean;
  clientId: string | null;
  authMode: "secret" | "certificate" | null;
  certificateThumbprint: string | null;
  brandName: string | null;
  brandAccentColor: string | null;
  consecutiveFailures: number;
  nextCollectionAfter: string | null;
  credentialSource: "tenant" | "install" | "none";
  configured: boolean;
  consentGrantedAt: string | null;
  lastCollectionAt: string | null;
  lastCollectionStatus: string | null;
  lastError: string | null;
  openAlerts: number | null;
}

/** The tenants the signed-in user may work in, plus the one currently selected. */
export interface MyTenants {
  current: string | null;
  tenants: Pick<ClientTenant, "id" | "name" | "isActive" | "configured" | "lastCollectionStatus">[];
}

/** One row of the cross-tenant rollup: the MSP's morning triage view. */
export interface TenantRollupRow {
  id: string;
  name: string;
  configured: boolean;
  lastCollectionAt: string | null;
  lastCollectionStatus: string | null;
  lastError: string | null;
  open: { critical: number; high: number; medium: number; low: number; total: number };
  unresolvedAlerts: { critical: number; high: number };
  health: "good" | "warning" | "error" | "neutral";
}

type Result<T = void> = { ok: true; value: T } | { ok: false; error: string };

async function call<T = unknown>(url: string, init?: RequestInit): Promise<Result<T>> {
  try {
    const r = await apiFetch(`${apiBase}${url}`, init);
    const body = await r.json().catch(() => ({}));
    if (r.ok) return { ok: true, value: body as T };
    return { ok: false, error: (body as { message?: string; error?: string }).message ?? (body as { error?: string }).error ?? `Request failed (${r.status})` };
  } catch {
    return { ok: false, error: "Could not reach the API." };
  }
}

const json = (method: string, body: unknown): RequestInit =>
  ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

export const tenantApi = {
  me: () => call<MyTenants>("/api/tenants/me"),
  list: () => call<ClientTenant[]>("/api/tenants"),
  get: (id: string) => call<ClientTenant>(`/api/tenants/${id}`),
  rollup: () => call<TenantRollupRow[]>("/api/tenants/rollup"),
  create: (t: { name: string; microsoftTenantId?: string | null; notes?: string | null; brandName?: string | null; brandAccentColor?: string | null }) =>
    call<{ id: string }>("/api/tenants", json("POST", t)),
  update: (id: string, t: { name: string; microsoftTenantId?: string | null; notes?: string | null; isActive?: boolean; brandName?: string | null; brandAccentColor?: string | null }) =>
    call(`/api/tenants/${id}`, json("PUT", t)),
  setCredentials: (id: string, c: { clientId: string; clientSecret?: string; loginInstance?: string | null; baseUrl?: string | null; certificateThumbprint?: string | null; certificatePath?: string | null; certificatePassword?: string | null }) =>
    call(`/api/tenants/${id}/credentials`, json("PUT", c)),
  clearCredentials: (id: string) => call(`/api/tenants/${id}/credentials`, { method: "DELETE" }),
  // redirectUri omitted → the server uses its own /consented landing page (the popup flow).
  consentUrl: (id: string, redirectUri?: string) =>
    call<{ url: string }>(`/api/tenants/${id}/consent-url${redirectUri ? `?redirectUri=${encodeURIComponent(redirectUri)}` : ""}`),
  test: (id: string) => call<{ microsoftTenantId: string | null; displayName: string | null }>(`/api/tenants/${id}/test`, { method: "POST" }),
  remove: (id: string, purge: boolean) => call(`/api/tenants/${id}${purge ? "?purge=true" : ""}`, { method: "DELETE" }),
  assignments: () => call<Record<string, string[]>>("/api/tenants/assignments"),
  setAssignments: (email: string, tenantIds: string[]) =>
    call(`/api/tenants/assignments/${encodeURIComponent(email)}`, json("PUT", { tenantIds })),
};

/** Per-client routing: where this client's alerts go, layered over the MSP's settings. */
export interface TenantRouting {
  exists: boolean;
  notifyMsp: boolean;
  notifyClient: boolean;
  recipientEmail: string | null;
  teamsWebhookUrl: string | null;
  hasWebhookUrl: boolean;
  minSeverity: string | null;
  lastDigestAt: string | null;
}

export const routingApi = {
  get: () => call<TenantRouting>("/api/notification-routing"),
  save: (r: { notifyMsp: boolean; notifyClient: boolean; recipientEmail?: string | null; teamsWebhookUrl?: string | null; webhookUrl?: string | null; minSeverity?: string | null }) =>
    call("/api/notification-routing", json("PUT", r)),
};

/** MSP setup: auto-create the shared multi-tenant app registration via the server's Azure CLI. */
export const setupApi = {
  registerMspApp: (body?: { displayName?: string; redirectUri?: string }) =>
    call<{ clientId: string; tenantId: string; redirectUri: string }>("/api/setup/register-msp-app", json("POST", body ?? {})),
};

export function collectionTone(row: { configured: boolean; lastCollectionStatus: string | null; lastError?: string | null }): "good" | "warning" | "error" | "neutral" {
  if (!row.configured) return "neutral";
  if (row.lastCollectionStatus === "Failed") return "error";
  if (row.lastError) return "warning";
  if (row.lastCollectionStatus === "Completed") return "good";
  return "neutral";
}
