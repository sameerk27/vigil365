import { apiBase, apiFetch, acquireTokenPopupForTenant, acquireTokenSilentForTenant } from "./api";

/**
 * Power Platform inventory (docs/COPILOT_POWER_PLATFORM_PLAN.md, phase D).
 *
 * Read in this browser with the signed-in person's own Microsoft sign-in: Microsoft only
 * lets an app read Power Platform if it is enrolled with Power Platform Administrator
 * rights, which Vigil365 doesn't take. The person's token goes to Microsoft only; the
 * Vigil365 server gets the rows read and returns them evaluated (flags, owner names).
 * Nothing is stored: the result is kept in memory while the app is open.
 */

export type PowerPlatformFlag = { label: string; severity: "high" | "medium" | "low" | string };

export type PowerPlatformEnvironment = {
  id: string; name: string; type: string | null; region: string | null; isManaged: boolean | null;
  apps: number; flows: number; agents: number;
};

export type PowerPlatformApp = {
  id: string; name: string; environmentId: string; environmentName: string; ownerId: string | null; owner: string | null;
  created: string | null; modified: string | null; sharedUsers: number | null; sharedGroups: number | null;
  bypassConsent: boolean | null; quarantined: boolean; connectors: string[]; flags: PowerPlatformFlag[];
};

export type PowerPlatformFlow = {
  id: string; name: string; environmentId: string; environmentName: string; ownerId: string | null; owner: string | null;
  created: string | null; modified: string | null; trigger: string | null; connectors: string[]; flags: PowerPlatformFlag[];
};

export type PowerPlatformAgent = {
  id: string; name: string; environmentId: string; environmentName: string; ownerId: string | null; owner: string | null;
  created: string | null; published: string | null; createdIn: string | null; authentication: string | null;
  channels: string[]; sharedUsers: number; sharedGroups: number; sharedWithTenant: boolean; quarantined: boolean;
  connectors: string[]; flags: PowerPlatformFlag[];
};

export type PowerPlatformSummary = {
  environments: number; apps: number; flows: number; agents: number; needALook: number; consentBypassed: number;
  highRiskConnector: number; agentsWithoutSignIn: number; sharedWithEveryone: number; ownerGone: number;
};

export type PowerPlatformInventory = {
  readAt: string; environments: PowerPlatformEnvironment[]; apps: PowerPlatformApp[]; flows: PowerPlatformFlow[];
  agents: PowerPlatformAgent[]; summary: PowerPlatformSummary; appDetails: boolean; ownersResolved: boolean; notes: string[];
};

export type PowerPlatformSettings = {
  configured: boolean; tenantId?: string; powerPlatformApi?: string; graphApi?: string; maxEnvironments?: number;
};

export async function loadPowerPlatformSettings(): Promise<PowerPlatformSettings> {
  const r = await apiFetch(`${apiBase}/api/power-platform/settings`);
  if (!r.ok) throw new Error(String(r.status));
  return r.json();
}

export const RBAC_DOCS_URL = "https://learn.microsoft.com/power-platform/admin/power-platform-inventory#access-requirements";

/** Anything worth a look: a high or medium flag. Low flags (a group share, a draft) are context. */
export const needsLook = (flags: PowerPlatformFlag[]) => flags.some(f => f.severity === "high" || f.severity === "medium");

/** Connector ids read better without Microsoft's "shared_" prefix. */
export const connectorName = (c: string) => c.replace(/^shared_/i, "");

// ─── Reading ───────────────────────────────────────────────────────────────────

const API_VERSION = "2024-10-01";
/** Microsoft's own "Power Platform API" app: its service principal must exist in the tenant. */
export const POWER_PLATFORM_API_APP_ID = "8578e004-a5c6-46e7-913e-12f58912df43";
const INVENTORY_TYPES = ["microsoft.powerplatform/environments", "microsoft.powerapps/canvasapps", "microsoft.powerautomate/cloudflows", "microsoft.copilotstudio/agents"];
const MAX_INVENTORY_PAGES = 50;
const MAX_APP_PAGES = 20;
const PARALLEL = 4;

export type ReadProblem = "ApiNotInTenant" | "NoAccess" | "NotAppAdmin" | "Cancelled" | "Failed";

export class PowerPlatformReadError extends Error {
  constructor(public problem: ReadProblem, message: string) { super(message); }
}

const ppBase = (s: PowerPlatformSettings) => (s.powerPlatformApi || "https://api.powerplatform.com").replace(/\/+$/, "");
const graphBase = (s: PowerPlatformSettings) => (s.graphApi || "https://graph.microsoft.com").replace(/\/+$/, "");
const readScopes = (s: PowerPlatformSettings) => [`${ppBase(s)}/ResourceQuery.Resources.Read`, `${ppBase(s)}/PowerApps.Apps.Read`];

function popupProblem(e: unknown): PowerPlatformReadError {
  const code = (e as { errorCode?: string } | null)?.errorCode ?? "";
  const text = `${code} ${e instanceof Error ? e.message : String(e)}`;
  if (code === "user_cancelled" || /user_cancelled|popup_window_error|access_denied/i.test(text))
    return new PowerPlatformReadError("Cancelled", "The Microsoft sign-in window was closed or consent was declined.");
  // The tenant has no service principal for the Power Platform API, so nobody can get a token for it.
  if (/AADSTS650052|AADSTS500011/.test(text))
    return new PowerPlatformReadError("ApiNotInTenant", "This tenant hasn't registered Microsoft's Power Platform API yet.");
  return new PowerPlatformReadError("Failed", `Microsoft sign-in failed: ${e instanceof Error ? e.message : String(e)}`);
}

async function popup(tenantId: string, scopes: string[]): Promise<string> {
  try { return await acquireTokenPopupForTenant(tenantId, scopes); }
  catch (e) { throw popupProblem(e); }
}

async function call(method: string, url: string, token: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
}

async function said(r: Response): Promise<string> {
  try { const j = await r.json(); return j?.error?.message ?? j?.message ?? ""; } catch { return ""; }
}

type Row = Record<string, unknown>;
const lower = (o: unknown): Record<string, unknown> => {
  const out: Record<string, unknown> = {};
  if (o && typeof o === "object" && !Array.isArray(o)) for (const [k, v] of Object.entries(o)) out[k.toLowerCase()] = v;
  return out;
};

/** Keeps only the fields Vigil365 evaluates, so a large tenant's read stays small on the way to the server. */
const KEEP = ["displayName", "environmentId", "ownerId", "createdBy", "createdAt", "lastModifiedAt", "isQuarantined", "trigger",
  "environmentType", "isManaged", "lastPublishedAt", "createdIn", "authentication", "channels", "sharedWithViewers", "isWebSearchEnabledForKnowledge"];
function slimResource(row: Row): Row {
  const top = lower(row);
  const p = lower(top["properties"]);
  const properties: Row = {};
  for (const k of KEEP) if (p[k.toLowerCase()] !== undefined) properties[k] = p[k.toLowerCase()];
  const connectors = p["powerplatformconnectors"];
  if (Array.isArray(connectors)) {
    properties.powerPlatformConnectors = connectors.map(c => {
      const cl = lower(c);
      const ops = Array.isArray(cl["operations"]) ? (cl["operations"] as unknown[]).map(op => {
        const o = lower(op);
        return { connectionProvider: o["connectionprovider"], isEnabled: o["isenabled"], usedAs: o["usedas"] };
      }) : [];
      return { connectorId: cl["connectorid"], operations: ops };
    });
  }
  return { name: top["name"], type: top["type"], location: top["location"], properties };
}

function slimApp(app: Row): Row {
  const top = lower(app);
  const p = lower(top["properties"]);
  const refs = p["connectionreferences"];
  const list = Array.isArray(refs) ? refs : refs && typeof refs === "object" ? Object.values(refs) : [];
  const owner = lower(p["owner"]);
  return {
    name: top["name"],
    properties: {
      displayName: p["displayname"], bypassConsent: p["bypassconsent"],
      sharedUsersCount: p["shareduserscount"], sharedGroupsCount: p["sharedgroupscount"],
      owner: { displayName: owner["displayname"], userPrincipalName: owner["userprincipalname"] },
      connectionReferences: list.map(c => {
        const cl = lower(c);
        return { displayName: cl["displayname"], isCustomApiConnection: cl["iscustomapiconnection"], isOnPremiseConnection: cl["isonpremiseconnection"], bypassConsent: cl["bypassconsent"] };
      }),
    },
  };
}

/** Microsoft's inventory (Azure Resource Graph): every environment, canvas app, cloud flow and agent, paged. */
async function readInventory(s: PowerPlatformSettings, token: string, progress: (t: string) => void): Promise<{ rows: Row[]; complete: boolean }> {
  const rows: Row[] = [];
  let skipToken = "";
  for (let page = 0; page < MAX_INVENTORY_PAGES; page++) {
    const r = await call("POST", `${ppBase(s)}/resourcequery/resources/query?api-version=${API_VERSION}`, token, {
      TableName: "PowerPlatformResources",
      Clauses: [{ $type: "where", FieldName: "type", Operator: "in~", Values: INVENTORY_TYPES.map(t => `'${t}'`) }],
      Options: { Top: 1000, Skip: 0, SkipToken: skipToken },
    });
    if (r.status === 401 || r.status === 403) {
      const why = await said(r);
      throw new PowerPlatformReadError("NoAccess",
        "That account can't read the Power Platform inventory. It needs one of these Microsoft Entra roles: Global Administrator, " +
        "Power Platform Administrator, Dynamics 365 Administrator or Global Reader." +
        (/multi-?factor|conditional access|mfa/i.test(why) ? " Microsoft also mentioned multifactor authentication: the inventory reads Azure Resource Manager, so a Conditional Access policy on it can block this." : "") +
        (why ? ` Microsoft said: ${why}` : ""));
    }
    if (!r.ok) throw new PowerPlatformReadError("Failed", `Reading the Power Platform inventory failed (${r.status})${(await said(r).then(w => w ? `: ${w}` : "."))}`);
    const body = await r.json().catch(() => undefined);
    if (body === undefined) throw new PowerPlatformReadError("Failed", "Microsoft's inventory sent a response Vigil365 couldn't read. Try again.");
    const data = body?.data;
    if (Array.isArray(data)) rows.push(...data.map(slimResource));
    else if (data?.columns && Array.isArray(data.rows)) {
      // Table format: turn [columns, rows] into objects.
      const cols: string[] = data.columns.map((c: { name: string }) => c.name);
      rows.push(...data.rows.map((v: unknown[]) => slimResource(Object.fromEntries(cols.map((c, i) => [c, v[i]])))));
    }
    progress(`Read ${rows.length.toLocaleString()} items from Microsoft's inventory…`);
    skipToken = typeof body?.skipToken === "string" ? body.skipToken : "";
    if (!skipToken) return { rows, complete: true };
  }
  return { rows, complete: false };
}

type AppDetails = { environmentId: string; items: Row[]; complete: boolean; error?: string };

/** Apps as administrator, per environment: sharing counts and the consent bypass, which the inventory doesn't carry. */
async function readAppDetails(s: PowerPlatformSettings, token: string, environmentId: string): Promise<AppDetails> {
  const base = ppBase(s);
  const items: Row[] = [];
  let url: string | null = `${base}/powerapps/environments/${encodeURIComponent(environmentId)}/apps?api-version=${API_VERSION}&$top=250`;
  for (let page = 0; url && page < MAX_APP_PAGES; page++) {
    const r = await call("GET", url, token);
    if (r.status === 401 || r.status === 403) return { environmentId, items, complete: false, error: "access refused" };
    if (!r.ok) return { environmentId, items, complete: false, error: `error ${r.status}` };
    const body = await r.json().catch(() => undefined);
    if (body === undefined) return { environmentId, items, complete: false, error: "unreadable response" };
    if (Array.isArray(body?.value)) items.push(...body.value.map(slimApp));
    const next: unknown = body?.nextLink ?? body?.["@odata.nextLink"];
    if (typeof next !== "string" || !next) return { environmentId, items, complete: true };
    // The token stays on Microsoft's API host: a next page anywhere else isn't followed, and the read is partial.
    if (!next.startsWith(`${base}/`)) return { environmentId, items, complete: false };
    url = next;
  }
  return { environmentId, items, complete: false };
}

const cache = new Map<string, PowerPlatformInventory>();
export const cachedInventory = (tenantId: string) => cache.get(tenantId.toLowerCase()) ?? null;
/** Tests: forget earlier reads. */
export const clearPowerPlatformCache = () => cache.clear();

/**
 * Reads the tenant's Power Platform. interactive = false tries without a popup and returns
 * null if Microsoft needs the person to sign in or consent; true opens the popup, so it
 * must be called straight from a click.
 */
export async function readPowerPlatform(s: PowerPlatformSettings, interactive: boolean, progress: (t: string) => void = () => {}): Promise<PowerPlatformInventory | null> {
  const tenantId = s.tenantId!;
  const token = interactive ? await popup(tenantId, readScopes(s)) : await acquireTokenSilentForTenant(tenantId, readScopes(s));
  if (!token) return null;

  progress("Reading Microsoft's Power Platform inventory…");
  const { rows, complete } = await readInventory(s, token, progress);

  // Environments that hold canvas apps, busiest first, up to the cap.
  const counts = new Map<string, number>();
  for (const r of rows) {
    const p = r.properties as Row;
    if (String(r.type).toLowerCase() === "microsoft.powerapps/canvasapps" && typeof p?.environmentId === "string")
      counts.set(p.environmentId, (counts.get(p.environmentId) ?? 0) + 1);
  }
  const envs = [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([id]) => id);
  const cap = Math.max(1, s.maxEnvironments ?? 50);
  const toRead = envs.slice(0, cap);
  const details: AppDetails[] = [];
  let problem: string | undefined;
  for (let i = 0; i < toRead.length && !problem; i += PARALLEL) {
    progress(`Reading app sharing and consent detail (${Math.min(i + PARALLEL, toRead.length)} of ${toRead.length} environments)…`);
    const batch = await Promise.all(toRead.slice(i, i + PARALLEL).map(id => readAppDetails(s, token, id)));
    // Refused everywhere in the first batch: the account isn't a Power Platform admin. Stop asking.
    if (i === 0 && batch.length > 0 && batch.every(b => b.error === "access refused")) {
      problem = "your account can read the inventory but not apps as an administrator (that needs Power Platform Administrator).";
      break;
    }
    details.push(...batch);
  }

  progress("Checking what needs a look…");
  const r = await apiFetch(`${apiBase}/api/power-platform/inventory/evaluate`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      resources: rows, resourcesComplete: complete,
      appDetails: problem ? [] : details, appDetailsSkipped: problem ? 0 : Math.max(0, envs.length - toRead.length), appDetailsProblem: problem ?? null,
    }),
  });
  if (r.status === 413) throw new PowerPlatformReadError("Failed", "This tenant's Power Platform inventory is larger than Vigil365 can evaluate in one read.");
  if (!r.ok) throw new PowerPlatformReadError("Failed", `Vigil365 couldn't evaluate the inventory (${r.status}).`);
  const inventory: PowerPlatformInventory = await r.json();
  cache.set(tenantId.toLowerCase(), inventory);
  return inventory;
}

/** Registers Microsoft's Power Platform API in the tenant (Microsoft's documented step), with the person's own sign-in. */
export async function registerPowerPlatformApi(s: PowerPlatformSettings): Promise<void> {
  const base = graphBase(s);
  const token = await popup(s.tenantId!, [`${base}/Application.ReadWrite.All`]);
  const r = await call("POST", `${base}/v1.0/servicePrincipals`, token, { appId: POWER_PLATFORM_API_APP_ID });
  if (r.ok || r.status === 409) return; // 409: it already exists
  if (r.status === 403)
    throw new PowerPlatformReadError("NotAppAdmin", "That account can't register apps. Sign in as an Application Administrator or Cloud Application Administrator.");
  const why = await said(r);
  if (/already exist|ObjectsWithSameKeyValue/i.test(why)) return;
  throw new PowerPlatformReadError("Failed", `Registering the Power Platform API failed (${r.status})${why ? `: ${why}` : "."}`);
}
