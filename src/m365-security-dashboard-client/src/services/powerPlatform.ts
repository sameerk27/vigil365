import { apiBase, apiFetch } from "./api";

/** GET /api/power-platform/inventory (docs/COPILOT_POWER_PLATFORM_PLAN.md, phase D). */

export type PowerPlatformFlag = { label: string; severity: "high" | "medium" | "low" | string };

export type PowerPlatformEnvironment = {
  id: string; name: string; type: string | null; state: string | null; region: string | null;
  hasDataverse: boolean; created: string | null; apps: number; flows: number; problem: string | null;
};

export type PowerPlatformApp = {
  id: string; name: string; environmentId: string; environmentName: string; owner: string | null;
  created: string | null; modified: string | null; sharedUsers: number; sharedGroups: number;
  bypassConsent: boolean; connectors: string[]; flags: PowerPlatformFlag[];
};

export type PowerPlatformFlow = {
  id: string; name: string; environmentId: string; environmentName: string; state: "On" | "Off" | "Suspended" | "Unknown" | string;
  created: string | null; modified: string | null; connectors: string[]; flags: PowerPlatformFlag[];
};

export type PowerPlatformSummary = {
  environments: number; environmentsRead: number; environmentsSkipped: number; apps: number; flows: number;
  consentBypassed: number; highRiskConnector: number; widelyShared: number; customConnector: number;
};

export type PowerPlatformSetup = { tenantId: string; appId: string; role: string; roleId: string; script: string; command: string };

export type PowerPlatformInventory = {
  configured: boolean;
  /** Ready, Partial (some environments couldn't be read), NotEnabled (no reader role yet) or Error. */
  status?: "Ready" | "Partial" | "NotEnabled" | "Error";
  /** NotEnabled: RoleMissing or ApiNotInTenant. Error: what went wrong. */
  reason?: string | null;
  readAt?: string;
  summary?: PowerPlatformSummary;
  environments?: PowerPlatformEnvironment[];
  apps?: PowerPlatformApp[];
  flows?: PowerPlatformFlow[];
  setup?: PowerPlatformSetup;
};

export async function loadPowerPlatformInventory(refresh = false): Promise<PowerPlatformInventory> {
  const r = await apiFetch(`${apiBase}/api/power-platform/inventory${refresh ? "?refresh=true" : ""}`);
  if (!r.ok) throw new Error(String(r.status));
  return r.json();
}

/** Where the grant script lives for people who don't have the repository checked out. */
export const GRANT_SCRIPT_URL = "https://github.com/sameerk27/vigil365/blob/master/grant-power-platform-reader.ps1";
export const RBAC_DOCS_URL = "https://learn.microsoft.com/power-platform/admin/programmability-tutorial-rbac-role-assignment";

/** Anything worth a look: a high or medium flag. Low flags (a group share, a suspended flow) are context. */
export const needsLook = (flags: PowerPlatformFlag[]) => flags.some(f => f.severity === "high" || f.severity === "medium");
