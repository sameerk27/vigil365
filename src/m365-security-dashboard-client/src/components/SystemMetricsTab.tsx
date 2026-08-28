import React, { useEffect, useState } from "react";
import { Card, LoadingSkeleton } from "./SharedComponents";
import { StatCard, TableCard, Sparkline, CodeBlock, MeterRow, EmptyState } from "./ui";
import { metricsApi, type SystemMetrics } from "../services/api";

function fmtBytes(b: number | null): string {
  if (b == null) return "—";
  if (b >= 1024 ** 3) return `${(b / 1024 ** 3).toFixed(1)} GB`;
  if (b >= 1024 ** 2) return `${(b / 1024 ** 2).toFixed(1)} MB`;
  if (b >= 1024) return `${(b / 1024).toFixed(0)} KB`;
  return `${b} B`;
}

export function SystemMetricsTab() {
  const [data, setData] = useState<SystemMetrics | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    metricsApi.get().then(d => { if (!cancelled) { setData(d); setLoading(false); } });
    return () => { cancelled = true; };
  }, []);

  if (loading) return <LoadingSkeleton type="card" />;
  if (!data) return <Card title="System Metrics"><EmptyState>Couldn&apos;t load metrics — refresh to retry.</EmptyState></Card>;

  const throttleHasData = data.throttleTrend.length >= 2;
  const throttledInWindow = data.throttleTrend.reduce((a, b) => a + b, 0);

  return (
    <div className="metrics-tab">
      <div className="stat-row-4">
        <StatCard label="Collector uptime" value={`${data.collectorUptimePct}%`}
          sub={data.runsWindow > 0 ? `${data.runsWindow} runs, 30-day window` : "no runs in 30 days"}/>
        <StatCard label="Graph calls / last run" value={data.graphCallsLastRun ?? "—"}
          sub={`${data.graphCallsTotal.toLocaleString()} all-time`}/>
        <StatCard label="Eval latency p95" value={data.evalP95Ms != null ? `${data.evalP95Ms} ms` : "—"}
          sub={data.evalSamples > 0 ? `${data.evalSamples} evaluations` : "no evaluations yet"}/>
        <StatCard label="DB size" value={fmtBytes(data.dbSizeBytes)}
          sub={`retention ${data.retentionDays} days`}/>
      </div>

      <div className="metrics-grid">
        <TableCard
          title="Prometheus Metrics"
          chip={<CodeBlock>/metrics</CodeBlock>}
          template="1.7fr 110px 1.4fr"
          columns={[
            { key: "metric", header: "METRIC" },
            { key: "value", header: "VALUE", align: "end" },
            { key: "meaning", header: "MEANING" },
          ]}
          rows={data.prometheus.map(r => [
            <span className="mono">{r.metric}</span>,
            <span className="mono">{r.value}</span>,
            <span className="metrics-meaning">{r.meaning}</span>,
          ])}
        />

        <div className="metrics-side">
          <Card title="Graph API Throttling"
            badge={<span className={`ui-pill tone-${throttledInWindow === 0 ? "good" : "warn"}`}>{throttledInWindow === 0 ? "Healthy" : `${throttledInWindow} throttled`}</span>}>
            {throttleHasData ? (
              <>
                <Sparkline points={data.throttleTrend} stroke="good" height={110} axis={["older", "latest"]}/>
                <p className="metrics-caption">429 responses per run over the last {data.throttleTrend.length} runs. A flat line at zero is healthy.</p>
              </>
            ) : (
              <EmptyState>Not enough collection runs yet to chart throttling.</EmptyState>
            )}
          </Card>

          <Card title="Evaluation Timing">
            {data.evalLastMs != null ? (
              <div className="metrics-eval">
                <MeterRow label="Last evaluation" pct={data.evalP95Ms ? Math.min(100, (data.evalLastMs / Math.max(data.evalP95Ms, 1)) * 100) : 50}
                  value={`${data.evalLastMs} ms`} tone="accent" labelWidth="120px"/>
                <MeterRow label="p95" pct={100} value={data.evalP95Ms != null ? `${data.evalP95Ms} ms` : "—"} tone="med" labelWidth="120px"/>
                <p className="metrics-caption">Policy-evaluation duration, measured over the last {data.evalSamples} runs since service start.</p>
              </div>
            ) : (
              <EmptyState>No policy evaluations recorded yet — they run after each collection.</EmptyState>
            )}
          </Card>
        </div>
      </div>
    </div>
  );
}
