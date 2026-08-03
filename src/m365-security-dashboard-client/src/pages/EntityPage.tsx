import React, { useState, useEffect } from "react";
import { ArrowLeft, User, Monitor, ShieldAlert, Activity as ActivityIcon } from "lucide-react";
import { EntityProfile } from "../services/types";
import { entityApi } from "../services/api";
import { Card, Badge, EmptyState, LoadingSkeleton, KpiTile } from "../components/SharedComponents";
import { fmtDate, relTime, sevTone } from "../services/utils";

export function EntityPage({ kind, id, onBack }: { kind: "user" | "device"; id: string; onBack: () => void }) {
  const [profile, setProfile] = useState<EntityProfile | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    entityApi.getProfile(kind, id).then(p => { if (!cancelled) { setProfile(p); setLoading(false); } });
    return () => { cancelled = true; };
  }, [kind, id]);

  const s = profile?.summary;

  return (
    <div className="page">
      <button className="btn-secondary entity-back" onClick={onBack}><ArrowLeft size={14}/> Back</button>

      <div className="entity-header">
        <div className="entity-avatar">{kind === "user" ? <User size={22}/> : <Monitor size={22}/>}</div>
        <div data-inline-style="inline-d529e1f5d1">
          <h1 className="entity-name" title={id}>{id}</h1>
          <div className="entity-kind">{kind === "user" ? "User" : "Device"} investigation profile</div>
        </div>
      </div>

      {loading ? <LoadingSkeleton type="kpi"/> : !profile || !s ? (
        <Card title="Entity"><EmptyState message="Could not load this entity's profile."/></Card>
      ) : (
        <>
          <div className="kpi-row kpi-row-4">
            <KpiTile icon={<ShieldAlert size={18}/>} label="TOTAL ALERTS" value={s.alertCount} sub="Collected for this entity" tone={s.alertCount > 0 ? "warning" : "good"}/>
            <KpiTile icon={<ShieldAlert size={18}/>} label="OPEN ALERTS" value={s.openAlertCount} sub="Unresolved" tone={s.openAlertCount > 0 ? "error" : "good"}/>
            <KpiTile icon={<ActivityIcon size={18}/>} label="AUDIT ACTIVITY" value={s.activityCount} sub="Tenant events involving this entity" tone="neutral"/>
            <KpiTile icon={<ActivityIcon size={18}/>} label="LAST SEEN" value={s.lastSeen ? relTime(s.lastSeen) : "—"} sub={s.firstSeen ? `First: ${fmtDate(s.firstSeen)}` : "No activity"} tone="neutral"/>
          </div>

          <Card title="Timeline" badge={<Badge label={`${profile.timeline.length} events`} tone="neutral"/>}>
            {profile.timeline.length === 0 ? (
              <EmptyState message="No alerts or audit activity recorded for this entity yet."/>
            ) : (
              <div className="entity-timeline">
                {profile.timeline.map((ev, i) => (
                  <div className="entity-tl-item" key={`${ev.type}-${ev.at}-${i}`}>
                    <div className={`entity-tl-marker entity-tl-${ev.type}`} aria-hidden="true">
                      {ev.type === "alert" ? <ShieldAlert size={12}/> : <ActivityIcon size={12}/>}
                    </div>
                    <div className="entity-tl-body">
                      <div className="entity-tl-top">
                        <span className="entity-tl-title">{ev.title}</span>
                        <Badge label={ev.severity} tone={sevTone(ev.severity)}/>
                        <span className="entity-tl-time" title={fmtDate(ev.at)}>{relTime(ev.at)}</span>
                      </div>
                      <div className="entity-tl-detail">{ev.detail}</div>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </Card>
        </>
      )}
    </div>
  );
}
