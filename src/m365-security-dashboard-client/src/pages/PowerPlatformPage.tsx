import React, { useCallback, useEffect, useMemo, useState } from "react";
import { Search, Workflow, AlertTriangle, RefreshCw, ShieldCheck, ExternalLink } from "lucide-react";
import { fmtDay } from "../services/utils";
import { Card, Badge, EmptyState, LoadingSkeleton, ExportDropdown, CopyButton } from "../components/SharedComponents";
import { StatCard } from "../components/ui";
import type { Tone } from "../services/types";
import {
  loadPowerPlatformInventory, needsLook, GRANT_SCRIPT_URL, RBAC_DOCS_URL,
  type PowerPlatformInventory, type PowerPlatformFlag,
} from "../services/powerPlatform";

/** Rows rendered per table; search, the filters and export reach the rest. */
const ROW_LIMIT = 500;

const flagTone = (f: PowerPlatformFlag): Tone => (f.severity === "high" ? "error" : f.severity === "medium" ? "warning" : "neutral");
const stateTone = (s: string): Tone => (s === "On" ? "good" : s === "Suspended" ? "warning" : "neutral");

function Flags({ flags }: { flags: PowerPlatformFlag[] }) {
  if (flags.length === 0) return <span className="pp-muted">None</span>;
  return <span className="pp-flags">{flags.map(f => <Badge key={f.label} label={f.label} tone={flagTone(f)}/>)}</span>;
}

function Truncated({ shown, total }: { shown: number; total: number }) {
  return shown < total ? <p className="pp-muted pp-truncated">Showing {shown} of {total}. Search, filter or export to see the rest.</p> : null;
}

/**
 * Power Platform inventory: every environment, canvas app and cloud flow, with the
 * apps and flows that bypass consent, use custom or high-risk connectors, or are
 * shared widely. Optional per tenant: it needs Microsoft's read-only Power Platform
 * reader role, which a Power Platform admin gives Vigil365's app. Until then the page
 * explains how, with the command filled in.
 */
export function PowerPlatformPage() {
  const [data, setData] = useState<PowerPlatformInventory | null>(null);
  const [failed, setFailed] = useState(false);
  const [checking, setChecking] = useState(false);
  const [appSearch, setAppSearch] = useState("");
  const [appFilter, setAppFilter] = useState("");
  const [flowSearch, setFlowSearch] = useState("");
  const [flowFilter, setFlowFilter] = useState("");
  const [environment, setEnvironment] = useState("");

  const load = useCallback((refresh: boolean) => {
    let cancelled = false;
    if (refresh) setChecking(true);
    loadPowerPlatformInventory(refresh)
      .then(d => { if (!cancelled) { setData(d); setFailed(false); } })
      .catch(() => { if (!cancelled) setFailed(true); })
      .finally(() => { if (!cancelled) setChecking(false); });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => load(false), [load]);

  const apps = useMemo(() => {
    const q = appSearch.trim().toLowerCase();
    return (data?.apps ?? []).filter(a =>
      (!environment || a.environmentId === environment) &&
      (!appFilter
        || (appFilter === "flagged" && needsLook(a.flags))
        || (appFilter === "consent" && a.bypassConsent)
        || (appFilter === "highrisk" && a.flags.some(f => f.label.startsWith("High-risk connector")))
        || (appFilter === "shared" && a.flags.some(f => f.label.startsWith("Shared with") && f.severity === "medium"))
        || (appFilter === "custom" && a.flags.some(f => f.label === "Custom connector"))) &&
      (!q || a.name.toLowerCase().includes(q) || (a.owner ?? "").toLowerCase().includes(q) || a.connectors.some(c => c.toLowerCase().includes(q))));
  }, [data, appSearch, appFilter, environment]);

  const flows = useMemo(() => {
    const q = flowSearch.trim().toLowerCase();
    return (data?.flows ?? []).filter(f =>
      (!environment || f.environmentId === environment) &&
      (!flowFilter || (flowFilter === "flagged" ? needsLook(f.flags) : f.state === flowFilter)) &&
      (!q || f.name.toLowerCase().includes(q) || f.connectors.some(c => c.toLowerCase().includes(q))));
  }, [data, flowSearch, flowFilter, environment]);

  if (failed) return <div className="page"><EmptyState message="Could not load the Power Platform inventory: the API request failed. Refresh to retry."/></div>;
  if (data === null) return <div className="page"><LoadingSkeleton type="table"/></div>;
  if (!data.configured) return <div className="page"><EmptyState icon={<Workflow size={28}/>} message="Connect Microsoft Graph in Setup to see Power Platform."/></div>;

  const checkAgain = (
    <button className="btn-export" onClick={() => load(true)} disabled={checking}>
      <RefreshCw size={13} aria-hidden="true"/> {checking ? "Checking…" : "Check again"}
    </button>
  );

  if (data.status === "NotEnabled") return <div className="page"><TurnOn data={data} checkAgain={checkAgain}/></div>;
  if (data.status === "Error") {
    return (
      <div className="page">
        <EmptyState icon={<Workflow size={28}/>} message={`The Power Platform inventory could not be read: ${data.reason ?? "unknown error"}`}/>
        <div className="pp-center">{checkAgain}</div>
      </div>
    );
  }

  const s = data.summary!;
  const envs = data.environments ?? [];
  const problems = envs.filter(e => e.problem);
  const flaggedApps = (data.apps ?? []).filter(a => needsLook(a.flags)).length;
  const flaggedFlows = (data.flows ?? []).filter(f => needsLook(f.flags)).length;
  const dataverseEnvs = envs.filter(e => e.hasDataverse).length;

  return (
    <div className="page">
      {(problems.length > 0 || s.environmentsSkipped > 0) && (
        <div className="notice-warn" role="status">
          <AlertTriangle size={16} aria-hidden="true"/>
          <div>
            <strong>Part of the inventory couldn't be read</strong>
            {s.environmentsSkipped > 0 && <p>{s.environmentsSkipped} environment{s.environmentsSkipped === 1 ? " wasn't" : "s weren't"} read: Vigil365 reads the first {envs.length}.</p>}
            {problems.map(e => <p key={e.id}>{e.name}: {e.problem}.</p>)}
          </div>
        </div>
      )}

      <div className="stat-row-4">
        <StatCard label="Environments" value={s.environments} sub={`${dataverseEnvs} with Dataverse`}/>
        <StatCard label="Apps" value={s.apps} sub={`${s.widelyShared} shared widely`}/>
        <StatCard label="Cloud flows" value={s.flows} sub={`${(data.flows ?? []).filter(f => f.state === "On").length} turned on`}/>
        <StatCard label="Need a look" value={flaggedApps + flaggedFlows} sub={`${s.consentBypassed} bypass consent · ${s.highRiskConnector} use a high-risk connector`}/>
      </div>

      <div className="pp-toolbar">
        <select className="filter-sel" aria-label="Filter by environment" value={environment} onChange={e => setEnvironment(e.target.value)}>
          <option value="">All environments</option>
          {envs.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
        <span className="pp-muted">Read {data.readAt ? new Date(data.readAt).toLocaleString() : ""}</span>
        <button className="btn-export" onClick={() => load(true)} disabled={checking}>
          <RefreshCw size={13} aria-hidden="true"/> {checking ? "Refreshing…" : "Refresh"}
        </button>
      </div>

      <Card title="Apps" badge={<Badge label={`${s.apps} canvas app${s.apps === 1 ? "" : "s"}`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search apps" value={appSearch} onChange={e => setAppSearch(e.target.value)} placeholder="App, owner or connector"/>
            </label>
            <select className="filter-sel" aria-label="Filter apps" value={appFilter} onChange={e => setAppFilter(e.target.value)}>
              <option value="">All apps</option>
              <option value="flagged">Need a look</option>
              <option value="consent">Consent prompt bypassed</option>
              <option value="highrisk">High-risk connector</option>
              <option value="shared">Shared widely</option>
              <option value="custom">Custom connector</option>
            </select>
            <ExportDropdown filename="power-platform-apps.csv" scopeTotal={data.apps?.length}
              rows={apps.map(a => ({ App: a.name, Environment: a.environmentName, Owner: a.owner ?? "", SharedUsers: a.sharedUsers, SharedGroups: a.sharedGroups,
                Connectors: a.connectors.join("; "), Flags: a.flags.map(f => f.label).join("; "), Modified: a.modified ?? "" }))}/>
          </div>
        }>
        {apps.length === 0 ? <EmptyState message={s.apps === 0 ? "No canvas apps in the environments read." : "No apps match these filters."}/> : (
          <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">App</th><th scope="col">Environment</th><th scope="col">Owner</th><th scope="col">Shared with</th><th scope="col">Connectors</th><th scope="col">Flags</th><th scope="col">Modified</th></tr></thead>
                <tbody>
                  {apps.slice(0, ROW_LIMIT).map(a => (
                    <tr key={a.environmentId + a.id}>
                      <td>{a.name}</td>
                      <td className="al-date">{a.environmentName}</td>
                      <td className="al-date">{a.owner ?? "—"}</td>
                      <td className="al-date">{a.sharedUsers} {a.sharedUsers === 1 ? "person" : "people"}{a.sharedGroups ? `, ${a.sharedGroups} group${a.sharedGroups === 1 ? "" : "s"}` : ""}</td>
                      <td className="al-date">{a.connectors.length ? a.connectors.join(", ") : "None"}</td>
                      <td><Flags flags={a.flags}/></td>
                      <td className="al-date">{a.modified ? fmtDay(a.modified) : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Truncated shown={Math.min(apps.length, ROW_LIMIT)} total={apps.length}/>
          </>
        )}
      </Card>

      <Card title="Cloud flows" badge={<Badge label={`${dataverseEnvs} Dataverse environment${dataverseEnvs === 1 ? "" : "s"}`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search flows" value={flowSearch} onChange={e => setFlowSearch(e.target.value)} placeholder="Flow or connector"/>
            </label>
            <select className="filter-sel" aria-label="Filter flows" value={flowFilter} onChange={e => setFlowFilter(e.target.value)}>
              <option value="">All flows</option>
              <option value="flagged">Need a look</option>
              <option value="On">On</option>
              <option value="Off">Off</option>
              <option value="Suspended">Suspended</option>
            </select>
            <ExportDropdown filename="power-platform-flows.csv" scopeTotal={data.flows?.length}
              rows={flows.map(f => ({ Flow: f.name, Environment: f.environmentName, State: f.state, Connectors: f.connectors.join("; "),
                Flags: f.flags.map(x => x.label).join("; "), Modified: f.modified ?? "" }))}/>
          </div>
        }>
        <p className="pp-muted pp-note">Microsoft lists cloud flows only in environments with Dataverse.</p>
        {flows.length === 0 ? <EmptyState message={s.flows === 0 ? "No cloud flows in the Dataverse environments read." : "No flows match these filters."}/> : (
          <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">Flow</th><th scope="col">Environment</th><th scope="col">State</th><th scope="col">Connectors</th><th scope="col">Flags</th><th scope="col">Modified</th></tr></thead>
                <tbody>
                  {flows.slice(0, ROW_LIMIT).map(f => (
                    <tr key={f.environmentId + f.id}>
                      <td>{f.name}</td>
                      <td className="al-date">{f.environmentName}</td>
                      <td><Badge label={f.state} tone={stateTone(f.state)}/></td>
                      <td className="al-date">{f.connectors.length ? f.connectors.join(", ") : "—"}</td>
                      <td><Flags flags={f.flags}/></td>
                      <td className="al-date">{f.modified ? fmtDay(f.modified) : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Truncated shown={Math.min(flows.length, ROW_LIMIT)} total={flows.length}/>
          </>
        )}
      </Card>

      <Card title="Environments" badge={<Badge label={`${s.environmentsRead} of ${s.environments} read`} tone={problems.length || s.environmentsSkipped ? "warning" : "good"}/>}>
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead><tr><th scope="col">Environment</th><th scope="col">Type</th><th scope="col">Region</th><th scope="col">Dataverse</th><th scope="col">Apps</th><th scope="col">Flows</th><th scope="col">Read</th></tr></thead>
            <tbody>
              {envs.map(e => (
                <tr key={e.id}>
                  <td>{e.name}</td>
                  <td className="al-date">{e.type ?? "—"}</td>
                  <td className="al-date">{e.region ?? "—"}</td>
                  <td className="al-date">{e.hasDataverse ? "Yes" : "No"}</td>
                  <td className="al-date">{e.apps}</td>
                  <td className="al-date">{e.hasDataverse ? e.flows : "—"}</td>
                  <td>{e.problem ? <Badge label="Partly" tone="warning"/> : <Badge label="Yes" tone="good"/>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <p className="pp-muted pp-footer">
        Read-only, through Microsoft's Power Platform reader role. To turn this off, run{" "}
        <code>{data.setup?.script} … -Remove</code>.
      </p>
    </div>
  );
}

function TurnOn({ data, checkAgain }: { data: PowerPlatformInventory; checkAgain: React.ReactNode }) {
  const setup = data.setup;
  return (
    <Card title="Power Platform inventory is off for this tenant" badge={<Badge label="Optional" tone="neutral"/>}>
      <div className="pp-turnon">
        <p>
          Turn it on to list every Power Platform environment, canvas app and cloud flow, and to flag the apps and flows
          that bypass the consent prompt, use custom or high-risk connectors, or are shared with many people.
        </p>
        <p className="pp-why">
          <ShieldCheck size={16} aria-hidden="true"/>
          <span>
            It needs one thing Microsoft Graph consent doesn't cover: a Power Platform administrator gives Vigil365's app
            Microsoft's read-only <strong>{setup?.role ?? "Power Platform reader"}</strong> role. No new Graph permission, no new
            consent. The role is in preview at Microsoft.
          </span>
        </p>
        {data.reason === "ApiNotInTenant" && (
          <p className="pp-muted">This tenant hasn't registered the Power Platform API yet. The script registers it.</p>
        )}
        {data.reason === "RoleMissing" && (
          <p className="pp-muted">Vigil365's app has no Power Platform role in this tenant yet.</p>
        )}
        <ol className="pp-steps">
          <li>Install the Az.Accounts PowerShell module if you don't have it (<code>Install-Module Az.Accounts -Scope CurrentUser</code>).</li>
          <li>
            Run this, as a Power Platform administrator of this tenant:
            {setup && (
              <div className="pp-command">
                <code>{setup.command}</code>
                <CopyButton value={setup.command} label="command"/>
              </div>
            )}
          </li>
          <li>Come back and select <strong>Check again</strong>. A new role can take a few minutes to apply.</li>
        </ol>
        <div className="pp-actions">
          {checkAgain}
          <a className="pp-link" href={GRANT_SCRIPT_URL} target="_blank" rel="noopener noreferrer">The script <ExternalLink size={12} aria-hidden="true"/></a>
          <a className="pp-link" href={RBAC_DOCS_URL} target="_blank" rel="noopener noreferrer">Microsoft's guide <ExternalLink size={12} aria-hidden="true"/></a>
        </div>
      </div>
    </Card>
  );
}
