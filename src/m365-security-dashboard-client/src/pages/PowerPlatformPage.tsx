import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Search, Workflow, BellRing, ListChecks } from "lucide-react";
import { apiBase, apiFetch, acApi, crossNavigate, isMspMode } from "../services/api";
import { fmtDate, relTime, severityRank, sevTone, matchesQuery } from "../services/utils";
import { Card, Badge, EmptyState, ExportDropdown, SortTh, rowActivation } from "../components/SharedComponents";
import { StatCard } from "../components/ui";
import { PurviewAuditPanel } from "../components/PurviewAuditPanel";
import type { AlertPolicy, TriggeredAlert, Tone } from "../services/types";

/** The alert categories this page covers: Power Platform, and Copilot Studio agents (built in Power Platform). */
const CATEGORIES = ["powerplatform", "copilot"];
const productOfCategory = (c: string) => (c === "copilot" ? "Copilot agents" : "Power Platform");

/** The Purview audit products, as the server names them in each event's category. */
const PRODUCTS = ["Copilot agents", "Power Automate", "Power Apps", "Power Platform admin"];

const OPEN = new Set(["new", "acknowledged"]);
const statusLabel: Record<string, string> = { new: "New", acknowledged: "Acknowledged", snoozed: "Snoozed", resolved: "Resolved", auto_resolved: "Resolved (auto)" };
const statusTone = (s: string): Tone => (s === "new" ? "error" : s === "acknowledged" ? "warning" : "neutral");

type AuditItem = {
  id: number; activity: string; category: string | null; source: string;
  actorUpn: string | null; actorApp: string | null; targetName: string | null; result: string | null; occurredAt: string;
};
type AuditPage = { total: number; page: number; pageSize: number; items: AuditItem[] };

const PAGE_SIZE = 200;

/**
 * A rule pattern as the server's LIKE reads it: "*" and "%" match anything, "_" one
 * character, the whole name must match. Case is ignored here; the badge is a hint, the
 * server decides.
 */
export function patternMatches(pattern: string, activity: string): boolean {
  const re = [...pattern.trim()].map(c => (c === "*" || c === "%" ? ".*" : c === "_" ? "." : c.replace(/[.+?^${}()|[\]\\]/g, "\\$&"))).join("");
  return re.length > 0 && new RegExp(`^${re}$`, "i").test(activity);
}

type AlertSort = "severity" | "rule" | "triggered" | "status";
type EventSort = "when" | "product" | "activity" | "who";
type RuleSort = "name" | "severity" | "lastTriggered";

/**
 * Power Platform alerts: what Copilot Studio agents, Power Automate flows, Power Apps and
 * Power Platform admins changed, and the alerts it raised. Everything comes from the
 * Microsoft Purview audit log, read in the background with Vigil365's own read-only
 * permission (AuditLogsQuery.Read.All); this page asks for no sign-in of its own.
 * Alerts are acted on in the Alert Center, so there is one place for that.
 */
export function PowerPlatformPage({ policies, triggeredAlerts, onOpenAlert }: {
  policies: AlertPolicy[];
  triggeredAlerts: TriggeredAlert[];
  onOpenAlert: (id: string) => void;
}) {
  // ── Alerts ──
  const alerts = useMemo(() => triggeredAlerts.filter(a => CATEGORIES.includes(a.category)), [triggeredAlerts]);
  const [alertSearch, setAlertSearch] = useState("");
  const [alertStatus, setAlertStatus] = useState("open");
  const [alertSeverity, setAlertSeverity] = useState("");
  const [alertProduct, setAlertProduct] = useState("");
  const [alertSort, setAlertSort] = useState<AlertSort>("triggered");
  const [alertDir, setAlertDir] = useState<"asc" | "desc">("desc");

  const shownAlerts = useMemo(() => {
    const dir = alertDir === "asc" ? 1 : -1;
    return alerts
      .filter(a =>
        (alertStatus === "" || (alertStatus === "open" ? OPEN.has(a.status) : a.status === alertStatus || (alertStatus === "resolved" && a.status === "auto_resolved"))) &&
        (!alertSeverity || a.severity.toLowerCase() === alertSeverity) &&
        (!alertProduct || productOfCategory(a.category) === alertProduct) &&
        matchesQuery(alertSearch, a.policyName, a.affectedEntities, a.assignedTo))
      .sort((a, b) => {
        const by = alertSort === "severity" ? severityRank(b.severity) - severityRank(a.severity)
          : alertSort === "rule" ? a.policyName.localeCompare(b.policyName)
          : alertSort === "status" ? (statusLabel[a.status] ?? a.status).localeCompare(statusLabel[b.status] ?? b.status)
          : a.triggeredAt.localeCompare(b.triggeredAt);
        return by * dir || b.triggeredAt.localeCompare(a.triggeredAt);
      });
  }, [alerts, alertSearch, alertStatus, alertSeverity, alertProduct, alertSort, alertDir]);

  const onAlertSort = (c: AlertSort) => {
    if (alertSort === c) setAlertDir(d => (d === "asc" ? "desc" : "asc"));
    else { setAlertSort(c); setAlertDir(c === "rule" || c === "status" ? "asc" : "desc"); }
  };

  // ── Rules ──
  // MSP: a client can switch a rule off or change its threshold; the evaluator honours that, so this page must too.
  const [overrides, setOverrides] = useState<Record<string, { enabled: boolean | null; threshold: number | null }>>({});
  useEffect(() => {
    if (!isMspMode()) return;
    let cancelled = false;
    acApi.getTenantOverrides().then(rows => { if (!cancelled) setOverrides(Object.fromEntries(rows.map(r => [r.policyId, r]))); });
    return () => { cancelled = true; };
  }, [policies]);

  const rules = useMemo(() => policies
    .filter(p => CATEGORIES.includes(p.category) && (p.kind ?? "metric") === "activity")
    .map(p => {
      const o = overrides[p.id];
      return o ? { ...p, enabled: o.enabled ?? p.enabled, threshold: o.threshold ?? p.threshold } : p;
    }), [policies, overrides]);
  const enabledRules = useMemo(() => rules.filter(r => r.enabled && r.activityPattern), [rules]);
  const [ruleSearch, setRuleSearch] = useState("");
  const [ruleState, setRuleState] = useState("");
  const [ruleSort, setRuleSort] = useState<RuleSort>("severity");
  const [ruleDir, setRuleDir] = useState<"asc" | "desc">("asc");

  const shownRules = useMemo(() => {
    const dir = ruleDir === "asc" ? 1 : -1;
    return rules
      .filter(r => (!ruleState || (ruleState === "on") === r.enabled) && matchesQuery(ruleSearch, r.name, r.activityPattern))
      .sort((a, b) => {
        const by = ruleSort === "name" ? a.name.localeCompare(b.name)
          : ruleSort === "lastTriggered" ? (a.lastTriggered ?? "").localeCompare(b.lastTriggered ?? "")
          : severityRank(a.severity) - severityRank(b.severity);
        return by * dir || a.name.localeCompare(b.name);
      });
  }, [rules, ruleSearch, ruleState, ruleSort, ruleDir]);

  const onRuleSort = (c: RuleSort) => {
    if (ruleSort === c) setRuleDir(d => (d === "asc" ? "desc" : "asc"));
    else { setRuleSort(c); setRuleDir(c === "lastTriggered" ? "desc" : "asc"); }
  };

  // ── Activity ──
  const [days, setDays] = useState(7);
  const [product, setProduct] = useState("");
  const [events, setEvents] = useState<AuditItem[] | null>(null);
  const [eventsTotal, setEventsTotal] = useState(0);
  const [eventsPage, setEventsPage] = useState(1);
  const [eventsFailed, setEventsFailed] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadMoreFailed, setLoadMoreFailed] = useState(false);
  // Bumped when the filters change, so a "Load more" still in flight can't add rows from the old filters.
  const generation = useRef(0);
  /** undefined while loading, null when it couldn't be read. */
  const [weekTotal, setWeekTotal] = useState<number | null | undefined>(undefined);
  const [eventSearch, setEventSearch] = useState("");
  const [wouldAlertOnly, setWouldAlertOnly] = useState(false);
  const [eventSort, setEventSort] = useState<EventSort>("when");
  const [eventDir, setEventDir] = useState<"asc" | "desc">("desc");

  const fetchEvents = useCallback(async (page: number): Promise<AuditPage> => {
    const qs = new URLSearchParams({ source: product || "purviewAudit", days: String(days), page: String(page), pageSize: String(PAGE_SIZE) });
    const r = await apiFetch(`${apiBase}/api/audit-events?${qs}`);
    if (!r.ok) throw new Error(String(r.status));
    return (await r.json()) as AuditPage;
  }, [product, days]);

  useEffect(() => {
    let cancelled = false;
    generation.current++;
    setEvents(null);
    setEventsFailed(false);
    setLoadingMore(false);
    setLoadMoreFailed(false);
    fetchEvents(1)
      .then(p => { if (!cancelled) { setEvents(p.items); setEventsTotal(p.total); setEventsPage(1); } })
      .catch(() => { if (!cancelled) setEventsFailed(true); });
    return () => { cancelled = true; };
  }, [fetchEvents]);

  // The tile counts the last 7 days whatever the table is filtered to.
  useEffect(() => {
    let cancelled = false;
    apiFetch(`${apiBase}/api/audit-events?source=purviewAudit&days=7&pageSize=1`)
      .then(r => (r.ok ? (r.json() as Promise<AuditPage>) : null))
      .then(p => { if (!cancelled) setWeekTotal(p ? p.total : null); })
      .catch(() => { if (!cancelled) setWeekTotal(null); });
    return () => { cancelled = true; };
  }, []);

  const loadMore = async () => {
    const started = generation.current;
    setLoadingMore(true);
    setLoadMoreFailed(false);
    try {
      const p = await fetchEvents(eventsPage + 1);
      if (started !== generation.current) return;
      // New records push older ones onto the next page, so a row can come back twice.
      setEvents(prev => {
        const seen = new Set((prev ?? []).map(e => e.id));
        return [...(prev ?? []), ...p.items.filter(e => !seen.has(e.id))];
      });
      setEventsPage(p.page);
      setEventsTotal(p.total);
    } catch {
      if (started === generation.current) setLoadMoreFailed(true);
    } finally {
      if (started === generation.current) setLoadingMore(false);
    }
  };

  // A rule with a threshold above 1 needs several events: one event alone only counts towards it.
  const rulesFor = useCallback((activity: string) => {
    const hits = enabledRules.filter(r => patternMatches(r.activityPattern!, activity));
    return { rule: hits.find(r => r.threshold <= 1) ?? null, counts: hits.find(r => r.threshold > 1) ?? null };
  }, [enabledRules]);

  const shownEvents = useMemo(() => {
    const dir = eventDir === "asc" ? 1 : -1;
    return (events ?? [])
      .map(e => ({ e, ...rulesFor(e.activity) }))
      .filter(({ e, rule }) => (!wouldAlertOnly || rule) && matchesQuery(eventSearch, e.activity, e.actorUpn, e.actorApp, e.targetName))
      .sort((a, b) => {
        const by = eventSort === "product" ? (a.e.category ?? "").localeCompare(b.e.category ?? "")
          : eventSort === "activity" ? a.e.activity.localeCompare(b.e.activity)
          : eventSort === "who" ? (a.e.actorUpn ?? a.e.actorApp ?? "").localeCompare(b.e.actorUpn ?? b.e.actorApp ?? "")
          : a.e.occurredAt.localeCompare(b.e.occurredAt);
        return by * dir || b.e.occurredAt.localeCompare(a.e.occurredAt);
      });
  }, [events, rulesFor, wouldAlertOnly, eventSearch, eventSort, eventDir]);

  const onEventSort = (c: EventSort) => {
    if (eventSort === c) setEventDir(d => (d === "asc" ? "desc" : "asc"));
    else { setEventSort(c); setEventDir(c === "when" ? "desc" : "asc"); }
  };

  const openAlerts = alerts.filter(a => OPEN.has(a.status));
  const openHigh = openAlerts.filter(a => severityRank(a.severity) <= 1).length;

  return (
    <div className="page">
      <PurviewAuditPanel/>
      <p className="pp-muted pp-note">
        From Microsoft's audit log, read in the background with Vigil365's own read-only permission. Microsoft can take up to
        24 hours to log an action, so an alert can arrive hours after the change.
      </p>

      <div className="stat-row-4">
        <StatCard label="Open alerts" value={openAlerts.length} sub="new or acknowledged"/>
        <StatCard label="Open, high" value={openHigh} sub={openHigh ? "look at these first" : "none"}/>
        <StatCard label="Events in 7 days" value={weekTotal ?? "—"} sub={weekTotal === undefined ? "loading" : weekTotal === null ? "couldn't be read" : "agents, flows, apps and admin changes"}/>
        <StatCard label="Rules on" value={enabledRules.length} sub={`of ${rules.length} Power Platform and agent rules`}/>
      </div>

      <Card title="Alerts" badge={<Badge label={`${shownAlerts.length} shown`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search alerts" value={alertSearch} onChange={e => setAlertSearch(e.target.value)} placeholder="Rule, entity or assignee"/>
            </label>
            <select className="filter-sel" aria-label="Filter alerts by status" value={alertStatus} onChange={e => setAlertStatus(e.target.value)}>
              <option value="open">Open</option>
              <option value="">All statuses</option>
              <option value="new">New</option>
              <option value="acknowledged">Acknowledged</option>
              <option value="snoozed">Snoozed</option>
              <option value="resolved">Resolved</option>
            </select>
            <select className="filter-sel" aria-label="Filter alerts by severity" value={alertSeverity} onChange={e => setAlertSeverity(e.target.value)}>
              <option value="">All severities</option>
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </select>
            <select className="filter-sel" aria-label="Filter alerts by product" value={alertProduct} onChange={e => setAlertProduct(e.target.value)}>
              <option value="">Agents and Power Platform</option>
              <option value="Copilot agents">Copilot agents</option>
              <option value="Power Platform">Power Platform</option>
            </select>
            <ExportDropdown filename="power-platform-alerts.csv"
              rows={shownAlerts.map(a => ({ Severity: a.severity, Rule: a.policyName, Product: productOfCategory(a.category), Triggered: a.triggeredAt, Status: statusLabel[a.status] ?? a.status, AssignedTo: a.assignedTo ?? "", Condition: a.condition }))}/>
          </div>
        }>
        {shownAlerts.length === 0 ? (
          <EmptyState icon={<BellRing size={24}/>} message={alerts.length === 0
            ? "No Power Platform or agent alerts yet. They appear here when a rule below matches an audit event."
            : "No alerts match these filters."}/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead>
                <tr>
                  <SortTh label="Severity" col="severity" sortBy={alertSort} sortDir={alertDir} onSort={onAlertSort}/>
                  <SortTh label="Rule" col="rule" sortBy={alertSort} sortDir={alertDir} onSort={onAlertSort}/>
                  <th scope="col">Product</th>
                  <SortTh label="Triggered" col="triggered" sortBy={alertSort} sortDir={alertDir} onSort={onAlertSort}/>
                  <SortTh label="Status" col="status" sortBy={alertSort} sortDir={alertDir} onSort={onAlertSort}/>
                  <th scope="col">Assigned to</th>
                </tr>
              </thead>
              <tbody>
                {shownAlerts.map(a => (
                  <tr key={a.id} className="tbl-row-click" {...rowActivation(() => onOpenAlert(a.id), `Open ${a.policyName} in the Alert Center`)}>
                    <td><Badge label={a.severity} tone={sevTone(a.severity)}/></td>
                    <td>{a.policyName}</td>
                    <td>{productOfCategory(a.category)}</td>
                    <td className="al-date" title={fmtDate(a.triggeredAt)}>{relTime(a.triggeredAt) || fmtDate(a.triggeredAt)}</td>
                    <td><Badge label={statusLabel[a.status] ?? a.status} tone={statusTone(a.status)}/></td>
                    <td>{a.assignedTo || <span className="pp-muted">Nobody</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="pp-muted pp-truncated">Select an alert to acknowledge, assign or resolve it in the Alert Center.</p>
        {triggeredAlerts.length >= 500 && (
          <p className="pp-muted pp-truncated">Vigil365 loads the newest 500 alerts across every area, so older Power Platform alerts may not be counted here.</p>
        )}
      </Card>

      <Card title="Activity" badge={<Badge label={events ? `${eventsTotal} in ${days} days` : eventsFailed ? "couldn't load" : "loading"} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search activity" value={eventSearch} onChange={e => setEventSearch(e.target.value)} placeholder="Activity, who or what"/>
            </label>
            <select className="filter-sel" aria-label="Filter activity by product" value={product} onChange={e => setProduct(e.target.value)}>
              <option value="">All products</option>
              {PRODUCTS.map(p => <option key={p} value={p}>{p}</option>)}
            </select>
            <select className="filter-sel" aria-label="Activity period" value={days} onChange={e => setDays(Number(e.target.value))}>
              <option value={7}>Last 7 days</option>
              <option value={30}>Last 30 days</option>
              <option value={90}>Last 90 days</option>
            </select>
            <label className="pp-check">
              <input type="checkbox" checked={wouldAlertOnly} onChange={e => setWouldAlertOnly(e.target.checked)}/> Only what a rule alerts on
            </label>
            <ExportDropdown filename="power-platform-activity.csv" scopeTotal={eventsTotal}
              rows={shownEvents.map(({ e, rule, counts }) => ({ When: e.occurredAt, Product: e.category ?? "", Activity: e.activity, Who: e.actorUpn ?? e.actorApp ?? "", Target: e.targetName ?? "", Result: e.result ?? "", Rule: rule?.name ?? (counts ? `Counts towards: ${counts.name}` : "") }))}/>
          </div>
        }>
        {eventsFailed ? (
          <EmptyState message="Couldn't load the activity: the API request failed. Refresh to try again."/>
        ) : events === null ? (
          <p className="pp-muted">Loading activity…</p>
        ) : shownEvents.length === 0 ? (
          <EmptyState icon={<Workflow size={24}/>} message={events.length === 0
            ? `Nothing logged in the last ${days} days. If the status above says a permission is missing, grant it first.`
            : "No activity matches these filters."}/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead>
                <tr>
                  <SortTh label="When" col="when" sortBy={eventSort} sortDir={eventDir} onSort={onEventSort}/>
                  <SortTh label="Product" col="product" sortBy={eventSort} sortDir={eventDir} onSort={onEventSort}/>
                  <SortTh label="Activity" col="activity" sortBy={eventSort} sortDir={eventDir} onSort={onEventSort}/>
                  <SortTh label="Who" col="who" sortBy={eventSort} sortDir={eventDir} onSort={onEventSort}/>
                  <th scope="col">What</th>
                  <th scope="col">Rule</th>
                </tr>
              </thead>
              <tbody>
                {shownEvents.map(({ e, rule, counts }) => (
                  <tr key={e.id}>
                    <td className="al-date" title={fmtDate(e.occurredAt)}>{fmtDate(e.occurredAt)}</td>
                    <td>{e.category ?? "—"}</td>
                    <td>{e.activity}{e.result === "failure" ? <> <Badge label="failed" tone="neutral"/></> : null}</td>
                    <td>{e.actorUpn ?? e.actorApp ?? "—"}</td>
                    <td>{e.targetName ?? "—"}</td>
                    <td>{rule ? <Badge label={`Would alert: ${rule.name}`} tone={sevTone(rule.severity)}/>
                      : counts ? <Badge label={`Counts towards: ${counts.name}`} tone="neutral"/>
                      : <span className="pp-muted">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {events && events.length < eventsTotal && (
          <div className="pp-center">
            <button className="btn-export" onClick={loadMore} disabled={loadingMore}>
              {loadingMore ? "Loading…" : `Load more (${events.length} of ${eventsTotal})`}
            </button>
          </div>
        )}
        {loadMoreFailed && <p className="pp-muted pp-truncated" role="status">Couldn't load more activity. Try again.</p>}
      </Card>

      <Card title="Rules" badge={<Badge label={`${enabledRules.length} on`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search rules" value={ruleSearch} onChange={e => setRuleSearch(e.target.value)} placeholder="Rule or pattern"/>
            </label>
            <select className="filter-sel" aria-label="Filter rules by state" value={ruleState} onChange={e => setRuleState(e.target.value)}>
              <option value="">On and off</option>
              <option value="on">On</option>
              <option value="off">Off</option>
            </select>
            <ExportDropdown filename="power-platform-rules.csv"
              rows={shownRules.map(r => ({ Rule: r.name, Product: productOfCategory(r.category), Pattern: r.activityPattern ?? "", Severity: r.severity, On: r.enabled ? "yes" : "no", Condition: r.condition, LastTriggered: r.lastTriggered ?? "", Triggered: r.triggerCount }))}/>
            <button className="btn-export" onClick={() => crossNavigate({ page: "alertcenter", tab: "policies" })}><ListChecks size={13}/> Open in Alert Center</button>
          </div>
        }>
        {shownRules.length === 0 ? (
          <EmptyState message={rules.length === 0 ? "No Power Platform or agent rules yet. Vigil365 adds them when it starts." : "No rules match these filters."}/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead>
                <tr>
                  <SortTh label="Rule" col="name" sortBy={ruleSort} sortDir={ruleDir} onSort={onRuleSort}/>
                  <th scope="col">Product</th>
                  <th scope="col">When</th>
                  <SortTh label="Severity" col="severity" sortBy={ruleSort} sortDir={ruleDir} onSort={onRuleSort}/>
                  <th scope="col">State</th>
                  <SortTh label="Last triggered" col="lastTriggered" sortBy={ruleSort} sortDir={ruleDir} onSort={onRuleSort}/>
                </tr>
              </thead>
              <tbody>
                {shownRules.map(r => (
                  <tr key={r.id}>
                    <td>{r.name}</td>
                    <td>{productOfCategory(r.category)}</td>
                    <td className="mono-cell">{r.condition}</td>
                    <td><Badge label={r.severity} tone={sevTone(r.severity)}/></td>
                    <td><Badge label={r.enabled ? "On" : "Off"} tone={r.enabled ? "good" : "neutral"}/></td>
                    <td className="al-date">{r.lastTriggered ? fmtDate(r.lastTriggered) : "Never"}{r.triggerCount ? ` · ${r.triggerCount}×` : ""}</td>
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
