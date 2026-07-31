import React, { useState, useMemo, useEffect } from "react";
import { Key, UserX, Globe, Users, Search, CheckCircle, XCircle, UserCheck, ShieldCheck, AlertCircle, AlertTriangle, Clock, User, Lock, ShieldAlert } from "lucide-react";
import { IdentityData, SecurityAlert, PrivilegedRolesData, PimData, MdiAlertsData, RiskDetectionsData, IdentityHealthData, MdiAlert, RiskDetection, Tone } from "../services/types";
import { pctTone, fmtDate, sevColor, relTime, fmtFullTime } from "../services/utils";
import { consumeNavSeed, crossNavigate } from "../services/api";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, InlineError, InfoRow, ProgressBar, ExportDropdown, CircleGauge, StatBox, SectHdr, rowActivation } from "../components/SharedComponents";
import { FilterPresets } from "../components/FilterPresets";

export function IdentityPage({ identity, alerts, privilegedRoles, pimData, mdiAlerts, riskDetections, identityHealth, onAlertClick }:
  { identity: IdentityData|null; alerts: SecurityAlert[]; privilegedRoles: PrivilegedRolesData|null; pimData: PimData|null; mdiAlerts: MdiAlertsData|null; riskDetections: RiskDetectionsData|null; identityHealth: IdentityHealthData|null; onAlertClick:(a:SecurityAlert)=>void }) {

  const [selectedMdi, setSelectedMdi] = useState<MdiAlert|null>(null);
  const [selectedDetection, setSelectedDetection] = useState<RiskDetection|null>(null);

  // Page-level search — applies to ALL sections on this page
  const [search, setSearch]     = useState("");
  const [riskLevel, setRiskLevel] = useState("");
  const [showResolved, setShowResolved] = useState(true); // default ON so nothing is hidden

  // Pick up a deep-link seed (e.g. Alert Center → "view user in Identity")
  useEffect(() => {
    const checkSeed = () => {
      const seed = consumeNavSeed("identity");
      if (seed) { setSearch(seed); setShowResolved(true); }
    };
    checkSeed();
    const listener = (e: any) => {
      if (e.detail?.page === "identity" && e.detail?.search) {
        setSearch(e.detail.search);
        setShowResolved(true);
      }
    };
    window.addEventListener("nav-seed-update", listener);
    return () => window.removeEventListener("nav-seed-update", listener);
  }, []);

  const identityAlerts = useMemo(() => alerts.filter(a => a.service==="EntraId"), [alerts]);
  const q = search.toLowerCase();

  const mfaMissing = useMemo(() => {
    let items = identityAlerts.filter(a => a.alertType==="MfaStatus" && (!showResolved ? !a.isResolved : true));
    if (search) items = items.filter(a => a.userPrincipalName?.toLowerCase().includes(q) || a.title.toLowerCase().includes(q));
    return items;
  }, [identityAlerts, search, showResolved, q]);

  const riskySignIns = useMemo(() => {
    let items = identityAlerts.filter(a => a.alertType==="RiskySignIn");
    if (!showResolved) items = items.filter(a => !a.isResolved);
    if (riskLevel) items = items.filter(a => a.severity.toLowerCase() === riskLevel);
    if (search) items = items.filter(a => a.userPrincipalName?.toLowerCase().includes(q) || a.title.toLowerCase().includes(q));
    return items;
  }, [identityAlerts, search, riskLevel, showResolved, q]);

  const riskyUsers = useMemo(() => {
    let items = identityAlerts.filter(a => a.alertType==="RiskyUser");
    if (!showResolved) items = items.filter(a => !a.isResolved);
    if (riskLevel) items = items.filter(a => a.severity.toLowerCase() === riskLevel);
    if (search) items = items.filter(a => a.userPrincipalName?.toLowerCase().includes(q) || a.title.toLowerCase().includes(q));
    return items;
  }, [identityAlerts, search, riskLevel, showResolved, q]);

  const filteredPim = useMemo(() => {
    let items = pimData?.activations ?? [];
    if (search) items = items.filter(a =>
      (a.principalDisplayName ?? a.principalUpn ?? "").toLowerCase().includes(q) ||
      (a.roleName ?? "").toLowerCase().includes(q)
    );
    return items;
  }, [pimData, search, q]);

  const filteredMdiAlerts = useMemo(() => {
    let items = mdiAlerts?.alerts ?? [];
    if (riskLevel) items = items.filter(a => a.severity.toLowerCase() === riskLevel);
    if (search) items = items.filter(a => (a.title ?? "").toLowerCase().includes(q) || (a.category ?? "").toLowerCase().includes(q));
    return items;
  }, [mdiAlerts, search, riskLevel, q]);

  const filteredDetections = useMemo(() => {
    let items = riskDetections?.detections ?? [];
    if (riskLevel) items = items.filter(d => d.riskLevel.toLowerCase() === riskLevel);
    if (search) items = items.filter(d =>
      (d.userPrincipalName ?? d.userDisplayName ?? "").toLowerCase().includes(q) ||
      (d.riskEventType ?? "").toLowerCase().includes(q)
    );
    return items;
  }, [riskDetections, search, riskLevel, q]);

  const filteredForeignSignIns = useMemo(() => {
    let items = identity?.foreignSignIns ?? [];
    if (search) items = items.filter(s =>
      (s.userPrincipalName ?? "").toLowerCase().includes(q) ||
      (s.title ?? "").toLowerCase().includes(q)
    );
    return items;
  }, [identity, search, q]);

  const mfaPct = identity?.mfa.percentage ?? 0;
  const hasFilter = !!(search || riskLevel);

  return (
    <div className="page">
      {selectedMdi && (
        <DetailModal
          title={selectedMdi.title ?? "MDI Alert"}
          subtitle={`${selectedMdi.severity} · ${selectedMdi.category ?? "Defender for Identity"}`}
          onClose={() => setSelectedMdi(null)}
          portalUrl={selectedMdi.alertWebUrl ?? (selectedMdi.id ? `https://security.microsoft.com/alerts/${selectedMdi.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Alert ID" value={selectedMdi.id} copy/>
          {selectedMdi.alertWebUrl && <DetailField label="Portal URL" value="Open in Defender XDR" onNavigate={()=>window.open(selectedMdi.alertWebUrl, "_blank")} navLabel="Open →"/>}
          <DetailField label="Severity" value={selectedMdi.severity}/>
          <DetailField label="Status" value={selectedMdi.status}/>
          <DetailField label="Category" value={selectedMdi.category}/>
          <DetailField label="Created" value={fmtFullTime(selectedMdi.createdDateTime)} title={fmtDate(selectedMdi.createdDateTime)}/>
          {selectedMdi.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMdi.description}</div></>}
          {(selectedMdi.mitreTechniques?.length ?? 0) > 0 && (
            <><div className="dm-section-hdr">MITRE Techniques</div>
            <div className="mitre-tags">{selectedMdi.mitreTechniques.map(t=><a key={t} href={`https://attack.mitre.org/techniques/${t}`} target="_blank" rel="noopener noreferrer" className="mitre-tag">{t}</a>)}</div></>
          )}
        </DetailModal>
      )}
      {selectedDetection && (
        <DetailModal
          title={selectedDetection.riskEventType?.replace(/([A-Z])/g," $1").trim() ?? "Risk Detection"}
          subtitle={`${selectedDetection.userPrincipalName ?? selectedDetection.userDisplayName ?? "Unknown user"}`}
          onClose={() => setSelectedDetection(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_IAM/IdentityProtectionMenuBlade/~/RiskDetections"
          portalLabel="View in Entra ID Protection"
        >
          <DetailField label="User Display Name" value={selectedDetection.userDisplayName}/>
          <DetailField label="User Principal Name" value={selectedDetection.userPrincipalName} copy={!!selectedDetection.userPrincipalName} onNavigate={selectedDetection.userPrincipalName ? () => { setSelectedDetection(null); setSearch(selectedDetection.userPrincipalName!); } : undefined} navLabel="Filter user"/>
          <DetailField label="Detection ID" value={selectedDetection.id} copy={!!selectedDetection.id}/>
          <DetailField label="Risk Event Type" value={selectedDetection.riskEventType?.replace(/([A-Z])/g," $1").trim()}/>
          <DetailField label="Risk Level" value={selectedDetection.riskLevel}/>
          <DetailField label="Risk State" value={selectedDetection.riskState}/>
          <DetailField label="IP Address" value={selectedDetection.ipAddress} copy={!!selectedDetection.ipAddress}/>
          <DetailField label="Location" value={[selectedDetection.city, selectedDetection.country].filter(Boolean).join(", ")||null}/>
          <DetailField label="Activity DateTime" value={fmtFullTime(selectedDetection.activityDateTime)} title={fmtDate(selectedDetection.activityDateTime)}/>
          <DetailField label="Last Updated" value={fmtFullTime(selectedDetection.lastUpdatedDateTime)} title={fmtDate(selectedDetection.lastUpdatedDateTime)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Key size={18}/>} label="MFA COVERAGE" value={`${mfaPct}%`}
          sub={`${identity?.mfa.registered??0} of ${identity?.mfa.total??0} users`} tone={pctTone(mfaPct,95,80)}
          onClick={() => { setSearch("mfa"); }}/>
        <KpiTile icon={<UserX size={18}/>} label="RISKY USERS" value={riskyUsers.length}
          sub="Active risk detections" tone={riskyUsers.length===0?"good":riskyUsers.length<=3?"warning":"error"}
          active={!showResolved} onClick={() => { setSearch(""); setRiskLevel(""); setShowResolved(false); }}/>
        <KpiTile icon={<Globe size={18}/>} label="FOREIGN SIGN-INS" value={identity?.signIns.foreign??0}
          sub="Last 7 days" tone={(identity?.signIns.foreign??0)===0?"good":"warning"}
          onClick={() => { document.getElementById("foreign-signins-section")?.scrollIntoView({ behavior: "smooth" }); }}/>
        <KpiTile icon={<Users size={18}/>} label="GUEST ACCOUNTS" value={identity?.guests.total??0}
          sub="External users" tone={((identity?.guests.total??0)>20)?"warning":"good"}/>
      </div>

      <div className="sticky-filter-bar filters-bar">
        <label className="search-box">
          <Search size={15}/>
          <input value={search} onChange={e=>setSearch(e.target.value)}
            placeholder="Search across all identity data — user, alert, role…" className="search-input"/>
        </label>
        <select value={riskLevel} onChange={e=>setRiskLevel(e.target.value)} className="filter-sel">
          <option value="">All risk levels</option>
          <option value="critical">Critical</option>
          <option value="high">High</option>
          <option value="medium">Medium</option>
          <option value="low">Low</option>
          <option value="informational">Informational</option>
        </select>
        <label className="toggle-label">
          <input type="checkbox" checked={showResolved} onChange={e=>setShowResolved(e.target.checked)}/>
          Show resolved
        </label>
        <ExportDropdown rows={[
          ...riskyUsers.map(a=>({ Section:"Risky Users", User:a.userPrincipalName??"", Severity:a.severity, Resolved:String(a.isResolved), Detected:a.detectedAt })),
          ...riskySignIns.map(a=>({ Section:"Risky Sign-ins", User:a.userPrincipalName??"", Severity:a.severity, Resolved:String(a.isResolved), Detected:a.detectedAt })),
          ...filteredMdiAlerts.map(a=>({ Section:"MDI Alerts", User:"", Severity:a.severity, Resolved:"", Detected:a.createdDateTime??"" })),
          ...filteredDetections.map(d=>({ Section:"Risk Detections", User:d.userPrincipalName??d.userDisplayName??"", Severity:d.riskLevel??"", Resolved:d.riskState??"", Detected:d.activityDateTime??"" })),
          ...filteredPim.map(p=>({ Section:"PIM Activations", User:p.principalDisplayName??p.principalUpn??"", Severity:p.roleName??"", Resolved:"", Detected:p.createdDateTime??"" })),
        ]} filename="identity-export.csv"/>
        {hasFilter&&<button className="btn-apply" onClick={()=>{setSearch("");setRiskLevel("");}}>Clear filters</button>}
        {search && (
          <span className="search-summary">
            {[
              mfaMissing.length > 0 && `${mfaMissing.length} MFA`,
              riskySignIns.length > 0 && `${riskySignIns.length} sign-in`,
              riskyUsers.length > 0 && `${riskyUsers.length} risky user`,
              filteredMdiAlerts.length > 0 && `${filteredMdiAlerts.length} MDI`,
              filteredDetections.length > 0 && `${filteredDetections.length} detection`,
            ].filter(Boolean).join(", ") || "0 matches"} match
          </span>
        )}
        <FilterPresets pageKey="identity" filters={{search,riskLevel}}
          onLoad={({search:s,riskLevel:r})=>{setSearch(s??"");setRiskLevel(r??"");}}/>
      </div>

      {/* Row 1: MFA (left, primary) + Risky Users (right) */}
      <div className="two-col">
        <Card title="MFA Registration Status" badge={<Badge label={`${mfaPct}% covered · ${mfaMissing.length} missing`} tone={pctTone(mfaPct,95,80)}/>}>
          <div className="mfa-hero">
            <CircleGauge pct={mfaPct} size={90} color={mfaPct>=95?"var(--status-good-icon)":mfaPct>=80?"var(--status-warn-icon)":"var(--status-error-icon)"}/>
            <div className="mfa-stats">
              <InfoRow label="MFA Registered" value={<><CheckCircle size={13} color="var(--status-good-icon)"/> {identity?.mfa.registered??0} users</>} tone="good"/>
              <InfoRow label="MFA Missing" value={<><XCircle size={13} color="var(--status-error-icon)"/> {mfaMissing.length} users</>} tone="error"/>
              <InfoRow label="Total Users" value={identity?.mfa.total??0}/>
            </div>
          </div>
          <ProgressBar pct={mfaPct}/>
          {mfaMissing.length>0?(
            <div className="mini-list" style={{marginTop:14}}>
              <SectHdr>USERS WITHOUT MFA ({mfaMissing.length})</SectHdr>
              {mfaMissing.slice(0,8).map((a,i)=>(
                <div key={i} className="mini-row al-clickable" {...rowActivation(()=>onAlertClick(a))}>
                  <UserX size={12} color="var(--status-error-icon)"/>
                  <span className="mr-user">{a.userPrincipalName}</span>
                  <Badge label="No MFA" tone="error"/>
                </div>
              ))}
              {mfaMissing.length>8&&<div className="more-link">+{mfaMissing.length-8} more</div>}
            </div>
          ):!identity?(
            <EmptyState message="Run a collection to load MFA data"/>
          ):mfaPct===0&&(identity?.mfa.total??0)===0?(
            <InlineError title="No MFA data collected" perm="AuditLog.Read.All"/>
          ):(
            <EmptyState message="All users have MFA registered"/>
          )}
        </Card>

        <Card title="Risky Users" badge={<><Badge label={riskyUsers.length===0?"None":"Needs review"} tone={riskyUsers.length===0?"good":"error"}/><span className="card-count">{riskyUsers.length}</span></>}>
          {riskyUsers.length===0
            ?<EmptyState icon={<UserCheck size={28}/>} message="No risky users detected"/>
            :(
              <div className="alert-list">
                {riskyUsers.slice(0,8).map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <span className="sev-dot" style={{background:sevColor(a.severity)}}/>
                    <div className="al-body">
                      <div className="al-title">{a.userPrincipalName??a.title}</div>
                      <div className="al-desc">{a.description}</div>
                    </div>
                    <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
      </div>

      {/* Row 2: Risky Sign-ins + Risk Detections */}
      <div className="two-col">
        <Card title="Risky Sign-ins" badge={<><Badge label={`${riskySignIns.length} risky`} tone={riskySignIns.length>0?"warning":"good"}/><span className="card-count">{riskySignIns.length}</span></>}>
          <div className="stat-row3" style={{marginBottom:14}}>
            <StatBox value={identity?.signIns.total??0} label="Total (24h)"/>
            <StatBox value={riskySignIns.length} label="Risky" color={riskySignIns.length>0?"var(--status-warn-text)":undefined}/>
            <StatBox value={identity?.signIns.foreign??0} label="Foreign" color={(identity?.signIns.foreign??0)>0?"var(--status-error-text)":undefined}/>
          </div>
          {riskySignIns.length>0?(
            <div className="alert-list">
              <div className="list-count">{riskySignIns.length} risky sign-in{riskySignIns.length!==1?"s":""}</div>
              <SectHdr>RECENT RISKY SIGN-INS — click to view</SectHdr>
              {riskySignIns.slice(0,6).map((a,i)=>(
                <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                  <span className="sev-dot" style={{background:sevColor(a.severity)}}/>
                  <div className="al-body">
                    <div className="al-title">{a.userPrincipalName??a.title}</div>
                    <div className="al-desc">{a.description}</div>
                  </div>
                  <span className="al-date">{fmtDate(a.detectedAt)}</span>
                </div>
              ))}
            </div>
          ):<EmptyState icon={<ShieldCheck size={28}/>} message="No risky sign-ins detected"/>}
          {filteredForeignSignIns.length > 0 && (
            <div id="foreign-signins-section" className="alert-list" style={{ marginTop: 18, borderTop: "1px solid var(--color-border)", paddingTop: 12 }}>
              <SectHdr>FOREIGN SIGN-INS ({filteredForeignSignIns.length})</SectHdr>
              {filteredForeignSignIns.slice(0, 5).map((s, i) => (
                <div key={i} className="al-item al-item-noclick" style={{ cursor: "default" }}>
                  <Globe size={14} style={{ color: "var(--status-warn-text)", flexShrink: 0, marginTop: 2 }} />
                  <div className="al-body">
                    <div className="al-title">{s.userPrincipalName ?? s.title}</div>
                    <div className="al-desc">{s.title !== s.userPrincipalName ? s.title : "Foreign sign-in detected"}</div>
                  </div>
                  <span className="al-date">{fmtFullTime(s.detectedAt)}</span>
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card title="Risk Detections" badge={<><Badge label={`${riskDetections?.total??0} detections`} tone={(riskDetections?.total??0)>0?"error":"good"}/><span className="card-count">{filteredDetections.length}</span></>}>
          {riskDetections?.error
            ?<EmptyState icon={<AlertTriangle size={28}/>} message="Needs IdentityRiskEvent.Read.All"/>
            :(riskDetections?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28}/>} message="No risk detections — no leaked credentials, password spray, or MITM attacks found"/>
              :(
                <>
                  <div className="stat-row3" style={{marginBottom:14}}>
                    <StatBox value={riskDetections!.byLevel?.["high"]??0} label="High" color={(riskDetections!.byLevel?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={riskDetections!.byLevel?.["medium"]??0} label="Medium" color={(riskDetections!.byLevel?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={riskDetections!.byLevel?.["low"]??0} label="Low"/>
                  </div>
                  <div className="mini-list">
                    <SectHdr>BY DETECTION TYPE</SectHdr>
                    {Object.entries(riskDetections!.byType).slice(0,7).map(([type,count])=>(
                      <div key={type} className="mini-row">
                        <AlertCircle size={11} color="var(--status-warn-icon)"/>
                        <span className="mr-user" style={{flex:1}}>{type.replace(/([A-Z])/g," $1").replace(/^./,c=>c.toUpperCase()).trim()}</span>
                        <Badge label={String(count)} tone={count>0?"warning":"neutral"}/>
                      </div>
                    ))}
                  </div>
                  <div className="mini-list" style={{marginTop:10}}>
                    <div className="list-count">{filteredDetections.length} of {riskDetections?.total??0} detection{(riskDetections?.total??0)!==1?"s":""}</div>
                    <SectHdr>DETECTIONS — {filteredDetections.length} shown</SectHdr>
                    {filteredDetections.length===0&&<div className="td-empty" style={{padding:8}}>No detections match the filter.</div>}
                    {filteredDetections.slice(0,6).map((d,i)=>(
                      <div key={d.id??i} className="al-item" onClick={()=>setSelectedDetection(d)}>
                        <span className={`sev-dot sev-${d.riskLevel.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{d.userPrincipalName?.split("@")[0]??d.userDisplayName??"Unknown"}</div>
                          <div className="row-meta">
                            <span className="row-meta-item">{d.riskEventType?.replace(/([A-Z])/g," $1").trim()}</span>
                            {d.city&&<span className="row-meta-item">{[d.city,d.country].filter(Boolean).join(", ")}</span>}
                            <span className="row-meta-item">{relTime(d.activityDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={d.riskLevel} tone={d.riskLevel==="high"?"error":d.riskLevel==="medium"?"warning":"neutral"}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>
      </div>

      {/* Row 3: MDI Alerts + PIM Role Activations */}
      <div className="two-col">
        <Card title="Defender for Identity Alerts" badge={<><Badge label={`${filteredMdiAlerts.length} / ${mdiAlerts?.total??0} alerts`} tone={(mdiAlerts?.total??0)>0?"error":"good"}/><span className="card-count">{filteredMdiAlerts.length}</span></>}>
          {mdiAlerts?.error
            ?<EmptyState icon={<ShieldAlert size={28}/>} message="Needs SecurityAlert.Read.All"/>
            :(mdiAlerts?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28}/>} message="No Defender for Identity alerts — no on-prem AD threats detected"/>
              :(
                <>
                  <div className="stat-row4" style={{marginBottom:14}}>
                    <StatBox value={mdiAlerts!.bySeverity?.["high"]??0} label="High" color={(mdiAlerts!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={mdiAlerts!.bySeverity?.["medium"]??0} label="Medium" color={(mdiAlerts!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={mdiAlerts!.bySeverity?.["low"]??0} label="Low"/>
                    <StatBox value={mdiAlerts!.bySeverity?.["informational"]??0} label="Info"/>
                  </div>
                  <div className="alert-list">
                    <div className="list-count">{filteredMdiAlerts.length} of {mdiAlerts?.total??0} MDI alert{(mdiAlerts?.total??0)!==1?"s":""}</div>
                    <SectHdr>MDI ALERTS — {filteredMdiAlerts.length} shown</SectHdr>
                    {filteredMdiAlerts.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                    {filteredMdiAlerts.slice(0,8).map((a,i)=>(
                      <div key={a.id??i} className="al-item" onClick={()=>setSelectedMdi(a)}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{a.title}</div>
                          <div className="row-meta">
                            {a.category&&<span className="row-meta-item">{a.category}</span>}
                            {a.status&&<Badge label={a.status} tone="neutral"/>}
                            <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":a.severity==="medium"||a.severity==="Medium"?"warning":"neutral"}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>

        <Card title="PIM Role Activations" badge={<Badge label={`${filteredPim.length} / ${pimData?.total??0} recent`} tone="neutral"/>}>
          {pimData?.error
            ?<EmptyState icon={<Key size={28}/>} message="Needs RoleManagement.Read.Directory"/>
            :(pimData?.activations.length??0)===0
              ?<EmptyState icon={<Clock size={28}/>} message="No recent role activations"/>
              :(
                <div className="act-list">
                  {filteredPim.length===0&&<div className="td-empty" style={{padding:12}}>No activations match the filter.</div>}
                  {filteredPim.map((a,i)=>(
                    <div key={a.id??i} className="act-row">
                      <div className={`act-badge act-${a.status==="Provisioned"||a.status==="Completed"?"good":"neutral"}`}>
                        <Key size={12}/>
                      </div>
                      <div className="act-body">
                        <span className="act-who">{a.principalDisplayName??a.principalUpn?.split("@")[0]??"Unknown"}</span>
                        <span className="act-what"> {a.roleName} · {a.action}</span>
                      </div>
                      <span className="act-date">{fmtDate(a.createdDateTime)}</span>
                    </div>
                  ))}
                </div>
              )
          }
        </Card>
      </div>

      {/* Row 4: Privileged Roles + Recent Admin Activity (informational, lower priority) */}
      <div className="two-col">
        <Card title="Privileged Roles" badge={<Badge label={`${privilegedRoles?.totalPrivilegedUsers??0} privileged users`} tone={(privilegedRoles?.totalPrivilegedUsers??0)>0?"warning":"good"}/>}>
          {privilegedRoles?.error
            ?<EmptyState icon={<ShieldAlert size={28}/>} message={`Could not load privileged roles: ${privilegedRoles.error}`}/>
            :(privilegedRoles?.roles.length??0)===0
              ?<EmptyState icon={<ShieldCheck size={28}/>} message="No high-privilege role members found"/>
              :(
                <div className="mini-list">
                  {privilegedRoles!.roles.map((r,i)=>{
                    const isGA = r.roleName==="Global Administrator";
                    return (
                      <div key={r.roleId??i} style={{marginBottom:12}}>
                        <div className="mini-row">
                          <Lock size={12} color={isGA?"var(--status-error-icon)":"var(--color-faint)"}/>
                          <span className="mr-user">{r.roleName}</span>
                          <Badge label={`${r.memberCount} member${r.memberCount===1?"":"s"}`} tone={isGA&&r.memberCount>0?"error":r.memberCount>0?"warning":"neutral"}/>
                        </div>
                        {r.members.slice(0,5).map((m,j)=>(
                          <div key={j} className="mini-row" style={{paddingLeft:22}}>
                            <User size={11}/>
                            <span className="mr-user">{m.userPrincipalName??m.displayName??"Unknown"}</span>
                          </div>
                        ))}
                        {r.members.length>5&&<div className="more-link">+{r.members.length-5} more</div>}
                      </div>
                    );
                  })}
                </div>
              )
          }
        </Card>

        <Card title="Recent Admin Activity" badge={<Badge label={`${identity?.recentAdminActivity.length??0} events`} tone="neutral"/>}>
          {(identity?.recentAdminActivity.length??0)===0
            ?<EmptyState icon={<Clock size={28}/>} message="Requires AuditLog.Read.All permission"/>
            :(
              <div className="act-list">
                {identity!.recentAdminActivity.map((a,i)=>(
                  <div key={i} className="act-row">
                    <div className={`act-badge act-${a.result==="success"?"good":"neutral"}`}>
                      {a.result==="success"?<CheckCircle size={12}/>:<AlertCircle size={12}/>}
                    </div>
                    <div className="act-body">
                      <span className="act-who">{a.initiatedByUser?.split("@")[0]??"System"}</span>
                      <span className="act-what"> {a.activityDisplayName}</span>
                    </div>
                    <span className="act-date">{fmtDate(a.activityDateTime)}</span>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
      </div>

      <Card title="Identity Sensor Health" badge={<Badge label={(identityHealth?.total??0)===0?"All Healthy":`${identityHealth?.total??0} issues`} tone={(identityHealth?.issues?.length??0)===0?"good":"error"}/>}>
        {identityHealth?.error
          ?<EmptyState icon={<AlertTriangle size={28}/>} message="Needs IdentityBaseline.Read.All — add permission in Azure App Registration"/>
          :(identityHealth?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={36}/>} message="All identity sensors reporting healthy — no MDI sensor gaps detected"/>
            :(
              <div className="alert-list">
                {identityHealth!.issues.map((iss,i)=>(
                  <div key={iss.id??i} className="al-item al-item-noclick">
                    <span className={`sev-dot sev-${iss.severity.toLowerCase()}`}/>
                    <div className="al-body">
                      <div className="al-title">{iss.displayName??iss.issueType??"Health Issue"}</div>
                      {iss.description&&<div className="al-desc">{iss.description}</div>}
                      {iss.sensorDNSNames.length>0&&<div className="al-desc">Sensor: {iss.sensorDNSNames.join(", ")}</div>}
                      {iss.recommendations&&<div className="al-desc tone-info">Fix: {iss.recommendations}</div>}
                    </div>
                    <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:4}}>
                      <Badge label={iss.severity} tone={iss.severity==="high"||iss.severity==="critical"?"error":"warning"}/>
                      <span className="al-date">{fmtDate(iss.createdDateTime)}</span>
                    </div>
                  </div>
                ))}
              </div>
            )
        }
      </Card>
    </div>
  );
}
