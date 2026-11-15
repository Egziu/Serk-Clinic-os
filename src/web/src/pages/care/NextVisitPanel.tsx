import { useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";

interface Recall { id: string; kind: string; dueOn: string; status: string; source: string; note: string }

const KINDS: [string, string][] = [["Review", "Review (e.g. after extraction or treatment)"], ["CheckUp", "Routine check-up"], ["Cleaning", "Scaling and cleaning"], ["Treatment", "Continue treatment"], ["Other", "Other"]];
const PRESETS: { label: string; weeks?: number; months?: number }[] = [
  { label: "1 week", weeks: 1 }, { label: "2 weeks", weeks: 2 }, { label: "1 month", months: 1 }, { label: "3 months", months: 3 }, { label: "6 months", months: 6 }, { label: "1 year", months: 12 },
];

/**
 * "When should we see this patient again?" — creates a recall. The clinic's automation then invites the patient (if they
 * agreed to messages), notices when they book, and asks reception to call if they do not.
 */
export function NextVisitPanel({ patientId, patientName }: { patientId: string; patientName: string }) {
  const [recalls, setRecalls] = useState<Recall[]>([]);
  const [kind, setKind] = useState("Review");
  const [preset, setPreset] = useState(PRESETS[0]!);
  const [note, setNote] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [reload, setReload] = useState(0);
  useEffect(() => { api.get<Recall[]>(`/api/v1/care/recalls?filter=all&patientId=${patientId}`).then((r) => setRecalls(r.data.filter((x) => x.status === "Due" || x.status === "Booked"))).catch(() => setRecalls([])); }, [patientId, reload]);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null); setSaved(false);
    try {
      await api.post("/api/v1/care/recalls", { patientId, kind, inWeeks: preset.weeks ?? null, inMonths: preset.months ?? null, note });
      setSaved(true); setNote(""); setReload((k) => k + 1);
    } catch (err) { setError(err); }
  };
  return (
    <form className="panel" onSubmit={save} aria-labelledby="next-visit-h">
      <h2 id="next-visit-h"><Icon name="calendar" /> Next visit for {patientName}</h2>
      {recalls.length > 0 && (
        <ul className="small">
          {recalls.map((r) => <li key={r.id}>{r.kind} due {r.dueOn} — <span className={`badge ${r.status === "Booked" ? "badge-ok" : "badge-info"}`}>{r.status === "Booked" ? "✓ Booked" : "Waiting to book"}</span> {r.source === "Automatic" && <span className="muted">(automatic)</span>}</li>)}
        </ul>
      )}
      <div className="form-grid">
        <Field id="nv-kind" label="Purpose"><select id="nv-kind" value={kind} onChange={(e) => setKind(e.target.value)}>{KINDS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></Field>
        <Field id="nv-when" label="See again in">
          <select id="nv-when" value={preset.label} onChange={(e) => setPreset(PRESETS.find((p) => p.label === e.target.value)!)}>{PRESETS.map((p) => <option key={p.label}>{p.label}</option>)}</select>
        </Field>
      </div>
      <Field id="nv-note" label="Note for the team" hint="internal; never sent to the patient"><input id="nv-note" maxLength={200} value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      <ErrorAlert error={error} />
      <div className="btn-row">
        <button className="btn btn-primary" type="submit">Schedule follow-up</button>
        {saved && <span className="badge badge-ok" role="status">✓ Follow-up scheduled — reception and reminders will take it from here</span>}
      </div>
    </form>
  );
}
