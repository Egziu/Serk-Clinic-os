import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { api, ApiError } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";
import { AssetPanel, type FacilitySettings } from "../SettingsPage";
import type { ClinicProfile, InsightsResponse, SetupItem } from "./insightTypes";

type Form = Omit<ClinicProfile, "facilityId" | "completeness" | "updatedAt" | "version">;

const SECTIONS: { title: string; hint: string; fields: { key: keyof Form; label: string; hint?: string; long?: boolean; type?: string; max: number }[] }[] = [
  { title: "Identity", hint: "How patients and partners see you.", fields: [
    { key: "displayName", label: "Clinic name", hint: "on every document", max: 120 },
    { key: "legalName", label: "Registered legal name", max: 200 },
    { key: "motto", label: "Motto / tagline", hint: "printed under the name", max: 140 },
  ] },
  { title: "Address", hint: "Shown on letterheads and slips.", fields: [
    { key: "addressLine1", label: "Street / building", max: 160 },
    { key: "addressLine2", label: "Sub-city, woreda, kebele", max: 160 },
    { key: "city", label: "City", max: 80 },
    { key: "region", label: "Region", max: 80 },
    { key: "country", label: "Country", max: 80 },
    { key: "postalCode", label: "P.O. Box / postal code", max: 20 },
  ] },
  { title: "Contacts", hint: "How patients reach you.", fields: [
    { key: "phone", label: "Main phone", type: "tel", max: 40 },
    { key: "altPhone", label: "Second phone", type: "tel", max: 40 },
    { key: "email", label: "E-mail", type: "email", max: 160 },
    { key: "website", label: "Website", hint: "https://…", type: "url", max: 160 },
    { key: "openingHours", label: "Opening hours", hint: "e.g. Mon–Sat 08:00–18:00", max: 200 },
  ] },
  { title: "Registration", hint: "Official numbers printed on documents and receipts.", fields: [
    { key: "licenceNo", label: "Facility licence number", max: 60 },
    { key: "taxId", label: "Tax ID (TIN)", max: 40 },
    { key: "registrationNo", label: "Business registration number", max: 60 },
  ] },
  { title: "Printed footers", hint: "Short closing lines.", fields: [
    { key: "receiptFooter", label: "Receipt footer", long: true, max: 240 },
    { key: "documentFooter", label: "Clinical document footer", long: true, max: 240 },
  ] },
];

const FORM_KEYS: (keyof Form)[] = ["displayName", "legalName", "motto", "addressLine1", "addressLine2", "city", "region", "country", "postalCode", "phone", "altPhone",
  "email", "website", "taxId", "registrationNo", "licenceNo", "openingHours", "receiptFooter", "documentFooter", "defaultLanguage", "calendarDisplay", "currency"];

function toForm(p: ClinicProfile): Form {
  return Object.fromEntries(FORM_KEYS.map((k) => [k, p[k]])) as Form;
}

/** Sets a CSS custom property through the DOM (the Content-Security-Policy forbids inline style attributes). */
function useCssVar<T extends HTMLElement>(name: string, value: string) {
  const ref = useRef<T>(null);
  useEffect(() => { ref.current?.style.setProperty(name, value); }, [name, value]);
  return ref;
}

export function SetupPage({ onNavigate, onSettingsChanged }: { onNavigate: (path: string) => void; onSettingsChanged: (s: FacilitySettings) => void }) {
  const [profile, setProfile] = useState<ClinicProfile | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [form, setForm] = useState<Form | null>(null);
  const [settings, setSettings] = useState<FacilitySettings | null>(null);
  const [setup, setSetup] = useState<{ items: SetupItem[]; percent: number } | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    api.get<ClinicProfile>("/api/v1/facility/profile").then((r) => { setProfile(r.data); setEtag(r.etag); setForm(toForm(r.data)); }).catch(setError);
    api.get<FacilitySettings>("/api/v1/facility/settings").then((r) => { setSettings(r.data); onSettingsChanged(r.data); }).catch(setError);
    api.get<InsightsResponse>("/api/v1/insights").then((r) => setSetup({ items: r.data.setup, percent: r.data.setupPercent })).catch(() => undefined);
  }, [reload, onSettingsChanged]);

  const dirty = useMemo(() => profile && form && JSON.stringify(toForm(profile)) !== JSON.stringify(form), [profile, form]);
  const fieldErrors = error instanceof ApiError ? error.fields : undefined;
  const set = (k: keyof Form, v: string) => { setSaved(false); setForm((f) => (f ? { ...f, [k]: v } : f)); };

  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (!form || !etag) return;
    setSaving(true);
    setError(null);
    try {
      const r = await api.put<ClinicProfile>("/api/v1/facility/profile", form, { ifMatch: etag });
      setProfile(r.data);
      setEtag(r.etag);
      setForm(toForm(r.data));
      setSaved(true);
      setReload((k) => k + 1);
    } catch (err) {
      setError(err);
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div className="page-header">
        <div>
          <h1>Clinic setup</h1>
          <p>Everything that makes this clinic yours: name, motto, address, contacts, registration, logo and stamp. Changes are versioned and audited.</p>
        </div>
      </div>
      {setup && setup.items.length > 0 && <SetupChecklist items={setup.items} percent={setup.percent} onNavigate={onNavigate} />}
      <ErrorAlert error={error} />
      {form && profile && (
        <div className="setup-grid">
          <form className="panel" onSubmit={save} aria-labelledby="profile-title" noValidate>
            <div className="panel-header">
              <h2 id="profile-title">Clinic profile</h2>
              <span className="badge badge-info">{profile.completeness}% complete</span>
            </div>
            {SECTIONS.map((s) => (
              <fieldset key={s.title} className="form-section">
                <legend>{s.title} <span className="hint">{s.hint}</span></legend>
                <div className="form-grid">
                  {s.fields.map((f) => (
                    <Field key={f.key} id={`p-${f.key}`} label={f.label} hint={f.hint} error={fieldErrors?.[f.key]}>
                      {f.long ? (
                        <textarea id={`p-${f.key}`} rows={2} maxLength={f.max} value={form[f.key]} onChange={(e) => set(f.key, e.target.value)} />
                      ) : (
                        <input id={`p-${f.key}`} type={f.type ?? "text"} maxLength={f.max} value={form[f.key]} onChange={(e) => set(f.key, e.target.value)}
                          aria-invalid={fieldErrors?.[f.key] ? true : undefined} />
                      )}
                    </Field>
                  ))}
                </div>
              </fieldset>
            ))}
            <fieldset className="form-section">
              <legend>Language, calendar and money</legend>
              <div className="form-grid">
                <Field id="p-lang" label="Default language for printed text">
                  <select id="p-lang" value={form.defaultLanguage} onChange={(e) => set("defaultLanguage", e.target.value)}>
                    <option value="en">English</option><option value="am">Amharic (አማርኛ)</option><option value="ti">Tigrinya (ትግርኛ)</option>
                  </select>
                </Field>
                <Field id="p-cal" label="Dates shown as">
                  <select id="p-cal" value={form.calendarDisplay} onChange={(e) => set("calendarDisplay", e.target.value)}>
                    <option value="Both">Gregorian and Ethiopian (labelled)</option><option value="Gregorian">Gregorian only</option><option value="Ethiopian">Ethiopian only</option>
                  </select>
                </Field>
                <Field id="p-cur" label="Currency" hint="3 letters">
                  <input id="p-cur" maxLength={3} value={form.currency} onChange={(e) => set("currency", e.target.value.toUpperCase())} />
                </Field>
              </div>
            </fieldset>
            <div className="btn-row sticky-actions">
              <button className="btn btn-primary" type="submit" disabled={!dirty || saving}>{saving ? "Saving…" : "Save clinic profile"}</button>
              {dirty && <button className="btn" type="button" onClick={() => setForm(toForm(profile))}>Discard changes</button>}
              {saved && !dirty && <span className="badge badge-ok" role="status">✓ Saved (version {profile.version})</span>}
            </div>
          </form>
          <div>
            <LetterheadPreview form={form} logoId={settings?.logo?.id ?? null} accent={settings?.accentColor ?? "#1257c9"} />
            {settings && (
              <>
                <AssetPanel kind="logo" title="Clinic logo" hint="PNG or JPEG. Shown on documents, slips and in the app." asset={settings.logo} onUploaded={() => setReload((k) => k + 1)} />
                <AssetPanel kind="stamp" title="Official stamp" hint="Applied only to issued (final) documents." asset={settings.stamp} onUploaded={() => setReload((k) => k + 1)} />
              </>
            )}
            <p className="muted">Colour, printers and automation: <button className="link-button" onClick={() => onNavigate("/settings")}>Settings</button>.</p>
          </div>
        </div>
      )}
    </>
  );
}

function SetupChecklist({ items, percent, onNavigate }: { items: SetupItem[]; percent: number; onNavigate: (path: string) => void }) {
  const done = items.filter((i) => i.done).length;
  const ring = useCssVar<HTMLDivElement>("--pct", String(percent));
  return (
    <section className="panel setup-checklist" aria-labelledby="checklist-title">
      <div ref={ring} className="progress-ring" role="img" aria-label={`Setup ${percent}% complete`}>
        <span>{percent}%</span>
      </div>
      <div className="checklist-body">
        <h2 id="checklist-title">Setup checklist — {done} of {items.length} done</h2>
        <ul className="check-grid">
          {items.map((i) => (
            <li key={i.code} className={i.done ? "done" : ""}>
              <span aria-hidden="true">{i.done ? "✓" : "○"}</span>
              <span>
                <strong>{i.label}</strong>{i.done ? <span className="sr-only"> (done)</span> : <span className="sr-only"> (to do)</span>}
                <br /><span className="muted">{i.hint}</span>
              </span>
              {!i.done && i.path !== "/setup" && <button className="btn btn-small" onClick={() => onNavigate(i.path)}>Open</button>}
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}

/** Live preview of the printed letterhead. Documents freeze the letterhead when they are prepared. */
function LetterheadPreview({ form, logoId, accent }: { form: Form; logoId: string | null; accent: string }) {
  const address = [form.addressLine1, form.addressLine2, form.city, form.region].filter(Boolean).join(", ");
  const contacts = [form.phone && `Tel ${form.phone}`, form.email, form.website].filter(Boolean).join(" | ");
  const reg = [form.licenceNo && `Licence ${form.licenceNo}`, form.taxId && `TIN ${form.taxId}`].filter(Boolean).join(" | ");
  const box = useCssVar<HTMLDivElement>("--lh-accent", accent);
  return (
    <section className="panel" aria-labelledby="preview-title">
      <h2 id="preview-title"><Icon name="printer" /> Letterhead preview</h2>
      <div ref={box} className="letterhead">
        <div className="letterhead-text">
          <div className="lh-name">{form.displayName || "Clinic name"}</div>
          {form.motto && <div className="lh-motto">{form.motto}</div>}
          <div className="lh-line">{[address, contacts].filter(Boolean).join(" | ") || "Address and contacts appear here"}</div>
          {reg && <div className="lh-line">{reg}</div>}
        </div>
        {logoId && <img src={`/api/v1/branding-assets/${logoId}/content`} alt="Clinic logo" />}
      </div>
      {form.documentFooter && <p className="lh-footer">{form.documentFooter}</p>}
      <p className="muted">New documents use this letterhead. Documents already prepared keep the letterhead they were prepared with.</p>
    </section>
  );
}
