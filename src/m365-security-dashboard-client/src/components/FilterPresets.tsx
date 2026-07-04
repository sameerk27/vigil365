import React, { useState } from "react";
import { Star, X } from "lucide-react";
import { showToast } from "../services/toast";

export function FilterPresets({ pageKey, filters, onLoad }: {
  pageKey: string;
  filters: Record<string, string>;
  onLoad: (f: Record<string, string>) => void;
}) {
  const key = `fp_${pageKey}`;
  const [presets, setPresets] = useState<{ name: string; filters: Record<string, string> }[]>(() => {
    try { return JSON.parse(localStorage.getItem(key) ?? "[]"); } catch { return []; }
  });
  const [saving, setSaving] = useState(false);
  const [name, setName] = useState("");

  const save = () => {
    if (!name.trim()) return;
    const next = [...presets.filter(p => p.name !== name.trim()), { name: name.trim(), filters }];
    setPresets(next);
    localStorage.setItem(key, JSON.stringify(next));
    showToast(`Filter preset "${name.trim()}" saved`);
    setSaving(false);
    setName("");
  };

  const remove = (n: string) => {
    const next = presets.filter(p => p.name !== n);
    setPresets(next);
    localStorage.setItem(key, JSON.stringify(next));
  };

  return (
    <div style={{ display:"flex", alignItems:"center", gap:6, flexWrap:"wrap" }}>
      {presets.length > 0 && (
        <select className="filter-sel" onChange={e => { const p = presets.find(x => x.name === e.target.value); if (p) { onLoad(p.filters); e.target.value = ""; } }}>
          <option value="">Load preset…</option>
          {presets.map(p => <option key={p.name} value={p.name}>{p.name}</option>)}
        </select>
      )}
      {saving ? (
        <>
          <input value={name} onChange={e => setName(e.target.value)} onKeyDown={e => e.key === "Enter" && save()}
            placeholder="Preset name…" className="filter-sel" style={{ minWidth:120 }} autoFocus />
          <button className="btn-apply" style={{ padding:"7px 14px" }} onClick={save}>Save</button>
          <button className="btn-export" aria-label="Cancel saving preset" onClick={() => setSaving(false)}><X size={13}/></button>
        </>
      ) : (
        <button className="btn-export" onClick={() => setSaving(true)}><Star size={12} /> Save filter</button>
      )}
      {presets.map(p => (
        <span key={p.name} className="preset-chip">
          {p.name}
          <button onClick={() => remove(p.name)} aria-label={`Remove preset ${p.name}`}
            style={{ background:"none", border:"none", cursor:"pointer", color:"var(--color-faint)", padding:"0 2px", display:"inline-flex", alignItems:"center" }}><X size={11}/></button>
        </span>
      ))}
    </div>
  );
}
