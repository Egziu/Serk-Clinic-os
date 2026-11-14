using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Finance.Domain;
using Serk.Clinic.Finance.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

public sealed record OpenSessionRequest(decimal OpeningFloat);

public sealed record CountSessionRequest(decimal CountedAmount, string? Explanation, int Version);

public sealed record SessionDto(Guid Id, Guid CashierUserId, string Status, decimal OpeningFloat, DateTimeOffset OpenedAt, DateOnly BusinessDate, decimal CashReceipts,
    decimal CashRefunds, decimal? ExpectedAmount, decimal? CountedAmount, decimal? Variance, string? VarianceExplanation, bool VarianceApproved, DateTimeOffset? ClosedAt, int Version);

public sealed record SettlementRequest(string? Provider, string? Reference, IReadOnlyList<Guid>? PaymentIds, decimal FeeAmount);

public sealed record SettlementDto(Guid Id, string Provider, string Reference, decimal GrossAmount, decimal FeeAmount, decimal NetAmount, DateOnly BusinessDate, int Payments);

/// <summary>
/// Cashier sessions (one accountable person per drawer) and provider settlements. Expected cash comes from verified drawer
/// transactions, never from invoices; while the drawer is open the expected figure is NOT shown to the cashier (blind count).
/// </summary>
public sealed class TreasuryService(DbSession session, RequestContext context, Ledger ledger, AuditTrail audit, TimeProvider clock)
{
    public async Task<Result<SessionDto>> OpenAsync(OpenSessionRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (!MoneyRules.IsMoney(body.OpeningFloat))
        {
            return Ledger.Invalid("invalid_float", "Opening float must be zero or more with at most two decimals.", "openingFloat");
        }

        var db = await ledger.DbAsync(ct);
        if (await db.Sessions.AnyAsync(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.CashierUserId == actor.UserId && s.Status == "Open", ct))
        {
            return Error.Conflict("session_open", "You already have an open cashier session.");
        }

        var row = new CashierSessionRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            CashierUserId = actor.UserId,
            OpeningFloat = body.OpeningFloat,
            OpenedAt = clock.GetUtcNow(),
            BusinessDate = await ledger.PostingDateAsync(db, actor.TenantId, actor.FacilityId, ct),
        };
        db.Sessions.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("treasury.session.open", "cashier_session", row.Id), ct);
        var committed = await ToDtoAsync(db, row, blind: true, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    public async Task<Result<SessionDto?>> CurrentAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = await db.Sessions.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.CashierUserId == actor.UserId)
            .OrderByDescending(s => s.OpenedAt).FirstOrDefaultAsync(ct);
        return Result<SessionDto?>.Ok(row is null ? null : await ToDtoAsync(db, row, blind: row.Status == "Open", ct));
    }

    public async Task<IReadOnlyList<SessionDto>> ListAsync(DateOnly? date, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var day = date ?? (await ledger.BusinessDayAsync(actor.TenantId, actor.FacilityId, ct)).Date;
        var rows = await db.Sessions.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && (s.BusinessDate == day || s.Status != "Closed"))
            .OrderBy(s => s.OpenedAt).ToListAsync(ct);
        var list = new List<SessionDto>();
        foreach (var r in rows)
        {
            list.Add(await ToDtoAsync(db, r, blind: false, ct));
        }

        return list;
    }

    /// <summary>The cashier counts the drawer. A zero variance closes it; any variance needs an explanation and a second person.</summary>
    public async Task<Result<SessionDto>> CountAsync(Guid sessionId, CountSessionRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId, ct);
        if (row is null || row.CashierUserId != actor.UserId)
        {
            return Error.NotFound();
        }

        if (row.Status != "Open")
        {
            return Error.Conflict("session_not_open", "This session has already been counted.");
        }

        if (row.Version != body.Version)
        {
            return Error.Precondition("session_changed", "The session changed. Reload and count again.");
        }

        if (!MoneyRules.IsMoney(body.CountedAmount))
        {
            return Ledger.Invalid("invalid_count", "Enter the counted cash with at most two decimals.", "countedAmount");
        }

        if (await db.Refunds.AnyAsync(r => r.CashierSessionId == row.Id && r.Status == "Requested", ct))
        {
            return Error.Rule("pending_refunds", "A cash refund from this drawer is waiting for a decision; it must be approved or rejected first.");
        }

        var (receipts, refunds) = await DrawerTotalsAsync(db, row.Id, ct);
        var expected = MoneyRules.ExpectedCash(row.OpeningFloat, receipts, refunds);
        var variance = body.CountedAmount - expected;
        var explanation = (body.Explanation ?? "").Trim();
        if (variance != 0 && explanation.Length < 5)
        {
            return Ledger.Invalid("explanation_required", $"The count differs from the expected cash by {MoneyRules.Format(variance)}. Recount, or explain the difference (at least 5 characters).", "explanation");
        }

        var now = clock.GetUtcNow();
        row.ExpectedAmount = expected;
        row.CountedAmount = body.CountedAmount;
        row.Variance = variance;
        row.VarianceExplanation = variance == 0 ? null : explanation[..Math.Min(explanation.Length, 500)];
        row.CountedAt = now;
        row.Status = variance == 0 ? "Closed" : "PendingVarianceApproval";
        row.ClosedAt = variance == 0 ? now : null;
        row.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Precondition("session_changed", "The session changed. Reload and count again.");
        }

        await audit.RecordAsync(new AuditEntry("treasury.session.count", "cashier_session", row.Id, Reason: row.VarianceExplanation), ct);
        var committed = await ToDtoAsync(db, row, blind: false, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    /// <summary>A second person accepts the explained variance; the over/short amount is posted and the session closes.</summary>
    public async Task<Result<SessionDto>> ApproveVarianceAsync(Guid sessionId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        if (row.Status != "PendingVarianceApproval")
        {
            return Error.Conflict("not_pending", "This session has no variance waiting for approval.");
        }

        if (row.CashierUserId == actor.UserId)
        {
            return Error.Forbidden("self_approval", "You cannot approve the variance of your own drawer.");
        }

        var now = clock.GetUtcNow();
        row.Status = "Closed";
        row.VarianceApprovedBy = actor.UserId;
        row.ClosedAt = now;
        row.Version++;
        var variance = row.Variance!.Value;
        await ledger.PostAsync(db, actor.TenantId, actor.FacilityId, row.BusinessDate, "cashier_session", row.Id, variance < 0 ? PostingEvents.CashShortage : PostingEvents.CashOverage,
            Math.Abs(variance), MoneyRules.DefaultCurrency, $"Cash {(variance < 0 ? "shortage" : "overage")} approved for drawer session", actor.UserId, now, ct);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("treasury.variance.approve", "cashier_session", row.Id), ct);
        var committed = await ToDtoAsync(db, row, blind: false, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    /// <summary>Records that the provider paid verified electronic payments into the bank (gross less fees), by statement reference.</summary>
    public async Task<Result<SettlementDto>> RecordSettlementAsync(SettlementRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var provider = (body.Provider ?? "").Trim();
        var reference = (body.Reference ?? "").Trim();
        var ids = (body.PaymentIds ?? []).Distinct().ToList();
        if (provider.Length < 2 || reference.Length is < 3 or > 80 || ids.Count is 0 or > 500)
        {
            return Ledger.Invalid("invalid_settlement", "Choose the provider, the bank statement reference and at least one payment.", "reference");
        }

        var db = await ledger.DbAsync(ct);
        if (await db.Settlements.AnyAsync(s => s.TenantId == actor.TenantId && s.Provider == provider && s.Reference == reference, ct))
        {
            return Error.Conflict("duplicate_settlement", "This settlement reference has already been recorded.");
        }

        var payments = await db.Payments.Where(p => p.TenantId == actor.TenantId && p.FacilityId == actor.FacilityId && ids.Contains(p.Id)).ToListAsync(ct);
        if (payments.Count != ids.Count || payments.Any(p => p.Status != "Verified" || p.Tender == "Cash" || p.Provider != provider || p.SettlementId is not null))
        {
            return Error.Rule("not_settleable", "Every payment must be a verified, unsettled electronic payment from this provider.");
        }

        var gross = payments.Sum(p => p.Amount - p.RefundedAmount);
        if (gross <= 0 || !MoneyRules.IsMoney(body.FeeAmount) || body.FeeAmount >= gross)
        {
            return Ledger.Invalid("invalid_fee", "The fee must be zero or more and less than the settled amount.", "feeAmount");
        }

        var now = clock.GetUtcNow();
        var row = new SettlementRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            Provider = provider,
            Reference = reference,
            GrossAmount = gross,
            FeeAmount = body.FeeAmount,
            Currency = MoneyRules.DefaultCurrency,
            BusinessDate = await ledger.PostingDateAsync(db, actor.TenantId, actor.FacilityId, ct),
            CreatedBy = actor.UserId,
            CreatedAt = now,
        };
        db.Settlements.Add(row);
        await db.SaveChangesAsync(ct);
        foreach (var p in payments)
        {
            p.SettlementId = row.Id;
        }

        await ledger.PostAsync(db, actor.TenantId, actor.FacilityId, row.BusinessDate, "settlement", row.Id, PostingEvents.ProviderSettlementNet, gross - body.FeeAmount,
            row.Currency, $"{provider} settlement {reference} (net)", actor.UserId, now, ct);
        if (body.FeeAmount > 0)
        {
            await ledger.PostAsync(db, actor.TenantId, actor.FacilityId, row.BusinessDate, "settlement", row.Id, PostingEvents.ProviderSettlementFee, body.FeeAmount,
                row.Currency, $"{provider} settlement {reference} (fees)", actor.UserId, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("treasury.settlement.record", "settlement", row.Id), ct);
        await session.CommitAsync(ct);
        return new SettlementDto(row.Id, provider, reference, gross, body.FeeAmount, gross - body.FeeAmount, row.BusinessDate, payments.Count);
    }

    public async Task<IReadOnlyList<PaymentDto>> UnsettledAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var rows = await db.Payments.AsNoTracking().Where(p => p.TenantId == actor.TenantId && p.FacilityId == actor.FacilityId && p.Status == "Verified" && p.Tender != "Cash" && p.SettlementId == null)
            .OrderBy(p => p.VerifiedAt).Take(500).ToListAsync(ct);
        return rows.Select(BillingService.ToDto).ToList();
    }

    internal static async Task<(decimal Receipts, decimal Refunds)> DrawerTotalsAsync(FinanceDbContext db, Guid sessionId, CancellationToken ct)
    {
        var receipts = await db.Payments.Where(p => p.CashierSessionId == sessionId && p.Status == "Verified").SumAsync(p => p.Amount, ct);
        var refunds = await db.Refunds.Where(r => r.CashierSessionId == sessionId && r.Status == "Paid").SumAsync(r => r.Amount, ct);
        return (receipts, refunds);
    }

    private static async Task<SessionDto> ToDtoAsync(FinanceDbContext db, CashierSessionRow r, bool blind, CancellationToken ct)
    {
        var (receipts, refunds) = await DrawerTotalsAsync(db, r.Id, ct);
        // Blind count: the cashier does not see receipts/expected totals for an open drawer.
        return new SessionDto(r.Id, r.CashierUserId, r.Status, r.OpeningFloat, r.OpenedAt, r.BusinessDate, blind ? 0 : receipts, blind ? 0 : refunds,
            blind ? null : r.ExpectedAmount ?? MoneyRules.ExpectedCash(r.OpeningFloat, receipts, refunds), r.CountedAmount, r.Variance, r.VarianceExplanation,
            r.VarianceApprovedBy is not null, r.ClosedAt, r.Version);
    }
}
