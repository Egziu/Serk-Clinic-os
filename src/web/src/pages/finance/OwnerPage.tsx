import { useCallback, useEffect, useState } from "react";
import { api } from "../../api/client";
import { ErrorAlert } from "../../components/common";
import { CheckList, CloseFigures } from "./CloseFigures";
import { when, type DailyClose, type Delivery, type Subscription } from "./financeTypes";

/** Owner reports: locked daily closes (aggregates only), digest recipients and delivery evidence. */
export function OwnerPage({ canManage }: { canManage: boolean }) {
  const [reports, setReports] = useState<DailyClose[]>([]);
  const [subs, setSubs] = useState<Subscription[]>([]);
  const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [challenge, setChallenge] = useState<{ channel: string; challengeCode: string; instructions: string; expiresAt: string } | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = useCallback(() => {
    api.get<DailyClose[]>("/api/v1/owner/reports/daily").then((r) => setReports(r.data)).catch(setError);
    api.get<Delivery[]>("/api/v1/owner/deliveries").then((r) => setDeliveries(r.data)).catch(setError);
    if (canManage) api.get<Subscription[]>("/api/v1/owner/subscriptions").then((r) => setSubs(r.data)).catch(setError);
  }, [canManage]);
  useEffect(load, [load]);
  const run = async (label: string | null, action: () => Promise<unknown>) => {
    setError(null);
    setNote(null);
    try {
      await action();
      if (label) setNote(label);
    } catch (err) {
      setError(err);
    }
    load();
  };

  return (
    <>
      <div className="page-header">
        <div>
          <h1>Owner reports</h1>
          <p>Approved and locked daily closes. Totals only — no patient information appears here or in any digest.</p>
        </div>
      </div>
      <ErrorAlert error={error} />
      {note && <p className="alert alert-info" role="status">{note}</p>}
      {reports.length === 0 && <p className="panel muted">No business day has been locked yet. Reports appear here once finance submits a day and a second person approves it.</p>}
      {reports.map((r, i) => (
        <section className="panel" key={r.id} aria-label={`Daily close ${r.serial}`}>
          <div className="panel-header">
            <h2>{r.businessDate} <span className="mono muted">{r.serial}</span></h2>
            <span>{r.fullyReconciled ? <span className="badge badge-ok">✓ Locked · fully reconciled</span> : <span className="badge badge-warning">✓ Locked · with open items</span>}</span>
          </div>
          <CloseFigures s={r.snapshot} />
          <details open={i === 0}>
            <summary>Checks, exceptions and digest text</summary>
            <CheckList checks={r.checks} />
            <pre className="digest" aria-label={`Digest text for ${r.serial}`}>{r.digest}</pre>
            <p className="status-line">Locked {when(r.decidedAt)} · snapshot <span className="mono">{r.snapshotSha256}</span></p>
          </details>
        </section>
      ))}

      {canManage && (
        <section className="panel" aria-labelledby="recipients-h">
          <h2 id="recipients-h">Digest recipients</h2>
          <p className="status-line">
            A recipient is added only by proving control of a private chat or mailbox with a one-time code. This demo uses a sandbox channel:
            nothing is actually sent to Telegram or e-mail until a real connector is approved and configured.
          </p>
          <div className="btn-row field">
            {(["Telegram", "Email"] as const).map((ch) => (
              <button key={ch} className="btn" onClick={() => run(null, async () => setChallenge((await api.post<{ channel: string; challengeCode: string; instructions: string; expiresAt: string }>("/api/v1/owner/subscriptions", { channel: ch })).data))}>Add {ch} recipient</button>
            ))}
          </div>
          {challenge && (
            <p className="alert alert-warning" role="status">
              {challenge.channel} verification code: <strong className="mono">{challenge.challengeCode}</strong> — {challenge.instructions} (expires {when(challenge.expiresAt)})
            </p>
          )}
          <table aria-label="Digest recipients">
            <thead><tr><th>Channel</th><th>Address</th><th>Status</th><th><span className="sr-only">Action</span></th></tr></thead>
            <tbody>
              {subs.length === 0 && <tr><td colSpan={4} className="muted">No recipients yet.</td></tr>}
              {subs.map((s) => (
                <tr key={s.id}>
                  <td>{s.channel}</td><td className="mono">{s.maskedAddress ?? "—"}</td>
                  <td>{s.status === "Verified" ? <span className="badge badge-ok">✓ Verified</span> : <span className="badge badge-warning">… Waiting for code</span>}</td>
                  <td><button className="btn" onClick={() => run("Recipient removed.", () => api.post(`/api/v1/owner/subscriptions/${s.id}/revoke`))} aria-label={`Remove ${s.channel} recipient`}>Remove</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      <section className="panel" aria-labelledby="deliveries-h">
        <h2 id="deliveries-h">Digest deliveries</h2>
        <p className="status-line">“Accepted” means the channel provider accepted the message; it does not prove that anyone read it.</p>
        <table aria-label="Digest deliveries">
          <thead><tr><th>Close</th><th>Channel</th><th>Status</th><th>Attempts</th><th>Updated</th><th><span className="sr-only">Action</span></th></tr></thead>
          <tbody>
            {deliveries.length === 0 && <tr><td colSpan={6} className="muted">No digests have been queued.</td></tr>}
            {deliveries.map((d) => (
              <tr key={d.id}>
                <td className="mono">{d.closeSerial}</td><td>{d.channel}</td>
                <td>{d.status === "ProviderAccepted" ? <span className="badge badge-ok">✓ Accepted by provider</span> : d.status === "DeadLetter" ? <span className="badge badge-danger">✕ Failed — needs action</span> : <span className="badge badge-warning">… {d.status === "Queued" ? "Queued" : "Retry pending"}</span>}
                  {d.lastError && <><br /><span className="hint">{d.lastError}</span></>}</td>
                <td>{d.attempts}</td><td>{when(d.updatedAt)}</td>
                <td>{canManage && d.status !== "ProviderAccepted" && <button className="btn" onClick={() => run("Delivery retried.", () => api.post(`/api/v1/owner/deliveries/${d.id}/retry`))} aria-label={`Retry delivery for ${d.closeSerial}`}>Retry</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </>
  );
}
