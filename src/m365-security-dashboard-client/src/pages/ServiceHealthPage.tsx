import React, { useState, useMemo } from "react";
import { Activity, CheckCircle, Clock, Globe, Search, AlertCircle, ShieldCheck } from "lucide-react";
import { ServiceHealthData } from "../services/types";
import { fmtDate, relTime } from "../services/utils";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, ExportDropdown } from "../components/SharedComponents";
import { ServiceHealthGrid, M365_SVCS, matchSvcIssue } from "../components/ServiceHealthGrid";

// Re-exported for existing importers (NetworkPage); the definitions live with the grid.
export { M365_SVCS, matchSvcIssue };

export function ServiceHealthPage({ serviceHealth }: { serviceHealth: ServiceHealthData|null }) {
  const [selectedIssue, setSelectedIssue] = useState<{title:string;description?:string;severity:string;detectedAt:string;portalUrl?:string}|null>(null);
  const [svcSearch, setSvcSearch] = useState("");
  const [sevFilter, setSevFilter] = useState("");

  const total = serviceHealth?.total??0;

  const filteredIssues = useMemo(() => {
    let items = serviceHealth?.issues ?? [];
    if (sevFilter) items = items.filter(i=>i.severity.toLowerCase()===sevFilter);
    if (svcSearch) { const q=svcSearch.toLowerCase(); items=items.filter(i=>i.title.toLowerCase().includes(q)||(i.description??'').toLowerCase().includes(q)); }
    return items;
  }, [serviceHealth, sevFilter, svcSearch]);

  return (
    <div className="page">
      {selectedIssue && (
        <DetailModal
          title={selectedIssue.title}
          subtitle={`${selectedIssue.severity} · Service Health`}
          onClose={()=>setSelectedIssue(null)}
          portalUrl={selectedIssue.portalUrl ?? "https://admin.microsoft.com/#/servicehealth"}
          portalLabel="View in Admin Center"
        >
          <DetailField label="Title" value={selectedIssue.title}/>
          <DetailField label="Severity" value={selectedIssue.severity}/>
          <DetailField label="Detected" value={fmtDate(selectedIssue.detectedAt)}/>
          {selectedIssue.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedIssue.description}</div></>}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Activity size={18}/>} label="ACTIVE ISSUES" value={total}
          sub="Current advisories" tone={total===0?"good":total<=2?"warning":"error"}/>
        <KpiTile icon={<CheckCircle size={18}/>} label="HEALTHY SERVICES"
          value={(() => { const affected = M365_SVCS.filter(svc => (serviceHealth?.issues ?? []).some(i => matchSvcIssue(svc, i.title))).length; return `${M365_SVCS.length - affected} / ${M365_SVCS.length}`; })()}
          sub="No open advisories" tone={total===0?"good":"warning"}/>
        <KpiTile icon={<Clock size={18}/>} label="DATA FRESHNESS" value="Per collection"
          sub="Updates each collection cycle" tone="neutral"/>
        <KpiTile icon={<Globe size={18}/>} label="SOURCE" value="Graph API"
          sub="M365 service announcements" tone="neutral"/>
      </div>

      <Card title="Service Status Overview"
        badge={total>0?<Badge label={`${total} issue${total>1?"s":""}`} tone="warning"/>:<Badge label="All services operational" tone="good"/>}>
        <ServiceHealthGrid issues={serviceHealth?.issues??[]}/>
      </Card>

      {total>0?(
        <Card title="Active Advisories & Incidents"
          badge={<Badge label={`${filteredIssues.length} shown`} tone="neutral"/>}
          action={
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:180}}>
                <Search size={14}/>
                <input value={svcSearch} onChange={e=>setSvcSearch(e.target.value)}
                  placeholder="Search service, description…" className="search-input"/>
              </label>
              <select value={sevFilter} onChange={e=>setSevFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All severities</option>
                <option value="high">High</option>
                <option value="medium">Medium</option>
                <option value="low">Low</option>
              </select>
              <ExportDropdown rows={filteredIssues.map(i=>({ Title:i.title, Severity:i.severity, Detected:i.detectedAt, Description:i.description??"" }))} filename="service-health-advisories.csv"/>
              {(svcSearch||sevFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSvcSearch("");setSevFilter("");}}>Clear</button>}
            </div>
          }>
          <div className="alert-list">
            {filteredIssues.length===0&&<div className="td-empty" style={{padding:12}}>No advisories match the filter.</div>}
            {filteredIssues.map((iss,i)=>(
              <div key={i} className="al-item" onClick={()=>setSelectedIssue(iss)}>
                <AlertCircle size={14} color="var(--status-warn-icon)"/>
                <div className="al-body">
                  <div className="al-title">{iss.title}</div>
                  <div className="row-meta">
                    <Badge label={iss.severity} tone={iss.severity==="High"||iss.severity==="Critical"?"error":"warning"}/>
                    <span className="row-meta-item">{relTime(iss.detectedAt) || fmtDate(iss.detectedAt)}</span>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </Card>
      ):(
        <Card title="Service Incident History">
          <EmptyState icon={<ShieldCheck size={36}/>}
            message="No active incidents — all M365 services are operating normally"/>
        </Card>
      )}

      <Card title="Per-Service Status">
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead><tr><th>Service</th><th>Status</th><th>Open Advisories</th><th>Latest Advisory</th></tr></thead>
            <tbody>
              {M365_SVCS.map(svc=>{
                const matchingIssues = serviceHealth?.issues.filter(i=>matchSvcIssue(svc, i.title)) || [];
                const hit = matchingIssues.length > 0;
                return(
                  <tr key={svc}>
                    <td><div className="al-title">{svc}</div></td>
                    <td><Badge label={hit?"Advisory":"Operational"} tone={hit?"warning":"good"}/></td>
                    <td>{matchingIssues.length}</td>
                    <td className="al-date">{hit?fmtDate(matchingIssues[0]?.detectedAt):"None open"}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}
