import React, { useState, useMemo, useEffect } from "react";
import { Inbox, AlertTriangle, ShieldAlert, CheckCircle, Archive, Send, XCircle, Filter, ShieldCheck, Search, Flag } from "lucide-react";
import { SecurityAlert, EmailProtectionData, EmailProtectionAlert, Tone } from "../services/types";
import { fmtDate, relTime, fmtFullTime } from "../services/utils";
import { consumeNavSeed } from "../services/api";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, ExportDropdown, SectHdr } from "../components/SharedComponents";

export function EmailPage({ alerts, emailProtection, onAlertClick }:
  { alerts: SecurityAlert[]; emailProtection: EmailProtectionData|null; onAlertClick:(a:SecurityAlert)=>void }) {
  const [selectedMdo, setSelectedMdo] = useState<EmailProtectionAlert|null>(null);
  const [search, setSearch] = useState("");
  const [sevFilter, setSevFilter] = useState("");
  const [catFilter, setCatFilter] = useState("");

  useEffect(() => {
    const checkSeed = () => {
      const seed = consumeNavSeed("email");
      if (seed) { setSearch(seed); }
    };
    checkSeed();
    const listener = (e: any) => {
      if (e.detail?.page === "email" && e.detail?.search) {
        setSearch(e.detail.search);
      }
    };
    window.addEventListener("nav-seed-update", listener);
    return () => window.removeEventListener("nav-seed-update", listener);
  }, []);

  const q = search.toLowerCase();
  const emailAlerts = useMemo(() => alerts.filter(a => a.service==="ExchangeOnline" || a.alertType==="MalwareDetection" || a.alertType==="QuarantinedMessage" || a.alertType==="MailFlowIssue"), [alerts]);
  const quarantined = useMemo(() => emailAlerts.filter(a => a.alertType==="QuarantinedMessage" && (!q || (a.title+a.description+(a.userPrincipalName||"")).toLowerCase().includes(q))), [emailAlerts, q]);
  const mailFlow = useMemo(() => emailAlerts.filter(a => a.alertType==="MailFlowIssue" && (!q || (a.title+a.description).toLowerCase().includes(q))), [emailAlerts, q]);
  const malware = useMemo(() => emailAlerts.filter(a => a.alertType==="MalwareDetection" && (!q || (a.title+a.description).toLowerCase().includes(q))), [emailAlerts, q]);

  const mdoCategories = useMemo(()=>
    [...new Set((emailProtection?.alerts??[]).map(a=>a.category).filter((c):c is string=>!!c))].sort(),
  [emailProtection]);

  const filteredMdo = useMemo(() => {
    let items = emailProtection?.alerts ?? [];
    if (sevFilter) items = items.filter(a=>a.severity.toLowerCase()===sevFilter);
    if (catFilter) items = items.filter(a=>a.category===catFilter);
    if (search) { const q=search.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.description??'').toLowerCase().includes(q)); }
    return items;
  }, [emailProtection, sevFilter, catFilter, search]);

  return (
    <div className="page">
      {selectedMdo && (
        <DetailModal
          title={selectedMdo.title ?? "MDO Alert"}
          subtitle={`${selectedMdo.severity} · ${selectedMdo.category ?? "Defender for Office 365"}`}
          onClose={()=>setSelectedMdo(null)}
          portalUrl={selectedMdo.alertWebUrl ?? (selectedMdo.id ? `https://security.microsoft.com/alerts/${selectedMdo.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR"
        >
          <DetailField label="Alert ID" value={selectedMdo.id} copy/>
          {selectedMdo.alertWebUrl && <DetailField label="Portal URL" value="Open in Defender XDR" onNavigate={()=>window.open(selectedMdo.alertWebUrl, "_blank")} navLabel="Open →"/>}
          <DetailField label="Severity" value={selectedMdo.severity}/>
          <DetailField label="Status" value={selectedMdo.status}/>
          <DetailField label="Category" value={selectedMdo.category}/>
          <DetailField label="Detected" value={fmtFullTime(selectedMdo.createdDateTime)} title={fmtDate(selectedMdo.createdDateTime)}/>
          {selectedMdo.description && <><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMdo.description}</div></>}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Inbox size={18}/>} label="QUARANTINED" value={quarantined.length}
          sub="Messages held in quarantine" tone={quarantined.length===0?"good":quarantined.length<=5?"warning":"error"}
          onClick={() => setSearch("quarantined")}/>
        <KpiTile icon={<AlertTriangle size={18}/>} label="MAIL FLOW ISSUES" value={mailFlow.length}
          sub="Active delivery problems" tone={mailFlow.length===0?"good":"error"}
          onClick={() => setSearch("mailflow")}/>
        <KpiTile icon={<ShieldAlert size={18}/>} label="MALWARE DETECTED" value={malware.length}
          sub="Email-borne threats" tone={malware.length===0?"good":"error"}
          onClick={() => setSearch("malware")}/>
        <KpiTile icon={<CheckCircle size={18}/>} label="DEFENDER STATUS" value={mailFlow.length===0?"Active":"Degraded"}
          sub="Office 365 Defender" tone={mailFlow.length===0?"good":"warning"}
          onClick={() => setSearch("")}/>
      </div>

      <div className="two-col">
        <Card title="Quarantined Messages" badge={<Badge label={`${quarantined.length} held`} tone={quarantined.length>0?"warning":"good"}/>}>
          {quarantined.length===0
            ?<EmptyState icon={<Inbox size={28} color="#d1d5db"/>} message="No messages currently quarantined"/>
            :(
              <div className="alert-list">
                {quarantined.slice(0,8).map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <Archive size={13} color="var(--status-warn-icon)"/>
                    <div className="al-body">
                      <div className="al-title">{a.title}</div>
                      <div className="al-desc">{a.userPrincipalName} · {a.description}</div>
                    </div>
                    <span className="al-date">{fmtFullTime(a.detectedAt)}</span>
                  </div>
                ))}
              </div>
            )
          }
        </Card>

        <Card title="Mail Flow Issues" badge={<Badge label={mailFlow.length===0?"Healthy":"Action needed"} tone={mailFlow.length===0?"good":"error"}/>}>
          {mailFlow.length===0
            ?<EmptyState icon={<Send size={28} color="#d1d5db"/>} message="Mail flow is operating normally"/>
            :(
              <div className="alert-list">
                {mailFlow.map((a,i)=>(
                  <div key={i} className="al-item" onClick={()=>onAlertClick(a)}>
                    <XCircle size={13} color="#dc2626"/>
                    <div className="al-body">
                      <div className="al-title">{a.title}</div>
                      <div className="al-desc">{a.description}</div>
                    </div>
                    <span className="al-date">{fmtFullTime(a.detectedAt)}</span>
                    <Badge label={a.severity} tone={a.severity==="High"||a.severity==="Critical"?"error":"warning"}/>
                  </div>
                ))}
              </div>
            )
          }
        </Card>
      </div>

      {malware.length > 0 && (
        <Card title="Malware Detections" badge={<Badge label={`${malware.length} threats`} tone="error"/>}>
          <div className="alert-list">
            {malware.map((a, i) => (
              <div key={i} className="al-item" onClick={() => onAlertClick(a)}>
                <ShieldAlert size={14} color="#dc2626" />
                <div className="al-body">
                  <div className="al-title">{a.title}</div>
                  <div className="al-desc">{a.userPrincipalName ? `${a.userPrincipalName} · ` : ""}{a.description}</div>
                </div>
                <span className="al-date">{fmtFullTime(a.detectedAt)}</span>
              </div>
            ))}
          </div>
        </Card>
      )}

      <Card title="Email Threat Summary">
        <div className="threat-grid">
          {[
            { icon:<ShieldAlert size={18}/>, label:"Malware Detections", value:malware.length, tone:"error" as Tone, desc:"Email-borne malware caught by Defender" },
            { icon:<Filter size={18}/>, label:"Quarantined Messages", value:quarantined.length, tone:"warning" as Tone, desc:"Held for review by Exchange protection" },
            { icon:<AlertTriangle size={18}/>, label:"Mail Flow Issues", value:mailFlow.length, tone:"error" as Tone, desc:"Active delivery disruptions" },
            { icon:<ShieldCheck size={18}/>, label:"MDO Alerts", value:(emailProtection?.configured&&!emailProtection.error)?emailProtection.total:"—", tone:((emailProtection?.total??0)>0?"warning":"good") as Tone, desc:emailProtection?.error?"Needs SecurityAlert.Read.All":"Defender for Office 365 detections" },
          ].map((t,i)=>(
            <div key={i} className="threat-card">
              <div className={`threat-icon tone-bg-${t.tone}`}>{t.icon}</div>
              <div className="threat-body">
                <div className="threat-label">{t.label}</div>
                <div className={`threat-value tone-${t.tone}`}>{t.value}</div>
                <div className="threat-desc">{t.desc}</div>
              </div>
            </div>
          ))}
        </div>
      </Card>

      <Card title="MDO Protection Alerts"
        badge={<Badge label={`${filteredMdo.length} / ${emailProtection?.total??0} alerts`} tone={(emailProtection?.total??0)>0?"warning":"good"}/>}
        action={(emailProtection?.configured && !emailProtection?.error && (emailProtection?.total??0)>0) ? (
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={14} color="#94a3b8"/>
              <input value={search} onChange={e=>setSearch(e.target.value)}
                placeholder="Search alert title, description…" className="search-input"/>
            </label>
            <select value={sevFilter} onChange={e=>setSevFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All severities</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
              <option value="informational">Informational</option>
            </select>
            {mdoCategories.length>0&&(
              <select value={catFilter} onChange={e=>setCatFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All categories</option>
                {mdoCategories.map(c=><option key={c} value={c}>{c}</option>)}
              </select>
            )}
            <ExportDropdown rows={filteredMdo.map(a=>({ Title:a.title??"", Severity:a.severity, Category:a.category??"", Status:a.status, Detected:a.createdDateTime??"" }))} filename="mdo-alerts.csv"/>
            {(search||sevFilter||catFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSearch("");setSevFilter("");setCatFilter("");}}>Clear</button>}
          </div>
        ) : undefined}>
        {emailProtection?.error
          ?<EmptyState icon={<ShieldAlert size={28} color="#d1d5db"/>} message="Needs SecurityAlert.Read.All for Defender for Office 365"/>
          :(emailProtection?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={28} color="#d1d5db"/>} message="No Defender for Office 365 alerts"/>
            :(
              <>
                <div className="threat-grid" style={{marginBottom:14}}>
                  {Object.entries(emailProtection!.byCategory??{}).slice(0,4).map(([cat,count])=>(
                    <div key={cat} className="threat-card" style={{cursor:"pointer"}} onClick={()=>setCatFilter(catFilter===cat?"":cat)}>
                      <div className={`threat-icon ${catFilter===cat?"tone-bg-error":"tone-bg-warning"}`}><Flag size={18}/></div>
                      <div className="threat-body">
                        <div className="threat-label">{cat}</div>
                        <div className="threat-value tone-warning">{count}</div>
                      </div>
                    </div>
                  ))}
                </div>
                <div className="alert-list">
                  <SectHdr>MDO ALERTS — {filteredMdo.length} shown</SectHdr>
                  {filteredMdo.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                  {filteredMdo.slice(0,10).map((a,i)=>(
                    <div key={a.id??i} className="al-item" onClick={()=>setSelectedMdo(a)}>
                      <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                      <div className="al-body">
                        <div className="al-title">{a.title}</div>
                        <div className="row-meta">
                          {a.category&&<span className="row-meta-item">{a.category}</span>}
                          <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                          <span className="row-meta-item">{fmtFullTime(a.createdDateTime)}</span>
                        </div>
                      </div>
                      <Badge label={a.severity} tone={a.severity==="high"||a.severity==="High"?"error":"warning"}/>
                    </div>
                  ))}
                  {filteredMdo.length>10&&<div className="more-link">{filteredMdo.length-10} more — use filters to narrow</div>}
                </div>
              </>
            )
        }
      </Card>
    </div>
  );
}
