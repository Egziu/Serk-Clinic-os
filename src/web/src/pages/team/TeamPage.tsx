import { useEffect, useMemo, useState } from "react";
import { api, postForFile, saveFile } from "../../api/client";
import { ErrorAlert } from "../../components/common";
import { Icon } from "../../components/icons";
import { AdminScopeContext } from "../admin/adminScope";
import { StaffAdmin } from "../admin/StaffAdmin";
import { RolesAdmin } from "../admin/RolesAdmin";
import type { Catalog, Organisation } from "../admin/adminTypes";

interface Capability { code: string; group: string; label: string; description: string; sensitivity: string }
interface MatrixRole { code: string; name: string; description: string; builtIn: boolean; responsibilities: string[]; capabilities: string[]; people: number }
interface Matrix { capabilities: Capability[]; roles: MatrixRole[]; policyVersion: number }
interface Assignment { facility: string; roles: string[]; added: string[]; removed: string[]; active: boolean; validUntil: string | null }
interface Member { userId: string; username: string; displayName: string; accountActive: boolean; locked: boolean; lastLoginAt: string | null; assignments: Assignment[] }

const sensitivityBadge: Record<string, string> = { Clinical: "badge-danger", PatientData: "badge-warning", Administrative: "badge-info", Standard: "badge-neutral" };
const sensitivityLabel: Record<string, string> = { Clinical: "Clinical", PatientData: "Patient data", Administrative: "Administrative", Standard: "Standard" };

const ORG_SCOPE = { base: "/api/v1/organisation/admin", organisationMode: true };

export function TeamPage({ canSeeTeam, canExport, canManage, currentUserId }: { canSeeTeam: boolean; canExport: boolean; canManage: boolean; currentUserId: string }) {
  const [tab, setTab] = useState<"roles" | "matrix" | "team" | "manage" | "custom">(canManage ? "manage" : "roles");
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [organisations, setOrganisations] = useState<Organisation[]>([]);
  const [matrix, setMatrix] = useState<Matrix | null>(null);
  const [team, setTeam] = useState<Member[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    api.get<Matrix>("/api/v1/organisation/roles").then((r) => setMatrix(r.data)).catch(setError);
    if (canSeeTeam) api.get<Member[]>("/api/v1/organisation/team").then((r) => setTeam(r.data)).catch(setError);
    if (canManage) {
      api.get<Catalog>(`${ORG_SCOPE.base}/catalog`).then((r) => setCatalog(r.data)).catch(setError);
      api.get<Organisation[]>(`${ORG_SCOPE.base}/organisations`).then((r) => setOrganisations(r.data)).catch(setError);
    }
  }, [canSeeTeam, canManage]);

  const exportTeam = async () => {
    try { saveFile(await postForFile("/api/v1/exports/staff.access", { format: "csv" })); } catch (e) { setError(e); }
  };

  type Tab = "roles" | "matrix" | "team" | "manage" | "custom";
  const tabs: [Tab, string][] = [
    ...(canManage ? [["manage", "Manage people"], ["custom", "Custom roles"]] as [Tab, string][] : []),
    ["roles", "Roles & responsibilities"], ["matrix", "Permission matrix"],
    ...(canSeeTeam && !canManage ? [["team", "Team"] as [Tab, string]] : []),
  ];
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Team & roles</h1>
          <p>Who is responsible for what, and exactly which permissions each role carries{matrix ? ` (policy version ${matrix.policyVersion})` : ""}.</p>
        </div>
        {canSeeTeam && canExport && <button className="btn" onClick={() => void exportTeam()}><Icon name="download" size={16} /> Export staff and access (CSV)</button>}
      </div>
      <ErrorAlert error={error} />
      <div className="tabs" role="tablist" aria-label="Team and roles">
        {tabs.map(([id, label]) => (
          <button key={id} role="tab" id={`tab-${id}`} aria-selected={tab === id} aria-controls={`panel-${id}`} onClick={() => setTab(id)}>{label}</button>
        ))}
      </div>
      <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`}>
        <AdminScopeContext.Provider value={ORG_SCOPE}>
          {tab === "manage" && catalog && organisations.length > 0 && <StaffAdmin catalog={catalog} organisations={organisations} currentUserId={currentUserId} />}
          {tab === "custom" && catalog && organisations.length > 0 && <RolesAdmin catalog={catalog} organisations={organisations} />}
        </AdminScopeContext.Provider>
        {tab === "roles" && matrix && <RoleCards matrix={matrix} />}
        {tab === "matrix" && matrix && <PermissionMatrix matrix={matrix} />}
        {tab === "team" && team && matrix && <TeamList team={team} matrix={matrix} />}
      </div>
      <p className="muted">
        {canManage
          ? "Every change is recorded in the change log. You cannot change your own access; people who also work for another organisation are changed by the system administrator."
          : "Changes to who has which role are made by the clinic owner or a system administrator and recorded in the change log."}
      </p>
    </>
  );
}

function RoleCards({ matrix }: { matrix: Matrix }) {
  const label = useMemo(() => new Map(matrix.capabilities.map((c) => [c.code, c])), [matrix]);
  return (
    <div className="role-cards">
      {matrix.roles.map((r) => (
        <article key={r.code} className="panel role-card">
          <div className="panel-header">
            <h2>{r.name}</h2>
            <span className="badge badge-neutral">{r.people} {r.people === 1 ? "person" : "people"}</span>
          </div>
          <p>{r.description}</p>
          {r.responsibilities.length > 0 && (
            <>
              <h3>Responsible for</h3>
              <ul className="tick-list">{r.responsibilities.map((x) => <li key={x}>{x}</li>)}</ul>
            </>
          )}
          <details>
            <summary>{r.capabilities.length} permissions</summary>
            <ul className="cap-list">
              {r.capabilities.map((c) => {
                const info = label.get(c);
                return <li key={c}><span className={`badge ${sensitivityBadge[info?.sensitivity ?? "Standard"]}`}>{sensitivityLabel[info?.sensitivity ?? "Standard"]}</span> {info?.label ?? c}</li>;
              })}
            </ul>
          </details>
          {!r.builtIn && <p className="muted">Custom role for this organisation.</p>}
        </article>
      ))}
    </div>
  );
}

function PermissionMatrix({ matrix }: { matrix: Matrix }) {
  const groups = useMemo(() => {
    const m = new Map<string, Capability[]>();
    for (const c of matrix.capabilities) m.set(c.group, [...(m.get(c.group) ?? []), c]);
    return [...m.entries()];
  }, [matrix]);
  return (
    <div className="panel table-scroll" role="region" aria-label="Permission matrix" tabIndex={0}>
      <table className="matrix">
        <caption className="sr-only">Permissions by role. A tick means the role includes the permission.</caption>
        <thead>
          <tr><th scope="col">Permission</th>{matrix.roles.map((r) => <th key={r.code} scope="col">{r.name}</th>)}</tr>
        </thead>
        <tbody>
          {groups.map(([group, caps]) => [
            <tr key={group} className="group-row"><th colSpan={matrix.roles.length + 1} scope="colgroup">{group}</th></tr>,
            ...caps.map((c) => (
              <tr key={c.code}>
                <th scope="row" title={c.description}>{c.label} <span className={`badge ${sensitivityBadge[c.sensitivity]}`}>{sensitivityLabel[c.sensitivity]}</span></th>
                {matrix.roles.map((r) => (
                  <td key={r.code}>{r.capabilities.includes(c.code) ? <span aria-label="Yes">✓</span> : <span className="muted" aria-label="No">·</span>}</td>
                ))}
              </tr>
            )),
          ])}
        </tbody>
      </table>
    </div>
  );
}

function TeamList({ team, matrix }: { team: Member[]; matrix: Matrix }) {
  const roleName = new Map(matrix.roles.map((r) => [r.code, r.name]));
  const [q, setQ] = useState("");
  const shown = team.filter((m) => (m.displayName + " " + m.username).toLowerCase().includes(q.toLowerCase()));
  return (
    <section className="panel" aria-label="Team">
      <div className="field filter-field">
        <label htmlFor="team-filter">Find a person</label>
        <input id="team-filter" type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name or username" />
      </div>
      <div className="table-scroll">
        <table>
          <thead><tr><th scope="col">Person</th><th scope="col">Account</th><th scope="col">Assignments</th><th scope="col">Last sign-in</th></tr></thead>
          <tbody>
            {shown.map((m) => (
              <tr key={m.userId}>
                <td><strong>{m.displayName}</strong><br /><span className="muted">{m.username}</span></td>
                <td>{!m.accountActive ? <span className="badge badge-neutral">Deactivated</span> : m.locked ? <span className="badge badge-danger">Locked</span> : <span className="badge badge-ok">✓ Active</span>}</td>
                <td>
                  {m.assignments.map((a, i) => (
                    <div key={i} className={a.active ? "" : "muted"}>
                      {a.facility}: {a.roles.map((r) => roleName.get(r) ?? r).join(", ") || "no role"}
                      {a.added.length > 0 && <span className="badge badge-info"> +{a.added.length}</span>}
                      {a.removed.length > 0 && <span className="badge badge-warning"> −{a.removed.length}</span>}
                      {!a.active && " (inactive)"}
                    </div>
                  ))}
                </td>
                <td>{m.lastLoginAt ? new Date(m.lastLoginAt).toLocaleString() : <span className="muted">Never</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
