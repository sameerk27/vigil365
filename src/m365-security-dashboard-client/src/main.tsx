import React, { useEffect, useState, useMemo, useCallback, useRef } from "react";
import { createRoot } from "react-dom/client";
import { PublicClientApplication, type AccountInfo, type Configuration } from "@azure/msal-browser";
import {
  Home, Users, Monitor, Mail, AlertTriangle, Bell, CheckSquare, Activity, Wifi,
  Package, ShieldCheck, BookOpen, MapPin, UserCheck, Settings, ChevronRight, ChevronLeft,
  Clock, RefreshCw, Sun, Moon, LogIn, ShieldAlert, Shield, UserX, TrendingUp, Lightbulb
} from "lucide-react";
import "./styles.css";

// Import types & services
import {
  NavPage, AppRole, AuthInfo, SecurityAlert, Overview, SecureScore, IdentityData,
  DevicesData, ServiceHealthData, LicenseData, InactiveUsersData, PasswordExpiryData,
  ConditionalAccessData, AuditLogData, SignInLocationsData, DefenderAlertsData,
  SecurityIncidentsData, PrivilegedRolesData, DlpAlertsData, MdeVulnerabilitiesData,
  PimData, EmailProtectionData, MdiAlertsData, McasAlertsData, InsiderRiskData,
  RiskDetectionsData, IdentityHealthData, AttackSimulationData, AlertPolicy, TriggeredAlert,
  PurviewData
} from "./services/types";
import { apiBase, apiFetch, AuthContext, initMsal, acApi, AUTO_REFRESH_SEC, useAuth, registerNavHandler } from "./services/api";
import { showToast } from "./services/toast";
import { ToastContainer } from "./components/ToastContainer";
import { Badge, AlertDetailModal, DashboardSkeleton } from "./components/SharedComponents";
import { fmtDate, fmtCountdown } from "./services/utils";

// Import pages
import { OverviewPage } from "./pages/OverviewPage";
import { RecommendationsPage } from "./pages/RecommendationsPage";
import { IdentityPage } from "./pages/IdentityPage";
import { DevicesPage } from "./pages/DevicesPage";
import { EmailPage } from "./pages/EmailPage";
import { IncidentsPage } from "./pages/IncidentsPage";
import { AlertCenterPage } from "./pages/AlertCenterPage";
import { CompliancePage } from "./pages/CompliancePage";
import { ServiceHealthPage } from "./pages/ServiceHealthPage";
import { NetworkPage } from "./pages/NetworkPage";
import { LicensesPage } from "./pages/LicensesPage";
import { ConditionalAccessPage } from "./pages/ConditionalAccessPage";
import { AuditLogPage } from "./pages/AuditLogPage";
import { SignInLocationsPage } from "./pages/SignInLocationsPage";
import { UserManagementPage } from "./pages/UserManagementPage";
import { SetupPage } from "./pages/SetupPage";
import { TrendsPage } from "./pages/TrendsPage";

// App version — surfaced in the sidebar so the running build is always identifiable.
export const APP_VERSION = "1.0.0";

// ─── MSAL instances local to bootstrapping ─────────────────────────────────────
let _msalInstance: PublicClientApplication | null = null;
let _msalScopes: string[] = [];

// ─── Sidebar ───────────────────────────────────────────────────────────────────
const NAV: { id: NavPage; label: string; icon: React.ReactNode; group?: string; adminOnly?: boolean }[] = [
  { id:"overview",         label:"Overview",             icon:<Home size={17}/> },
  { id:"recommendations",  label:"Recommendations",      icon:<Lightbulb size={17}/> },
  { id:"trends",           label:"Trends & History",     icon:<TrendingUp size={17}/> },
  { id:"identity",         label:"Identity",             icon:<Users size={17}/> },
  { id:"devices",          label:"Devices",              icon:<Monitor size={17}/> },
  { id:"email",            label:"Email",                icon:<Mail size={17}/> },
  { id:"incidents",        label:"Incidents & Alerts",   icon:<AlertTriangle size={17}/> },
  { id:"alertcenter",      label:"Alert Center",         icon:<Bell size={17}/> },
  { id:"compliance",       label:"Compliance",           icon:<CheckSquare size={17}/> },
  { id:"servicehealth",    label:"Service Health",       icon:<Activity size={17}/> },
  { id:"network",          label:"M365 Connectivity",    icon:<Wifi size={17}/> },
  { id:"licenses",         label:"Licenses & Users",     icon:<Package size={17}/>, group:"Enterprise" },
  { id:"conditionalaccess",label:"Conditional Access",   icon:<ShieldCheck size={17}/>, group:"Enterprise" },
  { id:"auditlog",         label:"Audit Log",            icon:<BookOpen size={17}/>, group:"Enterprise" },
  { id:"signinmap",        label:"Sign-in Locations",    icon:<MapPin size={17}/>, group:"Enterprise" },
  { id:"users",            label:"User Management",      icon:<UserCheck size={17}/>, group:"Administration", adminOnly:true },
  { id:"setup",            label:"Setup",                icon:<Settings size={17}/>, group:"Administration", adminOnly:true },
];

function Sidebar({ page, setPage, alertCounts, collapsed, onToggleCollapse }: {
  page:NavPage; setPage:(p:NavPage)=>void; alertCounts: Record<string,number>;
  collapsed: boolean; onToggleCollapse: () => void;
}) {
  const { isAdmin } = useAuth();
  const items = NAV.filter(n => !n.adminOnly || isAdmin);
  return (
    <aside className={`sidebar ${collapsed ? "" : "expanded"}`}>
      <div className="sb-logo">
        <div className="sb-mark">
          <svg width="22" height="22" viewBox="0 0 22 22" fill="none" xmlns="http://www.w3.org/2000/svg">
            <path d="M11 2L3 5.5V11C3 15.1 6.4 18.9 11 20C15.6 18.9 19 15.1 19 11V5.5L11 2Z" fill="#3b82f6" stroke="#60a5fa" strokeWidth="0.8"/>
            <path d="M5.5 11.5 L7.5 11.5 L9 9 L11 14 L13 10 L14.5 11.5 L16.5 11.5" stroke="white" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" fill="none"/>
          </svg>
        </div>
        {!collapsed && (
          <div>
            <div className="sb-name">Vigil365</div>
            <div className="sb-sub">M365 Security Operations</div>
          </div>
        )}
      </div>
      <nav className="sb-nav">
        {items.map((n,i)=>(
          <React.Fragment key={n.id}>
            {!collapsed && n.group && (i===0 || items[i-1].group!==n.group) && (
              <div className="nav-group-label">{n.group}</div>
            )}
            <button
              className={`nav-item ${page===n.id?"nav-active":""}`}
              onClick={()=>setPage(n.id)}
              aria-label={n.label}
              aria-current={page===n.id ? "page" : undefined}
              title={collapsed ? n.label : undefined}
            >
              {n.icon}
              {!collapsed && <span>{n.label}</span>}
              {!collapsed && (alertCounts as Record<string,number>)[n.id]>0 && (
                <span className="nav-badge">{(alertCounts as Record<string,number>)[n.id]}</span>
              )}
              {collapsed && <span className="nav-tooltip">{n.label}{(alertCounts[n.id]??0)>0?` (${alertCounts[n.id]})`:"" }</span>}
            </button>
          </React.Fragment>
        ))}
      </nav>
      {!collapsed && <div className="sb-version">Vigil365 v{APP_VERSION}</div>}
      <button
        className="sb-collapse-btn"
        onClick={onToggleCollapse}
        aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
      >
        {collapsed ? <ChevronRight size={14}/> : <ChevronLeft size={14}/>}
      </button>
    </aside>
  );
}

// ─── Main App Shell ────────────────────────────────────────────────────────────
function App({ account, onSignOut }: { account?: AccountInfo | null; onSignOut?: () => void }) {
  const auth = useAuth();
  const [page, setPage] = useState<NavPage>("overview");
  const [darkMode, setDarkMode] = useState(() => localStorage.getItem("m365-theme") === "dark");
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false); // false = expanded

  // Track the alert count at the time the user last visited each page.
  // Badge = current count − seen count (only new items show as unread).
  const [seenCounts, setSeenCounts] = useState<Record<string,number>>(() => {
    try { return JSON.parse(localStorage.getItem('m365-seen') ?? '{}'); } catch { return {}; }
  });

  // Apply dark mode to document root
  useEffect(() => {
    document.documentElement.classList.toggle("dark", darkMode);
    localStorage.setItem("m365-theme", darkMode ? "dark" : "light");
  }, [darkMode]);

  // Allow pages to deep-link into one another (e.g. Alert Center → Identity by UPN)
  useEffect(() => registerNavHandler(({ page }) => setPage(page as NavPage)), []);

  const [overview, setOverview] = useState<Overview|null>(null);
  const [secureScore, setSecureScore] = useState<SecureScore|null>(null);
  const [identity, setIdentity] = useState<IdentityData|null>(null);
  const [devices, setDevices] = useState<DevicesData|null>(null);
  const [serviceHealth, setServiceHealth] = useState<ServiceHealthData|null>(null);
  const [allAlerts, setAllAlerts] = useState<SecurityAlert[]>([]);
  const [licenses, setLicenses] = useState<LicenseData|null>(null);
  const [inactiveUsers, setInactiveUsers] = useState<InactiveUsersData|null>(null);
  const [passwordExpiry, setPasswordExpiry] = useState<PasswordExpiryData|null>(null);
  const [conditionalAccess, setConditionalAccess] = useState<ConditionalAccessData|null>(null);
  const [auditLog, setAuditLog] = useState<AuditLogData|null>(null);
  const [signInLocations, setSignInLocations] = useState<SignInLocationsData|null>(null);
  const [defenderAlerts, setDefenderAlerts] = useState<DefenderAlertsData|null>(null);
  const [privilegedRoles, setPrivilegedRoles] = useState<PrivilegedRolesData|null>(null);
  const [dlpAlerts, setDlpAlerts] = useState<DlpAlertsData|null>(null);
  const [mdeVulnerabilities, setMdeVulnerabilities] = useState<MdeVulnerabilitiesData|null>(null);
  const [pimData, setPimData] = useState<PimData|null>(null);
  const [emailProtection, setEmailProtection] = useState<EmailProtectionData|null>(null);
  const [purview, setPurview] = useState<PurviewData|null>(null);
  const [securityIncidents, setSecurityIncidents] = useState<SecurityIncidentsData|null>(null);
  const [mdiAlerts, setMdiAlerts] = useState<MdiAlertsData|null>(null);
  const [mcasAlerts, setMcasAlerts] = useState<McasAlertsData|null>(null);
  const [insiderRisk, setInsiderRisk] = useState<InsiderRiskData|null>(null);
  const [riskDetections, setRiskDetections] = useState<RiskDetectionsData|null>(null);
  const [identityHealth, setIdentityHealth] = useState<IdentityHealthData|null>(null);
  const [attackSimulation, setAttackSimulation] = useState<AttackSimulationData|null>(null);
  const [alertPolicies, setAlertPolicies] = useState<AlertPolicy[]>([]);
  const [triggeredAlerts, setTriggeredAlerts] = useState<TriggeredAlert[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [lastRefresh, setLastRefresh] = useState(new Date());
  const [running, setRunning] = useState(false);
  const [selectedAlert, setSelectedAlert] = useState<SecurityAlert|null>(null);
  const [countdown, setCountdown] = useState(AUTO_REFRESH_SEC);
  const [refreshKey, setRefreshKey] = useState(0);
  const abortRef = useRef<AbortController|null>(null);

  const load = useCallback(async () => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;

    setLoading(true); setError("");

    const safeJson = (r: Response) => r.ok ? r.json() : Promise.resolve(null);
    const sig = ctrl.signal;

    // Helper: fetch one endpoint and update state immediately on resolve
    // Each request has a 20-second timeout so slow Graph calls don't block the loading bar
    const fetchOne = <T,>(url: string, setter: (v: T) => void, transform?: (v: T) => T) => {
      const timeoutSig = AbortSignal.timeout(20_000);
      const combinedSig = (AbortSignal as { any?: (sigs: AbortSignal[]) => AbortSignal }).any
        ? (AbortSignal as { any: (sigs: AbortSignal[]) => AbortSignal }).any([sig, timeoutSig])
        : sig;
      return apiFetch(url, { signal: combinedSig })
        .then(safeJson)
        .then((v: T) => {
          if (!ctrl.signal.aborted && v != null)
            setter(transform ? transform(v) : v);
        })
        .catch(() => { /* individual failure or timeout — silently skip */ });
    };

    try {
      // Fire all requests simultaneously; each updates state as it resolves
      await Promise.allSettled([
        fetchOne(`${apiBase}/api/dashboard/overview`, setOverview),
        fetchOne(`${apiBase}/api/dashboard/securescore`, setSecureScore),
        fetchOne(`${apiBase}/api/dashboard/identity`, setIdentity),
        fetchOne(`${apiBase}/api/dashboard/devices`, setDevices),
        fetchOne(`${apiBase}/api/dashboard/servicehealth`, setServiceHealth),
        fetchOne<{items: SecurityAlert[]}>(`${apiBase}/api/alerts?page=1&pageSize=200&resolved=false`, v => setAllAlerts(v.items ?? [])),
        fetchOne(`${apiBase}/api/dashboard/licenses`, setLicenses),
        fetchOne(`${apiBase}/api/dashboard/inactive-users`, setInactiveUsers),
        fetchOne(`${apiBase}/api/dashboard/password-expiry`, setPasswordExpiry),
        fetchOne(`${apiBase}/api/dashboard/conditional-access`, setConditionalAccess),
        fetchOne(`${apiBase}/api/dashboard/audit-log`, setAuditLog),
        fetchOne(`${apiBase}/api/dashboard/signin-locations`, setSignInLocations),
        fetchOne(`${apiBase}/api/dashboard/defender-alerts`, setDefenderAlerts),
        fetchOne(`${apiBase}/api/dashboard/security-incidents`, setSecurityIncidents),
        fetchOne(`${apiBase}/api/dashboard/privileged-roles`, setPrivilegedRoles),
        fetchOne(`${apiBase}/api/dashboard/dlp-alerts`, setDlpAlerts),
        fetchOne(`${apiBase}/api/dashboard/mde-vulnerabilities`, setMdeVulnerabilities),
        fetchOne(`${apiBase}/api/dashboard/pim`, setPimData),
        fetchOne(`${apiBase}/api/dashboard/email-protection`, setEmailProtection),
        fetchOne(`${apiBase}/api/dashboard/purview`, setPurview),
        fetchOne(`${apiBase}/api/dashboard/mdi-alerts`, setMdiAlerts),
        fetchOne(`${apiBase}/api/dashboard/mcas-alerts`, setMcasAlerts),
        fetchOne(`${apiBase}/api/dashboard/insider-risk`, setInsiderRisk),
        fetchOne(`${apiBase}/api/dashboard/risk-detections`, setRiskDetections),
        fetchOne(`${apiBase}/api/dashboard/identity-health`, setIdentityHealth),
        fetchOne(`${apiBase}/api/dashboard/attack-simulation`, setAttackSimulation),
      ]);
      if (ctrl.signal.aborted) return;
      setLastRefresh(new Date());
      setCountdown(AUTO_REFRESH_SEC);
    } catch(e: unknown) {
      if (e instanceof Error && e.name !== "AbortError")
        setError("Failed to load dashboard data. Is the API running?");
    } finally {
      if (!ctrl.signal.aborted) setLoading(false);
    }
  }, []);

  const runCollection = useCallback(async () => {
    setRunning(true);
    try {
      const res = await apiFetch(`${apiBase}/api/collector/run`, { method: "POST" });
      if (!res.ok) throw new Error(await res.text());
      setRefreshKey(k => k + 1);
    } catch(e: unknown) {
      let msg = e instanceof Error ? e.message : "Collection failed";
      try { const p = JSON.parse(msg); if (p?.error) msg = p.error; } catch { /* not JSON */ }
      setError(msg);
    } finally { setRunning(false); }
  }, []);

  // Initial load + re-fetch whenever refreshKey increments
  useEffect(() => { load(); return () => abortRef.current?.abort(); }, [load, refreshKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // Pull alert policies + triggered alerts from the backend
  const refreshAlertCenter = useCallback(async () => {
    const [pol, trig] = await Promise.all([acApi.getPolicies(), acApi.getTriggered()]);
    setAlertPolicies(pol);
    setTriggeredAlerts(trig);
  }, []);

  // After each data load, ask the backend to evaluate policies, then refresh.
  useEffect(() => {
    if (loading) return;
    let cancelled = false;
    (async () => {
      await acApi.evaluate();
      if (!cancelled) await refreshAlertCenter();
    })();
    return () => { cancelled = true; };
  }, [loading, refreshKey, refreshAlertCenter]);

  // Countdown ticker
  useEffect(() => {
    const ticker = setInterval(() => setCountdown(prev => Math.max(0, prev - 1)), 1000);
    return () => clearInterval(ticker);
  }, []);

  // When the countdown reaches zero, trigger a refresh.
  useEffect(() => {
    if (countdown === 0 && !loading) setRefreshKey(k => k + 1);
  }, [countdown, loading]);

  const newTriggeredCount = useMemo(() => triggeredAlerts.filter(a => a.status === "new").length, [triggeredAlerts]);

  const alertCounts = useMemo(() => ({
    identity: allAlerts.filter(a=>a.service==="EntraId").length + (mdiAlerts?.total??0) + (riskDetections?.total??0) + (identityHealth?.total??0),
    devices: allAlerts.filter(a=>a.service==="Intune").length,
    email: allAlerts.filter(a=>a.service==="ExchangeOnline").length,
    // Security alerts only — service-health advisories have their own badge and
    // must not inflate the incidents count.
    incidents: allAlerts.filter(a=>a.service!=="ServiceHealth").length + (defenderAlerts?.total??0) + (securityIncidents?.total??0),
    alertcenter: newTriggeredCount,
    servicehealth: serviceHealth?.total??0,
    licenses: (inactiveUsers?.inactive90Count??0)+(passwordExpiry?.expiringSoonCount??0),
    conditionalaccess: conditionalAccess?.disabled??0,
    auditlog: auditLog?.failures??0,
    signinmap: signInLocations?.failures??0,
    compliance: (mcasAlerts?.total??0) + (insiderRisk?.total??0),
    overview:0, network:0, users:0, setup:0
  }), [allAlerts, serviceHealth, defenderAlerts, securityIncidents, inactiveUsers, passwordExpiry, conditionalAccess, auditLog, signInLocations, mdiAlerts, riskDetections, identityHealth, mcasAlerts, insiderRisk, newTriggeredCount]);

  // When you're on a page (or navigate to one), mark that page's current count as "seen".
  useEffect(() => {
    const current = (alertCounts as Record<string,number>)[page] ?? 0;
    setSeenCounts(prev => {
      if (prev[page] === current) return prev;
      const updated = { ...prev, [page]: current };
      try { localStorage.setItem('m365-seen', JSON.stringify(updated)); } catch {}
      return updated;
    });
  }, [page, alertCounts]); // eslint-disable-line react-hooks/exhaustive-deps

  // Badge shows only the increase since last visit
  const unreadCounts = useMemo(() => {
    const r: Record<string,number> = {};
    for (const [k, v] of Object.entries(alertCounts))
      r[k] = Math.max(0, v - (seenCounts[k] ?? 0));
    return r;
  }, [alertCounts, seenCounts]);

  const currentNav = NAV.find(n=>n.id===page);


  const isInitialLoad = loading && overview === null;

  return (
    <div className={`app-shell${darkMode ? " dark" : ""}`}>
      <Sidebar page={page} setPage={setPage} alertCounts={unreadCounts}
        collapsed={sidebarCollapsed} onToggleCollapse={() => setSidebarCollapsed(c => !c)}/>
      <div className="main-area">
        <header className="main-hdr">
          <div>
            <h1 className="hdr-title">{currentNav?.label??"Overview"}</h1>
            <p className="hdr-sub">
              Vigil365 · M365 Security Operations · Updated {lastRefresh.toLocaleTimeString()}
              {" · "}<span className="countdown-chip"><Clock size={10}/>Next refresh {fmtCountdown(countdown)}</span>
            </p>
          </div>
          <div className="hdr-actions">
            {overview?.lastRun&&(
              <Badge label={`Last run: ${fmtDate(overview.lastRun.completedAt??overview.lastRun.startedAt)}`} tone="neutral"/>
            )}
            {auth.isAdmin && (
              <button className={`btn-run${(!overview&&!running&&!loading)?" btn-run-pulse":""}`} onClick={runCollection} disabled={running||loading} title="Run Graph collection now">
                <RefreshCw size={13} className={running?"spin":""}/>
                {running?"Collecting…":"Run Collection"}
              </button>
            )}
            <button className="btn-icon" onClick={() => setRefreshKey(k => k + 1)} title="Refresh data" disabled={loading||running} aria-label="Refresh data">
              <RefreshCw size={15} className={loading?"spin":""}/>
            </button>
            <button className="theme-toggle" onClick={() => setDarkMode(d => !d)} aria-label={darkMode ? "Switch to light mode" : "Switch to dark mode"} title={darkMode ? "Light mode" : "Dark mode"}>
              {darkMode ? <Sun size={15}/> : <Moon size={15}/>}
            </button>
            {account && (
              <div style={{ position: "relative" }}>
                <button
                  className="btn-icon"
                  onClick={() => setUserMenuOpen(o => !o)}
                  title={account.username}
                  style={{ display: "flex", alignItems: "center", gap: 6, padding: "4px 8px", borderRadius: 6 }}
                >
                  <div style={{ width: 26, height: 26, borderRadius: "50%", background: "#2563eb", display: "flex", alignItems: "center", justifyContent: "center", color: "#fff", fontSize: 11, fontWeight: 700, flexShrink: 0 }}>
                    {(account.name ?? account.username).charAt(0).toUpperCase()}
                  </div>
                  <span style={{ fontSize: 12, maxWidth: 120, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} className="mr-user">
                    {account.name ?? account.username}
                  </span>
                </button>
                {userMenuOpen && (
                  <div style={{ position: "absolute", right: 0, top: "calc(100% + 6px)", background: "var(--color-card)", border: "1px solid var(--color-border)", borderRadius: 8, boxShadow: "0 4px 16px rgba(0,0,0,0.15)", minWidth: 220, zIndex: 200 }}>
                    <div style={{ padding: "12px 16px", borderBottom: "1px solid var(--color-border)" }}>
                      <div style={{ fontWeight: 600, fontSize: 13 }}>{account.name}</div>
                      <div style={{ fontSize: 11, color: "var(--color-muted)", marginTop: 2 }}>{account.username}</div>
                      <div style={{ marginTop: 6 }}>
                        <Badge label={auth.role} tone={auth.isAdmin ? "info" : auth.canMutate ? "good" : "neutral"}/>
                      </div>
                    </div>
                    <button
                      onClick={() => { setUserMenuOpen(false); onSignOut?.(); }}
                      style={{ display: "flex", alignItems: "center", gap: 8, width: "100%", padding: "10px 16px", background: "none", border: "none", cursor: "pointer", fontSize: 13, color: "var(--color-text)", borderRadius: "0 0 8px 8px" }}
                      onMouseEnter={e => (e.currentTarget.style.background = "var(--color-raised)")}
                      onMouseLeave={e => (e.currentTarget.style.background = "none")}
                    >
                      <LogIn size={14} style={{ transform: "rotate(180deg)" }}/> Sign out
                    </button>
                  </div>
                )}
              </div>
            )}
          </div>
        </header>
        {loading && !isInitialLoad && <div className="loading-bar"><div className="loading-bar-fill"/></div>}
        {error&&<div className="err-banner">{error} <button style={{marginLeft:8,textDecoration:"underline",background:"none",border:"none",color:"inherit",cursor:"pointer"}} onClick={()=>setError("")}>Dismiss</button></div>}
        {isInitialLoad ? (
          <DashboardSkeleton />
        ) : (
          <>
            {page==="overview"&&<OverviewPage overview={overview} secureScore={secureScore} identity={identity} devices={devices} serviceHealth={serviceHealth} alerts={allAlerts} defenderAlerts={defenderAlerts} securityIncidents={securityIncidents} onAlertClick={setSelectedAlert} onNavigateAlertCenter={()=>setPage("alertcenter")} alertPolicies={alertPolicies} overviewTriggered={triggeredAlerts} healthRefreshKey={refreshKey}/>}
            {page==="recommendations"&&<RecommendationsPage />}
            {page==="trends"&&<TrendsPage />}
            {page==="identity"&&<IdentityPage identity={identity} alerts={allAlerts} privilegedRoles={privilegedRoles} pimData={pimData} mdiAlerts={mdiAlerts} riskDetections={riskDetections} identityHealth={identityHealth} onAlertClick={setSelectedAlert}/>}
            {page==="devices"&&<DevicesPage devices={devices} alerts={allAlerts} mdeVulnerabilities={mdeVulnerabilities} onAlertClick={setSelectedAlert}/>}
            {page==="email"&&<EmailPage alerts={allAlerts} emailProtection={emailProtection} onAlertClick={setSelectedAlert}/>}
            {page==="incidents"&&<IncidentsPage alerts={allAlerts} serviceHealth={serviceHealth} defenderAlerts={defenderAlerts} securityIncidents={securityIncidents} onAlertClick={setSelectedAlert}/>}
            {page==="alertcenter"&&<AlertCenterPage policies={alertPolicies} triggeredAlerts={triggeredAlerts} onChanged={refreshAlertCenter}/>}
            {page==="compliance"&&<CompliancePage secureScore={secureScore} overview={overview} dlpAlerts={dlpAlerts} purview={purview} mcasAlerts={mcasAlerts} insiderRisk={insiderRisk} attackSimulation={attackSimulation} identity={identity} devices={devices} ca={conditionalAccess} securityIncidents={securityIncidents} privilegedRoles={privilegedRoles} emailProtection={emailProtection}/>}
            {page==="servicehealth"&&<ServiceHealthPage serviceHealth={serviceHealth}/>}
            {page==="network"&&<NetworkPage serviceHealth={serviceHealth} signInLocations={signInLocations}/>}
            {page==="licenses"&&<LicensesPage licenses={licenses} inactive={inactiveUsers} passwords={passwordExpiry}/>}
            {page==="conditionalaccess"&&<ConditionalAccessPage data={conditionalAccess}/>}
            {page==="auditlog"&&<AuditLogPage data={auditLog}/>}
            {page==="signinmap"&&<SignInLocationsPage data={signInLocations}/>}
            {page==="users"&&<UserManagementPage/>}
            {page==="setup"&&<SetupPage/>}
          </>
        )}
      </div>
      {selectedAlert&&<AlertDetailModal alert={selectedAlert} allAlerts={allAlerts} onSelectAlert={setSelectedAlert} onClose={()=>setSelectedAlert(null)}/>}
      <ToastContainer/>
    </div>
  );
}

// ─── Auth gate bootstrap ───────────────────────────────────────────────────────
function AuthGate() {
  const [authReady, setAuthReady] = useState(false);
  const [account, setAccount] = useState<AccountInfo | null>(null);
  const [authEnabled, setAuthEnabled] = useState(false);
  const [loginError, setLoginError] = useState<string | null>(null);
  const [me, setMe] = useState<{ email: string; name: string; role: AppRole } | null>(null);

  // Once signed in, fetch the in-app role (Admin/Analyst/Viewer).
  useEffect(() => {
    if (!account) { setMe(null); return; }
    (async () => {
      try {
        const r = await apiFetch(`${apiBase}/api/auth/me`);
        if (r.ok) setMe(await r.json());
      } catch { /* leave null — treated as Viewer */ }
    })();
  }, [account]);

  useEffect(() => {
    (async () => {
      try {
        const res = await fetch(`${apiBase}/api/auth/config`);
        if (!res.ok) { setAuthReady(true); return; }
        const cfg: { clientId: string; tenantId: string; redirectUri: string } = await res.json();
        if (!cfg.clientId || !cfg.tenantId) { setAuthReady(true); return; }

        setAuthEnabled(true);
        const msalConfig: Configuration = {
          auth: { clientId: cfg.clientId, authority: `https://login.microsoftonline.com/${cfg.tenantId}`, redirectUri: cfg.redirectUri },
          cache: { cacheLocation: "sessionStorage" },
        };
        const pca = new PublicClientApplication(msalConfig);
        await pca.initialize();

        _msalInstance = pca;
        _msalScopes = [`api://${cfg.clientId}/access_as_user`];
        initMsal(pca, _msalScopes);

        // Handle redirect response — must be called before getAllAccounts()
        const redirectResult = await pca.handleRedirectPromise();
        if (redirectResult?.account) {
          pca.setActiveAccount(redirectResult.account);
          setAccount(redirectResult.account);
        } else {
          const accounts = pca.getAllAccounts();
          if (accounts.length > 0) {
            pca.setActiveAccount(accounts[0]);
            setAccount(accounts[0]);
          }
        }
      } catch (e) {
        console.error("Auth init error", e);
      }
      setAuthReady(true);
    })();
  }, []);

  const handleLogin = async () => {
    if (!_msalInstance) return;
    setLoginError(null);
    try {
      await _msalInstance.loginRedirect({ scopes: _msalScopes });
    } catch (e: unknown) {
      setLoginError(e instanceof Error ? e.message : "Login failed");
    }
  };

  if (!authReady) {
    return (
      <div style={{ display: "flex", alignItems: "center", justifyContent: "center", height: "100vh", background: "var(--color-bg, #0f172a)" }}>
        <div style={{ color: "#94a3b8", fontSize: 14 }}>Loading…</div>
      </div>
    );
  }

  if (authEnabled && !account) {
    return (
      <div style={{ display: "flex", height: "100vh", background: "#0f172a", fontFamily: "system-ui, -apple-system, sans-serif" }}>
        {/* Left panel — branding */}
        <div style={{ flex: 1, display: "flex", flexDirection: "column", justifyContent: "space-between", padding: "48px 56px", background: "linear-gradient(135deg, #0f172a 0%, #1e293b 100%)", borderRight: "1px solid #1e293b" }}>
          <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
            <div style={{ width: 40, height: 40, borderRadius: 10, background: "linear-gradient(135deg, #2563eb, #1d4ed8)", display: "flex", alignItems: "center", justifyContent: "center", boxShadow: "0 0 20px rgba(37,99,235,0.4)" }}>
              <Shield size={22} color="#fff" />
            </div>
            <span style={{ fontSize: 22, fontWeight: 700, color: "#f1f5f9", letterSpacing: "-0.3px" }}>Vigil365</span>
          </div>
          <div>
            <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 20 }}>
              <div style={{ width: 6, height: 6, borderRadius: "50%", background: "#22c55e", boxShadow: "0 0 8px #22c55e" }} />
              <span style={{ fontSize: 12, color: "#94a3b8", letterSpacing: "0.08em", textTransform: "uppercase" }}>Live Security Monitoring</span>
            </div>
            <h1 style={{ fontSize: 38, fontWeight: 800, color: "#f8fafc", lineHeight: 1.15, margin: "0 0 16px", letterSpacing: "-0.5px" }}>
              Microsoft 365<br />Security Operations
            </h1>
            <p style={{ fontSize: 15, color: "#cbd5e1", lineHeight: 1.7, margin: 0, maxWidth: 380 }}>
              Real-time visibility across identity, devices, email, and compliance — all in one self-hosted dashboard.
            </p>
            <div style={{ display: "flex", flexDirection: "column", gap: 14, marginTop: 40 }}>
              {[
                { icon: <Users size={14}/>, label: "Identity & Access Monitoring" },
                { icon: <Monitor size={14}/>, label: "Device Compliance & Intune" },
                { icon: <ShieldAlert size={14}/>, label: "Defender XDR & Threat Detection" },
                { icon: <Activity size={14}/>, label: "Alert Policies & Notifications" },
              ].map((f, i) => (
                <div key={i} style={{ display: "flex", alignItems: "center", gap: 10, color: "#94a3b8", fontSize: 13 }}>
                  <div style={{ color: "#60a5fa" }}>{f.icon}</div>
                  {f.label}
                </div>
              ))}
            </div>
          </div>
          <div style={{ fontSize: 12, color: "#64748b" }}>
            Open source · Self-hosted · MIT License
          </div>
        </div>

        {/* Right panel — login form */}
        <div style={{ width: 460, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", padding: "48px 56px", background: "#0f172a" }}>
          <div style={{ width: "100%", maxWidth: 340 }}>
            <h2 style={{ fontSize: 24, fontWeight: 700, color: "#f8fafc", margin: "0 0 8px", letterSpacing: "-0.3px" }}>Sign in</h2>
            <p style={{ fontSize: 13, color: "#94a3b8", margin: "0 0 36px" }}>Use your Microsoft 365 account to access the security dashboard.</p>

            {loginError && (
              <div style={{ background: "#450a0a", border: "1px solid #7f1d1d", borderRadius: 8, padding: "10px 14px", marginBottom: 20, fontSize: 13, color: "#fca5a5", display: "flex", alignItems: "center", gap: 8 }}>
                <AlertTriangle size={14}/> {loginError}
              </div>
            )}

            <button
              onClick={handleLogin}
              style={{ display: "flex", alignItems: "center", justifyContent: "center", gap: 10, width: "100%", padding: "13px 20px", background: "#2563eb", color: "#fff", border: "none", borderRadius: 10, fontSize: 15, fontWeight: 600, cursor: "pointer", boxShadow: "0 4px 14px rgba(37,99,235,0.4)", transition: "background 0.15s" }}
              onMouseEnter={e => (e.currentTarget.style.background = "#1d4ed8")}
              onMouseLeave={e => (e.currentTarget.style.background = "#2563eb")}
            >
              <svg width="18" height="18" viewBox="0 0 21 21" fill="none">
                <rect x="1" y="1" width="9" height="9" fill="#f25022"/>
                <rect x="11" y="1" width="9" height="9" fill="#7fba00"/>
                <rect x="1" y="11" width="9" height="9" fill="#00a4ef"/>
                <rect x="11" y="11" width="9" height="9" fill="#ffb900"/>
              </svg>
              Sign in with Microsoft
            </button>

            <div style={{ marginTop: 24, padding: "14px 16px", background: "#1e293b", borderRadius: 8, border: "1px solid #334155" }}>
              <div style={{ fontSize: 12, color: "#94a3b8", lineHeight: 1.6 }}>
                🔒 Access is restricted to your Microsoft 365 organisation. Only users in your tenant can sign in.
              </div>
            </div>
          </div>
        </div>
      </div>
    );
  }

  const handleSignOut = async () => {
    if (!_msalInstance) return;
    await _msalInstance.logoutRedirect({ postLogoutRedirectUri: window.location.origin });
  };

  // Least privilege on failure: an unreadable /api/auth/me must not surface
  // Admin-only controls (the server still enforces, but the UI shouldn't tease).
  const role: AppRole = me?.role ?? "Viewer";
  const auth: AuthInfo = {
    email: me?.email ?? "",
    name: me?.name ?? account?.name ?? "",
    role,
    isAdmin: role === "Admin",
    canMutate: role === "Admin" || role === "Analyst",
  };

  return (
    <AuthContext.Provider value={auth}>
      <App account={account} onSignOut={handleSignOut} />
    </AuthContext.Provider>
  );
}
class ErrorBoundary extends React.Component<{ children: React.ReactNode }, { hasError: boolean; error: Error | null }> {
  constructor(props: { children: React.ReactNode }) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error) {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error("ErrorBoundary caught an error", error, errorInfo);
  }

  render() {
    if (this.state.hasError) {
      return (
        <div style={{ padding: "40px", fontFamily: "system-ui, sans-serif", color: "#f8fafc", background: "#0f172a", minHeight: "100vh", display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center" }}>
          <div style={{ maxWidth: "600px", width: "100%", background: "#1e293b", padding: "32px", borderRadius: "12px", border: "1px solid #334155", boxShadow: "0 10px 25px rgba(0,0,0,0.3)" }}>
            <h1 style={{ color: "#ef4444", fontSize: "24px", margin: "0 0 16px 0", display: "flex", alignItems: "center", gap: "10px" }}>
              <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>
              Dashboard Render Error
            </h1>
            <p style={{ fontSize: "14px", color: "#94a3b8", lineHeight: "1.6" }}>
              Vigil365 encountered a rendering exception. This usually happens due to missing API fields or unhandled null values in the UI components.
            </p>
            <pre style={{ background: "#0f172a", padding: "16px", borderRadius: "8px", overflowX: "auto", fontSize: "12px", color: "#fca5a5", border: "1px solid #450a0a", margin: "20px 0 0 0", whiteSpace: "pre-wrap", wordBreak: "break-all" }}>
              {this.state.error?.stack || this.state.error?.message || String(this.state.error)}
            </pre>
            <button 
              onClick={() => window.location.reload()}
              style={{ marginTop: "24px", padding: "10px 20px", background: "#2563eb", color: "#fff", border: "none", borderRadius: "6px", cursor: "pointer", fontWeight: "600" }}
            >
              Reload Page
            </button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

createRoot(document.getElementById("root")!).render(<ErrorBoundary><AuthGate /></ErrorBoundary>);

