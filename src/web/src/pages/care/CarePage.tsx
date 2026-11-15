import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";
import { kindLabel, langLabel, type Campaign, type CareMessage, type CareSummary, type CareTask, type Reply, type Sandbox, type Template, type Tip } from "./careTypes";

type Tab = "desk" | "messages" | "tips" | "campaigns" | "templates" | "overview";

/** Care+: the follow-up desk, patient messages, clinician-approved health tips and campaigns, message templates. */
export function CarePage({ can }: { can: (c: string) => boolean }) {
  const tabs: [Tab, string, boolean][] = [
    ["desk", "Care desk", can("care.desk")],
    ["messages", "Messages", can("care.desk")],
    ["tips", "Health tips", can("care.tips.write")],
    ["campaigns", "Campaigns", can("care.manage")],
    ["templates", "Message templates", can("care.manage")],
    ["overview", "Overview", can("care.manage")],
  ];
  const visible = tabs.filter((t) => t[2]);
  const [tab, setTab] = useState<Tab>(visible[0]?.[0] ?? "desk");
  const [sandbox, setSandbox] = useState<Sandbox | null>(null);
  useEffect(() => { api.get<Sandbox>("/api/v1/care/sandbox").then((r) => setSandbox(r.data)).catch(() => undefined); }, []);
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Care+</h1>
          <p>Thoughtful follow-up for every appointment: confirmations, reminders, after-visit check-ins and health tips — by SMS or Telegram, only for patients who agreed.</p>
        </div>
        {sandbox && (
          <div className="btn-row">
            <span className={`badge ${sandbox.telegramLive ? "badge-ok" : "badge-neutral"}`}>Telegram: {sandbox.telegramLive ? "live" : "sandbox"}</span>
            <span className="badge badge-neutral">SMS: sandbox</span>
          </div>
        )}
      </div>
      {sandbox && !sandbox.telegramLive && (
        <p className="alert alert-info small">Sandbox mode: messages are prepared and recorded here but nothing is sent to patients' phones until the owner connects an SMS gateway or Telegram bot.</p>
      )}
      <div className="tabs" role="tablist" aria-label="Care+">
        {visible.map(([id, label]) => <button key={id} role="tab" id={`ctab-${id}`} aria-selected={tab === id} aria-controls={`cpanel-${id}`} onClick={() => setTab(id)}>{label}</button>)}
      </div>
      <div role="tabpanel" id={`cpanel-${tab}`} aria-labelledby={`ctab-${tab}`}>
        {tab === "desk" && <Desk />}
        {tab === "messages" && <Messages canRetry={can("care.manage")} simulation={sandbox?.simulation ?? false} />}
        {tab === "tips" && <Tips canApprove={can("care.tips.approve")} />}
        {tab === "campaigns" && <Campaigns />}
        {tab === "templates" && <Templates />}
        {tab === "overview" && <Overview />}
      </div>
    </>
  );
}

function Desk() {
  const [open, setOpen] = useState<CareTask[] | null>(null);
  const [done, setDone] = useState<CareTask[] | null>(null);
  const [replies, setReplies] = useState<Reply[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0);
  useEffect(() => {
    api.get<CareTask[]>("/api/v1/care/tasks?open=true").then((r) => setOpen(r.data)).catch(setError);
    api.get<CareTask[]>("/api/v1/care/tasks?open=false").then((r) => setDone(r.data)).catch(setError);
    api.get<Reply[]>("/api/v1/care/replies").then((r) => setReplies(r.data)).catch(setError);
  }, [reload]);
  return (
    <div className="grid-2">
      <section aria-labelledby="desk-open">
        <h2 id="desk-open">Patients to contact {open && <span className="badge badge-neutral">{open.length}</span>}</h2>
        <ErrorAlert error={error} />
        {open?.length === 0 && <p className="all-clear"><Icon name="check" /> Everyone has been looked after.</p>}
        {open?.map((t) => <TaskCard key={t.id} task={t} onDone={() => setReload((k) => k + 1)} />)}
        {done && done.length > 0 && (
          <details className="panel">
            <summary>Recently completed ({done.length})</summary>
            <ul className="timeline">{done.map((t) => <li key={t.id}><strong>{t.patientName}</strong> · {t.summary}<div className="small muted">{t.outcome} — {t.doneAt && new Date(t.doneAt).toLocaleString()}</div></li>)}</ul>
          </details>
        )}
      </section>
      <section className="panel" aria-labelledby="desk-replies">
        <h2 id="desk-replies">Latest patient replies</h2>
        {replies?.length === 0 && <p className="muted">No replies in the last 30 days.</p>}
        <ul className="timeline">
          {replies?.map((r) => (
            <li key={r.id}>
              <strong>{r.patientName}</strong> <span className="muted">{r.mrn} · {r.channel} · {new Date(r.receivedAt).toLocaleString()}</span>
              <div>“{r.text}”</div>
              {r.rating !== null && <span className="rating" aria-label={`Rated ${r.rating} of 5`}>{"★".repeat(r.rating)}{"☆".repeat(5 - r.rating)}</span>}
              {r.optOut && <span className="badge badge-neutral">Opted out</span>}
              {r.needsAttention && <span className="badge badge-warning">Needs a call</span>}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}

function TaskCard({ task, onDone }: { task: CareTask; onDone: () => void }) {
  const [outcome, setOutcome] = useState("");
  const [error, setError] = useState<unknown>(null);
  const complete = async (e: FormEvent) => {
    e.preventDefault();
    try { await api.post(`/api/v1/care/tasks/${task.id}/complete`, { outcome }, { ifMatch: `"${task.version}"` }); onDone(); } catch (err) { setError(err); }
  };
  return (
    <form className={`panel care-task ${task.priority === "High" ? "care-task-urgent" : ""}`} onSubmit={complete} aria-label={`Follow up ${task.patientName}`}>
      <div className="insight-head">
        <span className={`badge ${task.priority === "High" ? "badge-danger" : "badge-info"}`}>{task.priority === "High" ? "⚠ Call today" : "Follow up"}</span>
        <span className="muted small">{task.source} · {new Date(task.createdAt).toLocaleString()}</span>
      </div>
      <strong>{task.patientName}</strong> <span className="muted">{task.mrn}</span>
      {task.phone && <div><Icon name="reception" size={14} /> <a href={`tel:${task.phone}`}>{task.phone}</a></div>}
      <p>{task.summary}</p>
      {task.replyText && <blockquote>“{task.replyText}”</blockquote>}
      <Field id={`out-${task.id}`} label="What was done">
        <input id={`out-${task.id}`} value={outcome} onChange={(e) => setOutcome(e.target.value)} placeholder="e.g. Called; mild pain, review tomorrow 09:00" />
      </Field>
      <ErrorAlert error={error} />
      <button className="btn btn-primary btn-small" type="submit" disabled={outcome.trim().length < 3}>Mark as done</button>
    </form>
  );
}

function Messages({ canRetry, simulation }: { canRetry: boolean; simulation: boolean }) {
  const [status, setStatus] = useState("");
  const [rows, setRows] = useState<CareMessage[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0);
  useEffect(() => { api.get<CareMessage[]>(`/api/v1/care/messages${status ? `?status=${status}` : ""}`).then((r) => setRows(r.data)).catch(setError); }, [status, reload]);
  const simulate = async (m: CareMessage) => {
    const text = window.prompt(`Simulated reply from ${m.patientName} (sandbox):`, "5");
    if (!text) return;
    try { await api.post(`/api/v1/care/messages/${m.id}/simulate-reply`, { text }); setReload((k) => k + 1); } catch (err) { setError(err); }
  };
  const retry = async (m: CareMessage) => { try { await api.post(`/api/v1/care/messages/${m.id}/retry`); setReload((k) => k + 1); } catch (err) { setError(err); } };
  return (
    <>
      <div className="field filter-field">
        <label htmlFor="msg-status">Show</label>
        <select id="msg-status" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">All messages (60 days)</option><option value="Scheduled">Scheduled</option><option value="Sent">Sent</option><option value="Failed">Failed</option><option value="Cancelled">Cancelled</option><option value="Skipped">Skipped</option>
        </select>
      </div>
      <ErrorAlert error={error} />
      <div className="panel table-scroll">
        <table>
          <thead><tr><th scope="col">When</th><th scope="col">Patient</th><th scope="col">Message</th><th scope="col">Status</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {rows?.map((m) => (
              <tr key={m.id}>
                <td>{new Date(m.sentAt ?? m.dueAt).toLocaleString()}<div className="muted small">{m.channel}{m.channel === "Sms" ? ` · ${m.segments} part${m.segments > 1 ? "s" : ""}` : ""}</div></td>
                <td><strong>{m.patientName}</strong><div className="muted small">{m.mrn}</div></td>
                <td><div className="small"><strong>{kindLabel[m.kind] ?? m.kind}</strong></div><div className="message-preview">{m.body}</div></td>
                <td>
                  <span className={`badge ${m.status === "Sent" ? "badge-ok" : m.status === "Failed" ? "badge-danger" : m.status === "Scheduled" ? "badge-info" : "badge-neutral"}`}>{m.status === "Sent" && m.simulated ? "Sent (sandbox)" : m.status}</span>
                  {m.lastError && <div className="small muted">{m.lastError}</div>}
                </td>
                <td className="btn-row">
                  {simulation && m.status === "Sent" && <button className="btn btn-small" onClick={() => void simulate(m)}>Simulate reply</button>}
                  {canRetry && m.status === "Failed" && <button className="btn btn-small" onClick={() => void retry(m)}>Retry</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows?.length === 0 && <p className="muted">No messages yet. They appear when consenting patients book, visit or miss appointments.</p>}
      </div>
    </>
  );
}

function Tips({ canApprove }: { canApprove: boolean }) {
  const [tips, setTips] = useState<Tip[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [form, setForm] = useState({ title: "", body: "", topic: "Oral hygiene", language: "en" });
  const [reload, setReload] = useState(0);
  const load = useCallback(() => api.get<Tip[]>("/api/v1/care/tips").then((r) => setTips(r.data)).catch(setError), []);
  useEffect(() => { void load(); }, [load, reload]);
  const create = async (e: FormEvent) => {
    e.preventDefault();
    try { await api.post("/api/v1/care/tips", form); setForm({ ...form, title: "", body: "" }); setReload((k) => k + 1); } catch (err) { setError(err); }
  };
  const act = async (id: string, action: "approve" | "retire") => { try { await api.post(`/api/v1/care/tips/${id}/${action}`); setReload((k) => k + 1); } catch (err) { setError(err); } };
  return (
    <div className="grid-2">
      <form className="panel" onSubmit={create} aria-labelledby="tip-new">
        <h2 id="tip-new">Write a health tip</h2>
        <p className="muted small">General advice only — never about a specific patient. A different clinician must approve it before it can be sent.</p>
        <Field id="tip-title" label="Title"><input id="tip-title" value={form.title} maxLength={120} onChange={(e) => setForm({ ...form, title: e.target.value })} /></Field>
        <Field id="tip-body" label="Text" hint={`${form.body.length}/600`}><textarea id="tip-body" rows={4} maxLength={600} value={form.body} onChange={(e) => setForm({ ...form, body: e.target.value })} /></Field>
        <div className="form-grid">
          <Field id="tip-topic" label="Topic"><input id="tip-topic" value={form.topic} onChange={(e) => setForm({ ...form, topic: e.target.value })} /></Field>
          <Field id="tip-lang" label="Language"><select id="tip-lang" value={form.language} onChange={(e) => setForm({ ...form, language: e.target.value })}><option value="en">English</option><option value="am">Amharic</option><option value="ti">Tigrinya</option></select></Field>
        </div>
        <ErrorAlert error={error} />
        <button className="btn btn-primary" type="submit" disabled={form.title.trim().length < 3 || form.body.trim().length < 20}>Save draft for approval</button>
      </form>
      <section aria-labelledby="tip-list">
        <h2 id="tip-list">Tip library</h2>
        {tips?.length === 0 && <p className="muted">No tips yet.</p>}
        {tips?.map((t) => (
          <article key={t.id} className="panel tip-card" aria-label={t.title}>
            <div className="insight-head">
              <span className={`badge ${t.status === "Approved" ? "badge-ok" : t.status === "Draft" ? "badge-warning" : "badge-neutral"}`}>{t.status === "Approved" ? "✓ Approved" : t.status === "Draft" ? "Awaiting clinician approval" : "Retired"}</span>
              <span className="muted small">{t.topic} · {langLabel[t.language]}</span>
            </div>
            <strong>{t.title}</strong>
            <p>{t.body}</p>
            <div className="muted small">By {t.authorName}{t.approvedByName ? ` · approved by ${t.approvedByName}` : ""} · {t.segments} SMS part{t.segments > 1 ? "s" : ""}</div>
            <div className="btn-row">
              {canApprove && t.status === "Draft" && <button className="btn btn-small" onClick={() => void act(t.id, "approve")}>Approve as clinically appropriate</button>}
            </div>
          </article>
        ))}
      </section>
    </div>
  );
}

function Campaigns() {
  const [tips, setTips] = useState<Tip[]>([]);
  const [rows, setRows] = useState<Campaign[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [form, setForm] = useState({ tipId: "", audience: "AllConsented", months: 12, scheduledAt: "" });
  const [reload, setReload] = useState(0);
  useEffect(() => {
    api.get<Tip[]>("/api/v1/care/tips").then((r) => setTips(r.data.filter((t) => t.status === "Approved"))).catch(setError);
    api.get<Campaign[]>("/api/v1/care/campaigns").then((r) => setRows(r.data)).catch(setError);
  }, [reload]);
  const create = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await api.post("/api/v1/care/campaigns", { tipId: form.tipId, audience: form.audience, months: form.audience === "VisitedRecently" ? form.months : null, scheduledAt: form.scheduledAt ? new Date(form.scheduledAt).toISOString() : null });
      setReload((k) => k + 1);
    } catch (err) { setError(err); }
  };
  const cancel = async (id: string) => { try { await api.post(`/api/v1/care/campaigns/${id}/cancel`); setReload((k) => k + 1); } catch (err) { setError(err); } };
  return (
    <div className="grid-2">
      <form className="panel" onSubmit={create} aria-labelledby="camp-new">
        <h2 id="camp-new"><Icon name="megaphone" /> Send a health tip</h2>
        <Field id="camp-tip" label="Approved tip">
          <select id="camp-tip" value={form.tipId} onChange={(e) => setForm({ ...form, tipId: e.target.value })}>
            <option value="">Choose…</option>{tips.map((t) => <option key={t.id} value={t.id}>{t.title} ({langLabel[t.language]})</option>)}
          </select>
        </Field>
        <Field id="camp-aud" label="Who">
          <select id="camp-aud" value={form.audience} onChange={(e) => setForm({ ...form, audience: e.target.value })}>
            <option value="AllConsented">Everyone who agreed to health tips</option><option value="VisitedRecently">Those who visited recently</option>
          </select>
        </Field>
        {form.audience === "VisitedRecently" && <Field id="camp-months" label="Visited in the last (months)"><input id="camp-months" type="number" min={1} max={60} value={form.months} onChange={(e) => setForm({ ...form, months: Number(e.target.value) })} /></Field>}
        <Field id="camp-at" label="Send at" hint="empty = now; never 20:00–08:00"><input id="camp-at" type="datetime-local" value={form.scheduledAt} onChange={(e) => setForm({ ...form, scheduledAt: e.target.value })} /></Field>
        <ErrorAlert error={error} />
        <button className="btn btn-primary" type="submit" disabled={!form.tipId}>Schedule campaign</button>
      </form>
      <section className="panel" aria-labelledby="camp-list">
        <h2 id="camp-list">Campaigns</h2>
        {rows?.length === 0 && <p className="muted">No campaigns yet.</p>}
        <ul className="timeline">
          {rows?.map((c) => (
            <li key={c.id}>
              <strong>{c.tipTitle}</strong> <span className={`badge ${c.status === "Queued" ? "badge-ok" : c.status === "Scheduled" ? "badge-info" : "badge-neutral"}`}>{c.status}</span>
              <div className="small muted">{new Date(c.scheduledAt).toLocaleString()} · {c.audience === "AllConsented" ? "all who agreed" : `visited in last ${c.months} months`}</div>
              <div className="small">{c.recipients} recipients · {c.sent} sent · {c.failed} failed · {c.skipped} skipped</div>
              {c.status === "Scheduled" && <button className="btn btn-small" onClick={() => void cancel(c.id)}>Cancel</button>}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}

function Templates() {
  const [rows, setRows] = useState<Template[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0);
  useEffect(() => { api.get<Template[]>("/api/v1/care/templates").then((r) => setRows(r.data)).catch(setError); }, [reload]);
  return (
    <>
      <p className="muted">Allowed placeholders: <span className="mono">{"{clinic} {clinic_phone} {first_name} {date} {date_et} {time} {clinician}"}</span>. Nothing clinical can be inserted. Amharic and Tigrinya defaults need review by a native speaker.</p>
      <ErrorAlert error={error} />
      <div className="dataset-grid">
        {rows?.map((t) => <TemplateCard key={`${t.kind}-${t.language}-${t.version}`} t={t} onSaved={() => setReload((k) => k + 1)} />)}
      </div>
    </>
  );
}

function TemplateCard({ t, onSaved }: { t: Template; onSaved: () => void }) {
  const [body, setBody] = useState(t.body);
  const [enabled, setEnabled] = useState(t.isEnabled);
  const [error, setError] = useState<unknown>(null);
  const id = `tpl-${t.kind}-${t.language}`;
  const save = async (e: FormEvent) => {
    e.preventDefault();
    try { await api.put(`/api/v1/care/templates/${t.kind}/${t.language}`, { body, isEnabled: enabled }); onSaved(); } catch (err) { setError(err); }
  };
  return (
    <form className="panel" onSubmit={save} aria-labelledby={`${id}-h`}>
      <h3 id={`${id}-h`}>{kindLabel[t.kind]} · {langLabel[t.language]} {t.isDefault && <span className="badge badge-neutral">default</span>}</h3>
      <Field id={id} label="Text" hint={`${body.length}/600`}><textarea id={id} rows={4} maxLength={600} value={body} onChange={(e) => setBody(e.target.value)} /></Field>
      <label className="check-row"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Send this message</label>
      <details><summary>Preview ({t.segments} SMS part{t.segments > 1 ? "s" : ""})</summary><p className="message-preview">{t.sample}</p></details>
      <ErrorAlert error={error} />
      <button className="btn btn-small" type="submit" disabled={body === t.body && enabled === t.isEnabled}>Save</button>
    </form>
  );
}

function Overview() {
  const [s, setS] = useState<CareSummary | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { api.get<CareSummary>("/api/v1/care/summary").then((r) => setS(r.data)).catch(setError); }, []);
  if (!s) return <ErrorAlert error={error} />;
  const kpis: [string, string, string?][] = [
    ["Messages sent", String(s.sent), `last ${s.days} days`],
    ["Delivery failures", String(s.failed), s.sent + s.failed > 0 ? `${Math.round((100 * s.failed) / (s.sent + s.failed))}% of attempts` : undefined],
    ["Patient replies", String(s.replies), s.averageRating !== null ? `average rating ${s.averageRating.toFixed(1)} / 5` : "no ratings yet"],
    ["Open care tasks", String(s.openTasks), s.urgentTasks > 0 ? `${s.urgentTasks} to call today` : "none urgent"],
    ["Agreed to reminders", String(s.patientsConsentingReminders), `${s.telegramLinked} on Telegram`],
    ["Agreed to health tips", String(s.patientsConsentingTips)],
  ];
  return (
    <>
      <div className="kpi-grid" role="group" aria-label="Care+ indicators">
        {kpis.map(([label, value, sub]) => (
          <div key={label} className="kpi"><div className="kpi-label">{label}</div><div className="kpi-value">{value}</div>{sub && <div className="kpi-sub">{sub}</div>}</div>
        ))}
      </div>
      <section className="panel" aria-labelledby="by-kind">
        <h2 id="by-kind">By message type</h2>
        <table>
          <thead><tr><th scope="col">Type</th><th scope="col">Sent</th><th scope="col">Failed</th></tr></thead>
          <tbody>{s.byKind.map((k) => <tr key={k.kind}><td>{kindLabel[k.kind] ?? k.kind}</td><td className="money">{k.sent}</td><td className="money">{k.failed}</td></tr>)}</tbody>
        </table>
      </section>
    </>
  );
}
