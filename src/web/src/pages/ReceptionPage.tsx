import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { api, fetchPdfUrl, newIdempotencyKey } from "../api/client";
import { Icon } from "../components/icons";
import type { Printer } from "./SettingsPage";
import type { Appointment, DuplicateCandidate, PatientSummary, Practitioner, RegisterResponse, Session } from "../api/types";
import { ErrorAlert, Field, StatusBadge } from "../components/common";
import { facilityLocalToUtcIso, formatDualDate, formatTime, localIsoDate } from "../lib/format";
import { t } from "../i18n";

const emptyForm = { givenName: "", fatherName: "", grandfatherName: "", sex: "", birthDate: "", birthDateIsEstimated: false, phone: "", nationalId: "" };

export function ReceptionPage({ session }: { session: Session }) {
  const can = (c: string) => session.capabilities.includes(c);
  const [selected, setSelected] = useState<PatientSummary | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  return (
    <>
      <h1>{t("reception")}</h1>
      <div className="grid-2">
        <div>
          <PatientSearch onSelect={setSelected} selected={selected} />
          {can("patient.register") && <RegisterPatient onRegistered={setSelected} />}
        </div>
        <div>
          {can("appointment.book") && <BookAppointment patient={selected} onBooked={() => setRefreshKey((k) => k + 1)} />}
          <TodaysAppointments refreshKey={refreshKey} canCheckIn={can("appointment.checkin")} />
        </div>
      </div>
    </>
  );
}

function PatientSearch({ onSelect, selected }: { onSelect: (p: PatientSummary) => void; selected: PatientSummary | null }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<PatientSummary[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const search = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      setResults((await api.get<PatientSummary[]>(`/api/v1/patients?query=${encodeURIComponent(query)}`)).data);
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="search-h">
      <h2 id="search-h">{t("patientSearch")}</h2>
      <form onSubmit={search} role="search" className="btn-row">
        <label htmlFor="patient-query" className="sr-only">Name, MRN, phone or national ID</label>
        <input id="patient-query" type="search" placeholder="Name, MRN, phone or national ID" value={query} onChange={(e) => setQuery(e.target.value)} minLength={2} required />
        <button className="btn btn-primary" type="submit">Search</button>
      </form>
      <ErrorAlert error={error} />
      {results && (
        <table aria-label="Search results">
          <thead>
            <tr><th>Patient</th><th>MRN</th><th>Sex</th><th>Date of birth</th><th><span className="sr-only">Action</span></th></tr>
          </thead>
          <tbody>
            {results.length === 0 && (
              <tr><td colSpan={5} className="muted">No matching patient in this clinic. Check spelling or register a new patient.</td></tr>
            )}
            {results.map((p) => (
              <tr key={p.id} aria-selected={selected?.id === p.id}>
                <td>{p.displayName}</td>
                <td className="mono">{p.mrn}</td>
                <td>{p.sex}</td>
                <td>{formatDualDate(p.birthDate)}{p.birthDateIsEstimated && " (estimated)"} · {p.ageYears} y</td>
                <td><button className="btn" onClick={() => onSelect(p)} aria-label={`Select ${p.displayName} ${p.mrn}`}>Select</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {results && results.length === 25 && <p className="status-line">Showing the first 25 matches. Refine the search to see others.</p>}
    </section>
  );
}

function RegisterPatient({ onRegistered }: { onRegistered: (p: PatientSummary) => void }) {
  const [form, setForm] = useState(emptyForm);
  const [error, setError] = useState<unknown>(null);
  const [candidates, setCandidates] = useState<DuplicateCandidate[] | null>(null);
  const [overrideReason, setOverrideReason] = useState("");
  const [created, setCreated] = useState<string | null>(null);
  const keyRef = useRef(newIdempotencyKey());

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setCreated(null);
    try {
      const body = { ...form, sex: form.sex || null, birthDate: form.birthDate || null, nationalId: form.nationalId || null, duplicateOverrideReason: candidates ? overrideReason || null : null };
      const r = await api.post<RegisterResponse>("/api/v1/patients", body, { idempotencyKey: keyRef.current });
      if (r.data.outcome === "DuplicateReviewRequired") {
        setCandidates(r.data.candidates);
        keyRef.current = newIdempotencyKey();
        return;
      }
      const p = r.data.patient!;
      setCreated(`${p.displayName} registered as ${p.mrn}.`);
      onRegistered(p);
      setForm(emptyForm);
      setCandidates(null);
      setOverrideReason("");
      keyRef.current = newIdempotencyKey();
    } catch (err) {
      setError(err);
      keyRef.current = newIdempotencyKey();
    }
  };

  const set = (k: keyof typeof emptyForm) => (e: { target: { value: string; checked?: boolean; type?: string } }) =>
    setForm((f) => ({ ...f, [k]: e.target.type === "checkbox" ? Boolean(e.target.checked) : e.target.value }));
  const definite = candidates?.some((c) => c.strength === "Definite");

  return (
    <section className="panel" aria-labelledby="register-h">
      <h2 id="register-h">{t("registerPatient")}</h2>
      {created && <div className="alert alert-info" role="status">{created}</div>}
      <ErrorAlert error={error} />
      <form onSubmit={submit} aria-label="Register patient">
        <div className="field-row">
          <Field id="givenName" label="Given name"><input id="givenName" type="text" value={form.givenName} onChange={set("givenName")} required maxLength={100} /></Field>
          <Field id="fatherName" label="Father's name"><input id="fatherName" type="text" value={form.fatherName} onChange={set("fatherName")} required maxLength={100} /></Field>
          <Field id="grandfatherName" label="Grandfather's name"><input id="grandfatherName" type="text" value={form.grandfatherName} onChange={set("grandfatherName")} maxLength={100} /></Field>
        </div>
        <div className="field-row">
          <Field id="sex" label="Sex">
            <select id="sex" value={form.sex} onChange={set("sex")} required>
              <option value="">Select…</option>
              <option>Female</option>
              <option>Male</option>
              <option>Other</option>
              <option>Unknown</option>
            </select>
          </Field>
          <Field id="birthDate" label="Date of birth" hint="Gregorian">
            <input id="birthDate" type="date" value={form.birthDate} onChange={set("birthDate")} required />
            {form.birthDate && <div className="status-line">{formatDualDate(form.birthDate)}</div>}
          </Field>
        </div>
        <label className="checkbox field"><input type="checkbox" checked={form.birthDateIsEstimated} onChange={set("birthDateIsEstimated")} /> Date of birth is estimated</label>
        <div className="field-row">
          <Field id="phone" label="Phone" hint="optional"><input id="phone" type="tel" value={form.phone} onChange={set("phone")} /></Field>
          <Field id="nationalId" label="National ID" hint="optional"><input id="nationalId" type="text" value={form.nationalId} onChange={set("nationalId")} /></Field>
        </div>

        {candidates && (
          <div className="alert alert-warning" role="alert" aria-live="assertive">
            <strong>Possible existing record{candidates.length > 1 ? "s" : ""} in this clinic.</strong> Confirm identity with the patient (ID card, date of birth, phone) before creating a new record.
            <ul>
              {candidates.map((c) => (
                <li key={c.id}>
                  <strong>{c.displayName}</strong> · {c.mrn} · born {formatDualDate(c.birthDate)} — {c.strength} match ({c.reasons.join(", ").replaceAll("_", " ")})
                </li>
              ))}
            </ul>
            {definite ? (
              <p><strong>Same national ID already registered.</strong> Use the existing record; a new record cannot be created.</p>
            ) : (
              <Field id="overrideReason" label="Reason this is a different person" hint="required to continue; audited">
                <textarea id="overrideReason" value={overrideReason} onChange={(e) => setOverrideReason(e.target.value)} maxLength={500} />
              </Field>
            )}
          </div>
        )}
        <button className="btn btn-primary" type="submit" disabled={Boolean(definite) || (candidates !== null && overrideReason.trim().length < 5)}>
          {candidates ? "Register as a different person" : "Register patient"}
        </button>
      </form>
    </section>
  );
}

function BookAppointment({ patient, onBooked }: { patient: PatientSummary | null; onBooked: () => void }) {
  const [practitioners, setPractitioners] = useState<Practitioner[]>([]);
  const [practitionerId, setPractitionerId] = useState("");
  const [date, setDate] = useState(localIsoDate(new Date()));
  const [time, setTime] = useState("");
  const [duration, setDuration] = useState(20);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState<string | null>(null);

  useEffect(() => {
    api.get<Practitioner[]>("/api/v1/practitioners").then((r) => setPractitioners(r.data)).catch(setError);
  }, []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!patient) return;
    setError(null);
    setDone(null);
    try {
      const r = await api.post<Appointment>("/api/v1/appointments", { patientId: patient.id, practitionerId, startsAt: facilityLocalToUtcIso(date, time), durationMinutes: duration, reason }, { idempotencyKey: newIdempotencyKey() });
      setDone(`Booked ${r.data.patientDisplayName} with ${r.data.practitionerDisplayName} at ${formatTime(r.data.startsAt)} EAT.`);
      onBooked();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="book-h">
      <h2 id="book-h">Book appointment</h2>
      {patient ? (
        <p>For <strong>{patient.displayName}</strong> <span className="mono">{patient.mrn}</span> · born {formatDualDate(patient.birthDate)}</p>
      ) : (
        <p className="muted">Search for and select a patient first.</p>
      )}
      {done && <div className="alert alert-info" role="status">{done}</div>}
      <ErrorAlert error={error} />
      <form onSubmit={submit} aria-label="Book appointment">
        <Field id="practitioner" label="Clinician">
          <select id="practitioner" value={practitionerId} onChange={(e) => setPractitionerId(e.target.value)} required>
            <option value="">Select…</option>
            {practitioners.map((p) => <option key={p.id} value={p.id}>{p.displayName} — {p.specialty}</option>)}
          </select>
        </Field>
        <div className="field-row">
          <Field id="appt-date" label="Date" hint="Gregorian"><input id="appt-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} required /></Field>
          <Field id="appt-time" label="Time" hint="EAT, UTC+3"><input id="appt-time" type="time" value={time} onChange={(e) => setTime(e.target.value)} required /></Field>
          <Field id="appt-duration" label="Minutes">
            <select id="appt-duration" value={duration} onChange={(e) => setDuration(Number(e.target.value))}>
              {[10, 15, 20, 30, 45, 60].map((m) => <option key={m} value={m}>{m}</option>)}
            </select>
          </Field>
        </div>
        {date && <p className="status-line">{formatDualDate(date)}</p>}
        <Field id="appt-reason" label="Reason" hint="optional, avoid clinical detail"><input id="appt-reason" type="text" value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} /></Field>
        <button className="btn btn-primary" type="submit" disabled={!patient}>Book</button>
      </form>
    </section>
  );
}

function TodaysAppointments({ refreshKey, canCheckIn }: { refreshKey: number; canCheckIn: boolean }) {
  const [items, setItems] = useState<Appointment[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [confirming, setConfirming] = useState<Appointment | null>(null);
  const [identityConfirmed, setIdentityConfirmed] = useState(false);
  const dialogRef = useRef<HTMLDialogElement>(null);
  const printFrame = useRef<HTMLIFrameElement>(null);
  const [printers, setPrinters] = useState<Printer[]>([]);
  const [printerId, setPrinterId] = useState("");
  const [slip, setSlip] = useState<{ url: string; appointmentId: string; patient: string; printer: string } | null>(null);
  const [printNote, setPrintNote] = useState<string | null>(null);
  const today = localIsoDate(new Date());

  useEffect(() => {
    api.get<Printer[]>("/api/v1/facility/printers").then((r) => {
      const slips = r.data.filter((p) => p.purpose === "AppointmentSlips");
      setPrinters(slips);
      setPrinterId(slips.find((p) => p.isDefault)?.id ?? slips[0]?.id ?? "");
    }).catch(() => setPrinters([]));
  }, []);
  useEffect(() => () => { if (slip) URL.revokeObjectURL(slip.url); }, [slip]);
  // Browsers without an inline PDF viewer (or that block it) never fire the frame's load event: offer the slip as a link instead.
  const [slipFallback, setSlipFallback] = useState(false);
  useEffect(() => {
    if (!slip) return;
    const timer = window.setTimeout(() => setSlipFallback(true), 4000);
    return () => window.clearTimeout(timer);
  }, [slip]);

  const printSlip = async (a: Appointment) => {
    setError(null);
    const printer = printers.find((p) => p.id === printerId);
    const paper = printer ? (printer.paper === "A3" ? "A4" : printer.paper) : "Thermal80";
    try {
      const r = await fetchPdfUrl(`/api/v1/appointments/${a.id}/slip?paper=${paper}`);
      // Printing happens in the iframe's onLoad, so it always prints the PDF that was just loaded (never a previous patient's slip).
      setSlip({ url: r.url, appointmentId: a.id, patient: a.patientDisplayName, printer: printer?.name ?? "a thermal 80 mm printer" });
      setPrintNote(`Preparing slip for ${a.patientDisplayName}…`);
      setSlipFallback(false);
    } catch (err) {
      setError(err);
    }
  };

  const load = useCallback(() => {
    api.get<Appointment[]>(`/api/v1/appointments?day=${today}`).then((r) => setItems(r.data)).catch(setError);
  }, [today]);

  useEffect(load, [load, refreshKey]);

  const open = (a: Appointment) => {
    setConfirming(a);
    setIdentityConfirmed(false);
    dialogRef.current?.showModal();
  };

  const checkIn = async () => {
    if (!confirming) return;
    try {
      await api.post(`/api/v1/appointments/${confirming.id}/check-in`, undefined, { ifMatch: `"v${confirming.version}"` });
      dialogRef.current?.close();
      load();
    } catch (err) {
      dialogRef.current?.close();
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="today-h">
      <div className="panel-header">
        <h2 id="today-h">{t("todaysAppointments")}</h2>
        <span className="status-line">{formatDualDate(today)}</span>
      </div>
      <ErrorAlert error={error} />
      <div className="btn-row field">
        <label htmlFor="slip-printer"><Icon name="printer" size={16} /> Slip printer</label>
        <select id="slip-printer" value={printerId} onChange={(e) => setPrinterId(e.target.value)}>
          {printers.length === 0 && <option value="">Thermal 80 mm (default)</option>}
          {printers.map((p) => <option key={p.id} value={p.id}>{p.name} — {p.kind} {p.paper}</option>)}
        </select>
      </div>
      {printNote && !slipFallback && <p className="alert alert-info" role="status">{printNote}</p>}
      {slip && slipFallback && (
        <p className="alert alert-info" role="status">
          Slip for {slip.patient} is ready, but this browser did not open the print dialog automatically.{" "}
          <a href={slip.url} target="_blank" rel="noopener">Open the slip to print it</a> on {slip.printer}.
        </p>
      )}
      <table aria-label="Today's appointments">
        <thead><tr><th>Time (EAT)</th><th>Patient</th><th>Clinician</th><th>Status</th><th><span className="sr-only">Action</span></th></tr></thead>
        <tbody>
          {items.length === 0 && <tr><td colSpan={5} className="muted">No appointments today.</td></tr>}
          {items.map((a) => (
            <tr key={a.id}>
              <td>{formatTime(a.startsAt)}–{formatTime(a.endsAt)}</td>
              <td>{a.patientDisplayName}<br /><span className="mono">{a.patientMrn}</span></td>
              <td>{a.practitionerDisplayName}</td>
              <td><StatusBadge status={a.status} /></td>
              <td className="btn-row">
                {canCheckIn && a.status === "Booked" && <button className="btn" onClick={() => open(a)} aria-label={`Check in ${a.patientDisplayName}`}>Check in</button>}
                {(a.status === "Booked" || a.status === "CheckedIn") && <button className="btn" onClick={() => printSlip(a)} aria-label={`Print slip for ${a.patientDisplayName}`}><Icon name="printer" size={16} /> Slip</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {slip && (
        <iframe key={slip.url} ref={printFrame} className="sr-only" title="Appointment slip for printing" src={slip.url} data-appointment-id={slip.appointmentId}
          onLoad={() => {
            setSlipFallback(false);
            printFrame.current?.contentWindow?.print();
            setPrintNote(`Print dialog opened for ${slip.patient}'s slip — choose ${slip.printer}. The system cannot confirm paper was printed.`);
          }} />
      )}
      <dialog ref={dialogRef} aria-labelledby="checkin-h" onClose={() => setConfirming(null)}>
        {confirming && (
          <>
            <h2 id="checkin-h">Confirm patient identity</h2>
            <p>Ask the patient to state their full name and date of birth, and compare:</p>
            <dl>
              <dt>Name</dt><dd><strong>{confirming.patientDisplayName}</strong></dd>
              <dt>MRN</dt><dd className="mono">{confirming.patientMrn}</dd>
              <dt>Date of birth</dt><dd>{confirming.patientBirthDate ? formatDualDate(confirming.patientBirthDate) : "Not shown"}{confirming.patientBirthDateIsEstimated && " (estimated)"}</dd>
              <dt>Sex</dt><dd>{confirming.patientSex}</dd>
              <dt>Clinician</dt><dd>{confirming.practitionerDisplayName}</dd>
            </dl>
            <label className="checkbox field">
              <input type="checkbox" checked={identityConfirmed} onChange={(e) => setIdentityConfirmed(e.target.checked)} />
              I confirmed the patient's name and date of birth match this record.
            </label>
            <div className="btn-row">
              <button className="btn btn-primary" onClick={checkIn} disabled={!identityConfirmed}>Check in</button>
              <button className="btn" onClick={() => dialogRef.current?.close()}>Cancel</button>
            </div>
          </>
        )}
      </dialog>
    </section>
  );
}
