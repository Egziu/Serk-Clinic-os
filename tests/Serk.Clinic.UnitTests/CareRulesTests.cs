using Serk.Clinic.Care.Domain;

namespace Serk.Clinic.UnitTests;

public class CareRulesTests
{
    private static readonly TimeZoneInfo Addis = TimeZoneInfo.FindSystemTimeZoneById("Africa/Addis_Ababa");

    [Fact]
    public void Templates_render_only_allowed_values_and_count_sms_parts()
    {
        var text = Templates.Render(Templates.Default(MessageKinds.Reminder, "en"),
            new TemplateValues("Bole Smile", "+251115550100", "Selam", new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(3)), "Dr. Abebe"));
        Assert.Contains("Bole Smile", text, StringComparison.Ordinal);
        Assert.Contains("09:30", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.Equal(1, Templates.SmsSegments(new string('a', 160)));
        Assert.Equal(2, Templates.SmsSegments(new string('a', 161)));
        Assert.Equal(2, Templates.SmsSegments(new string('ሰ', 71))); // Ge'ez script uses 70-character parts
    }

    [Theory]
    [InlineData("Hello {first_name}, your result is {diagnosis}.")]
    [InlineData("Dear {first_name} {reason}")]
    [InlineData("short")]
    public void Templates_reject_unknown_placeholders_and_bad_lengths(string body) => Assert.False(Templates.Validate(body).IsSuccess);

    [Fact]
    public void Every_default_template_uses_only_allowed_placeholders()
    {
        foreach (var kind in MessageKinds.Templated)
        {
            foreach (var lang in new[] { "en", "am", "ti" })
            {
                Assert.True(Templates.Validate(Templates.Default(kind, lang)).IsSuccess, $"{kind}/{lang}");
            }
        }
    }

    [Theory]
    [InlineData(21, 0, 8, 0, 1)]   // 21:00 -> next day 08:00
    [InlineData(6, 30, 8, 0, 0)]   // 06:30 -> same day 08:00
    [InlineData(10, 15, 10, 15, 0)] // daytime unchanged
    public void Messages_never_go_out_at_night(int h, int m, int eh, int em, int dayShift)
    {
        var local = new DateTimeOffset(2026, 11, 16, h, m, 0, TimeSpan.FromHours(3));
        var result = TimeZoneInfo.ConvertTime(CareSchedule.RespectQuietHours(local.ToUniversalTime(), Addis), Addis);
        Assert.Equal((eh, em, 16 + dayShift), (result.Hour, result.Minute, result.Day));
    }

    [Fact]
    public void Schedule_rules()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(now, CareSchedule.ConfirmationDue(now, now.AddMinutes(-5), now.AddDays(3), "Booked"));
        Assert.Null(CareSchedule.ConfirmationDue(now, now.AddDays(-5), now.AddDays(3), "Booked")); // booked long ago
        Assert.Equal(now.AddDays(2), CareSchedule.ReminderDue(now, now.AddDays(-1), now.AddDays(3), "Booked"));
        Assert.Null(CareSchedule.ReminderDue(now, now.AddHours(-1), now.AddHours(20), "Booked")); // booked inside the 24 h window
        Assert.Null(CareSchedule.ReminderDue(now, now.AddDays(-1), now.AddDays(3), "Cancelled"));
        Assert.Equal(now.AddHours(-2).AddHours(24), CareSchedule.FollowUpDue(now, now.AddHours(-2), "CheckedIn"));
        Assert.Null(CareSchedule.FollowUpDue(now, now.AddDays(-4), "CheckedIn"));
        Assert.Equal(now, CareSchedule.RebookDue(now, now.AddHours(-5), "NoShow"));
    }

    [Theory]
    [InlineData("5", 5, false, false)]
    [InlineData(" 4/5 ", 4, false, false)]
    [InlineData("2", 2, false, true)]
    [InlineData("3 but there is some swelling", null, false, true)]
    [InlineData("ህመም አለኝ", null, false, true)]
    [InlineData("STOP", null, true, false)]
    [InlineData("ቁም", null, true, false)]
    [InlineData("Thank you, all fine!", null, false, true)] // free text is always read by a person
    [InlineData("Stop!", null, true, false)]
    [InlineData("please stop", null, true, false)]
    [InlineData("STOP.", null, true, false)]
    [InlineData("stop the bleeding please", null, false, true)] // a worrying word wins over an opt-out word
    [InlineData("እየደማሁ ነው", null, false, true)]
    public void Replies_are_triaged_cautiously(string text, int? rating, bool optOut, bool attention)
    {
        var t = ReplyRules.Read(text);
        Assert.Equal((rating, optOut, attention), (t.Rating, t.OptOut, t.NeedsAttention));
    }

    [Fact]
    public void Social_and_phone_helpers()
    {
        Assert.Equal("+251911234567", SocialRules.NormalisePhone("0911 23 45 67"));
        Assert.Equal("+251711234567", SocialRules.NormalisePhone("0711234567"));
        Assert.Equal("+251911234567", SocialRules.NormalisePhone("+251 911-234-567"));
        Assert.Equal(0.05, SocialRules.EngagementRate(50, 1000));
        Assert.Null(SocialRules.EngagementRate(5, 0));
        Assert.Equal(0.1, SocialRules.Growth(1000, 1100));
        Assert.Null(SocialRules.Growth(null, 10));
        Assert.Equal("Selam", SocialRules.FirstName("Selam Tesfaye Bekele"));
    }
}

public class AutomationRulesTests
{
    private static readonly TimeZoneInfo Addis = TimeZoneInfo.FindSystemTimeZoneById("Africa/Addis_Ababa");

    [Fact]
    public void Default_settings_are_valid_and_bad_ones_are_explained()
    {
        Assert.True(Serk.Clinic.Care.Domain.AutomationSettings.Default.Validate().IsSuccess);
        var bad = Serk.Clinic.Care.Domain.AutomationSettings.Default with { ReminderLeadHours = 100, SameDayReminderEnabled = true, SameDayLeadHours = 6, QuietStart = new TimeOnly(9, 0), QuietEnd = new TimeOnly(9, 0) };
        var r = bad.Validate();
        Assert.False(r.IsSuccess);
        Assert.True(r.Error!.Fields!.Keys.ToHashSet().IsSupersetOf(["reminderLeadHours", "quietStart"]));
        var order = Serk.Clinic.Care.Domain.AutomationSettings.Default with { ReminderLeadHours = 3, SameDayReminderEnabled = true, SameDayLeadHours = 4 };
        Assert.True(order.Validate().Error!.Fields!.ContainsKey("sameDayLeadHours"));
    }

    [Theory]
    [InlineData(13, 30, 12, 0, 14, 0, 14, 0)]   // midday quiet window (lunch) -> moved to its end
    [InlineData(11, 0, 12, 0, 14, 0, 11, 0)]    // outside it -> unchanged
    [InlineData(22, 0, 21, 0, 7, 0, 7, 0)]      // window crossing midnight
    public void Quiet_window_may_be_any_period(int h, int m, int qsh, int qsm, int qeh, int qem, int eh, int em)
    {
        var local = new DateTimeOffset(2026, 11, 16, h, m, 0, TimeSpan.FromHours(3));
        var moved = TimeZoneInfo.ConvertTime(CareSchedule.RespectQuietHours(local.ToUniversalTime(), Addis, new TimeOnly(qsh, qsm), new TimeOnly(qeh, qem)), Addis);
        Assert.Equal((eh, em), (moved.Hour, moved.Minute));
    }

    [Fact]
    public void Same_day_and_configurable_reminders()
    {
        var now = new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero);
        var start = now.AddHours(6);
        Assert.Equal(start.AddHours(-2), CareSchedule.SameDayDue(now, now.AddDays(-2), start, "Booked", 2));
        Assert.Null(CareSchedule.SameDayDue(now, start.AddHours(-1), start, "Booked", 2)); // booked after the reminder time
        Assert.Equal(start.AddDays(2).AddHours(-48), CareSchedule.ReminderDue(now, now.AddDays(-5), start.AddDays(2), "Booked", 48));
        Assert.Equal(now.AddHours(-1).AddHours(6), CareSchedule.FollowUpDue(now, now.AddHours(-1), "CheckedIn", 6));
    }

    [Fact]
    public void Recall_rules()
    {
        var today = new DateOnly(2026, 11, 16);
        Assert.True(RecallRules.InviteDue(today, today.AddDays(14), 14));
        Assert.False(RecallRules.InviteDue(today, today.AddDays(15), 14));
        Assert.True(RecallRules.Overdue(today, today.AddDays(-15), 14));
        Assert.False(RecallRules.Overdue(today, today.AddDays(-14), 14));
        Assert.Equal(new DateOnly(2027, 5, 16), RecallRules.CheckupDate(today, 6));
        Assert.True(RecallRules.BookingMatches(today.AddMonths(6), today.AddMonths(5)));
        Assert.False(RecallRules.BookingMatches(today.AddMonths(6), today.AddDays(5)));
    }

    [Theory]
    [InlineData("Please call MRN-000123 back", true)]
    [InlineData("Her number is 0911 23 45 67", true)]
    [InlineData("Call +251 911 234 567", true)]
    [InlineData("Staff meeting Friday 08:00 in room 2", false)]
    [InlineData("New sterilisation protocol from 1 Dec", false)]
    public void Notices_refuse_obvious_patient_identifiers(string text, bool refused) =>
        Assert.Equal(refused, Serk.Clinic.Care.Application.NoticeService.LooksLikePatientData(text));
}
