import React from "react";
import { StatusDot, Badge } from "./SharedComponents";

export const M365_SVCS = ["Exchange Online","Microsoft Teams","SharePoint Online","OneDrive","Microsoft Entra","Microsoft Intune","Microsoft Defender","Viva Engage"];

/** Single matcher for "does this advisory title concern this service" — shared by
 *  the status grid, the benchmarks table, and the Network page so they never
 *  disagree about the same advisory. */
export const matchSvcIssue = (svc: string, title: string): boolean => {
  const t = title.toLowerCase();
  if (svc === "Exchange Online") return t.includes("exchange") || t.includes("outlook");
  if (svc === "Microsoft Teams") return t.includes("teams");
  if (svc === "SharePoint Online") return t.includes("sharepoint");
  if (svc === "OneDrive") return t.includes("onedrive");
  if (svc === "Microsoft Entra") return t.includes("entra") || t.includes("azure ad") || t.includes("identity");
  if (svc === "Microsoft Intune") return t.includes("intune") || t.includes("mdm");
  if (svc === "Microsoft Defender") return t.includes("defender") || t.includes("security") || t.includes("mde") || t.includes("mdo");
  if (svc === "Viva Engage") return t.includes("viva") || t.includes("yammer");
  return t.includes(svc.toLowerCase());
};

export function ServiceHealthGrid({ issues }: { issues: { title: string }[] }) {
  return (
    <div className="svc-grid">
      {M365_SVCS.map(svc => {
        const hasIssue = issues.some(i => matchSvcIssue(svc, i.title));
        return (
          <div key={svc} className="svc-item">
            <StatusDot status={hasIssue?"warning":"good"}/>
            <span className="svc-name">{svc}</span>
            <Badge label={hasIssue?"Advisory":"Operational"} tone={hasIssue?"warning":"good"}/>
          </div>
        );
      })}
    </div>
  );
}
