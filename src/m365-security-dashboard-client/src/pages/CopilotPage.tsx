import React, { useEffect, useMemo, useState } from "react";
import { Search, Sparkles, EyeOff, Lightbulb } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { fmtDay } from "../services/utils";
import { Card, Badge, EmptyState, LoadingSkeleton, ExportDropdown, ProgressBar } from "../components/SharedComponents";
import { StatCard } from "../components/ui";
import type { Tone } from "../services/types";

type CopilotUser = { name: string; upn: string | null; lastActivity: string | null; apps: string[]; status: "Active" | "Inactive" | "Never used" | string };
type CopilotUsage = {
  configured: boolean;
  error?: string;
  reportRefreshDate?: string | null;
  licensedUsers?: number;
  active?: number;
  inactive?: number;
  neverUsed?: number;
  seats?: number | null;
  assignedSeats?: number | null;
  unusedSeats?: number | null;
  skus?: { partNumber: string; purchased: number; assigned: number }[];
  namesConcealed?: boolean;
  apps?: { name: string; active: number }[];
  users?: CopilotUser[];
  activeDays?: number;
};

const statusTone = (s: string): Tone => (s === "Active" ? "good" : s === "Never used" ? "neutral" : "warning");

/**
 * Copilot usage: who holds a Microsoft 365 Copilot seat, who uses it and in which apps,
 * and how many paid seats do nothing. From the Copilot usage report (Reports.Read.All)
 * and the tenant's licences; no extra permission.
 */
export function CopilotPage() {
  const [data, setData] = useState<CopilotUsage | null>(null);
  const [failed, setFailed] = useState(false);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");

  useEffect(() => {
    let cancelled = false;
    apiFetch(`${apiBase}/api/dashboard/copilot`)
      .then(r => { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .then((d: CopilotUsage) => { if (!cancelled) setData(d); })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, []);

  const users = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (data?.users ?? []).filter(u =>
      (!status || u.status === status) &&
      (!q || u.name.toLowerCase().includes(q) || (u.upn ?? "").toLowerCase().includes(q)));
  }, [data, search, status]);

  if (failed) return <div className="page"><EmptyState message="Could not load Copilot usage: the API request failed. Refresh to retry."/></div>;
  if (data === null) return <div className="page"><LoadingSkeleton type="table"/></div>;
  if (!data.configured) return <div className="page"><EmptyState icon={<Sparkles size={28}/>} message="Connect Microsoft Graph in Setup to see Copilot usage."/></div>;
  if (data.error) return <div className="page"><EmptyState icon={<Sparkles size={28}/>} message={`Copilot usage could not be read: ${data.error}`}/></div>;

  const days = data.activeDays ?? 30;
  const licensed = data.licensedUsers ?? 0;
  const idle = (data.inactive ?? 0) + (data.neverUsed ?? 0);
  const maxApp = Math.max(1, ...(data.apps ?? []).map(a => a.active));

  if (licensed === 0) {
    return (
      <div className="page">
        <EmptyState icon={<Sparkles size={28}/>}
          message="No Copilot usage to show. Users appear here once they hold a Microsoft 365 Copilot licence, and the report can take a few days to fill."/>
      </div>
    );
  }

  return (
    <div className="page">
      {data.namesConcealed && (
        <div className="notice-warn" role="status">
          <EyeOff size={16} aria-hidden="true"/>
          <div>
            <strong>User names are hidden in this tenant's usage reports</strong>
            <p>Users show as codes. A Global Administrator can turn this off in the Microsoft 365 admin center's report settings. The counts are still correct.</p>
          </div>
        </div>
      )}

      <div className="stat-row-4">
        <StatCard label="Licensed users" value={licensed} sub={data.seats != null ? `of ${data.seats} seats bought` : "in the usage report"}/>
        <StatCard label={`Active in ${days} days`} value={data.active ?? 0} sub={`${Math.round(((data.active ?? 0) / licensed) * 100)}% of licensed users`}/>
        <StatCard label={`Inactive ${days}+ days`} value={idle} sub={`${data.neverUsed ?? 0} never used it`}/>
        <StatCard label="Unused seats" value={data.unusedSeats ?? "—"} sub={data.unusedSeats != null ? "bought, not assigned" : "no Copilot licence found"}/>
      </div>

      <div className="two-col">
        <Card title="Active users by app" badge={<Badge label={`last ${days} days`} tone="neutral"/>}>
          <div className="copilot-apps">
            {(data.apps ?? []).map(a => (
              <div key={a.name} className="copilot-app-row">
                <span>{a.name}</span>
                <ProgressBar pct={(a.active / maxApp) * 100} color="var(--accent)"/>
                <span className="copilot-app-count">{a.active}</span>
              </div>
            ))}
          </div>
        </Card>
        <Card title="Recommendation">
          {idle > 0 ? (
            <div className="copilot-rec">
              <Lightbulb size={16} aria-hidden="true"/>
              <div>
                <strong>{idle} licensed user{idle === 1 ? " hasn't" : "s haven't"} used Copilot in {days} days</strong>
                <p>
                  Reassign or remove those licences.
                  {data.unusedSeats ? ` With the ${data.unusedSeats} unassigned seat${data.unusedSeats === 1 ? "" : "s"}, ${idle + data.unusedSeats} paid seats are doing nothing.` : ""}
                </p>
                <button className="btn-export" onClick={() => setStatus("Inactive")}>Show inactive users</button>
              </div>
            </div>
          ) : (
            <p className="copilot-muted">Every licensed user has used Copilot in the last {days} days.</p>
          )}
        </Card>
      </div>

      <Card title="Licensed users"
        badge={<Badge label={data.reportRefreshDate ? `report from ${fmtDay(data.reportRefreshDate)}` : `${licensed} users`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search users" value={search} onChange={e => setSearch(e.target.value)} placeholder="Name or email"/>
            </label>
            <select className="filter-sel" aria-label="Filter by status" value={status} onChange={e => setStatus(e.target.value)}>
              <option value="">All licensed users</option>
              <option value="Active">Active</option>
              <option value="Inactive">Inactive {days}+ days</option>
              <option value="Never used">Never used</option>
            </select>
            <ExportDropdown rows={users.map(u => ({ User: u.name, Email: u.upn ?? "", LastActivity: u.lastActivity ?? "", Apps: u.apps.join("; "), Status: u.status }))}
              filename="copilot-usage.csv" scopeTotal={data.users?.length}/>
          </div>
        }>
        {users.length === 0 ? (
          <EmptyState message="No users match these filters."/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead>
                <tr><th scope="col">User</th><th scope="col">Last activity</th><th scope="col">Apps used in {days} days</th><th scope="col">Status</th></tr>
              </thead>
              <tbody>
                {users.map(u => (
                  <tr key={(u.upn ?? u.name) + u.lastActivity}>
                    <td className={data.namesConcealed ? "mono-cell" : undefined}>{u.name}{u.upn && u.upn !== u.name && !data.namesConcealed ? <span className="copilot-upn">{u.upn}</span> : null}</td>
                    <td className="al-date">{u.lastActivity ? fmtDay(u.lastActivity) : "Never"}</td>
                    <td className="al-date">{u.apps.length ? u.apps.join(", ") : "None"}</td>
                    <td><Badge label={u.status === "Inactive" ? `Inactive ${days}+ days` : u.status} tone={statusTone(u.status)}/></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}
