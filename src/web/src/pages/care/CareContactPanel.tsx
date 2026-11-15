import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";
import type { Contact } from "./careTypes";

/** Reception: how and whether this patient wants to hear from the clinic. Consent is per purpose and always revocable. */
export function CareContactPanel({ patientId, patientName }: { patientId: string; patientName: string }) {
  const [contact, setContact] = useState<Contact | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [form, setForm] = useState({ smsPhone: "", preferredChannel: "None", language: "en", consentReminders: false, consentTips: false, consentRecalls: false, consentMethod: "Verbal" });
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [code, setCode] = useState<{ code: string; expiresAt: string; deepLink: string | null; botLive: boolean } | null>(null);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    api.get<Contact>(`/api/v1/care/patients/${patientId}/contact`).then((r) => {
      setContact(r.data); setEtag(r.etag); setSaved(false); setCode(null);
      setForm({ smsPhone: r.data.smsPhone, preferredChannel: r.data.preferredChannel, language: r.data.language, consentReminders: r.data.consentReminders, consentTips: r.data.consentTips, consentRecalls: r.data.consentRecalls, consentMethod: r.data.consentMethod ?? "Verbal" });
    }).catch(setError);
  }, [patientId, reload]);

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null); setSaved(false);
    try {
      const r = await api.put<Contact>(`/api/v1/care/patients/${patientId}/contact`, form, { ifMatch: etag ?? "0" });
      setContact(r.data); setEtag(r.etag); setSaved(true);
    } catch (err) { setError(err); }
  };
  const link = async () => {
    try { setCode((await api.post<{ code: string; expiresAt: string; deepLink: string | null; botLive: boolean }>(`/api/v1/care/patients/${patientId}/telegram-link`)).data); } catch (err) { setError(err); }
  };
  const fields = error instanceof ApiError ? error.fields : undefined;
  if (!contact) return <ErrorAlert error={error} />;
  return (
    <form className="panel care-contact" onSubmit={save} aria-labelledby="care-contact-h">
      <h2 id="care-contact-h"><Icon name="heart" /> Care+ messages for {patientName}</h2>
      <p className="muted small">Reminders, after-visit follow-up and health tips by SMS or Telegram — only with the patient's agreement. Texts never contain diagnoses or treatment details.</p>
      <div className="form-grid">
        <Field id="cc-phone" label="Mobile for SMS" hint={contact.registeredPhone ? `registered: ${contact.registeredPhone}` : undefined} error={fields?.smsPhone}>
          <input id="cc-phone" type="tel" value={form.smsPhone} onChange={(e) => setForm({ ...form, smsPhone: e.target.value })} placeholder="+251 9…" />
        </Field>
        <Field id="cc-channel" label="Send by" error={fields?.preferredChannel}>
          <select id="cc-channel" value={form.preferredChannel} onChange={(e) => setForm({ ...form, preferredChannel: e.target.value })}>
            <option value="None">No messages</option><option value="Sms">SMS</option><option value="Telegram" disabled={!contact.telegramLinked}>Telegram{contact.telegramLinked ? "" : " (link first)"}</option>
          </select>
        </Field>
        <Field id="cc-lang" label="Language">
          <select id="cc-lang" value={form.language} onChange={(e) => setForm({ ...form, language: e.target.value })}>
            <option value="en">English</option><option value="am">Amharic (አማርኛ)</option><option value="ti">Tigrinya (ትግርኛ)</option>
          </select>
        </Field>
      </div>
      <fieldset className="consent-box">
        <legend>The patient agrees to receive</legend>
        <label className="check-row"><input type="checkbox" checked={form.consentReminders} onChange={(e) => setForm({ ...form, consentReminders: e.target.checked })} /> Appointment confirmations, reminders and after-visit follow-up</label>
        <label className="check-row"><input type="checkbox" checked={form.consentRecalls} onChange={(e) => setForm({ ...form, consentRecalls: e.target.checked })} /> Invitations to book check-ups and reviews when they are due</label>
        <label className="check-row"><input type="checkbox" checked={form.consentTips} onChange={(e) => setForm({ ...form, consentTips: e.target.checked })} /> Health tips approved by our medical team</label>
        {(form.consentReminders || form.consentTips || form.consentRecalls) && (
          <Field id="cc-method" label="How did the patient agree?" error={fields?.consentMethod}>
            <select id="cc-method" value={form.consentMethod} onChange={(e) => setForm({ ...form, consentMethod: e.target.value })}>
              <option value="Verbal">Said yes at the desk (verbal)</option><option value="Written">Signed the consent form (written)</option><option value="Telegram">Agreed in Telegram</option>
            </select>
          </Field>
        )}
      </fieldset>
      <ErrorAlert error={error} />
      <div className="btn-row">
        <button className="btn btn-primary" type="submit">Save consent</button>
        <button className="btn" type="button" onClick={() => void link()}><Icon name="send" size={16} /> {contact.telegramLinked ? "Re-link Telegram" : "Link Telegram"}</button>
        {contact.telegramLinked && <span className="badge badge-ok">✓ Telegram linked</span>}
        {saved && <span className="badge badge-ok" role="status">✓ Saved</span>}
      </div>
      {code && (
        <div className="alert alert-info" role="status">
          Ask the patient to open the clinic's Telegram bot and send <strong className="mono">/start {code.code}</strong>
          {code.deepLink && <> (or open <span className="mono">{code.deepLink}</span>)</>}. The code works once, until {new Date(code.expiresAt).toLocaleTimeString()}.
          {!code.botLive && <div className="small">Sandbox: no Telegram bot is connected, so nothing is sent outside this system.</div>}
          <button type="button" className="link-button" onClick={() => setReload((k) => k + 1)}>Refresh status</button>
        </div>
      )}
      {contact.history.length > 0 && (
        <details>
          <summary>Consent history ({contact.history.length})</summary>
          <ul className="small">
            {contact.history.map((h, i) => (
              <li key={i}>{new Date(h.recordedAt).toLocaleString()}: reminders {h.reminders ? "yes" : "no"}, check-up invitations {h.recalls ? "yes" : "no"}, tips {h.tips ? "yes" : "no"} — {h.method}</li>
            ))}
          </ul>
        </details>
      )}
    </form>
  );
}
