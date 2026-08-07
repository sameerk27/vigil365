import React, { useState, useEffect, useMemo } from "react";
import { ShieldAlert, ExternalLink, Lightbulb, CheckCircle2, AlertTriangle, ChevronRight, Layers, ShieldCheck, RefreshCw } from "lucide-react";
import { SecurityRecommendation, CaGapFinding } from "../services/types";
import { recApi, caApi } from "../services/api";

// ─── Findings hub: fold analyzer findings into the recommendation format ──────
// CA gap analysis and SharePoint/OneDrive sharing posture produce the same
// shape of insight (finding + why + remediation). Surfacing them here keeps ONE
// place to answer "what should I fix?", instead of three scattered cards.
const sevRank = (s: string) => ({ critical: 0, high: 1, medium: 2, low: 3 } as Record<string, number>)[s] ?? 4;

function findingToRec(f: CaGapFinding, idx: number, source: "ca" | "sharing"): SecurityRecommendation {
  return {
    id: `${source}-${idx}`,
    category: source === "ca" ? "Conditional Access" : "SharePoint & OneDrive",
    title: f.title,
    severity: f.severity,
    affectedCount: 0,
    whyItMatters: f.detail,
    remediationSteps: [f.recommendation],
    portalBladeName: source === "ca" ? "Entra — Conditional Access" : "SharePoint admin — Sharing",
    portalDeepLink: source === "ca"
      ? "https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/ConditionalAccessBlade/~/Policies"
      : "https://admin.microsoft.com/sharepoint?page=sharing&modern=true",
  };
}
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
    // Each source loads independently — a failing analyzer never blanks the hub.
    const [recs, gaps, sharing] = await Promise.all([
      recApi.getRecommendations().catch(() => null),
      caApi.getGaps().catch(() => null),
      caApi.getSharingPosture().catch(() => null),
    ]);
    const merged: SecurityRecommendation[] = [
      ...(recs ?? []),
      ...(gaps?.findings ?? []).map((f, i) => findingToRec(f, i, "ca")),
      ...(sharing?.findings ?? []).map((f, i) => findingToRec(f, i, "sharing")),
    ].sort((a, b) => sevRank(a.severity) - sevRank(b.severity));

    if (recs === null && gaps === null && sharing === null) {
      setError("Could not load recommendations — the API request failed. Refresh to retry.");
    } else {
      setRecommendations(merged);
      if (merged.length > 0 && !expandedId) setExpandedId(merged[0].id);
    }
    setLoading(false);
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
        <div data-inline-style="inline-03fbcc5593">
          Prioritized posture improvements paired with why-it-matters context, step-by-step remediation,
          and a deep link to the right Microsoft 365 portal blade. Vigil365 never changes your tenant —
          all fixes are applied by you, in Microsoft's own portals.
        </div>

        <div data-inline-style="inline-5e49daee63">
          {categories.map(cat => {
            const count = cat === "All" ? recommendations.length : recommendations.filter(r => r.category === cat).length;
            return (
              <button key={cat} onClick={() => setSelectedCategory(cat)}
                className={selectedCategory === cat ? "btn-apply" : "btn-export"}
                data-inline-style="inline-02dfbae3d8">
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
          <div data-inline-style="inline-379fabac31">
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
                    data-inline-style="inline-e234c9eaae">
                    <div data-inline-style="inline-62a74bbb19">
                      <Lightbulb size={18} data-inline-style="inline-856d8b4afe" />
                      <div data-inline-style="inline-d529e1f5d1">
                        <div data-inline-style="inline-3002b9c150">
                          <Badge label={item.severity.toUpperCase()} tone={sevTone(item.severity)} />
                          <Badge label={item.category} tone="neutral" />
                          {item.affectedCount > 0
                            ? <Badge label={`${item.affectedCount} affected`} tone="warning" />
                            : <Badge label="Proactive" tone="good" />}
                        </div>
                        <div data-inline-style="inline-d83d37bfd1">{item.title}</div>
                      </div>
                    </div>
                    <div data-inline-style="inline-a36f4e065e">
                      <a className="btn-export" data-inline-style="inline-0ab1bd7012"
                        href={item.portalDeepLink} target="_blank" rel="noopener noreferrer"
                        onClick={e => e.stopPropagation()}>
                        Fix in Microsoft portal <ExternalLink size={12} />
                      </a>
                      <ChevronRight size={16} aria-hidden="true"
                        style={{ color: "var(--color-muted)", transform: isExpanded ? "rotate(90deg)" : undefined, transition: "transform .15s" }} />
                    </div>
                  </div>

                  {isExpanded && (
                    <div data-inline-style="inline-ae172c2dc3">
                      <div>
                        <div className="dm-section-hdr">Why this matters</div>
                        <div data-inline-style="inline-92dd221104">
                          {item.whyItMatters}
                        </div>
                        <div data-inline-style="inline-938ecffc1d">
                          Portal blade: <span data-inline-style="inline-09fb91c11d">{item.portalBladeName}</span>
                        </div>
                      </div>
                      <div>
                        <div className="dm-section-hdr">Remediation steps</div>
                        <ol data-inline-style="inline-24dee6c9a9">
                          {item.remediationSteps.map((step, idx) => (
                            <li key={idx} data-inline-style="inline-f1307a247f">{step}</li>
                          ))}
                        </ol>
                        <div data-inline-style="inline-21ba8aff3c">
                          <a className="btn-apply" data-inline-style="inline-64952b8dd3"
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
