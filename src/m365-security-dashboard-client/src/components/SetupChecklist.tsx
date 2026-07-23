import React, { useEffect, useState } from "react";
import { CheckCircle2, Circle, ChevronRight, X } from "lucide-react";
import { apiBase, apiFetch, crossNavigate } from "../services/api";
import { Card } from "./SharedComponents";

type SetupStep = { key: string; label: string; done: boolean; hint: string; page: string };
type SetupStatus = { complete: boolean; completedCount: number; totalCount: number; steps: SetupStep[] };

const DISMISS_KEY = "vigil365-setup-checklist-dismissed";

/**
 * First-run onboarding. A fresh install lands on an Overview full of empty
 * cards with no indication of what to do; this turns that into an ordered
 * "do these things" list driven by real backend state (/api/setup/status).
 *
 * Self-hides once every step is done, and can be dismissed early (persisted per
 * browser) so it does not nag an established install — but reappears if a step
 * later regresses, since that is a real problem worth surfacing again.
 */
export function SetupChecklist({ refreshKey }: { refreshKey: number }) {
  const [status, setStatus] = useState<SetupStatus | null>(null);
  const [dismissed, setDismissed] = useState(() => localStorage.getItem(DISMISS_KEY) === "1");

  useEffect(() => {
    let cancelled = false;
    apiFetch(`${apiBase}/api/setup/status`)
      .then(r => r.ok ? r.json() : Promise.reject())
      .then((d: SetupStatus) => { if (!cancelled) setStatus(d); })
      .catch(() => { if (!cancelled) setStatus(null); });
    return () => { cancelled = true; };
  }, [refreshKey]);

  if (!status) return null;

  // Once everything is done, clear any stale dismissal and stop rendering.
  if (status.complete) {
    if (localStorage.getItem(DISMISS_KEY)) localStorage.removeItem(DISMISS_KEY);
    return null;
  }

  // Dismissed early, and nothing has regressed since — stay quiet.
  if (dismissed) return null;

  const dismiss = () => { localStorage.setItem(DISMISS_KEY, "1"); setDismissed(true); };

  return (
    <Card title="Finish setting up Vigil365"
      badge={<span className="setup-progress-chip">{status.completedCount} of {status.totalCount} done</span>}
      action={
        <button type="button" className="setup-dismiss" onClick={dismiss}
          title="Hide this checklist (it returns if a step regresses)" aria-label="Hide setup checklist">
          <X size={14}/>
        </button>
      }>
      <div className="setup-progress-track" aria-hidden="true">
        <div className="setup-progress-fill" style={{ width: `${(status.completedCount / status.totalCount) * 100}%` }}/>
      </div>
      <ol className="setup-steps">
        {status.steps.map(step => (
          <li key={step.key} className={`setup-step ${step.done ? "done" : ""}`}>
            {step.done
              ? <CheckCircle2 size={17} className="setup-step-icon done" aria-label="Done"/>
              : <Circle size={17} className="setup-step-icon" aria-label="Not done"/>}
            <div className="setup-step-body">
              <span className="setup-step-label">{step.label}</span>
              {!step.done && <span className="setup-step-hint">{step.hint}</span>}
            </div>
            {!step.done && (
              <button type="button" className="setup-step-go"
                onClick={() => crossNavigate({ page: step.page })}>
                Go <ChevronRight size={13}/>
              </button>
            )}
          </li>
        ))}
      </ol>
    </Card>
  );
}
