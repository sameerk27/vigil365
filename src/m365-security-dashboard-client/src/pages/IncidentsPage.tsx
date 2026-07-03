import React, { useState, useMemo } from "react";
import { ShieldAlert, AlertCircle, AlertTriangle, Bell, Search, Eye, ExternalLink, Database } from "lucide-react";
import { SecurityAlert, ServiceHealthData, DefenderAlertsData, SecurityIncidentsData, DefenderAlert, SecurityIncident } from "../services/types";
import { fmtDate, relTime, fmtDefenderSource, fmtService, sevColor } from "../services/utils";
import { DetailModal, DetailField, KpiTile, Card, Badge, MiniBarChart, InlineError, ExportDropdown } from "../components/SharedComponents";
import { FilterPresets } from "../components/FilterPresets";

export type UnifiedItem =
  | { kind: "alert"; data: SecurityAlert }
  | { kind: "defender"; data: DefenderAlert }
  | { kind: "incident"; data: SecurityIncident }
  | { kind: "advisory"; data: { title: string; description?: string; severity: string; detectedAt: string; portalUrl?: string } };

export type IncidentFilter = "all" | "alerts" | "defender" | "incidents" | "advisories";

export function DefenderAlertModal({ alert, onClose }: { alert: DefenderAlert; onClose: () => void }) {
  const portalUrl = alert.alertWebUrl ?? (alert.id ? `https://security.microsoft.com/alerts/${alert.id}` : "https://security.microsoft.com/alerts");
  const sev = alert.severity.charAt(0).toUpperCase() + alert.severity.slice(1);
  return (
    <DetailModal
      title={alert.title ?? "Defender Alert"}
      subtitle={`${sev} · ${fmtDefenderSource(alert.serviceSource ?? "Defender")}`}
      onClose={onClose}
      portalUrl={portalUrl}
      portalLabel="View in Defender XDR"
    >
      <DetailField label="Alert ID" value={alert.id} copy={!!alert.id}/>
      <DetailField label="Severity" value={sev}/>
      <DetailField label="Status" value={alert.status}/>
      <DetailField label="Classification" value={alert.classification}/>
      <DetailField label="Service Source" value={fmtDefenderSource(alert.serviceSource ?? "")}/>
      <DetailField label="Detection Source" value={alert.detectionSource}/>
      <DetailField label="Category" value={alert.category}/>
      <DetailField label="Assigned To" value={alert.assignedTo}/>
      <DetailField label="Threat Actor" value={alert.actorDisplayName}/>
      <DetailField label="Threat" value={alert.threatDisplayName}/>
      <DetailField label="Incident ID" value={alert.incidentId} copy={!!alert.incidentId}/>
      <DetailField label="Detected" value={alert.createdDateTime ? `${relTime(alert.createdDateTime)} (${fmtDate(alert.createdDateTime)})` : undefined} title={fmtDate(alert.createdDateTime)}/>
      <DetailField label="Last Updated" value={alert.lastUpdateDateTime ? `${relTime(alert.lastUpdateDateTime)} (${fmtDate(alert.lastUpdateDateTime)})` : undefined} title={fmtDate(alert.lastUpdateDateTime)}/>
      {alert.description && (
        <>
          <div className="dm-section-hdr">Description</div>
          <div className="dm-desc-block">{alert.description}</div>
        </>
      )}
      {((Array.isArray(alert.mitreTechniques)?alert.mitreTechniques:[]).filter(Boolean)).length>0&&(
        <>
          <div className="dm-section-hdr">MITRE Techniques</div>
          <div className="mitre-tags">
            {(Array.isArray(alert.mitreTechniques)?alert.mitreTechniques:[]).filter(Boolean).map(t=>(
              <a key={t} href={`https://attack.mitre.org/techniques/${t.replace(".","/")||t}`} target="_blank" rel="noopener noreferrer" className="mitre-tag">{t}</a>
            ))}
          </div>
        </>
      )}
      {alert.recommendedActions && (
        <>
          <div className="dm-section-hdr">Recommended Actions</div>
          <div className="dm-desc-block">{alert.recommendedActions}</div>
        </>
      )}
    </DetailModal>
  );
}

export function IncidentsPage({ alerts, serviceHealth, defenderAlerts, securityIncidents, onAlertClick }:
  { alerts: SecurityAlert[]; serviceHealth: ServiceHealthData|null; defenderAlerts: DefenderAlertsData|null; securityIncidents: SecurityIncidentsData|null; onAlertClick:(a:SecurityAlert)=>void }) {
  const [search, setSearch] = useState("");
  const [severity, setSeverity] = useState("");
  const [typeFilter, setTypeFilter] = useState<IncidentFilter>("all");
  const [dateRange, setDateRange] = useState<"all"|"24h"|"7d"|"30d">("all");
  const [selectedDefender, setSelectedDefender] = useState<DefenderAlert|null>(null);
  const [selectedIncident, setSelectedIncident] = useState<SecurityIncident|null>(null);

  const unified = useMemo((): UnifiedItem[] => {
    const alertItems: UnifiedItem[] = alerts.map(a=>({ kind:"alert", data:a }));
    const defenderItems: UnifiedItem[] = (defenderAlerts?.alerts??[]).map(d=>({ kind:"defender", data:d }));
    const incidentItems: UnifiedItem[] = (securityIncidents?.incidents??[]).map(i=>({ kind:"incident", data:i }));
    const advisoryItems: UnifiedItem[] = (serviceHealth?.issues??[]).map(i=>({ kind:"advisory", data:i }));
    return [...defenderItems, ...incidentItems, ...alertItems, ...advisoryItems];
  }, [alerts, defenderAlerts, securityIncidents, serviceHealth]);

  const filtered = useMemo(() => {
    let items = unified;
    if (typeFilter==="alerts") items=items.filter(i=>i.kind==="alert");
    if (typeFilter==="defender") items=items.filter(i=>i.kind==="defender");
    if (typeFilter==="incidents") items=items.filter(i=>i.kind==="incident");
    if (typeFilter==="advisories") items=items.filter(i=>i.kind==="advisory");
    if (dateRange!=="all") {
      const cutoff = new Date();
      if (dateRange==="24h") cutoff.setHours(cutoff.getHours()-24);
      else if (dateRange==="7d") cutoff.setDate(cutoff.getDate()-7);
      else if (dateRange==="30d") cutoff.setDate(cutoff.getDate()-30);
      items = items.filter(i => {
        const dt = i.kind==="alert"?i.data.detectedAt:i.kind==="defender"?i.data.createdDateTime:i.kind==="incident"?i.data.createdDateTime:i.data.detectedAt;
        return dt ? new Date(dt)>=cutoff : true;
      });
    }
    if (severity) {
      const sl = severity.toLowerCase();
      items=items.filter(i=>{
        if (i.kind==="alert") return i.data.severity.toLowerCase()===sl;
        if (i.kind==="defender") return i.data.severity.toLowerCase()===sl;
        if (i.kind==="incident") return i.data.severity.toLowerCase()===sl;
        return i.data.severity.toLowerCase()===sl;
      });
    }
    if (search) {
      const q=search.toLowerCase();
      items=items.filter(i=>{
        if (i.kind==="alert") return i.data.title.toLowerCase().includes(q)||(i.data.userPrincipalName?.toLowerCase().includes(q)??false)||(i.data.deviceName?.toLowerCase().includes(q)??false);
        if (i.kind==="defender") return (i.data.title??'').toLowerCase().includes(q)||(i.data.serviceSource??'').toLowerCase().includes(q);
        if (i.kind==="incident") return (i.data.displayName??'').toLowerCase().includes(q);
        return i.data.title.toLowerCase().includes(q);
      });
    }
    // Triage order: worst first (severity), then most recent. The queue must lead
    // with the item an analyst should look at now, regardless of source.
    const sevRank = (s?: string) => ({ critical:0, high:1, medium:2, low:3, informational:4 } as Record<string,number>)[(s??"").toLowerCase()] ?? 5;
    const itemSev = (i: UnifiedItem) => i.kind==="alert"?i.data.severity:i.kind==="defender"?i.data.severity:i.kind==="incident"?i.data.severity:i.data.severity;
    const itemWhen = (i: UnifiedItem) => i.kind==="alert"?i.data.detectedAt:i.kind==="defender"?i.data.createdDateTime:i.kind==="incident"?i.data.createdDateTime:i.data.detectedAt;
    return [...items].sort((a,b) =>
      sevRank(itemSev(a)) - sevRank(itemSev(b)) ||
      new Date(itemWhen(b) ?? 0).getTime() - new Date(itemWhen(a) ?? 0).getTime());
  }, [unified, typeFilter, severity, search, dateRange]);

  const dbBySeverity = useMemo(() =>
    alerts.reduce((acc,a)=>({...acc,[a.severity]:(acc[a.severity]??0)+1}),{} as Record<string,number>),
  [alerts]);

  const defenderCount = defenderAlerts?.total ?? 0;
  const incidentCount = securityIncidents?.total ?? 0;
  const advisoryCount = serviceHealth?.total ?? 0;

  const counts: Record<IncidentFilter,number> = {
    all: unified.length,
    defender: defenderCount,
    incidents: incidentCount,
    alerts: alerts.length,
    advisories: advisoryCount,
  };

  const allSeverities = ["critical","high","medium","low","informational","unknown"];

  return (
    <div className="page">
      {selectedDefender&&<DefenderAlertModal alert={selectedDefender} onClose={()=>setSelectedDefender(null)}/>}
      {selectedIncident && (
        <DetailModal
          title={selectedIncident.displayName ?? "Security Incident"}
          subtitle={`${selectedIncident.severity} · ${selectedIncident.status}`}
          onClose={()=>setSelectedIncident(null)}
          portalUrl={selectedIncident.incidentWebUrl ?? (selectedIncident.id ? `https://security.microsoft.com/incidents/${selectedIncident.id}` : "https://security.microsoft.com/incidents")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Incident ID" value={selectedIncident.id} copy={!!selectedIncident.id}/>
          <DetailField label="Display Name" value={selectedIncident.displayName}/>
          <DetailField label="Severity" value={selectedIncident.severity}/>
          <DetailField label="Status" value={selectedIncident.status}/>
          <DetailField label="Classification" value={selectedIncident.classification}/>
          <DetailField label="Assigned To" value={selectedIncident.assignedTo}/>
          <DetailField label="Created" value={selectedIncident.createdDateTime ? `${relTime(selectedIncident.createdDateTime)} (${fmtDate(selectedIncident.createdDateTime)})` : undefined} title={fmtDate(selectedIncident.createdDateTime)}/>
          <DetailField label="Last Updated" value={selectedIncident.lastUpdateDateTime ? `${relTime(selectedIncident.lastUpdateDateTime)} (${fmtDate(selectedIncident.lastUpdateDateTime)})` : undefined} title={fmtDate(selectedIncident.lastUpdateDateTime)}/>
          {(selectedIncident.customTags?.length ?? 0) > 0 && <DetailField label="Tags" value={selectedIncident.customTags.join(", ")}/>}
          {selectedIncident.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedIncident.description}</div></>}
          {selectedIncident.recommendedActions && <><div className="dm-section-hdr">Recommended Actions</div><div className="dm-desc-block">{selectedIncident.recommendedActions}</div></>}
        </DetailModal>
      )}

      <div className="kpi-row kpi-row-5">
        <KpiTile icon={<ShieldAlert size={18}/>} label="DEFENDER ALERTS" value={defenderCount}
          sub={defenderAlerts?.error?"Permission needed":"Active, unresolved"} tone={defenderCount>0?"error":"good"}
          active={typeFilter==="defender"} onClick={()=>{setSeverity("");setTypeFilter("defender");}}/>
        <KpiTile icon={<AlertCircle size={18}/>} label="SECURITY INCIDENTS" value={incidentCount}
          sub={securityIncidents?.error?"Permission needed":"Active incidents"} tone={incidentCount>0?"warning":"good"}
          active={typeFilter==="incidents"} onClick={()=>{setSeverity("");setTypeFilter("incidents");}}/>
        <KpiTile icon={<AlertTriangle size={16}/>} label="CRITICAL ALERTS"
          value={dbBySeverity["Critical"]??0}
          sub="Critical severity, unresolved"
          tone={(dbBySeverity["Critical"]??0)>0?"error":"good"}
          active={typeFilter==="alerts"&&severity==="critical"} onClick={()=>{setTypeFilter("alerts");setSeverity("critical");}}/>
        <KpiTile icon={<AlertTriangle size={16}/>} label="HIGH ALERTS"
          value={dbBySeverity["High"]??0}
          sub="High severity, unresolved"
          tone={(dbBySeverity["High"]??0)>0?"warning":"good"}
          active={typeFilter==="alerts"&&severity==="high"} onClick={()=>{setTypeFilter("alerts");setSeverity("high");}}/>
        <KpiTile icon={<Bell size={18}/>} label="M365 ADVISORIES" value={advisoryCount} sub="Active advisories" tone={advisoryCount>0?"warning":"good"}
          active={typeFilter==="advisories"} onClick={()=>{setSeverity("");setTypeFilter("advisories");}}/>
      </div>

      {(defenderAlerts?.configured && !defenderAlerts.error && defenderCount > 0) && (
        <Card title="Defender — By Source" badge={<Badge label={`${defenderCount} alerts`} tone="error"/>}>
          <MiniBarChart items={Object.entries(defenderAlerts.bySource??{}).map(([k,v])=>({ label:fmtDefenderSource(k), value:v, color:"var(--dot-high)" }))}/>
        </Card>
      )}
      {(defenderAlerts?.error || securityIncidents?.error) && (
        <Card title="Missing Permissions" badge={<Badge label="Action Required" tone="error"/>}>
          <div style={{display:"flex",flexDirection:"column",gap:10}}>
            {defenderAlerts?.error && (
              <InlineError title="Defender alerts unavailable" perm="SecurityAlert.Read.All"
                message={`Could not load Microsoft Defender alerts (${defenderAlerts.error}).`}/>
            )}
            {securityIncidents?.error && (
              <InlineError title="Security incidents unavailable" perm="SecurityIncident.Read.All"
                message={`Could not load security incidents (${securityIncidents.error}).`}/>
            )}
          </div>
        </Card>
      )}

      <Card title="All Incidents & Advisories" badge={<Badge label={`${filtered.length} shown`} tone="neutral"/>}>
        <div className="filters-bar">
          <div className="pill-group">
            {(["all","defender","incidents","alerts","advisories"] as IncidentFilter[]).map(t=>(
              <button key={t} className={`pill-btn ${typeFilter===t?"active":""}`} onClick={()=>setTypeFilter(t)}>
                {t==="all"?`All (${counts.all})`:t==="defender"?`Defender (${defenderCount})`:t==="incidents"?`Incidents (${incidentCount})`:t==="alerts"?`Vigil365 Alerts (${alerts.length})`:`Advisories (${advisoryCount})`}
              </button>
            ))}
          </div>
          <label className="search-box">
            <Search size={15} color="#94a3b8"/>
            <input value={search} onChange={e=>setSearch(e.target.value)}
              placeholder="Search title, user, source…" className="search-input"/>
          </label>
          <select value={severity} onChange={e=>setSeverity(e.target.value)} className="filter-sel">
            <option value="">All severities</option>
            {allSeverities.map(s=><option key={s} value={s}>{s.charAt(0).toUpperCase()+s.slice(1)}</option>)}
          </select>
          <div className="pill-group">
            {(["all","24h","7d","30d"] as const).map(r=>(
              <button key={r} className={`pill-btn ${dateRange===r?"active":""}`} onClick={()=>setDateRange(r)}>
                {r==="all"?"All time":r==="24h"?"Last 24h":r==="7d"?"Last 7d":"Last 30d"}
              </button>
            ))}
          </div>
          <ExportDropdown
            rows={filtered.map(i=>{
              if (i.kind==="alert") return { Source:"SecurityDB", Title:i.data.title, Severity:i.data.severity, User:i.data.userPrincipalName??"", Detected:i.data.detectedAt };
              if (i.kind==="defender") return { Source:"Defender", Title:i.data.title??"", Severity:i.data.severity, User:"", Detected:i.data.createdDateTime??"" };
              if (i.kind==="incident") return { Source:"Incident", Title:i.data.displayName??"", Severity:i.data.severity, User:"", Detected:i.data.createdDateTime??"" };
              return { Source:"Advisory", Title:i.data.title, Severity:i.data.severity, User:"", Detected:i.data.detectedAt };
            })}
            filename="incidents.csv"
          />
          {(search||severity||typeFilter!=="all"||dateRange!=="all")&&(
            <button className="btn-apply" onClick={()=>{setSearch("");setSeverity("");setTypeFilter("all");setDateRange("all");}}>Clear</button>
          )}
          <FilterPresets pageKey="incidents" filters={{search,severity,typeFilter,dateRange}}
            onLoad={f=>{setSearch(f.search??"");setSeverity(f.severity??"");setTypeFilter((f.typeFilter as IncidentFilter|undefined)||"all");setDateRange((f.dateRange as "all"|"24h"|"7d"|"30d"|undefined)||"all");}}/>
        </div>

        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead>
              <tr><th>Severity</th><th>Source</th><th>Title</th><th>Details</th><th>Detected</th><th></th></tr>
            </thead>
            <tbody>
              {filtered.length===0&&<tr><td colSpan={6} className="td-empty">No items match current filters.</td></tr>}
              {filtered.map((item,idx)=>{
                if (item.kind==="defender") {
                  const a=item.data;
                  const sev=a.severity.charAt(0).toUpperCase()+a.severity.slice(1);
                  return (
                    <tr key={`def-${idx}`} className="tbl-row-click row-border-defender" onClick={()=>setSelectedDefender(a)}>
                      <td><span className="sev-pill" style={{borderColor:sevColor(sev),color:sevColor(sev)}}>{sev}</span></td>
                      <td><span className="src-badge src-defender"><ShieldAlert size={10}/>{fmtDefenderSource(a.serviceSource??'Defender')}</span></td>
                      <td>
                        <div className="al-title trunc" title={a.title??undefined}>{a.title}</div>
                        <div className="al-desc">{a.category}{a.assignedTo ? ` · ${a.assignedTo}` : ""}</div>
                      </td>
                      <td className="al-desc">{a.status}{a.classification ? ` · ${a.classification}` : ""}</td>
                      <td className="al-date">{relTime(a.createdDateTime) || fmtDate(a.createdDateTime)}</td>
                      <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                    </tr>
                  );
                }
                if (item.kind==="incident") {
                  const i=item.data;
                  const sev=i.severity.charAt(0).toUpperCase()+i.severity.slice(1);
                  return (
                    <tr key={`inc-${idx}`} className="tbl-row-click row-border-incident" onClick={()=>setSelectedIncident(i)}>
                      <td><span className="sev-pill" style={{borderColor:sevColor(sev),color:sevColor(sev)}}>{sev}</span></td>
                      <td><span className="src-badge src-incident"><AlertCircle size={10}/>Incident</span></td>
                      <td>
                        <div className="al-title trunc" title={i.displayName??undefined}>{i.displayName??'Security Incident'}</div>
                        {i.assignedTo&&<div className="al-desc">Assigned: {i.assignedTo}</div>}
                      </td>
                      <td className="al-desc">{i.status} {i.classification ? `· ${i.classification}` : ""}</td>
                      <td className="al-date">{relTime(i.createdDateTime) || fmtDate(i.createdDateTime)}</td>
                      <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                    </tr>
                  );
                }
                if (item.kind==="advisory") {
                  const a=item.data;
                  return (
                    <tr key={`adv-${idx}`} className="row-border-advisory">
                      <td><span className="sev-pill sev-pill-medium">{a.severity}</span></td>
                      <td><span className="src-badge src-advisory"><Bell size={10}/>Advisory</span></td>
                      <td>
                        <div className="al-title trunc" title={a.title}>{a.title}</div>
                        {a.description&&<div className="al-desc">{a.description}</div>}
                      </td>
                      <td className="al-desc">M365 Service Health</td>
                      <td className="al-date" title={fmtDate(a.detectedAt)}>{relTime(a.detectedAt) || fmtDate(a.detectedAt)}</td>
                      <td>{a.portalUrl&&<a href={a.portalUrl} target="_blank" rel="noopener noreferrer" className="portal-link"><ExternalLink size={11}/></a>}</td>
                    </tr>
                  );
                }
                const a=item.data;
                return (
                  <tr key={`db-${a.service}-${a.id}`} className="tbl-row-click row-border-db" onClick={()=>onAlertClick(a)}>
                    <td><span className="sev-pill" style={{borderColor:sevColor(a.severity),color:sevColor(a.severity)}}>{a.severity}</span></td>
                    <td><span className="src-badge src-db"><Database size={10}/>{fmtService(a.service)}</span></td>
                    <td>
                      <div className="al-title trunc" title={a.title}>{a.title}</div>
                      {a.description&&<div className="al-desc">{a.description}</div>}
                    </td>
                    <td className="trunc" style={{maxWidth:140}} title={a.userPrincipalName||a.deviceName||undefined}>{a.userPrincipalName||a.deviceName||"—"}</td>
                    <td className="al-date" title={fmtDate(a.detectedAt)}>{relTime(a.detectedAt) || fmtDate(a.detectedAt)}</td>
                    <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {filtered.length > 0 && <div className="tbl-count">{filtered.length} of {unified.length} items</div>}
      </Card>
    </div>
  );
}
