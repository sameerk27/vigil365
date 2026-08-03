import React, { useState, useMemo } from "react";
import { Activity, LogIn, XCircle, Globe, AlertCircle, ExternalLink, BarChart2 } from "lucide-react";
import { ServiceHealthData, SignInLocationsData, SignInEntry } from "../services/types";
import { fmtDate, relTime, fmtShort, countryFlag, fmtFullTime } from "../services/utils";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, StatusDot, SectHdr, MiniBarChart, rowActivation} from "../components/SharedComponents";
import { M365_SVCS, matchSvcIssue } from "./ServiceHealthPage";

export function NetworkPage({ serviceHealth, signInLocations }: { serviceHealth: ServiceHealthData|null; signInLocations: SignInLocationsData|null }) {
  const [selectedSignIn, setSelectedSignIn] = useState<SignInEntry|null>(null);
  const svcIssues = serviceHealth?.total??0;

  // top apps by sign-in count
  const topApps = useMemo(()=>{
    if (!signInLocations?.recent.length) return [];
    const counts: Record<string,{ total: number; failures: number; fullApp: string }> = {};
    signInLocations.recent.forEach(s=>{
      if (s.app) {
        const key = s.app.replace("Microsoft ","").slice(0,22);
        if (!counts[key]) counts[key] = { total: 0, failures: 0, fullApp: s.app };
        counts[key].total += 1;
        if (!s.success) counts[key].failures += 1;
      }
    });
    return Object.entries(counts).sort((a,b)=>b[1].total-a[1].total).slice(0,6).map(([app,data])=>({ label:app, value:data.total, failures:data.failures, fullApp:data.fullApp, color:"var(--color-primary)" }));
  },[signInLocations]);

  // per-service status from health data
  const svcStatus = M365_SVCS.map(svc=>{
    const matchingIssues = serviceHealth?.issues.filter(i=>matchSvcIssue(svc, i.title)) || [];
    const issue = matchingIssues[0];
    return { name:svc, issue, status:issue?"Advisory":"Operational" };
  });

  return (
    <div className="page">
      {selectedSignIn && (
        <DetailModal
          title={selectedSignIn.upn ?? "Sign-in Event"}
          subtitle={`${selectedSignIn.success ? "Successful" : "Failed"} · ${[selectedSignIn.city, selectedSignIn.country].filter(Boolean).join(", ") || "Unknown"}`}
          onClose={()=>setSelectedSignIn(null)}
          portalUrl="https://entra.microsoft.com/#view/Microsoft_AAD_IAM/SignInEventsV3Blade"
          portalLabel="View in Entra Sign-ins"
        >
          <DetailField label="User Principal Name" value={selectedSignIn.upn}/>
          <DetailField label="Application" value={selectedSignIn.app}/>
          <DetailField label="Result" value={selectedSignIn.success ? "Success" : "Failure"}/>
          <DetailField label="City" value={selectedSignIn.city}/>
          <DetailField label="Country" value={selectedSignIn.country}/>
          <DetailField label="Date/Time" value={fmtDate(selectedSignIn.created)}/>
        </DetailModal>
      )}
      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Activity size={18}/>} label="M365 SERVICE STATUS"
          value={svcIssues===0?"All Operational":`${svcIssues} Issue${svcIssues>1?"s":""}`}
          sub={svcIssues===0?"No active advisories":"Check advisories below"}
          tone={svcIssues===0?"good":svcIssues<=2?"warning":"error"}/>
        <KpiTile icon={<LogIn size={18}/>} label="SIGN-IN EVENTS" value={signInLocations?.total??"—"}
          sub="Last 100 sign-ins tracked" tone="neutral"/>
        <KpiTile icon={<XCircle size={18}/>} label="SIGN-IN FAILURES" value={signInLocations?.failures??"—"}
          sub="Auth failures in period" tone={(signInLocations?.failures??0)>10?"error":(signInLocations?.failures??0)>3?"warning":"good"}/>
        <KpiTile icon={<Globe size={18}/>} label="COUNTRIES" value={signInLocations?.countries??"—"}
          sub="Sign-in origin countries" tone={(signInLocations?.countries??0)>3?"warning":"good"}/>
      </div>

      <div className="two-col">
        <Card title="M365 Service Endpoint Status"
          badge={svcIssues>0?<Badge label={`${svcIssues} advisory`} tone="warning"/>:<Badge label="All operational" tone="good"/>}>
          <div className="svc-grid">
            {svcStatus.map(s=>(
              <div key={s.name} className="svc-item">
                <StatusDot status={s.issue?"warning":"good"}/>
                <span className="svc-name">{s.name}</span>
                <Badge label={s.status} tone={s.issue?"warning":"good"}/>
              </div>
            ))}
          </div>
          {svcIssues>0&&(
            <div data-inline-style="inline-f2fecb34dc">
              <SectHdr>ACTIVE ADVISORIES</SectHdr>
              {serviceHealth!.issues.map((iss,i)=>(
                <div key={i} className="al-item al-item-noclick" data-inline-style="inline-c98a9f1869">
                  <AlertCircle size={13} color="var(--status-warn-icon)"/>
                  <div className="al-body">
                    <div className="al-title">{iss.title}</div>
                    {iss.description&&<div className="al-desc">{iss.description}</div>}
                  </div>
                  <div data-inline-style="inline-658148c038">
                    <Badge label={iss.severity} tone={iss.severity==="High"||iss.severity==="Critical"?"error":"warning"}/>
                    <span className="al-date">{fmtShort(iss.detectedAt)}</span>
                    {iss.portalUrl&&<a href={iss.portalUrl} target="_blank" rel="noopener noreferrer" className="portal-link" onClick={e=>e.stopPropagation()}><ExternalLink size={11}/> Portal</a>}
                  </div>
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card title="Top Apps Accessed" badge={<Badge label="From sign-in log" tone="neutral"/>}>
          {topApps.length===0
            ? <EmptyState icon={<BarChart2 size={28}/>} message="No sign-in data available"/>
            : <>
                <MiniBarChart items={topApps}/>
                <div className="tbl-wrap" data-inline-style="inline-f2fecb34dc">
                  <table className="data-tbl">
                    <thead><tr><th scope="col">Application</th><th scope="col">Sign-in Events</th><th scope="col">Status</th></tr></thead>
                    <tbody>
                      {topApps.map((a,i)=>(
                        <tr key={i}>
                          <td><div className="al-title" title={(a as any).fullApp}>{a.label}</div></td>
                          <td data-inline-style="inline-3d9df89ef8">{a.value}</td>
                          <td><Badge label={(a as any).failures > 0 ? `${(a as any).failures} errors` : "Reachable"} tone={(a as any).failures > 0 ? "warning" : "good"}/></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
          }
        </Card>
      </div>

      <Card title="Recent Sign-in Activity" badge={<Badge label={`${signInLocations?.total??0} events`} tone="neutral"/>}>
        {!signInLocations?.configured||signInLocations.recent.length===0
          ? <EmptyState icon={<LogIn size={28}/>} message="No recent sign-in data"/>
          : <div className="tbl-wrap">
              <table className="data-tbl">
                <thead><tr><th scope="col">User</th><th scope="col">App</th><th scope="col">Location</th><th scope="col">Time</th><th scope="col">Result</th></tr></thead>
                <tbody>
                  {signInLocations.recent.map((s,i)=>(
                    <tr key={i} className="tbl-row-click" {...rowActivation(()=>setSelectedSignIn(s), "Open sign-in detail")}>
                      <td><div className="al-title">{s.upn?.split("@")[0]??"Unknown"}</div></td>
                      <td className="al-desc">{s.app??"—"}</td>
                      <td className="al-desc">{countryFlag(s.country) && <span className="flag-emoji">{countryFlag(s.country)}</span>} {[s.city,s.country].filter(Boolean).join(", ")||"Unknown"}</td>
                      <td className="al-date" title={fmtFullTime(s.created)}>{relTime(s.created) || fmtDate(s.created)}</td>
                      <td><Badge label={s.success?"Success":"Failed"} tone={s.success?"good":"error"}/></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
        }
      </Card>
    </div>
  );
}
