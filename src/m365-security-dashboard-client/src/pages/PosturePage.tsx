import React, { useEffect, useMemo, useState } from "react";
import { Search, ShieldCheck, Download, RefreshCw, AlertTriangle } from "lucide-react";
import { apiBase, apiFetch, clientFileName } from "../services/api";
import { showToast } from "../services/toast";
import { Card, Badge, EmptyState, LoadingSkeleton, ExportDropdown, KpiTile, DetailModal, RelativeTime, SortTh } from "../components/SharedComponents";
import type { Tone } from "../services/types";

type PostureStatus = "Pass" | "Fail" | "LicensedNotConfigured" | "LicensedLowAdoption" | "NotLicensed" | "Unknown";

type PostureFinding = {
  id: string; title: string; workload: string; status: PostureStatus | string; detail: string;
  licensedBy: string | null; unlockedBy: string[]; readFrom: string;
  whyItMatters: string; minimum: string; fix: string[]; portalUrl: string | null;
  feature: string; featureSource: string;
  baselines: { scuba: string[]; cis: string[]; secureScore: string[] };
};

type Posture = {
  configured: boolean;
  generatedAt?: string;
  organizationName?: string | null;
  error?: string | null;
  licences?: { partNumber: string; name: string; enabled: number; consumed: number }[];
  includedFeatures?: string[];
  summary?: { total: number; pass: number; fail: number; licensedNotConfigured: number; licensedLowAdoption: number; notLicensed: number; unknown: number };
  scuba?: { pass: number; total: number };
  secureScore?: { current: number; max: number } | null;
  sources?: { name: string; state: string; detail: string }[];
  findings?: PostureFinding[];
  upgrades?: { unlockedBy: string[]; checks: string[] }[];
};

// The same words as the client report's tiles, so the page and the report agree.
// Rank orders the default sort: what needs doing first.
const STATUS: Record<PostureStatus, { label: string; tone: Tone; rank: number }> = {
  Fail: { label: "Fail", tone: "error", rank: 0 },
  LicensedNotConfigured: { label: "Paid for, not used", tone: "warning", rank: 1 },
  LicensedLowAdoption: { label: "Low adoption", tone: "warning", rank: 2 },
  Unknown: { label: "Unknown", tone: "neutral", rank: 3 },
  NotLicensed: { label: "Needs another licence", tone: "neutral", rank: 4 },
  Pass: { label: "Pass", tone: "good", rank: 5 },
};
// A status a newer API sends shows as itself rather than breaking the page.
const meta = (s: string) => STATUS[s as PostureStatus] ?? { label: s, tone: "neutral" as Tone, rank: 9 };

const sourceTone = (state: string): Tone =>
  state === "Connected" ? "good" : state === "Partly read" ? "warning" : state === "Couldn't read" ? "error" : "neutral";

const licenceCell = (f: PostureFinding) =>
  f.status === "NotLicensed" ? `Needs ${f.unlockedBy[0] ?? f.feature}` : f.licensedBy ?? "";

type SortCol = "status" | "title" | "workload";

/**
 * Licence posture (docs/LICENSE_POSTURE_PLAN.md): what the tenant's licences include, which
 * of those features are set up to a minimum, which are paid for but unused, and what would
 * need another licence. Read live from Graph on each visit; read-only.
 */
export function PosturePage() {
  const [data, setData] = useState<Posture | null>(null);
  const [failed, setFailed] = useState(false);
  const [loadKey, setLoadKey] = useState(0);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [workload, setWorkload] = useState("");
  const [sortBy, setSortBy] = useState<SortCol>("status");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");
  const [open, setOpen] = useState<PostureFinding | null>(null);
  const [downloading, setDownloading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    // The server keeps a read for a few minutes; "Read again" asks for a fresh one.
    apiFetch(`${apiBase}/api/posture${loadKey > 0 ? "?refresh=true" : ""}`)
      .then(r => { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .then((d: Posture) => { if (!cancelled) setData(d); })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, [loadKey]);

  const reload = () => { setData(null); setFailed(false); setLoadKey(k => k + 1); };

  const onSort = (col: SortCol) => {
    if (sortBy === col) setSortDir(d => d === "asc" ? "desc" : "asc");
    else { setSortBy(col); setSortDir("asc"); }
  };

  const all = data?.findings ?? [];
  const workloads = useMemo(() => [...new Set(all.map(f => f.workload))].sort(), [all]);
  const findings = useMemo(() => {
    const q = search.trim().toLowerCase();
    const dir = sortDir === "asc" ? 1 : -1;
    return all
      .filter(f => (!status || f.status === status) && (!workload || f.workload === workload)
        && (!q || [f.id, f.title, f.workload, f.detail, f.licensedBy ?? "", ...f.unlockedBy].some(v => v.toLowerCase().includes(q))))
      .sort((a, b) => {
        const by = sortBy === "status" ? meta(a.status).rank - meta(b.status).rank
          : sortBy === "title" ? a.title.localeCompare(b.title)
          : a.workload.localeCompare(b.workload);
        return by * dir || a.id.localeCompare(b.id);
      });
  }, [all, search, status, workload, sortBy, sortDir]);

  const downloadReport = async () => {
    setDownloading(true);
    try {
      const r = await apiFetch(`${apiBase}/api/posture/report.html`);
      if (!r.ok) { showToast("The client report couldn't be created. Try again in a minute.", "error"); return; }
      const url = URL.createObjectURL(await r.blob());
      const a = document.createElement("a");
      // MSP mode: the client's name leads the filename, as on every other export.
      a.href = url; a.download = clientFileName(`licence-posture-${new Date().toISOString().slice(0, 10)}.html`); a.click();
      URL.revokeObjectURL(url);
      showToast("Client report downloaded");
    } catch {
      showToast("The client report couldn't be created. Try again in a minute.", "error");
    } finally {
      setDownloading(false);
    }
  };

  if (failed) return <div className="page"><EmptyState message="Could not load licence posture: the API request failed. Refresh to retry."/></div>;
  if (data === null) return <div className="page"><LoadingSkeleton type="table"/></div>;
  if (!data.configured) return <div className="page"><EmptyState icon={<ShieldCheck size={28}/>} message="Connect Microsoft Graph in Setup to see licence posture."/></div>;

  const s = data.summary;
  const tile = (key: PostureStatus, value: number | undefined, sub: string) => (
    <KpiTile label={STATUS[key].label} value={value ?? 0} sub={sub} tone={STATUS[key].tone}
      active={status === key} onClick={() => setStatus(v => v === key ? "" : key)}/>
  );
  const byId = new Map(all.map(f => [f.id, f]));

  return (
    <div className="page">
      {data.error && (
        <div className="notice-warn" role="status">
          <AlertTriangle size={16} aria-hidden="true"/>
          <div>
            <strong>Every check shows as Unknown</strong>
            <p>{data.error} Vigil365 needs the licences to know which features the tenant has. Check the Graph permissions in Setup, then read again.</p>
          </div>
        </div>
      )}

      <div className="posture-toolbar">
        <span className="posture-read">
          {data.organizationName ? `${data.organizationName} · ` : ""}
          {data.generatedAt ? <RelativeTime iso={data.generatedAt} prefix="read "/> : null}
        </span>
        <button type="button" className="btn-export" onClick={reload}><RefreshCw size={13} aria-hidden="true"/> Read again</button>
        <button type="button" className="btn-export" onClick={downloadReport} disabled={downloading}>
          <Download size={13} aria-hidden="true"/> {downloading ? "Creating report…" : "Client report (HTML)"}
        </button>
      </div>

      <div className="kpi-row kpi-row-5">
        {tile("Pass", s?.pass, `of ${s?.total ?? 0} checks`)}
        {tile("Fail", s?.fail, "below the minimum")}
        {tile("LicensedNotConfigured", s?.licensedNotConfigured, "licensed, not set up")}
        {tile("NotLicensed", s?.notLicensed, "no owned licence includes it")}
        {tile("Unknown", s?.unknown, "couldn't be read")}
      </div>

      <div className="two-col">
        <Card title="Licences" badge={<Badge label={`${data.licences?.length ?? 0} owned`} tone="neutral"/>}>
          {(data.licences ?? []).length === 0 ? (
            <p className="posture-muted">{data.error ? "Licences couldn't be read." : "No licences with enabled seats."}</p>
          ) : (
            <ul className="posture-list">
              {(data.licences ?? []).map(l => (
                <li key={l.partNumber}><span>{l.name}</span><span className="posture-muted">{l.consumed} of {l.enabled} assigned</span></li>
              ))}
            </ul>
          )}
          {(data.includedFeatures ?? []).length > 0 && (
            <div className="posture-features" aria-label="Features these licences include">
              {(data.includedFeatures ?? []).map(name => <Badge key={name} label={name} tone="neutral"/>)}
            </div>
          )}
        </Card>
        <Card title="Sources and baselines">
          <ul className="posture-list">
            {(data.sources ?? []).map(src => (
              <li key={src.name}>
                <span>{src.name}<span className="posture-sub">{src.detail}</span></span>
                <Badge label={src.state} tone={sourceTone(src.state)}/>
              </li>
            ))}
            <li><span>CISA SCuBA</span><span className="posture-muted">{data.scuba?.pass ?? 0} of {data.scuba?.total ?? 0} mapped checks pass</span></li>
            <li><span>Microsoft Secure Score</span><span className="posture-muted">
              {data.secureScore ? `${Math.round(data.secureScore.current)} of ${Math.round(data.secureScore.max)} points` : "Not read"}
            </span></li>
            <li><span>CIS Microsoft 365 Foundations</span><span className="posture-muted">Not mapped yet</span></li>
          </ul>
        </Card>
      </div>

      <Card title="Checks"
        badge={<Badge label={`${findings.length} of ${all.length}`} tone="neutral"/>}
        action={
          <div className="copilot-filters">
            <label className="search-box">
              <Search size={14} aria-hidden="true"/>
              <input className="search-input" aria-label="Search checks" value={search} onChange={e => setSearch(e.target.value)} placeholder="Check, finding or licence"/>
            </label>
            <select className="filter-sel" aria-label="Filter by status" value={status} onChange={e => setStatus(e.target.value)}>
              <option value="">All statuses</option>
              {(Object.keys(STATUS) as PostureStatus[]).map(k => <option key={k} value={k}>{STATUS[k].label}</option>)}
            </select>
            <select className="filter-sel" aria-label="Filter by workload" value={workload} onChange={e => setWorkload(e.target.value)}>
              <option value="">All workloads</option>
              {workloads.map(w => <option key={w} value={w}>{w}</option>)}
            </select>
            <ExportDropdown filename="licence-posture.csv" scopeTotal={all.length}
              rows={findings.map(f => ({
                "Check ID": f.id, Check: f.title, Workload: f.workload, Status: meta(f.status).label, Finding: f.detail,
                "Licensed by": f.licensedBy ?? "", "Available with": f.unlockedBy.join(" or "), "Read from": f.readFrom,
                SCuBA: f.baselines.scuba.join(" "), Minimum: f.minimum,
              }))}/>
          </div>
        }>
        {findings.length === 0 ? (
          <EmptyState message="No checks match these filters."/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead>
                <tr>
                  <SortTh label="Check" col="title" sortBy={sortBy} sortDir={sortDir} onSort={onSort}/>
                  <SortTh label="Workload" col="workload" sortBy={sortBy} sortDir={sortDir} onSort={onSort}/>
                  <SortTh label="Status" col="status" sortBy={sortBy} sortDir={sortDir} onSort={onSort}/>
                  <th scope="col">What Vigil365 found</th>
                  <th scope="col">Licence</th>
                </tr>
              </thead>
              <tbody>
                {findings.map(f => (
                  <tr key={f.id}>
                    <td>
                      <button type="button" className="posture-check" onClick={() => setOpen(f)}>{f.title}</button>
                      <span className="posture-id">{f.id}</span>
                    </td>
                    <td className="al-date">{f.workload}</td>
                    <td><Badge label={meta(f.status).label} tone={meta(f.status).tone}/></td>
                    <td className="posture-finding">{f.detail}</td>
                    <td className="al-date">{licenceCell(f)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {(data.upgrades ?? []).length > 0 && (
        <Card title="Options that need another licence">
          <ul className="posture-list">
            {(data.upgrades ?? []).map(g => (
              <li key={g.unlockedBy.join("|")} className="posture-upgrade">
                <span>
                  <strong>{g.unlockedBy[0] ?? "Another licence"}</strong>
                  {g.unlockedBy.length > 1 && <span className="posture-sub">also in {g.unlockedBy.slice(1).join(", ")}</span>}
                </span>
                <span className="posture-muted">{g.checks.map(id => byId.get(id)?.title ?? id).join("; ")}</span>
              </li>
            ))}
          </ul>
        </Card>
      )}

      {open && (
        <DetailModal title={open.title} subtitle={`${open.id} · ${open.workload} · ${meta(open.status).label}`}
          onClose={() => setOpen(null)} portalUrl={open.portalUrl ?? undefined} portalLabel="Open in the admin centre">
          <div className="posture-detail">
            <h3>What Vigil365 found</h3>
            <p>{open.detail}</p>
            <h3>Why it matters</h3>
            <p>{open.whyItMatters}</p>
            <h3>Minimum</h3>
            <p>{open.minimum}</p>
            <h3>How to fix it</h3>
            <ol>{open.fix.map(step => <li key={step}>{step}</li>)}</ol>
            <h3>Licence</h3>
            <p>
              {open.status === "NotLicensed"
                ? `Needs ${open.feature}. Available with ${open.unlockedBy.join(" or ") || "a licence Vigil365 doesn't list yet"}.`
                : `${open.feature}, included in ${open.licensedBy ?? "the tenant's licences"}.`}
              {" "}<a href={open.featureSource} target="_blank" rel="noopener noreferrer">Microsoft's licensing note</a>
            </p>
            <h3>Read from</h3>
            <p>{open.readFrom}{open.baselines.scuba.length ? ` · SCuBA ${open.baselines.scuba.join(", ")}` : ""}</p>
          </div>
        </DetailModal>
      )}
    </div>
  );
}
