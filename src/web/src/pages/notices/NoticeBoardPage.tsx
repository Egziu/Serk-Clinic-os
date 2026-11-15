import { useEffect, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";

interface Notice { id: string; title: string; body: string; priority: "Normal" | "Important" | "Urgent"; audienceRoles: string[]; allFacilities: boolean; pinned: boolean; requiresAck: boolean; expiresAt: string | null; source: string; authorName: string; createdAt: string; updatedAt: string; read: boolean; acknowledged: boolean; canArchive: boolean; acknowledgements: number; readCount: number }
interface Receipts { audienceSize: number; receipts: { name: string; readAt: string; acknowledgedAt: string | null }[]; notYetAcknowledged: string[] }

const ROLES: [string, string][] = [["Receptionist", "Reception"], ["Nurse", "Nurses"], ["Clinician", "Clinicians"], ["Dentist", "Dentists"], ["Cashier", "Cashiers"], ["FinanceSupervisor", "Finance"], ["ClinicAdmin", "Administrators"], ["Owner", "Owners"]];
const priorityClass = { Urgent: "badge-danger", Important: "badge-warning", Normal: "badge-info" } as const;

/** Staff notice board: announcements, must-read protocols and automatic alerts. Never patient information. */
export function NoticeBoardPage({ canPost, canManage, onChanged }: { canPost: boolean; canManage: boolean; onChanged: () => void }) {
  const [list, setList] = useState<Notice[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0);
  const [archived, setArchived] = useState(false);
  const [writing, setWriting] = useState(false);
  useEffect(() => { api.get<Notice[]>(`/api/v1/notices${archived ? "?archived=true" : ""}`).then((r) => setList(r.data)).catch(setError); }, [reload, archived]);
  const refresh = () => { setReload((k) => k + 1); onChanged(); };
  const mark = async (n: Notice, ack: boolean) => { try { await api.post(`/api/v1/notices/${n.id}/${ack ? "acknowledge" : "read"}`); refresh(); } catch (err) { setError(err); } };
  const archive = async (n: Notice) => { if (!window.confirm(`Take down “${n.title}”?`)) return; try { await api.post(`/api/v1/notices/${n.id}/archive`); refresh(); } catch (err) { setError(err); } };
  const unread = list?.filter((n) => !n.read || (n.requiresAck && !n.acknowledged)).length ?? 0;
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Notice board</h1>
          <p>Team announcements, protocols to read and automatic alerts. Never write patient names, MRNs or phone numbers here.</p>
        </div>
        <div className="btn-row">
          {canManage && <label className="check-row"><input type="checkbox" checked={archived} onChange={(e) => setArchived(e.target.checked)} /> Show past notices</label>}
          {canPost && <button className="btn btn-primary" onClick={() => setWriting((w) => !w)}><Icon name="megaphone" size={16} /> {writing ? "Close" : "New notice"}</button>}
        </div>
      </div>
      <ErrorAlert error={error} />
      {writing && <NoticeForm canManage={canManage} onPosted={() => { setWriting(false); refresh(); }} />}
      {list && list.length === 0 && <p className="all-clear"><Icon name="check" /> No notices right now.</p>}
      {unread > 0 && <p className="status-line" role="status">{unread} notice{unread > 1 ? "s" : ""} waiting for you.</p>}
      <div className="notice-list">
        {list?.map((n) => <NoticeCard key={n.id} n={n} onMark={mark} onArchive={archive} canSeeReceipts={canManage || n.canArchive} />)}
      </div>
    </>
  );
}

function NoticeCard({ n, onMark, onArchive, canSeeReceipts }: { n: Notice; onMark: (n: Notice, ack: boolean) => void; onArchive: (n: Notice) => void; canSeeReceipts: boolean }) {
  const [receipts, setReceipts] = useState<Receipts | null>(null);
  const pending = !n.read || (n.requiresAck && !n.acknowledged);
  const loadReceipts = async () => { if (receipts) { setReceipts(null); return; } setReceipts((await api.get<Receipts>(`/api/v1/notices/${n.id}/receipts`)).data); };
  return (
    <article className={`panel notice notice-${n.priority.toLowerCase()} ${pending ? "notice-unread" : ""}`} aria-label={n.title}>
      <div className="insight-head">
        <span className="badge-stack">
          {n.pinned && <span className="badge badge-neutral">📌 Pinned</span>}
          <span className={`badge ${priorityClass[n.priority]}`}>{n.priority === "Urgent" ? "⚠ Urgent" : n.priority}</span>
          {n.source === "System" && <span className="badge badge-neutral">Automatic</span>}
          {pending && <span className="badge badge-info">New</span>}
        </span>
        <span className="muted small">{n.authorName} · {new Date(n.updatedAt).toLocaleString()}</span>
      </div>
      <h2 className="notice-title">{n.title}</h2>
      <p className="notice-body">{n.body}</p>
      <div className="muted small">
        For: {n.audienceRoles.length === 0 ? "everyone" : n.audienceRoles.map((r) => ROLES.find((x) => x[0] === r)?.[1] ?? r).join(", ")}{n.allFacilities ? " · all branches" : " · this branch"}
        {n.expiresAt && <> · until {new Date(n.expiresAt).toLocaleDateString()}</>}
        {n.requiresAck && <> · {n.acknowledgements} acknowledged</>}
      </div>
      <div className="btn-row">
        {n.requiresAck && !n.acknowledged && <button className="btn btn-primary btn-small" onClick={() => onMark(n, true)}>I have read and understood</button>}
        {n.requiresAck && n.acknowledged && <span className="badge badge-ok">✓ You acknowledged</span>}
        {!n.requiresAck && !n.read && <button className="btn btn-small" onClick={() => onMark(n, false)}>Mark as read</button>}
        {canSeeReceipts && n.source === "Staff" && <button className="btn btn-small" onClick={() => void loadReceipts()}>{receipts ? "Hide" : "Who has read it"}</button>}
        {n.canArchive && <button className="btn btn-small" onClick={() => onArchive(n)}>Take down</button>}
      </div>
      {receipts && (
        <div className="small">
          <p>{receipts.receipts.length} of {receipts.audienceSize} have opened it.</p>
          {n.requiresAck && receipts.notYetAcknowledged.length > 0 && <p><strong>Not yet acknowledged:</strong> {receipts.notYetAcknowledged.join(", ")}</p>}
        </div>
      )}
    </article>
  );
}

function NoticeForm({ canManage, onPosted }: { canManage: boolean; onPosted: () => void }) {
  const [f, setF] = useState({ title: "", body: "", priority: "Normal", audienceRoles: [] as string[], allFacilities: false, pinned: false, requiresAck: false, expiresAt: "" });
  const [error, setError] = useState<unknown>(null);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try { await api.post("/api/v1/notices", { ...f, expiresAt: f.expiresAt ? new Date(f.expiresAt + "T23:59:00").toISOString() : null }); onPosted(); } catch (err) { setError(err); }
  };
  return (
    <form className="panel" onSubmit={submit} aria-label="New notice">
      <h2>New notice</h2>
      <Field id="nt-title" label="Title"><input id="nt-title" value={f.title} maxLength={120} onChange={(e) => setF({ ...f, title: e.target.value })} /></Field>
      <Field id="nt-body" label="Message" hint="no patient names, MRNs or phone numbers"><textarea id="nt-body" rows={4} maxLength={4000} value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} /></Field>
      <div className="form-grid">
        <Field id="nt-priority" label="Priority">
          <select id="nt-priority" value={f.priority} onChange={(e) => setF({ ...f, priority: e.target.value })}>
            <option value="Normal">Normal</option><option value="Important">Important</option>{canManage && <option value="Urgent">Urgent</option>}
          </select>
        </Field>
        <Field id="nt-expires" label="Show until" hint="optional"><input id="nt-expires" type="date" value={f.expiresAt} onChange={(e) => setF({ ...f, expiresAt: e.target.value })} /></Field>
      </div>
      <fieldset className="consent-box">
        <legend>Who should see it (none ticked = everyone)</legend>
        <div className="role-chips">
          {ROLES.map(([code, label]) => (
            <label key={code} className="check-row"><input type="checkbox" checked={f.audienceRoles.includes(code)} onChange={(e) => setF({ ...f, audienceRoles: e.target.checked ? [...f.audienceRoles, code] : f.audienceRoles.filter((x) => x !== code) })} /> {label}</label>
          ))}
        </div>
      </fieldset>
      {canManage && (
        <div className="role-chips">
          <label className="check-row"><input type="checkbox" checked={f.requiresAck} onChange={(e) => setF({ ...f, requiresAck: e.target.checked })} /> Everyone must confirm they read it</label>
          <label className="check-row"><input type="checkbox" checked={f.pinned} onChange={(e) => setF({ ...f, pinned: e.target.checked })} /> Pin to the top</label>
          <label className="check-row"><input type="checkbox" checked={f.allFacilities} onChange={(e) => setF({ ...f, allFacilities: e.target.checked })} /> All branches</label>
        </div>
      )}
      <ErrorAlert error={error} />
      <button className="btn btn-primary" type="submit" disabled={f.title.trim().length < 3 || !f.body.trim()}>Post notice</button>
    </form>
  );
}
