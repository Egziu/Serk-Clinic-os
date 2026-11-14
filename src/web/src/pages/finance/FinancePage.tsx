import { useCallback, useEffect, useState, type FormEvent, type KeyboardEvent } from "react";
import { api } from "../../api/client";
import { ErrorAlert, Field } from "../../components/common";
import { CheckList, CloseFigures } from "./CloseFigures";
import { money, when, type ApprovalItem, type CashSession, type ClosePreview, type DailyClose, type Invoice, type Payment, type Service, type Trace, type TrialBalance } from "./financeTypes";

const TABS = [
  { id: "close", label: "Day close" },
  { id: "approvals", label: "Approvals" },
  { id: "cash", label: "Drawers & settlements" },
  { id: "ledger", label: "Ledger & trace" },
  { id: "prices", label: "Price list" },
] as const;
type Tab = (typeof TABS)[number]["id"];

/** Finance supervisor workspace: approvals, reconciliation, daily close, ledger and prices. No clinical data. */
export function FinancePage({ can }: { can: (capability: string) => boolean }) {
  const [tab, setTab] = useState<Tab>("close");
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const i = TABS.findIndex((t) => t.id === tab);
    const next = e.key === "ArrowRight" ? (i + 1) % TABS.length : e.key === "ArrowLeft" ? (i - 1 + TABS.length) % TABS.length : -1;
    if (next >= 0) {
      e.preventDefault();
      setTab(TABS[next]!.id);
      document.getElementById(`ftab-${TABS[next]!.id}`)?.focus();
    }
  };
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Finance</h1>
          <p>Every figure comes from posted, balanced ledger entries. Approvals always need a second person.</p>
        </div>
      </div>
      <div className="tabs" role="tablist" aria-label="Finance sections" onKeyDown={onKey}>
        {TABS.map((t) => <button key={t.id} id={`ftab-${t.id}`} role="tab" aria-selected={tab === t.id} aria-controls={`fpanel-${t.id}`} tabIndex={tab === t.id ? 0 : -1} onClick={() => setTab(t.id)}>{t.label}</button>)}
      </div>
      <div id={`fpanel-${tab}`} role="tabpanel" aria-labelledby={`ftab-${tab}`}>
        {tab === "close" && <ClosePanel canClose={can("finance.close")} canApprove={can("finance.approve")} />}
        {tab === "approvals" && (can("finance.approve") ? <ApprovalsPanel /> : <p className="panel muted">Your role cannot approve finance exceptions.</p>)}
        {tab === "cash" && <CashPanel canSettle={can("finance.close")} canApprove={can("finance.approve")} />}
        {tab === "ledger" && <LedgerPanel />}
        {tab === "prices" && <PricesPanel canEdit={can("finance.configure")} />}
      </div>
    </>
  );
}

function ClosePanel({ canClose, canApprove }: { canClose: boolean; canApprove: boolean }) {
  const [preview, setPreview] = useState<ClosePreview | null>(null);
  const [closes, setCloses] = useState<DailyClose[]>([]);
  const [date, setDate] = useState("");
  const [comment, setComment] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = useCallback(() => {
    api.get<ClosePreview>(`/api/v1/finance/daily-closes/preview${date ? `?date=${date}` : ""}`).then((r) => { setPreview(r.data); setError(null); }).catch(setError);
    api.get<DailyClose[]>("/api/v1/finance/daily-closes").then((r) => setCloses(r.data)).catch(setError);
  }, [date]);
  useEffect(load, [load]);

  const run = async (label: string, action: () => Promise<unknown>) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setNote(label);
    } catch (err) {
      setError(err);
    }
    load();
  };
  const existing = preview && closes.find((c) => c.businessDate === preview.businessDate && (c.status === "Submitted" || c.status === "Locked"));

  return (
    <>
      <section className="panel" aria-labelledby="close-h">
        <div className="panel-header">
          <h2 id="close-h">Business day {preview?.businessDate}</h2>
          <div className="inline-form">
            <label htmlFor="close-date" className="inline-label">Day (Gregorian)</label>
            <input id="close-date" type="date" value={date || preview?.businessDate || ""} onChange={(e) => setDate(e.target.value)} />
          </div>
        </div>
        <ErrorAlert error={error} />
        {note && <p className="alert alert-info" role="status">{note}</p>}
        {preview && (
          <>
            <CloseFigures s={preview.snapshot} />
            <h3>Checks before closing</h3>
            <CheckList checks={preview.checks} />
            {existing ? (
              <p className="alert alert-info">This day is {existing.status === "Locked" ? "locked" : "submitted and waiting for approval"} as {existing.serial}.</p>
            ) : canClose ? (
              <form className="inline-form" aria-label="Submit daily close" onSubmit={(e: FormEvent) => { e.preventDefault(); void run("Close submitted. A different person must now approve it.", () => api.post("/api/v1/finance/daily-closes", { businessDate: preview.businessDate, comment: comment || null })); }}>
                <Field id="close-comment" label="Comment" hint="optional"><input id="close-comment" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} /></Field>
                <button className="btn btn-primary" type="submit" disabled={!preview.canSubmit}>Submit day for approval</button>
                {!preview.canSubmit && <span className="status-line">Resolve the blocking checks first.</span>}
                {preview.canSubmit && !preview.fullyReconciled && <span className="status-line">Open items remain: the close will be marked “not fully reconciled”.</span>}
              </form>
            ) : null}
          </>
        )}
      </section>
      <section className="panel" aria-labelledby="closes-h">
        <h2 id="closes-h">Submitted and locked days</h2>
        <table aria-label="Daily closes">
          <thead><tr><th>Close</th><th>Status</th><th className="money">Collections</th><th>Submitted</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {closes.length === 0 && <tr><td colSpan={5} className="muted">No day has been submitted yet.</td></tr>}
            {closes.map((c) => (
              <tr key={c.id}>
                <td><span className="mono">{c.serial}</span><br /><span className="hint">{c.businessDate} · snapshot {c.snapshotSha256.slice(0, 12)}</span></td>
                <td>
                  {c.status === "Locked" ? <span className="badge badge-ok">✓ Locked</span> : c.status === "Submitted" ? <span className="badge badge-warning">… Waiting for approval</span> : <span className="badge badge-neutral">↩ Returned</span>}
                  {c.status !== "Returned" && !c.fullyReconciled && <><br /><span className="hint">with open items</span></>}
                  {c.decisionReason && <><br /><span className="hint">{c.decisionReason}</span></>}
                </td>
                <td className="money">{money(c.snapshot.verifiedCollections.total)}</td>
                <td>{when(c.submittedAt)}</td>
                <td className="btn-row">
                  {c.status === "Submitted" && canApprove && (
                    <>
                      <button className="btn btn-primary" aria-label={`Approve and lock ${c.serial}`} onClick={() => { if (window.confirm(`Approve and lock ${c.serial}? A locked day cannot be changed.`)) void run("Day locked. Owner digests are queued.", () => api.post(`/api/v1/finance/daily-closes/${c.id}/approve`)); }}>Approve and lock</button>
                      <button className="btn" aria-label={`Return ${c.serial}`} onClick={() => { const r = window.prompt("Why is this close returned?"); if (r) void run("Close returned.", () => api.post(`/api/v1/finance/daily-closes/${c.id}/return`, { reason: r })); }}>Return</button>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </>
  );
}

function ApprovalsPanel() {
  const [items, setItems] = useState<ApprovalItem[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = useCallback(() => { api.get<ApprovalItem[]>("/api/v1/finance/approvals").then((r) => setItems(r.data)).catch(setError); }, []);
  useEffect(load, [load]);
  const decide = async (item: ApprovalItem, approve: boolean) => {
    const url = item.kind === "Discount" ? `/api/v1/billing/invoices/${item.id}/approve-discount`
      : item.kind === "Refund" ? `/api/v1/finance/refunds/${item.id}/${approve ? "approve" : "reject"}`
      : `/api/v1/treasury/sessions/${item.id}/approve-variance`;
    setError(null);
    try {
      await api.post(url);
      setNote(approve ? "Approved." : "Rejected.");
    } catch (err) {
      setError(err);
    }
    load();
  };
  const label: Record<ApprovalItem["kind"], string> = { Discount: "Discount", Refund: "Refund", CashVariance: "Cash difference" };
  return (
    <section className="panel" aria-labelledby="appr-h">
      <h2 id="appr-h">Waiting for a second person</h2>
      <ErrorAlert error={error} />
      {note && <p className="alert alert-info" role="status">{note}</p>}
      <table aria-label="Approvals">
        <thead><tr><th>Type</th><th>Details</th><th className="money">Amount (ETB)</th><th>Requested</th><th><span className="sr-only">Decision</span></th></tr></thead>
        <tbody>
          {items.length === 0 && <tr><td colSpan={5} className="muted">Nothing is waiting for approval.</td></tr>}
          {items.map((i) => (
            <tr key={i.id}>
              <td><span className="badge badge-warning">{label[i.kind]}</span></td>
              <td>{i.reference}<br /><span className="hint">Reason: {i.reason}</span></td>
              <td className="money">{money(i.amount)}</td>
              <td>{when(i.requestedAt)}</td>
              <td className="btn-row">
                {i.requestedByMe ? <span className="hint">Your own request — another person must decide</span> : (
                  <>
                    <button className="btn btn-primary" onClick={() => decide(i, true)} aria-label={`Approve ${label[i.kind]} ${money(i.amount)}`}>Approve</button>
                    {i.kind === "Refund" && <button className="btn" onClick={() => decide(i, false)} aria-label={`Reject refund ${money(i.amount)}`}>Reject</button>}
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function CashPanel({ canSettle, canApprove }: { canSettle: boolean; canApprove: boolean }) {
  const [sessions, setSessions] = useState<CashSession[]>([]);
  const [unsettled, setUnsettled] = useState<Payment[]>([]);
  const [picked, setPicked] = useState<string[]>([]);
  const [reference, setReference] = useState("");
  const [fee, setFee] = useState("0");
  const [sandbox, setSandbox] = useState(false);
  const [pending, setPending] = useState<Invoice[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = useCallback(() => {
    api.get<CashSession[]>("/api/v1/treasury/sessions").then((r) => setSessions(r.data)).catch(setError);
    api.get<Payment[]>("/api/v1/treasury/unsettled").then((r) => setUnsettled(r.data)).catch(setError);
    api.get<Invoice[]>("/api/v1/finance/invoices?status=Open").then((r) => setPending(r.data)).catch(setError);
  }, []);
  useEffect(() => {
    load();
    api.get<{ manualConfirm: boolean }>("/api/v1/billing/sandbox").then((r) => setSandbox(r.data.manualConfirm)).catch(() => setSandbox(false));
  }, [load]);
  const run = async (label: string, action: () => Promise<unknown>) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setNote(label);
    } catch (err) {
      setError(err);
    }
    load();
  };
  const provider = unsettled.find((p) => picked.includes(p.id))?.provider ?? "";
  const waiting = pending.flatMap((i) => i.payments.filter((p) => p.status === "PendingProvider").map((p) => ({ p, serial: i.serial })));

  return (
    <>
      <section className="panel" aria-labelledby="sessions-h">
        <h2 id="sessions-h">Cash drawers</h2>
        <ErrorAlert error={error} />
        {note && <p className="alert alert-info" role="status">{note}</p>}
        <table aria-label="Cashier sessions">
          <thead><tr><th>Opened</th><th>Status</th><th className="money">Float</th><th className="money">Receipts</th><th className="money">Expected</th><th className="money">Counted</th><th className="money">Difference</th><th><span className="sr-only">Action</span></th></tr></thead>
          <tbody>
            {sessions.length === 0 && <tr><td colSpan={8} className="muted">No drawers for today.</td></tr>}
            {sessions.map((s) => (
              <tr key={s.id}>
                <td>{when(s.openedAt)}</td>
                <td>{s.status === "Closed" ? <span className="badge badge-ok">✓ Closed</span> : s.status === "Open" ? <span className="badge badge-info">Open</span> : <span className="badge badge-warning">! Difference to approve</span>}</td>
                <td className="money">{money(s.openingFloat)}</td><td className="money">{money(s.cashReceipts)}</td><td className="money">{s.expectedAmount === null ? "—" : money(s.expectedAmount)}</td>
                <td className="money">{s.countedAmount === null ? "—" : money(s.countedAmount)}</td>
                <td className="money">{s.variance === null ? "—" : money(s.variance)}{s.varianceExplanation && <><br /><span className="hint">{s.varianceExplanation}</span></>}</td>
                <td>{s.status === "PendingVarianceApproval" && canApprove && <button className="btn btn-primary" onClick={() => run("Difference approved and posted.", () => api.post(`/api/v1/treasury/sessions/${s.id}/approve-variance`))}>Approve difference</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {waiting.length > 0 && (
        <section className="panel" aria-labelledby="waiting-h">
          <h2 id="waiting-h">Waiting for provider confirmation</h2>
          <p className="status-line">These attempts are not money yet. They become receipts only when the provider confirms them.</p>
          <table aria-label="Payment attempts">
            <thead><tr><th>Invoice</th><th>Provider reference</th><th className="money">Amount</th><th><span className="sr-only">Action</span></th></tr></thead>
            <tbody>
              {waiting.map(({ p, serial }) => (
                <tr key={p.id}>
                  <td className="mono">{serial}</td><td>{p.provider} · {p.providerReference}</td><td className="money">{money(p.amount)}</td>
                  <td>{sandbox && canSettle && <button className="btn" onClick={() => run("Provider confirmation simulated.", () => api.post(`/api/v1/billing/payments/${p.id}/sandbox-confirm`))} aria-label={`Simulate provider confirmation for ${p.providerReference}`}>Simulate provider confirmation</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      <section className="panel" aria-labelledby="settle-h">
        <h2 id="settle-h">Provider money not yet in the bank</h2>
        <table aria-label="Unsettled electronic payments">
          <thead><tr><th>Select</th><th>Receipt</th><th>Provider reference</th><th className="money">Amount</th></tr></thead>
          <tbody>
            {unsettled.length === 0 && <tr><td colSpan={4} className="muted">All confirmed electronic payments are settled.</td></tr>}
            {unsettled.map((p) => (
              <tr key={p.id}>
                <td><input type="checkbox" aria-label={`Select ${p.receiptSerial}`} checked={picked.includes(p.id)} disabled={!canSettle || (provider !== "" && p.provider !== provider)}
                  onChange={(e) => setPicked((ids) => (e.target.checked ? [...ids, p.id] : ids.filter((x) => x !== p.id)))} /></td>
                <td className="mono">{p.receiptSerial}</td><td>{p.provider} · {p.providerReference}</td><td className="money">{money(p.amount - p.refundedAmount)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {canSettle && picked.length > 0 && (
          <form className="inline-form" aria-label="Record settlement" onSubmit={(e: FormEvent) => { e.preventDefault(); void run("Settlement recorded.", async () => { await api.post("/api/v1/treasury/settlements", { provider, reference, paymentIds: picked, feeAmount: Number(fee) }); setPicked([]); setReference(""); setFee("0"); }); }}>
            <Field id="settle-ref" label="Bank statement reference"><input id="settle-ref" value={reference} onChange={(e) => setReference(e.target.value)} minLength={3} required /></Field>
            <Field id="settle-fee" label="Provider fee (ETB)"><input id="settle-fee" type="number" min="0" step="0.01" value={fee} onChange={(e) => setFee(e.target.value)} /></Field>
            <button className="btn btn-primary" type="submit">Record settlement of {picked.length} payment(s)</button>
          </form>
        )}
      </section>
    </>
  );
}

function LedgerPanel() {
  const [tb, setTb] = useState<TrialBalance | null>(null);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [trace, setTrace] = useState<Trace | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    api.get<TrialBalance>("/api/v1/finance/trial-balance").then((r) => setTb(r.data)).catch(setError);
    api.get<Invoice[]>("/api/v1/finance/invoices").then((r) => setInvoices(r.data.filter((i) => i.serial))).catch(setError);
  }, []);
  return (
    <div className="grid-2">
      <section className="panel" aria-labelledby="tb-h">
        <h2 id="tb-h">Trial balance</h2>
        <ErrorAlert error={error} />
        {tb && (
          <>
            <p className="status-line">As of business day {tb.asOf} · {tb.currency} · {tb.balanced ? <span className="badge badge-ok">✓ Debits equal credits</span> : <span className="badge badge-danger">✕ Not balanced</span>}</p>
            <table aria-label="Trial balance">
              <thead><tr><th>Account</th><th className="money">Debits</th><th className="money">Credits</th><th className="money">Balance</th></tr></thead>
              <tbody>
                {tb.lines.map((l) => <tr key={l.code}><td><span className="mono">{l.code}</span> {l.name}<br /><span className="hint">{l.type}</span></td><td className="money">{money(l.debits)}</td><td className="money">{money(l.credits)}</td><td className="money">{money(l.balance)}</td></tr>)}
                <tr><th scope="row">Total</th><td className="money"><strong>{money(tb.totalDebits)}</strong></td><td className="money"><strong>{money(tb.totalCredits)}</strong></td><td /></tr>
              </tbody>
            </table>
            <p className="status-line">Account mappings are illustrative defaults and must be approved by a qualified accountant before real use.</p>
          </>
        )}
      </section>
      <section className="panel" aria-labelledby="trace-h">
        <h2 id="trace-h">Trace every birr</h2>
        <Field id="trace-invoice" label="Invoice">
          <select id="trace-invoice" defaultValue="" onChange={(e) => { if (e.target.value) api.get<Trace>(`/api/v1/finance/invoices/${e.target.value}/trace`).then((r) => setTrace(r.data)).catch(setError); }}>
            <option value="">Choose an invoice…</option>
            {invoices.map((i) => <option key={i.id} value={i.id}>{i.serial} — {money(i.totalAmount)} {i.currency} ({i.status})</option>)}
          </select>
        </Field>
        {trace && (
          <ol className="steps" aria-label={`Money trace for ${trace.serial}`}>
            {trace.steps.map((s, i) => (
              <li key={i}>
                <strong>{s.step}</strong> <span className="mono">{s.reference}</span> · {s.status}{s.amount !== null && <> · <span className="money">{money(s.amount)} {trace.currency}</span></>}
                <br /><span className="hint">{when(s.at)} · {s.detail}</span>
              </li>
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}

function PricesPanel({ canEdit }: { canEdit: boolean }) {
  const [services, setServices] = useState<Service[]>([]);
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [price, setPrice] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = useCallback(() => { api.get<Service[]>("/api/v1/billing/services").then((r) => setServices(r.data)).catch(setError); }, []);
  useEffect(load, [load]);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const r = await api.post<Service>("/api/v1/billing/services", { code, name, unitPrice: Number(price) });
      setNote(`${r.data.name} saved as price version ${r.data.version}. Invoices already issued keep their price.`);
      setCode(""); setName(""); setPrice("");
      load();
    } catch (err) {
      setError(err);
    }
  };
  return (
    <section className="panel" aria-labelledby="prices-h">
      <h2 id="prices-h">Service and price list</h2>
      <ErrorAlert error={error} />
      {note && <p className="alert alert-info" role="status">{note}</p>}
      <table aria-label="Price list">
        <thead><tr><th>Code</th><th>Service</th><th className="money">Price (ETB)</th><th>Version</th><th><span className="sr-only">Action</span></th></tr></thead>
        <tbody>
          {services.map((s) => (
            <tr key={s.code}>
              <td className="mono">{s.code}</td><td>{s.name}</td><td className="money">{money(s.unitPrice)}</td><td>v{s.version}</td>
              <td>{canEdit && <button className="btn" onClick={() => { setCode(s.code); setName(s.name); setPrice(String(s.unitPrice)); }} aria-label={`Change price of ${s.name}`}>Change</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {canEdit && (
        <form onSubmit={save} className="inline-form" aria-label="Save service">
          <Field id="svc-code" label="Code"><input id="svc-code" value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} required maxLength={20} /></Field>
          <Field id="svc-name" label="Service name"><input id="svc-name" value={name} onChange={(e) => setName(e.target.value)} required /></Field>
          <Field id="svc-price" label="Price (ETB)"><input id="svc-price" type="number" min="0" step="0.01" value={price} onChange={(e) => setPrice(e.target.value)} required /></Field>
          <button className="btn btn-primary" type="submit">Save price</button>
        </form>
      )}
    </section>
  );
}
