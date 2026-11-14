import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, postForFile, saveFile, uploadFile } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";

interface Dataset { code: string; group: string; title: string; description: string; containsPatientData: boolean; usesDateRange: boolean; columns: string[] }
interface ExportLog { id: string; dataset: string; format: string; from: string | null; to: string | null; containsPatientData: boolean; purpose: string | null; rowCount: number; sha256: string; createdAt: string }
interface BackupLog { id: string; action: string; archiveSha256: string | null; sizeBytes: number; tableCount: number; rowCount: number; ledgerBalanced: boolean | null; detail: string; createdAt: string }
interface Verification { valid: boolean; createdAt: string | null; organisationName: string | null; sameOrganisation: boolean; tables: { name: string; rows: number; ok: boolean }[]; objects: number; objectsOk: number; ledgerBalanced: boolean | null; problems: string[] }

const iso = (d: Date) => d.toISOString().slice(0, 10);

export function DataPage({ canExport, canBackup, canSeeHistory }: { canExport: boolean; canBackup: boolean; canSeeHistory: boolean }) {
  const tabs = [
    ...(canExport ? [["export", "Export data"]] as const : []),
    ...(canBackup ? [["backup", "Backups"]] as const : []),
    ...(canSeeHistory ? [["history", "Export history"]] as const : []),
  ];
  const [tab, setTab] = useState<string>(tabs[0]?.[0] ?? "export");
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Data & backups</h1>
          <p>Take your data with you at every level — reports, ledgers, registers — and keep an encrypted copy of everything. Every export and backup is logged.</p>
        </div>
      </div>
      <div className="tabs" role="tablist" aria-label="Data and backups">
        {tabs.map(([id, label]) => <button key={id} role="tab" id={`dtab-${id}`} aria-selected={tab === id} aria-controls={`dpanel-${id}`} onClick={() => setTab(id)}>{label}</button>)}
      </div>
      <div role="tabpanel" id={`dpanel-${tab}`} aria-labelledby={`dtab-${tab}`}>
        {tab === "export" && <ExportCentre />}
        {tab === "backup" && <Backups />}
        {tab === "history" && <ExportHistory />}
      </div>
    </>
  );
}

function ExportCentre() {
  const [datasets, setDatasets] = useState<Dataset[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [q, setQ] = useState("");
  useEffect(() => { api.get<Dataset[]>("/api/v1/exports").then((r) => setDatasets(r.data)).catch(setError); }, []);
  const groups = useMemo(() => {
    const m = new Map<string, Dataset[]>();
    for (const d of datasets ?? []) {
      if (q && !(d.title + d.description + d.group).toLowerCase().includes(q.toLowerCase())) continue;
      m.set(d.group, [...(m.get(d.group) ?? []), d]);
    }
    return [...m.entries()];
  }, [datasets, q]);
  return (
    <>
      <ErrorAlert error={error} />
      <div className="field filter-field">
        <label htmlFor="ds-filter">Find a dataset</label>
        <input id="ds-filter" type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="e.g. journal, appointments, audit" />
      </div>
      {datasets?.length === 0 && <p className="muted">Your role has no export permission.</p>}
      {groups.map(([group, list]) => (
        <section key={group} aria-labelledby={`g-${group}`}>
          <h2 id={`g-${group}`} className="group-title">{group}</h2>
          <div className="dataset-grid">{list.map((d) => <DatasetCard key={d.code} d={d} />)}</div>
        </section>
      ))}
    </>
  );
}

function DatasetCard({ d }: { d: Dataset }) {
  const today = new Date();
  const [from, setFrom] = useState(iso(new Date(today.getTime() - 29 * 86400000)));
  const [to, setTo] = useState(iso(today));
  const [format, setFormat] = useState("csv");
  const [purpose, setPurpose] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState<string | null>(null);
  const id = d.code.replace(/[^a-z0-9]/gi, "-");

  const run = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null); setDone(null);
    try {
      const file = await postForFile(`/api/v1/exports/${d.code}`, { format, from: d.usesDateRange ? from : null, to: d.usesDateRange ? to : null, purpose: d.containsPatientData ? purpose : null, password: d.containsPatientData ? password : null });
      saveFile(file);
      setDone(`${file.rows ?? "?"} rows · SHA-256 ${file.sha256?.slice(0, 12)}…`);
      setPassword("");
    } catch (err) { setError(err); } finally { setBusy(false); }
  };

  return (
    <form className={`panel dataset ${d.containsPatientData ? "dataset-phi" : ""}`} onSubmit={run} aria-labelledby={`ds-${id}`}>
      <h3 id={`ds-${id}`}>{d.title} {d.containsPatientData && <span className="badge badge-warning"><Icon name="lock" size={12} /> Patient data</span>}</h3>
      <p className="muted">{d.description}</p>
      <details><summary>{d.columns.length} columns</summary><p className="mono small">{d.columns.join(", ")}</p></details>
      <div className="inline-form">
        {d.usesDateRange && (
          <>
            <Field id={`${id}-from`} label="From"><input id={`${id}-from`} type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} /></Field>
            <Field id={`${id}-to`} label="To"><input id={`${id}-to`} type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} /></Field>
          </>
        )}
        <Field id={`${id}-fmt`} label="Format">
          <select id={`${id}-fmt`} value={format} onChange={(e) => setFormat(e.target.value)}><option value="csv">CSV (Excel)</option><option value="json">JSON</option></select>
        </Field>
      </div>
      {d.containsPatientData && (
        <>
          <Field id={`${id}-purpose`} label="Purpose" hint="required, 10–300 characters, recorded">
            <input id={`${id}-purpose`} value={purpose} maxLength={300} onChange={(e) => setPurpose(e.target.value)} placeholder="e.g. Ministry of Health quarterly report" />
          </Field>
          <Field id={`${id}-pw`} label="Your password" hint="confirms it is you">
            <input id={`${id}-pw`} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        </>
      )}
      <ErrorAlert error={error} />
      <div className="btn-row">
        <button className="btn btn-primary" type="submit" disabled={busy || (d.containsPatientData && (purpose.trim().length < 10 || !password))}>
          <Icon name="download" size={16} /> {busy ? "Preparing…" : "Download"}
        </button>
        {done && <span className="badge badge-ok" role="status">✓ {done}</span>}
      </div>
    </form>
  );
}

function passphraseStrength(p: string): { score: number; label: string } {
  let score = 0;
  if (p.length >= 12) score++;
  if (p.length >= 20) score++;
  if (/[a-z]/.test(p) && /[A-Z]/.test(p)) score++;
  if (/\d/.test(p) || /[^A-Za-z0-9]/.test(p)) score++;
  if (new Set(p).size < 6) score = Math.min(score, 1);
  return { score, label: ["Too weak", "Weak", "Fair", "Good", "Strong"][score]! };
}

function Backups() {
  const [history, setHistory] = useState<BackupLog[] | null>(null);
  const [reload, setReload] = useState(0);
  const [error, setError] = useState<unknown>(null);
  const [pass, setPass] = useState("");
  const [pass2, setPass2] = useState("");
  const [password, setPassword] = useState("");
  const [purpose, setPurpose] = useState("Regular backup kept in the clinic safe");
  const [ack, setAck] = useState(false);
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<string | null>(null);
  useEffect(() => { api.get<BackupLog[]>("/api/v1/backups").then((r) => setHistory(r.data)).catch(setError); }, [reload]);
  const strength = passphraseStrength(pass);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null); setCreated(null);
    try {
      const file = await postForFile("/api/v1/backups", { passphrase: pass, password, purpose });
      saveFile(file);
      setCreated(`${file.fileName} · ${file.rows} rows · SHA-256 ${file.sha256?.slice(0, 16)}…`);
      setPass(""); setPass2(""); setPassword(""); setAck(false);
      setReload((k) => k + 1);
    } catch (err) { setError(err); } finally { setBusy(false); }
  };

  return (
    <div className="grid-2">
      <div>
        <form className="panel" onSubmit={create} aria-labelledby="bk-create">
          <h2 id="bk-create"><Icon name="database" /> Create an encrypted backup</h2>
          <p className="muted">Every record of this organisation — including clinical notes — and its stored files (logos, stamps, signatures, issued PDFs), in one file encrypted with AES-256. The server does not keep the file or the passphrase.</p>
          <Field id="bk-purpose" label="Purpose" hint="recorded in the backup log">
            <input id="bk-purpose" value={purpose} maxLength={300} onChange={(e) => setPurpose(e.target.value)} />
          </Field>
          <Field id="bk-pass" label="Backup passphrase" hint="at least 12 characters">
            <input id="bk-pass" type="password" autoComplete="new-password" value={pass} onChange={(e) => setPass(e.target.value)} aria-describedby="bk-strength" />
          </Field>
          <div id="bk-strength" className={`strength strength-${strength.score}`} aria-live="polite"><span /> {pass && strength.label}</div>
          <Field id="bk-pass2" label="Repeat passphrase" error={pass2 && pass2 !== pass ? ["Does not match."] : undefined}>
            <input id="bk-pass2" type="password" autoComplete="new-password" value={pass2} onChange={(e) => setPass2(e.target.value)} />
          </Field>
          <Field id="bk-pw" label="Your sign-in password" hint="confirms it is you">
            <input id="bk-pw" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          <label className="check-row">
            <input type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} />
            I will store the passphrase safely. Without it the backup cannot be opened by anyone, including SERK support.
          </label>
          <ErrorAlert error={error} />
          <div className="btn-row">
            <button className="btn btn-primary" type="submit" disabled={busy || !ack || pass.length < 12 || pass !== pass2 || !password || strength.score < 2 || purpose.trim().length < 10}>
              {busy ? "Encrypting…" : "Create and download backup"}
            </button>
          </div>
          {created && <p className="alert alert-info" role="status">✓ Backup downloaded: {created}. Verify it below before relying on it.</p>}
        </form>
        <VerifyBackup onVerified={() => setReload((k) => k + 1)} />
      </div>
      <section className="panel" aria-labelledby="bk-history">
        <h2 id="bk-history">Backup history</h2>
        {history?.length === 0 && <p className="muted">No backups yet.</p>}
        <ul className="timeline">
          {history?.map((h) => (
            <li key={h.id}>
              <span className={`badge ${h.action === "Created" ? "badge-info" : h.action === "Verified" ? "badge-ok" : "badge-danger"}`}>{h.action === "VerifyFailed" ? "Verification failed" : h.action}</span>{" "}
              <strong>{new Date(h.createdAt).toLocaleString()}</strong>
              <div className="muted small">{h.tableCount} tables · {h.rowCount.toLocaleString()} rows · {(h.sizeBytes / 1024).toFixed(0)} KB{h.ledgerBalanced === true ? " · ledger balanced" : h.ledgerBalanced === false ? " · LEDGER NOT BALANCED" : ""}</div>
              {h.archiveSha256 && <div className="mono small">SHA-256 {h.archiveSha256.slice(0, 24)}…</div>}
              {h.detail && h.action === "VerifyFailed" && <div className="small">{h.detail}</div>}
            </li>
          ))}
        </ul>
        <p className="muted small">Restore is done by the operator into a fresh environment, never over live data (see the backup and restore procedure).</p>
      </section>
    </div>
  );
}

function VerifyBackup({ onVerified }: { onVerified: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [pass, setPass] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Verification | null>(null);
  const [error, setError] = useState<unknown>(null);
  const verify = async (e: FormEvent) => {
    e.preventDefault();
    if (!file) return;
    setBusy(true); setError(null); setResult(null);
    try { setResult(await uploadFile<Verification>("/api/v1/backups/verify", file, { passphrase: pass })); onVerified(); } catch (err) { setError(err); } finally { setBusy(false); }
  };
  return (
    <form className="panel" onSubmit={verify} aria-labelledby="bk-verify">
      <h2 id="bk-verify"><Icon name="shield" /> Verify a backup file</h2>
      <p className="muted">Opens the file with its passphrase and checks every fingerprint, row count and that the ledger balances. Nothing is imported.</p>
      <Field id="bk-file" label="Backup file (.serkbak)"><input id="bk-file" type="file" accept=".serkbak" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></Field>
      <Field id="bk-vpass" label="Passphrase"><input id="bk-vpass" type="password" autoComplete="off" value={pass} onChange={(e) => setPass(e.target.value)} /></Field>
      <ErrorAlert error={error} />
      <button className="btn" type="submit" disabled={busy || !file || !pass}>{busy ? "Checking…" : "Verify"}</button>
      {result && (
        <div className={`alert ${result.valid ? "alert-info" : "alert-error"}`} role="status">
          <strong>{result.valid ? "✓ Backup is complete and intact." : "✗ Backup failed verification."}</strong>
          {result.createdAt && <div>Created {new Date(result.createdAt).toLocaleString()}{result.organisationName ? ` · ${result.organisationName}` : ""}</div>}
          {result.tables.length > 0 && <div>{result.tables.filter((t) => t.ok).length} of {result.tables.length} tables intact · {result.objectsOk} of {result.objects} files intact · ledger {result.ledgerBalanced === null ? "n/a" : result.ledgerBalanced ? "balanced" : "NOT balanced"}</div>}
          {result.problems.length > 0 && <ul>{result.problems.map((p) => <li key={p}>{p}</li>)}</ul>}
        </div>
      )}
    </form>
  );
}

function ExportHistory() {
  const [rows, setRows] = useState<ExportLog[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { api.get<ExportLog[]>("/api/v1/exports/history?limit=100").then((r) => setRows(r.data)).catch(setError); }, []);
  return (
    <>
      <ErrorAlert error={error} />
      <div className="panel table-scroll">
        <table>
          <thead><tr><th scope="col">When</th><th scope="col">Dataset</th><th scope="col">Range</th><th scope="col">Rows</th><th scope="col">Purpose</th><th scope="col">Fingerprint</th></tr></thead>
          <tbody>
            {rows?.map((r) => (
              <tr key={r.id}>
                <td>{new Date(r.createdAt).toLocaleString()}</td>
                <td>{r.dataset} <span className="muted">({r.format.toUpperCase()})</span> {r.containsPatientData && <span className="badge badge-warning">Patient data</span>}</td>
                <td>{r.from ? `${r.from} → ${r.to}` : "Snapshot"}</td>
                <td className="money">{r.rowCount.toLocaleString()}</td>
                <td>{r.purpose ?? <span className="muted">—</span>}</td>
                <td className="mono small">{r.sha256.slice(0, 16)}…</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows?.length === 0 && <p className="muted">No exports yet.</p>}
    </>
  );
}
