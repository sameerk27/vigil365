import React, { useState, useCallback, useEffect } from "react";
import { CheckCircle, AlertCircle } from "lucide-react";
import { AppRole, Tone } from "../services/types";
import { apiBase, apiFetch, useAuth } from "../services/api";
import { showToast } from "../services/toast";
import { Card, Badge, EmptyState, LoadingSkeleton } from "../components/SharedComponents";
import { relTime, fmtDate } from "../services/utils";

export interface ManagedUser {
  email: string;
  displayName?: string;
  role: AppRole;
  createdAt: string;
  lastSeenAt: string;
}

export interface AuditRow {
  id: number;
  timestamp: string;
  actorEmail: string;
  action: string;
  targetType: string;
  targetId?: string;
  details?: string;
  ipAddress?: string;
  userAgent?: string;
  entryHash?: string;
}

interface VerifyResult {
  valid: boolean;
  total: number;
  verified: number;
  legacyUnhashed: number;
  firstBrokenId?: number;
}

export function UserManagementPage() {
  const { email: myEmail } = useAuth();
  const [users, setUsers] = useState<ManagedUser[] | null>(null);
  const [audit, setAudit] = useState<AuditRow[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  const [addEmail, setAddEmail] = useState("");
  const [addRole, setAddRole] = useState<AppRole>("Viewer");
  const [addName, setAddName] = useState("");
  const [addInvite, setAddInvite] = useState(false);
  const [adding, setAdding] = useState(false);

  const load = useCallback(async () => {
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users`);
      if (r.ok) setUsers(await r.json()); else setUsers([]);
    } catch { setUsers([]); }
    try {
      const a = await apiFetch(`${apiBase}/api/admin/audit-log`);
      if (a.ok) setAudit(await a.json()); else setAudit([]);
    } catch { setAudit([]); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const changeRole = async (u: ManagedUser, role: AppRole) => {
    if (role === u.role) return;
    setBusy(u.email);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users/${encodeURIComponent(u.email)}/role`, {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ role }),
      });
      if (r.ok) { showToast(`${u.email} is now ${role}`); await load(); }
      else { const e = await r.json().catch(() => ({})); showToast(e.error ?? "Could not change role"); }
    } finally { setBusy(null); }
  };

  const removeUser = async (u: ManagedUser) => {
    setBusy(u.email);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users/${encodeURIComponent(u.email)}`, { method: "DELETE" });
      if (r.ok) { showToast(`Removed ${u.email}`); await load(); }
      else { const e = await r.json().catch(() => ({})); showToast(e.error ?? "Could not remove user"); }
    } finally { setBusy(null); }
  };

  const addUser = async () => {
    const email = addEmail.trim().toLowerCase();
    if (!email || !email.includes("@")) { showToast("Enter a valid email address"); return; }
    setAdding(true);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, role: addRole, displayName: addName.trim() || null, sendInvite: addInvite }),
      });
      if (r.ok) {
        const d = await r.json().catch(() => ({}));
        if (addInvite && d.inviteError) showToast(`Added ${email}, but email failed: ${d.inviteError}`);
        else if (addInvite && d.inviteSent) showToast(`Added ${email} as ${addRole} — invite sent`);
        else showToast(`Added ${email} as ${addRole}`);
        setAddEmail(""); setAddName(""); setAddRole("Viewer"); setAddInvite(false); setShowAdd(false);
        await load();
      } else {
        const e = await r.json().catch(() => ({}));
        showToast(e.error ?? "Could not add user");
      }
    } finally { setAdding(false); }
  };

  const sendInvite = async (u: ManagedUser) => {
    setBusy(u.email);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users/${encodeURIComponent(u.email)}/invite`, { method: "POST" });
      if (r.ok) showToast(`Invite email sent to ${u.email}`);
      else { const e = await r.json().catch(() => ({})); showToast(e.error ?? "Could not send invite"); }
    } finally { setBusy(null); }
  };

  const roleTone = (r: string): Tone => r === "Admin" ? "info" : r === "Analyst" ? "good" : "neutral";

  const [verify, setVerify] = useState<VerifyResult | null>(null);
  const [verifying, setVerifying] = useState(false);
  const [exporting, setExporting] = useState(false);

  const exportAudit = async () => {
    setExporting(true);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/audit-log/export`);
      if (!r.ok) { showToast("Export failed"); return; }
      const blob = await r.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `vigil365-audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
      showToast("Audit log exported");
    } catch { showToast("Export failed"); }
    finally { setExporting(false); }
  };

  const verifyChain = async () => {
    setVerifying(true);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/audit-log/verify`);
      if (!r.ok) { showToast("Verification request failed"); return; }
      const d: VerifyResult = await r.json();
      setVerify(d);
      showToast(d.valid
        ? `Chain intact — ${d.verified} entries verified`
        : `Tampering detected at entry #${d.firstBrokenId}`);
    } catch { showToast("Verification request failed"); }
    finally { setVerifying(false); }
  };

  return (
    <div className="page">
      <Card title={`Users${users ? ` (${users.length})` : ""}`}
        badge={<Badge label="Admin only" tone="info"/>}
        action={<button className="btn-export" onClick={() => setShowAdd(s => !s)}>{showAdd ? "Cancel" : "Add User"}</button>}>
        <div style={{ fontSize:12, color:"var(--color-muted)", padding:"0 0 12px", lineHeight:1.6 }}>
          Assign roles to people who have signed in. Roles are stored in Vigil365 — no Entra ID changes needed.
          <strong> Admin</strong> = full access · <strong>Analyst</strong> = acknowledge/snooze/resolve · <strong>Viewer</strong> = read-only.
        </div>
        {showAdd && (
          <div style={{ display:"flex", gap:8, alignItems:"center", flexWrap:"wrap", padding:"0 0 16px" }}>
            <input className="form-input" type="email" placeholder="user@domain.com"
              value={addEmail} onChange={e => setAddEmail(e.target.value)} style={{ minWidth:220 }} />
            <input className="form-input" type="text" placeholder="Display name (optional)"
              value={addName} onChange={e => setAddName(e.target.value)} style={{ minWidth:180 }} />
            <select className="filter-sel" value={addRole} onChange={e => setAddRole(e.target.value as AppRole)}>
              <option value="Admin">Admin</option>
              <option value="Analyst">Analyst</option>
              <option value="Viewer">Viewer</option>
            </select>
            <label style={{ display:"flex", alignItems:"center", gap:5, fontSize:12, color:"var(--color-text)", cursor:"pointer" }} title="Sends a sign-in link via the SMTP server configured in Settings">
              <input type="checkbox" checked={addInvite} onChange={e => setAddInvite(e.target.checked)} />
              Send invite email
            </label>
            <button className="btn-export" disabled={adding} onClick={addUser}>{adding ? "Adding…" : "Add"}</button>
            <button className="btn-apply" style={{ padding:"5px 10px", fontSize:12 }} onClick={() => setShowAdd(false)}>Cancel</button>
          </div>
        )}
        {users === null
          ? <LoadingSkeleton type="table"/>
          : users.length === 0
          ? <EmptyState message="No users yet."/>
          : (
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr><th>User</th><th>Email</th><th>Role</th><th>Last seen</th><th>Actions</th></tr>
                </thead>
                <tbody>
                  {users.map(u => (
                    <tr key={u.email}>
                      <td style={{ fontWeight:600 }}>
                        {u.displayName || "—"}
                        {u.email === myEmail && <span style={{ fontSize:10, color:"var(--color-muted)", marginLeft:6 }}>(you)</span>}
                      </td>
                      <td className="al-date">{u.email}</td>
                      <td><Badge label={u.role} tone={roleTone(u.role)}/></td>
                      <td className="al-date" title={new Date(u.lastSeenAt).getFullYear() <= 1 ? "Never signed in" : fmtDate(u.lastSeenAt)}>{new Date(u.lastSeenAt).getFullYear() <= 1 ? "Never" : (relTime(u.lastSeenAt) || fmtDate(u.lastSeenAt))}</td>
                      <td style={{ display:"flex", gap:6, alignItems:"center", flexWrap:"wrap" }}>
                        <select
                           className="filter-sel" style={{ padding:"3px 6px", fontSize:12 }}
                          value={u.role} disabled={busy===u.email}
                          onChange={e => changeRole(u, e.target.value as AppRole)}
                        >
                          <option value="Admin">Admin</option>
                          <option value="Analyst">Analyst</option>
                          <option value="Viewer">Viewer</option>
                        </select>
                        <button className="btn-apply" style={{ padding:"3px 8px", fontSize:11 }}
                          disabled={busy===u.email} onClick={() => sendInvite(u)}
                          title="Email this user a sign-in link (requires SMTP configured in Settings)">
                          {new Date(u.lastSeenAt).getFullYear() <= 1 ? "Send invite" : "Resend invite"}
                        </button>
                        {u.email !== myEmail && (
                          <button className="btn-resolve" style={{ padding:"3px 8px", fontSize:11 }}
                            disabled={busy===u.email} onClick={() => removeUser(u)} title="Remove user">Remove</button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      </Card>

      <Card title="Activity Log"
        badge={verify
          ? <Badge label={verify.valid ? "Chain verified" : "TAMPERED"} tone={verify.valid ? "good" : "error"}/>
          : <Badge label={audit ? `${audit.length} events` : "—"} tone="neutral"/>}
        action={
          <div style={{ display:"flex", gap:6 }}>
            <button className="btn-apply" style={{ padding:"5px 10px", fontSize:12 }}
              disabled={verifying} onClick={verifyChain}
              title="Recompute the SHA-256 hash chain over every entry to prove the log hasn't been edited or truncated">
              {verifying ? "Verifying…" : "Verify integrity"}
            </button>
            <button className="btn-export" disabled={exporting} onClick={exportAudit}
              title="Download the full audit trail as CSV (the export itself is audited)">
              {exporting ? "Exporting…" : "Export CSV"}
            </button>
          </div>
        }>
        <div style={{ fontSize:12, color:"var(--color-muted)", padding:"0 0 12px", lineHeight:1.6 }}>
          Append-only audit trail of security-relevant actions (sign-ins, user and role changes, settings).
          Entries are hash-chained — each row's hash covers the previous row's, so any tampering is detectable.
          {verify && !verify.valid && (
            <span style={{ color:"var(--sev-critical-text)", fontWeight:600 }}>
              {" "}Integrity check failed at entry #{verify.firstBrokenId}. Investigate immediately.
            </span>
          )}
          {verify && verify.valid && verify.legacyUnhashed > 0 && (
            <span> Verified {verify.verified} hashed entries ({verify.legacyUnhashed} predate hashing).</span>
          )}
        </div>
        {audit === null
          ? <LoadingSkeleton type="table"/>
          : audit.length === 0
          ? <EmptyState message="No activity recorded yet."/>
          : (
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr><th>When</th><th>Actor</th><th>Action</th><th>Target</th><th>Details</th><th>IP</th></tr>
                </thead>
                <tbody>
                  {audit.map(a => (
                    <tr key={a.id}>
                      <td className="al-date" title={fmtDate(a.timestamp)}>{relTime(a.timestamp) || fmtDate(a.timestamp)}</td>
                      <td className="al-date">{a.actorEmail}</td>
                      <td><Badge label={a.action} tone="neutral"/></td>
                      <td className="al-date">{a.targetId || a.targetType}</td>
                      <td style={{ fontSize:12, color:"var(--color-muted)" }}>{a.details}</td>
                      <td className="al-date" title={a.userAgent || undefined}>{a.ipAddress || "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      </Card>
    </div>
  );
}
