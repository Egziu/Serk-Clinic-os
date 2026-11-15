import { useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";

interface Recall { id: string; patientId: string; mrn: string; patientName: string; phone: string; kind: string; note: string; dueOn: string; status: string; source: string; overdue: boolean; invitedAt: string | null; escalatedAt: string | null; createdAt: string; closeReason: string | null; version: number }
export interface Automation {
  confirmationsEnabled: boolean; reminderEnabled: boolean; reminderLeadHours: number; sameDayReminderEnabled: boolean; sameDayLeadHours: number;
  followUpEnabled: boolean; followUpDelayHours: number; rebookEnabled: boolean; quietStart: string; quietEnd: string;
  recallInviteLeadDays: number; recallOverdueDays: number; autoCheckupRecallEnabled: boolean; autoCheckupMonths: number; staffNoticesEnabled: boolean;
  isDefault: boolean; updatedAt: string | null; version: number;
}

const kindName: Record<string, string> = { CheckUp: "Check-up", Review: "Review", Cleaning: "Cleaning", Treatment: "Treatment", Other: "Other" };

/** Recall desk: who should come back, who was invited, who is overdue. Booking in Reception closes the loop automatically. */
export function Recalls() {
  const [filter, setFilter] = useState("open");
  const [rows, setRows] = useState<Recall[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0);
  useEffect(() => { api.get<Recall[]>(`/api/v1/care/recalls?filter=${filter}`).then((r) => setRows(r.data)).catch(setError); }, [filter, reload]);
  const close = async (r: Recall, status: "Done" | "Cancelled") => {
    const reason = window.prompt(status === "Done" ? "How was this recall completed?" : `Why is the recall for ${r.patientName} no longer needed?`);
    if (!reason) return;
    try { await api.post(`/api/v1/care/recalls/${r.id}/close`, { status, reason }); setReload((k) => k + 1); } catch (err) { setError(err); }
  };
  const overdue = rows?.filter((r) => r.overdue).length ?? 0;
  return (
    <>
      <div className="btn-row field">
        <label htmlFor="recall-filter">Show</label>
        <select id="recall-filter" value={filter} onChange={(e) => setFilter(e.target.value)}>
          <option value="open">Waiting to book</option><option value="upcoming">Due soon</option><option value="booked">Booked</option><option value="all">All</option>
        </select>
        {filter === "open" && overdue > 0 && <span className="badge badge-warning">{overdue} overdue</span>}
      </div>
      <ErrorAlert error={error} />
      <div className="panel table-scroll">
        <table>
          <caption className="sr-only">Recalls</caption>
          <thead><tr><th scope="col">Due</th><th scope="col">Patient</th><th scope="col">Purpose</th><th scope="col">Progress</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {rows?.map((r) => (
              <tr key={r.id} className={r.overdue ? "row-overdue" : undefined}>
                <td><strong>{r.dueOn}</strong>{r.overdue && <div className="badge badge-warning">Overdue</div>}</td>
                <td><strong>{r.patientName}</strong><div className="muted small">{r.mrn}{r.phone && <> · <a href={`tel:${r.phone}`}>{r.phone}</a></>}</div></td>
                <td>{kindName[r.kind] ?? r.kind}{r.note && <div className="muted small">{r.note}</div>}<div className="muted small">{r.source === "Automatic" ? "set automatically" : `set by ${r.source.toLowerCase()}`}</div></td>
                <td className="small">
                  {r.status === "Booked" ? <span className="badge badge-ok">✓ Booked</span> : r.status === "Done" ? <span className="badge badge-ok">✓ Done</span> : r.status === "Cancelled" ? <span className="badge badge-neutral">Cancelled</span> : null}
                  {r.invitedAt && <div>Invited {new Date(r.invitedAt).toLocaleDateString()}</div>}
                  {r.escalatedAt && <div>Call-back task created</div>}
                  {r.closeReason && <div className="muted">{r.closeReason}</div>}
                </td>
                <td className="btn-row">
                  {(r.status === "Due" || r.status === "Booked") && <>
                    <button className="btn btn-small" onClick={() => void close(r, "Done")}>Done</button>
                    <button className="btn btn-small" onClick={() => void close(r, "Cancelled")}>Not needed</button>
                  </>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows?.length === 0 && <p className="all-clear"><Icon name="check" /> No recalls here.</p>}
      </div>
      <p className="muted small">Recalls are set by clinicians at the end of a visit (Next visit) or automatically as a routine check-up. When the patient books, the recall is marked booked; when they attend, it is done.</p>
    </>
  );
}

/** Owner control of every automation, with a plain-language timeline of what will happen. */
export function AutomationSettings() {
  const [s, setS] = useState<Automation | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => { api.get<Automation>("/api/v1/care/automation").then((r) => { setS(r.data); setEtag(r.etag); }).catch(setError); }, []);
  if (!s) return <ErrorAlert error={error} />;
  const set = <K extends keyof Automation>(k: K, v: Automation[K]) => { setSaved(false); setS({ ...s, [k]: v }); };
  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try { const r = await api.put<Automation>("/api/v1/care/automation", s, { ifMatch: etag ?? "0" }); setS(r.data); setEtag(r.etag); setSaved(true); } catch (err) { setError(err); }
  };
  const toggle = (k: keyof Automation, label: string, hint: string) => (
    <label key={k} className="check-row"><input type="checkbox" checked={s[k] as boolean} onChange={(e) => set(k, e.target.checked as never)} /><span><strong>{label}</strong><br /><span className="muted small">{hint}</span></span></label>
  );
  const num = (k: keyof Automation, label: string, min: number, max: number, unit: string) => (
    <Field key={k} id={`au-${k}`} label={label} hint={`${min}–${max} ${unit}`}><input id={`au-${k}`} type="number" min={min} max={max} value={s[k] as number} onChange={(e) => set(k, Number(e.target.value) as never)} /></Field>
  );
  return (
    <form onSubmit={save} aria-label="Automation settings">
      <div className="grid-2">
        <section className="panel" aria-labelledby="au-appt">
          <h2 id="au-appt"><Icon name="calendar" /> Appointments</h2>
          {toggle("confirmationsEnabled", "Confirm every booking", "Sent right after booking (only to patients who agreed).")}
          {toggle("reminderEnabled", "Reminder before the visit", "Main reminder, usually the day before.")}
          {s.reminderEnabled && num("reminderLeadHours", "Hours before the visit", 2, 72, "hours")}
          {toggle("sameDayReminderEnabled", "Second reminder on the day", "A short nudge a few hours before.")}
          {s.sameDayReminderEnabled && num("sameDayLeadHours", "Hours before (same day)", 1, 6, "hours")}
          {toggle("rebookEnabled", "Invite to re-book after a missed visit", "Sent when an appointment becomes a no-show.")}
        </section>
        <section className="panel" aria-labelledby="au-follow">
          <h2 id="au-follow"><Icon name="heart" /> Follow-up and recalls</h2>
          {toggle("followUpEnabled", "Ask how the patient feels after the visit", "Replies with worrying words or low ratings become urgent call-backs.")}
          {s.followUpEnabled && num("followUpDelayHours", "Hours after check-in", 2, 96, "hours")}
          {toggle("autoCheckupRecallEnabled", "Automatic routine check-up recall", "After each visit, if nothing else is booked, remind the team (and the patient) when the next check-up is due.")}
          {s.autoCheckupRecallEnabled && num("autoCheckupMonths", "Check-up every", 3, 24, "months")}
          {num("recallInviteLeadDays", "Invite patients this many days before a recall", 1, 60, "days")}
          {num("recallOverdueDays", "Ask reception to call when a recall is overdue by", 1, 90, "days")}
        </section>
        <section className="panel" aria-labelledby="au-quiet">
          <h2 id="au-quiet"><Icon name="moon" /> Quiet hours and staff notices</h2>
          <div className="form-grid">
            <Field id="au-qs" label="No messages from"><input id="au-qs" type="time" value={s.quietStart} onChange={(e) => set("quietStart", e.target.value)} /></Field>
            <Field id="au-qe" label="until"><input id="au-qe" type="time" value={s.quietEnd} onChange={(e) => set("quietEnd", e.target.value)} /></Field>
          </div>
          {toggle("staffNoticesEnabled", "Automatic staff notices", "Posts on the notice board: patients to call back, recalls to book, day close missing, backup due.")}
        </section>
        <section className="panel" aria-labelledby="au-timeline">
          <h2 id="au-timeline">What happens for a typical visit</h2>
          <ol className="timeline small">
            {s.confirmationsEnabled && <li><strong>Booking</strong> — confirmation sent.</li>}
            {s.reminderEnabled && <li><strong>{s.reminderLeadHours} h before</strong> — reminder.</li>}
            {s.sameDayReminderEnabled && <li><strong>{s.sameDayLeadHours} h before</strong> — same-day reminder.</li>}
            <li><strong>Visit</strong> — the dentist may set a follow-up (Next visit).</li>
            {s.followUpEnabled && <li><strong>{s.followUpDelayHours} h after</strong> — "How are you feeling?"; worrying answers → urgent call-back.</li>}
            {s.autoCheckupRecallEnabled && <li><strong>{s.autoCheckupMonths} months later</strong> — check-up recall; invitation {s.recallInviteLeadDays} days before; call-back after {s.recallOverdueDays} days overdue.</li>}
            {s.rebookEnabled && <li><strong>Missed visit</strong> — invitation to re-book.</li>}
            <li className="muted">Nothing is sent between {s.quietStart} and {s.quietEnd}, and only to patients who agreed.</li>
          </ol>
        </section>
      </div>
      <ErrorAlert error={error} />
      <div className="btn-row">
        <button className="btn btn-primary" type="submit">Save automation</button>
        {saved && <span className="badge badge-ok" role="status">✓ Saved</span>}
        {s.isDefault && !saved && <span className="muted small">Using the recommended defaults.</span>}
      </div>
    </form>
  );
}
