import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { Icon } from "../../components/icons";
import { BarList, LineChart } from "../../components/charts";

interface Post { id: string; platform: string; plannedAt: string; title: string; caption: string; status: string; campaign: string; showsPatient: boolean; patientConsentRef: string | null; publishedUrl: string | null; publishedAt: string | null; version: number }
interface Metric { id: string; platform: string; measuredOn: string; followers: number; reach: number; engagements: number; inquiries: number; bookings: number; engagementRate: number | null }
interface PlatformSummary { platform: string; followers: number; followerGrowth: number | null; reach30: number; engagements30: number; engagementRate: number | null; inquiries30: number; bookings30: number; published30: number; planned14: number }
interface Summary { platforms: PlatformSummary[]; plannedNext14: number; publishedLast30: number; ideas: number }

const PLATFORMS = ["Facebook", "Instagram", "TikTok", "Telegram", "X", "LinkedIn", "YouTube", "WhatsApp"];
const STATUSES = ["Idea", "Draft", "Scheduled", "Published", "Cancelled"];
const pct = (v: number | null) => (v === null ? "—" : `${(v * 100).toFixed(1)}%`);
const localInput = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);

/** Social media planner and tracker. Planning only — nothing is posted automatically. */
export function SocialPage() {
  const [tab, setTab] = useState<"overview" | "planner" | "results">("overview");
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Social media</h1>
          <p>Plan what the clinic shares, keep patient photos and stories consent-safe, and see which platforms bring inquiries and bookings.</p>
        </div>
      </div>
      <div className="tabs" role="tablist" aria-label="Social media">
        {([["overview", "Overview"], ["planner", "Content planner"], ["results", "Results"]] as const).map(([id, label]) => (
          <button key={id} role="tab" id={`stab-${id}`} aria-selected={tab === id} aria-controls={`spanel-${id}`} onClick={() => setTab(id)}>{label}</button>
        ))}
      </div>
      <div role="tabpanel" id={`spanel-${tab}`} aria-labelledby={`stab-${tab}`}>
        {tab === "overview" && <Overview />}
        {tab === "planner" && <Planner />}
        {tab === "results" && <Results />}
      </div>
    </>
  );
}

function Overview() {
  const [s, setS] = useState<Summary | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { api.get<Summary>("/api/v1/social/summary").then((r) => setS(r.data)).catch(setError); }, []);
  if (!s) return <ErrorAlert error={error} />;
  return (
    <>
      <div className="kpi-grid" role="group" aria-label="Social media indicators">
        <div className="kpi"><div className="kpi-label">Planned next 14 days</div><div className="kpi-value">{s.plannedNext14}</div></div>
        <div className="kpi"><div className="kpi-label">Published last 30 days</div><div className="kpi-value">{s.publishedLast30}</div></div>
        <div className="kpi"><div className="kpi-label">Ideas waiting</div><div className="kpi-value">{s.ideas}</div></div>
        <div className="kpi"><div className="kpi-label">Bookings from social (30 d)</div><div className="kpi-value">{s.platforms.reduce((a, p) => a + p.bookings30, 0)}</div><div className="kpi-sub">as recorded by staff</div></div>
      </div>
      {s.platforms.length === 0 && <p className="muted">Record your first results under “Results” to see each platform here.</p>}
      <div className="role-cards">
        {s.platforms.map((p) => (
          <article key={p.platform} className="panel" aria-label={p.platform}>
            <div className="panel-header"><h2>{p.platform}</h2><span className="badge badge-info">{p.followers.toLocaleString()} followers</span></div>
            <dl className="metric-grid">
              <div><dt>Growth (4 weeks)</dt><dd>{pct(p.followerGrowth)}</dd></div>
              <div><dt>Reach (30 d)</dt><dd>{p.reach30.toLocaleString()}</dd></div>
              <div><dt>Engagement rate</dt><dd>{pct(p.engagementRate)}</dd></div>
              <div><dt>Inquiries / bookings</dt><dd>{p.inquiries30} / {p.bookings30}</dd></div>
              <div><dt>Published / planned</dt><dd>{p.published30} / {p.planned14}</dd></div>
            </dl>
          </article>
        ))}
      </div>
      {s.platforms.length > 0 && (
        <section className="panel" aria-labelledby="soc-book"><h2 id="soc-book">Bookings by platform (30 days)</h2>
          <div className="chart-narrow"><BarList title="Bookings by platform" items={s.platforms.map((p) => ({ label: p.platform, value: p.bookings30 }))} /></div>
        </section>
      )}
    </>
  );
}

const emptyPost = () => ({ platform: "Facebook", plannedAt: localInput(new Date(Date.now() + 86400000)), title: "", caption: "", status: "Draft", campaign: "", showsPatient: false, patientConsentRef: "", publishedUrl: "" });

function Planner() {
  const [posts, setPosts] = useState<Post[] | null>(null);
  const [form, setForm] = useState(emptyPost());
  const [editing, setEditing] = useState<Post | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0);
  useEffect(() => { api.get<Post[]>("/api/v1/social/posts").then((r) => setPosts(r.data)).catch(setError); }, [reload]);
  const byWeek = useMemo(() => {
    const m = new Map<string, Post[]>();
    for (const p of posts ?? []) {
      const d = new Date(p.plannedAt);
      const monday = new Date(d); monday.setDate(d.getDate() - ((d.getDay() + 6) % 7)); monday.setHours(0, 0, 0, 0);
      const key = monday.toISOString().slice(0, 10);
      m.set(key, [...(m.get(key) ?? []), p]);
    }
    return [...m.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [posts]);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    const body = { ...form, plannedAt: new Date(form.plannedAt).toISOString(), patientConsentRef: form.patientConsentRef || null, publishedUrl: form.publishedUrl || null };
    try {
      if (editing) await api.put(`/api/v1/social/posts/${editing.id}`, body, { ifMatch: `"${editing.version}"` });
      else await api.post("/api/v1/social/posts", body);
      setForm(emptyPost()); setEditing(null); setReload((k) => k + 1);
    } catch (err) { setError(err); }
  };
  const edit = (p: Post) => {
    setEditing(p);
    setForm({ platform: p.platform, plannedAt: localInput(new Date(p.plannedAt)), title: p.title, caption: p.caption, status: p.status, campaign: p.campaign, showsPatient: p.showsPatient, patientConsentRef: p.patientConsentRef ?? "", publishedUrl: p.publishedUrl ?? "" });
  };
  return (
    <div className="setup-grid">
      <section aria-labelledby="plan-h">
        <h2 id="plan-h"><Icon name="calendar" /> Plan</h2>
        {byWeek.length === 0 && <p className="muted">Nothing planned yet.</p>}
        {byWeek.map(([week, list]) => (
          <div key={week} className="panel">
            <h3 className="small">Week of {new Date(week).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })}</h3>
            <ul className="timeline">
              {list.map((p) => (
                <li key={p.id}>
                  <span className={`badge ${p.status === "Published" ? "badge-ok" : p.status === "Scheduled" ? "badge-info" : p.status === "Cancelled" ? "badge-neutral" : "badge-warning"}`}>{p.status}</span>{" "}
                  <strong>{p.title}</strong> <span className="muted">· {p.platform} · {new Date(p.plannedAt).toLocaleString(undefined, { weekday: "short", hour: "2-digit", minute: "2-digit" })}</span>
                  {p.campaign && <span className="badge badge-neutral">{p.campaign}</span>}
                  {p.showsPatient && <span className="badge badge-warning">Patient · consent {p.patientConsentRef}</span>}
                  <button className="link-button small" onClick={() => edit(p)}>Edit</button>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </section>
      <form className="panel" onSubmit={save} aria-labelledby="post-form-h">
        <h2 id="post-form-h">{editing ? "Edit post" : "New post"}</h2>
        <div className="form-grid">
          <Field id="p-platform" label="Platform"><select id="p-platform" value={form.platform} onChange={(e) => setForm({ ...form, platform: e.target.value })}>{PLATFORMS.map((p) => <option key={p}>{p}</option>)}</select></Field>
          <Field id="p-when" label="Planned for"><input id="p-when" type="datetime-local" value={form.plannedAt} onChange={(e) => setForm({ ...form, plannedAt: e.target.value })} /></Field>
          <Field id="p-status" label="Status"><select id="p-status" value={form.status} onChange={(e) => setForm({ ...form, status: e.target.value })}>{STATUSES.map((s) => <option key={s}>{s}</option>)}</select></Field>
          <Field id="p-campaign" label="Campaign" hint="optional"><input id="p-campaign" value={form.campaign} maxLength={80} onChange={(e) => setForm({ ...form, campaign: e.target.value })} /></Field>
        </div>
        <Field id="p-title" label="Title / idea"><input id="p-title" value={form.title} maxLength={120} onChange={(e) => setForm({ ...form, title: e.target.value })} /></Field>
        <Field id="p-caption" label="Caption" hint={`${form.caption.length}/2200`}><textarea id="p-caption" rows={4} maxLength={2200} value={form.caption} onChange={(e) => setForm({ ...form, caption: e.target.value })} /></Field>
        <label className="check-row"><input type="checkbox" checked={form.showsPatient} onChange={(e) => setForm({ ...form, showsPatient: e.target.checked })} /> Shows a patient (photo, video, story or testimonial)</label>
        {form.showsPatient && <Field id="p-consent" label="Signed media-consent reference" hint="required"><input id="p-consent" value={form.patientConsentRef} onChange={(e) => setForm({ ...form, patientConsentRef: e.target.value })} placeholder="e.g. MC-2026-014" /></Field>}
        {form.status === "Published" && <Field id="p-url" label="Link to the published post" hint="https://"><input id="p-url" type="url" value={form.publishedUrl} onChange={(e) => setForm({ ...form, publishedUrl: e.target.value })} /></Field>}
        <ErrorAlert error={error} />
        <div className="btn-row">
          <button className="btn btn-primary" type="submit" disabled={form.title.trim().length < 3}>{editing ? "Save changes" : "Add to plan"}</button>
          {editing && <button className="btn" type="button" onClick={() => { setEditing(null); setForm(emptyPost()); }}>Cancel</button>}
        </div>
      </form>
    </div>
  );
}

function Results() {
  const [rows, setRows] = useState<Metric[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [form, setForm] = useState({ platform: "Facebook", measuredOn: new Date().toISOString().slice(0, 10), followers: 0, reach: 0, engagements: 0, inquiries: 0, bookings: 0 });
  const [reload, setReload] = useState(0);
  useEffect(() => { api.get<Metric[]>("/api/v1/social/metrics?days=180").then((r) => setRows(r.data)).catch(setError); }, [reload]);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    try { await api.post("/api/v1/social/metrics", form); setReload((k) => k + 1); } catch (err) { setError(err); }
  };
  const platforms = [...new Set((rows ?? []).map((r) => r.platform))];
  const days = [...new Set((rows ?? []).map((r) => r.measuredOn))].sort();
  const points = days.map((d) => ({ label: d, values: platforms.map((p) => rows!.find((r) => r.platform === p && r.measuredOn === d)?.followers ?? 0) }));
  const num = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: Math.max(0, Number(e.target.value)) });
  return (
    <div className="setup-grid">
      <section>
        {points.length > 1 && <section className="panel"><LineChart title="Followers by platform" points={points} seriesNames={platforms} /></section>}
        <div className="panel table-scroll">
          <table>
            <caption className="sr-only">Recorded social media results</caption>
            <thead><tr><th scope="col">Date</th><th scope="col">Platform</th><th scope="col">Followers</th><th scope="col">Reach</th><th scope="col">Engagement</th><th scope="col">Inquiries</th><th scope="col">Bookings</th></tr></thead>
            <tbody>{[...(rows ?? [])].reverse().map((r) => (
              <tr key={r.id}><td>{r.measuredOn}</td><td>{r.platform}</td><td className="money">{r.followers.toLocaleString()}</td><td className="money">{r.reach.toLocaleString()}</td><td className="money">{pct(r.engagementRate)}</td><td className="money">{r.inquiries}</td><td className="money">{r.bookings}</td></tr>
            ))}</tbody>
          </table>
          {rows?.length === 0 && <p className="muted">No results recorded yet.</p>}
        </div>
      </section>
      <form className="panel" onSubmit={save} aria-labelledby="res-h">
        <h2 id="res-h">Record results</h2>
        <p className="muted small">Copy the numbers from each platform's insights page (weekly is enough). Re-entering the same day replaces it.</p>
        <div className="form-grid">
          <Field id="m-platform" label="Platform"><select id="m-platform" value={form.platform} onChange={(e) => setForm({ ...form, platform: e.target.value })}>{PLATFORMS.map((p) => <option key={p}>{p}</option>)}</select></Field>
          <Field id="m-day" label="Date"><input id="m-day" type="date" value={form.measuredOn} max={new Date().toISOString().slice(0, 10)} onChange={(e) => setForm({ ...form, measuredOn: e.target.value })} /></Field>
          <Field id="m-followers" label="Followers"><input id="m-followers" type="number" min={0} value={form.followers} onChange={num("followers")} /></Field>
          <Field id="m-reach" label="Reach"><input id="m-reach" type="number" min={0} value={form.reach} onChange={num("reach")} /></Field>
          <Field id="m-eng" label="Engagements" hint="likes, comments, shares"><input id="m-eng" type="number" min={0} value={form.engagements} onChange={num("engagements")} /></Field>
          <Field id="m-inq" label="Inquiries" hint="messages, calls"><input id="m-inq" type="number" min={0} value={form.inquiries} onChange={num("inquiries")} /></Field>
          <Field id="m-book" label="Bookings"><input id="m-book" type="number" min={0} value={form.bookings} onChange={num("bookings")} /></Field>
        </div>
        <ErrorAlert error={error} />
        <button className="btn btn-primary" type="submit">Save results</button>
      </form>
    </div>
  );
}
