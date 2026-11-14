using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Serk.Clinic.Finance.Application;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Api;

public static class FinanceEndpoints
{
    public const string SignatureHeader = "X-Serk-Signature";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        var billing = app.MapGroup("/api/v1/billing").WithTags("Billing");
        billing.MapGet("/services", async (BillingService s, CancellationToken ct) => Results.Ok(await s.ListServicesAsync(ct))).RequireCapability();
        billing.MapPost("/services", async (ServiceRequest body, BillingService s, CancellationToken ct) => (await s.SaveServiceAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceConfigure);
        billing.MapGet("/invoices", async (Guid? patientId, string? status, BillingService s, CancellationToken ct) => Results.Ok(await s.ListInvoicesAsync(patientId, status, ct))).RequireCapability(Capabilities.InvoiceWrite);
        billing.MapPost("/invoices", async (CreateInvoiceRequest body, BillingService s, CancellationToken ct) => (await s.CreateInvoiceAsync(body, ct)).ToHttp(i => Results.Created($"/api/v1/billing/invoices/{i.Id}", i))).RequireCapability(Capabilities.InvoiceWrite);
        billing.MapGet("/invoices/{id:guid}", async (Guid id, BillingService s, RequestContextAccessor a, CancellationToken ct) =>
            a.CanReadInvoices ? (await s.GetInvoiceAsync(id, ct)).ToHttp(Results.Ok) : ResultHttp.Problem(Error.Forbidden("capability_required", "Your role does not permit this action."))).RequireCapability();
        billing.MapPost("/invoices/{id:guid}/issue", async (Guid id, BillingService s, CancellationToken ct) => (await s.IssueAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.InvoiceWrite);
        billing.MapPost("/invoices/{id:guid}/cancel", async (Guid id, BillingService s, CancellationToken ct) => (await s.CancelDraftAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.InvoiceWrite);
        billing.MapPost("/invoices/{id:guid}/approve-discount", async (Guid id, BillingService s, CancellationToken ct) => (await s.ApproveDiscountAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceApprove);
        billing.MapPost("/invoices/{id:guid}/payments/cash", async (Guid id, CashPaymentRequest body, HttpRequest http, BillingService s, CancellationToken ct) =>
            (await s.PayCashAsync(id, body, IdempotencyKey.From(http), ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.PaymentCollect);
        billing.MapPost("/invoices/{id:guid}/payments/electronic", async (Guid id, ElectronicPaymentRequest body, BillingService s, CancellationToken ct) =>
            (await s.StartElectronicAsync(id, body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.PaymentCollect);

        // Provider confirmation: NOT a staff endpoint. Authenticated by an HMAC-SHA256 signature of the raw body with the
        // provider's shared secret (sandbox connector). A browser "payment succeeded" message is never trusted.
        billing.MapPost("/payments/confirm", async (HttpRequest http, IConfiguration config, BillingService s, CancellationToken ct) =>
        {
            var (ok, body) = await ReadSignedAsync<ProviderCallback>(http, config["Finance:Providers:Sandbox:Secret"], ct);
            return ok ? (await s.ConfirmProviderAsync(body!, ct)).ToHttp(Results.Ok) : Results.Unauthorized();
        }).AllowAnonymous();

        var finance = app.MapGroup("/api/v1/finance").WithTags("Finance");
        finance.MapPost("/refunds", async (RefundRequest body, BillingService s, CancellationToken ct) => (await s.RequestRefundAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.InvoiceWrite);
        finance.MapGet("/refunds", async (string? status, BillingService s, CancellationToken ct) => Results.Ok(await s.ListRefundsAsync(status, ct))).RequireCapability(Capabilities.FinanceRead);
        finance.MapPost("/refunds/{id:guid}/approve", async (Guid id, BillingService s, CancellationToken ct) => (await s.DecideRefundAsync(id, true, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceApprove);
        finance.MapPost("/refunds/{id:guid}/reject", async (Guid id, BillingService s, CancellationToken ct) => (await s.DecideRefundAsync(id, false, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceApprove);
        finance.MapGet("/approvals", async (FinanceQueries q, CancellationToken ct) => Results.Ok(await q.ApprovalsAsync(ct))).RequireCapability(Capabilities.FinanceApprove);
        finance.MapGet("/trial-balance", async (DateOnly? asOf, FinanceQueries q, CancellationToken ct) => Results.Ok(await q.TrialBalanceAsync(asOf, ct))).RequireCapability(Capabilities.FinanceRead);
        finance.MapGet("/invoices/{id:guid}/trace", async (Guid id, FinanceQueries q, CancellationToken ct) => (await q.TraceInvoiceAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceRead);
        finance.MapGet("/invoices", async (string? status, BillingService s, CancellationToken ct) => Results.Ok(await s.ListInvoicesAsync(null, status, ct))).RequireCapability(Capabilities.FinanceRead);
        finance.MapGet("/daily-closes/preview", async (DateOnly? date, CloseService s, CancellationToken ct) => (await s.PreviewAsync(date, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceRead);
        finance.MapGet("/daily-closes", async (CloseService s, CancellationToken ct) => Results.Ok(await s.ListAsync(false, ct))).RequireCapability(Capabilities.FinanceRead);
        finance.MapGet("/daily-closes/{id:guid}", async (Guid id, CloseService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceRead);
        finance.MapPost("/daily-closes", async (SubmitCloseRequest body, CloseService s, CancellationToken ct) => (await s.SubmitAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceClose);
        finance.MapPost("/daily-closes/{id:guid}/approve", async (Guid id, CloseService s, CancellationToken ct) => (await s.ApproveAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceApprove);
        finance.MapPost("/daily-closes/{id:guid}/return", async (Guid id, ReturnCloseRequest body, CloseService s, CancellationToken ct) => (await s.ReturnAsync(id, body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceApprove);

        var treasury = app.MapGroup("/api/v1/treasury").WithTags("Treasury");
        treasury.MapPost("/sessions", async (OpenSessionRequest body, TreasuryService s, CancellationToken ct) => (await s.OpenAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.PaymentCollect);
        treasury.MapGet("/sessions/current", async (TreasuryService s, CancellationToken ct) => (await s.CurrentAsync(ct)).ToHttp(x => x is null ? Results.NoContent() : Results.Ok(x))).RequireCapability(Capabilities.PaymentCollect);
        treasury.MapPost("/sessions/{id:guid}/count", async (Guid id, CountSessionRequest body, TreasuryService s, CancellationToken ct) => (await s.CountAsync(id, body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.PaymentCollect);
        treasury.MapGet("/sessions", async (DateOnly? date, TreasuryService s, CancellationToken ct) => Results.Ok(await s.ListAsync(date, ct))).RequireCapability(Capabilities.FinanceRead);
        treasury.MapPost("/sessions/{id:guid}/approve-variance", async (Guid id, TreasuryService s, CancellationToken ct) => (await s.ApproveVarianceAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceApprove);
        treasury.MapGet("/unsettled", async (TreasuryService s, CancellationToken ct) => Results.Ok(await s.UnsettledAsync(ct))).RequireCapability(Capabilities.FinanceRead);
        treasury.MapPost("/settlements", async (SettlementRequest body, TreasuryService s, CancellationToken ct) => (await s.RecordSettlementAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.FinanceClose);

        var owner = app.MapGroup("/api/v1/owner").WithTags("Owner reports");
        owner.MapGet("/reports/daily", async (CloseService s, CancellationToken ct) => Results.Ok(await s.ListAsync(true, ct))).RequireCapability(Capabilities.OwnerReportsRead);
        owner.MapGet("/subscriptions", async (OwnerService s, CancellationToken ct) => Results.Ok(await s.ListSubscriptionsAsync(ct))).RequireCapability(Capabilities.OwnerSubscriptionsManage);
        owner.MapPost("/subscriptions", async (StartSubscriptionRequest body, OwnerService s, CancellationToken ct) => (await s.StartAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.OwnerSubscriptionsManage);
        owner.MapPost("/subscriptions/{id:guid}/revoke", async (Guid id, OwnerService s, CancellationToken ct) => (await s.RevokeAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.OwnerSubscriptionsManage);
        owner.MapGet("/deliveries", async (OwnerService s, CancellationToken ct) => Results.Ok(await s.ListDeliveriesAsync(ct))).RequireCapability(Capabilities.OwnerReportsRead);
        owner.MapPost("/deliveries/{id:guid}/retry", async (Guid id, OwnerService s, CancellationToken ct) => (await s.RetryAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.OwnerSubscriptionsManage);

        // Channel connector callback (bot/mail gateway), authenticated by its own HMAC secret. Never a staff endpoint.
        owner.MapPost("/subscriptions/verify", async (HttpRequest http, IConfiguration config, OwnerService s, CancellationToken ct) =>
        {
            var (ok, body) = await ReadSignedAsync<VerifySubscriptionCallback>(http, config["Finance:Digest:ConnectorSecret"], ct);
            return ok ? (await s.VerifyAsync(body!, ct)).ToHttp(_ => Results.NoContent()) : Results.Unauthorized();
        }).AllowAnonymous();
    }

    /// <summary>Reads a JSON body and verifies its HMAC-SHA256 signature (hex) in constant time. No secret configured = always refused.</summary>
    private static async Task<(bool Ok, T? Body)> ReadSignedAsync<T>(HttpRequest http, string? secret, CancellationToken ct)
        where T : class
    {
        using var reader = new StreamReader(http.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrEmpty(secret) || secret.Length < 16)
        {
            return (false, null);
        }

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(raw)));
        var provided = http.Headers[SignatureHeader].ToString().Trim().ToLowerInvariant();
        if (provided.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(provided), Encoding.ASCII.GetBytes(expected)))
        {
            return (false, null);
        }

        try
        {
            return (true, JsonSerializer.Deserialize<T>(raw, Json));
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }
}

/// <summary>Small helper so the invoice-detail route accepts either billing or finance readers.</summary>
public sealed class RequestContextAccessor(Serk.Clinic.Platform.Context.RequestContext context)
{
    public bool CanReadInvoices => context.Actor is { } a && (a.Has(Capabilities.InvoiceWrite) || a.Has(Capabilities.FinanceRead));
}
