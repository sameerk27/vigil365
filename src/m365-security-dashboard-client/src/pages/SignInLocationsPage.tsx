import React, { useState, useMemo } from "react";
import { MapPin, LogIn, XCircle, Globe, Search } from "lucide-react";
import { SignInLocationsData, SignInEntry } from "../services/types";
import { fmtDate, relTime, countryFlag } from "../services/utils";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, MiniBarChart, ExportDropdown } from "../components/SharedComponents";
import { FilterPresets } from "../components/FilterPresets";

export function SignInLocationsPage({ data }: { data: SignInLocationsData|null }) {
  const [selectedSignIn, setSelectedSignIn] = useState<SignInEntry|null>(null);
  const [signInSearch, setSignInSearch] = useState("");
  const [resultFilter, setResultFilter] = useState<""|"success"|"failure">("");
  const [countryFilter, setCountryFilter] = useState("");

  const allCountries = useMemo(()=>
    [...new Set((data?.recent??[]).map(s=>s.country).filter((c):c is string=>!!c))].sort(),
  [data]);

  const filteredSignIns = useMemo(() => {
    let items = data?.recent ?? [];
    if (resultFilter==="success") items = items.filter(s=>s.success);
    if (resultFilter==="failure") items = items.filter(s=>!s.success);
    if (countryFilter) items = items.filter(s=>s.country===countryFilter);
    if (signInSearch) {
      const q=signInSearch.toLowerCase();
      items=items.filter(s=>
        (s.upn??'').toLowerCase().includes(q)||
        (s.country??'').toLowerCase().includes(q)||
        (s.city??'').toLowerCase().includes(q)||
        (s.app??'').toLowerCase().includes(q)
      );
    }
    return items;
  }, [data, resultFilter, countryFilter, signInSearch]);

  return (
    <div className="page">
      {selectedSignIn && (
        <DetailModal
          title={selectedSignIn.upn ?? "Sign-in Event"}
          subtitle={`${selectedSignIn.success ? "Successful" : "Failed"} · ${[selectedSignIn.city, selectedSignIn.country].filter(Boolean).join(", ") || "Unknown location"}`}
          onClose={()=>setSelectedSignIn(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_IAM/SignInEventsV3Blade"
          portalLabel="View in Entra Sign-ins"
        >
          <DetailField label="User Principal Name" value={selectedSignIn.upn} copy={!!selectedSignIn.upn}/>
          <DetailField label="Application" value={selectedSignIn.app}/>
          <DetailField label="Result" value={selectedSignIn.success ? "Success" : "Failure"}/>
          <DetailField label="City" value={selectedSignIn.city}/>
          <DetailField label="Country" value={selectedSignIn.country}/>
          <DetailField label="Date/Time" value={selectedSignIn.created ? `${relTime(selectedSignIn.created)} (${fmtDate(selectedSignIn.created)})` : undefined} title={fmtDate(selectedSignIn.created)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<MapPin size={18}/>} label="COUNTRIES DETECTED" value={data?.countries??"—"}
          sub="Distinct sign-in countries" tone={(data?.countries??0)>5?"warning":"good"}/>
        <KpiTile icon={<LogIn size={18}/>} label="TOTAL SIGN-INS" value={data?.total??"—"}
          sub="Last 100 events" tone="neutral"/>
        <KpiTile icon={<XCircle size={18}/>} label="FAILED SIGN-INS" value={data?.failures??"—"}
          sub="Authentication failures" tone={(data?.failures??0)>5?"error":(data?.failures??0)>0?"warning":"good"}
          active={resultFilter==="failure"} onClick={()=>{setSignInSearch("");setCountryFilter("");setResultFilter(resultFilter==="failure"?"":"failure");}}/>
        <KpiTile icon={<Globe size={18}/>} label="UNIQUE APPS" value={data?([...new Set(data.recent.map(s=>s.app).filter(Boolean))].length):"—"}
          sub="Apps accessed" tone="info"/>
      </div>

      <div className="two-col">
        <Card title="Sign-ins by Country" badge={<Badge label={`${data?.byCountry.length??0} countries`} tone="neutral"/>}>
          {!data?.configured
            ? <EmptyState message={data?.error??"Requires AuditLog.Read.All permission"}/>
            : data.byCountry.length===0
              ? <EmptyState icon={<Globe size={28} color="#d1d5db"/>} message="No location data available"/>
              : <>
                  <MiniBarChart items={data.byCountry.slice(0,8).map(c=>({
                    label:c.country??"Unknown", value:c.count, color:c.failures>2?"#dc2626":"#3b82f6"
                  }))}/>
                  <div className="tbl-wrap" style={{marginTop:12}}>
                    <table className="data-tbl">
                      <thead><tr><th>Country</th><th>Sign-ins</th><th>Failures</th></tr></thead>
                      <tbody>
                        {data.byCountry.map((c,i)=>(
                          <tr key={i}>
                            <td><span className="flag-cell"><span className="flag-emoji">{countryFlag(c.country)}</span><span className="al-title">{c.country||"Unknown"}</span></span></td>
                            <td>{c.count}</td>
                            <td style={{color:c.failures>0?"var(--status-error-text)":"var(--status-good-text)",fontWeight:600}}>{c.failures}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </>
          }
        </Card>

        <Card title="Recent Sign-in Activity"
          badge={<Badge label={`${filteredSignIns.length} shown`} tone="neutral"/>}
          action={(data?.configured && data.recent.length>0) ? (
            <div style={{display:"flex",alignItems:"center",gap:6}}>
              <label className="search-box" style={{minWidth:180}}>
                <Search size={14} color="#94a3b8"/>
                <input value={signInSearch} onChange={e=>setSignInSearch(e.target.value)}
                  placeholder="Search user, country, city, app…" className="search-input"/>
              </label>
              <select value={resultFilter} onChange={e=>setResultFilter(e.target.value as ""|"success"|"failure")} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                <option value="">All results</option>
                <option value="success">Success</option>
                <option value="failure">Failure</option>
              </select>
              {allCountries.length>0&&(
                <select value={countryFilter} onChange={e=>setCountryFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
                  <option value="">All countries</option>
                  {allCountries.map(c=><option key={c} value={c}>{countryFlag(c)} {c}</option>)}
                </select>
              )}
              <ExportDropdown rows={filteredSignIns.map(s=>({ User:s.upn??"", App:s.app??"", City:s.city??"", Country:s.country??"", Result:s.success?"Success":"Failure", Time:s.created??"" }))} filename="sign-ins.csv"/>
              {(signInSearch||resultFilter||countryFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setSignInSearch("");setResultFilter("");setCountryFilter("");}}>Clear</button>}
              <FilterPresets pageKey="signins" filters={{signInSearch,resultFilter,countryFilter}}
                onLoad={f=>{setSignInSearch(f.signInSearch??"");setResultFilter((f.resultFilter as ""|"success"|"failure"|undefined)??"");setCountryFilter(f.countryFilter??"");}}/>
            </div>
          ) : undefined}>
          {!data?.configured
            ? <EmptyState message="Requires AuditLog.Read.All permission"/>
            : data.recent.length===0
              ? <EmptyState icon={<LogIn size={28} color="#d1d5db"/>} message="No recent sign-in data"/>
              : <>
                  <div className="alert-list">
                    {filteredSignIns.length===0&&<div className="td-empty" style={{padding:12}}>No sign-ins match the filter.</div>}
                    {filteredSignIns.map((s,i)=>(
                      <div key={i} className="al-item" onClick={()=>setSelectedSignIn(s)}>
                        <div style={{width:8,height:8,borderRadius:"50%",background:s.success?"var(--status-good-icon)":"var(--status-error-icon)",flexShrink:0,marginTop:3}}/>
                        <div className="al-body">
                          <div className="al-title">{s.upn?.split("@")[0]??"Unknown"}</div>
                          <div className="row-meta">
                            <span className="row-meta-item">{countryFlag(s.country)} {[s.city,s.country].filter(Boolean).join(", ")||"Unknown"}</span>
                            {s.app&&<span className="row-meta-item">{s.app}</span>}
                            <Badge label={s.success?"Success":"Failed"} tone={s.success?"good":"error"}/>
                            <span className="row-meta-item" title={fmtDate(s.created)}>{relTime(s.created)}</span>
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                </>
          }
        </Card>
      </div>
    </div>
  );
}
