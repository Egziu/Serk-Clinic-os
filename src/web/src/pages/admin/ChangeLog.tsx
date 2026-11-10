import { useEffect, useState } from "react";
import { api } from "../../api/client";
import { ErrorAlert } from "../../components/common";
import { fmtDateTime, type Change } from "./adminTypes";

const ACTION_LABEL: Record<string, string> = {
  "organisation.create": "Organisation created", "organisation.rename": "Organisation renamed", "facility.create": "Facility added", "facility.update": "Facility updated",
  "staff.create": "Person created", "staff.rename": "Name changed", "staff.activate": "Account reactivated", "staff.deactivate": "Account deactivated",
  "staff.unlock": "Account unlocked", "staff.password_reset": "Password reset", "staff.sessions_revoked": "Sessions ended",
  "practitioner.upsert": "Practitioner profile saved", "assignment.create": "Access given", "assignment.update": "Access changed", "assignment.deactivate": "Access removed",
  "role.create": "Role created", "role.update": "Role changed", "super_admin.grant": "Super admin granted", "super_admin.revoke": "Super admin revoked",
};

export function ChangeList({ changes }: { changes: Change[] }) {
  if (changes.length === 0) return <p className="muted">No changes recorded.</p>;
  return (
    <table aria-label="Change history">
      <thead><tr><th>When</th><th>Who</th><th>What</th><th>Details</th></tr></thead>
      <tbody>
        {changes.map((c) => (
          <tr key={c.id}>
            <td>{fmtDateTime(c.occurredAt)}</td>
            <td>{c.actor}</td>
            <td>{ACTION_LABEL[c.action] ?? c.action}{c.reason && <><br /><span className="hint">Reason: {c.reason}</span></>}</td>
            <td>
              <details>
                <summary>Before / after</summary>
                <pre className="json">{c.before ? `before: ${c.before}\n` : ""}{c.after ? `after:  ${c.after}` : ""}</pre>
              </details>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function ChangeLog() {
  const [changes, setChanges] = useState<Change[]>([]);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { api.get<Change[]>("/api/v1/platform/changes?limit=100").then((r) => setChanges(r.data)).catch(setError); }, []);
  return (
    <section className="panel" aria-labelledby="log-h">
      <h2 id="log-h">Change log</h2>
      <p className="status-line">Every access-control change, who made it and why. The log is append-only: it cannot be edited or deleted, even in the database.</p>
      <ErrorAlert error={error} />
      <ChangeList changes={changes} />
    </section>
  );
}
