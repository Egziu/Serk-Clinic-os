using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Serk.Clinic.Care.Application;

public sealed class CareOptions
{
    /// <summary>Background planning and sending of patient messages.</summary>
    public bool AutomationEnabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Allows the "simulate patient reply" helper (only honoured in Development, Testing and Demo).</summary>
    public bool AllowSimulation { get; set; }

    public TelegramOptions Telegram { get; set; } = new();
}

public sealed class TelegramOptions
{
    /// <summary>Real Telegram sending is OFF unless explicitly enabled AND a bot token is configured (owner approval needed).</summary>
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = "";

    /// <summary>Public bot username (without @), used for the patient's link: https://t.me/{username}?start={code}.</summary>
    public string BotUsername { get; set; } = "";

    /// <summary>Secret Telegram echoes in X-Telegram-Bot-Api-Secret-Token on every webhook call.</summary>
    public string WebhookSecret { get; set; } = "";

    public bool IsLive => Enabled && BotToken.Length > 20;
}

/// <summary>One outbound channel. Returns the provider's message id when the provider ACCEPTED the message (not proof of reading).</summary>
public interface ICareMessenger
{
    string Name { get; }

    Task<string> SendAsync(string channel, string address, string text, CancellationToken ct);
}

/// <summary>Default: records messages in memory and sends nothing anywhere. Used for both SMS and Telegram unless configured.</summary>
public sealed class SandboxCareMessenger : ICareMessenger
{
    private readonly List<(string Channel, string Address, string Text)> _sent = [];

    public string Name => "sandbox";

    /// <summary>Test hook: when set, sends fail (simulated provider outage).</summary>
    public bool Failing { get; set; }

    /// <summary>Test hook: returns an exception to throw for a given address (simulates a malformed provider answer).</summary>
    public Func<string, Exception?>? FaultFor { get; set; }

    public IReadOnlyList<(string Channel, string Address, string Text)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    public Task<string> SendAsync(string channel, string address, string text, CancellationToken ct)
    {
        if (Failing)
        {
            throw new HttpRequestException("Sandbox provider outage (simulated).");
        }

        if (FaultFor?.Invoke(address) is { } fault)
        {
            throw fault;
        }

        lock (_sent)
        {
            _sent.Add((channel, address, text));
        }

        return Task.FromResult("sandbox-" + Guid.NewGuid().ToString("N")[..12]);
    }
}

/// <summary>
/// Telegram Bot API sender (https://core.telegram.org/bots/api#sendmessage). Only used when <see cref="TelegramOptions.IsLive"/>.
/// The token is read from configuration (secret store) and never logged.
/// </summary>
public sealed class TelegramBotMessenger(HttpClient http, IOptions<CareOptions> options) : ICareMessenger
{
    public string Name => "telegram";

    public async Task<string> SendAsync(string channel, string address, string text, CancellationToken ct)
    {
        var token = options.Value.Telegram.BotToken;
        using var response = await http.PostAsJsonAsync(new Uri($"https://api.telegram.org/bot{token}/sendMessage"),
            new { chat_id = address, text, disable_web_page_preview = true }, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!response.IsSuccessStatusCode || !body.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = body.TryGetProperty("description", out var d) ? d.GetString() : null;
            throw new HttpRequestException($"Telegram refused the message ({(int)response.StatusCode}{(description is null ? "" : ": " + description)}).");
        }

        return body.GetProperty("result").GetProperty("message_id").GetRawText();
    }
}

/// <summary>Chooses the sender per channel: Telegram only when live; SMS always sandbox (no gateway connected yet).</summary>
public sealed class CareChannels(SandboxCareMessenger sandbox, IHttpClientFactory httpClients, IOptions<CareOptions> options)
{
    public const string TelegramClient = "care-telegram";

    public ICareMessenger For(string channel) =>
        channel == "Telegram" && options.Value.Telegram.IsLive ? new TelegramBotMessenger(httpClients.CreateClient(TelegramClient), options) : sandbox;

    public bool IsLive(string channel) => channel == "Telegram" && options.Value.Telegram.IsLive;
}
