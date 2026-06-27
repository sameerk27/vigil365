import React, { useState, useMemo, useRef, useEffect } from "react";
import { Monitor, XCircle, Clock, ShieldAlert, Search, Laptop, CheckCircle, Eye, ShieldCheck } from "lucide-react";
import { DevicesData, SecurityAlert, MdeVulnerabilitiesData, MdeAlert } from "../services/types";
import { pctTone, fmtDate, relTime } from "../services/utils";
import { consumeNavSeed } from "../services/api";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, MiniBarChart, InfoRow, CircleGauge, ExportDropdown, StatBox, SectHdr } from "../components/SharedComponents";

export function DevicesPage({ devices, alerts, mdeVulnerabilities, onAlertClick }:
  { devices: DevicesData|null; alerts: SecurityAlert[]; mdeVulnerabilities: MdeVulnerabilitiesData|null; onAlertClick:(a:SecurityAlert)=>void }) {
  const [selectedMde, setSelectedMde] = useState<MdeAlert|null>(null);
  const ncRef = useRef<HTMLDivElement>(null);
  const staleRef = useRef<HTMLDivElement>(null);
  const scrollTo = (ref: React.RefObject<HTMLDivElement | null>) => ref.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  // Per-card search states — each card searches independently
  const [ncSearch, setNcSearch]   = useState("");  // Non-Compliant Devices
  const [ncSev, setNcSev]         = useState("");
  const [staleSearch, setStaleSearch] = useState(""); // Not Checked In
  const [mdeSearch, setMdeSearch] = useState("");
  const [mdeSev, setMdeSev]       = useState("");

  useEffect(() => { const seed = consumeNavSeed("devices"); if (seed) { setNcSearch(seed); scrollTo(ncRef); } }, []);

  const deviceAlerts = useMemo(() => alerts.filter(a => a.service==="Intune"), [alerts]);

  // Search checks deviceName, userPrincipalName AND title (title = "Non-compliant device: ADMIN")
  const nonCompliant = useMemo(() => {
    let items = deviceAlerts.filter(a => a.alertType==="NonCompliantDevice");
    if (ncSev) items = items.filter(a => a.severity.toLowerCase() === ncSev);
    if (ncSearch) {
      const q = ncSearch.toLowerCase();
      items = items.filter(a =>
        a.deviceName?.toLowerCase().includes(q) ||
        a.userPrincipalName?.toLowerCase().includes(q) ||
        a.title.toLowerCase().includes(q) ||
        a.description?.toLowerCase().includes(q)
      );
    }
    return items;
  }, [deviceAlerts, ncSearch, ncSev]);

  const notCheckedIn = useMemo(() => {
    let items = deviceAlerts.filter(a => a.alertType==="DeviceNotCheckedIn");
    if (staleSearch) {
      const q = staleSearch.toLowerCase();
      items = items.filter(a =>
        a.deviceName?.toLowerCase().includes(q) ||
        a.userPrincipalName?.toLowerCase().includes(q) ||
        a.title.toLowerCase().includes(q)
      );
    }
    return items;
  }, [deviceAlerts, staleSearch]);

  const filteredMde = useMemo(() => {
    let items = mdeVulnerabilities?.alerts ?? [];
    if (mdeSev) items = items.filter(a => a.severity.toLowerCase() === mdeSev);
    if (mdeSearch) { const q = mdeSearch.toLowerCase(); items = items.filter(a => (a.title??'').toLowerCase().includes(q)||(a.category??'').toLowerCase().includes(q)); }
    return items;
  }, [mdeVulnerabilities, mdeSev, mdeSearch]);

  const devComplPct = devices?.compliancePct??(devices?.nonCompliant===0?100:94);

  const complianceData = [
    { label:"Compliant", value: devices?(devices.totalDevices>0?Math.max(0,devices.totalDevices-devices.nonCompliant):0):0, color:"var(--status-good-icon)" },
    { label:"Non-Compliant", value: devices?.nonCompliant??0, color:"var(--status-error-icon)" },
    { label:"Not Checked In", value: devices?.notCheckedIn??0, color:"var(--status-warn-icon)" },
  ];

  return (
    <div className="page">
      {selectedMde && (
        <DetailModal
          title={selectedMde.title ?? "MDE Alert"}
          subtitle={`${selectedMde.severity} · ${selectedMde.category ?? "Defender for Endpoint"}`}
          onClose={() => setSelectedMde(null)}
          portalUrl={selectedMde.alertWebUrl ?? (selectedMde.id ? `https://security.microsoft.com/alerts/${selectedMde.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Alert ID" value={selectedMde.id} copy/>
          <DetailField label="Severity" value={selectedMde.severity}/>
          <DetailField label="Status" value={selectedMde.status}/>
          <DetailField label="Category" value={selectedMde.category}/>
          <DetailField label="Detected" value={selectedMde.createdDateTime ? `${relTime(selectedMde.createdDateTime)} (${fmtDate(selectedMde.createdDateTime)})` : undefined} title={fmtDate(selectedMde.createdDateTime)}/>
          {selectedMde.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMde.description}</div></>}
          {(selectedMde.mitreTechniques?.length ?? 0) > 0 && (
            <><div className="dm-section-hdr">MITRE Techniques</div>
            <div className="mitre-tags">{selectedMde.mitreTechniques.map(t=><a key={t} href={`https://attack.mitre.org/techniques/${t}`} target="_blank" rel="noopener noreferrer" className="mitre-tag">{t}</a>)}</div></>
          )}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Monitor size={18}/>} label="COMPLIANCE RATE" value={`${devComplPct}%`}
          sub={(devices?.totalDevices??0)>0?`${devices!.totalDevices} total devices`:"From collected alerts"}
          tone={pctTone(devComplPct)}/>
        <KpiTile icon={<XCircle size={18}/>} label="NON-COMPLIANT" value={devices?.nonCompliant??0}
          sub="Require immediate action" tone={(devices?.nonCompliant??0)===0?"good":(devices?.nonCompliant??0)<=3?"warning":"error"}
          onClick={() => { setNcSearch(""); setNcSev(""); scrollTo(ncRef); }}/>
        <KpiTile icon={<Clock size={18}/>} label="NOT CHECKED IN" value={devices?.notCheckedIn??0}
          sub={`>${7} days inactive`} tone={(devices?.notCheckedIn??0)===0?"good":"warning"}
          onClick={() => { setStaleSearch(""); scrollTo(staleRef); }}/>
        <KpiTile icon={<ShieldAlert size={18}/>} label="TOTAL ALERTS" value={deviceAlerts.length}
          sub="Active device alerts" tone={deviceAlerts.length===0?"good":deviceAlerts.length<=5?"warning":"error"}/>
      </div>

      {/* Compliance Overview — full-width summary at top */}
      <Card title="Compliance Overview" badge={<Badge label={`${devComplPct}%`} tone={pctTone(devComplPct)}/>}>
        <div className="compliance-hero">
          <CircleGauge pct={devComplPct} size={100}/>
          <div style={{flex:1}}>
            <MiniBarChart items={complianceData}/>
          </div>
        </div>
        <div className="info-rows" style={{marginTop:12}}>
          <InfoRow label="Last Sync Window" value="7 days"/>
          <InfoRow label="Policy Engine" value="Microsoft Intune"/>
          <InfoRow label="Total Managed" value={(devices?.totalDevices??0)>0?devices!.totalDevices:"Unknown"}/>
        </div>
      </Card>

      {/* Non-Compliant Devices — full-width, search in card header */}
      <div ref={ncRef}/>
      <Card title="Non-Compliant Devices"
        badge={<Badge label={`${nonCompliant.length} devices`} tone={nonCompliant.length>0?"error":"good"}/>}
        action={
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={13} color="#94a3b8"/>
              <input value={ncSearch} onChange={e=>setNcSearch(e.target.value)}
                placeholder="Search device or user…" className="search-input"/>
            </label>
            <select value={ncSev} onChange={e=>setNcSev(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All severities</option>
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </select>
            <ExportDropdown rows={nonCompliant.map(a=>({ Device:a.deviceName??a.title, User:a.userPrincipalName??"", Severity:a.severity, Detected:a.detectedAt }))} filename="non-compliant-devices.csv"/>
            {(ncSearch||ncSev)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setNcSearch("");setNcSev("");}}>Clear</button>}
          </div>
        }>
        {deviceAlerts.filter(a=>a.alertType==="NonCompliantDevice").length===0
          ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="All devices are compliant"/>
          : nonCompliant.length===0
            ?<div className="td-empty" style={{padding:16}}>No devices match the filter.</div>
            :(
              <div className="alert-list">
                {nonCompliant.map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <Laptop size={14} color="#dc2626"/>
                    <div className="al-body">
                      <div className="al-title">{a.deviceName??a.title}</div>
                      <div className="al-desc">{a.userPrincipalName} · {a.description}</div>
                    </div>
                    <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:4}}>
                      <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                      <span className="al-date">{fmtDate(a.detectedAt)}</span>
                    </div>
                  </div>
                ))}
              </div>
            )
        }
      </Card>

      {/* Devices Not Checked In — search in card header */}
      <div ref={staleRef}/>
      <Card title="Devices Not Checked In"
        badge={<Badge label={`${notCheckedIn.length} stale`} tone={notCheckedIn.length>0?"warning":"good"}/>}
        action={
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={13} color="#94a3b8"/>
              <input value={staleSearch} onChange={e=>setStaleSearch(e.target.value)}
                placeholder="Search device or user…" className="search-input"/>
            </label>
            <ExportDropdown rows={notCheckedIn.map(a=>({ Device:a.deviceName??a.title, User:a.userPrincipalName??"", LastSeen:a.detectedAt }))} filename="stale-devices.csv"/>
            {staleSearch&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>setStaleSearch("")}>Clear</button>}
          </div>
        }>
        {deviceAlerts.filter(a=>a.alertType==="DeviceNotCheckedIn").length===0
          ?<EmptyState icon={<CheckCircle size={28} color="#d1d5db"/>} message="All devices checked in within the sync window"/>
          : notCheckedIn.length===0
            ?<div className="td-empty" style={{padding:16}}>No devices match the filter.</div>
            :(
              <div className="tbl-wrap">
                <table className="data-tbl">
                  <thead><tr><th>Device</th><th>User</th><th>Last Seen</th><th>Status</th><th></th></tr></thead>
                  <tbody>
                    {notCheckedIn.map((a,i)=>(
                      <tr key={i} className="tbl-row-click" onClick={()=>onAlertClick(a)}>
                        <td><div className="al-title trunc" style={{maxWidth:180}} title={a.deviceName??a.title}>{a.deviceName??a.title}</div></td>
                        <td><div className="trunc" style={{maxWidth:160}} title={a.userPrincipalName??undefined}>{a.userPrincipalName??"—"}</div></td>
                        <td className="al-date" title={fmtDate(a.detectedAt)}>{relTime(a.detectedAt)}</td>
                        <td><Badge label="Stale" tone="warning"/></td>
                        <td><Eye size={13} color="#94a3b8" className="tbl-eye"/></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {notCheckedIn.length > 0 && <div className="tbl-count">{notCheckedIn.length} device{notCheckedIn.length!==1?"s":""}</div>}
              </div>
          )
        }
      </Card>

      <Card title="MDE Endpoint Alerts"
        badge={<><Badge label={`${filteredMde.length} / ${mdeVulnerabilities?.total??0} alerts`} tone={(mdeVulnerabilities?.total??0)>0?"error":"good"}/><span className="card-count">{filteredMde.length}</span></>}
        action={
          (mdeVulnerabilities?.configured && !mdeVulnerabilities?.error && (mdeVulnerabilities?.total??0)>0) ? (
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:180}}>
                <Search size={13} color="#94a3b8"/>
                <input value={mdeSearch} onChange={e=>setMdeSearch(e.target.value)}
                  placeholder="Search title, category…" className="search-input"/>
              </label>
              <select value={mdeSev} onChange={e=>setMdeSev(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All severities</option>
                <option value="high">High</option>
                <option value="medium">Medium</option>
                <option value="low">Low</option>
                <option value="informational">Informational</option>
              </select>
              <ExportDropdown rows={filteredMde.map(a=>({ Title:a.title??"", Severity:a.severity, Category:a.category??"", Status:a.status, Detected:a.createdDateTime??"" }))} filename="mde-alerts.csv"/>
              {(mdeSearch||mdeSev)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setMdeSearch("");setMdeSev("");}}>Clear</button>}
            </div>
          ) : undefined
        }>
        {mdeVulnerabilities?.error
          ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All"/>
          :(mdeVulnerabilities?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Defender for Endpoint alerts"/>
            :(
              <>
                <div className="stat-row4" style={{marginBottom:14}}>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["high"]??0} label="High" color={(mdeVulnerabilities!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["medium"]??0} label="Medium" color={(mdeVulnerabilities!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["low"]??0} label="Low"/>
                  <StatBox value={mdeVulnerabilities!.bySeverity?.["informational"]??0} label="Info"/>
                </div>
                <div className="alert-list">
                  <SectHdr>ENDPOINT ALERTS — {filteredMde.length} shown</SectHdr>
                  {filteredMde.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                  {filteredMde.slice(0,10).map((a,i)=>(
                    <div key={a.id??i} className="al-item" onClick={()=>setSelectedMde(a)}>
                      <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                      <div className="al-body">
                        <div className="al-title">{(a.title??"Untitled").length>60?(a.title??"").slice(0,60)+"…":a.title}</div>
                        <div className="row-meta">
                          {a.category&&<span className="row-meta-item">{a.category}</span>}
                          <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                          <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                        </div>
                      </div>
                      <Eye size={13} color="#94a3b8" style={{flexShrink:0}}/>
                    </div>
                  ))}
                  {filteredMde.length>10&&<div className="more-link">{filteredMde.length-10} more results — use search to narrow</div>}
                </div>
              </>
            )
        }
      </Card>
    </div>
  );
}
