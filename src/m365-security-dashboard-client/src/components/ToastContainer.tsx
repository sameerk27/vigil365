import React, { useState, useCallback, useEffect } from "react";
import { XCircle, CheckCircle, Info } from "lucide-react";
import { ToastEntry, registerToastHandler } from "../services/toast";

export function ToastContainer() {
  const [toasts, setToasts] = useState<ToastEntry[]>([]);

  const addToast = useCallback((t: Omit<ToastEntry, "id">) => {
    const id = Date.now() + Math.random();
    setToasts(prev => [...prev, { ...t, id }]);
    // Undo-able toasts stay longer so the action is actually reachable.
    setTimeout(() => setToasts(prev => prev.filter(x => x.id !== id)), t.action ? 6000 : 3000);
  }, []);

  useEffect(() => {
    const unregister = registerToastHandler(addToast);
    return unregister;
  }, [addToast]);

  return (
    <div className="toast-container" role="status" aria-live="polite" aria-atomic="true">
      {toasts.map(t => (
        <div key={t.id} className={`toast toast-${t.type ?? "success"}`}>
          {t.type === "error"
            ? <XCircle size={15} color="var(--status-error-icon)" />
            : t.type === "info"
              ? <Info size={15} color="var(--sev-info-icon)" />
              : <CheckCircle size={15} color="var(--status-good-icon)" />}
          <span style={{ flex: 1 }}>{t.message}</span>
          {t.action && (
            <button className="toast-action"
              onClick={() => { t.action!.onAction(); setToasts(prev => prev.filter(x => x.id !== t.id)); }}>
              {t.action.label}
            </button>
          )}
        </div>
      ))}
    </div>
  );
}
