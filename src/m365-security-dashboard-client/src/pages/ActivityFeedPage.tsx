import React, { useCallback, useEffect, useState } from "react";
import { Search, ListChecks, User, AppWindow } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { relTime, fmtDate } from "../services/utils";
import { Card, Badge, EmptyState, LoadingSkeleton, ExportDropdown } from "../components/SharedComponents";

type AuditEventRow = {
  id: number;
  activity: string;
  category?: string;
  actorUpn?: string;
  actorApp?: string;
  targetName?: string;
  result?: string;
  occurredAt: string;
};

type Page = { total: number; page: number; pageSize: number; items: AuditEventRow[] };

/**
 * Tenant Activity: the collected directory-audit event stream that powers
 * activity-based alert policies. Answers "what changed in the tenant?"
 * without opening the Entra portal.
 */
export function ActivityFeedPage() {
  const [data, setData] = useState<Page | null>(null);
  const [error, setError] = useState(false);
  const [search, setSearch] = useState("");
  const [days, setDays] = useState(7);
  const [page, setPage] = useState(1);

  const load = useCallback(async () => {
    setError(false);
    try {
      const qs = new URLSearchParams({ days: String(days), page: String(page), pageSize: "50" });
      if (search.trim()) qs.set("search", search.trim());
      const r = await apiFetch(`${apiBase}/api/audit-events?${qs}`);
      if (!r.ok) throw new Error(String(r.status));
      setData(await r.json());
    } catch {
      setError(true);
    }
  }, [search, days, page]);

  // Debounce search typing; immediate on page/days change.
  useEffect(() => {
    const t = setTimeout(load, search ? 300 : 0);
    return () => clearTimeout(t);
  }, [load, search]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="page">
      <Card title="Tenant Activity"
        badge={<Badge label={data ? `${data.total} events · last ${days}d` : "—"} tone="neutral"/>}
        action={
          <div data-inline-style="inline-a0c5370730">
            <label className="search-box" data-inline-style="inline-b33e69fdee">
              <Search size={14}/>
              <input className="search-input" value={search}
                onChange={e => { setPage(1); setSearch(e.target.value); }}
                placeholder="Search activity, actor, target…"/>
            </label>
            <select className="filter-sel" value={days} onChange={e => { setPage(1); setDays(Number(e.target.value)); }}>
              <option value={1}>Last 24 hours</option>
              <option value={7}>Last 7 days</option>
              <option value={30}>Last 30 days</option>
              <option value={90}>Last 90 days</option>
            </select>
            <ExportDropdown rows={(data?.items ?? []).map(e => ({
              Occurred: e.occurredAt, Activity: e.activity, Category: e.category ?? "",
              Actor: e.actorUpn ?? e.actorApp ?? "", Target: e.targetName ?? "", Result: e.result ?? "",
            }))} filename="tenant-activity.csv" scopeTotal={data?.total}/>
          </div>
        }>
        <div data-inline-style="inline-03fbcc5593">
          Entra directory-audit events collected each cycle — the stream that activity-based
          alert policies match against. Create a policy from any recurring activity in
          Alert Center → Policies (type: Tenant activity).
        </div>
        {error ? (
          <EmptyState message="Could not load tenant activity — the API request failed. Refresh to retry."/>
        ) : data === null ? (
          <LoadingSkeleton type="table"/>
        ) : data.items.length === 0 ? (
          <EmptyState icon={<ListChecks size={28}/>}
            message={search ? `No activity matches "${search}" in the selected range.` : "No tenant activity collected yet — events appear after the next collection cycle."}/>
        ) : (
          <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr>
                    <th scope="col">When</th><th scope="col">Activity</th><th scope="col">Category</th>
                    <th scope="col">Actor</th><th scope="col">Target</th><th scope="col">Result</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map(e => (
                    <tr key={e.id}>
                      <td className="al-date" title={fmtDate(e.occurredAt)}>{relTime(e.occurredAt) || fmtDate(e.occurredAt)}</td>
                      <td data-inline-style="inline-3d9df89ef8">{e.activity}</td>
                      <td className="al-date">{e.category ?? "—"}</td>
                      <td className="al-date">
                        <span data-inline-style="inline-8657467ca0">
                          {e.actorUpn ? <User size={11}/> : e.actorApp ? <AppWindow size={11}/> : null}
                          {e.actorUpn ?? e.actorApp ?? "—"}
                        </span>
                      </td>
                      <td className="al-date trunc" title={e.targetName ?? undefined}>{e.targetName ?? "—"}</td>
                      <td>{e.result ? <Badge label={e.result} tone={e.result === "success" ? "good" : "error"}/> : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div data-inline-style="inline-e169eaa636">
              <span data-inline-style="inline-af7da65b76">
                {(data.page - 1) * data.pageSize + 1}–{Math.min(data.page * data.pageSize, data.total)} of {data.total}
              </span>
              <div data-inline-style="inline-3633e433a1">
                <button className="btn-export" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>Previous</button>
                <button className="btn-export" disabled={page >= totalPages} onClick={() => setPage(p => p + 1)}>Next</button>
              </div>
            </div>
          </>
        )}
      </Card>
    </div>
  );
}
