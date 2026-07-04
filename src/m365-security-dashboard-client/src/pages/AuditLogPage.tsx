import React, { useState, useMemo } from "react";
import { BookOpen, XCircle, Database, Clock, Search } from "lucide-react";
import { AuditLogData, AuditEvent } from "../services/types";
import { fmtDate, relTime } from "../services/utils";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, ExportDropdown } from "../components/SharedComponents";
import { FilterPresets } from "../components/FilterPresets";

export function AuditLogPage({ data }: { data: AuditLogData|null }) {
  const [selectedEvent, setSelectedEvent] = useState<AuditEvent|null>(null);
  const [search, setSearch] = useState("");
  const [resultFilter, setResultFilter] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("");

  const filtered = useMemo(()=>{
    let items = data?.events??[];
    if (resultFilter) items=items.filter(e=>e.result===resultFilter);
    if (categoryFilter) items=items.filter(e=>e.category===categoryFilter);
    if (search){
      const q=search.toLowerCase();
      items=items.filter(e=>
        e.activityDisplayName?.toLowerCase().includes(q)||
        e.initiatedByUser?.toLowerCase().includes(q)||
        e.category?.toLowerCase().includes(q)||
        e.targetResources.some(t=>t?.toLowerCase().includes(q))
      );
    }
    return items;
  },[data,search,resultFilter,categoryFilter]);

  const categories = useMemo(()=>
    [...new Set((data?.events??[]).map(e=>e.category).filter((c): c is string => !!c))].sort(),
  [data]);

  return (
    <div className="page">
      {selectedEvent && (
        <DetailModal
          title={selectedEvent.activityDisplayName ?? "Audit Event"}
          subtitle={selectedEvent.initiatedByUser ?? "System"}
          onClose={()=>setSelectedEvent(null)}
          portalUrl="https://compliance.microsoft.com/auditlogsearch"
          portalLabel="View Audit Logs"
        >
          <DetailField label="Operation" value={selectedEvent.activityDisplayName}/>
          <DetailField label="Category" value={selectedEvent.category}/>
          <DetailField label="Result" value={selectedEvent.result}/>
          <DetailField label="Result Reason" value={selectedEvent.resultReason}/>
          <DetailField label="Activity DateTime" value={selectedEvent.activityDateTime ? `${relTime(selectedEvent.activityDateTime)} (${fmtDate(selectedEvent.activityDateTime)})` : undefined} title={fmtDate(selectedEvent.activityDateTime)}/>
          <DetailField label="Initiated By" value={selectedEvent.initiatedByUser}/>
          {selectedEvent.targetResources.length > 0 && (
            <>
              <div className="dm-section-hdr">Target Resources</div>
              {selectedEvent.targetResources.map((t,i)=><DetailField key={i} label={`Target ${i+1}`} value={t}/>)}
            </>
          )}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<BookOpen size={18}/>} label="AUDIT EVENTS" value={data?.total??"—"}
          sub="Last 50 operations" tone="neutral"/>
        <KpiTile icon={<XCircle size={18}/>} label="FAILURES" value={data?.failures??"—"}
          sub="Failed operations" tone={(data?.failures??0)>0?"error":"good"}
          active={resultFilter==="failure"} onClick={()=>{setSearch("");setCategoryFilter("");setResultFilter(resultFilter==="failure"?"":"failure");}}/>
        <KpiTile icon={<Database size={18}/>} label="CATEGORIES" value={categories.length}
          sub="Distinct activity types" tone="neutral"/>
        <KpiTile icon={<Clock size={18}/>} label="DATA FRESHNESS" value="On refresh" sub="Fetched from Graph on page load" tone="neutral"/>
      </div>

      <Card title="Admin Audit Log"
        badge={<Badge label={`${filtered.length} events`} tone="neutral"/>}
        action={(data?.configured && !data.error) ? (
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={15}/>
              <input value={search} onChange={e=>setSearch(e.target.value)}
                placeholder="Search actor, action, target…" className="search-input"/>
            </label>
            <select value={resultFilter} onChange={e=>setResultFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All results</option>
              <option value="success">Success</option>
              <option value="failure">Failure</option>
            </select>
            {categories.length>0&&(
              <select value={categoryFilter} onChange={e=>setCategoryFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All categories</option>
                {categories.map(c=><option key={c} value={c}>{c}</option>)}
              </select>
            )}
            <ExportDropdown rows={filtered.map(e=>({ Time:e.activityDateTime??"", Actor:e.initiatedByUser??"System", Action:e.activityDisplayName??"", Category:e.category??"", Target:e.targetResources.join("; "), Result:e.result??"" }))} filename="audit-log.csv"/>
            {(search||resultFilter||categoryFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSearch("");setResultFilter("");setCategoryFilter("");}}>Clear</button>}
            <FilterPresets pageKey="auditlog" filters={{search,resultFilter,categoryFilter}}
              onLoad={f=>{setSearch(f.search??"");setResultFilter(f.resultFilter??"");setCategoryFilter(f.categoryFilter??"");}}/>
          </div>
        ) : undefined}>
        {data==null
          ? <EmptyState message="Loading audit events…"/>
          : !data.configured
          ? <EmptyState message={data.error??"Requires AuditLog.Read.All permission"}/>
          : data.error
          ? <EmptyState message={data.error}/>
          : <>
              <div className="tbl-wrap">
                <table className="data-tbl">
                  <thead><tr><th scope="col">Time</th><th scope="col">Actor</th><th scope="col">Action</th><th scope="col">Target</th><th scope="col">Result</th></tr></thead>
                  <tbody>
                    {filtered.length===0&&<tr><td colSpan={5} className="td-empty">No events match the filter.</td></tr>}
                    {filtered.map((e,i)=>(
                      <tr key={i} className="tbl-row-click" onClick={()=>setSelectedEvent(e)}>
                        <td className="al-date" title={fmtDate(e.activityDateTime)}>{relTime(e.activityDateTime) || fmtDate(e.activityDateTime)}</td>
                        <td><div className="al-title trunc" style={{maxWidth:120}} title={e.initiatedByUser??undefined}>{e.initiatedByUser?.split("@")[0]??"System"}</div></td>
                        <td>
                          <div className="al-title">{e.activityDisplayName}</div>
                          {e.category&&<div className="al-desc">{e.category}</div>}
                        </td>
                        <td className="al-desc trunc" style={{maxWidth:160}} title={e.targetResources.slice(0,2).join(", ")||undefined}>{e.targetResources.slice(0,2).join(", ")||"—"}</td>
                        <td>
                          <Badge label={e.result??"unknown"} tone={e.result==="success"?"good":e.result==="failure"?"error":"neutral"}/>
                          {e.resultReason&&<div className="al-desc">{e.resultReason}</div>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {filtered.length > 0 && <div className="tbl-count">{filtered.length} of {data?.total??0} events</div>}
              </div>
            </>
        }
      </Card>
    </div>
  );
}
