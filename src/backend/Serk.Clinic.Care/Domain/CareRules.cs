using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Domain;

public static class MessageKinds
{
    public const string Confirmation = "Confirmation";
    public const string Reminder = "Reminder";
    public const string SameDayReminder = "SameDayReminder";
    public const string Recall = "Recall";
    public const string FollowUp = "FollowUp";
    public const string Rebook = "Rebook";
    public const string Tip = "Tip";

    public static readonly IReadOnlyList<string> Templated = [Confirmation, Reminder, SameDayReminder, FollowUp, Rebook, Recall];
}

/// <summary>Values a template may use. Nothing clinical (no reason for visit, diagnosis, treatment or results).</summary>
public sealed record TemplateValues(string Clinic, string ClinicPhone, string PatientFirstName, DateTimeOffset? StartsAtLocal, string Clinician);

/// <summary>
/// Message templates. Only the placeholders below are allowed, so a template can never pull clinical data into a text
/// message. Default texts are synthetic starting points; Amharic and Tigrinya defaults need review by a native speaker.
/// </summary>
public static partial class Templates
{
    public static readonly IReadOnlyList<string> Placeholders = ["{clinic}", "{clinic_phone}", "{first_name}", "{date}", "{date_et}", "{time}", "{clinician}"];

    private static readonly Dictionary<(string Kind, string Lang), string> Defaults = new()
    {
        [(MessageKinds.Confirmation, "en")] = "Hello {first_name}, your appointment at {clinic} is booked for {date} ({date_et} E.C.) at {time} with {clinician}. To change it call {clinic_phone}. Reply STOP to opt out.",
        [(MessageKinds.Reminder, "en")] = "Reminder from {clinic}: we look forward to seeing you tomorrow, {date} at {time}. If you cannot come please call {clinic_phone}. Reply STOP to opt out.",
        [(MessageKinds.FollowUp, "en")] = "Dear {first_name}, thank you for visiting {clinic}. How are you feeling today? Reply with a number from 1 (not well) to 5 (very well), or write to us. If you feel unwell, call {clinic_phone}. Reply STOP to opt out.",
        [(MessageKinds.Rebook, "en")] = "Dear {first_name}, we missed you at {clinic} today. Your care matters to us — call {clinic_phone} or reply to book a new time. Reply STOP to opt out.",
        [(MessageKinds.SameDayReminder, "en")] = "{clinic}: see you today at {time}, {first_name}. Please arrive 10 minutes early. To change, call {clinic_phone}. Reply STOP to opt out.",
        [(MessageKinds.Recall, "en")] = "Dear {first_name}, it is time for your next visit at {clinic} (due around {date}). Call {clinic_phone} or reply to book a convenient time. Reply STOP to opt out.",
        [(MessageKinds.SameDayReminder, "am")] = "{clinic}፦ ዛሬ ከ{time} ጀምሮ እንጠብቅዎታለን፣ {first_name}። እባክዎ 10 ደቂቃ ቀደም ብለው ይምጡ። ለመቀየር {clinic_phone} ይደውሉ። መልእክት ለማቆም STOP ይመልሱ።",
        [(MessageKinds.Recall, "am")] = "ውድ {first_name}፣ በ{clinic} የሚቀጥለው ምርመራዎ ጊዜ ደርሷል ({date_et} ዓ.ም. አካባቢ)። ቀጠሮ ለመያዝ {clinic_phone} ይደውሉ ወይም ይመልሱ። መልእክት ለማቆም STOP ይመልሱ።",
        [(MessageKinds.SameDayReminder, "ti")] = "{clinic}፦ ሎሚ ሰዓት {time} ንጽበየኩም ኣለና፣ {first_name}። በጃኹም 10 ደቒቕ ቀዲምኩም ምጹ። ንምቕያር {clinic_phone} ደውሉ። መልእኽቲ ንምቁራጽ STOP ምለሱ።",
        [(MessageKinds.Recall, "ti")] = "ክቡር/ቲ {first_name}፣ ኣብ {clinic} ዝቕጽል ምርመራኹም ግዜኡ በጺሑ ኣሎ ({date_et} ዓ.ም. ኣቢሉ)። ቆጸራ ንምሓዝ {clinic_phone} ደውሉ ወይ ምለሱ። መልእኽቲ ንምቁራጽ STOP ምለሱ።",
        [(MessageKinds.Confirmation, "am")] = "ሰላም {first_name}፣ በ{clinic} ቀጠሮዎ {date_et} ዓ.ም. ({date}) ከ{time} ጀምሮ ከ{clinician} ጋር ተይዟል። ለመቀየር {clinic_phone} ይደውሉ። መልእክት ለማቆም STOP ብለው ይመልሱ።",
        [(MessageKinds.Reminder, "am")] = "ከ{clinic} ማስታወሻ፦ ነገ {date_et} ዓ.ም. ከ{time} ጀምሮ እንጠብቅዎታለን። መምጣት ካልቻሉ {clinic_phone} ይደውሉ። መልእክት ለማቆም STOP ይመልሱ።",
        [(MessageKinds.FollowUp, "am")] = "ውድ {first_name}፣ {clinic}ን ስለጎበኙ እናመሰግናለን። ዛሬ እንዴት ነዎት? ከ1 (ጥሩ አይደለሁም) እስከ 5 (በጣም ጥሩ) ቁጥር ይመልሱ ወይም ይጻፉልን። ከተሰማዎት {clinic_phone} ይደውሉ።",
        [(MessageKinds.Rebook, "am")] = "ውድ {first_name}፣ ዛሬ በ{clinic} አላገኘንዎትም። አዲስ ቀጠሮ ለመያዝ {clinic_phone} ይደውሉ ወይም ይመልሱ። መልእክት ለማቆም STOP ይመልሱ።",
        [(MessageKinds.Confirmation, "ti")] = "ሰላም {first_name}፣ ኣብ {clinic} ቆጸራኹም {date_et} ዓ.ም. ({date}) ሰዓት {time} ምስ {clinician} ተታሒዙ ኣሎ። ንምቕያር {clinic_phone} ደውሉ። መልእኽቲ ንምቁራጽ STOP ምለሱ።",
        [(MessageKinds.Reminder, "ti")] = "መዘኻኸሪ ካብ {clinic}፦ ጽባሕ {date_et} ዓ.ም. ሰዓት {time} ንጽበየኩም ኣለና። ክትመጹ እንተ ዘይክኣልኩም {clinic_phone} ደውሉ። መልእኽቲ ንምቁራጽ STOP ምለሱ።",
        [(MessageKinds.FollowUp, "ti")] = "ክቡር/ቲ {first_name}፣ ን{clinic} ስለ ዝበጻሕኩም የቐንየልና። ሎሚ ከመይ ኣለኹም? ካብ 1 (ጽቡቕ ኣይኮንኩን) ክሳብ 5 (ኣዝዩ ጽቡቕ) ቁጽሪ ምለሱ። እንተ ተሰሚዑኩም {clinic_phone} ደውሉ።",
        [(MessageKinds.Rebook, "ti")] = "ክቡር/ቲ {first_name}፣ ሎሚ ኣብ {clinic} ኣይረኸብናኹምን። ሓድሽ ቆጸራ ንምሓዝ {clinic_phone} ደውሉ ወይ ምለሱ። መልእኽቲ ንምቁራጽ STOP ምለሱ።",
    };

    public static string Default(string kind, string language) =>
        Defaults.TryGetValue((kind, language), out var t) ? t : Defaults[(kind, "en")];

    public static Result<string> Validate(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length is < 10 or > 600)
        {
            return Error.Validation("invalid_template", "Templates are 10–600 characters.", new Dictionary<string, string[]> { ["body"] = ["10–600 characters."] });
        }

        var unknown = PlaceholderPattern().Matches(text).Select(m => m.Value).Where(p => !Placeholders.Contains(p)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            return Error.Validation("unknown_placeholder", "Only these placeholders are allowed: " + string.Join(" ", Placeholders) + ". Unknown: " + string.Join(" ", unknown),
                new Dictionary<string, string[]> { ["body"] = ["Unknown placeholder: " + string.Join(" ", unknown)] });
        }

        if (text.Any(c => char.IsControl(c) && c != '\n'))
        {
            return Error.Validation("invalid_template", "Control characters are not allowed.");
        }

        return text;
    }

    public static string Render(string template, TemplateValues v)
    {
        var local = v.StartsAtLocal;
        var et = local is { } d ? EthiopianDate.FromGregorian(DateOnly.FromDateTime(d.DateTime)).ToString() : "";
        return template
            .Replace("{clinic}", v.Clinic, StringComparison.Ordinal)
            .Replace("{clinic_phone}", v.ClinicPhone, StringComparison.Ordinal)
            .Replace("{first_name}", v.PatientFirstName, StringComparison.Ordinal)
            .Replace("{date}", local?.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal)
            .Replace("{date_et}", et, StringComparison.Ordinal)
            .Replace("{time}", local?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal)
            .Replace("{clinician}", v.Clinician, StringComparison.Ordinal);
    }

    /// <summary>SMS parts the provider will bill: GSM-7 text fits 160/153 characters, anything else (e.g. Ge'ez) 70/67.</summary>
    public static int SmsSegments(string text)
    {
        var gsm = text.All(c => c < 128);
        var (single, multi) = gsm ? (160, 153) : (70, 67);
        return text.Length <= single ? 1 : (int)Math.Ceiling(text.Length / (double)multi);
    }

    [GeneratedRegex(@"\{[^{}\s]{1,40}\}")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>Owner-adjustable automation settings for one facility (defaults when never changed).</summary>
public sealed record AutomationSettings(
    bool ConfirmationsEnabled,
    bool ReminderEnabled,
    int ReminderLeadHours,
    bool SameDayReminderEnabled,
    int SameDayLeadHours,
    bool FollowUpEnabled,
    int FollowUpDelayHours,
    bool RebookEnabled,
    TimeOnly QuietStart,
    TimeOnly QuietEnd,
    int RecallInviteLeadDays,
    int RecallOverdueDays,
    bool AutoCheckupRecallEnabled,
    int AutoCheckupMonths,
    bool StaffNoticesEnabled)
{
    public static AutomationSettings Default { get; } = new(true, true, 24, false, 2, true, 24, true, new TimeOnly(20, 0), new TimeOnly(8, 0), 14, 14, true, 6, true);

    public Result<AutomationSettings> Validate()
    {
        var e = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Range(string field, int v, int min, int max)
        {
            if (v < min || v > max)
            {
                e[field] = [$"Between {min} and {max}."];
            }
        }

        Range("reminderLeadHours", ReminderLeadHours, 2, 72);
        Range("sameDayLeadHours", SameDayLeadHours, 1, 6);
        Range("followUpDelayHours", FollowUpDelayHours, 2, 96);
        Range("recallInviteLeadDays", RecallInviteLeadDays, 1, 60);
        Range("recallOverdueDays", RecallOverdueDays, 1, 90);
        Range("autoCheckupMonths", AutoCheckupMonths, 3, 24);
        if (QuietStart == QuietEnd)
        {
            e["quietStart"] = ["Quiet hours need a different start and end."];
        }

        if (SameDayReminderEnabled && ReminderEnabled && SameDayLeadHours >= ReminderLeadHours)
        {
            e["sameDayLeadHours"] = ["The same-day reminder must come after the main reminder."];
        }

        return e.Count == 0 ? this : Error.Validation("invalid_automation", "Some automation settings need attention.", e);
    }
}

/// <summary>When automated patient messages are due (facility-local time; never during quiet hours).</summary>
public static class CareSchedule
{
    public static readonly TimeOnly QuietEnd = new(8, 0);
    public static readonly TimeOnly QuietStart = new(20, 0);
    public static readonly TimeSpan ReminderLead = TimeSpan.FromHours(24);
    public static readonly TimeSpan FollowUpDelay = TimeSpan.FromHours(24);

    /// <summary>Moves a send time out of the quiet window [start, end) — which may cross midnight — to its end (facility-local).</summary>
    public static DateTimeOffset RespectQuietHours(DateTimeOffset utc, TimeZoneInfo zone, TimeOnly quietStart, TimeOnly quietEnd)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var t = TimeOnly.FromDateTime(local.DateTime);
        var crossesMidnight = quietStart > quietEnd;
        var quiet = crossesMidnight ? t >= quietStart || t < quietEnd : t >= quietStart && t < quietEnd;
        if (!quiet)
        {
            return utc;
        }

        var day = DateOnly.FromDateTime(local.DateTime);
        var next = crossesMidnight && t >= quietStart ? day.AddDays(1) : day;
        var at = next.ToDateTime(quietEnd, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(at, zone), TimeSpan.Zero);
    }

    /// <summary>Same-day reminder a few hours before; only for appointments booked before that day's reminder time.</summary>
    public static DateTimeOffset? SameDayDue(DateTimeOffset now, DateTimeOffset bookedAt, DateTimeOffset startsAt, string status, int leadHours)
    {
        var due = startsAt.AddHours(-leadHours);
        return status == "Booked" && bookedAt < due && startsAt > now.AddMinutes(30) ? due : null;
    }

    public static DateTimeOffset? ReminderDue(DateTimeOffset now, DateTimeOffset bookedAt, DateTimeOffset startsAt, string status, int leadHours)
    {
        var lead = TimeSpan.FromHours(leadHours);
        return status != "Booked" || startsAt - bookedAt < lead || startsAt < now.AddHours(Math.Min(4, leadHours)) ? null : startsAt - lead;
    }

    public static DateTimeOffset? FollowUpDue(DateTimeOffset now, DateTimeOffset? checkedInAt, string status, int delayHours) =>
        checkedInAt is { } c && status is "CheckedIn" or "Completed" && now - c < TimeSpan.FromHours(delayHours) + TimeSpan.FromDays(2) ? c.AddHours(delayHours) : null;

    /// <summary>Moves a send time out of the 20:00–08:00 quiet hours to the next 08:00 (facility-local).</summary>
    public static DateTimeOffset RespectQuietHours(DateTimeOffset utc, TimeZoneInfo zone) => RespectQuietHours(utc, zone, QuietStart, QuietEnd);

    /// <summary>Confirmation: right away for future appointments booked recently.</summary>
    public static DateTimeOffset? ConfirmationDue(DateTimeOffset now, DateTimeOffset bookedAt, DateTimeOffset startsAt, string status) =>
        status == "Booked" && startsAt > now.AddHours(2) && now - bookedAt < TimeSpan.FromDays(2) ? now : null;

    /// <summary>Reminder: 24 h before; skipped when the appointment is less than 4 h away or was booked inside the window.</summary>
    public static DateTimeOffset? ReminderDue(DateTimeOffset now, DateTimeOffset bookedAt, DateTimeOffset startsAt, string status)
    {
        if (status != "Booked" || startsAt - bookedAt < ReminderLead || startsAt < now.AddHours(4))
        {
            return null;
        }

        return startsAt - ReminderLead;
    }

    /// <summary>Follow-up: 24 h after check-in, for visits checked in within the last 3 days.</summary>
    public static DateTimeOffset? FollowUpDue(DateTimeOffset now, DateTimeOffset? checkedInAt, string status) =>
        checkedInAt is { } c && status is "CheckedIn" or "Completed" && now - c < TimeSpan.FromDays(3) ? c + FollowUpDelay : null;

    /// <summary>Re-booking invitation after a no-show (appointment within the last 3 days).</summary>
    public static DateTimeOffset? RebookDue(DateTimeOffset now, DateTimeOffset startsAt, string status) =>
        status == "NoShow" && now - startsAt < TimeSpan.FromDays(3) ? now : null;
}

/// <summary>Dental recalls: when to invite, when a recall is overdue, and the automatic check-up date.</summary>
public static class RecallRules
{
    public static readonly IReadOnlyList<string> Kinds = ["CheckUp", "Review", "Cleaning", "Treatment", "Other"];

    public static bool InviteDue(DateOnly today, DateOnly dueOn, int leadDays) => today >= dueOn.AddDays(-leadDays);

    public static bool Overdue(DateOnly today, DateOnly dueOn, int overdueDays) => today > dueOn.AddDays(overdueDays);

    public static DateOnly CheckupDate(DateOnly visit, int months) => visit.AddMonths(months);

    /// <summary>A future appointment counts as booking the recall when it is no more than 60 days before the due date.</summary>
    public static bool BookingMatches(DateOnly dueOn, DateOnly appointmentDay) => appointmentDay >= dueOn.AddDays(-60);
}

public sealed record ReplyTriage(int? Rating, bool OptOut, bool NeedsAttention, string Reason);

/// <summary>
/// Reads a patient's reply. Deliberately cautious: any worrying word or a low rating creates a task for a person to call
/// back; the system never answers medical questions itself. Word lists are a starting point for clinical review.
/// </summary>
public static partial class ReplyRules
{
    private static readonly string[] OptOutWords = ["stop", "unsubscribe", "ቁም", "አቁም", "ኣቋርጽ", "ደው"];

    private static readonly string[] ConcernWords =
    [
        "pain", "bleed", "blood", "swell", "fever", "infect", "pus", "worse", "emergency", "help", "can't breathe", "cannot breathe", "allerg", "rash", "dizzy",
        "ህመም", "ያመኛል", "ያመመኛል", "ደም", "ደማ", "ይደማ", "እየደማ", "እብጠት", "አብጧል", "ያብጣል", "ትኩሳት", "ተባብሷል", "እርዳታ", "ድንገተኛ",
        "ቃንዛ", "ረስኒ", "ሕማም", "ሓገዝ", "ይደምይ", "ደሚ", "ሕበጥ",
    ];

    public static ReplyTriage Read(string text)
    {
        var t = (text ?? "").Trim();
        var lower = t.ToLowerInvariant();
        int? rating = RatingPattern().Match(lower) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        // A worrying word always wins (e.g. "stop the bleeding" is a call-back, not an opt-out).
        var concern = ConcernWords.FirstOrDefault(w => lower.Contains(w, StringComparison.Ordinal));
        if (concern is not null)
        {
            return new ReplyTriage(rating, false, true, $"Mentions \"{concern}\" — call the patient");
        }

        // Opt-out: a short message (up to 5 words) containing a stop word, punctuation ignored ("Stop!", "please stop").
        var words = WordPattern().Matches(lower).Select(x => x.Value).ToList();
        if (words.Count is > 0 and <= 5 && words.Any(w => OptOutWords.Contains(w)))
        {
            return new ReplyTriage(null, true, false, "Asked to stop messages");
        }

        if (rating is <= 2)
        {
            return new ReplyTriage(rating, false, true, $"Rated {rating} of 5 — call the patient");
        }

        if (rating is null && t.Length > 0)
        {
            return new ReplyTriage(null, false, true, "Wrote a message — read and respond");
        }

        return new ReplyTriage(rating, false, false, rating is null ? "" : $"Rated {rating} of 5");
    }

    [GeneratedRegex(@"[\p{L}\p{M}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^\s*([1-5])\s*(?:/\s*5)?\s*[.!]?\s*$")]
    private static partial Regex RatingPattern();
}

public static class SocialRules
{
    public static readonly IReadOnlyList<string> Platforms = ["Facebook", "Instagram", "TikTok", "Telegram", "X", "LinkedIn", "YouTube", "WhatsApp"];
    public static readonly IReadOnlyList<string> PostStatuses = ["Idea", "Draft", "Scheduled", "Published", "Cancelled"];

    /// <summary>Engagement rate = engagements / reach (null when reach is 0).</summary>
    public static double? EngagementRate(int engagements, int reach) => reach > 0 ? Math.Round((double)engagements / reach, 4) : null;

    /// <summary>Follower growth between two measurements as a fraction (null without a baseline).</summary>
    public static double? Growth(int? previous, int current) => previous is > 0 ? Math.Round((current - previous.Value) / (double)previous.Value, 4) : null;

    public static string FirstName(string displayName) => (displayName ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    public static string NormalisePhone(string? phone)
    {
        var digits = new StringBuilder();
        foreach (var c in (phone ?? "").Trim())
        {
            if (char.IsDigit(c) || (c == '+' && digits.Length == 0))
            {
                digits.Append(c);
            }
        }

        var s = digits.ToString();
        // Ethiopian local mobile (09xxxxxxxx / 07xxxxxxxx) -> +2519xxxxxxxx / +2517xxxxxxxx
        return s.Length == 10 && s[0] == '0' && s[1] is '9' or '7' ? "+251" + s[1..] : s;
    }
}
