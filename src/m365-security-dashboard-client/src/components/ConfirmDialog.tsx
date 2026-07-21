import React, { useState, useCallback, useEffect, useRef } from "react";
import { AlertTriangle } from "lucide-react";
import { ConfirmRequest, registerConfirmHandler } from "../services/confirm";

type Pending = ConfirmRequest & { resolve: (ok: boolean) => void };

/**
 * Host for confirmAction(). Render once, near ToastContainer.
 * role="alertdialog" + focus trap + Escape-to-cancel, matching the a11y
 * behaviour of DetailModal.
 */
export function ConfirmDialog() {
  const [pending, setPending] = useState<Pending | null>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const returnFocusRef = useRef<HTMLElement | null>(null);

  const handler = useCallback((r: ConfirmRequest) => {
    returnFocusRef.current = document.activeElement as HTMLElement | null;
    return new Promise<boolean>(resolve => setPending({ ...r, resolve }));
  }, []);

  useEffect(() => registerConfirmHandler(handler), [handler]);

  const close = useCallback((ok: boolean) => {
    setPending(prev => { prev?.resolve(ok); return null; });
    // Return focus to whatever opened the dialog.
    returnFocusRef.current?.focus?.();
  }, []);

  // Focus the safe default (Cancel is safer, but the confirm button is the
  // expected target; Escape and the backdrop both cancel, so focus confirm).
  useEffect(() => { if (pending) confirmRef.current?.focus(); }, [pending]);

  useEffect(() => {
    if (!pending) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.preventDefault(); close(false); return; }
      if (e.key !== "Tab") return;
      const nodes = dialogRef.current?.querySelectorAll<HTMLElement>("button");
      if (!nodes?.length) return;
      const first = nodes[0], last = nodes[nodes.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [pending, close]);

  if (!pending) return null;

  return (
    <div className="confirm-backdrop" onClick={() => close(false)}>
      <div className="confirm-dialog" ref={dialogRef} role="alertdialog" aria-modal="true"
        aria-labelledby="confirm-title" aria-describedby="confirm-msg"
        onClick={e => e.stopPropagation()}>
        <div className="confirm-hdr">
          {pending.danger && <AlertTriangle size={18} color="var(--status-error-icon)" aria-hidden="true"/>}
          <h2 id="confirm-title" className="confirm-title">{pending.title}</h2>
        </div>
        <p id="confirm-msg" className="confirm-msg">{pending.message}</p>
        <div className="confirm-actions">
          <button type="button" className="btn-cancel" onClick={() => close(false)}>
            {pending.cancelLabel ?? "Cancel"}
          </button>
          <button type="button" ref={confirmRef}
            className={pending.danger ? "btn-danger" : "btn-apply"}
            onClick={() => close(true)}>
            {pending.confirmLabel ?? "Confirm"}
          </button>
        </div>
      </div>
    </div>
  );
}
