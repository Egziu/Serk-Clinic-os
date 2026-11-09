import { useRef, useEffect, useState, type FormEvent } from "react";
import { api, uploadFile } from "../api/client";
import { ErrorAlert, Field } from "../components/common";
import { Icon } from "../components/icons";
import { applyAccent, contrastRatio, deriveAccent, isHexColor } from "../lib/theme";

export interface Asset { id: string; kind: string; contentType: string; sha256: string; width: number; height: number; sizeBytes: number; uploadedAt: string }
export interface FacilitySettings { facilityId: string; accentColor: string; defaultClinicalPaper: string; autoPrepareDocuments: boolean; logo: Asset | null; stamp: Asset | null; version: number }
export interface Printer { id: string; name: string; kind: string; paper: string; purpose: string; isDefault: boolean; widthMm: number; heightMm: number }

const PAPERS: Record<string, { label: string; kind: "Standard" | "Thermal" }> = {
  A3: { label: "A3 (297 × 420 mm)", kind: "Standard" },
  A4: { label: "A4 (210 × 297 mm)", kind: "Standard" },
  A5: { label: "A5 (148 × 210 mm)", kind: "Standard" },
  Thermal80: { label: "Thermal roll 80 mm", kind: "Thermal" },
  Thermal58: { label: "Thermal roll 58 mm", kind: "Thermal" },
};

export function SettingsPage({ canBrand, canPrinters, onSettingsChanged }: { canBrand: boolean; canPrinters: boolean; onSettingsChanged: (s: FacilitySettings) => void }) {
  const [settings, setSettings] = useState<FacilitySettings | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    api.get<FacilitySettings>("/api/v1/facility/settings").then((r) => { setSettings(r.data); onSettingsChanged(r.data); }).catch(setError);
  }, [reloadKey, onSettingsChanged]);

  const changed = (s: FacilitySettings) => {
    setSettings(s);
    onSettingsChanged(s);
  };

  return (
    <>
      <div className="page-header">
        <div>
          <h1>Clinic settings</h1>
          <p>Branding, document defaults, automation and printers for this facility. Every change is audited.</p>
        </div>
      </div>
      <ErrorAlert error={error} />
      {settings && canBrand && (
        <div className="grid-2">
          <div>
            <AppearancePanel settings={settings} onSaved={changed} />
            <DefaultsPanel settings={settings} onSaved={changed} />
          </div>
          <div>
            <AssetPanel kind="logo" title="Clinic logo" hint="Printed in the header of documents and slips, shown in the sidebar." asset={settings.logo} onUploaded={() => setReloadKey((k) => k + 1)} />
            <AssetPanel kind="stamp" title="Official stamp" hint="Applied only to issued (final) documents, never to previews." asset={settings.stamp} onUploaded={() => setReloadKey((k) => k + 1)} />
          </div>
        </div>
      )}
      {canPrinters && <PrintersPanel />}
    </>
  );
}

function AppearancePanel({ settings, onSaved }: { settings: FacilitySettings; onSaved: (s: FacilitySettings) => void }) {
  const [color, setColor] = useState(settings.accentColor);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const valid = isHexColor(color);
  const ratio = valid ? contrastRatio(color, "#ffffff") : 0;
  const ok = ratio >= 4.5;

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setSaved(false);
    try {
      const r = await api.put<FacilitySettings>("/api/v1/facility/settings", { accentColor: color }, { ifMatch: `"v${settings.version}"` });
      applyAccent(r.data.accentColor);
      onSaved(r.data);
      setSaved(true);
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="appearance-h">
      <h2 id="appearance-h">Appearance</h2>
      <ErrorAlert error={error} />
      {saved && <div className="alert alert-info" role="status">Accent colour saved and applied for everyone at this facility.</div>}
      <form onSubmit={save}>
        <div className="field">
          <label htmlFor="accent-picker">Accent colour</label>
          <div className="color-row">
            <input id="accent-picker" type="color" value={valid ? color : "#000000"} onChange={(e) => setColor(e.target.value)} />
            <label htmlFor="accent-hex" className="sr-only">Accent colour hex code</label>
            <input id="accent-hex" type="text" value={color} onChange={(e) => setColor(e.target.value.trim())} maxLength={7} spellCheck={false} />
            <button type="button" className="btn" onClick={() => setColor("#1e4d3a")}>Reset to SERK green</button>
          </div>
        </div>
        <div className="field" aria-live="polite">
          {valid && (
            <p>
              Contrast with white text: <strong>{ratio.toFixed(2)}:1</strong>{" "}
              {ok ? <span className="badge badge-ok">✓ readable (WCAG AA)</span> : <span className="badge badge-danger">✗ too light — needs ≥ 4.5:1</span>}
            </p>
          )}
          {valid && ok && (
            <p className="muted">Dark theme uses a lightened variant ({deriveAccent(color.toLowerCase()).brandDark}) chosen automatically to stay ≥ 4.5:1 on dark surfaces.</p>
          )}
        </div>
        <button className="btn btn-primary" type="submit" disabled={!valid || !ok}>Save colour</button>
      </form>
    </section>
  );
}

function DefaultsPanel({ settings, onSaved }: { settings: FacilitySettings; onSaved: (s: FacilitySettings) => void }) {
  const [paper, setPaper] = useState(settings.defaultClinicalPaper);
  const [auto, setAuto] = useState(settings.autoPrepareDocuments);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const r = await api.put<FacilitySettings>("/api/v1/facility/settings", { defaultClinicalPaper: paper, autoPrepareDocuments: auto }, { ifMatch: `"v${settings.version}"` });
      onSaved(r.data);
      setSaved(true);
    } catch (err) {
      setError(err);
    }
  };
  return (
    <section className="panel" aria-labelledby="defaults-h">
      <h2 id="defaults-h">Documents and automation</h2>
      <ErrorAlert error={error} />
      {saved && <div className="alert alert-info" role="status">Saved.</div>}
      <form onSubmit={save}>
        <Field id="default-paper" label="Default paper for clinical documents" hint="thermal paper is not archival and is not offered">
          <select id="default-paper" value={paper} onChange={(e) => setPaper(e.target.value)}>
            {["A4", "A5", "A3"].map((p) => <option key={p} value={p}>{PAPERS[p]!.label}</option>)}
          </select>
        </Field>
        <label className="checkbox field">
          <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
          Automatically prepare a draft consultation summary when a note is signed (the clinician still reviews and finalizes)
        </label>
        <button className="btn btn-primary" type="submit">Save defaults</button>
      </form>
    </section>
  );
}

export function AssetPanel({ kind, title, hint, asset, onUploaded, uploadUrl }: { kind: string; title: string; hint: string; asset: Asset | null; onUploaded: () => void; uploadUrl?: string }) {
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const id = `upload-${kind}`;
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!file) return;
    const uploading = file;
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      await uploadFile(uploadUrl ?? `/api/v1/facility/branding/${kind}`, uploading);
      // Clear only the file that was uploaded (a newer choice made meanwhile is kept).
      setFile((current) => {
        if (current !== uploading) return current;
        if (inputRef.current) inputRef.current.value = "";
        return null;
      });
      setDone(`${title} updated.`);
      onUploaded();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };
  return (
    <section className="panel" aria-labelledby={`${id}-h`}>
      <h2 id={`${id}-h`}>{title}</h2>
      <p className="status-line">{hint}</p>
      <div className="asset-preview field">
        {asset ? (
          <>
            <img src={`/api/v1/branding-assets/${asset.id}/content`} alt={`Current ${title.toLowerCase()}`} />
            <span className="status-line">{asset.width}×{asset.height}px · {(asset.sizeBytes / 1024).toFixed(0)} KB · {asset.contentType}<br /><span className="mono">sha256 {asset.sha256.slice(0, 12)}</span></span>
          </>
        ) : <span className="muted">None uploaded.</span>}
      </div>
      <ErrorAlert error={error} />
      {done && <p className="alert alert-info" role="status">{done}</p>}
      <form onSubmit={submit} className="btn-row">
        <label htmlFor={id} className="sr-only">Choose {title.toLowerCase()} image</label>
        <input ref={inputRef} id={id} type="file" accept="image/png,image/jpeg" disabled={busy} onChange={(e) => { setFile(e.target.files?.[0] ?? null); setDone(null); }} />
        <button className="btn btn-primary" type="submit" disabled={!file || busy}><Icon name="upload" /> {busy ? "Uploading…" : `Upload ${title.toLowerCase()}`}</button>
      </form>
      <p className="status-line">PNG (24/32-bit) or JPEG, 16–4096 px, max 512 KB. Files are checked for hidden content; uploads replace the previous image but never alter documents already issued.</p>
    </section>
  );
}

function PrintersPanel() {
  const [printers, setPrinters] = useState<Printer[]>([]);
  const [name, setName] = useState("");
  const [paper, setPaper] = useState("Thermal80");
  const [purpose, setPurpose] = useState("AppointmentSlips");
  const [isDefault, setIsDefault] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const thermal = PAPERS[paper]!.kind === "Thermal";

  useEffect(() => {
    api.get<Printer[]>("/api/v1/facility/printers").then((r) => setPrinters(r.data)).catch(setError);
  }, [version]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    const submitted = name;
    try {
      await api.post("/api/v1/facility/printers", { name: submitted, paper, purpose, isDefault });
      setName((current) => (current === submitted ? "" : current)); // never erase text typed while saving
      setVersion((v) => v + 1);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };
  const remove = async (id: string) => {
    try {
      await api.post(`/api/v1/facility/printers/${id}/deactivate`);
      setVersion((v) => v + 1);
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="printers-h">
      <h2 id="printers-h"><Icon name="printer" /> Printers</h2>
      <p className="status-line">Classify each printer so documents go to suitable paper: standard (A3/A4/A5) for clinical documents; thermal rolls (58/80 mm) only for appointment slips.</p>
      <ErrorAlert error={error} />
      <table aria-label="Printer profiles">
        <thead><tr><th>Name</th><th>Class</th><th>Paper</th><th>Used for</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {printers.length === 0 && <tr><td colSpan={5} className="muted">No printers configured.</td></tr>}
          {printers.map((p) => (
            <tr key={p.id}>
              <td>{p.name} {p.isDefault && <span className="badge badge-ok">Default</span>}</td>
              <td><span className={`pill ${p.kind === "Thermal" ? "pill-thermal" : "pill-standard"}`}>{p.kind}</span></td>
              <td>{PAPERS[p.paper]?.label ?? p.paper}</td>
              <td>{p.purpose === "AppointmentSlips" ? "Appointment slips" : "Clinical documents"}</td>
              <td><button className="btn" onClick={() => remove(p.id)} aria-label={`Remove ${p.name}`}>Remove</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <form onSubmit={submit} aria-label="Add printer" className="field">
        <h3>Add printer</h3>
        <div className="field-row">
          <Field id="printer-name" label="Name"><input id="printer-name" type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={80} required /></Field>
          <Field id="printer-paper" label="Paper">
            <select id="printer-paper" value={paper} onChange={(e) => { setPaper(e.target.value); if (PAPERS[e.target.value]!.kind === "Thermal") setPurpose("AppointmentSlips"); }}>
              {Object.entries(PAPERS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
            </select>
          </Field>
          <Field id="printer-purpose" label="Used for">
            <select id="printer-purpose" value={purpose} onChange={(e) => setPurpose(e.target.value)}>
              <option value="AppointmentSlips">Appointment slips</option>
              <option value="ClinicalDocuments" disabled={thermal}>Clinical documents{thermal ? " (not on thermal)" : ""}</option>
            </select>
          </Field>
        </div>
        <label className="checkbox field"><input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} /> Default printer for this purpose</label>
        <button className="btn btn-primary" type="submit" disabled={busy}>{busy ? "Saving…" : "Add printer"}</button>
      </form>
    </section>
  );
}
