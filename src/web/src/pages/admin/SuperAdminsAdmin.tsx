import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { fmtDateTime, type StaffSummary, type SuperAdmin } from "./adminTypes";

/** Grant/revoke system super administration. Both require the acting administrator's own password (step-up). */
export function SuperAdminsAdmin({ currentUserId }: { currentUserId: string }) {
  const [admins, setAdmins] = useState<SuperAdmin[]>([]);
  const [staff, setStaff] = useState<StaffSummary[]>([]);
  const [userId, setUserId] = useState("");
  const [password, setPassword] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = useCallback(() => {
    api.get<SuperAdmin[]>("/api/v1/platform/super-admins").then((r) => setAdmins(r.data)).catch(setError);
    api.get<StaffSummary[]>("/api/v1/platform/staff").then((r) => setStaff(r.data.filter((s) => s.isActive && !s.isPlatformAdmin))).catch(setError);
  }, []);
  useEffect(load, [load]);

  const run = async (path: "grant" | "revoke", target: string, label: string) => {
    setError(null);
    setNote(null);
    try {
      await api.post(`/api/v1/platform/super-admins/${path}`, { userId: target, confirmPassword: password, reason: reason || null });
      setNote(label);
      setPassword(""); setReason(""); setUserId("");
      load();
    } catch (err) {
      setError(err);
    }
  };
  const grant = (e: FormEvent) => { e.preventDefault(); void run("grant", userId, "Super administration granted."); };
  const active = admins.filter((a) => a.isActive);

  return (
    <section className="panel" aria-labelledby="sa-h">
      <h2 id="sa-h">Super administrators</h2>
      <p className="status-line">
        Super administrators control organisations, people, roles and access across the whole system, but have no patient data access of their own.
        Nobody can change their own access; at least one active super administrator always remains. Both actions below need your own password.
      </p>
      <ErrorAlert error={error} />
      {note && <p className="alert alert-info" role="status">{note}</p>}
      <Field id="sa-pass" label="Your password (confirmation)">
        <input id="sa-pass" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
      </Field>
      <Field id="sa-reason" label="Reason" hint="kept in the change log; no patient information">
        <input id="sa-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} />
      </Field>
      <table aria-label="Super administrators">
        <thead><tr><th>Name</th><th>Granted</th><th>Status</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {admins.map((a) => (
            <tr key={a.userId}>
              <td>{a.displayName}<br /><span className="mono muted">{a.username}</span></td>
              <td>{fmtDateTime(a.grantedAt)}{a.grantedBy && <><br /><span className="hint">by {a.grantedBy}</span></>}</td>
              <td>{a.isActive ? <span className="badge badge-warning">★ Active</span> : <span className="badge badge-neutral">Revoked {fmtDateTime(a.revokedAt)}</span>}</td>
              <td>
                {a.isActive && a.userId !== currentUserId && (
                  <button className="btn" disabled={!password || active.length <= 1} onClick={() => run("revoke", a.userId, "Super administration revoked.")} aria-label={`Revoke super administration from ${a.displayName}`}>Revoke</button>
                )}
                {a.userId === currentUserId && <span className="hint">You</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <form onSubmit={grant} aria-label="Grant super administration" className="btn-row field">
        <label htmlFor="sa-user" className="inline-label">Grant to</label>
        <select id="sa-user" value={userId} onChange={(e) => setUserId(e.target.value)} required>
          <option value="">Choose a person…</option>
          {staff.map((s) => <option key={s.id} value={s.id}>{s.displayName} ({s.username})</option>)}
        </select>
        <button className="btn btn-primary" type="submit" disabled={!userId || !password}>Grant super administration</button>
      </form>
    </section>
  );
}
