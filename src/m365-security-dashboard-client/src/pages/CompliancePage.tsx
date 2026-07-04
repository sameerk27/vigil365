import React, { useState, useMemo } from "react";
import { Shield, FileText, Star, Search, ShieldAlert, ShieldCheck, Flag, ExternalLink, CheckCircle2, XCircle } from "lucide-react";
import { SecureScore, Overview, DlpAlertsData, PurviewData, McasAlertsData, InsiderRiskData, AttackSimulationData, DlpAlert, McasAlert, InsiderRiskAlert, AttackSim, Tone, IdentityData, DevicesData, ConditionalAccessData, SecurityIncidentsData, PrivilegedRolesData, EmailProtectionData } from "../services/types";
import { fmtDate, relTime, pctTone, sevTone } from "../services/utils";
import { DetailModal, DetailField, KpiTile, Card, Badge, EmptyState, ProgressBar, ExportDropdown, StatBox, SectHdr } from "../components/SharedComponents";
import { FilterPresets } from "../components/FilterPresets";

export function CompliancePage({ secureScore, overview, dlpAlerts, purview, mcasAlerts, insiderRisk, attackSimulation, identity, devices, ca, securityIncidents, privilegedRoles, emailProtection }: {
  secureScore: SecureScore|null; overview: Overview|null; dlpAlerts: DlpAlertsData|null; purview: PurviewData|null;
  mcasAlerts: McasAlertsData|null; insiderRisk: InsiderRiskData|null; attackSimulation: AttackSimulationData|null;
  identity?: IdentityData|null; devices?: DevicesData|null; ca?: ConditionalAccessData|null;
  securityIncidents?: SecurityIncidentsData|null; privilegedRoles?: PrivilegedRolesData|null; emailProtection?: EmailProtectionData|null;
}) {
  const [selectedDlp, setSelectedDlp] = useState<DlpAlert|null>(null);
  const [selectedMcas, setSelectedMcas] = useState<McasAlert|null>(null);
  const [selectedIrm, setSelectedIrm] = useState<InsiderRiskAlert|null>(null);
  const [selectedSim, setSelectedSim] = useState<AttackSim|null>(null);
  const [selectedFw, setSelectedFw] = useState<any|null>(null);
  const [alertSearch, setAlertSearch] = useState("");
  const [alertSev, setAlertSev] = useState("");

  const filteredDlp = useMemo(() => {
    let items = dlpAlerts?.alerts ?? [];
    if (alertSev) items = items.filter(a=>a.severity.toLowerCase()===alertSev);
    if (alertSearch) { const q=alertSearch.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.description??'').toLowerCase().includes(q)); }
    return items;
  }, [dlpAlerts, alertSev, alertSearch]);

  const filteredMcas = useMemo(() => {
    let items = mcasAlerts?.alerts ?? [];
    if (alertSev) items = items.filter(a=>a.severity.toLowerCase()===alertSev);
    if (alertSearch) { const q=alertSearch.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.category??'').toLowerCase().includes(q)); }
    return items;
  }, [mcasAlerts, alertSev, alertSearch]);

  const filteredIrm = useMemo(() => {
    let items = insiderRisk?.alerts ?? [];
    if (alertSev) items = items.filter(a=>a.severity.toLowerCase()===alertSev);
    if (alertSearch) { const q=alertSearch.toLowerCase(); items=items.filter(a=>(a.title??'').toLowerCase().includes(q)||(a.description??'').toLowerCase().includes(q)); }
    return items;
  }, [insiderRisk, alertSev, alertSearch]);

  const hasAlertFilter = !!(alertSearch || alertSev);

  const [thresholds, setThresholds] = useState(() => {
    try {
      const saved = localStorage.getItem("vigil365_compliance_thresholds");
      if (saved) return JSON.parse(saved);
    } catch {}
    return { mfaTarget: 90, maxAdmins: 5, minCaPolicies: 3, maxFailedSignIns: 50, maxPhishRate: 10, minSecureScore: 70 };
  });
  const [showThresholdModal, setShowThresholdModal] = useState(false);

  // Dynamic M365 Compliance Framework Engine (16 Comprehensive Controls).
  // Each control declares `hasData` — whether its source signal is actually
  // present. Controls without data are scored as "Insufficient data", NOT PASS,
  // so an unconnected tenant never shows a green compliance score.
  const identityData = !!identity?.configured;
  const caData = !!ca?.configured;
  const devicesData = (devices?.totalDevices ?? 0) > 0 || (devices?.nonCompliant ?? 0) > 0;
  const allControls = useMemo(() => [
    { id:"PR.AA-01", cis:"CIS 6.1", iso:"A.5.17", gdpr:"Art. 32(1)(b)", name:"Multi-Factor Authentication Enforcement", hasData:(identity?.mfa?.total ?? 0) > 0, passed:(identity?.mfa?.percentage ?? 0) >= thresholds.mfaTarget, signal: (identity?.mfa?.total ?? 0) > 0 ? `${identity!.mfa.percentage.toFixed(1)}% MFA registered (Target: ≥${thresholds.mfaTarget}%)` : "MFA data not collected (needs Reports.Read.All)", fix:"Require MFA for all users in Conditional Access", link:"https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade/~/Policies" },
    { id:"PR.AA-02", cis:"CIS 5.1", iso:"A.8.2", gdpr:"Art. 32(4)", name:"Privileged Identity Management (PIM) & Role Governance", hasData:!!privilegedRoles?.configured, passed:(privilegedRoles?.totalPrivilegedUsers ?? 0) > 0 && (privilegedRoles?.totalPrivilegedUsers ?? 0) <= thresholds.maxAdmins, signal: privilegedRoles?.configured ? `${privilegedRoles.totalPrivilegedUsers ?? 0} privileged users (Max: ${thresholds.maxAdmins})` : "Privileged role data not collected", fix:"Enforce PIM just-in-time elevation and limit permanent global admins", link:"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/PrivilegedIdentityManagementBlade" },
    { id:"PR.AA-03", cis:"CIS 6.3", iso:"A.5.15", gdpr:"Art. 32(1)(b)", name:"Zero Trust Conditional Access Baseline", hasData:caData, passed:(ca?.policies?.length ?? 0) >= thresholds.minCaPolicies, signal: caData ? `${ca?.policies?.length ?? 0} active CA policies (Target: ≥${thresholds.minCaPolicies})` : "CA policy data not collected", fix:"Implement baseline Zero Trust policies (Block legacy auth, require compliant device)", link:"https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade/~/Policies" },
    { id:"PR.AA-04", cis:"CIS 5.2", iso:"A.8.5", gdpr:"Art. 32(1)(a)", name:"Legacy Authentication Protocol Blocking", hasData:caData, passed:(ca?.policies ?? []).some((p:any) => p.name?.toLowerCase().includes("legacy") || p.name?.toLowerCase().includes("block")), signal: !caData ? "CA policy data not collected" : (ca?.policies ?? []).some((p:any) => p.name?.toLowerCase().includes("legacy") || p.name?.toLowerCase().includes("block")) ? "Legacy auth blocked" : "Legacy auth permitted", fix:"Create Conditional Access policy blocking client apps using basic/legacy auth", link:"https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade/~/Policies" },

    { id:"DE.CM-01", cis:"CIS 6.2", iso:"A.8.16", gdpr:"Art. 33", name:"Active Risky User Mitigation", hasData:identityData, passed:(identity?.riskyUsers ?? 0) === 0, signal: identityData ? `${identity?.riskyUsers ?? 0} active risky users` : "Identity data not collected", fix:"Investigate high-risk users and confirm compromise or dismiss", link:"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskyUsersBlade" },
    { id:"DE.CM-02", cis:"CIS 6.4", iso:"A.8.16", gdpr:"Art. 33", name:"Identity Anomaly Detection (Sign-in Signals)", hasData:identityData, passed:(identity?.signIns?.failed ?? 0) < thresholds.maxFailedSignIns, signal: identityData ? `${identity?.signIns?.failed ?? 0} failed sign-ins (Max: <${thresholds.maxFailedSignIns})` : "Sign-in data not collected", fix:"Review Entra sign-in diagnostic logs for brute-force attempts", link:"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/SignIns" },

    { id:"PR.DS-01", cis:"CIS 4.1", iso:"A.8.1", gdpr:"Art. 32(1)(b)", name:"Endpoint Compliance & Health Enrollment", hasData:devicesData, passed:(devices?.totalDevices ?? 0) > 0 && (devices?.nonCompliant ?? 0) === 0, signal: devicesData ? `${devices?.nonCompliant ?? 0} non-compliant of ${devices?.totalDevices ?? 0} devices` : "Device data not collected", fix:"Enforce Intune device compliance policies", link:"https://intune.microsoft.com/#view/Microsoft_Intune_DeviceSettings/DevicesComplianceMenu/~/compliancePolicies" },
    { id:"PR.DS-02", cis:"CIS 4.2", iso:"A.8.12", gdpr:"Art. 32(1)(a)", name:"Windows BitLocker Disk Encryption", hasData:devicesData, passed:(devices?.nonCompliant ?? 0) === 0, signal: !devicesData ? "Device data not collected" : (devices?.nonCompliant ?? 0) === 0 ? "Endpoints encrypted" : "Encryption unverified on non-compliant devices", fix:"Deploy BitLocker silent encryption policy in Intune Endpoint Security", link:"https://intune.microsoft.com/#view/Microsoft_Intune_Workflows/SecurityManagementMenu/~/diskEncryption" },
    { id:"PR.DS-03", cis:"CIS 7.1", iso:"A.8.8", gdpr:"Art. 32(1)(c)", name:"Endpoint EDR & Malicious Software Protection", hasData:!!securityIncidents?.configured, passed:(securityIncidents?.total ?? 0) === 0, signal: securityIncidents?.configured ? `${securityIncidents.total ?? 0} active security incidents` : "Incident data not collected", fix:"Review Microsoft Defender XDR incident queue", link:"https://security.microsoft.com/incidents" },

    { id:"PR.DS-04", cis:"CIS 3.1", iso:"A.8.12", gdpr:"Art. 32(1)(b)", name:"Data Loss Prevention (DLP) Enforcement", hasData:!!dlpAlerts?.configured, passed:(dlpAlerts?.total ?? 0) === 0, signal: dlpAlerts?.configured ? `${dlpAlerts.total ?? 0} active DLP violations` : "DLP data not collected", fix:"Review DLP incident queue and adjust oversharing rules", link:"https://compliance.microsoft.com/datalossprevention/alerts" },
    { id:"PR.DS-05", cis:"CIS 3.2", iso:"A.8.11", gdpr:"Art. 32(1)(a)", name:"Information Protection & Sensitivity Labelling", hasData:!!insiderRisk?.configured, passed:(insiderRisk?.total ?? 0) === 0, signal: insiderRisk?.configured ? (insiderRisk.total ? `${insiderRisk.total} insider risk alerts` : "No active data leakage alerts") : "Insider risk data not collected", fix:"Configure Microsoft Purview sensitivity labels and auto-labelling rules", link:"https://compliance.microsoft.com/informationprotection" },

    { id:"PR.PT-01", cis:"CIS 9.1", iso:"A.8.7", gdpr:"Art. 32(1)(b)", name:"Anti-Phishing & Impersonation Defense", hasData:!!attackSimulation?.configured && (attackSimulation?.total ?? 0) > 0, passed:(attackSimulation?.avgCompromiseRate ?? 0) < thresholds.maxPhishRate, signal: attackSimulation?.configured && (attackSimulation?.total ?? 0) > 0 ? `${attackSimulation!.avgCompromiseRate.toFixed(1)}% phish compromise rate (Target: <${thresholds.maxPhishRate}%)` : "No phishing simulation data", fix:"Run ongoing Defender for Office 365 phishing simulations", link:"https://security.microsoft.com/attacksimulator" },
    { id:"PR.PT-02", cis:"CIS 9.2", iso:"A.8.7", gdpr:"Art. 32(1)(b)", name:"Safe Attachments & Safe Links Sandboxing", hasData:!!emailProtection?.configured, passed:!!emailProtection?.configured, signal: emailProtection?.configured ? `${emailProtection.total ?? 0} MDO threat detections` : "MDO data not collected", fix:"Ensure Standard or Strict preset security policies are enabled in MDO", link:"https://security.microsoft.com/presetSecurityPolicies" },

    { id:"DE.AE-01", cis:"CIS 8.1", iso:"A.8.15", gdpr:"Art. 30", name:"Unified Audit Logging & Purview Signal Retention", hasData:purview != null, passed:!!purview?.configured, signal:purview?.configured ? "Purview audit log active" : "Purview API disconnected", fix:"Connect Microsoft Purview API permission SecurityAlert.Read.All", link:"https://compliance.microsoft.com/auditlogsearch" },
    { id:"DE.AE-02", cis:"CIS 2.1", iso:"A.5.23", gdpr:"Art. 28", name:"Cloud App (OAuth) Permission Governance", hasData:!!mcasAlerts?.configured, passed:(mcasAlerts?.total ?? 0) === 0, signal: mcasAlerts?.configured ? `${mcasAlerts.total ?? 0} risky cloud app alerts` : "Cloud app data not collected", fix:"Review Microsoft Defender for Cloud Apps OAuth app grant queue", link:"https://security.microsoft.com/cloudapps/oauth-apps" },

    { id:"ID.GV-01", cis:"CIS 16.1", iso:"A.5.1", gdpr:"Art. 24", name:"Continuous Posture Optimization", hasData:!!secureScore?.configured, passed:(secureScore?.percentage ?? 0) >= thresholds.minSecureScore, signal: secureScore?.configured ? `Secure Score at ${secureScore.percentage}% (Target: ≥${thresholds.minSecureScore}%)` : "Secure Score not collected", fix:"Complete recommended improvement actions in Microsoft Defender", link:"https://security.microsoft.com/securescore?viewid=actions" }
  ], [identity, identityData, devices, devicesData, dlpAlerts, ca, caData, purview, secureScore, overview, insiderRisk, attackSimulation, mcasAlerts, securityIncidents, privilegedRoles, emailProtection, thresholds]);

  // Scores are computed only over controls with data — "insufficient data" never
  // counts as pass OR fail. A framework with no measurable controls shows N/A.
  const frameworks = useMemo(() => [
    { name:"NIST CSF 2.0 (2024)", desc:"National Institute of Standards & Technology Core Framework", controls: allControls },
    { name:"CIS Controls v8.1", desc:"Center for Internet Security Critical Security Controls v8.1", controls: allControls.filter(c => c.cis) },
    { name:"ISO/IEC 27001:2022/Amd 1:2024", desc:"Information Security Management Systems Annex A (2024 Amendment)", controls: allControls.filter(c => c.iso) },
    { name:"GDPR Art. 32 (2016/679)", desc:"EU General Data Protection Regulation Security of Processing", controls: allControls.filter(c => c.gdpr) }
  ].map(fw => {
    const measured = fw.controls.filter(c => c.hasData);
    const passed = measured.filter(c => c.passed).length;
    const score = measured.length > 0 ? Math.round((passed / measured.length) * 100) : 0;
    const noData = measured.length === 0;
    return { ...fw, score, passed, total: fw.controls.length, measured: measured.length, noData,
      status: noData ? "No data" : score >= 80 ? "Compliant" : score >= 50 ? "Action Required" : "High Risk" };
  }), [allControls]);

  const controls = useMemo(() => {
    const calc = (ids: string[]) => {
      const sub = allControls.filter(c => ids.includes(c.id) && c.hasData);
      const pass = sub.filter(c => c.passed).length;
      return sub.length ? Math.round((pass / sub.length) * 100) : 0;
    };
    return [
      { area:"Identity & Access", score: calc(["PR.AA-01","PR.AA-02","PR.AA-03","PR.AA-04"]), max:100, items:["MFA enforcement","Conditional Access","Privileged Identity","Legacy Auth Blocking"] },
      { area:"Device Health", score: calc(["PR.DS-01","PR.DS-02","PR.DS-03"]), max:100, items:["Intune compliance","BitLocker encryption","EDR protection"] },
      { area:"Data Protection", score: calc(["PR.DS-04","PR.DS-05"]), max:100, items:["DLP policies","Sensitivity labelling","Insider risk"] },
      { area:"Email & Collaboration", score: calc(["PR.PT-01","PR.PT-02"]), max:100, items:["Anti-phishing","Safe Attachments","Attack simulation"] },
      { area:"Cloud Apps & Audit", score: calc(["DE.AE-01","DE.AE-02","DE.CM-01","DE.CM-02"]), max:100, items:["Purview logging","OAuth app governance","Anomaly detection"] },
      { area:"Governance & Posture", score: calc(["ID.GV-01"]), max:100, items:["Secure score baseline","Continuous optimization"] }
    ];
  }, [allControls]);

  return (
    <div className="page">
      {selectedDlp && (
        <DetailModal title={selectedDlp.title ?? "DLP Alert"} subtitle={`${selectedDlp.severity} · DLP`}
          onClose={()=>setSelectedDlp(null)}
          portalUrl={selectedDlp.alertWebUrl ?? "https://compliance.microsoft.com/datalossprevention/alerts"}
          portalLabel="View DLP Alerts">
          <DetailField label="Alert ID" value={selectedDlp.id}/>
          <DetailField label="Severity" value={selectedDlp.severity}/>
          <DetailField label="Status" value={selectedDlp.status}/>
          <DetailField label="Category" value={selectedDlp.category}/>
          <DetailField label="Service Source" value={selectedDlp.serviceSource}/>
          <DetailField label="Detected" value={fmtDate(selectedDlp.createdDateTime)}/>
          {selectedDlp.description&&<><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedDlp.description}</div></>}
        </DetailModal>
      )}
      {selectedMcas && (
        <DetailModal title={selectedMcas.title ?? "Cloud App Alert"} subtitle={`${selectedMcas.severity} · MCAS`}
          onClose={()=>setSelectedMcas(null)}
          portalUrl={selectedMcas.alertWebUrl ?? (selectedMcas.id ? `https://security.microsoft.com/alerts/${selectedMcas.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR">
          <DetailField label="Alert ID" value={selectedMcas.id}/>
          <DetailField label="Severity" value={selectedMcas.severity}/>
          <DetailField label="Status" value={selectedMcas.status}/>
          <DetailField label="Category" value={selectedMcas.category}/>
          <DetailField label="Detected" value={fmtDate(selectedMcas.createdDateTime)}/>
          {selectedMcas.description&&<><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedMcas.description}</div></>}
        </DetailModal>
      )}
      {selectedIrm && (
        <DetailModal title={selectedIrm.title ?? "Insider Risk Alert"} subtitle={`${selectedIrm.severity} · IRM`}
          onClose={()=>setSelectedIrm(null)}
          portalUrl={selectedIrm.alertWebUrl ?? (selectedIrm.id ? `https://security.microsoft.com/alerts/${selectedIrm.id}` : "https://security.microsoft.com/alerts")}
          portalLabel="View in Defender XDR">
          <DetailField label="Alert ID" value={selectedIrm.id}/>
          <DetailField label="Severity" value={selectedIrm.severity}/>
          <DetailField label="Status" value={selectedIrm.status}/>
          <DetailField label="Category" value={selectedIrm.category}/>
          <DetailField label="Detected" value={fmtDate(selectedIrm.createdDateTime)}/>
          {selectedIrm.description&&<><div className="dm-section-hdr">Description</div><div className="dm-desc-block">{selectedIrm.description}</div></>}
        </DetailModal>
      )}
      {selectedSim && (
        <DetailModal title={selectedSim.displayName ?? "Attack Simulation"} subtitle={selectedSim.attackType ?? "Simulation"}
          onClose={()=>setSelectedSim(null)}
          portalUrl="https://security.microsoft.com/attacksimulator"
          portalLabel="View in Attack Simulator">
          <DetailField label="Simulation ID" value={selectedSim.id}/>
          <DetailField label="Attack Type" value={selectedSim.attackType?.replace(/([A-Z])/g," $1").trim()}/>
          <DetailField label="Status" value={selectedSim.status}/>
          <DetailField label="Users Targeted" value={String(selectedSim.numberOfUsersTargeted)}/>
          <DetailField label="Clicked Phishing" value={String(selectedSim.clickedPhishingLinkCount)}/>
          <DetailField label="Did Not Click" value={String(selectedSim.didNotClickLinkCount)}/>
          <DetailField label="Compromise Rate" value={typeof selectedSim?.compromisedRate==='number'?`${selectedSim.compromisedRate.toFixed(1)}%`:"—"}/>
          <DetailField label="Created" value={fmtDate(selectedSim.createdDateTime)}/>
          <DetailField label="Completed" value={fmtDate(selectedSim.completionDateTime)}/>
        </DetailModal>
      )}

      {selectedFw && (
        <DetailModal title={selectedFw.name} subtitle={`${selectedFw.desc} · Assessment`}
          onClose={()=>setSelectedFw(null)}
          portalUrl="https://compliance.microsoft.com/compliance-manager"
          portalLabel="Open Compliance Manager">
          <div style={{ display: "flex", gap: 16, marginBottom: 16, background: "var(--color-raised)", padding: 16, borderRadius: 8 }}>
            <div>
              <div style={{ fontSize: 11, color: "var(--color-muted)", textTransform: "uppercase" }}>Overall Posture</div>
              <div style={{ fontSize: 28, fontWeight: 700, color: selectedFw.score >= 80 ? "var(--status-good-text)" : selectedFw.score >= 50 ? "var(--status-warn-text)" : "var(--status-error-text)" }}>{selectedFw.score}%</div>
            </div>
            <div style={{ marginLeft: 24 }}>
              <div style={{ fontSize: 11, color: "var(--color-muted)", textTransform: "uppercase" }}>Controls Passed</div>
              <div style={{ fontSize: 28, fontWeight: 700, color: "var(--color-text)" }}>{selectedFw.passed} <span style={{ fontSize: 16, fontWeight: 400, color: "var(--color-muted)" }}>/ {selectedFw.total}</span></div>
            </div>
          </div>

          <div className="dm-section-hdr">Control Requirement Assessment</div>
          <div style={{ display: "flex", flexDirection: "column", gap: 10, marginTop: 8 }}>
            {selectedFw.controls.map((ctrl: any) => (
              <div key={ctrl.id} style={{ border: "1px solid var(--color-border)", borderRadius: 8, padding: 12, background: "var(--color-card)" }}>
                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", marginBottom: 6 }}>
                  <div>
                    <span style={{ fontFamily: "monospace", fontSize: 11, background: "var(--color-raised)", padding: "2px 6px", borderRadius: 4, marginRight: 6, color: "var(--color-muted)" }}>{ctrl.id}</span>
                    {(ctrl.cis||ctrl.iso||ctrl.gdpr)&&<span style={{ fontFamily: "monospace", fontSize: 11, background: "rgba(59,130,246,0.1)", border:"1px solid rgba(59,130,246,0.2)", padding: "1px 5px", borderRadius: 4, marginRight: 8, color: "#3b82f6" }}>{ctrl.cis||ctrl.iso||ctrl.gdpr}</span>}
                    <span style={{ fontWeight: 600, fontSize: 13, color: "var(--color-text)" }}>{ctrl.name}</span>
                  </div>
                  <Badge label={!ctrl.hasData ? "NOT ASSESSED" : ctrl.passed ? "PASS" : "ACTION REQUIRED"} tone={!ctrl.hasData ? "neutral" : ctrl.passed ? "good" : "error"}/>
                </div>
                <div style={{ fontSize: 12, color: "var(--color-muted)", marginBottom: 8 }}>Signal: <strong style={{ color: "var(--color-text)" }}>{ctrl.signal}</strong></div>
                {ctrl.hasData && !ctrl.passed && (
                  <div style={{ background: "var(--status-error-bg)", border: "1px solid var(--status-error-border)", borderRadius: 6, padding: "8px 10px", fontSize: 12, display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                    <span style={{ color: "var(--status-error-text)" }}>{ctrl.fix}</span>
                    <a href={ctrl.link} target="_blank" rel="noreferrer" style={{ display: "flex", alignItems: "center", gap: 4, fontSize: 11, fontWeight: 600, color: "#3b82f6", textDecoration: "none", background: "var(--color-card)", padding: "4px 8px", borderRadius: 4, border: "1px solid var(--color-border)" }}>
                      Fix in M365 <ExternalLink size={12}/>
                    </a>
                  </div>
                )}
              </div>
            ))}
          </div>
        </DetailModal>
      )}

      {showThresholdModal && (
        <DetailModal title="Configure Compliance Thresholds" subtitle="Tune control evaluation criteria for your tenant" onClose={() => setShowThresholdModal(false)}>
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600 }}>Target MFA Registration (%)</label>
              <input type="number" className="form-input" style={{ width: "100%", marginTop: 4 }} value={thresholds.mfaTarget} onChange={e => setThresholds({ ...thresholds, mfaTarget: Number(e.target.value) })} />
            </div>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600 }}>Max Privileged Users (Admins)</label>
              <input type="number" className="form-input" style={{ width: "100%", marginTop: 4 }} value={thresholds.maxAdmins} onChange={e => setThresholds({ ...thresholds, maxAdmins: Number(e.target.value) })} />
            </div>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600 }}>Min Conditional Access Policies</label>
              <input type="number" className="form-input" style={{ width: "100%", marginTop: 4 }} value={thresholds.minCaPolicies} onChange={e => setThresholds({ ...thresholds, minCaPolicies: Number(e.target.value) })} />
            </div>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600 }}>Max Failed Sign-ins (24h)</label>
              <input type="number" className="form-input" style={{ width: "100%", marginTop: 4 }} value={thresholds.maxFailedSignIns} onChange={e => setThresholds({ ...thresholds, maxFailedSignIns: Number(e.target.value) })} />
            </div>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600 }}>Max Phishing Compromise Rate (%)</label>
              <input type="number" className="form-input" style={{ width: "100%", marginTop: 4 }} value={thresholds.maxPhishRate} onChange={e => setThresholds({ ...thresholds, maxPhishRate: Number(e.target.value) })} />
            </div>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600 }}>Target Secure Score (%)</label>
              <input type="number" className="form-input" style={{ width: "100%", marginTop: 4 }} value={thresholds.minSecureScore} onChange={e => setThresholds({ ...thresholds, minSecureScore: Number(e.target.value) })} />
            </div>
            <button className="btn-apply" style={{ marginTop: 8 }} onClick={() => { localStorage.setItem("vigil365_compliance_thresholds", JSON.stringify(thresholds)); setShowThresholdModal(false); }}>
              Save Thresholds
            </button>
          </div>
        </DetailModal>
      )}

      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 16 }}>
        <div>
          <h2 style={{ fontSize: 18, fontWeight: 700, margin: 0, color: "var(--color-text)" }}>Enterprise Compliance Baseline</h2>
          <div style={{ fontSize: 12, color: "var(--color-muted)" }}>Pinned frameworks: NIST CSF 2.0 (2024), CIS Controls v8.1, ISO/IEC 27001:2022/Amd 1:2024, GDPR Art. 32 (2016/679)</div>
        </div>
        <button className="btn-export" onClick={() => setShowThresholdModal(true)} style={{ display: "flex", alignItems: "center", gap: 6 }}>
          Configure Thresholds
        </button>
      </div>

      <div className="kpi-row kpi-row-4">
        <KpiTile icon={<Shield size={18}/>} label="SECURE SCORE"
          value={secureScore?.configured&&!secureScore.error?`${secureScore.percentage}%`:"—"}
          sub="Microsoft 365 posture" tone={pctTone(secureScore?.percentage??0)}/>
        <KpiTile icon={<FileText size={18}/>} label="FRAMEWORKS" value={`${frameworks.filter(f=>!f.noData&&f.score>=80).length}/${frameworks.length}`}
          sub="Compliant standards" tone="info"/>
        <KpiTile icon={<FileText size={18}/>} label="DLP VIOLATIONS" value={dlpAlerts?.configured&&!dlpAlerts.error?dlpAlerts.total:"—"}
          sub={dlpAlerts?.error?"Needs SecurityAlert.Read.All":"Data loss prevention alerts"} needsPerm={!!dlpAlerts?.error}
          tone={(dlpAlerts?.total??0)===0?"good":"warning"}/>
        {(() => {
          const assessed = frameworks.filter(f => !f.noData);
          const avg = assessed.length ? Math.round(assessed.reduce((a,b)=>a+b.score,0)/assessed.length) : 0;
          const totalMeasured = allControls.filter(c => c.hasData).length;
          return (
            <KpiTile icon={<Star size={18}/>} label="AVG FRAMEWORK SCORE" value={assessed.length ? `${avg}%` : "—"}
              sub={`${totalMeasured} of ${allControls.length} controls assessed`}
              tone={assessed.length ? pctTone(avg) : "neutral"}/>
          );
        })()}
      </div>

      <div className="two-col">
        <Card title="Security Control Areas">
          <div className="controls-list">
            {controls.map((c,i)=>(
              <div key={i} className="control-item">
                <div className="control-head">
                  <span className="control-name">{c.area}</span>
                  <span className="control-score" style={{color:c.score>=80?"var(--status-good-text)":c.score>=60?"var(--status-warn-text)":"var(--status-error-text)"}}>{c.score}%</span>
                </div>
                <ProgressBar pct={c.score}/>
                <div className="control-items">
                  {c.items.map((item,j)=>(
                    <span key={j} className="control-tag">{item}</span>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </Card>

        <Card title="Compliance Frameworks (Click to Drill Down)">
          <div className="frameworks-list">
            {frameworks.map((f,i)=>(
              <div key={i} className="framework-item al-clickable" onClick={()=>setSelectedFw(f)} style={{ cursor: "pointer", padding: "10px 12px", borderRadius: 6, transition: "background 0.15s", border: "1px solid var(--color-border-subtle)", marginBottom: 8 }}>
                <div className="fw-head" style={{ display: "flex", justifyContent: "space-between", marginBottom: 6 }}>
                  <div>
                    <div className="fw-name" style={{ fontWeight: 600, fontSize: 14, color: "var(--color-text)" }}>{f.name}</div>
                    <div style={{ fontSize: 11, color: "var(--color-muted)" }}>{f.desc}</div>
                  </div>
                  <Badge label={f.noData ? "No data" : f.status} tone={f.noData ? "neutral" : f.score>=80?"good":f.score>=50?"warning":"error"}/>
                </div>
                <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  <div style={{ flex: 1 }}><ProgressBar pct={f.noData ? 0 : f.score}/></div>
                  <span style={{ fontSize: 13, fontWeight: 700, minWidth: 38, textAlign: "right", color: f.noData ? "var(--color-muted)" : f.score>=80?"var(--status-good-text)":f.score>=50?"var(--status-warn-text)":"var(--status-error-text)" }}>{f.noData ? "N/A" : `${f.score}%`}</span>
                </div>
                {!f.noData && <div style={{ fontSize: 10.5, color: "var(--color-faint)", marginTop: 4 }}>{f.measured} of {f.total} controls assessed</div>}
              </div>
            ))}
          </div>
        </Card>
      </div>

      <div className="sticky-filter-bar filters-bar">
        <label className="search-box">
          <Search size={15}/>
          <input value={alertSearch} onChange={e=>setAlertSearch(e.target.value)}
            placeholder="Search DLP, MCAS, IRM alerts…" className="search-input"/>
        </label>
        <select value={alertSev} onChange={e=>setAlertSev(e.target.value)} className="filter-sel">
          <option value="">All severities</option>
          <option value="high">High</option>
          <option value="medium">Medium</option>
          <option value="low">Low</option>
          <option value="informational">Informational</option>
        </select>
        <ExportDropdown rows={[
          ...filteredDlp.map(a=>({Source:"DLP",Title:a.title??"",Severity:a.severity,Status:a.status,Detected:a.createdDateTime??""})),
          ...filteredMcas.map(a=>({Source:"MCAS",Title:a.title??"",Severity:a.severity,Status:a.status,Detected:a.createdDateTime??""})),
          ...filteredIrm.map(a=>({Source:"IRM",Title:a.title??"",Severity:a.severity,Status:a.status,Detected:a.createdDateTime??""}))
        ]} filename="compliance-alerts.csv"/>
        {hasAlertFilter&&<button className="btn-apply" onClick={()=>{setAlertSearch("");setAlertSev("");}}>Clear</button>}
        <FilterPresets pageKey="compliance" filters={{alertSearch,alertSev}}
          onLoad={f=>{setAlertSearch(f.alertSearch??"");setAlertSev(f.alertSev??"");}}/>
      </div>

      <div className="two-col">
        <Card title="DLP Alerts" badge={<Badge label={`${filteredDlp.length} / ${dlpAlerts?.total??0} violations`} tone={(dlpAlerts?.total??0)>0?"error":"good"}/>}>
          {dlpAlerts?.error
            ?<EmptyState icon={<ShieldAlert size={28}/>} message="Needs SecurityAlert.Read.All"/>
            :(dlpAlerts?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28}/>} message="No DLP violations detected"/>
              :(
                <div className="alert-list">
                  {filteredDlp.length===0&&<div className="td-empty" style={{padding:12}}>No violations match the filter.</div>}
                  {filteredDlp.slice(0,8).map((a,i)=>(
                    <div key={a.id??i} className="al-item" onClick={()=>setSelectedDlp(a)}>
                      <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                      <div className="al-body">
                        <div className="al-title">{a.title}</div>
                        <div className="row-meta">
                          <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                          {a.category&&<span className="row-meta-item">{a.category}</span>}
                          <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                        </div>
                      </div>
                      <Badge label={a.severity} tone={sevTone(a.severity)}/>
                    </div>
                  ))}
                </div>
              )
          }
        </Card>

        <Card title="Sensitivity Labels" badge={<Badge label={`${purview?.labelCount??0} labels`} tone={(purview?.labelCount??0)>0?"info":"neutral"}/>}>
          {purview?.error
            ?<EmptyState icon={<FileText size={28}/>} message="Needs InformationProtectionPolicy.Read permission"/>
            :(purview?.labelCount??0)===0
              ?<EmptyState icon={<FileText size={28}/>} message="No sensitivity labels configured"/>
              :(
                <div className="mini-list">
                  {purview!.labels.map((l,i)=>(
                    <div key={l.id??i} className="mini-row">
                      <span className="status-dot" style={{background:l.color||"#94a3b8"}}/>
                      <span className="mr-user">{l.name}</span>
                      {l.description&&<span className="al-desc" style={{flex:1}}>{l.description}</span>}
                      {l.isActive&&<Badge label="Active" tone="good"/>}
                    </div>
                  ))}
                </div>
              )
          }
        </Card>
      </div>

      <div className="two-col">
        <Card title="Cloud App Anomalies" badge={<Badge label={`${filteredMcas.length} / ${mcasAlerts?.total??0} alerts`} tone={(mcasAlerts?.total??0)>0?"error":"good"}/>}>
          {mcasAlerts?.error
            ?<EmptyState icon={<ShieldAlert size={28}/>} message="Needs SecurityAlert.Read.All"/>
            :(mcasAlerts?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28}/>} message="No Cloud App anomalies — no impossible travel, mass downloads, or suspicious OAuth detected"/>
              :(
                <>
                  <div className="stat-row4" style={{marginBottom:14}}>
                    <StatBox value={mcasAlerts!.bySeverity?.["high"]??0} label="High" color={(mcasAlerts!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={mcasAlerts!.bySeverity?.["medium"]??0} label="Medium" color={(mcasAlerts!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={mcasAlerts!.bySeverity?.["low"]??0} label="Low"/>
                    <StatBox value={mcasAlerts!.bySeverity?.["informational"]??0} label="Info"/>
                  </div>
                  <div className="alert-list">
                    <SectHdr>CLOUD APP ALERTS — {filteredMcas.length} shown</SectHdr>
                    {filteredMcas.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                    {filteredMcas.slice(0,8).map((a,i)=>(
                      <div key={a.id??i} className="al-item" onClick={()=>setSelectedMcas(a)}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{a.title}</div>
                          <div className="row-meta">
                            {a.category&&<span className="row-meta-item">{a.category}</span>}
                            <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                            <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={a.severity} tone={sevTone(a.severity)}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>

        <Card title="Insider Risk Management" badge={<Badge label={`${filteredIrm.length} / ${insiderRisk?.total??0} alerts`} tone={(insiderRisk?.total??0)>0?"error":"good"}/>}>
          {insiderRisk?.error
            ?<EmptyState icon={<ShieldAlert size={28}/>} message="Needs SecurityAlert.Read.All — IRM alerts available if Purview IRM is licensed"/>
            :(insiderRisk?.total??0)===0
              ?<EmptyState icon={<ShieldCheck size={28}/>} message="No Insider Risk alerts — no data exfiltration or policy violations detected"/>
              :(
                <>
                  <div className="stat-row3" style={{marginBottom:14}}>
                    <StatBox value={insiderRisk!.bySeverity?.["high"]??0} label="High" color={(insiderRisk!.bySeverity?.["high"]??0)>0?"var(--status-error-text)":undefined}/>
                    <StatBox value={insiderRisk!.bySeverity?.["medium"]??0} label="Medium" color={(insiderRisk!.bySeverity?.["medium"]??0)>0?"var(--status-warn-text)":undefined}/>
                    <StatBox value={insiderRisk!.bySeverity?.["low"]??0} label="Low"/>
                  </div>
                  <div className="alert-list">
                    <SectHdr>IRM ALERTS — {filteredIrm.length} shown</SectHdr>
                    {filteredIrm.length===0&&<div className="td-empty" style={{padding:12}}>No alerts match the filter.</div>}
                    {filteredIrm.slice(0,8).map((a,i)=>(
                      <div key={a.id??i} className="al-item" onClick={()=>setSelectedIrm(a)}>
                        <span className={`sev-dot sev-${a.severity.toLowerCase()}`}/>
                        <div className="al-body">
                          <div className="al-title">{a.title}</div>
                          <div className="row-meta">
                            {a.category&&<span className="row-meta-item">{a.category}</span>}
                            <Badge label={a.status} tone={a.status==="resolved"?"good":"warning"}/>
                            <span className="row-meta-item">{relTime(a.createdDateTime)}</span>
                          </div>
                        </div>
                        <Badge label={a.severity} tone={sevTone(a.severity)}/>
                      </div>
                    ))}
                  </div>
                </>
              )
          }
        </Card>
      </div>

      <Card title="Attack Simulation & Training" badge={<Badge label={`${attackSimulation?.total??0} simulations`} tone="neutral"/>}>
        {attackSimulation?.error
          ?<EmptyState icon={<ShieldAlert size={28}/>} message="Needs AttackSimulation.ReadWrite.All — add permission in Azure App Registration"/>
          :(attackSimulation?.total??0)===0
            ?<EmptyState icon={<ShieldCheck size={28}/>} message="No attack simulations configured — consider running phishing tests to measure user resilience"/>
            :(
              <>
                <div className="stat-row3" style={{marginBottom:14}}>
                  <StatBox value={attackSimulation!.total} label="Simulations Run"/>
                  <StatBox value={attackSimulation!.totalTargeted} label="Users Targeted"/>
                  <StatBox value={`${attackSimulation!.avgCompromiseRate}%`} label="Avg Compromise Rate"
                    color={attackSimulation!.avgCompromiseRate>30?"var(--status-error-text)":attackSimulation!.avgCompromiseRate>10?"var(--status-warn-text)":"var(--status-good-text)"}/>
                </div>
                <div className="tbl-wrap">
                  <table className="data-tbl">
                    <thead><tr><th scope="col">Simulation</th><th scope="col">Type</th><th scope="col">Targeted</th><th scope="col">Clicked</th><th scope="col">Compromise Rate</th><th scope="col">Status</th></tr></thead>
                    <tbody>
                      {attackSimulation!.simulations.slice(0,8).map((s,i)=>(
                        <tr key={s.id??i} className="tbl-row-click" onClick={()=>setSelectedSim(s)}>
                          <td><div className="al-title">{s.displayName??"Unnamed simulation"}</div></td>
                          <td className="al-desc">{s.attackType?.replace(/([A-Z])/g," $1").trim()??s.attackType??"—"}</td>
                          <td>{s.numberOfUsersTargeted}</td>
                          <td style={{color:s.clickedPhishingLinkCount>0?"var(--status-error-text)":"var(--status-good-text)",fontWeight:600}}>{s.clickedPhishingLinkCount}</td>
                          <td>
                            <span style={{color:(s.compromisedRate??0)>30?"var(--status-error-text)":(s.compromisedRate??0)>10?"var(--status-warn-text)":"var(--status-good-text)",fontWeight:600}}>
                              {typeof s.compromisedRate==='number'?`${s.compromisedRate.toFixed(1)}%`:"—"}
                            </span>
                          </td>
                          <td><Badge label={s.status} tone={s.status==="completed"?"good":s.status==="running"?"info":"neutral"}/></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )
        }
      </Card>
    </div>
  );
}
