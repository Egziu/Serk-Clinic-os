import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { SENSITIVITY_LABEL, sensitivityClass, type Assignment, type Catalog, type Organisation, type Preview, type RoleInfo } from "./adminTypes";

type Override = "inherit" | "grant" | "revoke";

/**
 * Create or edit one assignment (person ↔ facility): roles, per-permission overrides (grant / remove on top of the roles),
 * validity window and reason. The effective permission set is previewed live from the server, with risk warnings.
 */
export function AccessEditor({ userId, catalog, organisations, existing, onSaved, onCancel }: {
  userId: string;
  catalog: Catalog;
  organisations: Organisation[];
  existing: Assignment | null;
  onSaved: () => void;
  onCancel: () => void;
}) {
  const facilities = organisations.flatMap((o) => o.facilities.map((f) => ({ ...f, tenantName: o.name })));
  const [facilityId, setFacilityId] = useState(existing?.facilityId ?? facilities[0]?.id ?? "");
  const tenantId = facilities.find((f) => f.id === facilityId)?.tenantId ?? "";
  const [roles, setRoles] = useState<string[]>(existing?.roles ?? []);
  const [overrides, setOverrides] = useState<Record<string, Override>>(() => {
    const o: Record<string, Override> = {};
    existing?.grantedCapabilities.forEach((c) => (o[c] = "grant"));
    existing?.revokedCapabilities.forEach((c) => (o[c] = "revoke"));
    return o;
  });
  const [validFrom, setValidFrom] = useState(existing ? existing.validFrom.slice(0, 10) : "");
  const [validUntil, setValidUntil] = useState(existing?.validUntil ? existing.validUntil.slice(0, 10) : "");
  const [isActive, setIsActive] = useState(existing?.isActive ?? true);
  const [reason, setReason] = useState("");
  const [customRoles, setCustomRoles] = useState<RoleInfo[]>([]);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!tenantId) return;
    api.get<RoleInfo[]>(`/api/v1/platform/organisations/${tenantId}/roles`)
      .then((r) => setCustomRoles(r.data.filter((x) => !x.isBuiltIn && x.isActive)))
      .catch(setError);
  }, [tenantId]);

  const granted = useMemo(() => Object.keys(overrides).filter((c) => overrides[c] === "grant").sort(), [overrides]);
  const revoked = useMemo(() => Object.keys(overrides).filter((c) => overrides[c] === "revoke").sort(), [overrides]);

  // Live preview of the effective permission set (debounced).
  useEffect(() => {
    if (!tenantId) return;
    const timer = window.setTimeout(() => {
      api.post<Preview>("/api/v1/platform/access-preview", { tenantId, userId, roles, grantedCapabilities: granted, revokedCapabilities: revoked })
        .then((r) => setPreview(r.data))
        .catch(() => setPreview(null));
    }, 200);
    return () => window.clearTimeout(timer);
  }, [tenantId, userId, roles, granted, revoked]);

  const allRoles = [...catalog.builtInRoles, ...customRoles];
  const fromRoles = new Set(allRoles.filter((r) => roles.includes(r.code)).flatMap((r) => r.capabilities));
  const effective = new Set(preview?.effective.map((c) => c.code) ?? []);
  const groups = Array.from(new Set(catalog.capabilities.map((c) => c.group)));
  const toggleRole = (code: string) => setRoles((rs) => (rs.includes(code) ? rs.filter((r) => r !== code) : [...rs, code]));

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    const body = {
      facilityId,
      roles,
      grantedCapabilities: granted,
      revokedCapabilities: revoked,
      validFrom: validFrom ? new Date(`${validFrom}T00:00:00Z`).toISOString() : null,
      validUntil: validUntil ? new Date(`${validUntil}T00:00:00Z`).toISOString() : null,
      isActive,
      version: existing?.version ?? null,
      reason: reason || null,
    };
    try {
      if (existing) await api.put(`/api/v1/platform/assignments/${existing.id}`, body);
      else await api.post(`/api/v1/platform/staff/${userId}/assignments`, body);
      onSaved();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="panel access-editor" onSubmit={save} aria-label={existing ? "Edit assignment" : "New assignment"}>
      <h3>{existing ? `Edit access at ${existing.facilityName}` : "Give access to a facility"}</h3>
      <ErrorAlert error={error} />
      <div className="grid-2">
        <div>
          <Field id="ae-facility" label="Facility">
            <select id="ae-facility" value={facilityId} disabled={!!existing} onChange={(e) => { setFacilityId(e.target.value); setRoles([]); }}>
              {facilities.map((f) => <option key={f.id} value={f.id}>{f.name} — {f.tenantName}</option>)}
            </select>
          </Field>
          <fieldset className="field">
            <legend>Roles</legend>
            {allRoles.map((r) => (
              <label key={r.code} className="checkbox">
                <input type="checkbox" checked={roles.includes(r.code)} onChange={() => toggleRole(r.code)} />
                <span><strong>{r.name}</strong>{r.isBuiltIn ? "" : " (custom)"} <span className="hint">— {r.description || `${r.capabilities.length} permissions`}</span></span>
              </label>
            ))}
          </fieldset>
          <div className="btn-row">
            <Field id="ae-from" label="Starts" hint="optional">
              <input id="ae-from" type="date" value={validFrom} onChange={(e) => setValidFrom(e.target.value)} />
            </Field>
            <Field id="ae-until" label="Ends" hint="optional">
              <input id="ae-until" type="date" value={validUntil} min={validFrom || undefined} onChange={(e) => setValidUntil(e.target.value)} />
            </Field>
          </div>
          <label className="checkbox field"><input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> Assignment active</label>
          <Field id="ae-reason" label="Reason for the change" hint="kept in the change log; no patient information">
            <input id="ae-reason" type="text" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
        </div>
        <div>
          <h4>Effective access</h4>
          {preview ? (
            <>
              <p className="status-line">
                {preview.effective.length} permissions ·{" "}
                {(Object.keys(SENSITIVITY_LABEL) as (keyof typeof SENSITIVITY_LABEL)[]).filter((k) => preview.bySensitivity[k] > 0)
                  .map((k) => `${preview.bySensitivity[k]} ${SENSITIVITY_LABEL[k].toLowerCase()}`).join(" · ") || "none"}
              </p>
              {preview.warnings.map((w) => <p key={w} className="alert alert-warning">⚠ {w}</p>)}
              <ul className="cap-chips" aria-label="Effective permissions">
                {preview.effective.map((c) => <li key={c.code}><span className={`badge ${sensitivityClass(c.sensitivity)}`}>{c.label}</span></li>)}
              </ul>
            </>
          ) : <p className="muted">Choose a facility and roles to see the resulting access.</p>}
        </div>
      </div>

      <details className="field">
        <summary>Fine-tune individual permissions ({granted.length} added, {revoked.length} removed)</summary>
        <p className="status-line">“From roles” follows the selected roles. “Add” grants a permission the roles lack; “Remove” takes one away even if a role grants it.</p>
        {groups.map((g) => (
          <table key={g} className="cap-table">
            <caption>{g}</caption>
            <thead><tr><th scope="col">Permission</th><th scope="col">Sensitivity</th><th scope="col">From roles</th><th scope="col">Override</th><th scope="col">Result</th></tr></thead>
            <tbody>
              {catalog.capabilities.filter((c) => c.group === g).map((c) => (
                <tr key={c.code}>
                  <th scope="row"><span>{c.label}</span><br /><span className="hint">{c.description}</span></th>
                  <td><span className={`badge ${sensitivityClass(c.sensitivity)}`}>{SENSITIVITY_LABEL[c.sensitivity]}</span></td>
                  <td>{fromRoles.has(c.code) ? "Yes" : "No"}</td>
                  <td>
                    <label className="sr-only" htmlFor={`ov-${c.code}`}>Override for {c.label}</label>
                    <select id={`ov-${c.code}`} value={overrides[c.code] ?? "inherit"}
                      onChange={(e) => setOverrides((o) => ({ ...o, [c.code]: e.target.value as Override }))}>
                      <option value="inherit">From roles</option>
                      <option value="grant">Add</option>
                      <option value="revoke">Remove</option>
                    </select>
                  </td>
                  <td>{effective.has(c.code) ? <span className="badge badge-ok">✓ Allowed</span> : <span className="badge badge-neutral">— Not allowed</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
      </details>

      <div className="btn-row">
        <button className="btn btn-primary" type="submit" disabled={busy || !facilityId || roles.length === 0}>{busy ? "Saving…" : existing ? "Save access" : "Give access"}</button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
