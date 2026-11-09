import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { EncounterListItem } from "../api/types";
import { AllergyBadge, ErrorAlert, StatusBadge } from "../components/common";
import { formatDualDate, formatTime, localIsoDate } from "../lib/format";
import { t } from "../i18n";

export function WorklistPage({ onOpen }: { onOpen: (id: string) => void }) {
  const [items, setItems] = useState<EncounterListItem[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const today = localIsoDate(new Date());

  useEffect(() => {
    api.get<EncounterListItem[]>(`/api/v1/encounters?day=${today}`).then((r) => setItems(r.data)).catch(setError);
  }, [today]);

  return (
    <section aria-labelledby="worklist-h">
      <div className="panel-header">
        <h1 id="worklist-h">{t("worklist")}</h1>
        <span className="status-line">{formatDualDate(today)}</span>
      </div>
      <ErrorAlert error={error} />
      <div className="panel">
        <table aria-label="Encounters">
          <thead><tr><th>Arrived (EAT)</th><th>Patient</th><th>Allergies</th><th>Note</th><th><span className="sr-only">Open</span></th></tr></thead>
          <tbody>
            {items?.length === 0 && <tr><td colSpan={5} className="muted">No checked-in patients for you today.</td></tr>}
            {items?.map((e) => (
              <tr key={e.id}>
                <td>{formatTime(e.startedAt)}</td>
                <td><strong>{e.patientDisplayName}</strong><br /><span className="mono">{e.patientMrn}</span></td>
                <td><AllergyBadge status={e.allergyStatus} /></td>
                <td><StatusBadge status={e.status} /></td>
                <td><button className="btn" onClick={() => onOpen(e.id)} aria-label={`Open encounter for ${e.patientDisplayName}`}>Open</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
