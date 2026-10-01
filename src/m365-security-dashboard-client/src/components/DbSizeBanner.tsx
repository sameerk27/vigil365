import React, { useEffect, useState } from "react";
import { Database } from "lucide-react";
import { apiBase } from "../services/api";

interface DbCheck { sizeBytes?: number | null; sizeWarning?: boolean; sizeWarningBytes?: number }

const gb = (b: number) => `${(b / 1024 ** 3).toFixed(1)} GB`;

/**
 * Admin-only warning when the database nears its size limit (MSP_V12_PLAN.md U8).
 * /health already computed it; nothing showed it, so an install on SQL Server
 * Express would only find out when writes started failing at 10 GB.
 */
export function DbSizeBanner() {
  const [db, setDb] = useState<DbCheck | null>(null);
  useEffect(() => {
    let cancelled = false;
    fetch(`${apiBase}/health`)
      .then(r => r.json())
      .then(h => { if (!cancelled) setDb(h?.checks?.database ?? null); })
      .catch(() => { /* health unreachable: nothing to warn about here */ });
    return () => { cancelled = true; };
  }, []);

  if (!db?.sizeWarning || !db.sizeBytes) return null;
  return (
    <div className="db-size-banner" role="alert">
      <Database size={16} aria-hidden="true" />
      <span>
        The database is {gb(db.sizeBytes)}{db.sizeWarningBytes ? ` (warning threshold ${gb(db.sizeWarningBytes)})` : ""}.
        SQL Server Express stops accepting writes at 10 GB. Shorten retention, or move to SQL Server Standard or PostgreSQL —
        see the Operations Runbook.
      </span>
    </div>
  );
}
