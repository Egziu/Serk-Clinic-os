import { useEffect, useState, type FormEvent, type KeyboardEvent } from "react";
import { api, ApiError } from "../api/client";
import { ErrorAlert, Field } from "../components/common";
import { formatDualInstant } from "../lib/format";

interface Finding {
  id: string;
  toothCode: number;
  toothName: string;
  surfaces: string[];
  condition: string;
  note: string;
  recordedAt: string;
  correctsEntryId: string | null;
}

interface Chart {
  patientId: string;
  current: Finding[];
  historyCount: number;
  clinicalReviewStatus: string;
}

const CONDITIONS = ["Sound", "Caries", "Restored", "Crown", "RootCanalTreated", "Fractured", "Sealant", "Missing"] as const;
const ABBREV: Record<string, string> = { Sound: "✓", Caries: "C", Restored: "R", Crown: "Cr", RootCanalTreated: "RCT", Fractured: "Fx", Sealant: "S", Missing: "M" };
const LABEL: Record<string, string> = { RootCanalTreated: "Root canal treated" };

/** FDI layout as seen facing the patient: patient's right on the viewer's left. */
function rows(primary: boolean): number[][] {
  const max = primary ? 5 : 8;
  const [ur, ul, ll, lr] = primary ? [5, 6, 7, 8] : [1, 2, 3, 4];
  const seq = (q: number, reverse: boolean) => Array.from({ length: max }, (_, i) => q * 10 + (reverse ? max - i : i + 1));
  return [[...seq(ur, true), ...seq(ul, false)], [...seq(lr, true), ...seq(ll, false)]];
}

export const isAnterior = (code: number) => code % 10 <= 3;
const surfacesFor = (code: number) => (isAnterior(code) ? ["Mesial", "Distal", "Incisal", "Buccal", "Lingual"] : ["Mesial", "Distal", "Occlusal", "Buccal", "Lingual"]);

export function DentalChart({ encounterId, canWrite, encounterOpen }: { encounterId: string; canWrite: boolean; encounterOpen: boolean }) {
  const [chart, setChart] = useState<Chart | null>(null);
  const [primary, setPrimary] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    api.get<Chart>(`/api/v1/encounters/${encounterId}/dental/chart`).then((r) => setChart(r.data)).catch(setError);
  }, [encounterId, version]);

  const byTooth = new Map((chart?.current ?? []).map((f) => [f.toothCode, f]));
  const onKey = (e: KeyboardEvent, code: number) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      setSelected(code);
    }
  };

  return (
    <section className="panel" aria-labelledby="dental-h">
      <div className="panel-header">
        <h2 id="dental-h">Dental chart (FDI)</h2>
        <span className="badge badge-warning">Clinical content {chart?.clinicalReviewStatus ?? "PENDING CLINICIAN REVIEW"}</span>
      </div>
      <ErrorAlert error={error} />
      <div className="btn-row field" role="radiogroup" aria-label="Dentition">
        <label className="checkbox"><input type="radio" name="dentition" checked={!primary} onChange={() => { setPrimary(false); setSelected(null); }} /> Permanent (11–48)</label>
        <label className="checkbox"><input type="radio" name="dentition" checked={primary} onChange={() => { setPrimary(true); setSelected(null); }} /> Primary (51–85)</label>
      </div>
      <div className="odontogram-orientation"><span>Patient's right</span><span>Patient's left</span></div>
      <svg className="odontogram" viewBox={`0 0 ${(primary ? 10 : 16) * 44 + 8} 128`} role="group" aria-label={`${primary ? "Primary" : "Permanent"} dentition odontogram. Upper arch first row, lower arch second row.`}>
        {rows(primary).map((row, ri) =>
          row.map((code, i) => {
            const f = byTooth.get(code);
            const x = 4 + i * 44 + (i >= row.length / 2 ? 4 : 0);
            const y = 4 + ri * 62;
            // WCAG 2.5.3 label-in-name: the accessible name starts with exactly the visible text (code + mark).
            const visible = f ? `${code} ${ABBREV[f.condition]}` : `${code}`;
            const detail = f ? `${f.toothName.slice(3)}, ${LABEL[f.condition] ?? f.condition}${f.surfaces.length ? ` (${f.surfaces.join(", ")})` : ""}` : "no finding recorded";
            const label = `${visible}: ${detail}`;
            return (
              <g key={code} role="button" tabIndex={0} aria-label={label} aria-pressed={selected === code} className={`tooth${f ? ` tooth-${f.condition.toLowerCase()}` : ""}${selected === code ? " tooth-selected" : ""}`}
                onClick={() => setSelected(code)} onKeyDown={(e) => onKey(e, code)}>
                <rect x={x} y={y} width={38} height={54} rx={6} />
                <text x={x + 19} y={y + 18} textAnchor="middle" className="tooth-code">{`${code} `}</text>
                <text x={x + 19} y={y + 42} textAnchor="middle" className="tooth-mark">{f ? ABBREV[f.condition] : ""}</text>
              </g>
            );
          }),
        )}
      </svg>
      <p className="status-line">Key: ✓ sound · C caries · R restored · Cr crown · RCT root canal treated · Fx fractured · S sealant · M missing. {chart && `${chart.historyCount} ${chart.historyCount === 1 ? "entry" : "entries"} in history.`}</p>

      <table aria-label="Current dental findings">
        <thead><tr><th>Tooth</th><th>Condition</th><th>Surfaces</th><th>Recorded</th></tr></thead>
        <tbody>
          {chart?.current.length === 0 && <tr><td colSpan={4} className="muted">No findings recorded.</td></tr>}
          {chart?.current.map((f) => (
            <tr key={f.id}>
              <td><button className="btn" onClick={() => setSelected(f.toothCode)}>{f.toothName}</button></td>
              <td>{LABEL[f.condition] ?? f.condition}{f.correctsEntryId && " (corrected)"}</td>
              <td>{f.surfaces.join(", ") || "—"}</td>
              <td>{formatDualInstant(f.recordedAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {selected !== null && canWrite && encounterOpen && (
        <FindingForm key={selected} encounterId={encounterId} tooth={selected} existing={byTooth.get(selected) ?? null} onSaved={() => setVersion((v) => v + 1)} />
      )}
      {selected !== null && canWrite && !encounterOpen && <p className="alert alert-info">The encounter is signed; findings can no longer be added to it.</p>}
    </section>
  );
}

function FindingForm({ encounterId, tooth, existing, onSaved }: { encounterId: string; tooth: number; existing: Finding | null; onSaved: () => void }) {
  const [condition, setCondition] = useState("");
  const [surfaces, setSurfaces] = useState<string[]>([]);
  const [note, setNote] = useState("");
  const [confirm, setConfirm] = useState("");
  const [isCorrection, setIsCorrection] = useState(false);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const toggle = (s: string) => setSurfaces((cur) => (cur.includes(s) ? cur.filter((x) => x !== s) : [...cur, s]));
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setSaved(null);
    try {
      await api.post(`/api/v1/encounters/${encounterId}/dental/findings`, {
        toothCode: tooth,
        confirmToothCode: Number(confirm),
        surfaces,
        condition,
        note,
        correctsEntryId: isCorrection && existing ? existing.id : null,
        correctionReason: isCorrection ? reason : null,
      });
      setSaved(`Recorded ${condition} on tooth ${tooth}.`);
      setConfirm("");
      onSaved();
    } catch (err) {
      setError(err);
      if (err instanceof ApiError && err.code === "tooth_confirmation_mismatch") setConfirm("");
    }
  };

  return (
    <form onSubmit={submit} aria-labelledby="finding-h" className="field">
      <h3 id="finding-h">Record finding — tooth {tooth}</h3>
      {existing && <p className="status-line">Current: {LABEL[existing.condition] ?? existing.condition} {existing.surfaces.join(", ")}</p>}
      {saved && <div className="alert alert-info" role="status">{saved}</div>}
      <ErrorAlert error={error} />
      <Field id="finding-condition" label="Condition">
        <select id="finding-condition" value={condition} onChange={(e) => setCondition(e.target.value)} required>
          <option value="">Select…</option>
          {CONDITIONS.map((c) => <option key={c} value={c}>{LABEL[c] ?? c}</option>)}
        </select>
      </Field>
      <fieldset className="field">
        <legend>Surfaces {isAnterior(tooth) ? "(anterior tooth: incisal edge)" : "(posterior tooth: occlusal surface)"}</legend>
        <div className="btn-row">
          {surfacesFor(tooth).map((s) => (
            <label key={s} className="checkbox"><input type="checkbox" checked={surfaces.includes(s)} onChange={() => toggle(s)} /> {s}</label>
          ))}
        </div>
      </fieldset>
      <Field id="finding-note" label="Note" hint="optional"><input id="finding-note" type="text" value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} /></Field>
      {existing && (
        <>
          <label className="checkbox field"><input type="checkbox" checked={isCorrection} onChange={(e) => setIsCorrection(e.target.checked)} /> This corrects the current entry (it was recorded in error)</label>
          {isCorrection && <Field id="finding-reason" label="Correction reason"><input id="finding-reason" type="text" value={reason} minLength={5} maxLength={300} onChange={(e) => setReason(e.target.value)} required /></Field>}
        </>
      )}
      <Field id="finding-confirm" label="Confirm tooth number" hint="type the FDI number again to prevent wrong-tooth entries">
        <input id="finding-confirm" type="text" inputMode="numeric" pattern="[0-9]{2}" value={confirm} onChange={(e) => setConfirm(e.target.value)} required autoComplete="off" />
      </Field>
      <button className="btn btn-primary" type="submit" disabled={!condition || confirm.length !== 2}>Record finding</button>
    </form>
  );
}
