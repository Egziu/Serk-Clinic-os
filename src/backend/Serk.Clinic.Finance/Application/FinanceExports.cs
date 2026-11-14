using Serk.Clinic.Platform.Export;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

/// <summary>Finance datasets (facility scope, business dates). Amounts are exact decimals.</summary>
public static class FinanceExports
{
    private static readonly string[] Read = [Capabilities.FinanceRead];

    public static readonly ExportDefinition Invoices = new(
        "finance.invoices", "Finance", "Invoices",
        "Invoices by business date: serial, status, gross, discount, total, paid. No patient data.",
        Read, false, true,
        ["business_date", "serial", "status", "currency", "gross", "discount", "total", "paid", "outstanding", "discount_reason", "issued_at"],
        """
        SELECT business_date, serial, status, currency, gross_amount, discount_amount, total_amount, paid_amount,
               total_amount - paid_amount, discount_reason, issued_at
        FROM finance.invoices
        WHERE tenant_id = @t AND facility_id = @f AND business_date BETWEEN @dfrom AND @dto
        ORDER BY business_date, serial LIMIT @limit
        """);

    public static readonly ExportDefinition InvoicesByPatient = new(
        "finance.invoices-by-patient", "Finance", "Invoices with patient",
        "Invoices with patient MRN and name (for statements and debt follow-up). Identifies patients.",
        Read, true, true,
        ["business_date", "serial", "status", "patient_mrn", "patient_name", "currency", "total", "paid", "outstanding"],
        """
        SELECT i.business_date, i.serial, i.status, p.mrn, p.display_name, i.currency, i.total_amount, i.paid_amount, i.total_amount - i.paid_amount
        FROM finance.invoices i LEFT JOIN patients.v_patient_reference p ON p.patient_id = i.patient_id
        WHERE i.tenant_id = @t AND i.facility_id = @f AND i.business_date BETWEEN @dfrom AND @dto
        ORDER BY i.business_date, i.serial LIMIT @limit
        """);

    public static readonly ExportDefinition Payments = new(
        "finance.payments", "Finance", "Payments and receipts",
        "Every payment: receipt, invoice, tender, provider reference, amount, refunds, status, cashier.",
        Read, false, true,
        ["business_date", "receipt_serial", "invoice_serial", "tender", "provider", "provider_reference", "currency", "amount", "refunded", "status", "verified_at", "cashier"],
        """
        SELECT p.business_date, p.receipt_serial, i.serial, p.tender, p.provider, p.provider_reference, p.currency, p.amount, p.refunded_amount,
               p.status, p.verified_at, s.display_name
        FROM finance.payments p
        JOIN finance.invoices i ON i.id = p.invoice_id
        LEFT JOIN identity.v_staff_reference s ON s.user_id = p.created_by
        WHERE p.tenant_id = @t AND p.facility_id = @f AND p.business_date BETWEEN @dfrom AND @dto
        ORDER BY p.business_date, p.created_at LIMIT @limit
        """);

    public static readonly ExportDefinition Journal = new(
        "finance.journal", "Finance", "General journal",
        "Posted double-entry lines: date, source, purpose, account, debit, credit. Each entry balances.",
        Read, false, true,
        ["business_date", "posted_at", "entry_id", "source_type", "purpose", "narrative", "account_code", "account_name", "debit", "credit"],
        """
        SELECT e.business_date, e.posted_at, e.id, e.source_type, e.purpose, e.narrative, a.code, a.name, l.debit, l.credit
        FROM finance.journal_entries e
        JOIN finance.journal_lines l ON l.entry_id = e.id
        JOIN finance.accounts a ON a.id = l.account_id
        WHERE e.tenant_id = @t AND e.facility_id = @f AND e.business_date BETWEEN @dfrom AND @dto
        ORDER BY e.business_date, e.posted_at, e.id, a.code LIMIT @limit
        """);

    public static readonly ExportDefinition TrialBalance = new(
        "finance.trial-balance", "Finance", "Trial balance",
        "Debits, credits and balance per account for postings in the period. Total debits equal total credits.",
        Read, false, true,
        ["account_code", "account_name", "account_type", "debit", "credit", "balance"],
        """
        SELECT a.code, a.name, a.type, coalesce(sum(l.debit), 0), coalesce(sum(l.credit), 0), coalesce(sum(l.debit - l.credit), 0)
        FROM finance.accounts a
        LEFT JOIN finance.journal_lines l ON l.account_id = a.id
          AND l.entry_id IN (SELECT id FROM finance.journal_entries WHERE tenant_id = @t AND facility_id = @f AND business_date BETWEEN @dfrom AND @dto)
        WHERE a.tenant_id = @t
        GROUP BY a.code, a.name, a.type ORDER BY a.code LIMIT @limit
        """);

    public static readonly ExportDefinition DailyCloses = new(
        "finance.daily-closes", "Finance", "Daily closes",
        "Close status per business day with snapshot fingerprint and approvers.",
        Read, false, true,
        ["business_date", "serial", "close_version", "status", "fully_reconciled", "snapshot_sha256", "submitted_by", "submitted_at", "decided_by", "decided_at", "decision_reason"],
        """
        SELECT c.business_date, c.serial, c.close_version, c.status, c.fully_reconciled, c.snapshot_sha256,
               sb.display_name, c.submitted_at, db.display_name, c.decided_at, c.decision_reason
        FROM finance.daily_closes c
        LEFT JOIN identity.v_staff_reference sb ON sb.user_id = c.submitted_by
        LEFT JOIN identity.v_staff_reference db ON db.user_id = c.decided_by
        WHERE c.tenant_id = @t AND c.facility_id = @f AND c.business_date BETWEEN @dfrom AND @dto
        ORDER BY c.business_date, c.close_version LIMIT @limit
        """);

    public static readonly ExportDefinition CashSessions = new(
        "finance.cash-sessions", "Finance", "Cash drawer sessions",
        "Per drawer: cashier, opening float, expected, counted, variance, explanation and approver.",
        Read, false, true,
        ["business_date", "cashier", "status", "opened_at", "opening_float", "expected", "counted", "variance", "variance_explanation", "variance_approved_by", "closed_at"],
        """
        SELECT s.business_date, u.display_name, s.status, s.opened_at, s.opening_float, s.expected_amount, s.counted_amount, s.variance,
               s.variance_explanation, ap.display_name, s.closed_at
        FROM finance.cashier_sessions s
        LEFT JOIN identity.v_staff_reference u ON u.user_id = s.cashier_user_id
        LEFT JOIN identity.v_staff_reference ap ON ap.user_id = s.variance_approved_by
        WHERE s.tenant_id = @t AND s.facility_id = @f AND s.business_date BETWEEN @dfrom AND @dto
        ORDER BY s.business_date, s.opened_at LIMIT @limit
        """);

    public static readonly ExportDefinition PriceList = new(
        "finance.price-list", "Finance", "Price list",
        "Current services and prices.",
        Read, false, false,
        ["code", "name", "unit_price", "currency", "version"],
        """
        SELECT code, name, unit_price, currency, version FROM finance.services
        WHERE tenant_id = @t AND is_current ORDER BY code LIMIT @limit
        """);

    public static ExportDefinition[] All => [Invoices, InvoicesByPatient, Payments, Journal, TrialBalance, DailyCloses, CashSessions, PriceList];
}
