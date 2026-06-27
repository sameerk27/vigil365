import React, { useState, useCallback, useEffect } from "react";
import { XCircle, CheckCircle } from "lucide-react";
import { ToastEntry, registerToastHandler } from "../services/toast";

export function ToastContainer() {
  const [toasts, setToasts] = useState<ToastEntry[]>([]);

  const addToast = useCallback((t: Omit<ToastEntry, "id">) => {
    const id = Date.now() + Math.random();
    setToasts(prev => [...prev, { ...t, id }]);
    setTimeout(() => setToasts(prev => prev.filter(x => x.id !== id)), 3000);
  }, []);

  useEffect(() => {
    const unregister = registerToastHandler(addToast);
    return unregister;
  }, [addToast]);

  return (
    <div className="toast-container">
      {toasts.map(t => (
        <div key={t.id} className={`toast toast-${t.type ?? "success"}`}>
          {t.type === "error" ? <XCircle size={15} /> : <CheckCircle size={15} color="var(--status-good-icon)" />}
          <span>{t.message}</span>
        </div>
      ))}
    </div>
  );
}
