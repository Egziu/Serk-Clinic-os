using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Finance.Infrastructure;

public sealed class AccountRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Type { get; set; } = ""; public bool IsActive { get; set; } = true; }

public sealed class PostingRuleRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string EventType { get; set; } = ""; public int Version { get; set; } public string DebitAccountCode { get; set; } = ""; public string CreditAccountCode { get; set; } = ""; public bool IsActive { get; set; } = true; public Guid? ApprovedBy { get; set; } public DateTimeOffset CreatedAt { get; set; } }

public sealed class ServiceRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; public decimal UnitPrice { get; set; } public string Currency { get; set; } = ""; public int Version { get; set; } public bool IsCurrent { get; set; } = true; public Guid CreatedBy { get; set; } public DateTimeOffset CreatedAt { get; set; } }

public sealed class InvoiceRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public string? Serial { get; set; }
    public string Status { get; set; } = "Draft";
    public string Currency { get; set; } = "";
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string? DiscountReason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DiscountApprovedBy { get; set; }
    public Guid? IssuedBy { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateOnly? BusinessDate { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class InvoiceLineRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid InvoiceId { get; set; } public Guid ServiceId { get; set; } public string ServiceCode { get; set; } = ""; public string Description { get; set; } = ""; public int Quantity { get; set; } public decimal UnitPrice { get; set; } public decimal LineTotal { get; set; } }

public sealed class CashierSessionRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public Guid CashierUserId { get; set; }
    public string Status { get; set; } = "Open";
    public decimal OpeningFloat { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public decimal? CountedAmount { get; set; }
    public decimal? Variance { get; set; }
    public string? VarianceExplanation { get; set; }
    public Guid? VarianceApprovedBy { get; set; }
    public DateTimeOffset? CountedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class SettlementRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid FacilityId { get; set; } public string Provider { get; set; } = ""; public string Reference { get; set; } = ""; public decimal GrossAmount { get; set; } public decimal FeeAmount { get; set; } public string Currency { get; set; } = ""; public DateOnly BusinessDate { get; set; } public Guid CreatedBy { get; set; } public DateTimeOffset CreatedAt { get; set; } }

public sealed class PaymentRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public Guid InvoiceId { get; set; }
    public string Tender { get; set; } = "";
    public string? Provider { get; set; }
    public string? ProviderReference { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ReceiptSerial { get; set; }
    public Guid? CashierSessionId { get; set; }
    public decimal RefundedAmount { get; set; }
    public Guid? SettlementId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateOnly? BusinessDate { get; set; }
}

public sealed class RefundRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid FacilityId { get; set; } public Guid PaymentId { get; set; } public string? Serial { get; set; } public decimal Amount { get; set; } public string Reason { get; set; } = ""; public string Status { get; set; } = "Requested"; public Guid? CashierSessionId { get; set; } public Guid RequestedBy { get; set; } public DateTimeOffset RequestedAt { get; set; } public Guid? DecidedBy { get; set; } public DateTimeOffset? DecidedAt { get; set; } public DateOnly? BusinessDate { get; set; } }

public sealed class JournalEntryRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid FacilityId { get; set; } public DateOnly BusinessDate { get; set; } public string SourceType { get; set; } = ""; public Guid SourceId { get; set; } public string Purpose { get; set; } = ""; public int RuleVersion { get; set; } public string Currency { get; set; } = ""; public string Narrative { get; set; } = ""; public Guid CreatedBy { get; set; } public DateTimeOffset OccurredAt { get; set; } public DateTimeOffset PostedAt { get; set; } public string CorrelationId { get; set; } = ""; }

public sealed class JournalLineRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid EntryId { get; set; } public Guid AccountId { get; set; } public decimal Debit { get; set; } public decimal Credit { get; set; } }

public sealed class DailyCloseRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int CloseVersion { get; set; }
    public string Serial { get; set; } = "";
    public string Status { get; set; } = "";
    public bool FullyReconciled { get; set; }
    public string Snapshot { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public string? Comment { get; set; }
    public Guid SubmittedBy { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class CloseCheckRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid CloseId { get; set; } public string ControlId { get; set; } = ""; public string Result { get; set; } = ""; public string Severity { get; set; } = ""; public string Detail { get; set; } = ""; }

public sealed class OwnerSubscriptionRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid FacilityId { get; set; } public Guid UserId { get; set; } public string Channel { get; set; } = ""; public string? Address { get; set; } public string Status { get; set; } = ""; public string? ChallengeSha256 { get; set; } public DateTimeOffset? ChallengeExpiresAt { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? VerifiedAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } }

public sealed class DeliveryAttemptRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid SubscriptionId { get; set; } public Guid CloseId { get; set; } public string Channel { get; set; } = ""; public string Payload { get; set; } = ""; public string PayloadSha256 { get; set; } = ""; public string Status { get; set; } = ""; public int Attempts { get; set; } public string? ProviderMessageId { get; set; } public string? LastError { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }

public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<PostingRuleRow> PostingRules => Set<PostingRuleRow>();
    public DbSet<ServiceRow> Services => Set<ServiceRow>();
    public DbSet<InvoiceRow> Invoices => Set<InvoiceRow>();
    public DbSet<InvoiceLineRow> InvoiceLines => Set<InvoiceLineRow>();
    public DbSet<CashierSessionRow> Sessions => Set<CashierSessionRow>();
    public DbSet<SettlementRow> Settlements => Set<SettlementRow>();
    public DbSet<PaymentRow> Payments => Set<PaymentRow>();
    public DbSet<RefundRow> Refunds => Set<RefundRow>();
    public DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();
    public DbSet<JournalLineRow> JournalLines => Set<JournalLineRow>();
    public DbSet<DailyCloseRow> DailyCloses => Set<DailyCloseRow>();
    public DbSet<CloseCheckRow> CloseChecks => Set<CloseCheckRow>();
    public DbSet<OwnerSubscriptionRow> Subscriptions => Set<OwnerSubscriptionRow>();
    public DbSet<DeliveryAttemptRow> Deliveries => Set<DeliveryAttemptRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("finance");
        modelBuilder.Entity<AccountRow>().ToTable("accounts");
        modelBuilder.Entity<PostingRuleRow>().ToTable("posting_rules");
        modelBuilder.Entity<ServiceRow>().ToTable("services");
        modelBuilder.Entity<InvoiceRow>().ToTable("invoices").Property(i => i.Version).IsConcurrencyToken();
        modelBuilder.Entity<InvoiceLineRow>().ToTable("invoice_lines").HasOne<InvoiceRow>().WithMany().HasForeignKey(l => l.InvoiceId);
        modelBuilder.Entity<CashierSessionRow>().ToTable("cashier_sessions").Property(s => s.Version).IsConcurrencyToken();
        modelBuilder.Entity<SettlementRow>().ToTable("settlements");
        modelBuilder.Entity<PaymentRow>().ToTable("payments");
        modelBuilder.Entity<RefundRow>().ToTable("refunds");
        modelBuilder.Entity<JournalEntryRow>().ToTable("journal_entries");
        modelBuilder.Entity<JournalLineRow>().ToTable("journal_lines").HasOne<JournalEntryRow>().WithMany().HasForeignKey(l => l.EntryId);
        modelBuilder.Entity<DailyCloseRow>().ToTable("daily_closes").Property(c => c.Snapshot).HasColumnType("jsonb");
        modelBuilder.Entity<CloseCheckRow>().ToTable("close_checks").HasOne<DailyCloseRow>().WithMany().HasForeignKey(c => c.CloseId);
        modelBuilder.Entity<OwnerSubscriptionRow>().ToTable("owner_subscriptions");
        modelBuilder.Entity<DeliveryAttemptRow>().ToTable("delivery_attempts");
        modelBuilder.ApplySnakeCaseColumns();
    }
}
