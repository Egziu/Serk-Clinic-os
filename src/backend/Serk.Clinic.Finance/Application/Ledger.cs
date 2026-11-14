using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Finance.Domain;
using Serk.Clinic.Finance.Infrastructure;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

/// <summary>
/// The only way finance code writes to the general ledger. A posting is derived from a business event through the
/// organisation's active posting rule (never free-form), is balanced by construction (one debit line, one credit line of the
/// same amount; the database re-checks at commit) and happens exactly once per (source, purpose).
/// </summary>
public sealed class Ledger(DbSession session, RequestContext context, IStaffDirectory staff, TimeProvider clock)
{
    public static readonly Guid SystemUserId = Guid.Parse("00000000-0000-7000-8000-0000000f1a4c");

    public async Task<FinanceDbContext> DbAsync(CancellationToken ct)
    {
        var db = await session.ContextAsync<FinanceDbContext>(ct);
        await EnsureChartAsync(db, ct);
        return db;
    }

    /// <summary>Copies the illustrative default chart and mappings into the organisation's own tables on first use.</summary>
    private async Task EnsureChartAsync(FinanceDbContext db, CancellationToken ct)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Tenant context required.");
        if (await db.Accounts.AnyAsync(a => a.TenantId == tenant, ct))
        {
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"finance-chart:" + tenant.ToString("N")}, 0))", ct);
        if (await db.Accounts.AnyAsync(a => a.TenantId == tenant, ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        db.Accounts.AddRange(DefaultChart.Accounts.Select(a => new AccountRow { Id = Guid.CreateVersion7(), TenantId = tenant, Code = a.Code, Name = a.Name, Type = a.Type }));
        db.PostingRules.AddRange(DefaultChart.Rules.Select(r => new PostingRuleRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            EventType = r.EventType,
            Version = 1,
            DebitAccountCode = r.Debit,
            CreditAccountCode = r.Credit,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<(DateOnly Date, string TimeZone, string FacilityName)> BusinessDayAsync(Guid tenantId, Guid facilityId, CancellationToken ct)
    {
        var facility = await staff.GetFacilityAsync(tenantId, facilityId, ct);
        var tz = facility?.TimeZone ?? BusinessTime.DefaultTimeZone;
        return (BusinessTime.LocalDate(clock.GetUtcNow(), BusinessTime.Resolve(tz)), tz, facility?.Name ?? "Facility");
    }

    /// <summary>
    /// The business date a new posting belongs to: the facility-local date, or — when that day is already locked — the next
    /// day that is not (a locked day is never written to again; the event keeps its true occurrence time).
    /// </summary>
    public async Task<DateOnly> PostingDateAsync(FinanceDbContext db, Guid tenantId, Guid facilityId, CancellationToken ct)
    {
        var date = (await BusinessDayAsync(tenantId, facilityId, ct)).Date;
        var locked = await db.DailyCloses.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.FacilityId == facilityId && c.BusinessDate >= date && (c.Status == "Locked" || c.Status == "Submitted"))
            .Select(c => c.BusinessDate).ToListAsync(ct);
        while (locked.Contains(date))
        {
            date = date.AddDays(1);
        }

        return date;
    }

    public static async Task<string> NextSerialAsync(FinanceDbContext db, Guid tenantId, Guid facilityId, string kind, string prefix, CancellationToken ct)
    {
        var numbers = await db.Database.SqlQuery<long>($"""
            INSERT INTO finance.document_sequences (tenant_id, facility_id, kind, next_no) VALUES ({tenantId}, {facilityId}, {kind}, 2)
            ON CONFLICT (tenant_id, facility_id, kind) DO UPDATE SET next_no = finance.document_sequences.next_no + 1
            RETURNING next_no - 1 AS "Value"
            """).ToListAsync(ct);
        return $"{prefix}-{numbers[0]:D6}";
    }

    /// <summary>Posts one balanced entry for a business event; a repeat for the same source and purpose is a no-op.</summary>
    public async Task PostAsync(FinanceDbContext db, Guid tenantId, Guid facilityId, DateOnly businessDate, string sourceType, Guid sourceId, string eventType,
        decimal amount, string currency, string narrative, Guid actorId, DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (amount <= 0 || !MoneyRules.IsMoney(amount))
        {
            throw new InvalidOperationException("Posting amount must be a positive money value.");
        }

        if (await db.JournalEntries.AnyAsync(e => e.TenantId == tenantId && e.SourceType == sourceType && e.SourceId == sourceId && e.Purpose == eventType, ct))
        {
            return;
        }

        var rule = await db.PostingRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.EventType == eventType && r.IsActive)
            .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No active posting rule for {eventType}.");
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.IsActive && (a.Code == rule.DebitAccountCode || a.Code == rule.CreditAccountCode)).ToListAsync(ct);
        var debit = accounts.SingleOrDefault(a => a.Code == rule.DebitAccountCode) ?? throw new InvalidOperationException($"Account {rule.DebitAccountCode} missing.");
        var credit = accounts.SingleOrDefault(a => a.Code == rule.CreditAccountCode) ?? throw new InvalidOperationException($"Account {rule.CreditAccountCode} missing.");
        var entry = new JournalEntryRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            FacilityId = facilityId,
            BusinessDate = businessDate,
            SourceType = sourceType,
            SourceId = sourceId,
            Purpose = eventType,
            RuleVersion = rule.Version,
            Currency = currency,
            Narrative = narrative,
            CreatedBy = actorId,
            OccurredAt = occurredAt,
            PostedAt = clock.GetUtcNow(),
            CorrelationId = context.CorrelationId,
        };
        db.JournalEntries.Add(entry);
        db.JournalLines.Add(new JournalLineRow { Id = Guid.CreateVersion7(), TenantId = tenantId, EntryId = entry.Id, AccountId = debit.Id, Debit = amount });
        db.JournalLines.Add(new JournalLineRow { Id = Guid.CreateVersion7(), TenantId = tenantId, EntryId = entry.Id, AccountId = credit.Id, Credit = amount });
    }

    /// <summary>Balance (debits − credits) of an account for a facility up to and including a business date.</summary>
    public static async Task<decimal> BalanceAsync(FinanceDbContext db, Guid tenantId, Guid facilityId, string accountCode, DateOnly asOf, CancellationToken ct) =>
        await (from l in db.JournalLines.AsNoTracking()
               join e in db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
               join a in db.Accounts.AsNoTracking() on l.AccountId equals a.Id
               where e.TenantId == tenantId && e.FacilityId == facilityId && e.BusinessDate <= asOf && a.Code == accountCode
               select l.Debit - l.Credit).SumAsync(ct);

    public static Error Invalid(string code, string message, string field) =>
        Error.Validation(code, message, new Dictionary<string, string[]> { [field] = [message] });
}
