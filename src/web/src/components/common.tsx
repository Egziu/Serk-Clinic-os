import type { ReactNode } from "react";
import { ApiError } from "../api/client";

export function ErrorAlert({ error }: { error: unknown }) {
  if (!error) return null;
  const message = error instanceof ApiError ? error.message : "Something went wrong. Please retry.";
  const fields = error instanceof ApiError ? error.fields : undefined;
  return (
    <div className="alert alert-error" role="alert">
      <strong>Could not complete the action.</strong> {message}
      {fields && (
        <ul>
          {Object.entries(fields).map(([k, v]) => (
            <li key={k}>
              {k}: {v.join(" ")}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function Field({ id, label, hint, children, error }: { id: string; label: string; hint?: string; children: ReactNode; error?: string[] | undefined }) {
  return (
    <div className="field">
      <label htmlFor={id}>
        {label} {hint && <span className="hint">({hint})</span>}
      </label>
      {children}
      {error && (
        <div className="field-error" id={`${id}-error`}>
          {error.join(" ")}
        </div>
      )}
    </div>
  );
}

/** Allergy status shown as text + symbol, never colour alone. "Unknown" is visually distinct from "No known allergies". */
export function AllergyBadge({ status }: { status: string | null }) {
  switch (status) {
    case "NoKnownAllergies":
      return <span className="badge badge-ok">✓ No known allergies</span>;
    case "HasAllergies":
      return <span className="badge badge-danger">⚠ Allergies recorded</span>;
    case "Unknown":
      return <span className="badge badge-warning">? Allergy status not recorded</span>;
    default:
      return <span className="badge badge-neutral">Allergy status not visible to your role</span>;
  }
}

export function StatusBadge({ status }: { status: string }) {
  const cls = status === "Signed" || status === "Final" ? "badge-ok" : status === "Superseded" || status === "Cancelled" ? "badge-neutral" : "badge-warning";
  const symbol = status === "Signed" || status === "Final" ? "✓ " : status === "Draft" || status === "InProgress" ? "✎ " : "";
  const labels: Record<string, string> = { InProgress: "Draft — not signed", CheckedIn: "Checked in", NoShow: "No-show" };
  return <span className={`badge ${cls}`}>{symbol}{labels[status] ?? status}</span>;
}
