import React, { useMemo } from "react";
import { Shield, Lock, Monitor, Activity, ShieldAlert, Flag, TrendingUp, ChevronRight, User, Database, Bell, CheckCircle } from "lucide-react";
import { Overview, SecureScore, IdentityData, DevicesData, ServiceHealthData, SecurityAlert, DefenderAlertsData, SecurityIncidentsData, AlertPolicy, TriggeredAlert } from "../services/types";
import { pctTone, fmtShort, fmtService, fmtDefenderSource, relTime, fmtDate, fmtFullTime, sevClass } from "../services/utils";
import { crossNavigate } from "../services/api";
import { Card, KpiTile, CircleGauge, LineChart, StatBox, SectHdr, Badge, EmptyState } from "../components/SharedComponents";
import { CollectionHealthCard } from "../components/CollectionHealthCard";

export function OverviewPage({ overview, secureScore, identity, devices, serviceHealth, alerts, defenderAlerts, securityIncidents, onAlertClick, onNavigateAlertCenter, alertPolicies, overviewTriggered, healthRefreshKey }:
  { overview:Overview|null; secureScore:SecureScore|null; identity:IdentityData|null; devices:DevicesData|null; serviceHealth:ServiceHealthData|null; alerts:SecurityAlert[]; defenderAlerts:DefenderAlertsData|null; securityIncidents:SecurityIncidentsData|null; onAlertClick:(a:SecurityAlert)=>void; onNavigateAlertCenter:()=>void; alertPolicies:AlertPolicy[]; overviewTriggered:TriggeredAlert[]; healthRefreshKey:number }) {

  const trendData = useMemo(() =>
    (secureScore?.trend??[]).map(t => ({ date:t.date, value:t.maxScore>0?+(t.score/t.maxScore*100).toFixed(1):0 })), [secureScore]);

  const posturePct = useMemo(() => {
    if (!overview||overview.totalActive===0) return 0;
    return +((overview.highPriority/overview.totalActive)*100).toFixed(1);
  }, [overview]);

  const activeAlerts = useMemo(() => alerts.filter(a => !a.isResolved), [alerts]);
  const mfaMissingCount = useMemo(() => activeAlerts.filter(a=>a.alertType==="MfaStatus").length, [activeAlerts]);
  const mfaPct = (identity?.mfa.total??0) > 0 ? (identity?.mfa.percentage??0) : 0;
  const mfaKnown = (identity?.mfa.total??0) > 0;

  const devNonCompliant = devices?.nonCompliant??0;
  const devEffectiveTotal = Math.max(devices?.totalDevices??0, devNonCompliant);
  const devComplPct = devEffectiveTotal > 0
    ? Math.max(0, Math.round((devEffectiveTotal - devNonCompliant) / devEffectiveTotal * 100))
    : (devNonCompliant === 0 ? 100 : 0);

  return (
    <div className="page">
      {(() => {
        const lr = overview?.lastRun;
        const completed = lr?.completedAt;
        const ageMin = completed ? (Date.now() - new Date(completed).getTime()) / 60000 : null;
        const stale = ageMin != null && ageMin > 45; // > ~3 collection cycles
        const failed = lr?.sourceFailures ?? 0;
        const ok = !!completed && !stale && failed === 0;
        const tone: "good" | "warn" | "neutral" = !completed ? "neutral" : ok ? "good" : "warn";
        const c = {
          good:    { bg: "var(--status-good-bg)",  bd: "var(--status-good-border)",  fg: "var(--status-good-text)",  dot: "var(--status-good-icon)" },
          warn:    { bg: "var(--status-warn-bg)",  bd: "var(--status-warn-border)",  fg: "var(--status-warn-text)",  dot: "var(--status-warn-icon)" },
          neutral: { bg: "var(--color-raised)",    bd: "var(--color-border)",        fg: "var(--color-muted)",       dot: "var(--color-faint)" },
        }[tone];
        const msg = !completed
          ? "No collection yet — run a collection to populate the dashboard"
          : `Data collected ${relTime(completed)}${stale ? " — stale, run a collection" : ""}${failed > 0 ? ` · ${failed} source${failed > 1 ? "s" : ""} failed` : " · all sources OK"}`;
        return (
          <div role="status" aria-live="polite"
            style={{ display: "flex", alignItems: "center", gap: 8, padding: "7px 14px", marginBottom: 12,
              borderRadius: 8, background: c.bg, border: `1px solid ${c.bd}`, color: c.fg, fontSize: 12.5 }}>
            <span style={{ width: 8, height: 8, borderRadius: "50%", background: c.dot, flexShrink: 0 }}/>
            <span>{msg}</span>
            {(stale || failed > 0) && (
              <button onClick={() => crossNavigate({ page: "servicehealth" })}
                style={{ marginLeft: "auto", background: "none", border: "none", color: c.fg, textDecoration: "underline", cursor: "pointer", fontSize: 12 }}>
                Details →
              </button>
            )}
          </div>
        );
      })()}
      <div className="kpi-row">
        <KpiTile icon={<Shield size={18}/>} label="SECURE SCORE"
          value={secureScore?.configured&&!secureScore.error?`${secureScore.percentage}%`:"—"}
          sub={secureScore?.configured&&!secureScore.error?`${Math.round(secureScore.currentScore)} / ${Math.round(secureScore.maxScore)} pts`:"Run collection to load"}
          needsPerm={!!secureScore && (!secureScore.configured || !!secureScore.error)}
          tone={secureScore?.configured&&!secureScore.error?pctTone(secureScore.percentage):"neutral"}
          onClick={() => crossNavigate({ page: "compliance" })}/>
        <KpiTile icon={<Lock size={18}/>} label="MFA COVERAGE"
          value={mfaKnown ? `${mfaPct}%` : mfaMissingCount > 0 ? `${mfaMissingCount}` : identity?.configured ? "—" : "—"}
          sub={mfaKnown ? `${identity!.mfa.registered}/${identity!.mfa.total} users` : mfaMissingCount > 0 ? `users missing MFA` : identity?.configured ? "Needs Reports.Read.All" : "Run collection"}
          needsPerm={!!identity && identity.configured && !mfaKnown && mfaMissingCount === 0}
          tone={mfaKnown ? pctTone(mfaPct,95,80) : mfaMissingCount > 0 ? "error" : "neutral"}
          onClick={() => crossNavigate({ page: "identity", search: "mfa" })}/>
        <KpiTile icon={<Monitor size={18}/>} label="DEVICE COMPLIANCE"
          value={devices ? (devNonCompliant===0 && devEffectiveTotal===0 ? "—" : devNonCompliant===0 ? "All OK" : `${devNonCompliant} issues`) : "—"}
          sub={devices ? (devEffectiveTotal>0 ? `${Math.max(0,devEffectiveTotal-devNonCompliant)}/${devEffectiveTotal} compliant` : `${devNonCompliant} non-compliant`) : "Run collection"}
          tone={devNonCompliant===0?"good":devNonCompliant<=3?"warning":"error"}
          onClick={() => crossNavigate({ page: "devices" })}/>
        <KpiTile icon={<Activity size={18}/>} label="POSTURE RISK"
          value={<span style={{color: posturePct>10?"#b91c1c":undefined}}>{posturePct}%</span>}
          sub={`${overview?.highPriority??0} high / ${overview?.totalActive??0} active`}
          tone={posturePct===0?"neutral":posturePct<=10?"good":posturePct<=25?"warning":"error"}
          onClick={() => crossNavigate({ page: "trends" })}/>
        <KpiTile icon={<ShieldAlert size={18}/>} label="DEFENDER ALERTS"
          value={defenderAlerts?.configured && !defenderAlerts.error ? `${defenderAlerts.total}` : "—"}
          sub={defenderAlerts?.configured && !defenderAlerts.error
            ? `${defenderAlerts.bySeverity?.["high"]??0} high / ${defenderAlerts.bySeverity?.["critical"]??0} critical`
            : defenderAlerts?.error ? "Needs SecurityAlert.Read.All" : "Run collection"}
          needsPerm={!!defenderAlerts?.error}
          tone={!defenderAlerts?.configured||defenderAlerts.error?"neutral":(defenderAlerts.bySeverity?.["critical"]??0)>0?"error":(defenderAlerts.bySeverity?.["high"]??0)>0?"warning":"good"}
          onClick={onNavigateAlertCenter}/>
        <KpiTile icon={<Flag size={18}/>} label="INCIDENTS"
          value={securityIncidents?.configured && !securityIncidents.error ? `${securityIncidents.total}` : "—"}
          sub={securityIncidents?.configured && !securityIncidents.error
            ? `${securityIncidents.bySeverity?.["high"]??0} high / ${securityIncidents.bySeverity?.["critical"]??0} critical`
            : securityIncidents?.error ? "Needs SecurityIncident.Read.All" : "Run collection"}
          needsPerm={!!securityIncidents?.error}
          tone={!securityIncidents?.configured||securityIncidents.error?"neutral":(securityIncidents.bySeverity?.["critical"]??0)>0?"error":(securityIncidents.bySeverity?.["high"]??0)>0?"warning":"good"}
          onClick={() => crossNavigate({ page: "incidents" })}/>
      </div>

      <div className="mid-row">
        <Card title="Secure Score Trend" badge={<Badge label={trendData.length>30?"90 Days":trendData.length>0?`${trendData.length} Days`:"No data"} tone="neutral"/>} className="card-score">
          {secureScore?.configured&&trendData.length>1?(
            <>
              <div className="score-hero">
                <CircleGauge pct={secureScore.percentage} size={80}/>
                <div>
                  <div className="score-big">{secureScore.percentage}%</div>
                  <div className="score-meta">{Math.round(secureScore.currentScore)} / {Math.round(secureScore.maxScore)} pts</div>
                  <div className="score-meta" style={{marginTop:4}}>Updated {fmtShort(secureScore.trend.at(-1)?.date)}</div>
                </div>
              </div>
              <LineChart data={trendData}/>
            </>
          ):(
            <EmptyState message={secureScore?.configured?"Collecting trend data — check back after first run":"Configure Graph credentials then run a collection"}/>
          )}
        </Card>
        <Card title="Defender Alerts"
          badge={defenderAlerts?.configured && !defenderAlerts.error
            ? <Badge label={`${defenderAlerts.total} active`} tone={defenderAlerts.total>0?"error":"good"}/>
            : <Badge label="No data" tone="neutral"/>}>
          {defenderAlerts?.configured && !defenderAlerts.error && defenderAlerts.total > 0 ? (
            <>
              <div className="stat-row4">
                {(["critical","high","medium","low"] as const).map(sev=>(
                  <StatBox key={sev} value={defenderAlerts.bySeverity?.[sev]??0} label={sev.charAt(0).toUpperCase()+sev.slice(1)}
                    color={sev==="critical"?"var(--dot-critical)":sev==="high"?"var(--dot-high)":sev==="medium"?"var(--dot-medium)":"var(--dot-info)"}/>
                ))}
              </div>
              <div className="mini-list" style={{marginTop:8}}>
                <SectHdr>RECENT ALERTS</SectHdr>
                {defenderAlerts.alerts.slice(0,5).map((a,i)=>(
                  <div key={i} className="mini-row al-clickable" onClick={onNavigateAlertCenter} style={{ cursor: "pointer" }}>
                    <span className={sevClass(a.severity)}/>
                    <span className="mr-user" style={{flex:1}}>{a.title??"Unknown"}</span>
                    <Badge label={fmtDefenderSource(a.serviceSource??a.severity)} tone="neutral"/>
                  </div>
                ))}
              </div>
            </>
          ) : defenderAlerts?.error ? (
            <EmptyState icon={<ShieldAlert size={24} color="#d1d5db"/>} message={`Needs SecurityAlert.Read.All permission`}/>
          ) : (
            <EmptyState icon={<ShieldAlert size={24} color="#d1d5db"/>} message="Run a collection to load Defender alerts"/>
          )}
        </Card>
        <Card title="Top Active Alerts"
          badge={<Badge label={`${activeAlerts.length} active`} tone={activeAlerts.length>0?"error":"good"}/>}>
          {activeAlerts.length > 0 ? (
            <div className="mini-list">
              {activeAlerts
                .sort((a,b)=>{ const o=["Critical","High","Medium","Low","Informational"]; return o.indexOf(a.severity)-o.indexOf(b.severity); })
                .slice(0,6).map((a,i)=>(
                  <div key={i} className="mini-row act-clickable" onClick={()=>onAlertClick(a)} style={{cursor:"pointer"}} title={fmtFullTime(a.detectedAt)}>
                    <span className={sevClass(a.severity)}/>
                    <span className="mr-user" style={{flex:1}}>{a.title}</span>
                    <Badge label={a.service==="EntraId"?"Entra":a.service==="DefenderXdr"?"Defender":a.service==="Intune"?"Intune":a.service==="ExchangeOnline"?"Exchange":"Health"} tone="neutral"/>
                  </div>
                ))}
            </div>
          ) : (
            <EmptyState icon={<CheckCircle size={24} color="#22c55e"/>} message="No active alerts — environment looks healthy"/>
          )}
        </Card>
      </div>

      <div className="lower-row">
        <Card title="Risky Users"
          badge={(() => { const r = alerts.filter(a=>a.alertType==="RiskyUser"&&!a.isResolved).length; return r>0?<Badge label={`${r} at risk`} tone="error"/>:<Badge label="All clear" tone="good"/>; })()}>
          {(() => {
            const riskyUsers = alerts.filter(a=>a.alertType==="RiskyUser"&&!a.isResolved);
            const mfaMissing = alerts.filter(a=>a.alertType==="MfaStatus"&&!a.isResolved);
            const risky = alerts.filter(a=>a.alertType==="RiskySignIn"&&!a.isResolved);
            return (
              <>
                <div className="stat-row3">
                  <StatBox value={riskyUsers.length} label="Risky Users" color={riskyUsers.length>0?"var(--status-error-text)":undefined}/>
                  <StatBox value={mfaMissing.length} label="No MFA" color={mfaMissing.length>0?"var(--status-warn-text)":undefined}/>
                  <StatBox value={risky.length} label="Risky Sign-ins" color={risky.length>0?"var(--status-warn-text)":undefined}/>
                </div>
                {riskyUsers.length>0&&(
                  <div className="mini-list">
                    <SectHdr>AT-RISK USERS</SectHdr>
                    {riskyUsers.slice(0,4).map((a,i)=>(
                      <div key={i} className="mini-row act-clickable" onClick={()=>onAlertClick(a)} style={{cursor:"pointer"}}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <span className="mr-user">{a.userPrincipalName??a.title}</span>
                        <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                      </div>
                    ))}
                  </div>
                )}
                {riskyUsers.length===0&&<EmptyState icon={<Activity size={24} color="#22c55e"/>} message="No risky users detected"/>}
              </>
            );
          })()}
        </Card>
        <Card title="Recent High Alerts"
          badge={<Badge label={`${activeAlerts.filter(a=>a.severity==="High"||a.severity==="Critical").length} high/critical`} tone={activeAlerts.filter(a=>a.severity==="High"||a.severity==="Critical").length>0?"error":"good"}/>}>
          {activeAlerts.filter(a=>a.severity==="High"||a.severity==="Critical").length>0 ? (
            <div className="mini-list">
              {activeAlerts.filter(a=>a.severity==="High"||a.severity==="Critical")
                .sort((a,b)=>new Date(b.detectedAt).getTime()-new Date(a.detectedAt).getTime())
                .slice(0,6).map((a,i)=>(
                  <div key={i} className="mini-row act-clickable" onClick={()=>onAlertClick(a)} style={{cursor:"pointer"}} title={fmtFullTime(a.detectedAt)}>
                    <span className={sevClass(a.severity)}/>
                    <span className="mr-user" style={{flex:1}}>{a.title}</span>
                    <span className="mr-date">{fmtShort(a.detectedAt)}</span>
                  </div>
                ))}
            </div>
          ) : (
            <EmptyState icon={<Activity size={24} color="#22c55e"/>} message="No high or critical alerts"/>
          )}
        </Card>
        <Card title="Alerts by Service">
          <div className="stat-row2">
            <StatBox value={overview?.totalActive??0} label="Total Active"/>
            <StatBox value={overview?.highPriority??0} label="High Priority" color={(overview?.highPriority??0)>0?"var(--status-error-text)":undefined}/>
          </div>
          {(overview?.byService??[]).length>0 ? (
            <div className="mini-list">
              <SectHdr>BREAKDOWN BY SERVICE</SectHdr>
              {(overview?.byService??[]).map((s,i)=>(
                <div key={i} className="mini-row al-clickable" onClick={() => crossNavigate({ page: s.service === "EntraId" ? "identity" : s.service === "DefenderXdr" ? "alertcenter" : s.service === "Intune" ? "devices" : s.service === "ExchangeOnline" ? "email" : "servicehealth" })} style={{ cursor: "pointer" }}>
                  <Database size={11} color="#6b7280"/>
                  <span className="mr-user">{fmtService(s.service)}</span>
                  <Badge label={String(s.count)} tone={s.count>10?"error":s.count>3?"warning":"neutral"}/>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState icon={<Database size={24} color="#d1d5db"/>} message="Run a collection to see alert breakdown"/>
          )}
        </Card>
        <Card title="Device Compliance"
          badge={devNonCompliant>0?<Badge label={`${devNonCompliant} non-compliant`} tone={devNonCompliant>3?"error":"warning"}/>:<Badge label="All compliant" tone="good"/>}>
          <div className="device-hero">
            <CircleGauge pct={devices ? devComplPct : 0} size={70}/>
            <div className="stat-col">
              <StatBox value={devices ? Math.max(0, devEffectiveTotal - devNonCompliant) : "—"} label="Compliant"/>
              <StatBox value={devNonCompliant} label="Non-compliant" color={devNonCompliant>0?"var(--status-error-text)":undefined}/>
            </div>
          </div>
          {(devices?.nonCompliantDevices.length??0)>0?(
            <div className="mini-list">
              {devices!.nonCompliantDevices.slice(0,3).map((d,i)=>(
                <div key={i} className="mini-row al-clickable" onClick={() => crossNavigate({ page: "devices", search: d.deviceName ?? "" })} style={{ cursor: "pointer" }}>
                  <Monitor size={11} color="#6b7280"/>
                  <span className="mr-user">{d.deviceName??"Unknown device"}</span>
                  <span className="mr-date">{d.userPrincipalName?.split("@")[0]}</span>
                </div>
              ))}
            </div>
          ):(
            !devices
              ?<div className="empty-state" style={{paddingTop:8}}><p>Run a collection to load device data</p></div>
              :devNonCompliant===0
                ?<div className="empty-state" style={{paddingTop:8}}><p>All devices are compliant</p></div>
                :<div className="empty-state" style={{paddingTop:8}}><p>{devNonCompliant} non-compliant reported by Intune summary</p></div>
          )}
        </Card>
      </div>

      <div className="footer-row">
        <Card title="Recent Admin Activity" badge={<Badge label={`${identity?.recentAdminActivity.length??0} events`} tone="neutral"/>}>
          {(identity?.recentAdminActivity.length??0)===0
            ?(
              <EmptyState icon={<User size={28} color="#d1d5db"/>}
                message="No admin activity — requires AuditLog.Read.All permission"/>
            ):(
              <div className="act-list">
                {identity!.recentAdminActivity.slice(0,6).map((a,i)=>(
                  <div key={i} className="act-row">
                    <User size={12} color="#94a3b8"/>
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
        <Card title="Top Improvement Actions" action={<button className="btn-export" onClick={() => crossNavigate({ page: "compliance" })}>View <ChevronRight size={13}/></button>}>
          <div className="impr-list">
            {(overview?.byService??[]).length===0
              ?<EmptyState message="Run a collection to see recommendations"/>
              :overview!.byService.map((s,i)=>(
                <div key={i} className="impr-row al-clickable" onClick={() => crossNavigate({ page: s.service === "EntraId" ? "identity" : s.service === "DefenderXdr" ? "alertcenter" : s.service === "Intune" ? "devices" : s.service === "ExchangeOnline" ? "email" : "servicehealth" })} style={{ cursor: "pointer" }}>
                  <div className="impr-icon"><TrendingUp size={12}/></div>
                  <span className="impr-text">Review {fmtService(s.service)} — {s.count} active alert{s.count!==1?"s":""}</span>
                  <Badge label={`+${Math.min(s.count*3,30)} pts`} tone="neutral"/>
                </div>
              ))
            }
          </div>
        </Card>
        {(() => {
          const triggered = overviewTriggered;
          const enabledPolicies = alertPolicies.filter(p => p.enabled).length;
          const todayAlerts = triggered.filter(a => new Date(a.triggeredAt).toDateString() === new Date().toDateString()).length;
          const recent3 = [...triggered].sort((a,b)=>new Date(b.triggeredAt).getTime()-new Date(a.triggeredAt).getTime()).slice(0,3);
          return (
            <Card title="Alert Policies" action={<button className="btn-export" onClick={onNavigateAlertCenter}>View All</button>}>
              <div className="stat-row2">
                <StatBox value={enabledPolicies} label="Active Policies"/>
                <StatBox value={todayAlerts} label="Triggered Today" color={todayAlerts>0?"var(--status-error-text)":undefined}/>
              </div>
              {recent3.length > 0 ? (
                <div className="mini-list" style={{marginTop:8}}>
                  <SectHdr>RECENT TRIGGERED</SectHdr>
                  {recent3.map((a,i)=>(
                    <div key={i} className="mini-row al-clickable" onClick={onNavigateAlertCenter} style={{ cursor: "pointer" }}>
                      <span className={sevClass(a.severity)}/>
                      <span className="mr-user" style={{flex:1}}>{a.policyName}</span>
                      <span className="mr-date" title={fmtFullTime(a.triggeredAt)}>{relTime(a.triggeredAt)}</span>
                    </div>
                  ))}
                </div>
              ) : (
                <EmptyState icon={<Bell size={22} color="#d1d5db"/>} message="No alerts triggered yet"/>
              )}
            </Card>
          );
        })()}
        <CollectionHealthCard refreshKey={healthRefreshKey}/>
      </div>
    </div>
  );
}
