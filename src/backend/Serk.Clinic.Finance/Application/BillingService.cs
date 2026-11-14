using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Finance.Domain;
using Serk.Clinic.Finance.Infrastructure;
using Serk.Clinic.Patients.Contracts;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Idempotency;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

public sealed record ServiceDto(Guid Id, string Code, string Name, decimal UnitPrice, string Currency, int Version);

public sealed record ServiceRequest(string? Code, string? Name, decimal UnitPrice);

public sealed record InvoiceLineRequest(string? ServiceCode, int Quantity);

public sealed record CreateInvoiceRequest(Guid PatientId, IReadOnlyList<InvoiceLineRequest>? Lines, decimal DiscountAmount, string? DiscountReason);

public sealed record InvoiceLineDto(string ServiceCode, string Description, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record PaymentDto(Guid Id, Guid InvoiceId, string Tender, string? Provider, string? ProviderReference, decimal Amount, string Currency, string Status,
    string? ReceiptSerial, decimal RefundedAmount, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt, DateOnly? BusinessDate, bool Settled);

public sealed record InvoiceDto(Guid Id, string? Serial, string Status, Guid PatientId, string PatientDisplayName, string PatientMrn, string Currency, decimal GrossAmount,
    decimal DiscountAmount, decimal TotalAmount, decimal PaidAmount, decimal BalanceDue, string? DiscountReason, DateTimeOffset CreatedAt, DateTimeOffset? IssuedAt,
    DateOnly? BusinessDate, int Version, IReadOnlyList<InvoiceLineDto> Lines, IReadOnlyList<PaymentDto> Payments);

public sealed record CashPaymentRequest(decimal Amount);

public sealed record ElectronicPaymentRequest(string? Tender, string? Provider, string? ProviderReference, decimal Amount);

public sealed record ProviderCallback(Guid TenantId, string? Provider, string? Reference, decimal Amount, string? Currency, string? Status);

public sealed record RefundRequest(Guid PaymentId, decimal Amount, string? Reason);

public sealed record RefundDto(Guid Id, string? Serial, Guid PaymentId, string? ReceiptSerial, string Tender, decimal Amount, string Reason, string Status, Guid RequestedBy,
    DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt);

/// <summary>
/// Invoices, payments, receipts and refunds. Money invariants enforced here and again in the database: issued invoices are
/// frozen; a payment never exceeds the balance due; cash needs the collector's own open drawer; an electronic payment counts
/// only when the provider's authenticated callback confirms it (once, however often it is replayed); refunds and large
/// discounts need a second person. Every money event posts to the ledger in the same transaction.
/// </summary>
public sealed class BillingService(DbSession session, RequestContext context, Ledger ledger, IPatientDirectory patients, AuditTrail audit, IdempotencyStore idempotency, TimeProvider clock)
{
    private static readonly string[] ElectronicTenders = ["MobileMoney", "BankTransfer", "Card"];

    // ---------- price list ----------
    public async Task<IReadOnlyList<ServiceDto>> ListServicesAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        return await db.Services.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.IsCurrent).OrderBy(s => s.Name)
            .Select(s => new ServiceDto(s.Id, s.Code, s.Name, s.UnitPrice, s.Currency, s.Version)).ToListAsync(ct);
    }

    /// <summary>Creates a service or a new price version. Earlier versions stay untouched so issued invoices keep their price.</summary>
    public async Task<Result<ServiceDto>> SaveServiceAsync(ServiceRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var code = (body.Code ?? "").Trim().ToUpperInvariant();
        var name = (body.Name ?? "").Trim();
        if (code.Length is < 2 or > 20 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || code[0] == '-')
        {
            return Ledger.Invalid("invalid_code", "Service code must be 2–20 letters, digits or dashes.", "code");
        }

        if (name.Length is < 2 or > 160)
        {
            return Ledger.Invalid("invalid_name", "Service name must be 2–160 characters.", "name");
        }

        if (!MoneyRules.IsMoney(body.UnitPrice))
        {
            return Ledger.Invalid("invalid_price", "Price must be zero or more with at most two decimals.", "unitPrice");
        }

        var db = await ledger.DbAsync(ct);
        var current = await db.Services.SingleOrDefaultAsync(s => s.TenantId == actor.TenantId && s.Code == code && s.IsCurrent, ct);
        if (current is not null)
        {
            current.IsCurrent = false;
            await db.SaveChangesAsync(ct); // partial unique index allows one current version
        }

        var row = new ServiceRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            Code = code,
            Name = name,
            UnitPrice = body.UnitPrice,
            Currency = MoneyRules.DefaultCurrency,
            Version = (current?.Version ?? 0) + 1,
            IsCurrent = true,
            CreatedBy = actor.UserId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Services.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("finance.service.save", "service", row.Id, Metadata: new Dictionary<string, string> { ["code"] = code, ["version"] = row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct);
        await session.CommitAsync(ct);
        return new ServiceDto(row.Id, row.Code, row.Name, row.UnitPrice, row.Currency, row.Version);
    }

    // ---------- invoices ----------
    public async Task<Result<InvoiceDto>> CreateInvoiceAsync(CreateInvoiceRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var lines = body.Lines ?? [];
        if (lines.Count is 0 or > 50 || lines.Any(l => l.Quantity is < 1 or > 999 || string.IsNullOrWhiteSpace(l.ServiceCode)))
        {
            return Ledger.Invalid("invalid_lines", "Add 1–50 services, each with a quantity of 1–999.", "lines");
        }

        if (!await patients.ExistsAsync(body.PatientId, ct))
        {
            return Ledger.Invalid("patient_not_found", "Patient not found.", "patientId");
        }

        var db = await ledger.DbAsync(ct);
        var codes = lines.Select(l => l.ServiceCode!.Trim().ToUpperInvariant()).ToList();
        var services = await db.Services.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.IsCurrent && codes.Contains(s.Code)).ToListAsync(ct);
        var missing = codes.FirstOrDefault(c => services.All(s => s.Code != c));
        if (missing is not null)
        {
            return Ledger.Invalid("unknown_service", $"'{missing}' is not on the price list.", "lines");
        }

        var invoice = new InvoiceRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            PatientId = body.PatientId,
            Currency = MoneyRules.DefaultCurrency,
            CreatedBy = actor.UserId,
            CreatedAt = clock.GetUtcNow(),
        };
        var rows = lines.Select(l =>
        {
            var s = services.Single(x => string.Equals(x.Code, l.ServiceCode!.Trim(), StringComparison.OrdinalIgnoreCase));
            return new InvoiceLineRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                InvoiceId = invoice.Id,
                ServiceId = s.Id,
                ServiceCode = s.Code,
                Description = s.Name,
                Quantity = l.Quantity,
                UnitPrice = s.UnitPrice,
                LineTotal = l.Quantity * s.UnitPrice,
            };
        }).ToList();
        invoice.GrossAmount = rows.Sum(r => r.LineTotal);
        if (!MoneyRules.IsMoney(body.DiscountAmount) || body.DiscountAmount > invoice.GrossAmount)
        {
            return Ledger.Invalid("invalid_discount", "Discount must be between zero and the invoice total.", "discountAmount");
        }

        var reason = (body.DiscountReason ?? "").Trim();
        if (body.DiscountAmount > 0 && reason.Length < 5)
        {
            return Ledger.Invalid("discount_reason_required", "Give a reason for the discount (at least 5 characters).", "discountReason");
        }

        invoice.DiscountAmount = body.DiscountAmount;
        invoice.DiscountReason = body.DiscountAmount > 0 ? reason[..Math.Min(reason.Length, 300)] : null;
        invoice.TotalAmount = invoice.GrossAmount - invoice.DiscountAmount;
        invoice.Status = MoneyRules.DiscountNeedsApproval(invoice.GrossAmount, invoice.DiscountAmount) ? "PendingApproval" : "Draft";
        db.Invoices.Add(invoice);
        db.InvoiceLines.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("billing.invoice.create", "invoice", invoice.Id, body.PatientId), ct);
        var committed = await GetInvoiceAsync(invoice.Id, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    /// <summary>A second person approves a discount above the threshold; the invoice can then be issued.</summary>
    public async Task<Result<InvoiceDto>> ApproveDiscountAsync(Guid invoiceId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId, ct);
        if (invoice is null)
        {
            return Error.NotFound();
        }

        if (invoice.Status != "PendingApproval")
        {
            return Error.Conflict("not_pending", "This invoice is not waiting for a discount approval.");
        }

        if (invoice.CreatedBy == actor.UserId)
        {
            return Error.Forbidden("self_approval", "You cannot approve a discount on an invoice you prepared.");
        }

        invoice.Status = "Draft";
        invoice.DiscountApprovedBy = actor.UserId;
        invoice.Version++;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("billing.discount.approve", "invoice", invoice.Id, invoice.PatientId), ct);
        var committed = await GetInvoiceAsync(invoiceId, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    public async Task<Result<InvoiceDto>> IssueAsync(Guid invoiceId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId, ct);
        if (invoice is null)
        {
            return Error.NotFound();
        }

        if (invoice.Status == "PendingApproval")
        {
            return Error.Rule("approval_required", "The discount on this invoice must be approved by a second person first.");
        }

        if (invoice.Status != "Draft")
        {
            return Error.Conflict("already_issued", "This invoice has already been issued or cancelled.");
        }

        if (invoice.TotalAmount <= 0)
        {
            return Error.Rule("zero_invoice", "An invoice with a zero total cannot be issued.");
        }

        var now = clock.GetUtcNow();
        invoice.BusinessDate = await ledger.PostingDateAsync(db, actor.TenantId, actor.FacilityId, ct);
        invoice.Serial = await Ledger.NextSerialAsync(db, actor.TenantId, actor.FacilityId, "Invoice", "INV", ct);
        invoice.Status = "Issued";
        invoice.IssuedBy = actor.UserId;
        invoice.IssuedAt = now;
        invoice.Version++;
        await ledger.PostAsync(db, actor.TenantId, actor.FacilityId, invoice.BusinessDate.Value, "invoice", invoice.Id, PostingEvents.InvoiceIssued,
            invoice.TotalAmount, invoice.Currency, $"Invoice {invoice.Serial} issued", actor.UserId, now, ct);
        if (!await TrySaveAsync(db, ct))
        {
            return Error.Precondition("invoice_changed", "This invoice was changed by someone else. Reload and try again.");
        }

        await audit.RecordAsync(new AuditEntry("billing.invoice.issue", "invoice", invoice.Id, invoice.PatientId), ct);
        var committed = await GetInvoiceAsync(invoiceId, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    public async Task<Result<InvoiceDto>> CancelDraftAsync(Guid invoiceId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId, ct);
        if (invoice is null)
        {
            return Error.NotFound();
        }

        if (invoice.Status is not ("Draft" or "PendingApproval"))
        {
            return Error.Rule("issued_invoice", "An issued invoice cannot be cancelled; refund the payment or ask finance for a correction.");
        }

        invoice.Status = "Cancelled";
        invoice.Version++;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("billing.invoice.cancel", "invoice", invoice.Id, invoice.PatientId), ct);
        var committed = await GetInvoiceAsync(invoiceId, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    public async Task<Result<InvoiceDto>> GetInvoiceAsync(Guid invoiceId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var list = await LoadAsync(db, db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId && i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId), ct);
        return list.Count == 0 ? Error.NotFound() : list[0];
    }

    public async Task<IReadOnlyList<InvoiceDto>> ListInvoicesAsync(Guid? patientId, string? status, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var query = db.Invoices.AsNoTracking().Where(i => i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId);
        if (patientId is { } p)
        {
            query = query.Where(i => i.PatientId == p);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = status == "Open" ? query.Where(i => i.Status == "Issued" || i.Status == "PartiallyPaid" || i.Status == "Draft" || i.Status == "PendingApproval") : query.Where(i => i.Status == status);
        }

        return await LoadAsync(db, query.OrderByDescending(i => i.CreatedAt).Take(100), ct);
    }

    // ---------- payments ----------
    public async Task<Result<InvoiceDto>> PayCashAsync(Guid invoiceId, CashPaymentRequest body, string? idempotencyKey, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var result = await idempotency.ExecuteAsync("billing.pay-cash", idempotencyKey, new { invoiceId, body.Amount }, async innerCt =>
        {
            var db = await ledger.DbAsync(innerCt);
            var invoice = await LockInvoiceAsync(db, invoiceId, actor, innerCt);
            var check = CheckPayable(invoice, body.Amount);
            if (check is not null)
            {
                return check;
            }

            var drawer = await db.Sessions.SingleOrDefaultAsync(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.CashierUserId == actor.UserId && s.Status == "Open", innerCt);
            if (drawer is null)
            {
                return Error.Rule("no_open_session", "Open your cashier session before taking cash.");
            }

            var now = clock.GetUtcNow();
            var payment = new PaymentRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                FacilityId = actor.FacilityId,
                InvoiceId = invoice!.Id,
                Tender = "Cash",
                Amount = body.Amount,
                Currency = invoice.Currency,
                Status = "Verified",
                CashierSessionId = drawer.Id,
                CreatedBy = actor.UserId,
                CreatedAt = now,
                VerifiedAt = now,
                // Cash belongs to the drawer's business day so the drawer count and the day's collections always agree.
                BusinessDate = drawer.BusinessDate,
            };
            payment.ReceiptSerial = await Ledger.NextSerialAsync(db, actor.TenantId, actor.FacilityId, "Receipt", "RCT", innerCt);
            db.Payments.Add(payment);
            Apply(invoice, body.Amount);
            await ledger.PostAsync(db, actor.TenantId, actor.FacilityId, payment.BusinessDate.Value, "payment", payment.Id, PostingEvents.CashPaymentVerified,
                payment.Amount, payment.Currency, $"Cash receipt {payment.ReceiptSerial} for {invoice.Serial}", actor.UserId, now, innerCt);
            await db.SaveChangesAsync(innerCt);
            await audit.RecordAsync(new AuditEntry("billing.payment.cash", "payment", payment.Id, invoice.PatientId), innerCt);
            return await GetInvoiceAsync(invoiceId, innerCt);
        }, ct);
        if (result.IsSuccess)
        {
            await session.CommitAsync(ct);
        }

        return result;
    }

    /// <summary>Records a payment ATTEMPT. Nothing is collected, receipted or posted until the provider confirms it.</summary>
    public async Task<Result<InvoiceDto>> StartElectronicAsync(Guid invoiceId, ElectronicPaymentRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var provider = (body.Provider ?? "").Trim();
        var reference = (body.ProviderReference ?? "").Trim();
        if (!ElectronicTenders.Contains(body.Tender) || provider.Length is < 2 or > 40 || reference.Length is < 3 or > 80)
        {
            return Ledger.Invalid("invalid_payment", "Choose the payment type, provider and the provider's transaction reference.", "providerReference");
        }

        var db = await ledger.DbAsync(ct);
        var invoice = await LockInvoiceAsync(db, invoiceId, actor, ct);
        var check = CheckPayable(invoice, body.Amount);
        if (check is not null)
        {
            return check;
        }

        if (await db.Payments.AnyAsync(p => p.TenantId == actor.TenantId && p.Provider == provider && p.ProviderReference == reference, ct))
        {
            return Error.Conflict("duplicate_reference", "This provider reference has already been used for a payment.");
        }

        var payment = new PaymentRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            InvoiceId = invoice!.Id,
            Tender = body.Tender!,
            Provider = provider,
            ProviderReference = reference,
            Amount = body.Amount,
            Currency = invoice.Currency,
            Status = "PendingProvider",
            CreatedBy = actor.UserId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("billing.payment.start", "payment", payment.Id, invoice.PatientId), ct);
        var committed = await GetInvoiceAsync(invoiceId, ct);
        await session.CommitAsync(ct);
        return committed;
    }

    /// <summary>
    /// Provider confirmation (authenticated by the endpoint's signature check). Idempotent: replaying a confirmation for an
    /// already verified payment changes nothing and posts nothing.
    /// </summary>
    public async Task<Result<PaymentDto>> ConfirmProviderAsync(ProviderCallback cb, CancellationToken ct)
    {
        context.SystemTenantId = cb.TenantId;
        var db = await ledger.DbAsync(ct);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.TenantId == cb.TenantId && p.Provider == cb.Provider && p.ProviderReference == cb.Reference, ct);
        if (payment is null)
        {
            return Error.NotFound("payment_not_found", "No payment attempt matches this provider reference.");
        }

        if (payment.Status == "Verified")
        {
            return ToDto(payment);
        }

        if (payment.Status == "Failed")
        {
            return Error.Conflict("payment_failed", "This payment attempt has already failed.");
        }

        if (cb.Amount != payment.Amount || !string.Equals(cb.Currency, payment.Currency, StringComparison.Ordinal))
        {
            return Error.Conflict("amount_mismatch", "The confirmed amount or currency does not match the payment attempt; left pending for review.");
        }

        var now = clock.GetUtcNow();
        if (!string.Equals(cb.Status, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            payment.Status = "Failed";
            await db.SaveChangesAsync(ct);
            await session.CommitAsync(ct);
            return ToDto(payment);
        }

        var invoice = (await db.Invoices.FromSqlInterpolated($"SELECT * FROM finance.invoices WHERE id = {payment.InvoiceId} FOR UPDATE").ToListAsync(ct)).Single();
        if (payment.Amount > invoice.TotalAmount - invoice.PaidAmount)
        {
            return Error.Conflict("exceeds_balance", "The invoice has been paid in the meantime; this confirmation needs finance review.");
        }

        payment.Status = "Verified";
        payment.VerifiedAt = now;
        payment.BusinessDate = await ledger.PostingDateAsync(db, payment.TenantId, payment.FacilityId, ct);
        payment.ReceiptSerial = await Ledger.NextSerialAsync(db, payment.TenantId, payment.FacilityId, "Receipt", "RCT", ct);
        Apply(invoice, payment.Amount);
        await ledger.PostAsync(db, payment.TenantId, payment.FacilityId, payment.BusinessDate.Value, "payment", payment.Id, PostingEvents.ElectronicPaymentVerified,
            payment.Amount, payment.Currency, $"{payment.Provider} receipt {payment.ReceiptSerial} for {invoice.Serial}", Ledger.SystemUserId, now, ct);
        await db.SaveChangesAsync(ct);
        await session.CommitAsync(ct);
        return ToDto(payment);
    }

    // ---------- refunds (maker-checker) ----------
    public async Task<Result<RefundDto>> RequestRefundAsync(RefundRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var reason = (body.Reason ?? "").Trim();
        if (reason.Length is < 5 or > 500)
        {
            return Ledger.Invalid("reason_required", "Give a reason for the refund (5–500 characters).", "reason");
        }

        var db = await ledger.DbAsync(ct);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == body.PaymentId && p.TenantId == actor.TenantId && p.FacilityId == actor.FacilityId, ct);
        if (payment is null || payment.Status != "Verified")
        {
            return Error.NotFound("payment_not_found", "Verified payment not found.");
        }

        var pending = await db.Refunds.Where(r => r.PaymentId == payment.Id && r.Status == "Requested").SumAsync(r => r.Amount, ct);
        if (!MoneyRules.IsMoney(body.Amount) || body.Amount <= 0 || body.Amount > payment.Amount - payment.RefundedAmount - pending)
        {
            return Ledger.Invalid("invalid_amount", "The refund cannot exceed what is still refundable on this payment.", "amount");
        }

        Guid? drawerId = null;
        if (payment.Tender == "Cash")
        {
            drawerId = await db.Sessions.Where(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.CashierUserId == actor.UserId && s.Status == "Open")
                .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
            if (drawerId is null)
            {
                return Error.Rule("no_open_session", "A cash refund is paid from your drawer: open your cashier session first.");
            }
        }

        var refund = new RefundRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            PaymentId = payment.Id,
            Amount = body.Amount,
            Reason = reason,
            CashierSessionId = drawerId,
            RequestedBy = actor.UserId,
            RequestedAt = clock.GetUtcNow(),
        };
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("finance.refund.request", "refund", refund.Id, Reason: reason), ct);
        await session.CommitAsync(ct);
        return ToDto(refund, payment);
    }

    public async Task<Result<RefundDto>> DecideRefundAsync(Guid refundId, bool approve, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var refund = (await db.Refunds.FromSqlInterpolated($"SELECT * FROM finance.refunds WHERE id = {refundId} FOR UPDATE").ToListAsync(ct))
            .SingleOrDefault(r => r.TenantId == actor.TenantId && r.FacilityId == actor.FacilityId);
        if (refund is null)
        {
            return Error.NotFound();
        }

        if (refund.Status != "Requested")
        {
            return Error.Conflict("already_decided", "This refund has already been decided.");
        }

        if (refund.RequestedBy == actor.UserId)
        {
            return Error.Forbidden("self_approval", "You cannot decide a refund you requested.");
        }

        var payment = await db.Payments.SingleAsync(p => p.Id == refund.PaymentId, ct);
        var now = clock.GetUtcNow();
        refund.DecidedBy = actor.UserId;
        refund.DecidedAt = now;
        if (!approve)
        {
            refund.Status = "Rejected";
        }
        else
        {
            var cash = payment.Tender == "Cash";
            if (cash)
            {
                var drawer = await db.Sessions.SingleAsync(s => s.Id == refund.CashierSessionId, ct);
                if (drawer.Status != "Open")
                {
                    return Error.Rule("session_closed", "The requesting cashier's drawer is closed; reject this request and ask for a new one.");
                }

                refund.BusinessDate = drawer.BusinessDate;
            }
            else
            {
                refund.BusinessDate = await ledger.PostingDateAsync(db, actor.TenantId, actor.FacilityId, ct);
            }

            var invoice = (await db.Invoices.FromSqlInterpolated($"SELECT * FROM finance.invoices WHERE id = {payment.InvoiceId} FOR UPDATE").ToListAsync(ct)).Single();
            refund.Status = "Paid";
            refund.Serial = await Ledger.NextSerialAsync(db, actor.TenantId, actor.FacilityId, "Refund", "RFD", ct);
            payment.RefundedAmount += refund.Amount;
            Apply(invoice, -refund.Amount);
            await ledger.PostAsync(db, actor.TenantId, actor.FacilityId, refund.BusinessDate.Value, "refund", refund.Id,
                cash ? PostingEvents.CashRefundPaid : PostingEvents.ElectronicRefundPaid, refund.Amount, payment.Currency,
                $"Refund {refund.Serial} of receipt {payment.ReceiptSerial}", actor.UserId, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry(approve ? "finance.refund.approve" : "finance.refund.reject", "refund", refund.Id), ct);
        await session.CommitAsync(ct);
        return ToDto(refund, payment);
    }

    public async Task<IReadOnlyList<RefundDto>> ListRefundsAsync(string? status, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var rows = await (from r in db.Refunds.AsNoTracking()
                          join p in db.Payments.AsNoTracking() on r.PaymentId equals p.Id
                          where r.TenantId == actor.TenantId && r.FacilityId == actor.FacilityId && (status == null || r.Status == status)
                          orderby r.RequestedAt descending
                          select new { r, p }).Take(100).ToListAsync(ct);
        return rows.Select(x => ToDto(x.r, x.p)).ToList();
    }

    // ---------- helpers ----------
    private static async Task<InvoiceRow?> LockInvoiceAsync(FinanceDbContext db, Guid invoiceId, StaffActor actor, CancellationToken ct) =>
        (await db.Invoices.FromSqlInterpolated($"SELECT * FROM finance.invoices WHERE id = {invoiceId} FOR UPDATE").ToListAsync(ct))
            .SingleOrDefault(i => i.TenantId == actor.TenantId && i.FacilityId == actor.FacilityId);

    private static Error? CheckPayable(InvoiceRow? invoice, decimal amount)
    {
        if (invoice is null)
        {
            return Error.NotFound();
        }

        if (invoice.Status is not ("Issued" or "PartiallyPaid"))
        {
            return Error.Rule("not_payable", invoice.Status == "Paid" ? "This invoice is already fully paid." : "Issue the invoice before taking payment.");
        }

        return !MoneyRules.IsMoney(amount) || amount <= 0 || amount > invoice.TotalAmount - invoice.PaidAmount
            ? Ledger.Invalid("invalid_amount", $"Enter an amount up to the balance due ({MoneyRules.Format(invoice.TotalAmount - invoice.PaidAmount)} {invoice.Currency}).", "amount")
            : null;
    }

    private static void Apply(InvoiceRow invoice, decimal delta)
    {
        invoice.PaidAmount += delta;
        invoice.Status = MoneyRules.InvoiceStatusFor(invoice.TotalAmount, invoice.PaidAmount);
        invoice.Version++;
    }

    private static async Task<bool> TrySaveAsync(FinanceDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<List<InvoiceDto>> LoadAsync(FinanceDbContext db, IQueryable<InvoiceRow> query, CancellationToken ct)
    {
        var invoices = await query.ToListAsync(ct);
        var ids = invoices.Select(i => i.Id).ToList();
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => ids.Contains(l.InvoiceId)).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.InvoiceId)).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        // Finance roles without demographic access see the MRN only (minimum necessary); cashiers see the name.
        var showNames = context.Actor?.Has(Capabilities.PatientReadDemographics) == true;
        var banners = await patients.GetBannersAsync(invoices.Select(i => i.PatientId).Distinct().ToList(), ct);
        return invoices.Select(i =>
        {
            banners.TryGetValue(i.PatientId, out var b);
            if (!showNames && b is not null)
            {
                b = b with { DisplayName = "Patient (name hidden)" };
            }

            return new InvoiceDto(i.Id, i.Serial, i.Status, i.PatientId, b?.DisplayName ?? "Patient", b?.Mrn ?? "", i.Currency, i.GrossAmount, i.DiscountAmount, i.TotalAmount,
                i.PaidAmount, i.Status is "Draft" or "PendingApproval" or "Cancelled" ? 0 : i.TotalAmount - i.PaidAmount, i.DiscountReason, i.CreatedAt, i.IssuedAt, i.BusinessDate, i.Version,
                lines.Where(l => l.InvoiceId == i.Id).Select(l => new InvoiceLineDto(l.ServiceCode, l.Description, l.Quantity, l.UnitPrice, l.LineTotal)).ToList(),
                payments.Where(p => p.InvoiceId == i.Id).Select(ToDto).ToList());
        }).ToList();
    }

    internal static PaymentDto ToDto(PaymentRow p) =>
        new(p.Id, p.InvoiceId, p.Tender, p.Provider, p.ProviderReference, p.Amount, p.Currency, p.Status, p.ReceiptSerial, p.RefundedAmount, p.CreatedAt, p.VerifiedAt, p.BusinessDate, p.SettlementId is not null);

    private static RefundDto ToDto(RefundRow r, PaymentRow p) =>
        new(r.Id, r.Serial, r.PaymentId, p.ReceiptSerial, p.Tender, r.Amount, r.Reason, r.Status, r.RequestedBy, r.RequestedAt, r.DecidedAt);
}
