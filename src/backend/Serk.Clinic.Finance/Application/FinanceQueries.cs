using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

public sealed record TrialBalanceLine(string Code, string Name, string Type, decimal Debits, decimal Credits, decimal Balance);

public sealed record TrialBalanceDto(DateOnly AsOf, string Currency, IReadOnlyList<TrialBalanceLine> Lines, decimal TotalDebits, decimal TotalCredits, bool Balanced);

public sealed record TraceStep(string Step, string Reference, string Status, decimal? Amount, DateTimeOffset? At, string Detail);

public sealed record TraceDto(Guid InvoiceId, string? Serial, string Currency, IReadOnlyList<TraceStep> Steps);

public sealed record ApprovalItem(string Kind, Guid Id, string Reference, decimal Amount, string Reason, DateTimeOffset RequestedAt, bool RequestedByMe);

/// <summary>Read-only finance views: trial balance, "trace every birr" for an invoice, and the approvals queue.</summary>
public sealed class FinanceQueries(RequestContext context, Ledger ledger)
{
    public async Task<TrialBalanceDto> TrialBalanceAsync(DateOnly? asOf, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var day = asOf ?? (await ledger.BusinessDayAsync(actor.TenantId, actor.FacilityId, ct)).Date;
        var sums = await (from l in db.JournalLines.AsNoTracking()
                          join e in db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                          where e.TenantId == actor.TenantId && e.FacilityId == actor.FacilityId && e.BusinessDate <= day
                          group l by l.AccountId into g
                          select new { AccountId = g.Key, Debits = g.Sum(x => x.Debit), Credits = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.TenantId == actor.TenantId).OrderBy(a => a.Code).ToListAsync(ct);
        var lines = accounts.Select(a =>
        {
            var s = sums.SingleOrDefault(x => x.AccountId == a.Id);
            return new TrialBalanceLine(a.Code, a.Name, a.Type, s?.Debits ?? 0, s?.Credits ?? 0, (s?.Debits ?? 0) - (s?.Credits ?? 0));
        }).ToList();
        return new TrialBalanceDto(day, Domain.MoneyRules.DefaultCurrency, lines, lines.Sum(l => l.Debits), lines.Sum(l => l.Credits), lines.Sum(l => l.Debits) == lines.Sum(l => l.Credits));
    }

    /// <summary>Invoice → receipts → refunds → journal entries → drawer → settlement → daily close, with ids, amounts and states.</summary>
    public async Task<Result<TraceDto>> TraceInvoiceAsync(Guid invoiceId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId, ct);
        if (invoice is null)
        {
            return Error.NotFound();
        }

        var payments = await db.Payments.AsNoTracking().Where(p => p.InvoiceId == invoiceId).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var paymentIds = payments.Select(p => p.Id).ToList();
        var refunds = await db.Refunds.AsNoTracking().Where(r => paymentIds.Contains(r.PaymentId)).ToListAsync(ct);
        var sourceIds = paymentIds.Concat(refunds.Select(r => r.Id)).Append(invoiceId).ToList();
        var settlementIds = payments.Where(p => p.SettlementId != null).Select(p => p.SettlementId!.Value).Distinct().ToList();
        var settlements = await db.Settlements.AsNoTracking().Where(s => settlementIds.Contains(s.Id)).ToListAsync(ct);
        sourceIds.AddRange(settlementIds);
        var entries = await db.JournalEntries.AsNoTracking().Where(e => e.TenantId == actor.TenantId && sourceIds.Contains(e.SourceId)).ToListAsync(ct);
        var entryIds = entries.Select(e => e.Id).ToList();
        var lines = await (from l in db.JournalLines.AsNoTracking()
                           join a in db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                           where entryIds.Contains(l.EntryId)
                           select new { l.EntryId, a.Code, a.Name, l.Debit, l.Credit }).ToListAsync(ct);
        var dates = entries.Select(e => e.BusinessDate).Distinct().ToList();
        var closes = await db.DailyCloses.AsNoTracking().Where(c => c.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId && dates.Contains(c.BusinessDate) && c.Status == "Locked").ToListAsync(ct);

        var steps = new List<TraceStep>();
        void Journals(Guid sourceId)
        {
            foreach (var e in entries.Where(x => x.SourceId == sourceId))
            {
                var dr = lines.Single(l => l.EntryId == e.Id && l.Debit > 0);
                var cr = lines.Single(l => l.EntryId == e.Id && l.Credit > 0);
                var close = closes.SingleOrDefault(c => c.BusinessDate == e.BusinessDate);
                steps.Add(new TraceStep("Journal entry", e.Id.ToString()[..8], "Posted", dr.Debit, e.PostedAt,
                    $"Dr {dr.Code} {dr.Name} / Cr {cr.Code} {cr.Name} · rule {e.Purpose} v{e.RuleVersion} · business day {e.BusinessDate:yyyy-MM-dd}" + (close is null ? " · day not closed yet" : $" · closed in {close.Serial}")));
            }
        }

        steps.Add(new TraceStep("Invoice", invoice.Serial ?? "(draft)", invoice.Status, invoice.TotalAmount, invoice.IssuedAt ?? invoice.CreatedAt,
            $"Gross {invoice.GrossAmount:0.00}, discount {invoice.DiscountAmount:0.00}"));
        Journals(invoice.Id);
        foreach (var p in payments)
        {
            steps.Add(new TraceStep(p.Tender == "Cash" ? "Cash receipt" : "Electronic payment", p.ReceiptSerial ?? p.ProviderReference ?? p.Id.ToString()[..8], p.Status, p.Amount, p.VerifiedAt ?? p.CreatedAt,
                p.Tender == "Cash" ? $"Drawer session {p.CashierSessionId.ToString()![..8]}" : $"{p.Provider} reference {p.ProviderReference}"));
            Journals(p.Id);
            foreach (var r in refunds.Where(x => x.PaymentId == p.Id))
            {
                steps.Add(new TraceStep("Refund", r.Serial ?? "(requested)", r.Status, r.Amount, r.DecidedAt ?? r.RequestedAt, r.Reason));
                Journals(r.Id);
            }

            if (settlements.SingleOrDefault(s => s.Id == p.SettlementId) is { } st)
            {
                steps.Add(new TraceStep("Provider settlement", st.Reference, "Settled to bank", st.GrossAmount - st.FeeAmount, st.CreatedAt, $"{st.Provider} batch: gross {st.GrossAmount:0.00}, fees {st.FeeAmount:0.00}"));
                Journals(st.Id);
            }
        }

        return new TraceDto(invoice.Id, invoice.Serial, invoice.Currency, steps);
    }

    public async Task<IReadOnlyList<ApprovalItem>> ApprovalsAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var items = new List<ApprovalItem>();
        items.AddRange((await db.Invoices.AsNoTracking().Where(i => i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId && i.Status == "PendingApproval").ToListAsync(ct))
            .Select(i => new ApprovalItem("Discount", i.Id, $"Invoice draft ({i.GrossAmount:0.00} gross)", i.DiscountAmount, i.DiscountReason ?? "", i.CreatedAt, i.CreatedBy == actor.UserId)));
        items.AddRange((await db.Refunds.AsNoTracking().Where(r => r.TenantId == actor.TenantId && r.FacilityId == actor.FacilityId && r.Status == "Requested").ToListAsync(ct))
            .Select(r => new ApprovalItem("Refund", r.Id, "Refund request", r.Amount, r.Reason, r.RequestedAt, r.RequestedBy == actor.UserId)));
        items.AddRange((await db.Sessions.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.Status == "PendingVarianceApproval").ToListAsync(ct))
            .Select(s => new ApprovalItem("CashVariance", s.Id, $"Drawer count {s.CountedAmount:0.00} vs expected {s.ExpectedAmount:0.00}", s.Variance ?? 0, s.VarianceExplanation ?? "", s.CountedAt ?? s.OpenedAt, s.CashierUserId == actor.UserId)));
        return items.OrderBy(i => i.RequestedAt).ToList();
    }
}
