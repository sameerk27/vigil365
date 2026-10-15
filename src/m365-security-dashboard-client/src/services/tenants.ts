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

// The signed-in user's permitted clients, fetched once per page load and shared by
// ClientGate, the switcher and the header badge (they all need the same answer).
let _mine: Promise<Result<MyTenants>> | null = null;
export function loadMyTenants(): Promise<Result<MyTenants>> {
  return (_mine ??= call<MyTenants>("/api/tenants/me"));
}
/** Tests only. */
export function resetMyTenantsCache(): void { _mine = null; }

/** One open alert in the cross-client queue (U6). */
export interface QueueItem {
  id: string; tenantId: string; tenantName: string; policyName: string; severity: string; category: string;
  condition: string; metricValue: number; triggeredAt: string; status: string; assignedTo: string | null; snoozedUntil: string | null;
}

export const queueApi = {
  list: () => call<QueueItem[]>("/api/tenants/alerts"),
  /** Acts in the alert's own client (explicit header), not the selected one. */
  act: (item: Pick<QueueItem, "id" | "tenantId">, action: "acknowledge" | "resolve") =>
    call(`/api/triggered-alerts/${item.id}/${action}`, { method: "POST", headers: { "X-Vigil-Tenant": item.tenantId } }),
};

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
  /** Admins only: the URL lets whoever holds it post into the client's channel. */
  teamsWebhookUrl: string | null;
  /** For everyone: whether a client Teams webhook is set, when the URL is withheld. */
  hasTeamsWebhookUrl: boolean;
  hasWebhookUrl: boolean;
  minSeverity: string | null;
  lastDigestAt: string | null;
}

export const routingApi = {
  get: () => call<TenantRouting>("/api/notification-routing"),
  save: (r: { notifyMsp: boolean; notifyClient: boolean; recipientEmail?: string | null; teamsWebhookUrl?: string | null; webhookUrl?: string | null; minSeverity?: string | null }) =>
    call("/api/notification-routing", json("PUT", r)),
};

/** Readiness of the install's own app registration for client consent (read-only). */
export interface MspAppStatus {
  readable: boolean;
  multiTenant: boolean | null;
  consentRedirectRegistered: boolean | null;
  missingPermissions: string[] | null;
  expectedRedirect: string;
  reason: string | null;
  ready: boolean;
}

export const setupApi = {
  mspAppStatus: () => call<MspAppStatus>("/api/setup/msp-app-status"),
};

export function collectionTone(row: { configured: boolean; lastCollectionStatus: string | null; lastError?: string | null }): "good" | "warning" | "error" | "neutral" {
  if (!row.configured) return "neutral";
  if (row.lastCollectionStatus === "Failed") return "error";
  if (row.lastError) return "warning";
  if (row.lastCollectionStatus === "Completed") return "good";
  return "neutral";
}
