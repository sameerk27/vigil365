import { apiBase, apiFetch } from "./api";

/** One Purview audit search (Power Platform, or Copilot Studio agents) for the selected tenant. */
export interface PurviewAuditFeed {
  feed: "powerPlatform" | "copilotStudio" | string;
  /** "NotStarted" | "Waiting" | "Ok" | "NeedsPermission" | "Failed" */
  status: string;
  lastRunAt: string | null;
  lastSuccessAt: string | null;
  lastEventCount: number;
  lastError: string | null;
  pending: boolean;
}

export interface PurviewAuditStatus {
  enabled: boolean;
  intervalMinutes: number;
  /** Install configuration (Graph:PowerPlatformHighRiskConnectors), shown read-only. */
  highRiskConnectors: string[];
  feeds: PurviewAuditFeed[];
}

/** The Tenant Activity source filter: "entra" is the directory audit, the rest are Purview audit products. */
export const ACTIVITY_SOURCES: { value: string; label: string }[] = [
  { value: "", label: "All sources" },
  { value: "entra", label: "Entra ID" },
  { value: "Copilot agents", label: "Copilot agents" },
  { value: "Power Automate", label: "Power Automate" },
  { value: "Power Apps", label: "Power Apps" },
  { value: "Power Platform admin", label: "Power Platform admin" },
];

export const purviewApi = {
  /** Null when the status can't be read; the caller shows nothing rather than a false all-clear. */
  status: async (): Promise<PurviewAuditStatus | null> => {
    try {
      const r = await apiFetch(`${apiBase}/api/purview-audit/status`);
      return r.ok ? ((await r.json()) as PurviewAuditStatus) : null;
    } catch {
      return null;
    }
  },
};
