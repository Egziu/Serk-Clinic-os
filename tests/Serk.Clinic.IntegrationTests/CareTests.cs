using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Care.Application;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

/// <summary>Care+: consent, automated follow-up, replies and care tasks, health tips (four-eyes), campaigns, social media.</summary>
[Collection(ApiCollection.Name)]
public class CareTests(SerkApiFixture f)
{
    private CareAutomation Automation => f.Factory.Services.GetRequiredService<CareAutomation>();

    private SandboxCareMessenger Sandbox => f.Factory.Services.GetRequiredService<SandboxCareMessenger>();

    private static object Contact(bool reminders, bool tips, string? method = "Verbal", string channel = "Sms", string phone = "0911223344") =>
        new { smsPhone = phone, preferredChannel = channel, language = "en", consentReminders = reminders, consentTips = tips, consentMethod = method };

    private async Task<Guid> ConsentingPatientAsync(ApiClient reception, bool reminders = true, bool tips = false, string? phone = null)
    {
        var pid = await Scenario.RegisterPatientAsync(reception, given: Scenario.UniqueName("Care"));
        var get = await reception.GetAsync($"/api/v1/care/patients/{pid}/contact");
        var put = await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact",
            Contact(reminders, tips, phone: phone ?? "09" + Random.Shared.Next(10_000_000, 99_999_999)), ifMatch: get.Headers.ETag!.Tag);
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        return pid;
    }

    private async Task<Guid> BookAsync(ApiClient reception, Guid pid, int daysAhead)
    {
        for (var i = 0; i < 40; i++)
        {
            var start = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(daysAhead).AddHours(5).AddMinutes(Random.Shared.Next(0, 96) * 5), TimeSpan.Zero);
            var r = await reception.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerHana, startsAt = start, durationMinutes = 15, reason = "SECRET-REASON-NOT-IN-TEXT" }, Scenario.Key());
            if (r.StatusCode == HttpStatusCode.Created)
            {
                return (await ApiClient.Body(r)).GetProperty("id").GetGuid();
            }
        }

        throw new InvalidOperationException("No free slot found.");
    }

    private Task MakeDueAsync(Guid patientId) =>
        f.OwnerScalarAsync<int>("UPDATE care.messages SET due_at = now() - interval '1 minute' WHERE patient_id = @p AND status = 'Scheduled' RETURNING 1", ("p", patientId));

    [Fact(DisplayName = "CARE-001 consent is recorded with method and history; channel rules enforced; only permitted roles")]
    public async Task Consent()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(reception);
        var get = await reception.GetAsync($"/api/v1/care/patients/{pid}/contact");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var etag = get.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.BadRequest, (await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact", Contact(true, false, method: null), ifMatch: etag)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact", Contact(true, false, channel: "Telegram"), ifMatch: etag)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact", Contact(true, false, phone: "12ab"), ifMatch: etag)).StatusCode);
        var ok = await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact", Contact(true, true, method: "Written"), ifMatch: etag);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ApiClient.Body(ok);
        Assert.Equal("+251911223344", body.GetProperty("smsPhone").GetString());
        Assert.Equal("Written", body.GetProperty("history")[0].GetProperty("method").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact", Contact(false, false), ifMatch: etag)).StatusCode);

        var link = await ApiClient.Body(await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/patients/{pid}/telegram-link"));
        Assert.Matches("^[A-Z2-9]{8}$", link.GetProperty("code").GetString());
        Assert.Equal(64, (await f.OwnerScalarAsync<string>("SELECT code_sha256 FROM care.telegram_links WHERE patient_id = @p", ("p", pid)))!.Length); // only the hash is stored

        using var cashier = await f.LoginAsync(DemoData.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/care/patients/{pid}/contact")).StatusCode);
        using var otherTenant = await f.LoginAsync(DemoData.ReceptionB);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync($"/api/v1/care/patients/{pid}/contact")).StatusCode);
    }

    [Fact(DisplayName = "CARE-002 booking confirmation and reminder go only to consenting patients, once, with no clinical detail; withdrawal cancels")]
    public async Task Appointment_messages()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var yes = await ConsentingPatientAsync(reception);
        var no = await Scenario.RegisterPatientAsync(reception);
        await BookAsync(reception, yes, 2);
        await BookAsync(reception, no, 2);

        await Automation.RunOnceAsync(CancellationToken.None);
        await Automation.RunOnceAsync(CancellationToken.None); // idempotent
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE patient_id = @p AND kind = 'Confirmation'", ("p", yes)));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE patient_id = @p AND kind = 'Reminder'", ("p", yes)));
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE patient_id = @p", ("p", no)));

        var before = Sandbox.Sent.Count;
        await f.OwnerScalarAsync<int>("UPDATE care.messages SET due_at = now() - interval '1 minute' WHERE patient_id = @p AND kind = 'Confirmation' RETURNING 1", ("p", yes));
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Sent", await f.OwnerScalarAsync<string>("SELECT status FROM care.messages WHERE patient_id = @p AND kind = 'Confirmation'", ("p", yes)));
        var text = Sandbox.Sent.Skip(before).Single(s => s.Channel == "Sms").Text;
        Assert.DoesNotContain("SECRET-REASON", text, StringComparison.Ordinal);
        Assert.Contains("STOP", text, StringComparison.Ordinal);

        // Withdrawing consent cancels the queued reminder.
        var get = await reception.GetAsync($"/api/v1/care/patients/{yes}/contact");
        Assert.Equal(HttpStatusCode.OK, (await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{yes}/contact", Contact(false, false), ifMatch: get.Headers.ETag!.Tag)).StatusCode);
        Assert.Equal("Cancelled", await f.OwnerScalarAsync<string>("SELECT status FROM care.messages WHERE patient_id = @p AND kind = 'Reminder'", ("p", yes)));
        Assert.Equal("StaffWithdrawal", await f.OwnerScalarAsync<string>("SELECT method FROM care.consent_events WHERE patient_id = @p ORDER BY recorded_at DESC LIMIT 1", ("p", yes)));
    }

    [Fact(DisplayName = "CARE-003 follow-up after the visit; a worrying reply creates an urgent care task that needs a written outcome; STOP opts out")]
    public async Task Follow_up_and_replies()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var pid = await ConsentingPatientAsync(reception);
        var (appt, _) = await Scenario.BookAndCheckInAsync(reception, pid);
        await f.OwnerScalarAsync<int>("UPDATE scheduling.appointments SET checked_in_at = now() - interval '25 hours' WHERE id = @a RETURNING 1", ("a", appt));
        await Automation.RunOnceAsync(CancellationToken.None);
        await MakeDueAsync(pid);
        await Automation.RunOnceAsync(CancellationToken.None);
        var followUp = await f.OwnerScalarAsync<Guid>("SELECT id FROM care.messages WHERE patient_id = @p AND kind = 'FollowUp' AND status = 'Sent'", ("p", pid));
        Assert.NotEqual(Guid.Empty, followUp);

        var reply = await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/messages/{followUp}/simulate-reply", new { text = "2 - there is swelling" });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var tasks = await reception.GetJsonAsync("/api/v1/care/tasks");
        var task = tasks.Items().Single(t => t.GetProperty("patientId").GetGuid() == pid);
        Assert.Equal("High", task.GetProperty("priority").GetString());
        Assert.Contains("swell", task.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var taskId = task.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/tasks/{taskId}/complete", new { outcome = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/tasks/{taskId}/complete", new { outcome = "Called; booked a review tomorrow 09:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/tasks/{taskId}/complete", new { outcome = "again" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/messages/{followUp}/simulate-reply", new { text = "STOP" })).StatusCode);
        Assert.False(await f.OwnerScalarAsync<bool>("SELECT consent_reminders FROM care.contact_preferences WHERE patient_id = @p", ("p", pid)));
        Assert.Equal("OptOutReply", await f.OwnerScalarAsync<string>("SELECT method FROM care.consent_events WHERE patient_id = @p ORDER BY recorded_at DESC LIMIT 1", ("p", pid)));

        // Replies are evidence: they cannot be edited.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE care.replies SET text = 'x' WHERE patient_id = @p RETURNING 1", ("p", pid)));
        Assert.Contains("serk_immutable", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CARE-004 health tips need another clinician's approval; campaigns reach only patients who agreed to tips")]
    public async Task Tips_and_campaigns()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var tipsYes = await ConsentingPatientAsync(reception, reminders: false, tips: true);
        var tipsNo = await ConsentingPatientAsync(reception, reminders: true, tips: false);

        using var author = await f.LoginAsync(DemoData.Clinician);
        var created = await author.SendAsync(HttpMethod.Post, "/api/v1/care/tips", new { title = "Brush twice a day", body = "Brush for two minutes with fluoride toothpaste, morning and night.", topic = "Oral hygiene", language = "en" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tipId = (await ApiClient.Body(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await author.SendAsync(HttpMethod.Post, $"/api/v1/care/tips/{tipId}/approve")).StatusCode); // own tip

        using var owner = await f.LoginAsync(DemoData.Owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.SendAsync(HttpMethod.Post, "/api/v1/care/campaigns", new { tipId, audience = "AllConsented" })).StatusCode); // not approved
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(HttpMethod.Post, $"/api/v1/care/tips/{tipId}/approve")).StatusCode); // owner is not a clinician

        using var approver = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityABole);
        Assert.Equal(HttpStatusCode.NoContent, (await approver.SendAsync(HttpMethod.Post, $"/api/v1/care/tips/{tipId}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await author.SendAsync(HttpMethod.Put, $"/api/v1/care/tips/{tipId}", new { title = "Changed after approval", body = "Changed text that is long enough to pass.", topic = "x1", language = "en" })).StatusCode);

        var campaign = await owner.SendAsync(HttpMethod.Post, "/api/v1/care/campaigns", new { tipId, audience = "AllConsented" });
        Assert.Equal(HttpStatusCode.Created, campaign.StatusCode);
        var campaignId = (await ApiClient.Body(campaign)).GetProperty("id").GetGuid();
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE kind = 'Tip' AND source_id = @c AND patient_id = @p", ("c", campaignId), ("p", tipsYes)));
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE kind = 'Tip' AND source_id = @c AND patient_id = @p", ("c", campaignId), ("p", tipsNo)));
        Assert.Equal("Queued", await f.OwnerScalarAsync<string>("SELECT status FROM care.campaigns WHERE id = @c", ("c", campaignId)));
    }

    [Fact(DisplayName = "CARE-006 review fixes: editor cannot approve; one failing message does not resend others; changed contact skips; campaigns stay in the facility")]
    public async Task Review_fixes()
    {
        // Four eyes also covers the last editor.
        using var author = await f.LoginAsync(DemoData.Nurse);
        var tip = (await ApiClient.Body(await author.SendAsync(HttpMethod.Post, "/api/v1/care/tips", new { title = "Floss daily", body = "Clean between your teeth once a day with floss or small brushes.", topic = "Oral hygiene", language = "en" }))).GetProperty("id").GetGuid();
        using var editor = await f.LoginAsync(DemoData.Clinician);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.SendAsync(HttpMethod.Put, $"/api/v1/care/tips/{tip}", new { title = "Floss every day", body = "Clean between your teeth once a day with floss or interdental brushes.", topic = "Oral hygiene", language = "en" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(HttpMethod.Post, $"/api/v1/care/tips/{tip}/approve")).StatusCode);
        using var other = await f.LoginAsync(DemoData.Dentist);
        Assert.Equal(HttpStatusCode.NoContent, (await other.SendAsync(HttpMethod.Post, $"/api/v1/care/tips/{tip}/approve")).StatusCode);

        // One malformed provider answer affects only its own message.
        using var reception = await f.LoginAsync(DemoData.Reception);
        var good = await ConsentingPatientAsync(reception, phone: "0911555001");
        var bad = await ConsentingPatientAsync(reception, phone: "0911555002");
        await BookAsync(reception, good, 2);
        await BookAsync(reception, bad, 2);
        await Automation.RunOnceAsync(CancellationToken.None);
        await MakeDueAsync(good);
        await MakeDueAsync(bad);
        Sandbox.FaultFor = a => a == "+251911555002" ? new InvalidDataException("provider sent HTML") : null;
        try
        {
            var before = Sandbox.Sent.Count(s => s.Address == "+251911555001");
            await Automation.RunOnceAsync(CancellationToken.None);
            await Automation.RunOnceAsync(CancellationToken.None);
            Assert.Equal(before + 2, Sandbox.Sent.Count(s => s.Address == "+251911555001")); // confirmation + reminder, each exactly once
            Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE patient_id = @p AND status = 'Sent'", ("p", bad)));
            Assert.Equal("Provider error (InvalidDataException)", await f.OwnerScalarAsync<string>("SELECT last_error FROM care.messages WHERE patient_id = @p LIMIT 1", ("p", bad)));
        }
        finally
        {
            Sandbox.FaultFor = null;
        }

        // Changed phone number: messages planned for the old number are skipped, not sent there.
        var moved = await ConsentingPatientAsync(reception, phone: "0911555003");
        await BookAsync(reception, moved, 2);
        await Automation.RunOnceAsync(CancellationToken.None);
        await f.OwnerScalarAsync<int>("UPDATE care.contact_preferences SET sms_phone = '+251911555004' WHERE patient_id = @p RETURNING 1", ("p", moved));
        await MakeDueAsync(moved);
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(Sandbox.Sent, s => s.Address == "+251911555003");
        Assert.Equal("Contact details changed since planning", await f.OwnerScalarAsync<string>("SELECT last_error FROM care.messages WHERE patient_id = @p LIMIT 1", ("p", moved)));

        // Campaigns reach only the creating facility's patients.
        var elsewhere = await ConsentingPatientAsync(reception, reminders: false, tips: true);
        await f.OwnerScalarAsync<int>("UPDATE patients.patients SET registered_at_facility_id = @f WHERE id = @p RETURNING 1", ("f", DemoData.FacilityAPiassa), ("p", elsewhere));
        using var owner = await f.LoginAsync(DemoData.Owner);
        var campaign = (await ApiClient.Body(await owner.SendAsync(HttpMethod.Post, "/api/v1/care/campaigns", new { tipId = tip, audience = "AllConsented" }))).GetProperty("id").GetGuid();
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE source_id = @c AND patient_id = @p", ("c", campaign), ("p", elsewhere)));
    }

    [Fact(DisplayName = "CARE-005 Telegram webhook is closed unless a bot is configured; templates reject clinical placeholders")]
    public async Task Webhook_and_templates()
    {
        using var anonymous = f.Client();
        var hook = await anonymous.SendAsync(HttpMethod.Post, "/api/v1/care/telegram/webhook", new { update_id = 1, message = new { chat = new { id = 1 }, text = "/start ABCDEFGH" } }, includeCsrf: false);
        Assert.Equal(HttpStatusCode.NotFound, hook.StatusCode);

        using var owner = await f.LoginAsync(DemoData.Owner);
        var templates = await owner.GetJsonAsync("/api/v1/care/templates");
        Assert.Equal(12, templates.GetArrayLength()); // 4 kinds × 3 languages
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Put, "/api/v1/care/templates/Reminder/en", new { body = "Your diagnosis {diagnosis} is ready", isEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, "/api/v1/care/templates/Reminder/en", new { body = "See you tomorrow at {time}, {first_name}! — {clinic}", isEnabled = true })).StatusCode);
        var summary = await owner.GetJsonAsync("/api/v1/care/summary");
        Assert.False(summary.GetProperty("telegramLive").GetBoolean());
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/care/templates")).StatusCode);
    }

    [Fact(DisplayName = "SOCIAL-001 social planner: patient posts need consent reference; results tracked with engagement; permissions")]
    public async Task Social()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var at = DateTimeOffset.UtcNow.AddDays(3);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/social/posts", new { platform = "Instagram", plannedAt = at, title = "Smile makeover story", status = "Draft", showsPatient = true })).StatusCode);
        var post = await owner.SendAsync(HttpMethod.Post, "/api/v1/social/posts", new { platform = "Instagram", plannedAt = at, title = "Smile makeover story", status = "Scheduled", showsPatient = true, patientConsentRef = "MC-2026-014", campaign = "Spring smiles" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Post, "/api/v1/social/metrics", new { platform = "Instagram", measuredOn = DateOnly.FromDateTime(DateTime.UtcNow), followers = 1200, reach = 4000, engagements = 200, inquiries = 6, bookings = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/social/metrics", new { platform = "Instagram", measuredOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), followers = 1 })).StatusCode);
        var summary = await owner.GetJsonAsync("/api/v1/social/summary");
        var ig = summary.GetProperty("platforms").Items().Single(p => p.GetProperty("platform").GetString() == "Instagram");
        Assert.Equal(0.05, ig.GetProperty("engagementRate").GetDouble());
        Assert.True(summary.GetProperty("plannedNext14").GetInt32() >= 1);
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/social/posts")).StatusCode);
    }
}
