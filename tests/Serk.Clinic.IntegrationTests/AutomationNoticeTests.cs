using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Care.Application;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

/// <summary>Automation settings, recalls and the staff notice board.</summary>
[Collection(ApiCollection.Name)]
public class AutomationNoticeTests(SerkApiFixture f)
{
    private CareAutomation Automation => f.Factory.Services.GetRequiredService<CareAutomation>();

    private async Task<Guid> ConsentingPatientAsync(ApiClient reception, bool recalls = true)
    {
        var pid = await Scenario.RegisterPatientAsync(reception, given: Scenario.UniqueName("Auto"));
        var get = await reception.GetAsync($"/api/v1/care/patients/{pid}/contact");
        var put = await reception.SendAsync(HttpMethod.Put, $"/api/v1/care/patients/{pid}/contact",
            new { smsPhone = "09" + Random.Shared.Next(10_000_000, 99_999_999), preferredChannel = "Sms", language = "en", consentReminders = true, consentTips = false, consentRecalls = recalls, consentMethod = "Verbal" },
            ifMatch: get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return pid;
    }

    [Fact(DisplayName = "AUTO-001 the owner adjusts automation; invalid settings are explained; only managers may change them")]
    public async Task Settings()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var get = await owner.GetAsync("/api/v1/care/automation");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var s = await ApiClient.Body(get);
        Assert.Equal(24, s.GetProperty("reminderLeadHours").GetInt32());
        object Body(int lead, bool sameDay, int sameDayLead, string qs = "20:00") => new
        {
            confirmationsEnabled = true,
            reminderEnabled = true,
            reminderLeadHours = lead,
            sameDayReminderEnabled = sameDay,
            sameDayLeadHours = sameDayLead,
            followUpEnabled = true,
            followUpDelayHours = 24,
            rebookEnabled = true,
            quietStart = qs,
            quietEnd = "08:00",
            recallInviteLeadDays = 14,
            recallOverdueDays = 14,
            autoCheckupRecallEnabled = true,
            autoCheckupMonths = 6,
            staffNoticesEnabled = true,
            isDefault = false,
            updatedAt = (DateTimeOffset?)null,
            version = 0,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Put, "/api/v1/care/automation", Body(3, true, 4), ifMatch: get.Headers.ETag!.Tag)).StatusCode);
        var ok = await owner.SendAsync(HttpMethod.Put, "/api/v1/care/automation", Body(24, true, 2), ifMatch: get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await ApiClient.Body(ok)).GetProperty("sameDayReminderEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await owner.SendAsync(HttpMethod.Put, "/api/v1/care/automation", Body(24, false, 2), ifMatch: get.Headers.ETag!.Tag)).StatusCode);

        // Same-day reminder is now planned for a consenting patient booked tomorrow-or-later.
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/care/automation")).StatusCode);
        var pid = await ConsentingPatientAsync(reception);
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(9).AddMinutes(Random.Shared.Next(0, 60) * 5 % 60), TimeSpan.Zero);
        for (var i = 0; i < 30; i++)
        {
            var b = await reception.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerAbebe, startsAt = start.AddMinutes(i * 20), durationMinutes = 15 }, Scenario.Key());
            if (b.StatusCode == HttpStatusCode.Created)
            {
                break;
            }
        }

        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE patient_id = @p AND kind = 'SameDayReminder'", ("p", pid)));
    }

    [Fact(DisplayName = "RECALL-001 automatic 6-month check-up after a visit; booking marks it booked and attending closes it; invites only with consent")]
    public async Task Automatic_recall_lifecycle()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var pid = await ConsentingPatientAsync(reception);
        await Scenario.BookAndCheckInAsync(reception, pid);
        await Automation.RunOnceAsync(CancellationToken.None);
        var recalls = await reception.GetJsonAsync($"/api/v1/care/recalls?filter=all&patientId={pid}");
        var recall = recalls.Items().Single();
        Assert.Equal("Automatic", recall.GetProperty("source").GetString());
        Assert.Equal("CheckUp", recall.GetProperty("kind").GetString());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6), DateOnly.Parse(recall.GetProperty("dueOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture), new DateOnlyNear());
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, (await reception.GetJsonAsync($"/api/v1/care/recalls?filter=all&patientId={pid}")).GetArrayLength()); // idempotent

        // Due soon -> invitation with consent.
        var id = recall.GetProperty("id").GetGuid();
        await f.OwnerScalarAsync<int>("UPDATE care.recalls SET due_on = current_date + 5 WHERE id = @id RETURNING 1", ("id", id));
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE kind = 'Recall' AND source_id = @id", ("id", id)));

        // A patient who agreed to appointment reminders but not to check-up invitations gets no recall message.
        var remindersOnly = await ConsentingPatientAsync(reception, recalls: false);
        await Scenario.BookAndCheckInAsync(reception, remindersOnly);
        await Automation.RunOnceAsync(CancellationToken.None);
        await f.OwnerScalarAsync<int>("UPDATE care.recalls SET due_on = current_date + 5 WHERE patient_id = @p RETURNING 1", ("p", remindersOnly));
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.messages WHERE kind = 'Recall' AND patient_id = @p", ("p", remindersOnly)));

        // Booking marks it booked; attending closes it.
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(4).AddHours(6), TimeSpan.Zero);
        Guid appt = Guid.Empty;
        for (var i = 0; i < 40 && appt == Guid.Empty; i++)
        {
            var b = await reception.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerHana, startsAt = start.AddMinutes(i * 20), durationMinutes = 15 }, Scenario.Key());
            if (b.StatusCode == HttpStatusCode.Created)
            {
                appt = (await ApiClient.Body(b)).GetProperty("id").GetGuid();
            }
        }

        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Booked", await f.OwnerScalarAsync<string>("SELECT status FROM care.recalls WHERE id = @id", ("id", id)));
        await f.OwnerScalarAsync<int>("UPDATE scheduling.appointments SET status = 'CheckedIn', checked_in_at = now() WHERE id = @a RETURNING 1", ("a", appt));
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Done", await f.OwnerScalarAsync<string>("SELECT status FROM care.recalls WHERE id = @id", ("id", id)));
    }

    [Fact(DisplayName = "RECALL-002 clinician sets a review; overdue recalls become a care task and a staff notice; closing needs a reason")]
    public async Task Manual_recall_and_escalation()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(reception, given: Scenario.UniqueName("Recall"));
        using var dentist = await f.LoginAsync(DemoData.Dentist);
        var created = await dentist.SendAsync(HttpMethod.Post, "/api/v1/care/recalls", new { patientId = pid, kind = "Review", inWeeks = 1, note = "Post-extraction check" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiClient.Body(created)).GetProperty("id").GetGuid();
        Assert.Equal("Clinician", await f.OwnerScalarAsync<string>("SELECT source FROM care.recalls WHERE id = @id", ("id", id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await dentist.SendAsync(HttpMethod.Post, "/api/v1/care/recalls", new { patientId = pid, kind = "Review", dueOn = "2020-01-01" })).StatusCode);

        await f.OwnerScalarAsync<int>("UPDATE care.recalls SET due_on = current_date - 30 WHERE id = @id RETURNING 1", ("id", id));
        // Regression: a clinic that has taken a backup must still get automatic notices (a timestamp cast once broke the whole step).
        await f.OwnerScalarAsync<int>(
            "INSERT INTO platform.backup_log (id, tenant_id, user_id, action, size_bytes, table_count, row_count, created_at, correlation_id) " +
            "SELECT gen_random_uuid(), @t, user_id, 'Created', 1, 1, 1, now(), 'test' FROM identity.affiliations WHERE tenant_id = @t LIMIT 1 RETURNING 1", ("t", DemoData.TenantA));
        await Automation.RunOnceAsync(CancellationToken.None);
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM care.tasks WHERE patient_id = @p AND source = 'Recall'", ("p", pid)));
        var notices = await reception.GetJsonAsync("/api/v1/notices");
        Assert.Contains(notices.Items(), n => n.GetProperty("title").GetString() == "Recalls to book" && n.GetProperty("source").GetString() == "System");
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM comms.notices WHERE system_key LIKE 'recalls:%' AND status = 'Active'"));

        Assert.Equal(HttpStatusCode.BadRequest, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/recalls/{id}/close", new { status = "Cancelled" })).StatusCode);
        // Another branch's recall is invisible (404), not "already closed".
        await f.OwnerScalarAsync<int>("UPDATE care.recalls SET facility_id = @f WHERE id = @id RETURNING 1", ("f", DemoData.FacilityAPiassa), ("id", id));
        Assert.Equal(HttpStatusCode.NotFound, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/recalls/{id}/close", new { status = "Cancelled", reason = "Wrong branch" })).StatusCode);
        await f.OwnerScalarAsync<int>("UPDATE care.recalls SET facility_id = @f WHERE id = @id RETURNING 1", ("f", DemoData.FacilityABole), ("id", id));
        Assert.Equal(HttpStatusCode.NoContent, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/care/recalls/{id}/close", new { status = "Cancelled", reason = "Moved to another city" })).StatusCode);

        // Once resolved the automatic notice is taken down; when the problem returns a fresh notice is posted again.
        await f.OwnerScalarAsync<long>("WITH u AS (UPDATE care.recalls SET status = 'Cancelled', close_reason = 'test reset' WHERE status = 'Due' AND due_on < current_date RETURNING 1) SELECT count(*) FROM u");
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM comms.notices WHERE system_key LIKE 'recalls:%' AND status = 'Active'"));
        var again = await dentist.SendAsync(HttpMethod.Post, "/api/v1/care/recalls", new { patientId = pid, kind = "CheckUp", inWeeks = 1 });
        var againId = (await ApiClient.Body(again)).GetProperty("id").GetGuid();
        await f.OwnerScalarAsync<int>("UPDATE care.recalls SET due_on = current_date - 30 WHERE id = @id RETURNING 1", ("id", againId));
        await Automation.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM comms.notices WHERE system_key LIKE 'recalls:%' AND status = 'Active'"));
    }

    [Fact(DisplayName = "NOTICE-001 notice board: audiences, priorities, acknowledgements and receipts; patient identifiers refused")]
    public async Task Notice_board()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var title = Scenario.UniqueName("New sterilisation protocol ");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/notices", new { title = "Call back", body = "Please call MRN-000123 today", priority = "Normal" })).StatusCode);
        var created = await owner.SendAsync(HttpMethod.Post, "/api/v1/notices", new { title, body = "From Monday all instruments follow the new autoclave log. Read the folder at reception.", priority = "Important", requiresAck = true, pinned = true, audienceRoles = new[] { "Receptionist", "Dentist" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiClient.Body(created)).GetProperty("id").GetGuid();
        var financeOnly = await owner.SendAsync(HttpMethod.Post, "/api/v1/notices", new { title = Scenario.UniqueName("Finance meeting "), body = "Monthly review on Friday.", priority = "Normal", audienceRoles = new[] { "FinanceSupervisor" } });
        var financeId = (await ApiClient.Body(financeOnly)).GetProperty("id").GetGuid();
        // The author keeps seeing a notice addressed to other roles, so they can follow and take it down.
        Assert.Contains((await owner.GetJsonAsync("/api/v1/notices")).Items(), n => n.GetProperty("id").GetGuid() == financeId);

        using var reception = await f.LoginAsync(DemoData.Reception);
        var unreadBefore = (await reception.GetJsonAsync("/api/v1/notices/unread-count")).GetProperty("count").GetInt32();
        var list = await reception.GetJsonAsync("/api/v1/notices");
        Assert.Contains(list.Items(), n => n.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain(list.Items(), n => n.GetProperty("id").GetGuid() == financeId);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/notices/{financeId}/acknowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/notices/{id}/acknowledge")).StatusCode);
        Assert.Equal(unreadBefore - 1, (await reception.GetJsonAsync("/api/v1/notices/unread-count")).GetProperty("count").GetInt32());

        // Only managers post urgent/pinned/must-acknowledge notices.
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(HttpMethod.Post, "/api/v1/notices", new { title = "Urgent", body = "x", priority = "Urgent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await reception.SendAsync(HttpMethod.Post, "/api/v1/notices", new { title = "Printer paper", body = "Thermal rolls are in cupboard 3.", priority = "Normal" })).StatusCode);

        var receipts = await owner.GetJsonAsync($"/api/v1/notices/{id}/receipts");
        Assert.Contains(receipts.GetProperty("receipts").Items(), r => r.GetProperty("acknowledgedAt").ValueKind != System.Text.Json.JsonValueKind.Null);
        Assert.True(receipts.GetProperty("audienceSize").GetInt32() >= 2);
        Assert.Contains(receipts.GetProperty("notYetAcknowledged").Items(), n => n.GetString()!.Contains("Liya", StringComparison.Ordinal) || n.GetString()!.Length > 0);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.GetAsync($"/api/v1/notices/{id}/receipts")).StatusCode);

        // Managers cannot read receipts of, or take down, another branch's notice.
        await f.OwnerScalarAsync<int>("UPDATE comms.notices SET facility_id = @f WHERE id = @id RETURNING 1", ("f", DemoData.FacilityAPiassa), ("id", financeId));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/notices/{financeId}/receipts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(HttpMethod.Post, $"/api/v1/notices/{financeId}/archive")).StatusCode);

        using var otherOrg = await f.LoginAsync(DemoData.ReceptionB);
        Assert.DoesNotContain((await otherOrg.GetJsonAsync("/api/v1/notices")).Items(), n => n.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Post, $"/api/v1/notices/{id}/archive")).StatusCode);
        Assert.DoesNotContain((await reception.GetJsonAsync("/api/v1/notices")).Items(), n => n.GetProperty("id").GetGuid() == id);
    }

    private sealed class DateOnlyNear : IEqualityComparer<DateOnly>
    {
        public bool Equals(DateOnly x, DateOnly y) => Math.Abs(x.DayNumber - y.DayNumber) <= 1;

        public int GetHashCode(DateOnly obj) => 0;
    }
}
