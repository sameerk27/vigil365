import React, { useRef, useState } from "react";
import { Download, Upload } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { showToast } from "../services/toast";
import { confirmAction } from "../services/confirm";

type ImportResult = {
  importedCount: number;
  updatedCount: number;
  skippedCount: number;
  rejectedCount: number;
  imported: string[];
  updated: string[];
  skipped: string[];
  rejected: { name: string; error: string }[];
};

/**
 * Export/import of alert policies as a portable JSON pack — share a tuned set
 * between installs, keep it in version control, or restore after a rebuild.
 *
 * Export strips notification recipients by default (packs get shared and those
 * are internal addresses); the user is asked explicitly if they want a backup
 * that includes them.
 */
export function PolicyPackControls({ onChanged }: { onChanged: () => void | Promise<void> }) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ImportResult | null>(null);

  const exportPack = async () => {
    const includeRecipients = await confirmAction({
      title: "Include notification recipients?",
      message: "Policy packs are often shared or committed to a repository. Notification email addresses are stripped by default. Include them only if this is a backup for your own organisation.",
      confirmLabel: "Include recipients",
      cancelLabel: "Strip recipients",
    });
    setBusy(true);
    try {
      const r = await apiFetch(`${apiBase}/api/alert-policies/export?includeRecipients=${includeRecipients}`);
      if (!r.ok) { showToast("Could not export policies", "error"); return; }
      const pack = await r.json();
      const blob = new Blob([JSON.stringify(pack, null, 2)], { type: "application/json" });
      const a = Object.assign(document.createElement("a"), {
        href: URL.createObjectURL(blob),
        download: `vigil365-policies-${new Date().toISOString().slice(0, 10)}.json`,
      });
      a.click();
      URL.revokeObjectURL(a.href);
      showToast(`Exported ${pack.policies?.length ?? 0} policies`);
    } catch {
      showToast("Could not export policies", "error");
    } finally { setBusy(false); }
  };

  const onFilePicked = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    // Reset immediately so re-picking the same file still fires a change event.
    e.target.value = "";
    if (!file) return;

    let pack: unknown;
    try {
      pack = JSON.parse(await file.text());
    } catch {
      showToast("That file is not valid JSON", "error");
      return;
    }

    const count = Array.isArray((pack as { policies?: unknown[] })?.policies)
      ? (pack as { policies: unknown[] }).policies.length : 0;
    if (count === 0) { showToast("No policies found in that file", "error"); return; }

    const update = await confirmAction({
      title: `Import ${count} polic${count === 1 ? "y" : "ies"}?`,
      message: "Policies are matched by name. Choose whether policies that already exist here should be overwritten with the pack's settings, or left exactly as they are.",
      confirmLabel: "Overwrite existing",
      cancelLabel: "Keep existing",
    });

    setBusy(true); setResult(null);
    try {
      const r = await apiFetch(`${apiBase}/api/alert-policies/import?mode=${update ? "update" : "skip"}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(pack),
      });
      if (!r.ok) {
        const body = await r.json().catch(() => ({}));
        showToast(body.error ?? "Import failed", "error");
        return;
      }
      const res: ImportResult = await r.json();
      setResult(res);
      const changed = res.importedCount + res.updatedCount;
      showToast(
        changed > 0
          ? `Imported ${res.importedCount}, updated ${res.updatedCount}`
          : "No policies changed",
        res.rejectedCount > 0 ? "error" : "success");
      if (changed > 0) await onChanged();
    } catch {
      showToast("Import failed", "error");
    } finally { setBusy(false); }
  };

  return (
    <>
      <button type="button" className="btn-export" disabled={busy} onClick={exportPack}>
        <Download size={13}/> Export
      </button>
      <button type="button" className="btn-export" disabled={busy} onClick={() => fileRef.current?.click()}>
        <Upload size={13}/> Import
      </button>
      <input ref={fileRef} type="file" accept="application/json,.json"
        style={{ display: "none" }} onChange={onFilePicked}/>

      {result && (
        <div className="pack-result">
          <div className="pack-result-head">
            <span>
              Imported <strong>{result.importedCount}</strong> ·
              Updated <strong>{result.updatedCount}</strong> ·
              Skipped <strong>{result.skippedCount}</strong>
              {result.rejectedCount > 0 && <> · <strong className="pack-rejected">Rejected {result.rejectedCount}</strong></>}
            </span>
            <button type="button" onClick={() => setResult(null)} aria-label="Dismiss import summary">Dismiss</button>
          </div>
          {result.rejected.length > 0 && (
            <ul className="pack-rejected-list">
              {result.rejected.map((r, i) => <li key={i}><strong>{r.name}</strong>: {r.error}</li>)}
            </ul>
          )}
          {result.skipped.length > 0 && (
            <div className="pack-note">
              Kept existing: {result.skipped.join(", ")}
            </div>
          )}
        </div>
      )}
    </>
  );
}
