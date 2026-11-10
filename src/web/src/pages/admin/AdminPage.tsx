import { useCallback, useEffect, useState, type KeyboardEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert } from "../../components/common";
import { ChangeLog } from "./ChangeLog";
import { OrganisationsAdmin } from "./OrganisationsAdmin";
import { RolesAdmin } from "./RolesAdmin";
import { StaffAdmin } from "./StaffAdmin";
import { SuperAdminsAdmin } from "./SuperAdminsAdmin";
import type { Catalog, Organisation, Overview } from "./adminTypes";

const TABS = [
  { id: "overview", label: "Overview" },
  { id: "people", label: "People & access" },
  { id: "roles", label: "Roles" },
  { id: "organisations", label: "Organisations" },
  { id: "super-admins", label: "Super admins" },
  { id: "log", label: "Change log" },
] as const;
type Tab = (typeof TABS)[number]["id"];

/** System administration console (super administrators only). Shows no patient data. */
export function AdminPage({ currentUserId }: { currentUserId: string }) {
  const [tab, setTab] = useState<Tab>("overview");
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [organisations, setOrganisations] = useState<Organisation[]>([]);
  const [overview, setOverview] = useState<Overview | null>(null);
  const [error, setError] = useState<unknown>(null);

  const reload = useCallback(() => {
    api.get<Organisation[]>("/api/v1/platform/organisations").then((r) => setOrganisations(r.data)).catch(setError);
    api.get<Overview>("/api/v1/platform/overview").then((r) => setOverview(r.data)).catch(setError);
  }, []);
  useEffect(() => {
    api.get<Catalog>("/api/v1/platform/catalog").then((r) => setCatalog(r.data)).catch(setError);
    reload();
  }, [reload]);

  // WAI-ARIA tabs: arrow keys move between tabs.
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const i = TABS.findIndex((t) => t.id === tab);
    const next = e.key === "ArrowRight" ? (i + 1) % TABS.length : e.key === "ArrowLeft" ? (i - 1 + TABS.length) % TABS.length : e.key === "Home" ? 0 : e.key === "End" ? TABS.length - 1 : -1;
    if (next >= 0) {
      e.preventDefault();
      setTab(TABS[next]!.id);
      document.getElementById(`tab-${TABS[next]!.id}`)?.focus();
    }
  };

  return (
    <>
      <div className="page-header">
        <div>
          <h1>System administration</h1>
          <p>Organisations, people, roles and access across SERK Clinic OS. No patient data is shown here.</p>
        </div>
      </div>
      <ErrorAlert error={error} />
      <div className="tabs" role="tablist" aria-label="Administration sections" onKeyDown={onKey}>
        {TABS.map((t) => (
          <button key={t.id} id={`tab-${t.id}`} role="tab" aria-selected={tab === t.id} aria-controls={`panel-${t.id}`} tabIndex={tab === t.id ? 0 : -1} onClick={() => setTab(t.id)}>{t.label}</button>
        ))}
      </div>
      <div id={`panel-${tab}`} role="tabpanel" aria-labelledby={`tab-${tab}`}>
        {tab === "overview" && overview && (
          <div className="kpi-grid" role="group" aria-label="System overview">
            {[
              ["Organisations", overview.organisations], ["Facilities", overview.facilities], ["People", `${overview.activeStaff} active / ${overview.staff}`],
              ["Access assignments in effect", overview.activeAssignments], ["Custom roles", overview.customRoles], ["Super administrators", overview.platformAdmins],
              ["Locked accounts", overview.lockedAccounts], ["Password changes pending", overview.pendingPasswordChanges], ["Changes in the last 7 days", overview.changesLast7Days],
            ].map(([label, value]) => (
              <div key={label} className="kpi"><div className="kpi-label">{label}</div><div className="kpi-value">{value}</div></div>
            ))}
          </div>
        )}
        {tab === "people" && catalog && <StaffAdmin catalog={catalog} organisations={organisations} currentUserId={currentUserId} />}
        {tab === "roles" && catalog && <RolesAdmin catalog={catalog} organisations={organisations} />}
        {tab === "organisations" && <OrganisationsAdmin organisations={organisations} onChanged={reload} />}
        {tab === "super-admins" && <SuperAdminsAdmin currentUserId={currentUserId} />}
        {tab === "log" && <ChangeLog />}
      </div>
    </>
  );
}
