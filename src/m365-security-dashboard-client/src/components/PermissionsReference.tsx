import React, { useEffect, useState } from "react";
import { CheckCircle2, XCircle, HelpCircle } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { Card, Badge, EmptyState, CopyButton } from "./SharedComponents";

type PermItem = { permission: string; features: string[]; status: "granted" | "missing" | "unknown" };
type PermResponse = { hasRun: boolean; permissions: PermItem[] };

const ENTRA_URL = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade";

/**
 * Live Graph permission reference. Lists every application permission Vigil365
 * needs, which features depend on it, and — inferred from the last collection
 * run — whether the tenant has actually granted it. Turns a denied source from
 * a mystery into a specific "grant this permission" instruction.
 */
export function PermissionsReference({ refreshKey }: { refreshKey: number }) {
  const [data, setData] = useState<PermResponse | null>(null);
  const [err, setErr] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setErr(false);
    apiFetch(`${apiBase}/api/setup/permissions`)
      .then(r => r.ok ? r.json() : Promise.reject())
      .then((d: PermResponse) => { if (!cancelled) setData(d); })
      .catch(() => { if (!cancelled) setErr(true); });
    return () => { cancelled = true; };
  }, [refreshKey]);

  const missing = data?.permissions.filter(p => p.status === "missing").length ?? 0;

  return (
    <Card title="Graph Permissions"
      badge={data ? (missing > 0
        ? <Badge label={`${missing} missing`} tone="error"/>
        : data.hasRun ? <Badge label="All granted" tone="good"/> : <Badge label="No run yet" tone="neutral"/>) : undefined}
      action={<a className="card-link-btn" href={ENTRA_URL} target="_blank" rel="noopener noreferrer">Open Entra app registrations ↗</a>}>
      {err ? (
        <EmptyState message="Could not load the permissions reference"/>
      ) : !data ? (
        <EmptyState message="Loading permissions…"/>
      ) : (
        <>
          <p className="perm-intro">
            Vigil365 uses read-only <strong>application</strong> permissions for unattended collection.
            Grant each in Entra → App registrations → your app → API permissions, then click
            <strong> Grant admin consent</strong>. Status is inferred from the most recent collection run.
          </p>
          <div className="table-wrap">
            <table>
              <thead><tr>
                <th scope="col">Status</th><th scope="col">Permission</th><th scope="col">Powers</th><th scope="col"></th>
              </tr></thead>
              <tbody>
                {data.permissions.map(p => (
                  <tr key={p.permission}>
                    <td>
                      {p.status === "granted"
                        ? <span className="perm-status granted"><CheckCircle2 size={15}/> Granted</span>
                        : p.status === "missing"
                          ? <span className="perm-status missing"><XCircle size={15}/> Missing</span>
                          : <span className="perm-status unknown"><HelpCircle size={15}/> Unknown</span>}
                    </td>
                    <td><code className="perm-code">{p.permission}</code></td>
                    <td className="perm-features">{p.features.join(", ")}</td>
                    <td><CopyButton value={p.permission} label={p.permission} size={12}/></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="perm-note">
            Some tenants also require an Entra directory role (e.g. Security Reader) or a
            product-specific role on the service principal, beyond the API permission.
          </p>
        </>
      )}
    </Card>
  );
}
