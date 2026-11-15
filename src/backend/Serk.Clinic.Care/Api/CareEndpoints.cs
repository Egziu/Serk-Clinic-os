using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serk.Clinic.Care.Application;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Api;

public static class CareEndpoints
{
    public static void MapCareEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/care").WithTags("Care+");
        static IResult Etag(HttpResponse response, ContactDto c)
        {
            response.Headers.ETag = ResultHttp.ETag(c.Version);
            return Results.Ok(c);
        }

        // ---- contact & consent (reception, nurses, clinicians)
        g.MapGet("/patients/{id:guid}/contact", async (Guid id, CareService s, HttpResponse response, CancellationToken ct) =>
            (await s.GetContactAsync(id, ct)).ToHttp(c => Etag(response, c))).RequireCapability(Capabilities.CareContactWrite);
        g.MapPut("/patients/{id:guid}/contact", async (Guid id, UpdateContactRequest body, HttpRequest http, HttpResponse response, CareService s, CancellationToken ct) =>
            (await s.UpdateContactAsync(id, body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(c => Etag(response, c))).RequireCapability(Capabilities.CareContactWrite);
        g.MapPost("/patients/{id:guid}/telegram-link", async (Guid id, CareService s, CancellationToken ct) =>
            (await s.CreateTelegramLinkAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.CareContactWrite);
        g.MapPost("/patients/{id:guid}/telegram-unlink", async (Guid id, CareService s, CancellationToken ct) =>
            (await s.UnlinkTelegramAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareContactWrite);

        // ---- care desk
        g.MapGet("/messages", async (string? status, Guid? patientId, CareService s, CancellationToken ct) => Results.Ok(await s.MessagesAsync(status, patientId, ct)))
            .RequireCapability(Capabilities.CareDesk);
        g.MapGet("/replies", async (CareService s, CancellationToken ct) => Results.Ok(await s.RepliesAsync(ct))).RequireCapability(Capabilities.CareDesk);
        g.MapGet("/tasks", async (bool? open, CareService s, CancellationToken ct) => Results.Ok(await s.TasksAsync(open ?? true, ct))).RequireCapability(Capabilities.CareDesk);
        g.MapPost("/tasks", async (ManualTaskRequest body, CareService s, CancellationToken ct) =>
            (await s.CreateManualTaskAsync(body, ct)).ToHttp(id => Results.Created($"/api/v1/care/tasks/{id}", new { id }))).RequireCapability(Capabilities.CareDesk);
        g.MapPost("/tasks/{id:guid}/complete", async (Guid id, CompleteTaskRequest body, HttpRequest http, CareService s, CancellationToken ct) =>
            (await s.CompleteTaskAsync(id, body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareDesk);
        g.MapPost("/replies/phone", async (PhoneReplyRequest body, CareService s, CancellationToken ct) =>
            (await s.RecordPhoneReplyAsync(body, ct)).ToHttp(Results.Ok)).RequireCapability(Capabilities.CareDesk);
        g.MapPost("/messages/{id:guid}/simulate-reply", async (Guid id, SimulateReplyRequest body, CareService s, IHostEnvironment env, IOptions<CareOptions> o, CancellationToken ct) =>
            SimulationAllowed(env, o.Value)
                ? (await s.SimulateReplyAsync(id, body, ct)).ToHttp(Results.Ok)
                : Results.NotFound()).RequireCapability(Capabilities.CareDesk);
        g.MapGet("/sandbox", (IHostEnvironment env, IOptions<CareOptions> o, CareChannels channels) =>
            Results.Ok(new { simulation = SimulationAllowed(env, o.Value), telegramLive = channels.IsLive("Telegram"), smsLive = false, botUsername = o.Value.Telegram.BotUsername }))
            .RequireCapability();

        // ---- management: templates, retries, statistics
        g.MapGet("/templates", async (CareService s, CancellationToken ct) => Results.Ok(await s.TemplatesAsync(ct))).RequireCapability(Capabilities.CareManage);
        g.MapPut("/templates/{kind}/{language}", async (string kind, string language, UpdateTemplateRequest body, CareService s, CancellationToken ct) =>
            (await s.UpdateTemplateAsync(kind, language, body, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareManage);
        g.MapPost("/messages/{id:guid}/retry", async (Guid id, CareService s, CancellationToken ct) =>
            (await s.RetryAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareManage);
        g.MapGet("/summary", async (CareService s, CancellationToken ct) => Results.Ok(await s.SummaryAsync(ct))).RequireCapability(Capabilities.CareManage);

        // ---- health tips (write: staff; approve: a different clinician) and campaigns
        g.MapGet("/tips", async (CareService s, CancellationToken ct) => Results.Ok(await s.TipsAsync(ct))).RequireCapability(Capabilities.CareTipsWrite);
        g.MapPost("/tips", async (TipRequest body, CareService s, CancellationToken ct) =>
            (await s.CreateTipAsync(body, ct)).ToHttp(id => Results.Created($"/api/v1/care/tips/{id}", new { id }))).RequireCapability(Capabilities.CareTipsWrite);
        g.MapPut("/tips/{id:guid}", async (Guid id, TipRequest body, HttpRequest http, CareService s, CancellationToken ct) =>
            (await s.UpdateTipAsync(id, body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareTipsWrite);
        g.MapPost("/tips/{id:guid}/approve", async (Guid id, CareService s, CancellationToken ct) =>
            (await s.ApproveTipAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareTipsApprove);
        g.MapPost("/tips/{id:guid}/retire", async (Guid id, CareService s, CancellationToken ct) =>
            (await s.RetireTipAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareManage);
        g.MapGet("/campaigns", async (CareService s, CancellationToken ct) => Results.Ok(await s.CampaignsAsync(ct))).RequireCapability(Capabilities.CareManage);
        g.MapPost("/campaigns", async (CampaignRequest body, CareService s, CancellationToken ct) =>
            (await s.CreateCampaignAsync(body, ct)).ToHttp(id => Results.Created($"/api/v1/care/campaigns/{id}", new { id }))).RequireCapability(Capabilities.CareManage);
        g.MapPost("/campaigns/{id:guid}/cancel", async (Guid id, CareService s, CancellationToken ct) =>
            (await s.CancelCampaignAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareManage);

        // ---- Telegram webhook (anonymous; authenticated by the secret token Telegram echoes back)
        app.MapPost("/api/v1/care/telegram/webhook", async (HttpRequest request, TelegramInbound inbound, IOptions<CareOptions> o, CancellationToken ct) =>
        {
            var t = o.Value.Telegram;
            var supplied = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
            if (!t.IsLive || t.WebhookSecret.Length < 24 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(t.WebhookSecret)))
            {
                return Results.NotFound();
            }

            JsonElement update;
            try
            {
                update = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            await inbound.HandleAsync(update, ct);
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery().WithTags("Care+").RequireRateLimiting("telegram-webhook");

        // ---- social media planner & tracker
        var s = app.MapGroup("/api/v1/social").WithTags("Social media");
        s.MapGet("/posts", async (DateOnly? from, DateOnly? to, SocialService svc, CancellationToken ct) => Results.Ok(await svc.PostsAsync(from, to, ct))).RequireCapability(Capabilities.SocialManage);
        s.MapPost("/posts", async (SocialPostRequest body, SocialService svc, CancellationToken ct) =>
            (await svc.SavePostAsync(null, body, null, ct)).ToHttp(id => Results.Created($"/api/v1/social/posts/{id}", new { id }))).RequireCapability(Capabilities.SocialManage);
        s.MapPut("/posts/{id:guid}", async (Guid id, SocialPostRequest body, HttpRequest http, SocialService svc, CancellationToken ct) =>
            (await svc.SavePostAsync(id, body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.SocialManage);
        s.MapGet("/metrics", async (int? days, SocialService svc, CancellationToken ct) => Results.Ok(await svc.MetricsAsync(days ?? 90, ct))).RequireCapability(Capabilities.SocialManage);
        s.MapPost("/metrics", async (SocialMetricRequest body, SocialService svc, CancellationToken ct) =>
            (await svc.RecordMetricAsync(body, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.SocialManage);
        s.MapGet("/summary", async (SocialService svc, CancellationToken ct) => Results.Ok(await svc.SummaryAsync(ct))).RequireCapability(Capabilities.SocialManage);
    }

    private static bool SimulationAllowed(IHostEnvironment env, CareOptions o) =>
        o.AllowSimulation && (env.IsDevelopment() || env.IsEnvironment("Testing") || env.IsEnvironment("Demo"));
}

public static class CareAutomationEndpoints
{
    public static void MapCareAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/care").WithTags("Care+");
        g.MapGet("/automation", async (RecallService s, HttpResponse response, CancellationToken ct) =>
        {
            var dto = await s.GetSettingsAsync(ct);
            response.Headers.ETag = ResultHttp.ETag(dto.Version);
            return Results.Ok(dto);
        }).RequireCapability(Capabilities.CareManage);
        g.MapPut("/automation", async (AutomationSettingsDto body, HttpRequest http, HttpResponse response, RecallService s, CancellationToken ct) =>
            (await s.UpdateSettingsAsync(body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(d =>
            {
                response.Headers.ETag = ResultHttp.ETag(d.Version);
                return Results.Ok(d);
            })).RequireCapability(Capabilities.CareManage);
        g.MapGet("/recalls", async (string? filter, Guid? patientId, RecallService s, CancellationToken ct) => Results.Ok(await s.ListAsync(filter, patientId, ct)))
            .RequireCapability(Capabilities.CareDesk);
        g.MapPost("/recalls", async (CreateRecallRequest body, RecallService s, CancellationToken ct) =>
            (await s.CreateAsync(body, ct)).ToHttp(id => Results.Created($"/api/v1/care/recalls/{id}", new { id }))).RequireCapability(Capabilities.CareDesk);
        g.MapPost("/recalls/{id:guid}/close", async (Guid id, CloseRecallRequest body, RecallService s, CancellationToken ct) =>
            (await s.CloseAsync(id, body, ct)).ToHttp(_ => Results.NoContent())).RequireCapability(Capabilities.CareDesk);

        var n = app.MapGroup("/api/v1/notices").WithTags("Notice board");
        n.MapGet("", async (bool? archived, NoticeService s, CancellationToken ct) => Results.Ok(await s.ListAsync(archived ?? false, ct))).RequireCapability();
        n.MapGet("/unread-count", async (NoticeService s, CancellationToken ct) => Results.Ok(new { count = await s.UnreadCountAsync(ct) })).RequireCapability();
        n.MapPost("", async (CreateNoticeRequest body, NoticeService s, CancellationToken ct) =>
            (await s.CreateAsync(body, ct)).ToHttp(id => Results.Created($"/api/v1/notices/{id}", new { id }))).RequireCapability(Capabilities.NoticePost);
        n.MapPost("/{id:guid}/read", async (Guid id, NoticeService s, CancellationToken ct) => (await s.MarkAsync(id, false, ct)).ToHttp(_ => Results.NoContent())).RequireCapability();
        n.MapPost("/{id:guid}/acknowledge", async (Guid id, NoticeService s, CancellationToken ct) => (await s.MarkAsync(id, true, ct)).ToHttp(_ => Results.NoContent())).RequireCapability();
        n.MapGet("/{id:guid}/receipts", async (Guid id, NoticeService s, CancellationToken ct) => (await s.ReceiptsAsync(id, ct)).ToHttp(Results.Ok)).RequireCapability();
        n.MapPost("/{id:guid}/archive", async (Guid id, NoticeService s, CancellationToken ct) => (await s.ArchiveAsync(id, ct)).ToHttp(_ => Results.NoContent())).RequireCapability();
    }
}
