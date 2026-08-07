import React, { useState } from "react";
import { FlaskConical, Info, AlertTriangle } from "lucide-react";
import { AlertPolicy } from "../services/types";
import { apiBase, apiFetch } from "../services/api";
import { fmtDate } from "../services/utils";

type BacktestResult = {
  supported: boolean;
  unsupportedReason: string | null;
  windowDays: number;
  threshold: number;
  wouldFireCount: number;
  maxObservedValue: number;
  samplesEvaluated: number;
  basis: string;
  firedAt: string[];
};

const WINDOWS = [7, 30, 90] as const;

/**
 * "If I saved this policy, how noisy would it be?" — answered against real
 * stored history before the analyst commits to a threshold.
 *
 * Deliberately reports an unsupported result rather than a zero when history
 * cannot answer the question: "would have fired 0 times" reads as "this policy
 * is safe", which is exactly the wrong conclusion to draw from missing data.
 */
export function PolicyDryRun({ buildDraft }: { buildDraft: () => AlertPolicy }) {
  const [days, setDays] = useState<number>(0);
  const [result, setResult] = useState<BacktestResult | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const clear = () => {
    setDays(0);
    setResult(null);
    setError(null);
  };

  const run = async (windowDays: number) => {
    if (days === windowDays && result) {
      clear();
      return;
    }
    setDays(windowDays);
    setRunning(true); setError(null); setResult(null);
    try {
      const r = await apiFetch(`${apiBase}/api/alert-policies/backtest?days=${windowDays}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildDraft()),
      });
      if (!r.ok) {
        const body = await r.json().catch(() => ({}));
        setError(body.error ?? "Could not run the dry run.");
      } else {
        setResult(await r.json());
      }
    } catch {
      setError("Could not reach the API.");
    } finally {
      setRunning(false);
    }
  };

  const noisy = result?.supported && result.wouldFireCount > 20;

  return (
    <div className="dryrun">
      <div className="dryrun-head">
        <span className="dryrun-title"><FlaskConical size={14}/> Dry run</span>
        <div className="pill-group">
          {WINDOWS.map(w => (
            <button key={w} type="button" className={`pill-btn ${days === w ? "active" : ""}`}
              disabled={running} onClick={() => run(w)}>
              {running && days === w ? "…" : `Test ${w}d`}
            </button>
          ))}
        </div>
      </div>

      {error && <div className="dryrun-msg dryrun-err"><AlertTriangle size={13}/> {error}</div>}

      {result && !result.supported && (
        <div className="dryrun-msg dryrun-unsupported">
          <Info size={13}/>
          <span><strong>Can't be tested against history.</strong> {result.unsupportedReason}</span>
        </div>
      )}

      {result?.supported && (
        <div className="dryrun-result">
          <div className="dryrun-headline">
            Would have fired{" "}
            <strong className={noisy ? "dryrun-noisy" : undefined}>{result.wouldFireCount}×</strong>{" "}
            in the last {result.windowDays} day{result.windowDays === 1 ? "" : "s"}
            {noisy && <span className="dryrun-noisy-hint"> — consider a higher threshold</span>}
          </div>
          <div className="dryrun-detail">
            Peak observed value <strong>{result.maxObservedValue}</strong> against a threshold of{" "}
            <strong>{result.threshold}</strong>. {result.basis}
          </div>
          {result.firedAt.length > 0 && (
            <details className="dryrun-times">
              <summary>{result.firedAt.length === result.wouldFireCount
                ? `When it would have fired (${result.firedAt.length})`
                : `First ${result.firedAt.length} firing times`}</summary>
              <ul>{result.firedAt.map(t => <li key={t}>{fmtDate(t)}</li>)}</ul>
            </details>
          )}
        </div>
      )}

      {!result && !error && !running && (
        <div className="dryrun-hint">
          Replay this policy against stored history to see how often it would have alerted
          before you save it.
        </div>
      )}
    </div>
  );
}
