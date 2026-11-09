import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { api, ApiError, fetchPdfUrl, newIdempotencyKey } from "../api/client";
import type { DocumentInfo, EncounterDetail, NoteContent, PatientBanner, Session } from "../api/types";
import { AllergyBadge, ErrorAlert, Field, StatusBadge } from "../components/common";
import { formatDualDate, formatDualInstant, shortHash } from "../lib/format";

type SaveState = "saved" | "dirty" | "saving" | "conflict" | "error";

const SECTIONS: { key: keyof NoteContent; label: string; required?: boolean; max: number }[] = [
  { key: "chiefComplaint", label: "Presenting complaint", required: true, max: 1000 },
  { key: "history", label: "History", max: 8000 },
  { key: "examination", label: "Examination", max: 8000 },
  { key: "assessment", label: "Assessment", required: true, max: 4000 },
  { key: "plan", label: "Plan", max: 4000 },
];

export function EncounterPage({ id, session, onBack }: { id: string; session: Session; onBack: () => void }) {
  const [enc, setEnc] = useState<EncounterDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const can = (c: string) => session.capabilities.includes(c);

  const load = useCallback(async () => {
    try {
      setEnc((await api.get<EncounterDetail>(`/api/v1/encounters/${id}`)).data);
    } catch (err) {
      setError(err);
    }
  }, [id]);

  useEffect(() => {
    api.get<EncounterDetail>(`/api/v1/encounters/${id}`).then((r) => setEnc(r.data)).catch(setError);
  }, [id]);

  if (!enc) {
    return (
      <>
        <button className="btn" onClick={onBack}>← Back</button>
        <ErrorAlert error={error} />
        {!error && <p role="status">Loading encounter…</p>}
      </>
    );
  }

  return (
    <>
      <PatientBannerView patient={enc.patient} encounterStatus={enc.status} startedAt={enc.startedAt} />
      <ErrorAlert error={error} />
      <div className="grid-2">
        <div>
          {enc.status === "InProgress" && enc.draft && (enc.isAttending ? (
            <NoteEditor encounter={enc} onSigned={load} canSign={can("clinical.note.sign")} />
          ) : (
            <div className="alert alert-info">Draft in progress by the attending clinician. Only the attending clinician can edit and sign.</div>
          ))}
          {enc.signedVersions.length > 0 && <SignedNotes encounter={enc} />}
          {enc.status === "Signed" && can("clinical.note.sign") && <AddendumForm encounterId={enc.id} onAdded={load} />}
        </div>
        <div>
          {can("clinical.allergy.write") && <AllergyPanel patient={enc.patient} onChanged={load} />}
          {can("document.read") && enc.signedVersions.length > 0 && <DocumentsPanel encounter={enc} canCreate={can("document.create")} canFinalize={can("document.finalize")} />}
        </div>
      </div>
    </>
  );
}

function PatientBannerView({ patient, encounterStatus, startedAt }: { patient: PatientBanner; encounterStatus: string; startedAt: string }) {
  return (
    <section className="patient-banner" aria-label="Patient identity and safety">
      <div>
        <div className="name">{patient.displayName}</div>
        <div className="meta">
          <span>MRN <span className="mono">{patient.mrn}</span></span>
          <span>{patient.sex}</span>
          <span>Born {formatDualDate(patient.birthDate)}{patient.birthDateIsEstimated && " (estimated)"}</span>
          <span>Encounter {formatDualInstant(startedAt)}</span>
        </div>
      </div>
      <div><StatusBadge status={encounterStatus} /></div>
      <div className="allergy-strip" aria-live="polite">
        <AllergyBadge status={patient.allergyStatus} />
        {patient.activeAllergies.map((a) => (
          <span key={a.substance} className="badge badge-danger">⚠ {a.substance} — {a.severity}{a.reaction && ` (${a.reaction})`}</span>
        ))}
      </div>
    </section>
  );
}

function NoteEditor({ encounter, onSigned, canSign }: { encounter: EncounterDetail; onSigned: () => Promise<void>; canSign: boolean }) {
  const [content, setContent] = useState<NoteContent>(encounter.draft!.content);
  const [version, setVersion] = useState(encounter.draft!.version);
  const [state, setState] = useState<SaveState>("saved");
  const [error, setError] = useState<unknown>(null);
  const [reviewed, setReviewed] = useState(false);
  const dialogRef = useRef<HTMLDialogElement>(null);
  const timer = useRef<number | undefined>(undefined);
  const latest = useRef({ content, version });
  useEffect(() => {
    latest.current = { content, version };
  }, [content, version]);

  const save = useCallback(async (): Promise<number | null> => {
    setState("saving");
    try {
      const r = await api.put<{ version: number }>(`/api/v1/encounters/${encounter.id}/note-draft`, latest.current.content, { ifMatch: `"v${latest.current.version}"` });
      latest.current = { ...latest.current, version: r.data.version };
      setVersion(r.data.version);
      setState("saved");
      return r.data.version;
    } catch (err) {
      setState(err instanceof ApiError && err.status === 412 ? "conflict" : "error");
      setError(err);
      return null;
    }
  }, [encounter.id]);

  const update = (key: keyof NoteContent, value: string) => {
    setContent((c) => {
      const next = { ...c, [key]: value };
      latest.current = { ...latest.current, content: next };
      return next;
    });
    setState("dirty");
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => void save(), 1500); // autosave
  };

  useEffect(() => () => window.clearTimeout(timer.current), []);

  const sign = async (e: FormEvent) => {
    e.preventDefault();
    window.clearTimeout(timer.current);
    const v = state === "saved" ? version : await save();
    if (v === null) {
      dialogRef.current?.close();
      return;
    }
    try {
      await api.post(`/api/v1/encounters/${encounter.id}/sign`, { draftVersion: v, allergiesReviewed: reviewed, allergyReviewToken: encounter.patient.allergyReviewToken }, { idempotencyKey: newIdempotencyKey() });
      dialogRef.current?.close();
      await onSigned();
    } catch (err) {
      dialogRef.current?.close();
      setError(err);
      if (err instanceof ApiError && err.code === "allergies_changed") await onSigned(); // reload so the new allergies are shown
    }
  };

  const statusText: Record<SaveState, string> = {
    saved: `Draft saved (version ${version}). Not signed.`,
    dirty: "Unsaved changes…",
    saving: "Saving draft…",
    conflict: "Someone else changed this draft. Reload before continuing — your text below has not been saved.",
    error: "Draft not saved. Check connection and retry.",
  };

  return (
    <section className="panel" aria-labelledby="note-h">
      <div className="panel-header">
        <h2 id="note-h">Consultation note</h2>
        <StatusBadge status="Draft" />
      </div>
      <p className={state === "conflict" || state === "error" ? "alert alert-error" : "status-line"} role="status" aria-live="polite">{statusText[state]}</p>
      {state !== "conflict" && <ErrorAlert error={state === "error" ? error : null} />}
      {SECTIONS.map((s) => (
        <div className="note-section" key={s.key}>
          <Field id={`note-${s.key}`} label={s.label} hint={s.required ? "required to sign" : undefined}>
            <textarea id={`note-${s.key}`} value={content[s.key]} maxLength={s.max} onChange={(e) => update(s.key, e.target.value)} />
          </Field>
        </div>
      ))}
      <div className="btn-row">
        <button className="btn" onClick={() => void save()} disabled={state === "saving" || state === "conflict"}>Save draft</button>
        {state === "conflict" && <button className="btn" onClick={() => window.location.reload()}>Reload latest</button>}
        {canSign && (
          <button className="btn btn-primary" onClick={() => { setReviewed(false); setError(null); dialogRef.current?.showModal(); }} disabled={state === "conflict" || !content.chiefComplaint.trim() || !content.assessment.trim()}>
            Review and sign…
          </button>
        )}
      </div>
      {error !== null && state !== "error" && state !== "conflict" && <ErrorAlert error={error} />}
      <dialog ref={dialogRef} aria-labelledby="sign-h">
        <form onSubmit={sign}>
          <h2 id="sign-h">Sign consultation note</h2>
          <p>You are signing for <strong>{encounter.patient.displayName}</strong> (<span className="mono">{encounter.patient.mrn}</span>), born {formatDualDate(encounter.patient.birthDate)}.</p>
          <p>Allergies on record: <AllergyBadge status={encounter.patient.allergyStatus} /> {encounter.patient.activeAllergies.map((a) => a.substance).join(", ")}</p>
          <p className="alert alert-warning">A signed note cannot be edited. Later corrections are added as attributed addenda.</p>
          <label className="checkbox field">
            <input type="checkbox" checked={reviewed} onChange={(e) => setReviewed(e.target.checked)} required />
            I have reviewed this patient's allergies and confirm the note is accurate.
          </label>
          <div className="btn-row">
            <button className="btn btn-primary" type="submit" disabled={!reviewed}>Sign note</button>
            <button className="btn" type="button" onClick={() => dialogRef.current?.close()}>Cancel</button>
          </div>
        </form>
      </dialog>
    </section>
  );
}

function SignedNotes({ encounter }: { encounter: EncounterDetail }) {
  return (
    <section className="panel" aria-labelledby="signed-h">
      <h2 id="signed-h">Signed record</h2>
      {encounter.signedVersions.map((v) => (
        <article key={v.id} className="signed-note" aria-label={v.kind === "Original" ? "Signed note" : `Addendum ${v.versionNo - 1}`}>
          <h3>{v.kind === "Original" ? "Signed note" : `Addendum ${v.versionNo - 1}`} <StatusBadge status="Signed" /></h3>
          <p className="status-line">
            {v.signerDisplayName} · {formatDualInstant(v.signedAt)} · SHA-256 <span className="mono">{shortHash(v.contentSha256)}</span>{" "}
            {v.integrityVerified ? <span className="badge badge-ok">✓ integrity verified</span> : <span className="badge badge-danger">✗ integrity check failed</span>}
          </p>
          {v.snapshot.content ? (
            <dl>
              {SECTIONS.map((s) => (
                <div key={s.key}><dt>{s.label}</dt><dd>{v.snapshot.content![s.key] || "Not recorded."}</dd></div>
              ))}
            </dl>
          ) : (
            <dl>
              <dt>Reason</dt><dd>{v.snapshot.addendumReason}</dd>
              <dt>Addendum</dt><dd>{v.snapshot.addendumText}</dd>
            </dl>
          )}
        </article>
      ))}
    </section>
  );
}

function AddendumForm({ encounterId, onAdded }: { encounterId: string; onAdded: () => Promise<void> }) {
  const [text, setText] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<unknown>(null);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await api.post(`/api/v1/encounters/${encounterId}/addenda`, { text, reason }, { idempotencyKey: newIdempotencyKey() });
      setText("");
      setReason("");
      await onAdded();
    } catch (err) {
      setError(err);
    }
  };
  return (
    <section className="panel" aria-labelledby="addendum-h">
      <h2 id="addendum-h">Add addendum</h2>
      <ErrorAlert error={error} />
      <form onSubmit={submit}>
        <Field id="addendum-reason" label="Reason for addendum"><input id="addendum-reason" type="text" value={reason} onChange={(e) => setReason(e.target.value)} minLength={5} maxLength={500} required /></Field>
        <Field id="addendum-text" label="Addendum text"><textarea id="addendum-text" value={text} onChange={(e) => setText(e.target.value)} maxLength={4000} required /></Field>
        <button className="btn btn-primary" type="submit">Sign addendum</button>
      </form>
    </section>
  );
}

function AllergyPanel({ patient, onChanged }: { patient: PatientBanner; onChanged: () => Promise<void> }) {
  const [substance, setSubstance] = useState("");
  const [reaction, setReaction] = useState("");
  const [severity, setSeverity] = useState("");
  const [error, setError] = useState<unknown>(null);

  const add = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await api.post(`/api/v1/patients/${patient.id}/allergies`, { substance, reaction, severity });
      setSubstance("");
      setReaction("");
      setSeverity("");
      await onChanged();
    } catch (err) {
      setError(err);
    }
  };

  const nka = async () => {
    try {
      const p = await api.get<{ version: number }>(`/api/v1/patients/${patient.id}`);
      await api.post(`/api/v1/patients/${patient.id}/allergies/no-known-allergies`, undefined, { ifMatch: p.etag ?? `"v${p.data.version}"` });
      await onChanged();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="allergy-h">
      <h2 id="allergy-h">Allergies</h2>
      <ErrorAlert error={error} />
      {patient.allergyStatus !== "HasAllergies" && (
        <p>
          <button className="btn" onClick={nka}>Record “no known allergies”</button>{" "}
          <span className="hint">only after asking the patient</span>
        </p>
      )}
      <form onSubmit={add} aria-label="Add allergy">
        <div className="field-row">
          <Field id="allergy-substance" label="Substance"><input id="allergy-substance" type="text" value={substance} onChange={(e) => setSubstance(e.target.value)} required maxLength={200} /></Field>
          <Field id="allergy-severity" label="Severity">
            <select id="allergy-severity" value={severity} onChange={(e) => setSeverity(e.target.value)} required>
              <option value="">Select…</option>
              <option>Mild</option><option>Moderate</option><option>Severe</option><option>Unknown</option>
            </select>
          </Field>
        </div>
        <Field id="allergy-reaction" label="Reaction" hint="optional"><input id="allergy-reaction" type="text" value={reaction} onChange={(e) => setReaction(e.target.value)} maxLength={500} /></Field>
        <button className="btn" type="submit">Add allergy</button>
      </form>
    </section>
  );
}

function DocumentsPanel({ encounter, canCreate, canFinalize }: { encounter: EncounterDetail; canCreate: boolean; canFinalize: boolean }) {
  const [docs, setDocs] = useState<DocumentInfo[]>([]);
  const [preview, setPreview] = useState<{ url: string; doc: DocumentInfo; kind: "preview" | "final" } | null>(null);
  const [paper, setPaper] = useState("A4");
  const [error, setError] = useState<unknown>(null);
  const [printNote, setPrintNote] = useState<string | null>(null);
  const frameRef = useRef<HTMLIFrameElement>(null);
  const latestVersion = encounter.signedVersions[encounter.signedVersions.length - 1]!;

  const load = useCallback(async () => {
    try {
      setDocs((await api.get<DocumentInfo[]>(`/api/v1/documents?encounterId=${encounter.id}`)).data);
    } catch (err) {
      setError(err);
    }
  }, [encounter.id]);

  const versionCount = encounter.signedVersions.length;
  useEffect(() => {
    api.get<DocumentInfo[]>(`/api/v1/documents?encounterId=${encounter.id}`).then((r) => setDocs(r.data)).catch(setError);
  }, [encounter.id, versionCount]);

  useEffect(() => () => { if (preview) URL.revokeObjectURL(preview.url); }, [preview]);

  const show = async (doc: DocumentInfo, kind: "preview" | "final") => {
    setError(null);
    setPrintNote(null);
    try {
      const r = await fetchPdfUrl(`/api/v1/documents/${doc.id}/${kind === "preview" ? "preview" : "download"}`);
      setPreview({ url: r.url, doc, kind });
    } catch (err) {
      setError(err);
    }
  };

  const create = async () => {
    try {
      const r = await api.post<DocumentInfo>("/api/v1/documents", { noteVersionId: latestVersion.id, paperSize: paper });
      await load();
      await show(r.data, "preview");
    } catch (err) {
      setError(err);
    }
  };

  const finalize = async (doc: DocumentInfo) => {
    try {
      const r = await api.post<DocumentInfo>(`/api/v1/documents/${doc.id}/finalize`, undefined, { idempotencyKey: newIdempotencyKey() });
      await load();
      await show(r.data, "final");
    } catch (err) {
      setError(err);
    }
  };

  const print = async () => {
    if (!preview || preview.kind !== "final") return;
    try {
      await api.post(`/api/v1/documents/${preview.doc.id}/print-events`, { kind: "PrintDialogOpened" });
      frameRef.current?.contentWindow?.print();
      setPrintNote("Print dialog opened. The system cannot confirm paper was printed — check the printer output.");
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="docs-h">
      <h2 id="docs-h">Documents</h2>
      <ErrorAlert error={error} />
      {canCreate && (
        <div className="btn-row field">
          <label htmlFor="paper" className="sr-only">Paper size</label>
          <select id="paper" value={paper} onChange={(e) => setPaper(e.target.value)}>
            <option value="A4">A4</option>
            <option value="A5">A5</option>
          </select>
          <button className="btn" onClick={create}>Prepare consultation summary (preview)</button>
        </div>
      )}
      <table aria-label="Documents for this encounter">
        <thead><tr><th>Document</th><th>Status</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {docs.length === 0 && <tr><td colSpan={3} className="muted">No documents yet.</td></tr>}
          {docs.map((d) => (
            <tr key={d.id}>
              <td>Consultation summary v{d.templateVersion} · {d.paperSize}<br /><span className="mono muted">ref {d.verificationRef}</span></td>
              <td><StatusBadge status={d.status} /></td>
              <td className="btn-row">
                {d.status === "Draft" && <button className="btn" onClick={() => show(d, "preview")}>Preview</button>}
                {d.status === "Draft" && canFinalize && <button className="btn btn-primary" onClick={() => finalize(d)}>Finalize</button>}
                {d.status === "Final" && <button className="btn" onClick={() => show(d, "final")}>Open final</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {preview && (
        <div className="field">
          <p className={preview.kind === "preview" ? "alert alert-warning" : "alert alert-info"} role="status">
            {preview.kind === "preview" ? "Draft preview — watermarked, not valid for clinical or legal use." : "Final document (immutable)."} Snapshot <span className="mono">{shortHash(preview.doc.snapshotSha256)}</span>
          </p>
          {preview.kind === "final" && <div className="btn-row field"><button className="btn" onClick={print}>Print…</button></div>}
          {printNote && <p className="alert alert-info" role="status">{printNote}</p>}
          <iframe ref={frameRef} className="pdf-frame" title={preview.kind === "preview" ? "Document preview" : "Final document"} src={preview.url} />
        </div>
      )}
    </section>
  );
}
