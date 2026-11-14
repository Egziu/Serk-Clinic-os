using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serk.Clinic.Finance.Api;
using Serk.Clinic.Finance.Application;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;
using Serk.Clinic.Platform.Outbox;

namespace Serk.Clinic.IntegrationTests;

/// <summary>
/// Finance expansion (SRS v2): money path, cashier control, daily close and owner digests. Every test builds its own
/// organisation, staff and fictional patient through the product's own APIs, so business days never collide between tests.
/// </summary>
[Collection(ApiCollection.Name)]
public class FinanceTests(SerkApiFixture f)
{
    private const string Temp = "Temporary-Pass-2026!";
    private const string Pass = "Own-Finance-Pass-91";

    private sealed record World(Guid Tenant, Guid Facility, ApiClient Cashier, ApiClient Finance, ApiClient Owner, Guid PatientId, string PatientGiven) : IDisposable
    {
        public void Dispose()
        {
            Cashier.Dispose();
            Finance.Dispose();
            Owner.Dispose();
        }
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode == HttpStatusCode.NoContent ? default : await ApiClient.Body(r);
    }

    private static async Task<string> Code(HttpResponseMessage r) => (await ApiClient.Body(r)).GetProperty("code").GetString()!;

    private async Task<World> NewWorldAsync()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var org = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/organisations",
            new { name = Scenario.UniqueName("Synthetic Finance Clinic "), facilityCode = "MAIN", facilityName = "Finance test branch (synthetic)" }), HttpStatusCode.Created);
        var tenant = org.GetProperty("id").GetGuid();
        var facility = org.GetProperty("facilities")[0].GetProperty("id").GetGuid();
        async Task<ApiClient> StaffAsync(params string[] roles)
        {
            var username = Scenario.UniqueName("fin.");
            var staff = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/staff", new { username, displayName = "Synthetic " + username, temporaryPassword = Temp }), HttpStatusCode.Created);
            await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{staff.GetProperty("summary").GetProperty("id").GetGuid()}/assignments", new { facilityId = facility, roles }), HttpStatusCode.Created);
            var c = f.Client();
            await c.LoginAsync(username, Temp);
            await Ok(await c.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = Temp, newPassword = Pass }));
            return c;
        }

        var cashier = await StaffAsync("Cashier", "Receptionist");
        var finance = await StaffAsync("FinanceSupervisor");
        var owner = await StaffAsync("Owner");
        foreach (var (code, name, price) in new[] { ("CROWN", "Porcelain crown", 12000m), ("EXAM", "Dental examination", 400m), ("FILL", "Composite filling", 1800m) })
        {
            await Ok(await finance.SendAsync(HttpMethod.Post, "/api/v1/billing/services", new { code, name, unitPrice = price }));
        }

        var given = Scenario.UniqueName("Finpt");
        var patient = await Scenario.RegisterPatientAsync(cashier, given);
        return new World(tenant, facility, cashier, finance, owner, patient, given);
    }

    private static async Task<JsonElement> IssuedInvoiceAsync(World w, string code = "EXAM", int quantity = 1)
    {
        var draft = await Ok(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/billing/invoices", new { patientId = w.PatientId, lines = new[] { new { serviceCode = code, quantity } }, discountAmount = 0 }), HttpStatusCode.Created);
        return await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{draft.GetProperty("id").GetGuid()}/issue"));
    }

    private async Task<HttpResponseMessage> SignedPostAsync(string url, object body, string secret, bool tamper = false)
    {
        var raw = JsonSerializer.Serialize(body, ApiClient.Json);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(raw)));
        using var anon = f.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(tamper ? raw.Replace("confirmed", "Confirmed", StringComparison.Ordinal) + " " : raw, Encoding.UTF8, "application/json") };
        request.Headers.Add(FinanceEndpoints.SignatureHeader, signature);
        return await anon.Http.SendAsync(request);
    }

    private Task<HttpResponseMessage> ProviderConfirmAsync(World w, string reference, decimal amount, string status = "confirmed", bool tamper = false) =>
        SignedPostAsync("/api/v1/billing/payments/confirm", new { tenantId = w.Tenant, provider = "telebirr-sandbox", reference, amount, currency = "ETB", status }, SerkApiFixture.ProviderSecret, tamper);

    private async Task ProcessOutboxAsync()
    {
        using var scope = f.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);
    }

    private static async Task OpenDrawerAsync(World w, decimal openingFloat) =>
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/treasury/sessions", new { openingFloat }));

    private static async Task<JsonElement> CountDrawerAsync(World w, decimal counted, string? explanation = null)
    {
        var current = await w.Cashier.GetJsonAsync("/api/v1/treasury/sessions/current");
        return await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/treasury/sessions/{current.GetProperty("id").GetGuid()}/count",
            new { countedAmount = counted, explanation, version = current.GetProperty("version").GetInt32() }));
    }

    private static async Task<DateOnly> TodayAsync(World w) =>
        DateOnly.Parse((await w.Finance.GetJsonAsync("/api/v1/finance/daily-closes/preview")).GetProperty("businessDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact(DisplayName = "FIN-A (Scenario A) replayed provider callback: one payment, one receipt, one journal entry; full trace; forged callbacks refused")]
    public async Task Traceable_mobile_payment_is_exactly_once()
    {
        using var w = await NewWorldAsync();
        var invoice = await IssuedInvoiceAsync(w, "FILL");
        var id = invoice.GetProperty("id").GetGuid();
        Assert.StartsWith("INV-", invoice.GetProperty("serial").GetString(), StringComparison.Ordinal);
        var reference = Scenario.UniqueName("TB");
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/electronic", new { tender = "MobileMoney", provider = "telebirr-sandbox", providerReference = reference, amount = 1800m }));
        // An attempt is not a payment: nothing collected or receipted yet.
        Assert.Equal("Issued", (await w.Cashier.GetJsonAsync($"/api/v1/billing/invoices/{id}")).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPostAsync("/api/v1/billing/payments/confirm", new { tenantId = w.Tenant, provider = "telebirr-sandbox", reference, amount = 1800m, currency = "ETB", status = "confirmed" }, "wrong-secret-wrong-secret")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProviderConfirmAsync(w, reference, 1800m, tamper: true)).StatusCode);
        Assert.Equal("amount_mismatch", await Code(await ProviderConfirmAsync(w, reference, 18m)));

        var first = await Ok(await ProviderConfirmAsync(w, reference, 1800m));
        var second = await Ok(await ProviderConfirmAsync(w, reference, 1800m));
        Assert.Equal(first.GetProperty("receiptSerial").GetString(), second.GetProperty("receiptSerial").GetString());
        var paymentId = first.GetProperty("id").GetGuid();
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM finance.payments WHERE invoice_id=@i", ("i", id)));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM finance.journal_entries WHERE source_id=@p", ("p", paymentId)));
        var paid = await w.Cashier.GetJsonAsync($"/api/v1/billing/invoices/{id}");
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.Equal(0m, paid.GetProperty("balanceDue").GetDecimal());

        var trace = (await w.Finance.GetJsonAsync($"/api/v1/finance/invoices/{id}/trace")).GetProperty("steps").Items().Select(s => s.GetProperty("step").GetString()).ToList();
        Assert.Equal(["Invoice", "Journal entry", "Electronic payment", "Journal entry"], trace);
        // Finance sees the money trail but not the patient's name (minimum necessary).
        var financeView = await (await w.Finance.GetAsync($"/api/v1/billing/invoices/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(w.PatientGiven, financeView, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.GetAsync($"/api/v1/finance/invoices/{id}/trace")).StatusCode);
    }

    [Fact(DisplayName = "FIN-B (Scenario B) drawer shortage: 27,000 expected, 26,950 counted → 50 shortage needs explanation and a second person; posted to the ledger")]
    public async Task Cash_shortage_is_explained_approved_and_posted()
    {
        using var w = await NewWorldAsync();
        var invoice = await IssuedInvoiceAsync(w, "CROWN", 2); // 24,000
        var id = invoice.GetProperty("id").GetGuid();
        Assert.Equal("no_open_session", await Code(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/cash", new { amount = 24000m }, Scenario.Key())));
        await OpenDrawerAsync(w, 3000m);
        var key = Scenario.Key();
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/cash", new { amount = 24000m }, key));
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/cash", new { amount = 24000m }, key)); // idempotent replay
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM finance.payments WHERE invoice_id=@i", ("i", id)));
        Assert.Equal("not_payable", await Code(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/cash", new { amount = 1m }, Scenario.Key())));

        // Blind count: the open drawer does not reveal the expected cash to the cashier.
        var open = await w.Cashier.GetJsonAsync("/api/v1/treasury/sessions/current");
        Assert.Equal(JsonValueKind.Null, open.GetProperty("expectedAmount").ValueKind);
        var sessionId = open.GetProperty("id").GetGuid();
        Assert.Equal("explanation_required", await Code(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/treasury/sessions/{sessionId}/count", new { countedAmount = 26950m, version = open.GetProperty("version").GetInt32() })));
        var counted = await CountDrawerAsync(w, 26950m, "One 50 birr note missing after recount");
        Assert.Equal("PendingVarianceApproval", counted.GetProperty("status").GetString());
        Assert.Equal(27000m, counted.GetProperty("expectedAmount").GetDecimal());
        Assert.Equal(-50m, counted.GetProperty("variance").GetDecimal());

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/treasury/sessions/{sessionId}/approve-variance")).StatusCode);
        Assert.Equal("close_blocked", await Code(await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/finance/daily-closes", new { businessDate = await TodayAsync(w) })));
        var approved = await Ok(await w.Finance.SendAsync(HttpMethod.Post, $"/api/v1/treasury/sessions/{sessionId}/approve-variance"));
        Assert.Equal("Closed", approved.GetProperty("status").GetString());
        Assert.Equal(50m, await f.OwnerScalarAsync<decimal>("SELECT sum(l.debit) FROM finance.journal_lines l JOIN finance.journal_entries e ON e.id=l.entry_id WHERE e.source_id=@s AND e.purpose='CashShortage'", ("s", sessionId)));

        var preview = await w.Finance.GetJsonAsync("/api/v1/finance/daily-closes/preview");
        var drawer = preview.GetProperty("snapshot").GetProperty("cashDrawer");
        Assert.Equal((27000m, 26950m, -50m, true), (drawer.GetProperty("expected").GetDecimal(), drawer.GetProperty("counted").GetDecimal(), drawer.GetProperty("variance").GetDecimal(), drawer.GetProperty("varianceApproved").GetBoolean()));
        Assert.Contains(preview.GetProperty("snapshot").GetProperty("exceptions").Items(), e => e.GetProperty("code").GetString() == "CASH_SHORTAGE" && e.GetProperty("amount").GetDecimal() == 50m);
    }

    [Fact(DisplayName = "FIN-C (Scenario C) a verified payment without a journal entry blocks the close and is named; nothing is sent")]
    public async Task Missing_journal_blocks_close()
    {
        using var w = await NewWorldAsync();
        var invoice = await IssuedInvoiceAsync(w);
        var today = await TodayAsync(w);
        // Simulate corruption: a verified payment inserted behind the application's back (no ledger posting).
        await f.OwnerScalarAsync<int>("""
            INSERT INTO finance.payments (id, tenant_id, facility_id, invoice_id, tender, provider, provider_reference, amount, currency, status, receipt_serial, created_by, created_at, verified_at, business_date)
            VALUES (gen_random_uuid(), @t, @f, @i, 'Card', 'rogue', 'ROGUE-0001', 100, 'ETB', 'Verified', 'RCT-ROGUE', @u, now(), now(), @d) RETURNING 1
            """, ("t", w.Tenant), ("f", w.Facility), ("i", invoice.GetProperty("id").GetGuid()), ("u", DemoData.UserId(DemoData.SuperAdmin)), ("d", today));
        var blocked = await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/finance/daily-closes", new { businessDate = today });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        var body = await ApiClient.Body(blocked);
        Assert.Equal("close_blocked", body.GetProperty("code").GetString());
        Assert.Contains("RCT-ROGUE", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("CLOSE-12", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM finance.daily_closes WHERE tenant_id=@t", ("t", w.Tenant)));
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM finance.delivery_attempts WHERE tenant_id=@t", ("t", w.Tenant)));
    }

    [Fact(DisplayName = "FIN-CLOSE day close: maker-checker, exact figures, immutable snapshot, one PHI-free digest per verified recipient, later postings go to the next day")]
    public async Task Daily_close_lock_and_owner_digest()
    {
        using var w = await NewWorldAsync();
        var channel = f.Factory.Services.GetRequiredService<SandboxDigestChannel>();
        // Owner binds a private Telegram chat through the challenge.
        var start = await Ok(await w.Owner.SendAsync(HttpMethod.Post, "/api/v1/owner/subscriptions", new { channel = "Telegram" }));
        var code = start.GetProperty("challengeCode").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = code, address = "chat-778899", chatType = "private" }, SerkApiFixture.ConnectorSecret)).StatusCode);

        await OpenDrawerAsync(w, 1000m);
        var cashInvoice = await IssuedInvoiceAsync(w, "EXAM"); // 400 cash
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{cashInvoice.GetProperty("id").GetGuid()}/payments/cash", new { amount = 400m }, Scenario.Key()));
        var mobileInvoice = await IssuedInvoiceAsync(w, "FILL"); // 1,800 mobile
        var reference = Scenario.UniqueName("TB");
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{mobileInvoice.GetProperty("id").GetGuid()}/payments/electronic", new { tender = "MobileMoney", provider = "telebirr-sandbox", providerReference = reference, amount = 1800m }));
        await Ok(await ProviderConfirmAsync(w, reference, 1800m));
        await IssuedInvoiceAsync(w, "CROWN"); // 12,000 unpaid → receivable
        await CountDrawerAsync(w, 1400m);

        var today = await TodayAsync(w);
        var submitted = await Ok(await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/finance/daily-closes", new { businessDate = today, comment = "Synthetic close" }));
        var closeId = submitted.GetProperty("id").GetGuid();
        Assert.Equal("Submitted", submitted.GetProperty("status").GetString());
        Assert.False(submitted.GetProperty("fullyReconciled").GetBoolean()); // unsettled provider money → conditional
        Assert.Equal("self_approval", await Code(await w.Finance.SendAsync(HttpMethod.Post, $"/api/v1/finance/daily-closes/{closeId}/approve")));
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/finance/daily-closes/{closeId}/approve")).StatusCode);
        var locked = await Ok(await w.Owner.SendAsync(HttpMethod.Post, $"/api/v1/finance/daily-closes/{closeId}/approve"));
        Assert.Equal("Locked", locked.GetProperty("status").GetString());

        var s = locked.GetProperty("snapshot");
        Assert.Equal(14200m, s.GetProperty("issuedServiceCharges").GetDecimal());
        Assert.Equal(14200m, s.GetProperty("postedServiceRevenue").GetDecimal());
        Assert.Equal((400m, 1800m, 2200m), (s.GetProperty("verifiedCollections").GetProperty("cash").GetDecimal(), s.GetProperty("verifiedCollections").GetProperty("electronic").GetDecimal(), s.GetProperty("verifiedCollections").GetProperty("total").GetDecimal()));
        Assert.Equal(12000m, s.GetProperty("outstandingAccountsReceivable").GetDecimal());
        Assert.Equal(1800m, s.GetProperty("unsettledProviderClearing").GetDecimal());
        Assert.Equal(s.GetProperty("journalDebits").GetDecimal(), s.GetProperty("journalCredits").GetDecimal());
        Assert.Equal(64, locked.GetProperty("snapshotSha256").GetString()!.Length);

        // Immutable even for the database owner; journal lines too.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE finance.daily_closes SET fully_reconciled = true WHERE id=@c RETURNING 1", ("c", closeId)));
        Assert.Contains("serk_immutable", ex.MessageText, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE finance.journal_lines SET debit = debit + 1 WHERE tenant_id=@t RETURNING 1", ("t", w.Tenant)));

        // Digest: exactly one, provider-accepted, aggregate only (no patient name), not duplicated by redelivery.
        var before = channel.Sent.Count(m => m.Address == "chat-778899");
        await ProcessOutboxAsync();
        await ProcessOutboxAsync();
        var sent = channel.Sent.Where(m => m.Address == "chat-778899").ToList();
        Assert.Equal(before + 1, sent.Count);
        Assert.Contains("VERIFIED PATIENT COLLECTIONS", sent[^1].Text, StringComparison.Ordinal);
        Assert.Contains("2,200.00", sent[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(w.PatientGiven, sent[^1].Text, StringComparison.Ordinal);
        var delivery = Assert.Single((await w.Owner.GetJsonAsync("/api/v1/owner/deliveries")).Items());
        Assert.Equal("ProviderAccepted", delivery.GetProperty("status").GetString());
        Assert.Single((await w.Owner.GetJsonAsync("/api/v1/owner/reports/daily")).Items());
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.GetAsync("/api/v1/owner/reports/daily")).StatusCode);

        // The locked day is never written to again: a new invoice lands on the next business day.
        var late = await IssuedInvoiceAsync(w, "EXAM");
        Assert.Equal(today.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), late.GetProperty("businessDate").GetString());
        Assert.Equal("close_exists", await Code(await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/finance/daily-closes", new { businessDate = today })));
    }

    [Fact(DisplayName = "FIN-D (Scenario D) provider outage: close stays locked, failure recorded, retry delivers once")]
    public async Task Delivery_outage_does_not_affect_close()
    {
        using var w = await NewWorldAsync();
        var channel = f.Factory.Services.GetRequiredService<SandboxDigestChannel>();
        var start = await Ok(await w.Owner.SendAsync(HttpMethod.Post, "/api/v1/owner/subscriptions", new { channel = "Email" }));
        await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = start.GetProperty("challengeCode").GetString(), address = "owner-outage@example.test" }, SerkApiFixture.ConnectorSecret);
        await IssuedInvoiceAsync(w);
        var submitted = await Ok(await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/finance/daily-closes", new { businessDate = await TodayAsync(w) }));
        var closeId = submitted.GetProperty("id").GetGuid();
        channel.Failing = true;
        try
        {
            await Ok(await w.Owner.SendAsync(HttpMethod.Post, $"/api/v1/finance/daily-closes/{closeId}/approve"));
            await ProcessOutboxAsync();
            var failed = Assert.Single((await w.Owner.GetJsonAsync("/api/v1/owner/deliveries")).Items());
            Assert.Equal("RetryPending", failed.GetProperty("status").GetString());
            Assert.Equal("HttpRequestException", failed.GetProperty("lastError").GetString());
            Assert.Equal("Locked", (await w.Finance.GetJsonAsync($"/api/v1/finance/daily-closes/{closeId}")).GetProperty("status").GetString());
            Assert.Single((await w.Owner.GetJsonAsync("/api/v1/owner/reports/daily")).Items()); // still available in the portal
            channel.Failing = false;
            var retried = await Ok(await w.Owner.SendAsync(HttpMethod.Post, $"/api/v1/owner/deliveries/{failed.GetProperty("id").GetGuid()}/retry"));
            Assert.Equal("ProviderAccepted", retried.GetProperty("status").GetString());
            Assert.Equal("already_delivered", await Code(await w.Owner.SendAsync(HttpMethod.Post, $"/api/v1/owner/deliveries/{failed.GetProperty("id").GetGuid()}/retry")));
            Assert.Equal(1, channel.Sent.Count(m => m.Address == "owner-outage@example.test"));
        }
        finally
        {
            channel.Failing = false;
        }
    }

    [Fact(DisplayName = "FIN-E (Scenario E) recipients: cashier cannot bind; forged, wrong, reused codes and group chats are refused")]
    public async Task Unauthorized_recipient_changes_are_refused()
    {
        using var w = await NewWorldAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/owner/subscriptions", new { channel = "Telegram" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/owner/subscriptions", new { channel = "Telegram" })).StatusCode);
        var start = await Ok(await w.Owner.SendAsync(HttpMethod.Post, "/api/v1/owner/subscriptions", new { channel = "Telegram" }));
        var code = start.GetProperty("challengeCode").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = code, address = "chat-1", chatType = "private" }, "not-the-connector-secret")).StatusCode);
        Assert.Equal("challenge_not_found", await Code(await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = "00000000", address = "chat-1", chatType = "private" }, SerkApiFixture.ConnectorSecret)));
        Assert.Equal("private_chat_required", await Code(await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = code, address = "group-1", chatType = "group" }, SerkApiFixture.ConnectorSecret)));
        Assert.Equal(HttpStatusCode.NoContent, (await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = code, address = "chat-554433", chatType = "private" }, SerkApiFixture.ConnectorSecret)).StatusCode);
        Assert.Equal("challenge_not_found", await Code(await SignedPostAsync("/api/v1/owner/subscriptions/verify", new { tenantId = w.Tenant, challengeCode = code, address = "chat-attacker", chatType = "private" }, SerkApiFixture.ConnectorSecret)));
        var listed = Assert.Single((await w.Owner.GetJsonAsync("/api/v1/owner/subscriptions")).Items(), x => x.GetProperty("status").GetString() == "Verified");
        Assert.Equal("*******4433", listed.GetProperty("maskedAddress").GetString());
        Assert.Equal("chat-554433", await f.OwnerScalarAsync<string>("SELECT address FROM finance.owner_subscriptions WHERE id=@i", ("i", listed.GetProperty("id").GetGuid())));
    }

    [Fact(DisplayName = "FIN-LEDGER ledger integrity in the database: unbalanced entries cannot commit; entries, issued invoices and payments are immutable; tenants isolated")]
    public async Task Ledger_integrity_and_isolation()
    {
        using var w = await NewWorldAsync();
        var invoice = await IssuedInvoiceAsync(w);
        var id = invoice.GetProperty("id").GetGuid();
        var account = await f.OwnerScalarAsync<Guid>("SELECT id FROM finance.accounts WHERE tenant_id=@t AND code='1000'", ("t", w.Tenant));
        var unbalanced = await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("""
            WITH e AS (INSERT INTO finance.journal_entries (id, tenant_id, facility_id, business_date, source_type, source_id, purpose, rule_version, currency, narrative, created_by, occurred_at, posted_at, correlation_id)
                       VALUES (gen_random_uuid(), @t, @f, current_date, 'manual', gen_random_uuid(), 'Test', 1, 'ETB', 'unbalanced', @u, now(), now(), 'test') RETURNING id)
            INSERT INTO finance.journal_lines (id, tenant_id, entry_id, account_id, debit, credit) SELECT gen_random_uuid(), @t, e.id, @a, 10, 0 FROM e RETURNING 1
            """, ("t", w.Tenant), ("f", w.Facility), ("u", DemoData.UserId(DemoData.SuperAdmin)), ("a", account)));
        Assert.Contains("serk_ledger", unbalanced.MessageText, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("DELETE FROM finance.journal_entries WHERE tenant_id=@t RETURNING 1", ("t", w.Tenant)));
        await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE finance.invoices SET total_amount = 1, gross_amount = 1 WHERE id=@i RETURNING 1", ("i", id)));
        await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE finance.invoice_lines SET quantity = 9 WHERE invoice_id=@i RETURNING 1", ("i", id)));

        var tb = await w.Finance.GetJsonAsync("/api/v1/finance/trial-balance");
        Assert.True(tb.GetProperty("balanced").GetBoolean());
        Assert.Equal(400m, tb.GetProperty("totalDebits").GetDecimal());

        // Another organisation cannot see or act on this invoice.
        using var other = await NewWorldAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Cashier.GetAsync($"/api/v1/billing/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Finance.GetAsync($"/api/v1/finance/invoices/{id}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/electronic", new { tender = "Card", provider = "x-bank", providerReference = "REF-1", amount = 1m })).StatusCode);
        Assert.Empty((await other.Finance.GetJsonAsync("/api/v1/finance/invoices")).Items());
        // Clinical staff have no finance access at all.
        using var clinician = await f.LoginAsync(DemoData.Clinician);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinician.GetAsync("/api/v1/finance/trial-balance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinician.GetAsync("/api/v1/billing/invoices")).StatusCode);
    }

    [Fact(DisplayName = "FIN-REFUND refunds and discounts need a second person; refunds stay within what was paid and reduce expected drawer cash")]
    public async Task Refund_and_discount_maker_checker()
    {
        using var w = await NewWorldAsync();
        // Discount above 10% needs approval before issue.
        var draft = await Ok(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/billing/invoices",
            new { patientId = w.PatientId, lines = new[] { new { serviceCode = "FILL", quantity = 1 } }, discountAmount = 300m, discountReason = "Staff family (synthetic)" }), HttpStatusCode.Created);
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal("PendingApproval", draft.GetProperty("status").GetString());
        Assert.Equal("approval_required", await Code(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/issue")));
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/approve-discount")).StatusCode);
        Assert.Single((await w.Finance.GetJsonAsync("/api/v1/finance/approvals")).Items(), a => a.GetProperty("kind").GetString() == "Discount");
        await Ok(await w.Finance.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/approve-discount"));
        var issued = await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/issue"));
        Assert.Equal(1500m, issued.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("discount_reason_required", await Code(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/billing/invoices",
            new { patientId = w.PatientId, lines = new[] { new { serviceCode = "FILL", quantity = 1 } }, discountAmount = 10m })));

        await OpenDrawerAsync(w, 500m);
        Assert.Equal("invalid_amount", await Code(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/cash", new { amount = 1500.01m }, Scenario.Key())));
        var paid = await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{id}/payments/cash", new { amount = 1500m }, Scenario.Key()));
        var paymentId = paid.GetProperty("payments")[0].GetProperty("id").GetGuid();

        Assert.Equal("invalid_amount", await Code(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/finance/refunds", new { paymentId, amount = 1500.01m, reason = "Too much (synthetic)" })));
        var refund = await Ok(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/finance/refunds", new { paymentId, amount = 600m, reason = "Treatment not completed (synthetic)" }));
        var refundId = refund.GetProperty("id").GetGuid();
        Assert.Equal("invalid_amount", await Code(await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/finance/refunds", new { paymentId, amount = 1000m, reason = "Second request exceeding the rest" })));
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/finance/refunds/{refundId}/approve")).StatusCode);
        var current = await w.Cashier.GetJsonAsync("/api/v1/treasury/sessions/current");
        Assert.Equal("pending_refunds", await Code(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/treasury/sessions/{current.GetProperty("id").GetGuid()}/count", new { countedAmount = 2000m, version = current.GetProperty("version").GetInt32() })));
        var done = await Ok(await w.Finance.SendAsync(HttpMethod.Post, $"/api/v1/finance/refunds/{refundId}/approve"));
        Assert.Equal("Paid", done.GetProperty("status").GetString());
        Assert.Equal("already_decided", await Code(await w.Finance.SendAsync(HttpMethod.Post, $"/api/v1/finance/refunds/{refundId}/approve")));
        // Database-level maker-checker: the requester can never be recorded as the decider.
        await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE finance.refunds SET decided_by = requested_by WHERE id=@r RETURNING 1", ("r", refundId)));

        var after = await w.Cashier.GetJsonAsync($"/api/v1/billing/invoices/{id}");
        Assert.Equal(("PartiallyPaid", 900m, 600m), (after.GetProperty("status").GetString(), after.GetProperty("paidAmount").GetDecimal(), after.GetProperty("balanceDue").GetDecimal()));
        var closed = await CountDrawerAsync(w, 1400m); // 500 float + 1,500 − 600 refund
        Assert.Equal(("Closed", 1400m, 0m), (closed.GetProperty("status").GetString(), closed.GetProperty("expectedAmount").GetDecimal(), closed.GetProperty("variance").GetDecimal()));
        Assert.True((await w.Finance.GetJsonAsync("/api/v1/finance/daily-closes/preview")).GetProperty("canSubmit").GetBoolean());
    }

    [Fact(DisplayName = "FIN-SETTLE provider settlement moves money from clearing to bank less fees; the day then reconciles fully")]
    public async Task Provider_settlement_reconciles()
    {
        using var w = await NewWorldAsync();
        var invoice = await IssuedInvoiceAsync(w, "FILL");
        var reference = Scenario.UniqueName("TB");
        await Ok(await w.Cashier.SendAsync(HttpMethod.Post, $"/api/v1/billing/invoices/{invoice.GetProperty("id").GetGuid()}/payments/electronic", new { tender = "MobileMoney", provider = "telebirr-sandbox", providerReference = reference, amount = 1800m }));
        var payment = await Ok(await ProviderConfirmAsync(w, reference, 1800m));
        Assert.False((await w.Finance.GetJsonAsync("/api/v1/finance/daily-closes/preview")).GetProperty("fullyReconciled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Cashier.SendAsync(HttpMethod.Post, "/api/v1/treasury/settlements", new { provider = "telebirr-sandbox", reference = "STMT-1", paymentIds = new[] { payment.GetProperty("id").GetGuid() }, feeAmount = 18m })).StatusCode);
        var statement = Scenario.UniqueName("STMT");
        var settled = await Ok(await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/treasury/settlements", new { provider = "telebirr-sandbox", reference = statement, paymentIds = new[] { payment.GetProperty("id").GetGuid() }, feeAmount = 18m }));
        Assert.Equal((1800m, 18m, 1782m), (settled.GetProperty("grossAmount").GetDecimal(), settled.GetProperty("feeAmount").GetDecimal(), settled.GetProperty("netAmount").GetDecimal()));
        Assert.Equal("not_settleable", await Code(await w.Finance.SendAsync(HttpMethod.Post, "/api/v1/treasury/settlements", new { provider = "telebirr-sandbox", reference = statement + "-2", paymentIds = new[] { payment.GetProperty("id").GetGuid() }, feeAmount = 0m })));
        var preview = await w.Finance.GetJsonAsync("/api/v1/finance/daily-closes/preview");
        Assert.True(preview.GetProperty("fullyReconciled").GetBoolean());
        Assert.Equal(0m, preview.GetProperty("snapshot").GetProperty("unsettledProviderClearing").GetDecimal());
        var tb = (await w.Finance.GetJsonAsync("/api/v1/finance/trial-balance")).GetProperty("lines").Items().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("balance").GetDecimal());
        Assert.Equal((1782m, 18m, 0m, 0m), (tb["1010"], tb["5910"], tb["1020"], tb["1100"]));
    }
}
