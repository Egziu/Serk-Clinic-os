import { money, type CloseCheck, type CloseSnapshot } from "./financeTypes";

/** The close figures. Every figure states its currency and business day; they are different measures and do not add up. */
export function CloseFigures({ s }: { s: CloseSnapshot }) {
  const items: [string, number, string?][] = [
    ["Invoiced services", s.issuedServiceCharges, `${s.invoicesIssued} invoices`],
    ["Discounts", s.discounts],
    ["Net posted revenue", s.postedServiceRevenue],
    ["Verified collections", s.verifiedCollections.total, `cash ${money(s.verifiedCollections.cash)} · electronic ${money(s.verifiedCollections.electronic)}`],
    ["Cash expected", s.cashDrawer.expected, `${s.cashDrawer.sessions} drawer(s), float ${money(s.cashDrawer.openingFloat)}`],
    ["Cash counted", s.cashDrawer.counted, s.cashDrawer.variance === 0 ? "no difference" : `${s.cashDrawer.variance < 0 ? "shortage" : "overage"} ${money(Math.abs(s.cashDrawer.variance))}${s.cashDrawer.varianceApproved ? " (approved)" : " (not approved)"}`],
    ["Refunds paid", s.refundsActuallyPaid],
    ["Outstanding receivables", s.outstandingAccountsReceivable, "owed by patients, all days"],
    ["Unsettled provider money", s.unsettledProviderClearing, "confirmed but not yet in the bank"],
  ];
  return (
    <>
      <p className="status-line">{s.facilityName} · business day {s.businessDateLocal} ({s.timezone}) · amounts in {s.currency}</p>
      <div className="figure-grid" role="group" aria-label="Close figures">
        {items.map(([label, value, sub]) => (
          <div className="figure" key={label}>
            <div className="figure-label">{label}</div>
            <div className="figure-value">{money(value)}</div>
            {sub && <div className="figure-sub">{sub}</div>}
          </div>
        ))}
      </div>
    </>
  );
}

export function CheckList({ checks }: { checks: CloseCheck[] }) {
  return (
    <ul className="check-list" aria-label="Close checks">
      {checks.map((c) => (
        <li key={c.controlId}>
          {c.result === "Pass" ? <span className="badge badge-ok">✓ Pass</span> : c.result === "Fail" ? <span className="badge badge-danger">✕ Blocks close</span> : <span className="badge badge-warning">! Open item</span>}
          <span><span className="mono muted">{c.controlId}</span> {c.detail}</span>
        </li>
      ))}
    </ul>
  );
}
