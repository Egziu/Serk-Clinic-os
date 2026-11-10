import { useState, type FormEvent } from "react";
import { api } from "../api/client";
import type { Session } from "../api/types";
import { ErrorAlert, Field } from "../components/common";

/**
 * Change own password. Also used full-screen for the forced change after an administrator created or reset the account:
 * until it is done the server grants no access at all.
 */
export function AccountPage({ session, forced, onSession }: { session: Session; forced: boolean; onSession: (s: Session) => void }) {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const mismatch = confirm.length > 0 && confirm !== next;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (mismatch) return;
    setBusy(true);
    setError(null);
    try {
      const r = await api.post<Session>("/api/v1/me/password", { currentPassword: current, newPassword: next });
      setCurrent(""); setNext(""); setConfirm("");
      setDone(true);
      onSession(r.data);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="panel narrow" aria-labelledby="account-h">
      <h1 id="account-h">{forced ? "Choose your own password" : "My account"}</h1>
      {forced
        ? <p className="alert alert-warning">Signed in as <strong>{session.displayName}</strong> with a temporary password. Choose a new password to continue; nothing else is available until you do.</p>
        : <p className="muted">Signed in as <strong>{session.displayName}</strong>. Changing your password signs you out on your other devices.</p>}
      <ErrorAlert error={error} />
      {done && !forced && <p className="alert alert-info" role="status">Password changed.</p>}
      <form onSubmit={submit} aria-label="Change password">
        <Field id="pw-current" label={forced ? "Temporary password" : "Current password"}>
          <input id="pw-current" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
        </Field>
        <Field id="pw-new" label="New password" hint="at least 12 characters, not containing your username">
          <input id="pw-new" type="password" autoComplete="new-password" minLength={12} value={next} onChange={(e) => setNext(e.target.value)} required />
        </Field>
        <Field id="pw-confirm" label="Repeat new password" error={mismatch ? ["The passwords do not match."] : undefined}>
          <input id="pw-confirm" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required aria-invalid={mismatch} aria-describedby={mismatch ? "pw-confirm-error" : undefined} />
        </Field>
        <button className="btn btn-primary" type="submit" disabled={busy || mismatch}>{busy ? "Saving…" : "Change password"}</button>
      </form>
    </section>
  );
}
