using System.Net.Http.Json;
using System.Threading.Channels;
using Matddns.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Matddns.Services;

public enum NotificationKind { Change, Failure, Recovery, Test }

/// <summary>One message: what happened to which record. <see cref="Title"/> is the subject or the title, <see cref="Text"/> the body.</summary>
public sealed record Notification(NotificationKind Kind, string Title, string Text, string? Record = null, string? Type = null, string? Value = null, string? Previous = null, string? Source = null)
{
    public DateTime Time { get; init; } = DateTime.UtcNow;

    /// <summary>The name of <see cref="Kind"/> in the JSON of a webhook: change, failure, recovery, test.</summary>
    public string Event => Kind.ToString().ToLowerInvariant();
}

/// <summary>Sends one mail; the default is MailKit, the tests put in their own.</summary>
public interface IMailSender
{
    Task SendAsync(MailSettings mail, string subject, string body, CancellationToken ct);
}

public sealed class MailKitSender : IMailSender
{
    public async Task SendAsync(MailSettings mail, string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(mail.From.Trim()));
        foreach (var to in NotificationService.Recipients(mail.To)) message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient { Timeout = 15000 };
        var security = mail.Tls switch
        {
            MailTls.Tls => SecureSocketOptions.SslOnConnect,
            MailTls.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };
        await smtp.ConnectAsync(mail.Host.Trim(), mail.Port, security, ct);
        if (!string.IsNullOrEmpty(mail.Username)) await smtp.AuthenticateAsync(mail.Username, mail.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// Tells the configured receivers (mail, a webhook in JSON, ntfy or Gotify form) when a record gets a new value, fails or works again.
/// <see cref="Notify"/> only queues; the background loop sends, so a slow mail server never holds up the updater. A failed delivery is logged
/// (without the password or the URL, which may carry a token) and dropped.
/// </summary>
public class NotificationService : BackgroundService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly IHttpClientFactory _http;
    private readonly IMailSender _mail;
    private readonly Channel<Notification> _queue = Channel.CreateBounded<Notification>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

    public NotificationService(ConfigService config, LogService log, IHttpClientFactory http, IMailSender? mail = null)
    {
        _config = config;
        _log = log;
        _http = http;
        _mail = mail ?? new MailKitSender();
    }

    /// <summary>Queues <paramref name="n"/> when its kind is switched on and a receiver is set up.</summary>
    public void Notify(Notification n)
    {
        var s = _config.Read(c => c.Settings.Notifications);
        var wanted = n.Kind switch
        {
            NotificationKind.Change => s.OnChange,
            NotificationKind.Failure or NotificationKind.Recovery => s.OnFailure,
            _ => true,
        };
        if (wanted && (s.Mail.Enabled || s.Webhook.Enabled)) _queue.Writer.TryWrite(n);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var n in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(n, stoppingToken);
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    /// <summary>
    /// The mail settings that are used. Today the own ones of System, Notifications; mail through MatSMTP (the <c>MATSMTP_*</c> variables of the Mat
    /// platform, MatPlatform <c>MatSmtpSettings</c>) will take their place here, field by field, when they are set.
    /// </summary>
    public MailSettings EffectiveMail() => _config.Read(c => c.Settings.Notifications.Mail);

    /// <summary>Sends <paramref name="n"/> to every enabled receiver now. Answers one line per receiver that failed (empty = all went out).</summary>
    public async Task<IReadOnlyList<string>> SendAsync(Notification n, CancellationToken ct)
    {
        var s = _config.Read(c => c.Settings.Notifications);
        var mail = EffectiveMail();
        var problems = new List<string>();

        if (s.Mail.Enabled)
        {
            try
            {
                if (MailProblem(mail) is { } p) throw new InvalidOperationException(p);
                await _mail.SendAsync(mail, $"Matddns: {n.Title}", n.Text, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problems.Add($"Mail: {ex.Message}");
            }
        }

        if (s.Webhook.Enabled)
        {
            try { await PostWebhookAsync(s.Webhook, n, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problems.Add($"Webhook: {(ex is HttpRequestException h && h.StatusCode is { } code ? $"HTTP {(int)code}" : ex.Message)}");
            }
        }

        foreach (var p in problems) _log.Log(LogLevel.Warn, "notify", $"{n.Event} '{n.Title}' not delivered: {p}");
        return problems;
    }

    private async Task PostWebhookAsync(WebhookSettings hook, Notification n, CancellationToken ct)
    {
        if (!InputCheck.IsHttpUrl(hook.Url)) throw new InvalidOperationException("no http(s) URL configured");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url.Trim());
        switch (hook.Format)
        {
            case WebhookFormat.Ntfy:
                request.Content = new StringContent(n.Text);
                request.Headers.TryAddWithoutValidation("Title", $"Matddns: {n.Title}");
                request.Headers.TryAddWithoutValidation("Tags", n.Kind is NotificationKind.Failure ? "warning" : n.Kind is NotificationKind.Recovery ? "white_check_mark" : "globe_with_meridians");
                if (n.Kind == NotificationKind.Failure) request.Headers.TryAddWithoutValidation("Priority", "high");
                break;
            case WebhookFormat.Gotify:
                request.Content = JsonContent.Create(new { title = $"Matddns: {n.Title}", message = n.Text, priority = n.Kind == NotificationKind.Failure ? 8 : 5 });
                break;
            default:
                request.Content = JsonContent.Create(new
                {
                    app = "matddns",
                    @event = n.Event,
                    title = n.Title,
                    message = n.Text,
                    record = n.Record,
                    type = n.Type,
                    value = n.Value,
                    previous = n.Previous,
                    source = n.Source,
                    time = n.Time,
                });
                break;
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>What is missing for mail, or null.</summary>
    public static string? MailProblem(MailSettings m)
    {
        if (string.IsNullOrWhiteSpace(m.Host)) return "no SMTP server set";
        if (m.Port is < 1 or > 65535) return "the port must be 1 to 65535";
        if (!IsAddress(m.From)) return "no valid sender address";
        var to = Recipients(m.To).ToList();
        if (to.Count == 0 || !to.All(IsAddress)) return "no valid recipient address";
        return null;
    }

    public static IEnumerable<string> Recipients(string? to)
        => (to ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsAddress(string? s) => !string.IsNullOrWhiteSpace(s) && MailboxAddress.TryParse(s.Trim(), out var a) && a.Address.Contains('@');
}
