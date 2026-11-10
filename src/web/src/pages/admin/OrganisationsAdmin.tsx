import { useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import type { Organisation } from "./adminTypes";

export function OrganisationsAdmin({ organisations, onChanged }: { organisations: Organisation[]; onChanged: () => void }) {
  const [name, setName] = useState("");
  const [facilityCode, setFacilityCode] = useState("MAIN");
  const [facilityName, setFacilityName] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await api.post("/api/v1/platform/organisations", { name, facilityCode, facilityName, timeZone: "Africa/Addis_Ababa" });
      setNote(`Organisation “${name}” created.`);
      setName(""); setFacilityName(""); setFacilityCode("MAIN");
      onChanged();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <>
      <section className="panel" aria-labelledby="orgs-h">
        <h2 id="orgs-h">Organisations and facilities</h2>
        {note && <p className="alert alert-info" role="status">{note}</p>}
        {organisations.map((o) => <OrganisationCard key={o.id} org={o} onChanged={onChanged} />)}
      </section>
      <form className="panel" onSubmit={create} aria-label="New organisation">
        <h2>New organisation</h2>
        <ErrorAlert error={error} />
        <div className="grid-2">
          <Field id="org-name" label="Organisation name"><input id="org-name" value={name} onChange={(e) => setName(e.target.value)} required minLength={2} maxLength={200} /></Field>
          <Field id="org-fname" label="First facility name"><input id="org-fname" value={facilityName} onChange={(e) => setFacilityName(e.target.value)} required minLength={2} /></Field>
          <Field id="org-fcode" label="Facility code" hint="letters, digits, dashes"><input id="org-fcode" value={facilityCode} onChange={(e) => setFacilityCode(e.target.value.toUpperCase())} required maxLength={20} /></Field>
        </div>
        <button className="btn btn-primary" type="submit">Create organisation</button>
      </form>
    </>
  );
}

function OrganisationCard({ org, onChanged }: { org: Organisation; onChanged: () => void }) {
  const [adding, setAdding] = useState(false);
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [address, setAddress] = useState("");
  const [phone, setPhone] = useState("");
  const [error, setError] = useState<unknown>(null);

  const rename = async () => {
    const next = window.prompt("New organisation name", org.name);
    if (!next || next === org.name) return;
    try {
      await api.put(`/api/v1/platform/organisations/${org.id}`, { name: next });
      onChanged();
    } catch (err) {
      setError(err);
    }
  };
  const addFacility = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await api.post("/api/v1/platform/facilities", { tenantId: org.id, code, name, addressLine: address, phone, timeZone: "Africa/Addis_Ababa" });
      setAdding(false); setCode(""); setName(""); setAddress(""); setPhone("");
      onChanged();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <article className="org-card" aria-label={org.name}>
      <div className="panel-header">
        <h3>{org.name}</h3>
        <div className="btn-row">
          <span className="status-line">{org.staffCount} people · {org.customRoleCount} custom roles</span>
          <button className="btn" onClick={rename} aria-label={`Rename ${org.name}`}>Rename</button>
          <button className="btn" onClick={() => setAdding((a) => !a)} aria-expanded={adding}>+ Facility</button>
        </div>
      </div>
      <ErrorAlert error={error} />
      <ul className="facility-list">
        {org.facilities.map((f) => <li key={f.id}><strong>{f.name}</strong> <span className="mono muted">{f.code}</span>{f.addressLine && <> · {f.addressLine}</>} · {f.timeZone}</li>)}
      </ul>
      {adding && (
        <form onSubmit={addFacility} aria-label={`New facility for ${org.name}`} className="grid-2">
          <Field id={`fc-${org.id}`} label="Code"><input id={`fc-${org.id}`} value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} required maxLength={20} /></Field>
          <Field id={`fn-${org.id}`} label="Name"><input id={`fn-${org.id}`} value={name} onChange={(e) => setName(e.target.value)} required /></Field>
          <Field id={`fa-${org.id}`} label="Address" hint="optional"><input id={`fa-${org.id}`} value={address} onChange={(e) => setAddress(e.target.value)} /></Field>
          <Field id={`fp-${org.id}`} label="Phone" hint="optional"><input id={`fp-${org.id}`} value={phone} onChange={(e) => setPhone(e.target.value)} /></Field>
          <div className="btn-row"><button className="btn btn-primary" type="submit">Add facility</button></div>
        </form>
      )}
    </article>
  );
}
