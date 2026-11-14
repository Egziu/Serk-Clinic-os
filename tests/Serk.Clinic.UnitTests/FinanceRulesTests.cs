using Serk.Clinic.Finance.Domain;

namespace Serk.Clinic.UnitTests;

public class FinanceRulesTests
{
    [Theory]
    [InlineData(3000, 24000, 0, 27000)]
    [InlineData(500, 1500, 600, 1400)]
    [InlineData(0, 0, 0, 0)]
    public void Expected_cash_is_float_plus_receipts_minus_refunds(decimal open, decimal receipts, decimal refunds, decimal expected) =>
        Assert.Equal(expected, MoneyRules.ExpectedCash(open, receipts, refunds));

    [Theory]
    [InlineData(1800, 180, false)]   // exactly 10% does not need approval
    [InlineData(1800, 180.01, true)]
    [InlineData(1800, 0, false)]
    [InlineData(0, 0, false)]
    public void Discount_threshold(decimal gross, decimal discount, bool needsApproval) => Assert.Equal(needsApproval, MoneyRules.DiscountNeedsApproval(gross, discount));

    [Theory]
    [InlineData(10.50, true)]
    [InlineData(10.505, false)]
    [InlineData(-1, false)]
    public void Money_has_at_most_two_decimals_and_is_not_negative(decimal value, bool ok) => Assert.Equal(ok, MoneyRules.IsMoney(value));

    [Theory]
    [InlineData(100, 0, "Issued")]
    [InlineData(100, 40, "PartiallyPaid")]
    [InlineData(100, 100, "Paid")]
    public void Invoice_status_follows_payments(decimal total, decimal paid, string status) => Assert.Equal(status, MoneyRules.InvoiceStatusFor(total, paid));

    [Fact]
    public void Default_mappings_reference_only_defined_accounts_and_are_two_sided()
    {
        var codes = DefaultChart.Accounts.Select(a => a.Code).ToHashSet();
        Assert.All(DefaultChart.Rules, r => Assert.True(codes.Contains(r.Debit) && codes.Contains(r.Credit) && r.Debit != r.Credit, r.EventType));
        Assert.Equal(DefaultChart.Rules.Count, DefaultChart.Rules.Select(r => r.EventType).Distinct().Count());
    }

    [Fact]
    public void Owner_digest_matches_the_pack_example_figures_and_carries_no_patient_fields()
    {
        var snapshot = new CloseSnapshot(1, "Demo Dental Clinic", "2026-11-10", "Africa/Addis_Ababa", "ETB", 86000m, 0m, 79200m, new TenderTotals(24000m, 49500m, 73500m),
            new DrawerTotals(3000m, 24000m, 0m, 27000m, 26950m, -50m, true, 1), 1500m, 18500m, 8000m, 0m, 0m, 42, 40,
            [new CloseException("CASH_SHORTAGE", 1, 50m, true), new CloseException("PENDING_SETTLEMENT", 2, 8000m, false)], OwnerDigest.Policy);
        var text = OwnerDigest.Render(snapshot, "DC-20261110-000042", false, new string('a', 64));
        foreach (var expected in new[] { "86,000.00", "79,200.00", "73,500.00", "24,000.00", "49,500.00", "27,000.00", "26,950.00", "CASH SHORTAGE", "50.00", "18,500.00", "8,000.00", "DC-20261110-000042", "with open items" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        // The snapshot type has no patient-identifying members at all, so the digest cannot leak them.
        var members = typeof(CloseSnapshot).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, m => m.Contains("patient", StringComparison.Ordinal) || m.Contains("mrn", StringComparison.Ordinal) || m.Contains("phone", StringComparison.Ordinal) || m.Contains("diagnos", StringComparison.Ordinal));
    }
}
