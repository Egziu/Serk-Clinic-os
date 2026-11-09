import { useState, type FormEvent } from "react";
import { api, setCsrfToken } from "../api/client";
import type { Session } from "../api/types";
import { ErrorAlert, Field } from "../components/common";
import { t } from "../i18n";

export function LoginPage({ session, onSession }: { session: Session | null; onSession: (s: Session | null) => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api.post<Session>("/api/v1/auth/login", { username, password });
      setCsrfToken(r.data.csrfToken);
      onSession(r.data);
    } catch (err) {
      setError(err);
      setPassword("");
    } finally {
      setBusy(false);
    }
  };

  const choose = async (affiliationId: string) => {
    setError(null);
    try {
      const r = await api.post<Session>("/api/v1/auth/session/affiliation", { affiliationId });
      onSession(r.data);
    } catch (err) {
      setError(err);
    }
  };

  return (
    <div className="login-wrap">
      <div className="synthetic-banner" role="note">{t("syntheticBanner")}</div>
      <main id="main" className="panel">
        <h1>{t("appName")}</h1>
        <p className="muted">One secure clinical foundation. Every specialty. Beautifully simple care.</p>
        <ErrorAlert error={error} />
        {session && session.activeAffiliationId === null ? (
          <section aria-labelledby="facility-heading">
            <h2 id="facility-heading">{t("chooseFacility")}</h2>
            {session.affiliations.length === 0 && <div className="alert alert-warning">This account has no active clinic affiliation and cannot access patient data.</div>}
            <ul>
              {session.affiliations.map((a) => (
                <li key={a.id} className="field">
                  <button className="btn" onClick={() => choose(a.id)}>
                    {a.facilityName} — {a.tenantName} ({a.roles.join(", ")})
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ) : (
          <form onSubmit={submit} aria-label="Sign in">
            <Field id="username" label={t("username")}>
              <input id="username" type="text" autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required />
            </Field>
            <Field id="password" label={t("password")}>
              <input id="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
            </Field>
            <button className="btn btn-primary" type="submit" disabled={busy}>
              {busy ? "Signing in…" : t("signIn")}
            </button>
          </form>
        )}
      </main>
    </div>
  );
}
