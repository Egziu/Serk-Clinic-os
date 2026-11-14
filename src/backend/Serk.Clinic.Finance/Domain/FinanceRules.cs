using System.Globalization;
using System.Text;

namespace Serk.Clinic.Finance.Domain;

/// <summary>Ledger posting purposes. Each (source, purpose) posts exactly once; mappings come from versioned posting rules.</summary>
public static class PostingEvents
{
    public const string InvoiceIssued = "InvoiceIssued";
    public const string CashPaymentVerified = "CashPaymentVerified";
    public const string ElectronicPaymentVerified = "ElectronicPaymentVerified";
    public const string CashRefundPaid = "CashRefundPaid";
    public const string ElectronicRefundPaid = "ElectronicRefundPaid";
    public const string CashShortage = "CashShortage";
    public const string CashOverage = "CashOverage";
    public const string ProviderSettlementNet = "ProviderSettlementNet";
    public const string ProviderSettlementFee = "ProviderSettlementFee";
}

public sealed record AccountSeed(string Code, string Name, string Type);

public sealed record RuleSeed(string EventType, string Debit, string Credit);

/// <summary>
/// ILLUSTRATIVE default chart of accounts and mappings, copied into each organisation's own configurable tables on first use.
/// They follow SRS v2 §6.2 examples and are NOT accounting advice: a qualified accountant must review and approve them.
/// </summary>
public static class DefaultChart
{
    public const string Cash = "1000";
    public const string Bank = "1010";
    public const string ProviderClearing = "1020";
    public const string PatientReceivable = "1100";
    public const string ServiceRevenue = "4000";
    public const string CashOverShort = "5900";
    public const string ProviderFees = "5910";

    public static readonly IReadOnlyList<AccountSeed> Accounts =
    [
        new(Cash, "Cash on hand", "Asset"),
        new(Bank, "Bank", "Asset"),
        new(ProviderClearing, "Payment provider clearing", "Asset"),
        new(PatientReceivable, "Patient accounts receivable", "Asset"),
        new(ServiceRevenue, "Service revenue", "Revenue"),
        new(CashOverShort, "Cash over / short", "Expense"),
        new(ProviderFees, "Payment provider fees", "Expense"),
    ];

    public static readonly IReadOnlyList<RuleSeed> Rules =
    [
        new(PostingEvents.InvoiceIssued, PatientReceivable, ServiceRevenue),
        new(PostingEvents.CashPaymentVerified, Cash, PatientReceivable),
        new(PostingEvents.ElectronicPaymentVerified, ProviderClearing, PatientReceivable),
        new(PostingEvents.CashRefundPaid, PatientReceivable, Cash),
        new(PostingEvents.ElectronicRefundPaid, PatientReceivable, ProviderClearing),
        new(PostingEvents.CashShortage, CashOverShort, Cash),
        new(PostingEvents.CashOverage, Cash, CashOverShort),
        new(PostingEvents.ProviderSettlementNet, Bank, ProviderClearing),
        new(PostingEvents.ProviderSettlementFee, ProviderFees, ProviderClearing),
    ];
}

/// <summary>Pure money rules. All amounts are <see cref="decimal"/> with two decimal places; floats are never used for money.</summary>
public static class MoneyRules
{
    public const string DefaultCurrency = "ETB";

    /// <summary>Discounts above this share of the gross amount need approval by a different person (maker-checker).</summary>
    public const decimal DiscountApprovalThreshold = 0.10m;

    public static bool IsMoney(decimal value) => value >= 0 && decimal.Round(value, 2) == value && value <= 999_999_999.99m;

    public static bool DiscountNeedsApproval(decimal gross, decimal discount) => gross > 0 && discount > decimal.Round(gross * DiscountApprovalThreshold, 2);

    /// <summary>Expected cash = opening float + verified cash receipts − cash refunds paid (SRS v2 §6.5; no transfers/drops yet).</summary>
    public static decimal ExpectedCash(decimal openingFloat, decimal cashReceipts, decimal cashRefunds) => openingFloat + cashReceipts - cashRefunds;

    public static string InvoiceStatusFor(decimal total, decimal paid) => paid <= 0 ? "Issued" : paid >= total ? "Paid" : "PartiallyPaid";

    public static string Format(decimal amount) => amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

public sealed record TenderTotals(decimal Cash, decimal Electronic, decimal Total);

public sealed record DrawerTotals(decimal OpeningFloat, decimal VerifiedCashReceipts, decimal CashRefunds, decimal Expected, decimal Counted, decimal Variance, bool VarianceApproved, int Sessions);

public sealed record CloseException(string Code, int Count, decimal Amount, bool Approved);

public sealed record CloseCheck(string ControlId, string Result, string Severity, string Detail);

/// <summary>
/// The daily close snapshot: aggregates only (no patient data). Its canonical JSON is hashed; the hash is what the approver
/// confirms and what the owner digest cites. Field meanings follow SRS v2 §7.3; the figures are different accounting measures
/// and are not expected to add up to each other.
/// </summary>
public sealed record CloseSnapshot(
    int SchemaVersion,
    string FacilityName,
    string BusinessDateLocal,
    string Timezone,
    string Currency,
    decimal IssuedServiceCharges,
    decimal Discounts,
    decimal PostedServiceRevenue,
    TenderTotals VerifiedCollections,
    DrawerTotals CashDrawer,
    decimal RefundsActuallyPaid,
    decimal OutstandingAccountsReceivable,
    decimal UnsettledProviderClearing,
    decimal JournalDebits,
    decimal JournalCredits,
    int InvoicesIssued,
    int ReceiptsIssued,
    IReadOnlyList<CloseException> Exceptions,
    string RecipientSensitiveDataPolicy);

public static class OwnerDigest
{
    public const string Policy = "AGGREGATE_ONLY_NO_PHI";

    /// <summary>Plain-text owner briefing built ONLY from the aggregate snapshot, so it cannot contain patient data.</summary>
    public static string Render(CloseSnapshot s, string closeSerial, bool fullyReconciled, string snapshotSha256)
    {
        var b = new StringBuilder();
        void Row(string label, decimal amount) => b.Append(label.PadRight(30)).AppendLine(MoneyRules.Format(amount).PadLeft(14));
        b.AppendLine("SERK CLINIC OS  |  DAILY OWNER BRIEFING");
        b.Append("Facility: ").AppendLine(s.FacilityName);
        b.Append("Business day: ").Append(s.BusinessDateLocal).Append(" (").Append(s.Timezone).Append(")  |  Currency: ").AppendLine(s.Currency);
        b.Append("Status: APPROVED & LOCKED").Append(fullyReconciled ? "" : " (with open items)").Append("  |  Close ID: ").AppendLine(closeSerial);
        b.AppendLine();
        Row("INVOICED SERVICES", s.IssuedServiceCharges);
        Row("DISCOUNTS", s.Discounts);
        Row("NET POSTED SERVICE REVENUE", s.PostedServiceRevenue);
        Row("VERIFIED PATIENT COLLECTIONS", s.VerifiedCollections.Total);
        Row("  Cash", s.VerifiedCollections.Cash);
        Row("  Electronic", s.VerifiedCollections.Electronic);
        Row("CASH EXPECTED", s.CashDrawer.Expected);
        Row("CASH COUNTED", s.CashDrawer.Counted);
        Row(s.CashDrawer.Variance < 0 ? "CASH SHORTAGE" : "CASH OVERAGE", Math.Abs(s.CashDrawer.Variance));
        Row("REFUNDS PAID", s.RefundsActuallyPaid);
        Row("OUTSTANDING AR", s.OutstandingAccountsReceivable);
        Row("UNSETTLED PROVIDER CLEARING", s.UnsettledProviderClearing);
        b.AppendLine();
        b.Append("Exceptions: ").AppendLine(s.Exceptions.Count == 0 ? "none" : string.Join("; ", s.Exceptions.Select(e =>
            $"{e.Code} x{e.Count} ({MoneyRules.Format(e.Amount)}{(e.Approved ? ", approved" : ", open")})")));
        b.Append("Activity: ").Append(s.InvoicesIssued).Append(" invoices issued; ").Append(s.ReceiptsIssued).AppendLine(" receipts issued.");
        b.AppendLine("Detailed report: sign in to SERK Clinic OS > Owner reports.");
        b.Append("Snapshot ").AppendLine(snapshotSha256[..16]);
        b.AppendLine("Totals are different accounting measures and are not expected to sum to each other.");
        return b.ToString();
    }
}
