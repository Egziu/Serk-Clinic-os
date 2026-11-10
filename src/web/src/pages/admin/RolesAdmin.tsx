import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { SENSITIVITY_LABEL, sensitivityClass, type Catalog, type Organisation, type RoleInfo } from "./adminTypes";

export function RolesAdmin({ catalog, organisations }: { catalog: Catalog; organisations: Organisation[] }) {
  const [tenantId, setTenantId] = useState(organisations[0]?.id ?? "");
  const [roles, setRoles] = useState<RoleInfo[]>([]);
  const [editing, setEditing] = useState<{ base: RoleInfo | null; existing: RoleInfo | null } | null>(null);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback(() => {
    if (tenantId) api.get<RoleInfo[]>(`/api/v1/platform/organisations/${tenantId}/roles`).then((r) => setRoles(r.data)).catch(setError);
  }, [tenantId]);
  useEffect(load, [load]);

  return (
    <section className="panel" aria-labelledby="roles-h">
      <div className="panel-header">
        <h2 id="roles-h">Roles</h2>
        <div className="btn-row">
          <label htmlFor="roles-org" className="inline-label">Organisation</label>
          <select id="roles-org" value={tenantId} onChange={(e) => { setTenantId(e.target.value); setEditing(null); }}>
            {organisations.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </select>
          <button className="btn btn-primary" onClick={() => setEditing({ base: null, existing: null })}>+ New custom role</button>
        </div>
      </div>
      <p className="status-line">Built-in roles are the tested safety baseline and cannot be edited; clone one to customise it for this organisation. Changes apply on each person's next request.</p>
      <ErrorAlert error={error} />
      {editing && (
        <RoleEditor key={editing.existing?.id ?? editing.base?.code ?? "new"} tenantId={tenantId} catalog={catalog} base={editing.base} existing={editing.existing}
          onSaved={() => { setEditing(null); load(); }} onCancel={() => setEditing(null)} />
      )}
      <table aria-label="Roles">
        <thead><tr><th>Role</th><th>Permissions</th><th>In use</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {roles.map((r) => (
            <tr key={r.code}>
              <td><strong>{r.name}</strong> {r.isBuiltIn ? <span className="badge badge-neutral">Built-in</span> : r.isActive ? <span className="badge badge-ok">Custom</span> : <span className="badge badge-neutral">✕ Inactive</span>}<br /><span className="hint">{r.description}</span></td>
              <td><RiskSummary catalog={catalog} codes={r.capabilities} /></td>
              <td>{r.assignedCount}</td>
              <td className="btn-row">
                {r.isBuiltIn
                  ? <button className="btn" onClick={() => setEditing({ base: r, existing: null })} aria-label={`Clone ${r.name}`}>Clone</button>
                  : <button className="btn" onClick={() => setEditing({ base: null, existing: r })} aria-label={`Edit ${r.name}`}>Edit</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function RiskSummary({ catalog, codes }: { catalog: Catalog; codes: string[] }) {
  const infos = catalog.capabilities.filter((c) => codes.includes(c.code));
  const counts = (Object.keys(SENSITIVITY_LABEL) as (keyof typeof SENSITIVITY_LABEL)[]).map((k) => [k, infos.filter((i) => i.sensitivity === k).length] as const).filter(([, n]) => n > 0);
  return (
    <span className="badge-stack">
      <span>{infos.length} permissions</span>
      {counts.map(([k, n]) => <span key={k} className={`badge ${sensitivityClass(k)}`}>{n} {SENSITIVITY_LABEL[k].toLowerCase()}</span>)}
    </span>
  );
}

function RoleEditor({ tenantId, catalog, base, existing, onSaved, onCancel }: {
  tenantId: string; catalog: Catalog; base: RoleInfo | null; existing: RoleInfo | null; onSaved: () => void; onCancel: () => void;
}) {
  const [name, setName] = useState(existing?.name ?? (base ? `${base.name} (custom)` : ""));
  const [description, setDescription] = useState(existing?.description ?? base?.description ?? "");
  const [caps, setCaps] = useState<string[]>(existing?.capabilities ?? base?.capabilities ?? []);
  const [isActive, setIsActive] = useState(existing?.isActive ?? true);
  const [error, setError] = useState<unknown>(null);
  const groups = Array.from(new Set(catalog.capabilities.map((c) => c.group)));
  const toggle = (code: string) => setCaps((cs) => (cs.includes(code) ? cs.filter((c) => c !== code) : [...cs, code]));
  const selected = catalog.capabilities.filter((c) => caps.includes(c.code));
  const mixesDuties = selected.some((c) => c.sensitivity === "Clinical") && selected.some((c) => c.sensitivity === "Administrative");

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      if (existing) await api.put(`/api/v1/platform/roles/${existing.id}`, { name, description, capabilities: caps, isActive, version: existing.version });
      else await api.post("/api/v1/platform/roles", { tenantId, name, description, capabilities: caps });
      onSaved();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <form className="panel role-editor" onSubmit={save} aria-label={existing ? `Edit role ${existing.name}` : "New custom role"}>
      <h3>{existing ? `Edit “${existing.name}”` : base ? `Clone “${base.name}”` : "New custom role"}</h3>
      <ErrorAlert error={error} />
      <div className="grid-2">
        <Field id="re-name" label="Role name"><input id="re-name" value={name} onChange={(e) => setName(e.target.value)} required minLength={2} maxLength={80} /></Field>
        <Field id="re-desc" label="Description" hint="optional"><input id="re-desc" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} /></Field>
      </div>
      {existing && <label className="checkbox field"><input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> Role active (an inactive role grants nothing, even where assigned)</label>}
      {existing && existing.assignedCount > 0 && <p className="alert alert-info">Assigned to {existing.assignedCount} people; changes apply to all of them on their next request.</p>}
      {mixesDuties && <p className="alert alert-warning">⚠ This role combines clinical record access with administration. Prefer separate roles (separation of duties).</p>}
      <div className="cap-groups">
        {groups.map((g) => (
          <fieldset key={g} className="cap-group">
            <legend>{g}</legend>
            {catalog.capabilities.filter((c) => c.group === g).map((c) => (
              <label key={c.code} className="checkbox">
                <input type="checkbox" checked={caps.includes(c.code)} onChange={() => toggle(c.code)} />
                <span>{c.label} <span className={`badge ${sensitivityClass(c.sensitivity)}`}>{SENSITIVITY_LABEL[c.sensitivity]}</span><br /><span className="hint">{c.description}</span></span>
              </label>
            ))}
          </fieldset>
        ))}
      </div>
      <div className="btn-row">
        <button className="btn btn-primary" type="submit" disabled={caps.length === 0}>{existing ? "Save role" : "Create role"}</button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
        <span className="status-line">{caps.length} permissions selected</span>
      </div>
    </form>
  );
}
