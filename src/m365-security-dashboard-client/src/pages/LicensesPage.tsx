import React, { useState, useMemo } from "react";
import { Package, CheckCircle, UserX, Key, Search, Clock, UserCheck } from "lucide-react";
import { LicenseData, InactiveUsersData, PasswordExpiryData } from "../services/types";
import { fmtDate, expiryChip } from "../services/utils";
import { KpiTile, Card, Badge, EmptyState, ProgressBar, MiniBarChart, InlineError, InfoRow, ExportDropdown } from "../components/SharedComponents";

/** Rows shown per password-expiry group before the table says what it is hiding. */
const PW_ROW_CAP = 10;

export function LicensesPage({ licenses, inactive, passwords }: {
  licenses: LicenseData|null; inactive: InactiveUsersData|null; passwords: PasswordExpiryData|null
}) {
  const [userSearch, setUserSearch] = useState("");
  const [inactiveSort, setInactiveSort] = useState<"days"|"alpha">("days");
  const [showLicensedOnly, setShowLicensedOnly] = useState(false);
  const [skuSearch, setSkuSearch] = useState("");

  const filteredInactive = useMemo(() => {
    let items = inactive?.inactive90 ?? [];
    if (showLicensedOnly) items = items.filter(u=>u.hasLicense);
    if (userSearch) { const q=userSearch.toLowerCase(); items=items.filter(u=>u.upn.toLowerCase().includes(q)||(u.name??'').toLowerCase().includes(q)); }
    if (inactiveSort==="days") items = [...items].sort((a,b)=>b.daysSince-a.daysSince);
    else items = [...items].sort((a,b)=>a.upn.localeCompare(b.upn));
    return items;
  }, [inactive, userSearch, inactiveSort, showLicensedOnly]);

  const filteredSkus = useMemo(() => {
    let items = licenses?.skus ?? [];
    if (skuSearch) { const q=skuSearch.toLowerCase(); items=items.filter(s=>s.name.toLowerCase().includes(q)); }
    return items;
  }, [licenses, skuSearch]);

  const utilPct = licenses?.totalPurchased ? Math.round(licenses.totalConsumed / licenses.totalPurchased * 100) : 0;
  return (
    <div className="page">
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Package size={18}/>} label="LICENSES PURCHASED" value={licenses?.totalPurchased??"—"}
          sub="Total across all SKUs" tone="neutral"/>
        <KpiTile icon={<CheckCircle size={18}/>} label="CONSUMED" value={licenses?.totalConsumed??"—"}
          sub={`${utilPct}% utilization`} tone={utilPct>95?"warning":utilPct>80?"good":"neutral"}/>
        <KpiTile icon={<UserX size={18}/>} label="INACTIVE USERS (90D)" value={inactive?.inactive90Count??"—"}
          sub="No sign-in in 90+ days" tone={(inactive?.inactive90Count??0)>0?"warning":"good"}/>
        <KpiTile icon={<Key size={18}/>} label="PASSWORDS EXPIRING" value={passwords?.expiringSoonCount??"—"}
          sub="Within next 14 days" tone={(passwords?.expiringSoonCount??0)>0?"warning":"good"}/>
      </div>

      <div className="two-col">
        <Card title="License Usage by SKU"
          badge={<Badge label={`${filteredSkus.length} / ${licenses?.skus.length??0} SKUs`} tone="neutral"/>}
          action={(licenses?.configured && (licenses?.skus.length??0)>0) ? (
            <div data-inline-style="inline-8da89a75a7">
              <label className="search-box" data-inline-style="inline-1fc8e3ac15">
                <Search size={14}/>
                <input value={skuSearch} onChange={e=>setSkuSearch(e.target.value)}
                  placeholder="Search SKU name…" className="search-input"/>
              </label>
              <ExportDropdown rows={filteredSkus.map(s=>({ SKU:s.name, Purchased:s.purchased, Consumed:s.consumed, Available:s.available }))} filename="licenses.csv"/>
              {skuSearch&&<button className="btn-apply" data-inline-style="inline-84a31235d6" onClick={()=>setSkuSearch("")}>Clear</button>}
            </div>
          ) : undefined}>
          {(!licenses?.configured || !licenses.skus.length)
            ? (licenses?.configured && licenses.error
                ? <InlineError title="License data unavailable" perm="Organization.Read.All" message={licenses.error}/>
                : <EmptyState icon={<Package size={28}/>} message={licenses?.error??"Requires Organization.Read.All permission"}/>)
            : <>
                <div className="util-banner">
                  <div className="ub-bar"><ProgressBar pct={utilPct} color={utilPct>95?"var(--status-error-icon)":utilPct>80?"var(--status-good-icon)":"var(--color-primary)"}/></div>
                  <div className="ub-pct">{utilPct}%</div>
                </div>
                <MiniBarChart items={filteredSkus.slice(0,8).map(s=>({ label:s.name.replace(/_/g," ").slice(0,22), value:s.consumed, color:s.available<=5?"#dc2626":"#3b82f6" }))}/>
                <div className="tbl-wrap" data-inline-style="inline-f2fecb34dc">
                  <table className="data-tbl">
                    <thead><tr><th scope="col">SKU</th><th scope="col">Purchased</th><th scope="col">Consumed</th><th scope="col" style={{textAlign: 'right'}}>Available (Waste)</th></tr></thead>
                    <tbody>
                      {filteredSkus.length===0&&<tr><td colSpan={4} className="td-empty">No SKUs match.</td></tr>}
                      {filteredSkus.map((s,i)=>{
                        const wastePct = s.purchased > 0 ? Math.round((s.available / s.purchased) * 100) : 0;
                        return (
                        <tr key={i}>
                          <td><div className="al-title">{s.name}</div></td>
                          <td>{s.purchased}</td>
                          <td>{s.consumed}</td>
                          <td style={{textAlign: 'right', color:s.available<=5?"var(--status-good-text)":"var(--status-warn-text)",fontWeight:600}}>
                            {s.available} <span style={{opacity: 0.7, fontSize: '0.9em', marginLeft: 4}}>({wastePct}%)</span>
                          </td>
                        </tr>
                      );})}
                    </tbody>
                  </table>
                </div>
              </>
          }
        </Card>

        <Card title="Inactive Users — 90+ Days"
          badge={<Badge label={`${filteredInactive.length} / ${inactive?.inactive90Count??0} users`} tone={(inactive?.inactive90Count??0)>0?"warning":"good"}/>}
          action={(inactive?.configured && (inactive?.inactive90Count??0)>0) ? (
            <div data-inline-style="inline-8da89a75a7">
              <label className="search-box" data-inline-style="inline-1fc8e3ac15">
                <Search size={14}/>
                <input value={userSearch} onChange={e=>setUserSearch(e.target.value)}
                  placeholder="Search UPN or name…" className="search-input"/>
              </label>
              <select value={inactiveSort} onChange={e=>setInactiveSort(e.target.value as "days"|"alpha")} className="filter-sel" data-inline-style="inline-1c8c76b2ad">
                <option value="days">Sort: Most inactive</option>
                <option value="alpha">Sort: A–Z</option>
              </select>
              <label className="toggle-label">
                <input type="checkbox" checked={showLicensedOnly} onChange={e=>setShowLicensedOnly(e.target.checked)}/>
                Licensed only
              </label>
              <ExportDropdown rows={filteredInactive.map(u=>({ UPN:u.upn, Name:u.name??"", LastSignIn:u.lastSignIn??"Never", DaysSince:u.daysSince, HasLicense:u.hasLicense }))} filename="inactive-users.csv"/>
              {(userSearch||showLicensedOnly)&&<button className="btn-apply" data-inline-style="inline-84a31235d6" onClick={()=>{setUserSearch("");setShowLicensedOnly(false);}}>Clear</button>}
            </div>
          ) : undefined}>
          {!inactive?.configured
            ? <EmptyState icon={<UserX size={28}/>} message={inactive?.error??"Requires AuditLog.Read.All + User.Read.All"}/>
            : inactive.inactive90Count===0
              ? <EmptyState icon={<UserCheck size={28}/>} message="No users inactive for 90+ days"/>
              : <>

                  <div className="alert-list">
                    {filteredInactive.length===0&&<div className="td-empty" data-inline-style="inline-43eb55eaea">No users match the filter.</div>}
                    {filteredInactive.slice(0,15).map((u,i)=>(
                      <div key={i} className="al-item al-item-noclick">
                        <UserX size={14} color="var(--status-warn-icon)"/>
                        <div className="al-body">
                          <div className="al-title">{u.upn}</div>
                          <div className="al-desc">{u.lastSignIn?`Last seen ${fmtDate(u.lastSignIn)}`:"Never signed in"}</div>
                        </div>
                        <div data-inline-style="inline-882e0924c7">
                          {u.hasLicense&&<Badge label="Licensed" tone="warning"/>}
                          <span className={`al-date days-chip ${u.daysSince>90?"over90":""}`}>{u.daysSince>=0?`${u.daysSince}d ago`:"Never"}</span>
                        </div>
                      </div>
                    ))}
                    {filteredInactive.length>15&&<div className="more-link">{filteredInactive.length-15} more — use search to narrow</div>}
                  </div>
                </>
          }
        </Card>
      </div>

      <Card title="Password Expiry Status" badge={<Badge label={`${passwords?.expiringSoonCount??0} expiring · ${passwords?.expiredCount??0} expired`} tone={(passwords?.expiringSoonCount??0)+(passwords?.expiredCount??0)>0?"warning":"good"}/>}>
        {!passwords?.configured
          ? <EmptyState message={passwords?.error??"Requires User.Read.All permission"}/>
          : <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">User</th><th scope="col">Days Until Expiry</th><th scope="col">Last Changed</th><th scope="col">Status</th></tr></thead>
                <tbody>
                  {passwords.expired.slice(0,PW_ROW_CAP).map((u,i)=>{
                    const c=expiryChip(u.daysUntilExpiry);
                    return (
                    <tr key={`exp-${i}`}>
                      <td><div className="al-title">{u.upn}</div></td>
                      <td><span className={`expiry-chip ${c.cls}`}><Clock size={11}/>{c.label}</span></td>
                      <td className="al-date">{fmtDate(u.lastChanged)}</td>
                      <td><Badge label="Expired" tone="error"/></td>
                    </tr>
                  );})}
                  {passwords.expiringSoon.slice(0,PW_ROW_CAP).map((u,i)=>{
                    const c=expiryChip(u.daysUntilExpiry);
                    return (
                    <tr key={`soon-${i}`}>
                      <td><div className="al-title">{u.upn}</div></td>
                      <td><span className={`expiry-chip ${c.cls}`}><Clock size={11}/>{c.label}</span></td>
                      <td className="al-date">{fmtDate(u.lastChanged)}</td>
                      <td><Badge label={u.daysUntilExpiry<=3?"Critical":"Expiring Soon"} tone={u.daysUntilExpiry<=3?"error":"warning"}/></td>
                    </tr>
                  );})}
                  {/* The card badge states the full counts, so a silently capped
                      table made the two disagree — and it capped the *expired*
                      list, the more urgent one, while showing every
                      expiring-soon row. Cap both, and say what is hidden. */}
                  {(passwords.expired.length>PW_ROW_CAP||passwords.expiringSoon.length>PW_ROW_CAP)&&(
                    <tr><td colSpan={4} className="td-empty">
                      Showing the first {PW_ROW_CAP} of each group
                      {passwords.expired.length>PW_ROW_CAP&&` · ${passwords.expired.length-PW_ROW_CAP} more expired`}
                      {passwords.expiringSoon.length>PW_ROW_CAP&&` · ${passwords.expiringSoon.length-PW_ROW_CAP} more expiring soon`}
                      . Export for the full list.
                    </td></tr>
                  )}
                  {passwords.expired.length===0&&passwords.expiringSoon.length===0&&(
                    <tr><td colSpan={4} className="td-empty">No passwords expiring in the next 14 days</td></tr>
                  )}
                </tbody>
              </table>
              <div className="info-rows" data-inline-style="inline-f2fecb34dc">
                <InfoRow label="Never-expire accounts" value={passwords.neverExpiresCount} tone={passwords.neverExpiresCount>10?"warning":"neutral"}/>
                <InfoRow label="Total users checked" value={passwords.totalUsers}/>
              </div>
            </div>
        }
      </Card>
    </div>
  );
}
