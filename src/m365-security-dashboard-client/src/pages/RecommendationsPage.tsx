import React, { useState, useEffect, useMemo } from "react";
import { ShieldAlert, ExternalLink, Lightbulb, CheckCircle2, AlertTriangle, ChevronRight, Layers, ShieldCheck, RefreshCw } from "lucide-react";
import { SecurityRecommendation } from "../services/types";
import { recApi } from "../services/api";
import { KpiTile, Card, Badge, EmptyState, LoadingSkeleton } from "../components/SharedComponents";
import { sevTone } from "../services/utils";

export function RecommendationsPage() {
  const [recommendations, setRecommendations] = useState<SecurityRecommendation[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedCategory, setSelectedCategory] = useState<string>("All");
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const loadData = async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await recApi.getRecommendations();
      setRecommendations(data);
      if (data.length > 0 && !expandedId) {
        setExpandedId(data[0].id);
      }
    } catch {
      setError("Could not load recommendations — the API request failed. Refresh to retry.");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const categories = useMemo(() => {
    const set = new Set<string>();
    set.add("All");
    recommendations.forEach(r => set.add(r.category));
    return Array.from(set);
  }, [recommendations]);

  const filtered = useMemo(() => {
    if (selectedCategory === "All") return recommendations;
    return recommendations.filter(r => r.category === selectedCategory);
  }, [recommendations, selectedCategory]);

  const criticalCount = useMemo(() => recommendations.filter(r => r.severity === "critical").length, [recommendations]);
  const highCount = useMemo(() => recommendations.filter(r => r.severity === "high").length, [recommendations]);
  const totalAffected = useMemo(() => recommendations.reduce((acc, r) => acc + r.affectedCount, 0), [recommendations]);

  return (
    <div className="page">
      <div className="kpi-row kpi-row-4">
        <KpiTile
          label="ACTIVE RECOMMENDATIONS"
          value={recommendations.length}
          icon={<Layers size={18} />}
          tone="info"
          sub="Evaluated against collected telemetry"
        />
        <KpiTile
          label="CRITICAL PRIORITY"
          value={criticalCount}
          icon={<ShieldAlert size={18} />}
          tone={criticalCount > 0 ? "error" : "good"}
          sub={criticalCount > 0 ? "Immediate action required" : "None open"}
        />
        <KpiTile
          label="HIGH PRIORITY"
          value={highCount}
          icon={<AlertTriangle size={18} />}
          tone={highCount > 0 ? "warning" : "good"}
          sub={highCount > 0 ? "Schedule remediation within 48h" : "None open"}
        />
        <KpiTile
          label="ENTITIES AFFECTED"
          value={totalAffected}
          icon={<ShieldCheck size={18} />}
          tone={totalAffected > 0 ? "warning" : "good"}
          sub="Users, devices & mailboxes"
        />
      </div>

      <Card title="Security Recommendations"
        badge={<Badge label="Read-only advisor" tone="info" />}
        action={
          <button className="btn-export" onClick={loadData} disabled={loading}>
            <RefreshCw size={13} className={loading ? "spin" : undefined} /> Refresh
          </button>
        }>
        <div style={{ fontSize: 12, color: "var(--color-muted)", padding: "0 0 12px", lineHeight: 1.6 }}>
          Prioritized posture improvements paired with why-it-matters context, step-by-step remediation,
          and a deep link to the right Microsoft 365 portal blade. Vigil365 never changes your tenant —
          all fixes are applied by you, in Microsoft's own portals.
        </div>

        <div style={{ display: "flex", flexWrap: "wrap", gap: 6, paddingBottom: 14 }}>
          {categories.map(cat => {
            const count = cat === "All" ? recommendations.length : recommendations.filter(r => r.category === cat).length;
            return (
              <button key={cat} onClick={() => setSelectedCategory(cat)}
                className={selectedCategory === cat ? "btn-apply" : "btn-export"}
                style={{ padding: "5px 12px", fontSize: 12 }}>
                {cat} ({count})
              </button>
            );
          })}
        </div>

        {loading ? (
          <LoadingSkeleton type="table" />
        ) : error ? (
          <EmptyState icon={<AlertTriangle size={28} />} message={error} />
        ) : filtered.length === 0 ? (
          <EmptyState icon={<CheckCircle2 size={28} />} message="No recommendations in this category — your tenant matches the baseline criteria." />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            {filtered.map(item => {
              const isExpanded = expandedId === item.id;
              return (
                <div key={item.id}
                  style={{
                    border: `1px solid ${isExpanded ? "var(--color-primary-border)" : "var(--color-border)"}`,
                    borderRadius: 10, overflow: "hidden",
                    background: isExpanded ? "var(--color-card)" : "var(--color-raised)",
                  }}>
                  <div role="button" tabIndex={0} aria-expanded={isExpanded}
                    onClick={() => setExpandedId(isExpanded ? null : item.id)}
                    onKeyDown={e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setExpandedId(isExpanded ? null : item.id); } }}
                    style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, padding: "14px 16px", cursor: "pointer" }}>
                    <div style={{ display: "flex", alignItems: "flex-start", gap: 12, minWidth: 0 }}>
                      <Lightbulb size={18} style={{ flexShrink: 0, marginTop: 2, color: "var(--color-primary)" }} />
                      <div style={{ minWidth: 0 }}>
                        <div style={{ display: "flex", flexWrap: "wrap", alignItems: "center", gap: 6, marginBottom: 4 }}>
                          <Badge label={item.severity.toUpperCase()} tone={sevTone(item.severity)} />
                          <Badge label={item.category} tone="neutral" />
                          {item.affectedCount > 0
                            ? <Badge label={`${item.affectedCount} affected`} tone="warning" />
                            : <Badge label="Proactive" tone="good" />}
                        </div>
                        <div style={{ fontWeight: 600, fontSize: 14, color: "var(--color-text)" }}>{item.title}</div>
                      </div>
                    </div>
                    <div style={{ display: "flex", alignItems: "center", gap: 8, flexShrink: 0 }}>
                      <a className="btn-export" style={{ display: "inline-flex", textDecoration: "none" }}
                        href={item.portalDeepLink} target="_blank" rel="noopener noreferrer"
                        onClick={e => e.stopPropagation()}>
                        Fix in Microsoft portal <ExternalLink size={12} />
                      </a>
                      <ChevronRight size={16} aria-hidden="true"
                        style={{ color: "var(--color-muted)", transform: isExpanded ? "rotate(90deg)" : undefined, transition: "transform .15s" }} />
                    </div>
                  </div>

                  {isExpanded && (
                    <div style={{ borderTop: "1px solid var(--color-border)", padding: "14px 16px", display: "grid", gridTemplateColumns: "minmax(220px, 5fr) minmax(280px, 7fr)", gap: 16 }}>
                      <div>
                        <div className="dm-section-hdr">Why this matters</div>
                        <div style={{ fontSize: 12.5, color: "var(--color-text-secondary)", lineHeight: 1.6, marginTop: 6 }}>
                          {item.whyItMatters}
                        </div>
                        <div style={{ marginTop: 10, fontSize: 12, color: "var(--color-muted)" }}>
                          Portal blade: <span style={{ fontFamily: "monospace", background: "var(--color-raised)", border: "1px solid var(--color-border)", borderRadius: 4, padding: "1px 6px", color: "var(--color-text)" }}>{item.portalBladeName}</span>
                        </div>
                      </div>
                      <div>
                        <div className="dm-section-hdr">Remediation steps</div>
                        <ol style={{ margin: "6px 0 0", paddingLeft: 20, display: "flex", flexDirection: "column", gap: 6 }}>
                          {item.remediationSteps.map((step, idx) => (
                            <li key={idx} style={{ fontSize: 12.5, color: "var(--color-text-secondary)", lineHeight: 1.55 }}>{step}</li>
                          ))}
                        </ol>
                        <div style={{ display: "flex", justifyContent: "flex-end", marginTop: 10 }}>
                          <a className="btn-apply" style={{ display: "inline-flex", alignItems: "center", gap: 5, textDecoration: "none", padding: "6px 14px", fontSize: 12 }}
                            href={item.portalDeepLink} target="_blank" rel="noopener noreferrer">
                            Open {item.portalBladeName} <ExternalLink size={12} />
                          </a>
                        </div>
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </Card>
    </div>
  );
}
