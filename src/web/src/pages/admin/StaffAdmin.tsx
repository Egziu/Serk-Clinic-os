import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { AccessEditor } from "./AccessEditor";
import { ChangeList } from "./ChangeLog";
import { fmtDateTime, generateTemporaryPassword, type Assignment, type Catalog, type Change, type Organisation, type StaffDetail, type StaffSummary } from "./adminTypes";

export function StaffAdmin({ catalog, organisations, currentUserId }: { catalog: Catalog; organisations: Organisation[]; currentUserId: string }) {
  const [q, setQ] = useState("");
  const [orgFilter, setOrgFilter] = useState("");
  const [list, setList] = useState<StaffSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(() => {
    const params = new URLSearchParams();
    if (q.trim()) params.set("q", q.trim());
    if (orgFilter) params.set("organisationId", orgFilter);
    api.get<StaffSummary[]>(`/api/v1/platform/staff?${params}`).then((r) => setList(r.data)).catch(setError);
  }, [q, orgFilter]);

  useEffect(() => {
    const t = window.setTimeout(load, 200);
    return () => window.clearTimeout(t);
  }, [load]);

  return (
    <div className="admin-split">
      <section className="panel" aria-labelledby="staff-h">
        <div className="panel-header">
          <h2 id="staff-h">People</h2>
          <button className="btn btn-primary" onClick={() => { setCreating(true); setSelected(null); }}>+ New person</button>
        </div>
        <ErrorAlert error={error} />
        <div className="btn-row field">
          <label className="sr-only" htmlFor="staff-q">Search people</label>
          <input id="staff-q" type="search" placeholder="Search name or username" value={q} onChange={(e) => setQ(e.target.value)} />
          <label className="sr-only" htmlFor="staff-org">Organisation</label>
          <select id="staff-org" value={orgFilter} onChange={(e) => setOrgFilter(e.target.value)}>
            <option value="">All organisations</option>
            {organisations.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </select>
        </div>
        <table aria-label="People">
          <thead><tr><th>Name</th><th>Status</th><th>Access</th></tr></thead>
          <tbody>
            {list.length === 0 && <tr><td colSpan={3} className="muted">No matching people.</td></tr>}
            {list.map((s) => (
              <tr key={s.id} aria-selected={selected === s.id} className={selected === s.id ? "row-selected" : undefined}>
                <td>
                  <button className="link-btn" onClick={() => { setSelected(s.id); setCreating(false); }} aria-label={`Open ${s.displayName}`}>{s.displayName}</button>
                  <br /><span className="mono muted">{s.username}</span>
                </td>
                <td><AccountBadges s={s} /></td>
                <td>{s.activeAssignments} active{s.organisations.length > 0 && <><br /><span className="hint">{s.organisations.join(", ")}</span></>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      <div>
        {creating && <CreatePerson onCreated={(id) => { setCreating(false); setSelected(id); load(); }} onCancel={() => setCreating(false)} />}
        {selected && <PersonDetail key={selected} userId={selected} catalog={catalog} organisations={organisations} isSelf={selected === currentUserId} onChanged={load} />}
        {!creating && !selected && <p className="panel muted">Select a person to manage their account and access, or create a new person.</p>}
      </div>
    </div>
  );
}

function AccountBadges({ s }: { s: StaffSummary }) {
  return (
    <span className="badge-stack">
      {s.isActive ? <span className="badge badge-ok">✓ Active</span> : <span className="badge badge-neutral">✕ Deactivated</span>}
      {s.isPlatformAdmin && <span className="badge badge-warning">★ Super admin</span>}
      {s.isLocked && <span className="badge badge-danger">🔒 Locked</span>}
      {s.mustChangePassword && <span className="badge badge-info">Password change pending</span>}
    </span>
  );
}

function CreatePerson({ onCreated, onCancel }: { onCreated: (id: string) => void; onCancel: () => void }) {
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState(generateTemporaryPassword);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api.post<StaffDetail>("/api/v1/platform/staff", { username, displayName, temporaryPassword: password });
      onCreated(r.data.summary.id);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };
  return (
    <form className="panel" onSubmit={submit} aria-label="New person">
      <h2>New person</h2>
      <ErrorAlert error={error} />
      <Field id="np-name" label="Full name"><input id="np-name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required maxLength={200} /></Field>
      <Field id="np-user" label="Username" hint="lowercase letters, digits, . _ -">
        <input id="np-user" value={username} onChange={(e) => setUsername(e.target.value.toLowerCase())} required pattern="[a-z0-9][a-z0-9._\-]{2,63}" autoComplete="off" />
      </Field>
      <Field id="np-pass" label="Temporary password" hint="share securely; must be changed at first sign-in">
        <div className="btn-row">
          <input id="np-pass" className="mono" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={12} autoComplete="off" />
          <button type="button" className="btn" onClick={() => setPassword(generateTemporaryPassword())}>Generate</button>
        </div>
      </Field>
      <div className="btn-row">
        <button className="btn btn-primary" type="submit" disabled={busy}>{busy ? "Creating…" : "Create person"}</button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

function PersonDetail({ userId, catalog, organisations, isSelf, onChanged }: { userId: string; catalog: Catalog; organisations: Organisation[]; isSelf: boolean; onChanged: () => void }) {
  const [detail, setDetail] = useState<StaffDetail | null>(null);
  const [changes, setChanges] = useState<Change[]>([]);
  const [editing, setEditing] = useState<Assignment | "new" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const [tempPassword, setTempPassword] = useState<string | null>(null);

  const load = useCallback(() => {
    api.get<StaffDetail>(`/api/v1/platform/staff/${userId}`).then((r) => setDetail(r.data)).catch(setError);
    api.get<Change[]>(`/api/v1/platform/changes?targetId=${userId}&limit=30`).then((r) => setChanges(r.data)).catch(() => setChanges([]));
  }, [userId]);
  useEffect(load, [load]);

  const act = async (label: string, run: () => Promise<unknown>) => {
    setError(null);
    setNote(null);
    try {
      await run();
      setNote(label);
      load();
      onChanged();
    } catch (err) {
      setError(err);
    }
  };

  if (!detail) return <section className="panel"><ErrorAlert error={error} /><p className="muted">Loading…</p></section>;
  const s = detail.summary;

  const toggleActive = () => {
    const reason = window.prompt(s.isActive ? `Reason for deactivating ${s.displayName}? (Their sessions end immediately. Do not include patient information.)` : `Reason for reactivating ${s.displayName}?`);
    if (reason === null) return;
    void act(s.isActive ? "Account deactivated; sessions ended." : "Account reactivated.", () => api.post(`/api/v1/platform/staff/${userId}/active`, { isActive: !s.isActive, reason }));
  };
  const resetPassword = () => {
    const pw = generateTemporaryPassword();
    if (!window.confirm(`Reset ${s.displayName}'s password? Their sessions end and they must choose a new password at next sign-in.`)) return;
    void act("Temporary password set.", async () => {
      await api.post(`/api/v1/platform/staff/${userId}/reset-password`, { temporaryPassword: pw });
      setTempPassword(pw);
    });
  };

  return (
    <section className="panel" aria-labelledby="person-h">
      <div className="panel-header">
        <div>
          <h2 id="person-h">{s.displayName}</h2>
          <span className="mono muted">{s.username}</span> · <AccountBadges s={s} />
        </div>
      </div>
      <p className="status-line">
        Created {fmtDateTime(detail.createdAt)} · Last sign-in {fmtDateTime(s.lastLoginAt)} · Password changed {fmtDateTime(detail.passwordChangedAt)} · {detail.activeSessions} active session{detail.activeSessions === 1 ? "" : "s"}
      </p>
      <ErrorAlert error={error} />
      {note && <p className="alert alert-info" role="status">{note}</p>}
      {tempPassword && (
        <p className="alert alert-warning" role="status">
          Temporary password for {s.username}: <strong className="mono">{tempPassword}</strong> — share it securely; it is shown only once.
        </p>
      )}
      {isSelf ? (
        <p className="alert alert-info">This is your own account. Your access can only be changed by another super administrator; change your password under “My account”.</p>
      ) : (
        <div className="btn-row field" role="group" aria-label="Account actions">
          <button className="btn" onClick={toggleActive}>{s.isActive ? "Deactivate account" : "Reactivate account"}</button>
          <button className="btn" onClick={resetPassword}>Reset password</button>
          {s.isLocked && <button className="btn" onClick={() => act("Account unlocked.", () => api.post(`/api/v1/platform/staff/${userId}/unlock`))}>Unlock</button>}
          <button className="btn" onClick={() => act("All sessions ended.", () => api.post(`/api/v1/platform/staff/${userId}/revoke-sessions`))} disabled={detail.activeSessions === 0}>End all sessions</button>
        </div>
      )}

      <h3>Access</h3>
      <table aria-label={`Access of ${s.displayName}`}>
        <thead><tr><th>Facility</th><th>Roles and overrides</th><th>Valid</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {detail.assignments.length === 0 && <tr><td colSpan={4} className="muted">No access to any facility.</td></tr>}
          {detail.assignments.map((a) => (
            <tr key={a.id}>
              <td>{a.facilityName}<br /><span className="hint">{a.tenantName}</span></td>
              <td>
                {a.roles.map((r) => catalog.builtInRoles.find((b) => b.code === r)?.name ?? r).join(", ")}
                {a.grantedCapabilities.length > 0 && <><br /><span className="hint">+ {a.grantedCapabilities.join(", ")}</span></>}
                {a.revokedCapabilities.length > 0 && <><br /><span className="hint">− {a.revokedCapabilities.join(", ")}</span></>}
                <br /><span className="hint">{a.effectiveCapabilities.length} effective permissions</span>
              </td>
              <td>
                {a.isCurrentlyValid ? <span className="badge badge-ok">✓ In effect</span> : <span className="badge badge-neutral">{a.isActive ? "Not in effect now" : "✕ Deactivated"}</span>}
                <br /><span className="hint">{a.validFrom.slice(0, 10)} → {a.validUntil ? a.validUntil.slice(0, 10) : "no end"}</span>
              </td>
              <td>{!isSelf && <button className="btn" onClick={() => setEditing(a)} aria-label={`Edit access at ${a.facilityName}`}>Edit</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {!isSelf && editing === null && <div className="btn-row field"><button className="btn btn-primary" onClick={() => setEditing("new")}>+ Give access to a facility</button></div>}
      {editing !== null && (
        <AccessEditor userId={userId} catalog={catalog} organisations={organisations} existing={editing === "new" ? null : editing}
          onSaved={() => { setEditing(null); setNote("Access saved. It applies on the person's next request."); load(); onChanged(); }}
          onCancel={() => setEditing(null)} />
      )}

      {!isSelf && <PractitionerForm userId={userId} organisations={organisations} profiles={detail.practitionerProfiles} onSaved={() => { setNote("Practitioner profile saved."); load(); }} />}

      <h3>History</h3>
      <ChangeList changes={changes} />
    </section>
  );
}

function PractitionerForm({ userId, organisations, profiles, onSaved }: { userId: string; organisations: Organisation[]; profiles: StaffDetail["practitionerProfiles"]; onSaved: () => void }) {
  const [tenantId, setTenantId] = useState(profiles[0]?.tenantId ?? organisations[0]?.id ?? "");
  return (
    <details className="field">
      <summary>Practitioner profile ({profiles.length ? profiles.map((p) => `${p.professionalTitle}, ${p.tenantName}`).join("; ") : "none"})</summary>
      <p className="status-line">Needed for signing notes and charting: signatures are refused without a profile with a valid licence in that organisation.</p>
      <Field id="pp-org" label="Organisation">
        <select id="pp-org" value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
          {organisations.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
        </select>
      </Field>
      {/* Keyed by organisation so the fields reset to that organisation's profile. */}
      <PractitionerFields key={tenantId} userId={userId} tenantId={tenantId} profile={profiles.find((p) => p.tenantId === tenantId)} onSaved={onSaved} />
    </details>
  );
}

function PractitionerFields({ userId, tenantId, profile, onSaved }: { userId: string; tenantId: string; profile: StaffDetail["practitionerProfiles"][number] | undefined; onSaved: () => void }) {
  const [title, setTitle] = useState(profile?.professionalTitle ?? "");
  const [specialty, setSpecialty] = useState(profile?.specialty ?? "");
  const [license, setLicense] = useState(profile?.licenseNumber ?? "");
  const [expires, setExpires] = useState(profile?.licenseExpiresOn ?? "");
  const [error, setError] = useState<unknown>(null);

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await api.put(`/api/v1/platform/staff/${userId}/practitioner`, { tenantId, professionalTitle: title, specialty, licenseNumber: license, licenseExpiresOn: expires || null });
      onSaved();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <form onSubmit={save} aria-label="Practitioner profile">
      <ErrorAlert error={error} />
      <div className="grid-2">
        <Field id="pp-title" label="Professional title"><input id="pp-title" value={title} onChange={(e) => setTitle(e.target.value)} placeholder="e.g. MD, DDS, RN" required /></Field>
        <Field id="pp-spec" label="Specialty"><input id="pp-spec" value={specialty} onChange={(e) => setSpecialty(e.target.value)} required /></Field>
        <Field id="pp-lic" label="Licence number"><input id="pp-lic" value={license} onChange={(e) => setLicense(e.target.value)} required /></Field>
        <Field id="pp-exp" label="Licence expires (Gregorian)"><input id="pp-exp" type="date" value={expires} onChange={(e) => setExpires(e.target.value)} required /></Field>
      </div>
      <button className="btn btn-primary" type="submit">Save practitioner profile</button>
    </form>
  );
}
