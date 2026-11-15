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
