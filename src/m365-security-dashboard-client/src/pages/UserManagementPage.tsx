import React, { useState, useCallback, useEffect } from "react";
import { CheckCircle, AlertCircle } from "lucide-react";
import { AppRole, Tone } from "../services/types";
import { apiBase, apiFetch, useAuth } from "../services/api";
import { showToast } from "../services/toast";
import { confirmAction } from "../services/confirm";
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

    // Demoting yourself takes effect immediately and removes the admin UI you
    // would need to undo it. The server already blocks removing the LAST admin,
    // but with other admins present this was a single mis-click on your own row.
    const demotingSelf = u.email === myEmail && u.role === "Admin" && role !== "Admin";
    if (demotingSelf) {
      const ok = await confirmAction({
        title: "Remove your own Admin access?",
        message: `You are about to change your own role to ${role}. Admin controls — user management, API tokens, notification settings — will disappear immediately, and you will need another Admin to restore them.`,
        confirmLabel: `Yes, make me ${role}`,
        danger: true,
      });
      if (!ok) { await load(); return; } // reload to reset the select
    }

    setBusy(u.email);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users/${encodeURIComponent(u.email)}/role`, {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ role }),
      });
      if (r.ok) { showToast(`${u.email} is now ${role}`); await load(); }
      else { const e = await r.json().catch(() => ({})); showToast(e.error ?? "Could not change role", "error"); }
    } finally { setBusy(null); }
  };

  const removeUser = async (u: ManagedUser) => {
    const ok = await confirmAction({
      title: `Remove ${u.email}?`,
      message: "They will immediately lose access to Vigil365. Their Microsoft 365 account is not affected, and the audit trail of their past actions is kept.",
      confirmLabel: "Remove access",
      danger: true,
    });
    if (!ok) return;
    setBusy(u.email);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users/${encodeURIComponent(u.email)}`, { method: "DELETE" });
      if (r.ok) { showToast(`Removed ${u.email}`); await load(); }
      else { const e = await r.json().catch(() => ({})); showToast(e.error ?? "Could not remove user", "error"); }
    } finally { setBusy(null); }
  };

  const addUser = async () => {
    const email = addEmail.trim().toLowerCase();
    if (!email || !email.includes("@")) { showToast("Enter a valid email address", "error"); return; }
    setAdding(true);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, role: addRole, displayName: addName.trim() || null, sendInvite: addInvite }),
      });
      if (r.ok) {
        const d = await r.json().catch(() => ({}));
        if (addInvite && d.inviteError) showToast(`Added ${email}, but email failed: ${d.inviteError}`, "error");
        else if (addInvite && d.inviteSent) showToast(`Added ${email} as ${addRole} — invite sent`);
        else showToast(`Added ${email} as ${addRole}`);
        setAddEmail(""); setAddName(""); setAddRole("Viewer"); setAddInvite(false); setShowAdd(false);
        await load();
      } else {
        const e = await r.json().catch(() => ({}));
        showToast(e.error ?? "Could not add user", "error");
      }
    } finally { setAdding(false); }
  };

  const sendInvite = async (u: ManagedUser) => {
    setBusy(u.email);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/users/${encodeURIComponent(u.email)}/invite`, { method: "POST" });
      if (r.ok) showToast(`Invite email sent to ${u.email}`);
      else { const e = await r.json().catch(() => ({})); showToast(e.error ?? "Could not send invite", "error"); }
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
      if (!r.ok) { showToast("Export failed", "error"); return; }
      const blob = await r.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `vigil365-audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
      showToast("Audit log exported");
    } catch { showToast("Export failed", "error"); }
    finally { setExporting(false); }
  };

  const verifyChain = async () => {
    setVerifying(true);
    try {
      const r = await apiFetch(`${apiBase}/api/admin/audit-log/verify`);
      if (!r.ok) { showToast("Verification request failed", "error"); return; }
      const d: VerifyResult = await r.json();
      setVerify(d);
      // Severity must match meaning: a broken chain is the single most serious
      // signal this product emits — it must never render as a green success toast.
      showToast(
        d.valid
          ? `Chain intact — ${d.verified} entries verified`
          : `Tampering detected at entry #${d.firstBrokenId}`,
        d.valid ? "success" : "error");
    } catch { showToast("Verification request failed", "error"); }
    finally { setVerifying(false); }
  };

  return (
    <div className="page">
      <Card title={`Users${users ? ` (${users.length})` : ""}`}
        badge={<Badge label="Admin only" tone="info"/>}
        action={<button className="btn-export" onClick={() => setShowAdd(s => !s)}>{showAdd ? "Cancel" : "Add User"}</button>}>
        <div data-inline-style="inline-03fbcc5593">
          Assign roles to people who have signed in. Roles are stored in Vigil365 — no Entra ID changes needed.
          <strong> Admin</strong> = full access · <strong>Analyst</strong> = acknowledge/snooze/resolve · <strong>Viewer</strong> = read-only.
        </div>
        {showAdd && (
          <div data-inline-style="inline-8b64f26f16">
            <input className="form-input" type="email" placeholder="user@domain.com"
              value={addEmail} onChange={e => setAddEmail(e.target.value)} data-inline-style="inline-b33e69fdee" />
            <input className="form-input" type="text" placeholder="Display name (optional)"
              value={addName} onChange={e => setAddName(e.target.value)} data-inline-style="inline-01a3bb680d" />
            <select className="filter-sel" value={addRole} onChange={e => setAddRole(e.target.value as AppRole)}>
              <option value="Admin">Admin</option>
              <option value="Analyst">Analyst</option>
              <option value="Viewer">Viewer</option>
            </select>
            <label data-inline-style="inline-d413cffa31" title="Sends a sign-in link via the SMTP server configured in Settings">
              <input type="checkbox" checked={addInvite} onChange={e => setAddInvite(e.target.checked)} />
              Send invite email
            </label>
            <button className="btn-apply" disabled={adding} onClick={addUser}>{adding ? "Adding…" : "Add"}</button>
            <button className="btn-export" onClick={() => setShowAdd(false)}>Cancel</button>
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
                  <tr><th scope="col">User</th><th scope="col">Email</th><th scope="col">Role</th><th scope="col">Last seen</th><th scope="col">Actions</th></tr>
                </thead>
                <tbody>
                  {users.map(u => (
                    <tr key={u.email}>
                      <td data-inline-style="inline-3d9df89ef8">
                        {u.displayName || "—"}
                        {u.email === myEmail && <span data-inline-style="inline-4ac97f251f">(you)</span>}
                      </td>
                      <td className="al-date">{u.email}</td>
                      <td><Badge label={u.role} tone={roleTone(u.role)}/></td>
                      <td className="al-date" title={new Date(u.lastSeenAt).getFullYear() <= 1 ? "Never signed in" : fmtDate(u.lastSeenAt)}>{new Date(u.lastSeenAt).getFullYear() <= 1 ? "Never" : (relTime(u.lastSeenAt) || fmtDate(u.lastSeenAt))}</td>
                      <td data-inline-style="inline-a0c5370730">
                        <select
                           className="filter-sel" data-inline-style="inline-f236f28555"
                          value={u.role} disabled={busy===u.email}
                          onChange={e => changeRole(u, e.target.value as AppRole)}
                        >
                          <option value="Admin">Admin</option>
                          <option value="Analyst">Analyst</option>
                          <option value="Viewer">Viewer</option>
                        </select>
                        <button className="btn-export" data-inline-style="inline-6ba117109b"
                          disabled={busy===u.email} onClick={() => sendInvite(u)}
                          title="Email this user a sign-in link (requires SMTP configured in Settings)">
                          {new Date(u.lastSeenAt).getFullYear() <= 1 ? "Send invite" : "Resend invite"}
                        </button>
                        {u.email !== myEmail && (
                          <button className="btn-danger" data-inline-style="inline-6ba117109b"
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
          <div data-inline-style="inline-3633e433a1">
            <button className="btn-apply" data-inline-style="inline-84a31235d6"
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
        <div data-inline-style="inline-03fbcc5593">
          Append-only audit trail of security-relevant actions (sign-ins, user and role changes, settings).
          Entries are hash-chained — each row's hash covers the previous row's, so any tampering is detectable.
          {verify && !verify.valid && (
            <span data-inline-style="inline-7d860f012f">
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
                  <tr><th scope="col">When</th><th scope="col">Actor</th><th scope="col">Action</th><th scope="col">Target</th><th scope="col">Details</th><th scope="col">IP</th></tr>
                </thead>
                <tbody>
                  {audit.map(a => (
                    <tr key={a.id}>
                      <td className="al-date" title={fmtDate(a.timestamp)}>{relTime(a.timestamp) || fmtDate(a.timestamp)}</td>
                      <td className="al-date">{a.actorEmail}</td>
                      <td><Badge label={a.action} tone="neutral"/></td>
                      <td className="al-date">{a.targetId || a.targetType}</td>
                      <td data-inline-style="inline-af7da65b76">{a.details}</td>
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
