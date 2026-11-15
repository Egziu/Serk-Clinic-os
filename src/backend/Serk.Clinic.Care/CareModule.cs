using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Care.Application;
using Serk.Clinic.Platform.Export;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care;

public static class CareModule
{
    public static IServiceCollection AddCareModule(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CareOptions>(config.GetSection("Care"));
        services.AddScoped<CareService>();
        services.AddScoped<SocialService>();
        services.AddScoped<RecallService>();
        services.AddScoped<NoticeService>();
        services.AddSingleton<SandboxCareMessenger>();
        // The bot token is part of the request path: this client must never log request URLs.
        services.AddHttpClient(CareChannels.TelegramClient, c => c.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers();
        services.AddSingleton<CareChannels>();
        services.AddSingleton<CareAutomation>();
        services.AddSingleton<TelegramInbound>();
        services.AddHostedService<CareAutomationService>();
        services.AddExportDatasets(CareExports.Messages, CareExports.SocialMetrics, CareExports.SocialPosts);
        return services;
    }
}

public static class CareExports
{
    public static readonly ExportDefinition Messages = new(
        "care.messages", "Care+", "Patient messages",
        "Follow-up, reminder and tip messages with status, channel and patient MRN. Identifies patients.",
        [Capabilities.CareDesk], ContainsPatientData: true, UsesDateRange: true,
        ["due_at", "kind", "channel", "status", "attempts", "sent_at", "patient_mrn", "last_error"],
        """
        SELECT m.due_at, m.kind, m.channel, m.status, m.attempts, m.sent_at, p.mrn, m.last_error
        FROM care.messages m LEFT JOIN patients.v_patient_contact p ON p.patient_id = m.patient_id
        WHERE m.tenant_id = @t AND m.facility_id = @f AND m.due_at >= @from_utc AND m.due_at < @to_utc ORDER BY m.due_at LIMIT @limit
        """);

    public static readonly ExportDefinition SocialMetrics = new(
        "social.metrics", "Marketing", "Social media results",
        "Followers, reach, engagements, inquiries and bookings per platform and day.",
        [Capabilities.SocialManage], ContainsPatientData: false, UsesDateRange: true,
        ["measured_on", "platform", "followers", "reach", "engagements", "inquiries", "bookings"],
        """
        SELECT measured_on, platform, followers, reach, engagements, inquiries, bookings FROM care.social_metrics
        WHERE tenant_id = @t AND measured_on BETWEEN @dfrom AND @dto ORDER BY measured_on, platform LIMIT @limit
        """);

    public static readonly ExportDefinition SocialPosts = new(
        "social.posts", "Marketing", "Social media plan",
        "Planned and published posts with platform, status, campaign and link.",
        [Capabilities.SocialManage], ContainsPatientData: false, UsesDateRange: true,
        ["planned_at", "platform", "status", "title", "campaign", "published_at", "published_url"],
        """
        SELECT planned_at, platform, status, title, campaign, published_at, published_url FROM care.social_posts
        WHERE tenant_id = @t AND planned_at >= @from_utc AND planned_at < @to_utc ORDER BY planned_at LIMIT @limit
        """);
}
