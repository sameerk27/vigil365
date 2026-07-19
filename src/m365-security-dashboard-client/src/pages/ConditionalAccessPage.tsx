import React, { useState, useMemo } from "react";
import { ShieldCheck, Eye, ShieldOff, Shield, Search } from "lucide-react";
import { ConditionalAccessData, CAPolicy, Tone } from "../services/types";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, ExportDropdown, InfoRow } from "../components/SharedComponents";
import { FilterPresets } from "../components/FilterPresets";

export function ConditionalAccessPage({ data }: { data: ConditionalAccessData|null }) {
  const [selectedPolicy, setSelectedPolicy] = useState<CAPolicy|null>(null);
  const [policySearch, setPolicySearch] = useState("");
  const [stateFilter, setStateFilter] = useState("");

  const stateTone = (s: string): Tone => s==="enabled"?"good":s==="enabledForReportingButNotEnforced"?"warning":"neutral";
  const stateLabel = (s: string) => s==="enabled"?"Enabled":s==="enabledForReportingButNotEnforced"?"Report Only":"Disabled";
  const total = data?(data.enabled+data.disabled+data.reportOnly):0;

  const filteredPolicies = useMemo(() => {
    let items = data?.policies ?? [];
    if (stateFilter) items = items.filter(p=>p.state===stateFilter);
    if (policySearch) { const q=policySearch.toLowerCase(); items=items.filter(p=>p.name.toLowerCase().includes(q)||p.apps.toLowerCase().includes(q)||p.inclUsers.toLowerCase().includes(q)); }
    return items;
  }, [data, stateFilter, policySearch]);

  return (
    <div className="page">
      {selectedPolicy && (
        <DetailModal
          title={selectedPolicy.name}
          subtitle={`${stateLabel(selectedPolicy.state)} · Conditional Access`}
          onClose={()=>setSelectedPolicy(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade/~/Policies"
          portalLabel="View in Entra CA"
        >
          <DetailField label="Policy Name" value={selectedPolicy.name}/>
          <DetailField label="State" value={stateLabel(selectedPolicy.state)}/>
          <div className="dm-section-hdr">Conditions</div>
          <DetailField label="Users Included" value={selectedPolicy.inclUsers}/>
          <DetailField label="Users Excluded" value={selectedPolicy.exclUsers !== "None" ? selectedPolicy.exclUsers : null}/>
          <DetailField label="Applications" value={selectedPolicy.apps}/>
          {selectedPolicy.controls.length > 0 && (
            <>
              <div className="dm-section-hdr">Grant Controls</div>
              {selectedPolicy.controls.map((c,i)=><DetailField key={i} label={`Control ${i+1}`} value={c}/>)}
            </>
          )}
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<ShieldCheck size={18}/>} label="ENABLED POLICIES" value={data?.enabled??"—"}
          sub="Actively enforced" tone={(data?.enabled??0)>0?"good":"warning"}
          active={stateFilter==="enabled"} onClick={()=>{setPolicySearch("");setStateFilter(stateFilter==="enabled"?"":"enabled");}}/>
        <KpiTile icon={<Eye size={18}/>} label="REPORT ONLY" value={data?.reportOnly??"—"}
          sub="Not yet enforced" tone={(data?.reportOnly??0)>0?"info":"neutral"}
          active={stateFilter==="enabledForReportingButNotEnforced"} onClick={()=>{setPolicySearch("");setStateFilter(stateFilter==="enabledForReportingButNotEnforced"?"":"enabledForReportingButNotEnforced");}}/>
        <KpiTile icon={<ShieldOff size={18}/>} label="DISABLED" value={data?.disabled??"—"}
          sub="Not active" tone={(data?.disabled??0)>0?"warning":"good"}
          active={stateFilter==="disabled"} onClick={()=>{setPolicySearch("");setStateFilter(stateFilter==="disabled"?"":"disabled");}}/>
        <KpiTile icon={<Shield size={18}/>} label="TOTAL POLICIES" value={total}
          sub="All CA policies" tone={total>0?"neutral":"warning"}
          active={!stateFilter&&!policySearch} onClick={()=>{setPolicySearch("");setStateFilter("");}}/>
      </div>

      <Card title="Conditional Access Policies"
        badge={<Badge label={`${filteredPolicies.length} / ${data?.policies.length??0} policies`} tone="neutral"/>}
        action={(data?.configured && data.policies.length>0) ? (
          <div style={{display:"flex",alignItems:"center",gap:6}}>
            <label className="search-box" style={{minWidth:200}}>
              <Search size={14}/>
              <input value={policySearch} onChange={e=>setPolicySearch(e.target.value)}
                placeholder="Search policy name, scope, apps…" className="search-input"/>
            </label>
            <select value={stateFilter} onChange={e=>setStateFilter(e.target.value)} className="filter-sel" style={{fontSize:12,padding:"5px 8px"}}>
              <option value="">All states</option>
              <option value="enabled">Enabled</option>
              <option value="enabledForReportingButNotEnforced">Report Only</option>
              <option value="disabled">Disabled</option>
            </select>
            <ExportDropdown rows={filteredPolicies.map(p=>({ Name:p.name, State:stateLabel(p.state), Scope:p.inclUsers, Apps:p.apps, Controls:p.controls.join("; ") }))} filename="ca-policies.csv"/>
            {(policySearch||stateFilter)&&<button className="btn-apply" style={{padding:"5px 10px",fontSize:12}} onClick={()=>{setPolicySearch("");setStateFilter("");}}>Clear</button>}
            <FilterPresets pageKey="ca-policies" filters={{policySearch,stateFilter}}
              onLoad={f=>{setPolicySearch(f.policySearch??"");setStateFilter(f.stateFilter??"");}}/>
          </div>
        ) : undefined}>
        {!data?.configured
          ? <EmptyState message={data?.error??"Requires Policy.Read.All permission"}/>
          : data.policies.length===0
            ? <EmptyState icon={<ShieldOff size={36}/>} message="No Conditional Access policies found. This is a significant security gap."/>
            : <>
                <div className="tbl-wrap">
                  <table className="data-tbl">
                    <thead><tr><th scope="col">Policy Name</th><th scope="col">State</th><th scope="col">Scope</th><th scope="col">Applications</th><th scope="col">Controls Required</th></tr></thead>
                    <tbody>
                      {filteredPolicies.length===0&&<tr><td colSpan={5} className="td-empty">No policies match the filter.</td></tr>}
                      {filteredPolicies.map((p,i)=>(
                        <tr key={i} className="tbl-row-click" onClick={()=>setSelectedPolicy(p)}>
                          <td>
                            <div className="al-title trunc" style={{maxWidth:200}} title={p.name}>{p.name}</div>
                            <div className="al-desc">{p.inclUsers} → {p.apps}</div>
                          </td>
                          <td><Badge label={stateLabel(p.state)} tone={stateTone(p.state)}/></td>
                          <td>
                            <div className="al-desc trunc" style={{maxWidth:120}} title={p.inclUsers}>{p.inclUsers}</div>
                            {p.exclUsers!=="None"&&<div className="al-desc tone-warning">{p.exclUsers}</div>}
                          </td>
                          <td className="al-desc trunc" style={{maxWidth:120}} title={p.apps}>{p.apps}</td>
                          <td style={{display:"flex",flexWrap:"wrap",gap:4,paddingTop:8}}>
                            {p.controls.length>0?p.controls.map((c,j)=><Badge key={j} label={c} tone="info"/>):<span className="al-desc">None</span>}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {filteredPolicies.length > 0 && <div className="tbl-count">{filteredPolicies.length} of {data?.policies.length??0} policies</div>}
                </div>
              </>
        }
      </Card>

      <Card title="Control Coverage Analysis">
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead><tr><th scope="col">Control</th><th scope="col">Enforced By</th><th scope="col">Best Practice</th></tr></thead>
            <tbody>
              {[
                { control:"mfa", label:"Multi-Factor Authentication", rec:"Should cover all users & all apps at minimum" },
                { control:"compliantDevice", label:"Compliant Device Required", rec:"Apply to sensitive apps and admin portals" },
                { control:"approvedApplication", label:"Approved App Required", rec:"Restrict to managed apps for mobile access" },
              ].map((row,i)=>{
                const count=data?.policies.filter(p=>p.controls.includes(row.control)&&p.state==="enabled").length??0;
                return(
                  <tr key={i}>
                    <td><div className="al-title">{row.label}</div></td>
                    <td style={{fontWeight:600,color:count>0?"var(--status-good-text)":"var(--status-error-text)"}}>{count} active {count===1?"policy":"policies"}</td>
                    <td className="al-desc">{row.rec}</td>
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
