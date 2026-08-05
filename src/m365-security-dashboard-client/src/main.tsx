import React, { useEffect, useState, useMemo, useCallback, useRef } from "react";
import { createRoot } from "react-dom/client";
import { PublicClientApplication, type AccountInfo, type Configuration } from "@azure/msal-browser";
import {
  Home, Users, Monitor, Mail, AlertTriangle, Bell, CheckSquare, Activity, Wifi,
  Package, ShieldCheck, BookOpen, MapPin, UserCheck, Settings, ChevronRight, ChevronLeft,
  Clock, RefreshCw, Sun, Moon, Rows2, Rows3, LogIn, LogOut, ShieldAlert, Shield, UserX, TrendingUp, Lightbulb, Lock,
  Search as SearchIcon, Pause, Play, Globe
} from "lucide-react";
import "./styles.css";

// Import types & services
import {
  NavPage, AppRole, AuthInfo, SecurityAlert, Overview, SecureScore, IdentityData,
  DevicesData, ServiceHealthData, LicenseData, InactiveUsersData, PasswordExpiryData,
  ConditionalAccessData, SignInLocationsData, DefenderAlertsData,
  SecurityIncidentsData, PrivilegedRolesData, DlpAlertsData, MdeVulnerabilitiesData,
  PimData, EmailProtectionData, MdiAlertsData, McasAlertsData, InsiderRiskData,
  RiskDetectionsData, IdentityHealthData, AttackSimulationData, AlertPolicy, TriggeredAlert,
  PurviewData
} from "./services/types";
import { apiBase, apiFetch, AuthContext, initMsal, acApi, AUTO_REFRESH_SEC, useAuth, registerNavHandler, registerRefreshHandler } from "./services/api";
import { showToast } from "./services/toast";
import { ToastContainer } from "./components/ToastContainer";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { PageHelp } from "./components/PageHelp";
import { GlobalSearch } from "./components/GlobalSearch";
import { Badge, AlertDetailModal, DashboardSkeleton } from "./components/SharedComponents";
import { fmtDate, fmtCountdown, tzLabel, fmtUtc, isUtcMode, setUtcMode } from "./services/utils";
import { useSessionTimeout, clearSessionStart, IDLE_TIMEOUT_MIN, type ExpiryReason } from "./services/session";

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
import { SignInLocationsPage } from "./pages/SignInLocationsPage";
import { UserManagementPage } from "./pages/UserManagementPage";
import { SetupPage } from "./pages/SetupPage";
import { TrendsPage } from "./pages/TrendsPage";
import { ReportsPage } from "./pages/ReportsPage";
import { EntityPage } from "./pages/EntityPage";
import { ActivityFeedPage } from "./pages/ActivityFeedPage";

// App version — surfaced in the sidebar so the running build is always
// identifiable. Injected by Vite from package.json rather than hardcoded, so it
// cannot drift from the version that was actually built and shipped.
export const APP_VERSION: string = __APP_VERSION__;

// ─── MSAL instances local to bootstrapping ─────────────────────────────────────
let _msalInstance: PublicClientApplication | null = null;
let _msalScopes: string[] = [];

/**
 * MSAL records "an interactive sign-in is underway" in sessionStorage the moment
 * a redirect starts, and clears it when the redirect comes back. If it never
 * comes back — the tab was reloaded mid-flight, the user backed out of the
 * Microsoft page, or a TLS warning interrupted the round trip — the flag is left
 * set and every later sign-in throws interaction_in_progress instead of
 * navigating anywhere. The button then looks dead with no way out short of
 * clearing site data by hand.
 *
 * Stale entries for a client id that no longer exists are cleared too:
 * re-running the installer can register a different application, leaving the
 * previous one's keys behind.
 */
function clearStaleMsalInteraction(): void {
  try {
    for (const key of Object.keys(sessionStorage)) {
      if (key.startsWith("msal.") && key.includes("interaction.status")) {
        sessionStorage.removeItem(key);
      }
    }
  } catch { /* storage blocked — nothing to clear */ }
}

function isInteractionInProgress(e: unknown): boolean {
  const code = (e as { errorCode?: string } | null)?.errorCode;
  if (code === "interaction_in_progress") return true;
  return e instanceof Error && e.message.includes("interaction_in_progress");
}

// ─── Navigation: 8 sections, each holding one or more tab pages ────────────────
// Every original page id still exists (crossNavigate targets are unchanged) —
// pages are grouped into sections; multi-page sections render a tab bar.
type SectionDef = {
  id: string; label: string; icon: React.ReactNode;
  pages: { id: NavPage; label: string; adminOnly?: boolean }[];
};
const SECTIONS: SectionDef[] = [
  { id:"overview", label:"Overview",       icon:<Home size={17}/>,          pages:[{ id:"overview", label:"Overview" }] },
  { id:"alerts",   label:"Alerts",         icon:<AlertTriangle size={17}/>, pages:[
      { id:"incidents",    label:"Alert Queue" },
      { id:"alertcenter",  label:"Rules & Notifications" },
      { id:"activityfeed", label:"Tenant Activity" }] },
  { id:"identity", label:"Identity",       icon:<Users size={17}/>,         pages:[
      { id:"identity",          label:"Overview" },
      { id:"signinmap",         label:"Sign-in Locations" },
      { id:"conditionalaccess", label:"Conditional Access" }] },
  { id:"devices",  label:"Devices",        icon:<Monitor size={17}/>,       pages:[{ id:"devices", label:"Devices" }] },
  { id:"email",    label:"Email",          icon:<Mail size={17}/>,          pages:[{ id:"email", label:"Email" }] },
  { id:"posture",  label:"Posture",        icon:<CheckSquare size={17}/>,   pages:[
      { id:"compliance",      label:"Compliance" },
      { id:"recommendations", label:"Recommendations" },
      { id:"trends",          label:"Trends & History" },
      { id:"reports",         label:"Reports" }] },
  { id:"health",   label:"M365 Health",    icon:<Activity size={17}/>,      pages:[
      { id:"servicehealth", label:"Service Health" },
      { id:"network",       label:"Connectivity" }] },
  { id:"admin",    label:"Administration", icon:<Settings size={17}/>,      pages:[
      { id:"licenses", label:"Licenses & Users" },
      { id:"users",    label:"User Management", adminOnly:true },
      { id:"setup",    label:"Setup", adminOnly:true }] },
];
const sectionOf = (p: NavPage): SectionDef => SECTIONS.find(s => s.pages.some(pg => pg.id === p)) ?? SECTIONS[0];
const pageLabel = (p: NavPage): string => {
  const s = sectionOf(p);
  const tab = s.pages.find(pg => pg.id === p);
  return s.pages.length > 1 && tab ? `${s.label} · ${tab.label}` : s.label;
};
const VALID_PAGES = new Set<string>(SECTIONS.flatMap(s => s.pages.map(p => p.id)));

// ─── Hash router ────────────────────────────────────────────────────────────────
// URL shape: #/{pageId} with an optional ?alert={id} permalink. Numeric ids are
// collected M365 security alerts; GUIDs are triggered policy alerts (the kind
// notifications link to). Keeps pages refresh-safe, bookmarkable, shareable.
type EntityRef = { kind: "user" | "device"; id: string };
function parseHash(): { page: NavPage | null; alertId: number | null; triggeredId: string | null; entity: EntityRef | null } {
  // Entity drill-down route: #/entity/{user|device}/{encoded-id}
  const ent = window.location.hash.match(/^#\/entity\/(user|device)\/(.+)$/);
  if (ent) {
    let id = ent[2];
    try { id = decodeURIComponent(id); } catch { /* leave raw if malformed */ }
    return { page: null, alertId: null, triggeredId: null, entity: { kind: ent[1] as "user" | "device", id } };
  }
  const m = window.location.hash.match(/^#\/([a-z]+)(?:\?alert=([0-9a-fA-F-]+))?/);
  const page = m && VALID_PAGES.has(m[1]) ? (m[1] as NavPage) : null;
  const raw = m?.[2] ?? null;
  const isGuid = !!raw && raw.includes("-");
  return {
    page,
    alertId: raw && !isGuid ? Number(raw) : null,
    triggeredId: isGuid ? raw : null,
    entity: null,
  };
}

function Sidebar({ page, setPage, alertCounts, collapsed, onToggleCollapse }: {
  page:NavPage; setPage:(p:NavPage)=>void; alertCounts: Record<string,number>;
  collapsed: boolean; onToggleCollapse: () => void;
}) {
  const { isAdmin } = useAuth();
  const activeSection = sectionOf(page).id;
  // Remember the last tab visited per section so returning to a section
  // reopens where the user left off, not always the first tab.
  const lastTab = React.useRef<Record<string, NavPage>>({});
  lastTab.current[activeSection] = page;
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
        {SECTIONS.map(s => {
          const visible = s.pages.filter(p => !p.adminOnly || isAdmin);
          if (visible.length === 0) return null;
          const count = visible.reduce((acc, p) => acc + (alertCounts[p.id] ?? 0), 0);
          const target = () => {
            const remembered = lastTab.current[s.id];
            return remembered && visible.some(v => v.id === remembered) ? remembered : visible[0].id;
          };
          return (
            <button
              key={s.id}
              className={`nav-item ${activeSection===s.id?"nav-active":""}`}
              onClick={()=>setPage(target())}
              aria-label={s.label}
              aria-current={activeSection===s.id ? "page" : undefined}
              title={collapsed ? s.label : undefined}
            >
              {s.icon}
              {!collapsed && <span>{s.label}</span>}
              {!collapsed && count>0 && <span className="nav-badge">{count > 99 ? "99+" : count}</span>}
              {collapsed && <span className="nav-tooltip">{s.label}{count>0?` (${count})`:"" }</span>}
            </button>
          );
        })}
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
  // Page state is hash-routed: initialised from the URL, kept in sync both ways,
  // so refresh preserves the page and back/forward navigates pages.
  const [page, setPageState] = useState<NavPage>(() => parseHash().page ?? "overview");
  const [pendingAlertId, setPendingAlertId] = useState<number | null>(() => parseHash().alertId);
  const [pendingTriggeredId, setPendingTriggeredId] = useState<string | null>(() => parseHash().triggeredId);
  const [entity, setEntity] = useState<EntityRef | null>(() => parseHash().entity);
  const setPage = useCallback((p: NavPage) => {
    setPageState(p);
    setEntity(null);
    if (window.location.hash !== `#/${p}`) window.history.pushState(null, "", `#/${p}`);
  }, []);
  useEffect(() => {
    if (!window.location.hash) window.history.replaceState(null, "", `#/${parseHash().page ?? "overview"}`);
    const sync = () => {
      const { page: p, alertId, triggeredId, entity: e } = parseHash();
      setEntity(e);
      if (p) setPageState(p);
      if (alertId != null) setPendingAlertId(alertId);
      if (triggeredId != null) setPendingTriggeredId(triggeredId);
    };
    window.addEventListener("popstate", sync);
    window.addEventListener("hashchange", sync);
    return () => { window.removeEventListener("popstate", sync); window.removeEventListener("hashchange", sync); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const [darkMode, setDarkMode] = useState(() => localStorage.getItem("m365-theme") === "dark");
  const [compact, setCompact] = useState(() => localStorage.getItem("m365-density") === "compact");
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false); // false = expanded

  // Timezone toggle
  const [, setTzTick] = useState(0);
  useEffect(() => {
    const handler = () => setTzTick(t => t + 1);
    window.addEventListener("timezone-changed", handler);
    return () => window.removeEventListener("timezone-changed", handler);
  }, []);

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

  // Row density. Analysts scanning queues for hours want more rows per screen;
  // spacing tightens but type size never does.
  useEffect(() => {
    document.documentElement.dataset.density = compact ? "compact" : "comfortable";
    localStorage.setItem("m365-density", compact ? "compact" : "comfortable");
  }, [compact]);

  // Allow pages to deep-link into one another (e.g. Alert Center → Identity by UPN)
  useEffect(() => registerNavHandler(({ page }) => setPage(page as NavPage)), []);

  const [overview, setOverview] = useState<Overview|null>(null);
  const [secureScore, setSecureScore] = useState<SecureScore|null>(null);
  const [identity, setIdentity] = useState<IdentityData|null>(null);
  const [devices, setDevices] = useState<DevicesData|null>(null);
  const [serviceHealth, setServiceHealth] = useState<ServiceHealthData|null>(null);
  const [allAlerts, setAllAlerts] = useState<SecurityAlert[]>([]);
  // Server-side total for the open-alert query. The list itself is capped at 200,
  // so this is what lets the UI say "showing 200 of N" instead of quietly
  // disagreeing with Overview's full-DB count.
  const [alertsTotal, setAlertsTotal] = useState(0);
  const [licenses, setLicenses] = useState<LicenseData|null>(null);
  const [inactiveUsers, setInactiveUsers] = useState<InactiveUsersData|null>(null);
  const [passwordExpiry, setPasswordExpiry] = useState<PasswordExpiryData|null>(null);
  const [conditionalAccess, setConditionalAccess] = useState<ConditionalAccessData|null>(null);
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
  // Correlation id of the failing request, so the banner can tell an admin
  // exactly which server-log entry to look up.
  const [errorId, setErrorId] = useState("");
  const [lastRefresh, setLastRefresh] = useState(new Date());
  const [running, setRunning] = useState(false);
  const [selectedAlert, setSelectedAlert] = useState<SecurityAlert|null>(null);

  // Alert permalinks: the open alert is reflected in the URL (#/page?alert=id)
  // so the link can be shared; opening such a link selects the alert once data
  // has loaded. replaceState avoids polluting history with every open/close.
  useEffect(() => {
    window.history.replaceState(null, "", selectedAlert ? `#/${page}?alert=${selectedAlert.id}` : `#/${page}`);
  }, [selectedAlert, page]);
  useEffect(() => {
    if (pendingAlertId == null || allAlerts.length === 0) return;
    const target = allAlerts.find(a => a.id === pendingAlertId);
    if (target) setSelectedAlert(target);
    setPendingAlertId(null);
  }, [pendingAlertId, allAlerts]);

  const [countdown, setCountdown] = useState(AUTO_REFRESH_SEC);
  const [refreshKey, setRefreshKey] = useState(0);
  // Pause-able auto-refresh: an analyst mid-read shouldn't have the list
  // re-render under them with no say in it.
  const [refreshPaused, setRefreshPaused] = useState(false);
  const refreshPausedRef = useRef(refreshPaused);
  refreshPausedRef.current = refreshPaused;
  const abortRef = useRef<AbortController|null>(null);

  const load = useCallback(async () => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;

    setLoading(true); setError("");

    const safeJson = (r: Response) => r.ok ? r.json() : Promise.resolve(null);
    const sig = ctrl.signal;

    // Failure accounting: a silently-swallowed fetch used to leave stale cards on
    // screen under a fresh "Updated HH:MM" stamp. Track outcomes so the UI can tell
    // the truth about what actually loaded.
    let attempted = 0, failed = 0;
    let correlationId = "";

    // Helper: fetch one endpoint and update state immediately on resolve
    // Each request has a 20-second timeout so slow Graph calls don't block the loading bar
    const fetchOne = <T,>(url: string, setter: (v: T) => void, transform?: (v: T) => T) => {
      attempted++;
      const timeoutSig = AbortSignal.timeout(20_000);
      const combinedSig = (AbortSignal as { any?: (sigs: AbortSignal[]) => AbortSignal }).any
        ? (AbortSignal as { any: (sigs: AbortSignal[]) => AbortSignal }).any([sig, timeoutSig])
        : sig;
      return apiFetch(url, { signal: combinedSig })
        .then(r => {
          if (!r.ok) {
            failed++;
            // The API stamps every response with a correlation id and logs under
            // it. Capturing it here turns "something broke" into a support ticket
            // an admin can actually trace in the server log.
            const cid = r.headers.get("X-Correlation-Id");
            if (cid) correlationId = cid;
          }
          return safeJson(r);
        })
        .then((v: T) => {
          if (!ctrl.signal.aborted && v != null)
            setter(transform ? transform(v) : v);
        })
        .catch(() => { failed++; /* individual failure or timeout */ });
    };

    try {
      // Fire all requests simultaneously; each updates state as it resolves
      await Promise.allSettled([
        fetchOne(`${apiBase}/api/dashboard/overview`, setOverview),
        fetchOne(`${apiBase}/api/dashboard/securescore`, setSecureScore),
        fetchOne(`${apiBase}/api/dashboard/identity`, setIdentity),
        fetchOne(`${apiBase}/api/dashboard/devices`, setDevices),
        fetchOne(`${apiBase}/api/dashboard/servicehealth`, setServiceHealth),
        fetchOne<{items: SecurityAlert[]; total: number}>(`${apiBase}/api/alerts?page=1&pageSize=200`,
          v => { setAllAlerts(v.items ?? []); setAlertsTotal(v.total ?? (v.items?.length ?? 0)); }),
        fetchOne(`${apiBase}/api/dashboard/licenses`, setLicenses),
        fetchOne(`${apiBase}/api/dashboard/inactive-users`, setInactiveUsers),
        fetchOne(`${apiBase}/api/dashboard/password-expiry`, setPasswordExpiry),
        fetchOne(`${apiBase}/api/dashboard/conditional-access`, setConditionalAccess),
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

      // Total failure: the API is unreachable/down. Do NOT advance the refresh
      // stamp — the cards on screen are stale and must not look current.
      setErrorId(correlationId);
      if (attempted > 0 && failed >= attempted) {
        setError("Failed to load dashboard data. Is the API running?");
      } else {
        if (failed > 0)
          // Deliberately worded as "dashboard panels", not "data sources": the
          // collection banner already says "N sources failed" about Graph
          // collectors. These are different failures and must not read alike.
          setError(`${failed} of ${attempted} dashboard panels failed to load — those cards may be out of date.`);
        setLastRefresh(new Date());
      }
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

  // Let any card offer a working "Retry" without threading a callback through
  // every level of props.
  useEffect(() => registerRefreshHandler(() => setRefreshKey(k => k + 1)), []);

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

  // Countdown ticker — frozen while paused.
  useEffect(() => {
    const ticker = setInterval(() => setCountdown(prev => refreshPausedRef.current ? prev : Math.max(0, prev - 1)), 1000);
    return () => clearInterval(ticker);
  }, []);

  // When the countdown reaches zero, trigger a refresh (unless paused).
  useEffect(() => {
    if (countdown === 0 && !loading && !refreshPaused) setRefreshKey(k => k + 1);
  }, [countdown, loading, refreshPaused]);

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
    signinmap: signInLocations?.failures??0,
    compliance: (mcasAlerts?.total??0) + (insiderRisk?.total??0),
    overview:0, network:0, users:0, setup:0
  }), [allAlerts, serviceHealth, defenderAlerts, securityIncidents, inactiveUsers, passwordExpiry, conditionalAccess, signInLocations, mdiAlerts, riskDetections, identityHealth, mcasAlerts, insiderRisk, newTriggeredCount]);

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

  const activeSectionDef = sectionOf(page);
  const visibleTabs = activeSectionDef.pages.filter(p => !p.adminOnly || auth.isAdmin);

  // ── Global search (Ctrl+K) ────────────────────────────────────────────────
  const [searchOpen, setSearchOpen] = useState(false);
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); setSearchOpen(o => !o); }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);
  const searchPages = useMemo(() =>
    SECTIONS.flatMap(s => s.pages
      .filter(p => !p.adminOnly || auth.isAdmin)
      .map(p => ({ id: p.id, label: s.pages.length > 1 ? `${s.label} · ${p.label}` : s.label }))),
    [auth.isAdmin]);

  const isInitialLoad = loading && overview === null;

  return (
    <div className={`app-shell${darkMode ? " dark" : ""}`}>
      {/* First tab stop: lets keyboard users jump the 8-section sidebar instead
          of tabbing through it on every page. */}
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <Sidebar page={page} setPage={setPage} alertCounts={unreadCounts}
        collapsed={sidebarCollapsed} onToggleCollapse={() => setSidebarCollapsed(c => !c)}/>
      <div className="main-area">
        <div className="mobile-notice">Vigil365 is optimised for desktop — navigation is limited on small screens.</div>
        <header className="main-hdr">
          <div>
            <div className="hdr-title-row">
              <h1 className="hdr-title">{pageLabel(page)}</h1>
              <PageHelp page={page}/>
            </div>
            <p className="hdr-sub">
              Vigil365 · M365 Security Operations · Updated{" "}
              <span title={fmtUtc(lastRefresh.toISOString())}>
                {lastRefresh.toLocaleTimeString("en-US")}
              </span>
              {" "}<span className="hdr-tz" title="All timestamps in this app use your local timezone">{tzLabel()}</span>
              {" · "}
              <button className="countdown-chip" onClick={() => setRefreshPaused(p => !p)}
                title={refreshPaused ? "Resume auto-refresh" : "Pause auto-refresh"}
                aria-pressed={refreshPaused}>
                {refreshPaused ? <><Play size={10}/>Auto-refresh paused</> : <><Pause size={10}/>Next refresh {fmtCountdown(countdown)}</>}
              </button>
            </p>
          </div>
          <div className="hdr-actions">
            <button className="hdr-search" onClick={() => setSearchOpen(true)} aria-label="Search (Ctrl+K)">
              <SearchIcon size={13}/><span>Search</span><kbd>Ctrl K</kbd>
            </button>
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
            <button className="theme-toggle" onClick={() => setCompact(c => !c)}
              aria-pressed={compact}
              aria-label={compact ? "Switch to comfortable row spacing" : "Switch to compact row spacing"}
              title={compact ? "Comfortable rows" : "Compact rows"}>
              {compact ? <Rows3 size={15}/> : <Rows2 size={15}/>}
            </button>
            <button className="theme-toggle" onClick={() => setUtcMode(!isUtcMode())}
              aria-label={isUtcMode() ? "Switch to local time" : "Switch to UTC"}
              title={isUtcMode() ? "UTC time" : "Local time"}>
              {isUtcMode() ? <Globe size={15}/> : <Clock size={15}/>}
            </button>
            <button className="theme-toggle" onClick={() => setDarkMode(d => !d)} aria-label={darkMode ? "Switch to light mode" : "Switch to dark mode"} title={darkMode ? "Light mode" : "Dark mode"}>
              {darkMode ? <Sun size={15}/> : <Moon size={15}/>}
            </button>
            {account && (
              <div className="user-menu-wrap"
                onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node)) setUserMenuOpen(false); }}
                onKeyDown={e => { if (e.key === "Escape") setUserMenuOpen(false); }}>
                <button
                  className="btn-icon user-menu-btn"
                  onClick={() => setUserMenuOpen(o => !o)}
                  title={account.username}
                  aria-haspopup="menu" aria-expanded={userMenuOpen}
                >
                  <span className="user-avatar">{(account.name ?? account.username).charAt(0).toUpperCase()}</span>
                  <span className="user-menu-name">{account.name ?? account.username}</span>
                </button>
                {userMenuOpen && (
                  <div className="user-menu" role="menu">
                    <div className="user-menu-hdr">
                      <div className="um-name">{account.name}</div>
                      <div className="um-mail">{account.username}</div>
                      <div className="user-menu-role">
                        <Badge label={auth.role} tone={auth.isAdmin ? "info" : auth.canMutate ? "good" : "neutral"}/>
                      </div>
                    </div>
                    <button role="menuitem" className="user-menu-item"
                      onClick={() => { setUserMenuOpen(false); onSignOut?.(); }}>
                      <LogOut size={14}/> Sign out
                    </button>
                  </div>
                )}
              </div>
            )}
          </div>
        </header>
        {visibleTabs.length > 1 && (
          <div className="ac-tabs section-tabs" role="tablist" aria-label={`${activeSectionDef.label} sections`}>
            {visibleTabs.map(t => (
              <button key={t.id} role="tab" aria-selected={page === t.id}
                className={`ac-tab ${page === t.id ? "active" : ""}`}
                onClick={() => setPage(t.id)}>
                {t.label}{(unreadCounts[t.id] ?? 0) > 0 ? ` (${unreadCounts[t.id]})` : ""}
              </button>
            ))}
          </div>
        )}
        {loading && !isInitialLoad && <div className="loading-bar"><div className="loading-bar-fill"/></div>}
        {error && (
          <div className="err-banner" role="alert">
            <span className="err-banner-msg">{error}</span>
            {errorId && <code className="err-banner-id" title="Quote this reference when reporting the problem — it matches the server log entry">Ref {errorId}</code>}
            <button type="button" className="err-banner-dismiss" onClick={() => { setError(""); setErrorId(""); }}>Dismiss</button>
          </div>
        )}
        <main id="main-content" tabIndex={-1}>
        {isInitialLoad ? (
          <DashboardSkeleton />
        ) : entity ? (
          <EntityPage kind={entity.kind} id={entity.id}
            onBack={() => { if (window.history.length > 1) window.history.back(); else setPage("overview"); }}/>
        ) : (
          <>
            {page==="overview"&&<OverviewPage overview={overview} secureScore={secureScore} identity={identity} devices={devices} serviceHealth={serviceHealth} alerts={allAlerts} defenderAlerts={defenderAlerts} securityIncidents={securityIncidents} onAlertClick={setSelectedAlert} onNavigateAlertCenter={()=>setPage("alertcenter")} alertPolicies={alertPolicies} overviewTriggered={triggeredAlerts} healthRefreshKey={refreshKey}/>}
            {page==="recommendations"&&<RecommendationsPage />}
            {page==="trends"&&<TrendsPage />}
            {page==="reports"&&<ReportsPage />}
            {page==="identity"&&<IdentityPage identity={identity} alerts={allAlerts} privilegedRoles={privilegedRoles} pimData={pimData} mdiAlerts={mdiAlerts} riskDetections={riskDetections} identityHealth={identityHealth} onAlertClick={setSelectedAlert}/>}
            {page==="devices"&&<DevicesPage devices={devices} alerts={allAlerts} mdeVulnerabilities={mdeVulnerabilities} onAlertClick={setSelectedAlert}/>}
            {page==="email"&&<EmailPage alerts={allAlerts} emailProtection={emailProtection} onAlertClick={setSelectedAlert}/>}
            {page==="incidents"&&<IncidentsPage alerts={allAlerts} alertsTotal={alertsTotal} serviceHealth={serviceHealth} defenderAlerts={defenderAlerts} securityIncidents={securityIncidents} onAlertClick={setSelectedAlert}/>}
            {page==="alertcenter"&&<AlertCenterPage policies={alertPolicies} triggeredAlerts={triggeredAlerts} onChanged={refreshAlertCenter}
              deepLinkAlertId={pendingTriggeredId} onDeepLinkConsumed={() => setPendingTriggeredId(null)}/>}
            {page==="activityfeed"&&<ActivityFeedPage/>}
            {page==="compliance"&&<CompliancePage secureScore={secureScore} overview={overview} dlpAlerts={dlpAlerts} purview={purview} mcasAlerts={mcasAlerts} insiderRisk={insiderRisk} attackSimulation={attackSimulation} identity={identity} devices={devices} ca={conditionalAccess} securityIncidents={securityIncidents} privilegedRoles={privilegedRoles} emailProtection={emailProtection}/>}
            {page==="servicehealth"&&<ServiceHealthPage serviceHealth={serviceHealth}/>}
            {page==="network"&&<NetworkPage serviceHealth={serviceHealth} signInLocations={signInLocations}/>}
            {page==="licenses"&&<LicensesPage licenses={licenses} inactive={inactiveUsers} passwords={passwordExpiry}/>}
            {page==="conditionalaccess"&&<ConditionalAccessPage data={conditionalAccess}/>}
            {page==="signinmap"&&<SignInLocationsPage data={signInLocations}/>}
            {page==="users"&&<UserManagementPage/>}
            {page==="setup"&&<SetupPage/>}
          </>
        )}
        </main>
      </div>
      {selectedAlert&&<AlertDetailModal alert={selectedAlert} allAlerts={allAlerts} onSelectAlert={setSelectedAlert} onClose={()=>setSelectedAlert(null)}/>}
      <GlobalSearch open={searchOpen} onClose={() => setSearchOpen(false)} alerts={allAlerts}
        pages={searchPages} onOpenAlert={a => setSelectedAlert(a)} onNavigatePage={setPage}/>
      <ToastContainer/>
      <ConfirmDialog/>
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
  // Read once and clear: explains the *next* login screen, not every later one.
  const [signOutReason] = useState<string | null>(() => {
    const r = sessionStorage.getItem("vigil365-signout-reason");
    if (r) sessionStorage.removeItem("vigil365-signout-reason");
    return r;
  });

  // Bound how long an unattended browser keeps showing tenant security data.
  // Declared before any conditional return so hook order stays stable, and
  // only armed once a session actually exists.
  const expireSession = useCallback(async (reason: ExpiryReason) => {
    clearSessionStart();
    sessionStorage.setItem("vigil365-signout-reason", reason);
    if (_msalInstance) {
      await _msalInstance.logoutRedirect({ postLogoutRedirectUri: window.location.origin });
    } else {
      window.location.reload();
    }
  }, []);
  useSessionTimeout(expireSession, !!account);

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
        const cfg: { clientId: string; tenantId: string; redirectUri: string; instance?: string } = await res.json();
        if (!cfg.clientId || !cfg.tenantId) { setAuthReady(true); return; }

        setAuthEnabled(true);
        const msalConfig: Configuration = {
          auth: { clientId: cfg.clientId, authority: `${cfg.instance ?? "https://login.microsoftonline.com/"}${cfg.tenantId}`, redirectUri: cfg.redirectUri },
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
          } else {
            // handleRedirectPromise has settled and nobody is signed in, so no
            // interaction can still be genuinely in flight. Anything left over is
            // debris from an abandoned attempt — drop it now so the sign-in
            // button works on the first click rather than the second.
            clearStaleMsalInteraction();
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
      // A previous redirect that never completed leaves MSAL's interaction flag
      // set, and it refuses to start another. Clear it and try once more, rather
      // than showing a raw error code the user cannot act on.
      if (isInteractionInProgress(e)) {
        clearStaleMsalInteraction();
        try {
          await _msalInstance.loginRedirect({ scopes: _msalScopes });
          return;
        } catch (retry: unknown) {
          setLoginError(retry instanceof Error ? retry.message : "Sign-in failed");
          return;
        }
      }
      setLoginError(e instanceof Error ? e.message : "Sign-in failed");
    }
  };

  if (!authReady) {
    return (
      <div className="app-loading-shell">
        <div className="app-loading-text">Loading…</div>
      </div>
    );
  }

  if (authEnabled && !account) {
    return (
      <div className="login-shell">
        {/* Left panel — branding (deliberately dark; scoped .login-* classes) */}
        <div className="login-brand">
          <div className="login-logo">
            <div className="login-logo-mark"><Shield size={22} color="#fff" /></div>
            <span className="login-logo-name">Vigil365</span>
          </div>
          <div>
            <div className="login-live">
              <span className="login-live-dot" />
              <span className="login-live-label">Scheduled Security Monitoring</span>
            </div>
            <h1 className="login-title">Microsoft 365<br />Security Operations</h1>
            <p className="login-desc">
              Continuous visibility across identity, devices, email, and compliance — collected on a schedule, all in one self-hosted dashboard.
            </p>
            <div className="login-features">
              {[
                { icon: <Users size={14}/>, label: "Identity & Access Monitoring" },
                { icon: <Monitor size={14}/>, label: "Device Compliance & Intune" },
                { icon: <ShieldAlert size={14}/>, label: "Defender XDR & Threat Detection" },
                { icon: <Activity size={14}/>, label: "Alert Policies & Notifications" },
              ].map((f, i) => (
                <div key={i} className="login-feature">{f.icon}{f.label}</div>
              ))}
            </div>
          </div>
          <div className="login-footer">Open source · Self-hosted · MIT License · v{APP_VERSION}</div>
        </div>

        {/* Right panel — login form */}
        <div className="login-form-panel">
          <div className="login-form">
            <h2>Sign in</h2>
            <p className="login-form-sub">Use your Microsoft 365 account to access the security dashboard.</p>

            {loginError && (
              <div className="login-error" role="alert"><AlertTriangle size={14}/> {loginError}</div>
            )}

            {/* An unexplained sign-out reads as a bug. Say which limit was hit. */}
            {signOutReason && (
              <div className="login-note" role="status">
                <Lock size={13} className="login-note-icon"/>
                <span>{signOutReason === "idle"
                  ? `You were signed out after ${IDLE_TIMEOUT_MIN} minutes of inactivity.`
                  : "You were signed out because the maximum session length was reached."}</span>
              </div>
            )}

            <button className="login-ms-btn" onClick={handleLogin}>
              <svg width="18" height="18" viewBox="0 0 21 21" fill="none" aria-hidden="true">
                <rect x="1" y="1" width="9" height="9" fill="#f25022"/>
                <rect x="11" y="1" width="9" height="9" fill="#7fba00"/>
                <rect x="1" y="11" width="9" height="9" fill="#00a4ef"/>
                <rect x="11" y="11" width="9" height="9" fill="#ffb900"/>
              </svg>
              Sign in with Microsoft
            </button>

            <div className="login-note">
              <Lock size={13} className="login-note-icon"/>
              <span>Access is restricted to your Microsoft 365 organisation. Only users in your tenant can sign in.</span>
            </div>
          </div>
        </div>
      </div>
    );
  }

  const handleSignOut = async () => {
    if (!_msalInstance) return;
    clearSessionStart();
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
        <div className="err-shell">
          <div className="err-card">
            <h1 className="err-title">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>
              Something went wrong
            </h1>
            <p className="err-desc">
              Vigil365 hit an unexpected error while rendering this page. Your data is safe —
              reloading usually resolves it. If it keeps happening, share the technical details
              below with your administrator.
            </p>
            <button className="btn-apply" onClick={() => window.location.reload()}>Reload page</button>
            <details className="err-details">
              <summary>Technical details</summary>
              <pre>{this.state.error?.stack || this.state.error?.message || String(this.state.error)}</pre>
            </details>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

createRoot(document.getElementById("root")!).render(<ErrorBoundary><AuthGate /></ErrorBoundary>);

