using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Finance.Domain;
using Serk.Clinic.Finance.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Outbox;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

public sealed record ClosePreviewDto(DateOnly BusinessDate, CloseSnapshot Snapshot, string SnapshotSha256, IReadOnlyList<CloseCheck> Checks, bool CanSubmit, bool FullyReconciled);

public sealed record SubmitCloseRequest(DateOnly BusinessDate, string? Comment);

public sealed record ReturnCloseRequest(string? Reason);

public sealed record DailyCloseDto(Guid Id, string Serial, DateOnly BusinessDate, int CloseVersion, string Status, bool FullyReconciled, CloseSnapshot Snapshot, string SnapshotSha256,
    string? Comment, Guid SubmittedBy, DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt, string? DecisionReason, IReadOnlyList<CloseCheck> Checks, string Digest);

public sealed record DailyCloseLockedEvent(Guid CloseId, Guid FacilityId);

/// <summary>
/// Daily financial close. The snapshot is computed from committed source data, cross-checked against an independent
/// re-aggregation from the ledger, hashed, submitted by one person and approved by another. Blocking checks stop the close;
/// conditional ones (e.g. unsettled provider money) are allowed but the close is then labelled "not fully reconciled".
/// A locked close is immutable; the day is never written to again (later events post to the next open day).
/// </summary>
public sealed class CloseService(DbSession session, RequestContext context, Ledger ledger, AuditTrail audit, OutboxWriter outbox, TimeProvider clock)
{
    public const string LockedMessageType = "finance.daily-close-locked.v1";

    public async Task<Result<ClosePreviewDto>> PreviewAsync(DateOnly? date, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var (today, tz, facilityName) = await ledger.BusinessDayAsync(actor.TenantId, actor.FacilityId, ct);
        var day = date ?? today;
        if (day > today)
        {
            return Ledger.Invalid("future_date", "A future business day cannot be closed.", "businessDate");
        }

        var (snapshot, checks) = await ComputeAsync(db, actor.TenantId, actor.FacilityId, day, tz, facilityName, ct);
        var sha = CanonicalJson.Sha256Hex(CanonicalJson.Serialize(snapshot));
        return new ClosePreviewDto(day, snapshot, sha, checks, checks.All(c => !(c.Severity == "Blocking" && c.Result == "Fail")), checks.All(c => c.Result == "Pass"));
    }

    public async Task<Result<DailyCloseDto>> SubmitAsync(SubmitCloseRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"close:" + actor.FacilityId.ToString("N") + body.BusinessDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}, 0))", ct);
        if (await db.DailyCloses.AnyAsync(c => c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId && c.BusinessDate == body.BusinessDate && (c.Status == "Submitted" || c.Status == "Locked"), ct))
        {
            return Error.Conflict("close_exists", "This business day has already been submitted or locked.");
        }

        var preview = await PreviewAsync(body.BusinessDate, ct);
        if (!preview.IsSuccess)
        {
            return preview.Error!;
        }

        var failing = preview.Value.Checks.Where(c => c.Severity == "Blocking" && c.Result == "Fail").ToList();
        if (failing.Count > 0)
        {
            await audit.RecordIndependentAsync(new AuditEntry("finance.close.blocked", "business_day", actor.FacilityId, Outcome: "blocked", Reason: string.Join("; ", failing.Select(f => f.ControlId))), ct);
            return Error.Rule("close_blocked", "The day cannot be closed yet: " + string.Join(" ", failing.Select(f => $"[{f.ControlId}] {f.Detail}")));
        }

        var version = 1 + await db.DailyCloses.CountAsync(c => c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId && c.BusinessDate == body.BusinessDate, ct);
        var row = new DailyCloseRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            BusinessDate = body.BusinessDate,
            CloseVersion = version,
            Serial = await Ledger.NextSerialAsync(db, actor.TenantId, actor.FacilityId, "Close", "DC-" + body.BusinessDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), ct),
            Status = "Submitted",
            FullyReconciled = preview.Value.FullyReconciled,
            Snapshot = CanonicalJson.Serialize(preview.Value.Snapshot),
            SnapshotSha256 = preview.Value.SnapshotSha256,
            Comment = Clip(body.Comment),
            SubmittedBy = actor.UserId,
            SubmittedAt = clock.GetUtcNow(),
        };
        db.DailyCloses.Add(row);
        db.CloseChecks.AddRange(preview.Value.Checks.Select(c => new CloseCheckRow { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, CloseId = row.Id, ControlId = c.ControlId, Result = c.Result, Severity = c.Severity, Detail = c.Detail }));
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("finance.close.submit", "daily_close", row.Id), ct);
        var committed = await GetAsync(row.Id, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    /// <summary>
    /// A different person approves. Everything is recomputed; if the figures no longer match the submitted hash the close is
    /// returned (stale approval is impossible). On success the close is locked and digest deliveries are queued atomically.
    /// </summary>
    public async Task<Result<DailyCloseDto>> ApproveAsync(Guid closeId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = (await db.DailyCloses.FromSqlInterpolated($"SELECT * FROM finance.daily_closes WHERE id = {closeId} FOR UPDATE").ToListAsync(ct))
            .SingleOrDefault(c => c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId);
        if (row is null)
        {
            return Error.NotFound();
        }

        if (row.Status != "Submitted")
        {
            return Error.Conflict("not_submitted", "This close has already been decided.");
        }

        if (row.SubmittedBy == actor.UserId)
        {
            return Error.Forbidden("self_approval", "The close must be approved by a different person than the one who submitted it.");
        }

        var preview = await PreviewAsync(row.BusinessDate, ct);
        var now = clock.GetUtcNow();
        row.DecidedBy = actor.UserId;
        row.DecidedAt = now;
        if (!preview.IsSuccess || !preview.Value.CanSubmit || preview.Value.SnapshotSha256 != row.SnapshotSha256)
        {
            row.Status = "Returned";
            row.DecisionReason = "Source data changed after submission; recomputed figures no longer match the submitted snapshot.";
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(new AuditEntry("finance.close.return", "daily_close", row.Id, Outcome: "stale", Reason: row.DecisionReason), ct);
            await session.CommitAsync(ct);
            return Error.Precondition("close_stale", "The figures changed after submission. The close was returned; review and submit again.");
        }

        row.Status = "Locked";
        var snapshot = CanonicalJson.Deserialize<CloseSnapshot>(row.Snapshot);
        var digest = OwnerDigest.Render(snapshot, row.Serial, row.FullyReconciled, row.SnapshotSha256);
        var recipients = await db.Subscriptions.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.Status == "Verified").ToListAsync(ct);
        db.Deliveries.AddRange(recipients.Select(s => new DeliveryAttemptRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            SubscriptionId = s.Id,
            CloseId = row.Id,
            Channel = s.Channel,
            Payload = digest,
            PayloadSha256 = CanonicalJson.Sha256Hex(digest),
            Status = "Queued",
            CreatedAt = now,
            UpdatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        await outbox.EnqueueAsync(LockedMessageType, new DailyCloseLockedEvent(row.Id, row.FacilityId), ct);
        await audit.RecordAsync(new AuditEntry("finance.close.lock", "daily_close", row.Id), ct);
        var committed = await GetAsync(row.Id, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    public async Task<Result<DailyCloseDto>> ReturnAsync(Guid closeId, ReturnCloseRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var reason = (body.Reason ?? "").Trim();
        if (reason.Length < 5)
        {
            return Ledger.Invalid("reason_required", "Say why the close is returned (at least 5 characters).", "reason");
        }

        var db = await ledger.DbAsync(ct);
        var row = await db.DailyCloses.SingleOrDefaultAsync(c => c.Id == closeId && c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        if (row.Status != "Submitted")
        {
            return Error.Conflict("not_submitted", "This close has already been decided.");
        }

        if (row.SubmittedBy == actor.UserId)
        {
            return Error.Forbidden("self_approval", "Only a different person can decide a submitted close.");
        }

        row.Status = "Returned";
        row.DecidedBy = actor.UserId;
        row.DecidedAt = clock.GetUtcNow();
        row.DecisionReason = Clip(reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("finance.close.return", "daily_close", row.Id, Reason: row.DecisionReason), ct);
        var committed = await GetAsync(row.Id, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    public async Task<Result<DailyCloseDto>> GetAsync(Guid closeId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = await db.DailyCloses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == closeId && c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId, ct);
        return row is null ? Error.NotFound() : await ToDtoAsync(db, row, ct);
    }

    public async Task<IReadOnlyList<DailyCloseDto>> ListAsync(bool lockedOnly, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var rows = await db.DailyCloses.AsNoTracking().Where(c => c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId && (!lockedOnly || c.Status == "Locked"))
            .OrderByDescending(c => c.BusinessDate).ThenByDescending(c => c.CloseVersion).Take(60).ToListAsync(ct);
        var list = new List<DailyCloseDto>();
        foreach (var r in rows)
        {
            list.Add(await ToDtoAsync(db, r, ct));
        }

        return list;
    }

    private static async Task<DailyCloseDto> ToDtoAsync(FinanceDbContext db, DailyCloseRow row, CancellationToken ct)
    {
        var checks = await db.CloseChecks.AsNoTracking().Where(c => c.CloseId == row.Id).OrderBy(c => c.ControlId).Select(c => new CloseCheck(c.ControlId, c.Result, c.Severity, c.Detail)).ToListAsync(ct);
        var snapshot = CanonicalJson.Deserialize<CloseSnapshot>(row.Snapshot);
        return new DailyCloseDto(row.Id, row.Serial, row.BusinessDate, row.CloseVersion, row.Status, row.FullyReconciled, snapshot, row.SnapshotSha256, row.Comment, row.SubmittedBy,
            row.SubmittedAt, row.DecidedAt, row.DecisionReason, checks, OwnerDigest.Render(snapshot, row.Serial, row.FullyReconciled, row.SnapshotSha256));
    }

    /// <summary>Computes the snapshot and the close checks for one facility-local business day.</summary>
    internal static async Task<(CloseSnapshot Snapshot, List<CloseCheck> Checks)> ComputeAsync(FinanceDbContext db, Guid tenant, Guid facility, DateOnly day, string tz, string facilityName, CancellationToken ct)
    {
        var invoices = await db.Invoices.AsNoTracking().Where(i => i.TenantId == tenant && i.FacilityId == facility && i.BusinessDate == day).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant && p.FacilityId == facility && p.BusinessDate == day && p.Status == "Verified").ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.TenantId == tenant && r.FacilityId == facility && r.BusinessDate == day && r.Status == "Paid").ToListAsync(ct);
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.TenantId == tenant && s.FacilityId == facility && s.BusinessDate == day).ToListAsync(ct);
        var entries = await (from e in db.JournalEntries.AsNoTracking()
                             where e.TenantId == tenant && e.FacilityId == facility && e.BusinessDate == day
                             select new { e.Id, e.SourceType, e.SourceId, e.Purpose }).ToListAsync(ct);
        var entryIds = entries.Select(e => e.Id).ToList();
        var lines = await (from l in db.JournalLines.AsNoTracking()
                           join a in db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                           where entryIds.Contains(l.EntryId)
                           select new { l.EntryId, a.Code, l.Debit, l.Credit }).ToListAsync(ct);

        decimal drawerReceipts = 0, drawerRefunds = 0;
        foreach (var s in sessions)
        {
            var (r, f) = await TreasuryService.DrawerTotalsAsync(db, s.Id, ct);
            drawerReceipts += r;
            drawerRefunds += f;
        }

        var cash = payments.Where(p => p.Tender == "Cash").Sum(p => p.Amount);
        var electronic = payments.Where(p => p.Tender != "Cash").Sum(p => p.Amount);
        var openingFloat = sessions.Sum(s => s.OpeningFloat);
        var expected = MoneyRules.ExpectedCash(openingFloat, drawerReceipts, drawerRefunds);
        var counted = sessions.Sum(s => s.CountedAmount ?? 0);
        var variance = sessions.Sum(s => s.Variance ?? 0);
        var ar = await Ledger.BalanceAsync(db, tenant, facility, DefaultChart.PatientReceivable, day, ct);
        var clearing = await Ledger.BalanceAsync(db, tenant, facility, DefaultChart.ProviderClearing, day, ct);
        var revenue = lines.Where(l => l.Code == DefaultChart.ServiceRevenue).Sum(l => l.Credit - l.Debit);

        var exceptions = new List<CloseException>();
        var varianceSessions = sessions.Where(s => (s.Variance ?? 0) != 0).ToList();
        if (varianceSessions.Count > 0)
        {
            exceptions.Add(new CloseException(variance < 0 ? "CASH_SHORTAGE" : "CASH_OVERAGE", varianceSessions.Count, Math.Abs(variance), varianceSessions.All(s => s.VarianceApprovedBy is not null)));
        }

        var unsettled = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant && p.FacilityId == facility && p.Status == "Verified" && p.Tender != "Cash" && p.SettlementId == null && p.BusinessDate <= day).ToListAsync(ct);
        if (unsettled.Count > 0)
        {
            exceptions.Add(new CloseException("PENDING_SETTLEMENT", unsettled.Count, unsettled.Sum(p => p.Amount - p.RefundedAmount), false));
        }

        var snapshot = new CloseSnapshot(1, facilityName, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), tz, MoneyRules.DefaultCurrency,
            invoices.Sum(i => i.GrossAmount), invoices.Sum(i => i.DiscountAmount), revenue, new TenderTotals(cash, electronic, cash + electronic),
            new DrawerTotals(openingFloat, drawerReceipts, drawerRefunds, expected, counted, variance, varianceSessions.All(s => s.VarianceApprovedBy is not null), sessions.Count),
            refunds.Sum(r => r.Amount), ar, clearing, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), invoices.Count, payments.Count, exceptions, OwnerDigest.Policy);

        // ---- checks (SRS v2 §7.2) ----
        var checks = new List<CloseCheck>();
        void Add(string id, bool pass, string severity, string okText, string failText) =>
            checks.Add(new CloseCheck(id, pass ? "Pass" : severity == "Blocking" ? "Fail" : "Conditional", severity, pass ? okText : failText));

        var openSessions = sessions.Count(s => s.Status == "Open");
        Add("CLOSE-01", openSessions == 0, "Blocking", "All cashier sessions are counted.", $"{openSessions} cashier session(s) are still open.");

        bool Posted(string type, Guid id) => entries.Any(e => e.SourceType == type && e.SourceId == id);
        var unposted = invoices.Where(i => !Posted("invoice", i.Id)).Select(i => $"invoice {i.Serial}")
            .Concat(payments.Where(p => !Posted("payment", p.Id)).Select(p => $"receipt {p.ReceiptSerial}"))
            .Concat(refunds.Where(r => !Posted("refund", r.Id)).Select(r => $"refund {r.Serial}")).ToList();
        Add("CLOSE-02", unposted.Count == 0, "Blocking", "Every invoice, receipt and refund has a journal entry.", "Missing journal entry for: " + string.Join(", ", unposted) + ".");

        var unbalanced = lines.GroupBy(l => l.EntryId).Count(g => g.Sum(l => l.Debit) != g.Sum(l => l.Credit));
        Add("CLOSE-03", unbalanced == 0 && snapshot.JournalDebits == snapshot.JournalCredits, "Blocking", $"Journal is balanced ({MoneyRules.Format(snapshot.JournalDebits)} debits = credits).", $"{unbalanced} unbalanced journal entr(ies).");

        var pendingVariance = sessions.Count(s => s.Status == "PendingVarianceApproval");
        Add("CLOSE-04", pendingVariance == 0, "Blocking", "Cash variances are resolved or approved.", $"{pendingVariance} cash variance(s) are waiting for approval.");

        Add("CLOSE-05", unsettled.Count == 0, "Conditional", "All electronic payments are settled to the bank.", $"{unsettled.Count} electronic payment(s) not yet settled by the provider ({MoneyRules.Format(unsettled.Sum(p => p.Amount - p.RefundedAmount))} {MoneyRules.DefaultCurrency}).");

        var pendingRefunds = await db.Refunds.AsNoTracking().CountAsync(r => r.TenantId == tenant && r.FacilityId == facility && r.Status == "Requested", ct);
        var pendingDiscounts = await db.Invoices.AsNoTracking().CountAsync(i => i.TenantId == tenant && i.FacilityId == facility && i.Status == "PendingApproval", ct);
        Add("CLOSE-06", pendingRefunds + pendingDiscounts == 0, "Blocking", "No refunds or discounts are waiting for a decision.", $"{pendingRefunds} refund(s) and {pendingDiscounts} discount(s) are waiting for a decision.");

        // CLOSE-12: independent re-aggregation — the day's collections and refunds recomputed from the ledger must equal the
        // subledger (payments/refunds), and receivables per invoices must equal the ledger's receivable balance.
        var ledgerCollections = entries.Where(e => e.SourceType == "payment").Join(lines, e => e.Id, l => l.EntryId, (_, l) => l)
            .Where(l => l.Code is DefaultChart.Cash or DefaultChart.ProviderClearing).Sum(l => l.Debit);
        var ledgerRefunds = entries.Where(e => e.SourceType == "refund").Join(lines, e => e.Id, l => l.EntryId, (_, l) => l)
            .Where(l => l.Code is DefaultChart.Cash or DefaultChart.ProviderClearing).Sum(l => l.Credit);
        var subledgerAr = await db.Invoices.AsNoTracking().Where(i => i.TenantId == tenant && i.FacilityId == facility && i.BusinessDate <= day).SumAsync(i => i.TotalAmount, ct)
            - await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant && p.FacilityId == facility && p.Status == "Verified" && p.BusinessDate <= day).SumAsync(p => p.Amount, ct)
            + await db.Refunds.AsNoTracking().Where(r => r.TenantId == tenant && r.FacilityId == facility && r.Status == "Paid" && r.BusinessDate <= day).SumAsync(r => r.Amount, ct);
        var agrees = ledgerCollections == cash + electronic && ledgerRefunds == snapshot.RefundsActuallyPaid && subledgerAr == ar && drawerReceipts == cash;
        Add("CLOSE-12", agrees, "Blocking", "Ledger re-aggregation agrees with invoices, receipts, refunds and drawers.",
            $"Re-aggregation mismatch: collections {MoneyRules.Format(cash + electronic)} vs ledger {MoneyRules.Format(ledgerCollections)}; receivables {MoneyRules.Format(subledgerAr)} vs ledger {MoneyRules.Format(ar)}; drawer cash {MoneyRules.Format(drawerReceipts)} vs cash receipts {MoneyRules.Format(cash)}.");
        return (snapshot, checks);
    }

    private static string? Clip(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 500)];
}
