import React, { useState, useRef, useEffect } from "react";
import { HelpCircle, X, ExternalLink } from "lucide-react";
import type { NavPage } from "../services/types";

type HelpEntry = {
  what: string;
  /** Optional Microsoft portal / docs the user might act in. */
  links?: { label: string; url: string }[];
};

/**
 * Per-page "what is this and what do I do here" help. Keyed by page id and
 * rendered from a single map so the header stays the one place that owns it —
 * no per-page wiring. Descriptions are written to match what each page actually
 * shows (the product audit found docs that promised features the app lacked).
 */
const HELP: Partial<Record<NavPage, HelpEntry>> = {
  overview: {
    what: "A whole-tenant summary: Secure Score, top KPIs, and the state of collection. Each card drills into its dedicated page. Depth lives in the left-menu tabs, not here.",
  },
  identity: {
    what: "Identity risk from Entra and Defender for Identity: risky users and sign-ins, MFA coverage, PIM assignments. Findings link to the matching Entra blade.",
    links: [{ label: "Entra risky users", url: "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskyUsersBlade" }],
  },
  devices: {
    what: "Intune device compliance and Defender endpoint vulnerabilities. Non-compliant and stale devices are surfaced with a portal deep link.",
    links: [{ label: "Intune devices", url: "https://intune.microsoft.com" }],
  },
  email: {
    what: "Defender for Office 365 signals: quarantined messages, mail-flow issues, and malware detections.",
    links: [{ label: "Defender portal", url: "https://security.microsoft.com" }],
  },
  incidents: {
    what: "Defender XDR incidents and alerts, plus Vigil365's own DB alerts and service advisories, in one prioritised list. Click any row for detail and correlated items.",
  },
  alertcenter: {
    what: "Define alert policies (metric, activity, anomaly), manage notification channels, review the coverage scorecard, and inspect collection-run health and Graph permissions.",
  },
  activityfeed: {
    what: "Tenant directory audit events — the raw activity behind activity-based alert policies. Search, filter by day range, and export to CSV.",
  },
  compliance: {
    what: "One honest control assessment (CIS/NIST/ISO/GDPR references) derived from collected signals, plus Secure Score and data-coverage KPIs. Controls with no data are marked NOT ASSESSED, not passed.",
  },
  recommendations: {
    what: "A single findings hub: prioritised recommendations folding in Conditional Access gaps and SharePoint/OneDrive sharing posture. Vigil365 recommends — it never changes anything itself.",
  },
  trends: {
    what: "Historical posture from periodic snapshots: Secure Score, risky users, compliance over time. Pick a range to see the trend.",
  },
  reports: {
    what: "Schedule an executive digest (daily/weekly/monthly) delivered by email with a CSV attachment, or preview it live. SMTP is configured under Alert Center → Notifications.",
  },
  conditionalaccess: {
    what: "Your Conditional Access policies with a state breakdown and an automated gap analysis (missing MFA baseline, legacy-auth exposure, report-only drift).",
    links: [{ label: "Entra Conditional Access", url: "https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade" }],
  },
  signinmap: {
    what: "Sign-in success/failure breakdown by country from recent sign-in events (a sample, not the full tenant history). Tabular — there is no map.",
  },
  servicehealth: {
    what: "Microsoft 365 service advisories and incidents — Microsoft's own service status, distinct from Vigil365's collection health.",
    links: [{ label: "M365 service health", url: "https://admin.microsoft.com/#/servicehealth" }],
  },
  licenses: {
    what: "License SKU consumption, inactive users, and expiring/expired passwords.",
  },
  users: {
    what: "In-app access control: assign Admin / Analyst / Viewer roles, invite users, and verify the tamper-evident audit trail. Roles are Vigil365's own — they do not change anyone's Microsoft account.",
  },
  setup: {
    what: "Connect Microsoft Graph with your tenant and app-registration credentials (secret or certificate). Required before collection can run.",
    links: [{ label: "Entra app registrations", url: "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade" }],
  },
};

export function PageHelp({ page }: { page: NavPage }) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const entry = HELP[page];

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDown); document.removeEventListener("keydown", onKey); };
  }, [open]);

  // Close the popover when navigating to a page that has no help entry.
  useEffect(() => { if (!entry) setOpen(false); }, [entry]);

  if (!entry) return null;

  return (
    <div className="page-help" ref={wrapRef}>
      <button type="button" className="page-help-btn" aria-label={`About the ${page} page`}
        aria-expanded={open} onClick={() => setOpen(o => !o)}>
        <HelpCircle size={16}/>
      </button>
      {open && (
        <div className="page-help-pop" role="dialog" aria-label="Page help">
          <div className="page-help-head">
            <span>About this page</span>
            <button type="button" onClick={() => setOpen(false)} aria-label="Close help"><X size={13}/></button>
          </div>
          <p className="page-help-body">{entry.what}</p>
          {entry.links && (
            <div className="page-help-links">
              {entry.links.map(l => (
                <a key={l.url} href={l.url} target="_blank" rel="noopener noreferrer">
                  {l.label} <ExternalLink size={11}/>
                </a>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
