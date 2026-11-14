import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { InsightsResponse } from "../pages/setup/insightTypes";
import { Icon } from "./icons";

const severityLabel = { Urgent: "Urgent", Attention: "Needs attention", Info: "For information" } as const;
const severityClass = { Urgent: "badge-danger", Attention: "badge-warning", Info: "badge-info" } as const;

/**
 * Explainable, rule-based observations for the signed-in person's permissions. Each card states the numbers and the
 * rule that produced it ("Why am I seeing this?"), so nothing is a black box. Never clinical advice.
 */
export function InsightsPanel({ onNavigate, compact = false }: { onNavigate: (path: string) => void; compact?: boolean }) {
  const [data, setData] = useState<InsightsResponse | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    api.get<InsightsResponse>("/api/v1/insights").then((r) => setData(r.data)).catch(() => setFailed(true));
  }, []);
  if (failed) return null;
  if (!data) return <p className="muted" role="status">Looking for things that need attention…</p>;
  const list = compact ? data.insights.slice(0, 4) : data.insights;
  return (
    <section className="panel insights" aria-labelledby="insights-title">
      <div className="panel-header">
        <h2 id="insights-title"><Icon name="spark" /> What needs attention</h2>
        <span className="muted">Rule-based checks · updated {new Date(data.generatedAt).toLocaleTimeString()}</span>
      </div>
      {list.length === 0 ? (
        <p className="all-clear"><Icon name="check" /> Nothing needs attention right now.</p>
      ) : (
        <ul className="insight-list">
          {list.map((i) => (
            <li key={i.code + i.detail} className={`insight insight-${i.severity.toLowerCase()}`}>
              <div className="insight-head">
                <span className={`badge ${severityClass[i.severity]}`}>{i.severity === "Urgent" ? "⚠ " : ""}{severityLabel[i.severity]}</span>
                <span className="muted">{i.area}</span>
              </div>
              <strong>{i.title}</strong>
              <p>{i.detail}</p>
              <details>
                <summary>Why am I seeing this?</summary>
                <p className="muted">{i.rule}</p>
              </details>
              {i.actionLabel && i.actionPath && (
                <button className="btn btn-small" onClick={() => onNavigate(i.actionPath!)}>{i.actionLabel} →</button>
              )}
            </li>
          ))}
        </ul>
      )}
      {compact && data.insights.length > list.length && <p className="muted">{data.insights.length - list.length} more on the dashboard.</p>}
    </section>
  );
}
