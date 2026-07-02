import React, { useState, useEffect, useMemo } from "react";
import { ShieldAlert, ExternalLink, Lightbulb, CheckCircle2, AlertTriangle, ChevronRight, Layers, ArrowRight, ShieldCheck, Filter, RefreshCw } from "lucide-react";
import { SecurityRecommendation, Tone } from "../services/types";
import { recApi } from "../services/api";
import { Card, KpiTile, Badge, EmptyState, SectHdr } from "../components/SharedComponents";

export function RecommendationsPage() {
  const [recommendations, setRecommendations] = useState<SecurityRecommendation[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedCategory, setSelectedCategory] = useState<string>("All");
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const loadData = async () => {
    setLoading(true);
    try {
      const data = await recApi.getRecommendations();
      setRecommendations(data);
      if (data.length > 0 && !expandedId) {
        setExpandedId(data[0].id);
      }
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadData();
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

  const sevTone = (sev: string): Tone => {
    switch (sev.toLowerCase()) {
      case "critical": return "error";
      case "high": return "warning";
      case "medium": return "info";
      default: return "good";
    }
  };

  return (
    <div className="space-y-6">
      {/* Page Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4 bg-gradient-to-r from-slate-900/90 via-slate-800/80 to-indigo-950/40 p-6 rounded-2xl border border-slate-800 shadow-xl">
        <div>
          <div className="flex items-center gap-3">
            <div className="p-2.5 rounded-xl bg-indigo-500/10 border border-indigo-500/20 text-indigo-400">
              <Lightbulb className="w-6 h-6" />
            </div>
            <div>
              <h1 className="text-2xl font-bold tracking-tight text-white flex items-center gap-2">
                Enterprise Security Recommendations
                <span className="text-xs px-2.5 py-0.5 rounded-full bg-indigo-500/20 text-indigo-300 font-semibold border border-indigo-500/30">Read-Only Advisor</span>
              </h1>
              <p className="text-sm text-slate-400 mt-1">
                Prioritized posture improvements paired with step-by-step remediation guides and direct M365 portal deep links.
              </p>
            </div>
          </div>
        </div>
        <button
          onClick={loadData}
          disabled={loading}
          className="flex items-center gap-2 px-4 py-2 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-700 text-sm font-medium transition disabled:opacity-50"
        >
          <RefreshCw className={`w-4 h-4 ${loading ? "animate-spin" : ""}`} />
          Refresh Analysis
        </button>
      </div>

      {/* Hero KPIs */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        <KpiTile
          label="Active Recommendations"
          value={recommendations.length}
          icon={<Layers size={20} />}
          tone="info"
          sub="Evaluated against active telemetry"
        />
        <KpiTile
          label="Critical Priority"
          value={criticalCount}
          icon={<ShieldAlert size={20} />}
          tone="error"
          sub="Immediate action required"
        />
        <KpiTile
          label="High Priority"
          value={highCount}
          icon={<AlertTriangle size={20} />}
          tone="warning"
          sub="Schedule remediation within 48h"
        />
        <KpiTile
          label="Total Entities Affected"
          value={totalAffected}
          icon={<ShieldCheck size={20} />}
          tone={totalAffected > 0 ? "warning" : "good"}
          sub="Users, devices & mailboxes"
        />
      </div>

      {/* Category Filter Tabs */}
      <div className="flex flex-wrap gap-2 pb-2 border-b border-slate-800">
        {categories.map(cat => (
          <button
            key={cat}
            onClick={() => setSelectedCategory(cat)}
            className={`px-4 py-2 rounded-lg text-sm font-medium transition flex items-center gap-2 ${
              selectedCategory === cat
                ? "bg-indigo-600 text-white shadow-md shadow-indigo-600/20"
                : "bg-slate-900/60 text-slate-400 hover:bg-slate-800 hover:text-slate-200 border border-slate-800"
            }`}
          >
            <Filter className="w-3.5 h-3.5" />
            {cat}
            <span className={`text-xs px-1.5 py-0.5 rounded-full ${selectedCategory === cat ? "bg-indigo-700 text-white" : "bg-slate-800 text-slate-400"}`}>
              {cat === "All" ? recommendations.length : recommendations.filter(r => r.category === cat).length}
            </span>
          </button>
        ))}
      </div>

      {/* Recommendations Feed */}
      {loading ? (
        <div className="p-12 text-center text-slate-500 bg-slate-900/30 rounded-xl border border-slate-800">
          <RefreshCw className="w-8 h-8 animate-spin mx-auto mb-3 text-indigo-500" />
          Analyzing tenant telemetry and building guidance cards...
        </div>
      ) : filtered.length === 0 ? (
        <EmptyState icon={<CheckCircle2 size={28} />} message="No recommendations found — your tenant matches all baseline criteria in this category." />
      ) : (
        <div className="grid grid-cols-1 gap-4">
          {filtered.map(item => {
            const isExpanded = expandedId === item.id;
            return (
              <div
                key={item.id}
                className={`transition-all duration-200 rounded-xl border overflow-hidden ${
                  isExpanded
                    ? "bg-slate-900/90 border-indigo-500/40 shadow-lg shadow-indigo-950/20"
                    : "bg-slate-900/40 border-slate-800/80 hover:border-slate-700 hover:bg-slate-900/60"
                }`}
              >
                {/* Header Row */}
                <div
                  onClick={() => setExpandedId(isExpanded ? null : item.id)}
                  className="p-5 flex flex-col sm:flex-row sm:items-center justify-between gap-4 cursor-pointer select-none"
                >
                  <div className="flex items-start gap-4">
                    <div className={`mt-0.5 p-2.5 rounded-xl border ${
                      item.severity === "critical" ? "bg-red-500/10 border-red-500/20 text-red-400" :
                      item.severity === "high" ? "bg-amber-500/10 border-amber-500/20 text-amber-400" :
                      item.severity === "medium" ? "bg-sky-500/10 border-sky-500/20 text-sky-400" :
                      "bg-emerald-500/10 border-emerald-500/20 text-emerald-400"
                    }`}>
                      <Lightbulb className="w-5 h-5" />
                    </div>
                    <div>
                      <div className="flex flex-wrap items-center gap-2 mb-1">
                        <Badge label={item.severity.toUpperCase()} tone={sevTone(item.severity)} />
                        <span className="text-xs font-semibold text-slate-400 bg-slate-800/80 px-2 py-0.5 rounded border border-slate-700/60">
                          {item.category}
                        </span>
                        {item.affectedCount > 0 ? (
                          <span className="text-xs font-bold text-amber-300 bg-amber-500/10 px-2 py-0.5 rounded border border-amber-500/20">
                            {item.affectedCount} Affected Entities
                          </span>
                        ) : (
                          <span className="text-xs font-medium text-emerald-400 bg-emerald-500/10 px-2 py-0.5 rounded border border-emerald-500/20">
                            Healthy / Proactive
                          </span>
                        )}
                      </div>
                      <h3 className="text-lg font-bold text-white group-hover:text-indigo-300 transition">
                        {item.title}
                      </h3>
                    </div>
                  </div>

                  <div className="flex items-center gap-3 self-end sm:self-center">
                    <a
                      href={item.portalDeepLink}
                      target="_blank"
                      rel="noopener noreferrer"
                      onClick={(e) => e.stopPropagation()}
                      className="px-4 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-500 text-white font-medium text-sm flex items-center gap-2 shadow-sm transition"
                    >
                      <span>Fix in Microsoft Portal</span>
                      <ExternalLink className="w-4 h-4" />
                    </a>
                    <div className="text-slate-400 p-1">
                      <ChevronRight className={`w-5 h-5 transform transition-transform ${isExpanded ? "rotate-90 text-indigo-400" : ""}`} />
                    </div>
                  </div>
                </div>

                {/* Expanded Guidance Detail Section */}
                {isExpanded && (
                  <div className="px-6 pb-6 pt-2 border-t border-slate-800/80 bg-slate-950/40 grid grid-cols-1 md:grid-cols-12 gap-6 animate-fadeIn">
                    {/* Left Column: Why It Matters */}
                    <div className="md:col-span-5 space-y-3">
                      <h4 className="text-sm font-bold uppercase tracking-wider text-indigo-400 flex items-center gap-2">
                        <AlertTriangle className="w-4 h-4" />
                        Why This Matters (Risk Impact)
                      </h4>
                      <div className="p-4 rounded-xl bg-slate-900/80 border border-slate-800 text-sm text-slate-300 leading-relaxed">
                        {item.whyItMatters}
                      </div>
                      <div className="p-4 rounded-xl bg-indigo-950/30 border border-indigo-900/40 text-xs text-indigo-300 flex items-center justify-between">
                        <span className="font-semibold">Target Portal Blade:</span>
                        <span className="font-mono bg-indigo-900/60 px-2 py-1 rounded text-white">{item.portalBladeName}</span>
                      </div>
                    </div>

                    {/* Right Column: Step-by-Step Remediation Plan */}
                    <div className="md:col-span-7 space-y-3">
                      <h4 className="text-sm font-bold uppercase tracking-wider text-emerald-400 flex items-center gap-2">
                        <CheckCircle2 className="w-4 h-4" />
                        Step-by-Step Engineer Guidance
                      </h4>
                      <div className="p-4 rounded-xl bg-slate-900/80 border border-slate-800 space-y-3">
                        {item.remediationSteps.map((step, idx) => (
                          <div key={idx} className="flex items-start gap-3 text-sm text-slate-300">
                            <span className="flex-shrink-0 w-6 h-6 rounded-full bg-emerald-500/10 border border-emerald-500/30 text-emerald-400 font-bold text-xs flex items-center justify-center mt-0.5">
                              {idx + 1}
                            </span>
                            <span className="leading-relaxed">{step}</span>
                          </div>
                        ))}
                      </div>
                      <div className="flex justify-end pt-2">
                        <a
                          href={item.portalDeepLink}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl bg-gradient-to-r from-indigo-600 to-indigo-500 hover:from-indigo-500 hover:to-indigo-400 text-white font-semibold text-sm shadow-md shadow-indigo-600/30 transition transform hover:-translate-y-0.5"
                        >
                          <span>Open {item.portalBladeName}</span>
                          <ExternalLink className="w-4 h-4" />
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
    </div>
  );
}
