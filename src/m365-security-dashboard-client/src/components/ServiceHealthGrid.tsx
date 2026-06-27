import React from "react";
import { StatusDot, Badge } from "./SharedComponents";

const M365_SVCS = ["Exchange Online","Microsoft Teams","SharePoint Online","OneDrive","Microsoft Entra","Microsoft Intune","Microsoft Defender","Viva Engage"];

export function ServiceHealthGrid({ issues }: { issues: { title: string }[] }) {
  return (
    <div className="svc-grid">
      {M365_SVCS.map(svc => {
        const hasIssue = issues.some(i =>
          i.title.toLowerCase().includes(svc.split(" ")[0].toLowerCase()) ||
          i.title.toLowerCase().includes(svc.split(" ").at(-1)!.toLowerCase()));
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
