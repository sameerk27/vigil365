import React, { useCallback, useEffect, useMemo, useState } from "react";
import { Search, Workflow, AlertTriangle, RefreshCw, ShieldCheck, ExternalLink, LogIn } from "lucide-react";
import { fmtDay } from "../services/utils";
import { Card, Badge, EmptyState, LoadingSkeleton, ExportDropdown } from "../components/SharedComponents";
import { StatCard } from "../components/ui";
import type { Tone } from "../services/types";
import {
  loadPowerPlatformSettings, readPowerPlatform, registerPowerPlatformApi, cachedInventory, needsLook, connectorName,
  PowerPlatformReadError, RBAC_DOCS_URL,
  type PowerPlatformSettings, type PowerPlatformInventory, type PowerPlatformFlag,
} from "../services/powerPlatform";

/** Rows rendered per table; search, the filters and export reach the rest. */
const ROW_LIMIT = 500;

const flagTone = (f: PowerPlatformFlag): Tone => (f.severity === "high" ? "error" : f.severity === "medium" ? "warning" : "neutral");
const has = (flags: PowerPlatformFlag[], label: string) => flags.some(f => f.label === label);
const hasPrefix = (flags: PowerPlatformFlag[], prefix: string) => flags.some(f => f.label.startsWith(prefix));
const ownerGone = (flags: PowerPlatformFlag[]) => flags.some(f => / (not in the directory|account is disabled)$/.test(f.label));

function Flags({ flags }: { flags: PowerPlatformFlag[] }) {
  if (flags.length === 0) return <span className="pp-muted">None</span>;
  return <span className="pp-flags">{flags.map(f => <Badge key={f.label} label={f.label} tone={flagTone(f)}/>)}</span>;
}

function Truncated({ shown, total }: { shown: number; total: number }) {
  return shown < total ? <p className="pp-muted pp-truncated">Showing {shown} of {total}. Search, filter or export to see the rest.</p> : null;
}

const ownerText = (owner: string | null, ownerId: string | null) => owner ?? (ownerId ? "Unknown user" : "—");
const connectorsText = (c: string[]) => (c.length ? c.map(connectorName).join(", ") : "None");
const sharedText = (users: number, groups: number) =>
  `${users} ${users === 1 ? "person" : "people"}${groups ? `, ${groups} group${groups === 1 ? "" : "s"}` : ""}`;

type Step = "idle" | "reading" | "needsRegister" | "registering" | "registered";

/**
 * Power Platform inventory: environments, canvas apps, cloud flows and Copilot Studio
 * agents, with the ones that need a look flagged. Read in the browser with the viewer's
 * own Microsoft sign-in (Microsoft won't let Vigil365's app read it without Power Platform
 * Administrator rights, which Vigil365 doesn't take). Kept in memory while the app is open.
 */
export function PowerPlatformPage() {
  const [settings, setSettings] = useState<PowerPlatformSettings | null>(null);
  const [failed, setFailed] = useState(false);
  const [inv, setInv] = useState<PowerPlatformInventory | null>(null);
  const [step, setStep] = useState<Step>("idle");
  const [trying, setTrying] = useState(false);
  const [progress, setProgress] = useState("");
  const [problem, setProblem] = useState<string | null>(null);
  const [closed, setClosed] = useState(false);

  const [environment, setEnvironment] = useState("");
  const [agentSearch, setAgentSearch] = useState("");
  const [agentFilter, setAgentFilter] = useState("");
  const [appSearch, setAppSearch] = useState("");
  const [appFilter, setAppFilter] = useState("");
  const [flowSearch, setFlowSearch] = useState("");
  const [flowFilter, setFlowFilter] = useState("");

  // Settings, then the read kept from earlier in this session, or a read without a popup
  // when the person already consented (same tenant).
  useEffect(() => {
    let cancelled = false;
    loadPowerPlatformSettings()
      .then(async s => {
        if (cancelled) return;
        setSettings(s);
        if (!s.configured || !s.tenantId) return;
        const cached = cachedInventory(s.tenantId);
        if (cached) { setInv(cached); return; }
        setTrying(true);
        try {
          const result = await readPowerPlatform(s, false, t => { if (!cancelled) setProgress(t); });
          if (!cancelled && result) setInv(result);
        } catch { /* fall back to the button */ }
        finally { if (!cancelled) { setTrying(false); setProgress(""); } }
      })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, []);

  // Straight from the click, so the browser lets the Microsoft popup open.
  const read = useCallback(async () => {
    if (!settings) return;
    setProblem(null);
    setClosed(false);
    setStep("reading");
    try {
      const result = await readPowerPlatform(settings, true, setProgress);
      if (result) setInv(result);
      setStep("idle");
    } catch (e) {
      const err = e instanceof PowerPlatformReadError ? e : null;
      setStep(err?.problem === "ApiNotInTenant" ? "needsRegister" : "idle");
      if (err?.problem === "Cancelled") setClosed(true);
      else setProblem(e instanceof Error ? e.message : String(e));
    } finally { setProgress(""); }
  }, [settings]);

  const register = useCallback(async () => {
    if (!settings) return;
    setProblem(null);
    setClosed(false);
    setStep("registering");
    try {
      await registerPowerPlatformApi(settings);
      setStep("registered");
    } catch (e) {
      setStep("needsRegister");
      if (e instanceof PowerPlatformReadError && e.problem === "Cancelled") setClosed(true);
      else setProblem(e instanceof Error ? e.message : String(e));
    }
  }, [settings]);

  const agents = useMemo(() => {
    const q = agentSearch.trim().toLowerCase();
    return (inv?.agents ?? []).filter(a =>
      (!environment || a.environmentId === environment) &&
      (!agentFilter
        || (agentFilter === "flagged" && needsLook(a.flags))
        || (agentFilter === "nosignin" && (a.authentication ?? "").toLowerCase() === "none")
        || (agentFilter === "tenant" && a.sharedWithTenant)
        || (agentFilter === "published" && !!a.published)
        || (agentFilter === "draft" && !a.published)) &&
      (!q || a.name.toLowerCase().includes(q) || (a.owner ?? "").toLowerCase().includes(q) || a.connectors.some(c => c.toLowerCase().includes(q))));
  }, [inv, agentSearch, agentFilter, environment]);

  const apps = useMemo(() => {
    const q = appSearch.trim().toLowerCase();
    return (inv?.apps ?? []).filter(a =>
      (!environment || a.environmentId === environment) &&
      (!appFilter
        || (appFilter === "flagged" && needsLook(a.flags))
        || (appFilter === "consent" && a.bypassConsent === true)
        || (appFilter === "highrisk" && hasPrefix(a.flags, "High-risk connector"))
        || (appFilter === "shared" && a.flags.some(f => f.label.startsWith("Shared with") && f.severity === "medium"))
        || (appFilter === "custom" && has(a.flags, "Custom connector"))
        || (appFilter === "owner" && ownerGone(a.flags))) &&
      (!q || a.name.toLowerCase().includes(q) || (a.owner ?? "").toLowerCase().includes(q) || a.connectors.some(c => c.toLowerCase().includes(q))));
  }, [inv, appSearch, appFilter, environment]);

  const flows = useMemo(() => {
    const q = flowSearch.trim().toLowerCase();
    return (inv?.flows ?? []).filter(f =>
      (!environment || f.environmentId === environment) &&
      (!flowFilter
        || (flowFilter === "flagged" && needsLook(f.flags))
        || (flowFilter === "highrisk" && hasPrefix(f.flags, "High-risk connector"))
        || (flowFilter === "owner" && ownerGone(f.flags))) &&
      (!q || f.name.toLowerCase().includes(q) || (f.owner ?? "").toLowerCase().includes(q) || f.connectors.some(c => c.toLowerCase().includes(q))));
  }, [inv, flowSearch, flowFilter, environment]);

  if (failed) return <div className="page"><EmptyState message="Could not load the Power Platform page: the API request failed. Refresh to retry."/></div>;
  if (settings === null || trying) {
    return <div className="page">{progress ? <p className="pp-muted pp-note">{progress}</p> : null}<LoadingSkeleton type="table"/></div>;
  }
  if (!settings.configured) return <div className="page"><EmptyState icon={<Workflow size={28}/>} message="Connect Microsoft Graph in Setup to see Power Platform."/></div>;

  const busy = step === "reading" || step === "registering";
  const feedback = (
    <>
      {progress && <p className="pp-progress" role="status"><RefreshCw size={14} aria-hidden="true" className="pp-spin"/> {progress}</p>}
      {problem && <p className="pp-error" role="alert">{problem}</p>}
      {closed && !problem && (
        <p className="pp-muted">
          The sign-in window was closed. If Microsoft said the request needs admin approval, a Global Administrator or
          Cloud Application Administrator has to approve Vigil365's request first.
        </p>
      )}
    </>
  );

  if (!inv) {
    return (
      <div className="page">
        <Card title="Read Power Platform with your Microsoft sign-in" badge={<Badge label="On demand" tone="neutral"/>}>
          <div className="pp-turnon">
            <p>
              See every Power Platform environment, canvas app, cloud flow and Copilot Studio agent, with the ones that need a look
              flagged: agents anyone can use without signing in or shared with the whole organisation, apps that skip the consent
              prompt or are shared widely, high-risk connectors, and owners who have left.
            </p>
            <p className="pp-why">
              <ShieldCheck size={16} aria-hidden="true"/>
              <span>
                Vigil365 reads it with <strong>your</strong> access, in this browser: a Microsoft sign-in window opens, and your
                account needs one of Global Administrator, Power Platform Administrator, Dynamics 365 Administrator or Global
                Reader. Vigil365's app gets no Power Platform access, and nothing is stored.
              </span>
            </p>
            {(step === "needsRegister" || step === "registering") ? (
              <div className="pp-step">
                <strong>First, register Microsoft's Power Platform API in this tenant</strong>
                <p className="pp-muted">
                  This tenant doesn't have it yet, so nobody can sign in to it. Sign in as an Application Administrator or Cloud
                  Application Administrator and accept the permission request.
                </p>
                <button className="btn-run" onClick={register} disabled={busy}>
                  <LogIn size={14} aria-hidden="true"/> {step === "registering" ? "Waiting for Microsoft sign-in…" : "Register the Power Platform API"}
                </button>
              </div>
            ) : (
              <div className="pp-step">
                {step === "registered" && <p className="pp-ok">Power Platform API registered. Now read the inventory.</p>}
                <button className="btn-run" onClick={read} disabled={busy}>
                  <LogIn size={14} aria-hidden="true"/> {step === "reading" ? "Reading…" : "Read with Microsoft sign-in"}
                </button>
              </div>
            )}
            {feedback}
            <div className="pp-actions">
              <a className="pp-link" href={RBAC_DOCS_URL} target="_blank" rel="noopener noreferrer">Who can read the inventory <ExternalLink size={12} aria-hidden="true"/></a>
            </div>
          </div>
        </Card>
      </div>
    );
  }

  const s = inv.summary;
  const envs = inv.environments;
  const agentsPublished = inv.agents.filter(a => a.published).length;

  return (
    <div className="page">
      {inv.notes.length > 0 && (
        <div className="notice-warn" role="status">
          <AlertTriangle size={16} aria-hidden="true"/>
          <div>
            <strong>Part of the inventory couldn't be read</strong>
            {inv.notes.map(n => <p key={n}>{n}</p>)}
          </div>
        </div>
      )}

      <div className="stat-row-5">
        <StatCard label="Environments" value={s.environments} sub={`${envs.filter(e => e.isManaged).length} managed`}/>
        <StatCard label="Copilot agents" value={s.agents} sub={`${agentsPublished} published · ${s.agentsWithoutSignIn} without sign-in`}/>
        <StatCard label="Canvas apps" value={s.apps} sub={inv.appDetails ? `${s.consentBypassed} bypass consent` : "sharing detail not read"}/>
        <StatCard label="Cloud flows" value={s.flows} sub={`${inv.flows.filter(f => hasPrefix(f.flags, "High-risk connector")).length} with a high-risk connector`}/>
        <StatCard label="Need a look" value={s.needALook} sub={`${s.ownerGone} with an owner who left or is disabled`}/>
      </div>

      <div className="pp-toolbar">
        <select className="filter-sel" aria-label="Filter by environment" value={environment} onChange={e => setEnvironment(e.target.value)}>
          <option value="">All environments</option>
          {envs.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
        <span className="pp-muted">Read {new Date(inv.readAt).toLocaleString()} with your sign-in</span>
        <button className="btn-export" onClick={read} disabled={busy}>
          <RefreshCw size={13} aria-hidden="true"/> {step === "reading" ? "Reading…" : "Read again"}
        </button>
      </div>
      {feedback}

      <Card title="Copilot Studio agents" badge={<Badge label={`${s.agents} agent${s.agents === 1 ? "" : "s"}`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search agents" value={agentSearch} onChange={e => setAgentSearch(e.target.value)} placeholder="Agent, owner or connector"/>
            </label>
            <select className="filter-sel" aria-label="Filter agents" value={agentFilter} onChange={e => setAgentFilter(e.target.value)}>
              <option value="">All agents</option>
              <option value="flagged">Need a look</option>
              <option value="nosignin">No sign-in required</option>
              <option value="tenant">Shared with the whole organisation</option>
              <option value="published">Published</option>
              <option value="draft">Draft</option>
            </select>
            <ExportDropdown filename="copilot-studio-agents.csv" scopeTotal={inv.agents.length}
              rows={agents.map(a => ({ Agent: a.name, Environment: a.environmentName, Owner: ownerText(a.owner, a.ownerId), Published: a.published ?? "",
                SignIn: a.authentication ?? "", SharedWithOrganisation: a.sharedWithTenant, SharedUsers: a.sharedUsers, SharedGroups: a.sharedGroups,
                Channels: a.channels.join("; "), Connectors: a.connectors.map(connectorName).join("; "), Flags: a.flags.map(f => f.label).join("; ") }))}/>
          </div>
        }>
        {agents.length === 0 ? <EmptyState message={s.agents === 0 ? "No Copilot Studio agents in the inventory." : "No agents match these filters."}/> : (
          <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">Agent</th><th scope="col">Environment</th><th scope="col">Owner</th><th scope="col">Published</th><th scope="col">Sign-in</th><th scope="col">Shared with</th><th scope="col">Flags</th></tr></thead>
                <tbody>
                  {agents.slice(0, ROW_LIMIT).map(a => (
                    <tr key={a.environmentId + a.id}>
                      <td>{a.name}{a.channels.length ? <span className="copilot-upn">{a.channels.join(", ")}</span> : null}</td>
                      <td className="al-date">{a.environmentName}</td>
                      <td className="al-date">{ownerText(a.owner, a.ownerId)}</td>
                      <td className="al-date">{a.published ? fmtDay(a.published) : "Draft"}</td>
                      <td className="al-date">{a.authentication ?? "—"}</td>
                      <td className="al-date">{a.sharedWithTenant ? "Everyone" : sharedText(a.sharedUsers, a.sharedGroups)}</td>
                      <td><Flags flags={a.flags}/></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Truncated shown={Math.min(agents.length, ROW_LIMIT)} total={agents.length}/>
          </>
        )}
      </Card>

      <Card title="Canvas apps" badge={<Badge label={`${s.apps} app${s.apps === 1 ? "" : "s"}`} tone="neutral"/>}
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
              <option value="owner">Owner left or disabled</option>
            </select>
            <ExportDropdown filename="power-platform-apps.csv" scopeTotal={inv.apps.length}
              rows={apps.map(a => ({ App: a.name, Environment: a.environmentName, Owner: ownerText(a.owner, a.ownerId), SharedUsers: a.sharedUsers ?? "", SharedGroups: a.sharedGroups ?? "",
                Connectors: a.connectors.map(connectorName).join("; "), Flags: a.flags.map(f => f.label).join("; "), Modified: a.modified ?? "" }))}/>
          </div>
        }>
        {apps.length === 0 ? <EmptyState message={s.apps === 0 ? "No canvas apps in the inventory." : "No apps match these filters."}/> : (
          <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">App</th><th scope="col">Environment</th><th scope="col">Owner</th><th scope="col">Shared with</th><th scope="col">Connectors</th><th scope="col">Flags</th><th scope="col">Modified</th></tr></thead>
                <tbody>
                  {apps.slice(0, ROW_LIMIT).map(a => (
                    <tr key={a.environmentId + a.id}>
                      <td>{a.name}</td>
                      <td className="al-date">{a.environmentName}</td>
                      <td className="al-date">{ownerText(a.owner, a.ownerId)}</td>
                      <td className="al-date">{a.sharedUsers === null ? "—" : sharedText(a.sharedUsers, a.sharedGroups ?? 0)}</td>
                      <td className="al-date">{connectorsText(a.connectors)}</td>
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

      <Card title="Cloud flows" badge={<Badge label={`${s.flows} flow${s.flows === 1 ? "" : "s"}`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14}/>
              <input className="search-input" aria-label="Search flows" value={flowSearch} onChange={e => setFlowSearch(e.target.value)} placeholder="Flow, creator or connector"/>
            </label>
            <select className="filter-sel" aria-label="Filter flows" value={flowFilter} onChange={e => setFlowFilter(e.target.value)}>
              <option value="">All flows</option>
              <option value="flagged">Need a look</option>
              <option value="highrisk">High-risk connector</option>
              <option value="owner">Creator left or disabled</option>
            </select>
            <ExportDropdown filename="power-platform-flows.csv" scopeTotal={inv.flows.length}
              rows={flows.map(f => ({ Flow: f.name, Environment: f.environmentName, CreatedBy: ownerText(f.owner, f.ownerId), Trigger: f.trigger ?? "",
                Connectors: f.connectors.map(connectorName).join("; "), Flags: f.flags.map(x => x.label).join("; "), Modified: f.modified ?? "" }))}/>
          </div>
        }>
        <p className="pp-muted pp-note">Microsoft lists the connectors a flow uses, not its built-in actions: a flow calling a web service through the built-in HTTP action shows no connector for it.</p>
        {flows.length === 0 ? <EmptyState message={s.flows === 0 ? "No cloud flows in the inventory." : "No flows match these filters."}/> : (
          <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">Flow</th><th scope="col">Environment</th><th scope="col">Created by</th><th scope="col">Trigger</th><th scope="col">Connectors</th><th scope="col">Flags</th><th scope="col">Modified</th></tr></thead>
                <tbody>
                  {flows.slice(0, ROW_LIMIT).map(f => (
                    <tr key={f.environmentId + f.id}>
                      <td>{f.name}</td>
                      <td className="al-date">{f.environmentName}</td>
                      <td className="al-date">{ownerText(f.owner, f.ownerId)}</td>
                      <td className="al-date">{f.trigger ?? "—"}</td>
                      <td className="al-date">{connectorsText(f.connectors)}</td>
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

      <Card title="Environments" badge={<Badge label={`${s.environments}`} tone="neutral"/>}>
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead><tr><th scope="col">Environment</th><th scope="col">Type</th><th scope="col">Region</th><th scope="col">Managed</th><th scope="col">Agents</th><th scope="col">Apps</th><th scope="col">Flows</th></tr></thead>
            <tbody>
              {envs.map(e => (
                <tr key={e.id}>
                  <td>{e.name}</td>
                  <td className="al-date">{e.type ?? "—"}</td>
                  <td className="al-date">{e.region ?? "—"}</td>
                  <td className="al-date">{e.isManaged === null ? "—" : e.isManaged ? "Yes" : "No"}</td>
                  <td className="al-date">{e.agents}</td>
                  <td className="al-date">{e.apps}</td>
                  <td className="al-date">{e.flows}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <div className="pp-footer">
        <p className="pp-muted">Read with your own Microsoft sign-in. Vigil365's app has no Power Platform access, and the read isn't stored.</p>
      </div>
    </div>
  );
}
