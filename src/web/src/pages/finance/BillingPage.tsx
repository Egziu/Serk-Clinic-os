import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, newIdempotencyKey } from "../../api/client";
import type { PatientSummary } from "../../api/types";
import { ErrorAlert, Field, StatusBadge } from "../../components/common";
import { Icon } from "../../components/icons";
import { money, type CashSession, type Invoice, type Payment, type Service } from "./financeTypes";

/** Cashier desk: own cash drawer, invoices from the price list, payments, receipts and refund requests. */
export function BillingPage() {
  const [patient, setPatient] = useState<PatientSummary | null>(null);
  const [drawerKey, setDrawerKey] = useState(0);
  return (
    <>
      <div className="page-header">
        <div>
          <h1>Billing</h1>
          <p>Invoices, payments and receipts. Money is recorded only when it is actually received or confirmed by the provider.</p>
        </div>
      </div>
      <div className="grid-2">
        <div>
          <DrawerPanel refreshKey={drawerKey} />
          <PatientPicker patient={patient} onPick={setPatient} />
        </div>
        <div>
          {patient ? <PatientInvoices key={patient.id} patient={patient} onMoney={() => setDrawerKey((k) => k + 1)} /> : <p className="panel muted">Find a patient to prepare an invoice or take a payment.</p>}
        </div>
      </div>
    </>
  );
}

function DrawerPanel({ refreshKey }: { refreshKey: number }) {
  const [session, setSession] = useState<CashSession | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [float, setFloat] = useState("0");
  const [counted, setCounted] = useState("");
  const [explanation, setExplanation] = useState("");
  const [error, setError] = useState<unknown>(null);
  const load = useCallback(() => {
    api.get<CashSession | undefined>("/api/v1/treasury/sessions/current").then((r) => { setSession(r.data ?? null); setLoaded(true); }).catch(setError);
  }, []);
  useEffect(load, [load, refreshKey]);

  const open = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      setSession((await api.post<CashSession>("/api/v1/treasury/sessions", { openingFloat: Number(float) })).data);
    } catch (err) {
      setError(err);
    }
  };
  const count = async (e: FormEvent) => {
    e.preventDefault();
    if (!session) return;
    setError(null);
    try {
      setSession((await api.post<CashSession>(`/api/v1/treasury/sessions/${session.id}/count`, { countedAmount: Number(counted), explanation: explanation || null, version: session.version })).data);
      setCounted(""); setExplanation("");
    } catch (err) {
      setError(err);
    }
  };

  return (
    <section className="panel" aria-labelledby="drawer-h">
      <h2 id="drawer-h"><Icon name="cash" /> My cash drawer</h2>
      <ErrorAlert error={error} />
      {loaded && (!session || session.status !== "Open") && (
        <>
          {session?.status === "PendingVarianceApproval" && (
            <p className="alert alert-warning" role="status">
              Your last count differed by <strong>{money(session.variance ?? 0)} ETB</strong> and is waiting for a supervisor's approval.
            </p>
          )}
          {session?.status === "Closed" && <p className="status-line">Last drawer closed: counted {money(session.countedAmount ?? 0)} ETB, difference {money(session.variance ?? 0)} ETB.</p>}
          <form onSubmit={open} className="inline-form" aria-label="Open cashier session">
            <Field id="drawer-float" label="Opening float (ETB)">
              <input id="drawer-float" type="number" min="0" step="0.01" value={float} onChange={(e) => setFloat(e.target.value)} required />
            </Field>
            <button className="btn btn-primary" type="submit">Open drawer</button>
          </form>
        </>
      )}
      {session?.status === "Open" && (
        <>
          <p><span className="badge badge-ok">✓ Drawer open</span> Float {money(session.openingFloat)} ETB · business day {session.businessDate}</p>
          <form onSubmit={count} aria-label="Count and close drawer">
            <p className="status-line">At the end of your shift, count the cash and enter the total. The expected amount is not shown before you count.</p>
            <div className="inline-form">
              <Field id="drawer-count" label="Counted cash (ETB)">
                <input id="drawer-count" type="number" min="0" step="0.01" value={counted} onChange={(e) => setCounted(e.target.value)} required />
              </Field>
              <Field id="drawer-expl" label="Explanation" hint="only if the count differs">
                <input id="drawer-expl" type="text" maxLength={500} value={explanation} onChange={(e) => setExplanation(e.target.value)} />
              </Field>
              <button className="btn" type="submit">Count and close</button>
            </div>
          </form>
        </>
      )}
    </section>
  );
}

function PatientPicker({ patient, onPick }: { patient: PatientSummary | null; onPick: (p: PatientSummary) => void }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<PatientSummary[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const search = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      setResults((await api.get<PatientSummary[]>(`/api/v1/patients?query=${encodeURIComponent(query)}`)).data);
    } catch (err) {
      setError(err);
    }
  };
  return (
    <section className="panel" aria-labelledby="bill-patient-h">
      <h2 id="bill-patient-h">Patient</h2>
      <ErrorAlert error={error} />
      <form onSubmit={search} className="inline-form" role="search" aria-label="Find patient for billing">
        <Field id="bill-q" label="Name, MRN or phone">
          <input id="bill-q" type="search" value={query} onChange={(e) => setQuery(e.target.value)} minLength={2} required />
        </Field>
        <button className="btn" type="submit">Search</button>
      </form>
      {results && (
        <table aria-label="Patient search results">
          <thead><tr><th>Patient</th><th>MRN</th><th><span className="sr-only">Action</span></th></tr></thead>
          <tbody>
            {results.length === 0 && <tr><td colSpan={3} className="muted">No patient found.</td></tr>}
            {results.map((p) => (
              <tr key={p.id} className={patient?.id === p.id ? "row-selected" : undefined}>
                <td>{p.displayName}</td>
                <td className="mono">{p.mrn}</td>
                <td><button className="btn" onClick={() => onPick(p)} aria-label={`Bill ${p.displayName}`}>Bill</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function PatientInvoices({ patient, onMoney }: { patient: PatientSummary; onMoney: () => void }) {
  const [services, setServices] = useState<Service[]>([]);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [lines, setLines] = useState<{ serviceCode: string; quantity: number }[]>([]);
  const [code, setCode] = useState("");
  const [qty, setQty] = useState(1);
  const [discount, setDiscount] = useState("0");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState<string | null>(null);

  const load = useCallback(() => {
    api.get<Invoice[]>(`/api/v1/billing/invoices?patientId=${patient.id}`).then((r) => setInvoices(r.data)).catch(setError);
  }, [patient.id]);
  useEffect(() => {
    api.get<Service[]>("/api/v1/billing/services").then((r) => { setServices(r.data); setCode(r.data[0]?.code ?? ""); }).catch(setError);
    load();
  }, [load]);

  const gross = lines.reduce((sum, l) => sum + l.quantity * (services.find((s) => s.code === l.serviceCode)?.unitPrice ?? 0), 0);
  const needsApproval = Number(discount) > Math.round(gross * 10) / 100;
  const run = async (label: string, action: () => Promise<unknown>, cash = false) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setNote(label);
      load();
      if (cash) onMoney();
    } catch (err) {
      setError(err);
    }
  };
  const create = (e: FormEvent) => {
    e.preventDefault();
    void run("Invoice prepared.", async () => {
      await api.post("/api/v1/billing/invoices", { patientId: patient.id, lines, discountAmount: Number(discount), discountReason: reason || null });
      setLines([]); setDiscount("0"); setReason("");
    });
  };

  return (
    <>
      <section className="panel" aria-labelledby="new-inv-h">
        <h2 id="new-inv-h">New invoice for {patient.displayName} <span className="mono muted">{patient.mrn}</span></h2>
        <ErrorAlert error={error} />
        {note && <p className="alert alert-info" role="status">{note}</p>}
        <div className="inline-form">
          <Field id="inv-service" label="Service">
            <select id="inv-service" value={code} onChange={(e) => setCode(e.target.value)}>
              {services.map((s) => <option key={s.code} value={s.code}>{s.name} — {money(s.unitPrice)} {s.currency}</option>)}
            </select>
          </Field>
          <Field id="inv-qty" label="Quantity">
            <input id="inv-qty" type="number" min={1} max={999} value={qty} onChange={(e) => setQty(Number(e.target.value))} />
          </Field>
          <button type="button" className="btn" disabled={!code} onClick={() => setLines((ls) => [...ls, { serviceCode: code, quantity: qty }])}>Add service</button>
        </div>
        {lines.length > 0 && (
          <form onSubmit={create} aria-label="Invoice draft">
            <table aria-label="Invoice lines">
              <thead><tr><th>Service</th><th className="money">Qty</th><th className="money">Amount</th><th><span className="sr-only">Remove</span></th></tr></thead>
              <tbody>
                {lines.map((l, i) => {
                  const s = services.find((x) => x.code === l.serviceCode);
                  return (
                    <tr key={i}>
                      <td>{s?.name}</td><td className="money">{l.quantity}</td><td className="money">{money(l.quantity * (s?.unitPrice ?? 0))}</td>
                      <td><button type="button" className="btn" onClick={() => setLines((ls) => ls.filter((_, j) => j !== i))} aria-label={`Remove ${s?.name}`}>Remove</button></td>
                    </tr>
                  );
                })}
                <tr><th scope="row" colSpan={2}>Total before discount</th><td className="money"><strong>{money(gross)}</strong></td><td /></tr>
              </tbody>
            </table>
            <div className="inline-form">
              <Field id="inv-discount" label="Discount (ETB)">
                <input id="inv-discount" type="number" min="0" step="0.01" max={gross} value={discount} onChange={(e) => setDiscount(e.target.value)} />
              </Field>
              {Number(discount) > 0 && (
                <Field id="inv-reason" label="Reason for discount">
                  <input id="inv-reason" type="text" value={reason} onChange={(e) => setReason(e.target.value)} minLength={5} maxLength={300} required />
                </Field>
              )}
              <button className="btn btn-primary" type="submit">Prepare invoice ({money(gross - Number(discount))} ETB)</button>
            </div>
            {needsApproval && <p className="alert alert-warning">A discount above 10% must be approved by a supervisor before the invoice can be issued.</p>}
          </form>
        )}
      </section>

      <section className="panel" aria-labelledby="inv-list-h">
        <h2 id="inv-list-h">Invoices</h2>
        {invoices.length === 0 && <p className="muted">No invoices for this patient yet.</p>}
        {/* Keyed by version so the payment form always starts from the current balance due. */}
        {invoices.map((inv) => <InvoiceCard key={`${inv.id}-${inv.version}`} invoice={inv} run={run} />)}
      </section>
    </>
  );
}

function InvoiceCard({ invoice, run }: { invoice: Invoice; run: (label: string, action: () => Promise<unknown>, cash?: boolean) => Promise<void> }) {
  const [amount, setAmount] = useState(String(invoice.balanceDue));
  const [tender, setTender] = useState("Cash");
  const [provider, setProvider] = useState("telebirr-sandbox");
  const [reference, setReference] = useState("");
  const payable = invoice.status === "Issued" || invoice.status === "PartiallyPaid";
  const pay = (e: FormEvent) => {
    e.preventDefault();
    if (tender === "Cash") {
      void run(`Cash received: ${money(Number(amount))} ETB. Receipt issued.`, () => api.post(`/api/v1/billing/invoices/${invoice.id}/payments/cash`, { amount: Number(amount) }, { idempotencyKey: newIdempotencyKey() }), true);
    } else {
      void run("Payment attempt recorded. It counts only when the provider confirms it.", () => api.post(`/api/v1/billing/invoices/${invoice.id}/payments/electronic`, { tender, provider, providerReference: reference, amount: Number(amount) }));
    }
  };
  const refund = (p: Payment) => {
    const value = window.prompt(`Refund amount for receipt ${p.receiptSerial} (up to ${money(p.amount - p.refundedAmount)} ETB)?`);
    if (!value) return;
    const why = window.prompt("Reason for the refund (no clinical details)?");
    if (!why) return;
    void run("Refund requested. A supervisor must approve it before any money is returned.", () => api.post("/api/v1/finance/refunds", { paymentId: p.id, amount: Number(value), reason: why }));
  };

  return (
    <article className="org-card" aria-label={`Invoice ${invoice.serial ?? "draft"}`}>
      <div className="panel-header">
        <h3>{invoice.serial ?? "Draft invoice"} <StatusBadge status={invoice.status === "PendingApproval" ? "Waiting for discount approval" : invoice.status === "PartiallyPaid" ? "Partially paid" : invoice.status} /></h3>
        <div className="btn-row">
          {invoice.status === "Draft" && <button className="btn btn-primary" onClick={() => run("Invoice issued.", () => api.post(`/api/v1/billing/invoices/${invoice.id}/issue`))}>Issue invoice</button>}
          {(invoice.status === "Draft" || invoice.status === "PendingApproval") && <button className="btn" onClick={() => run("Draft cancelled.", () => api.post(`/api/v1/billing/invoices/${invoice.id}/cancel`))}>Cancel draft</button>}
        </div>
      </div>
      <table aria-label={`Lines of ${invoice.serial ?? "draft"}`}>
        <tbody>
          {invoice.lines.map((l, i) => <tr key={i}><td>{l.description} × {l.quantity}</td><td className="money">{money(l.lineTotal)}</td></tr>)}
          {invoice.discountAmount > 0 && <tr><td>Discount — {invoice.discountReason}</td><td className="money">−{money(invoice.discountAmount)}</td></tr>}
          <tr><th scope="row">Total</th><td className="money"><strong>{money(invoice.totalAmount)} {invoice.currency}</strong></td></tr>
          {payable && <tr><th scope="row">Balance due</th><td className="money"><strong>{money(invoice.balanceDue)} {invoice.currency}</strong></td></tr>}
        </tbody>
      </table>
      {invoice.payments.length > 0 && (
        <table aria-label={`Payments of ${invoice.serial}`}>
          <thead><tr><th>Payment</th><th>Status</th><th className="money">Amount</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {invoice.payments.map((p) => (
              <tr key={p.id}>
                <td>{p.tender === "Cash" ? "Cash" : `${p.provider} · ${p.providerReference}`}{p.receiptSerial && <><br /><span className="mono">{p.receiptSerial}</span></>}</td>
                <td>{p.status === "Verified" ? <span className="badge badge-ok">✓ Received</span> : p.status === "PendingProvider" ? <span className="badge badge-warning">… Waiting for provider</span> : <span className="badge badge-danger">✕ Failed</span>}
                  {p.refundedAmount > 0 && <><br /><span className="hint">Refunded {money(p.refundedAmount)}</span></>}</td>
                <td className="money">{money(p.amount)}</td>
                <td>{p.status === "Verified" && p.refundedAmount < p.amount && <button className="btn" onClick={() => refund(p)} aria-label={`Request refund for ${p.receiptSerial}`}>Request refund</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {payable && (
        <form onSubmit={pay} className="inline-form" aria-label={`Take payment for ${invoice.serial}`}>
          <Field id={`pay-tender-${invoice.id}`} label="Paid by">
            <select id={`pay-tender-${invoice.id}`} value={tender} onChange={(e) => setTender(e.target.value)}>
              <option value="Cash">Cash</option><option value="MobileMoney">Mobile money</option><option value="BankTransfer">Bank transfer</option><option value="Card">Card</option>
            </select>
          </Field>
          <Field id={`pay-amount-${invoice.id}`} label="Amount (ETB)">
            <input id={`pay-amount-${invoice.id}`} type="number" min="0.01" step="0.01" max={invoice.balanceDue} value={amount} onChange={(e) => setAmount(e.target.value)} required />
          </Field>
          {tender !== "Cash" && (
            <>
              <Field id={`pay-provider-${invoice.id}`} label="Provider"><input id={`pay-provider-${invoice.id}`} value={provider} onChange={(e) => setProvider(e.target.value)} required /></Field>
              <Field id={`pay-ref-${invoice.id}`} label="Provider reference"><input id={`pay-ref-${invoice.id}`} value={reference} onChange={(e) => setReference(e.target.value)} minLength={3} required /></Field>
            </>
          )}
          <button className="btn btn-primary" type="submit">{tender === "Cash" ? "Take cash" : "Record payment attempt"}</button>
        </form>
      )}
    </article>
  );
}
