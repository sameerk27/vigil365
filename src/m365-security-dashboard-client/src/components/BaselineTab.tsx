import React, { useEffect, useState, useCallback } from "react";
import { Card, LoadingSkeleton } from "./SharedComponents";
import { TableCard, DarkCard, Button, Pill, EmptyState } from "./ui";
import { baselineApi, useAuth, type BaselineResponse } from "../services/api";
import { relTime, fmtDate } from "../services/utils";
import { showToast } from "../services/toast";

const TONE_PILL: Record<string, "good" | "neutral" | "warn"> = { good: "good", neutral: "neutral", warn: "warn" };

export function BaselineTab() {
  const { isAdmin } = useAuth();
  const [data, setData] = useState<BaselineResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [capturing, setCapturing] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    const r = await baselineApi.get();
    setData(r);
    setLoading(false);
  }, []);

  useEffect(() => { void load(); }, [load]);

  const capture = async () => {
    setCapturing(true);
    const ok = await baselineApi.capture();
    setCapturing(false);
    if (ok) { showToast("Baseline captured from the latest collection", "success"); await load(); }
    else showToast("Couldn't capture a baseline — run a collection first", "error");
  };

  if (loading) return <LoadingSkeleton type="card" />;

  const captured = data?.captured ?? null;
  const drift = data?.drift ?? [];
  const drifted = data?.driftedCount ?? drift.filter(d => d.tone === "warn").length;

  return (
    <div className="baseline-tab">
      <div className="baseline-top">
        <Card title="Tenant Baseline">
          <p className="baseline-explainer">
            A baseline freezes the tenant&apos;s posture at a point in time. Policies with a{" "}
            <code className="baseline-code">drift</code> condition compare live values against these numbers
            instead of a fixed threshold, so an alert means &quot;something changed&quot; rather than
            &quot;something is bad&quot;. The numbers are copied from a real collection — nothing is invented.
          </p>
        </Card>
        <DarkCard eyebrow={captured ? "Captured" : "No baseline yet"}>
          {captured ? (
            <>
              <div className="baseline-date">{fmtDate(captured.at)}</div>
              <div className="baseline-meta">{relTime(captured.at)} · by {captured.by}</div>
            </>
          ) : (
            <div className="baseline-meta">
              {data?.canCapture
                ? "Capture the latest collection to start tracking drift."
                : "Run a collection first — there is no snapshot to capture yet."}
            </div>
          )}
          {isAdmin && (
            <div className="baseline-actions">
              <Button variant="primary" onClick={capture} disabled={capturing || !data?.canCapture}>
                {capturing ? "Capturing…" : captured ? "Recapture" : "Capture baseline"}
              </Button>
            </div>
          )}
        </DarkCard>
      </div>

      {captured ? (
        <TableCard
          title="Drift Against Baseline"
          chip={`${drifted} metric${drifted === 1 ? "" : "s"} drifted`}
          template="1.5fr 110px 110px 120px 1fr"
          columns={[
            { key: "metric", header: "METRIC" },
            { key: "baseline", header: "BASELINE", align: "end" },
            { key: "current", header: "CURRENT", align: "end" },
            { key: "drift", header: "DRIFT" },
            { key: "note", header: "NOTE" },
          ]}
          rows={drift.map(d => [
            d.metric,
            <span className="mono">{d.baseline}</span>,
            <span className="mono">{d.current}</span>,
            <Pill tone={TONE_PILL[d.tone] ?? "neutral"}>{d.drift}</Pill>,
            <span className="baseline-note">{d.tone === "warn" ? "Moved the wrong way since baseline" : d.tone === "good" && d.drift === "0" ? "Held at baseline" : "Moved the right way"}</span>,
          ])}
        />
      ) : (
        <Card title="Drift Against Baseline">
          <EmptyState>No baseline captured yet — capture one above to start tracking drift.</EmptyState>
        </Card>
      )}
    </div>
  );
}
