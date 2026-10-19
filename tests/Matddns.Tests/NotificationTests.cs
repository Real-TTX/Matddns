using System.Net;
using System.Text.Json;
using Matddns.Models;
using Matddns.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

internal sealed class FakeMail : IMailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = new();
    public Exception? Fail { get; set; }

    public Task SendAsync(MailSettings mail, string subject, string body, CancellationToken ct)
    {
        if (Fail != null) throw Fail;
        Sent.Add((mail.To, subject, body));
        return Task.CompletedTask;
    }
}

public class NotificationTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly StubHandler _hook = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
    private readonly FakeMail _mail = new();

    public void Dispose() => _env.Dispose();

    private NotificationService Service() => new(_env.Config, _env.Log, new StubHttpClientFactory(_hook), _mail);

    private void Configure(Action<NotificationSettings> change) => _env.Config.Mutate(c => change(c.Settings.Notifications));

    private static readonly Notification Change = new(NotificationKind.Change, "home.example.com (A) is now 203.0.113.2",
        "home.example.com (A) is now 203.0.113.2, was 203.0.113.1.", "home.example.com", "A", "203.0.113.2", "203.0.113.1", "Office · WAN");

    [Fact]
    public async Task A_json_webhook_gets_the_event_and_the_record()
    {
        Configure(n => n.Webhook = new WebhookSettings { Enabled = true, Url = "https://hooks.example.com/matddns?key=secret" });

        Assert.Empty(await Service().SendAsync(Change, CancellationToken.None));

        var call = Assert.Single(_hook.Calls);
        Assert.Equal("POST", call.Method);
        var json = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("change", json.GetProperty("event").GetString());
        Assert.Equal("home.example.com", json.GetProperty("record").GetString());
        Assert.Equal("203.0.113.1", json.GetProperty("previous").GetString());
    }

    [Fact]
    public async Task Ntfy_gets_plain_text_and_gotify_its_message_json()
    {
        Configure(n => n.Webhook = new WebhookSettings { Enabled = true, Url = "https://ntfy.sh/topic", Format = WebhookFormat.Ntfy });
        await Service().SendAsync(Change, CancellationToken.None);
        Assert.Equal(Change.Text, _hook.Calls[0].Body);

        Configure(n => n.Webhook.Format = WebhookFormat.Gotify);
        await Service().SendAsync(Change, CancellationToken.None);
        var gotify = JsonDocument.Parse(_hook.Calls[1].Body).RootElement;
        Assert.Equal(Change.Text, gotify.GetProperty("message").GetString());
        Assert.Equal(5, gotify.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Mail_goes_to_the_recipients_and_a_failure_is_reported_without_secrets()
    {
        Configure(n => n.Mail = new MailSettings { Enabled = true, Host = "smtp.example.com", From = "matddns@example.com", To = "a@example.com", Password = "smtp-secret" });
        Configure(n => n.Webhook = new WebhookSettings { Enabled = true, Url = "https://hooks.example.com/x?token=hook-secret" });
        _hook.Calls.Clear();
        var failing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var service = new NotificationService(_env.Config, _env.Log, new StubHttpClientFactory(failing), _mail);

        var problems = await service.SendAsync(Change, CancellationToken.None);

        Assert.Equal("Matddns: " + Change.Title, Assert.Single(_mail.Sent).Subject);
        Assert.Equal(["Webhook: HTTP 401"], problems);
        var logged = string.Join("\n", _env.Log.Recent().Select(e => e.Message));
        Assert.DoesNotContain("hook-secret", logged);
        Assert.DoesNotContain("smtp-secret", logged);
    }

    [Theory]
    [InlineData("", "a@example.com", "b@example.com", "no SMTP server set")]
    [InlineData("smtp.example.com", "not-an-address", "b@example.com", "no valid sender address")]
    [InlineData("smtp.example.com", "a@example.com", "", "no valid recipient address")]
    [InlineData("smtp.example.com", "a@example.com", "b@example.com, c@example.com", null)]
    public void Mail_needs_a_server_a_sender_and_recipients(string host, string from, string to, string? problem)
        => Assert.Equal(problem, NotificationService.MailProblem(new MailSettings { Host = host, From = from, To = to }));

    [Fact]
    public async Task The_updater_tells_about_a_change_a_failure_and_the_recovery_once_each()
    {
        Configure(n => n.Webhook = new WebhookSettings { Enabled = true, Url = "https://hooks.example.com/x" });
        var notify = Service();
        await notify.StartAsync(CancellationToken.None);
        var dynDnsUp = true;
        var dyn = new StubHandler(_ => dynDnsUp ? new HttpResponseMessage { Content = new StringContent("good") } : new HttpResponseMessage(HttpStatusCode.BadGateway));
        var updater = TestUpdater.Create(_env, dyn, notify);
        var (sourceId, _) = TestUpdater.StaticToDynDns(_env, "203.0.113.10");

        await updater.RunNowAsync(CancellationToken.None);                       // change (first write)
        TestUpdater.SetStaticIp(_env, sourceId, "203.0.113.11");
        dynDnsUp = false;
        await updater.RunNowAsync(CancellationToken.None);                       // failure
        await updater.RunNowAsync(CancellationToken.None);                       // still failing: nothing new
        dynDnsUp = true;
        await updater.RunNowAsync(CancellationToken.None);                       // change and recovery

        var events = await EventsAsync(4);
        await notify.StopAsync(CancellationToken.None);
        Assert.Equal(["change", "failure", "change", "recovery"], events);
    }

    [Fact]
    public async Task Nothing_is_sent_for_a_kind_that_is_switched_off()
    {
        Configure(n => { n.OnChange = false; n.Webhook = new WebhookSettings { Enabled = true, Url = "https://hooks.example.com/x" }; });
        var notify = Service();
        await notify.StartAsync(CancellationToken.None);

        notify.Notify(Change);
        notify.Notify(Change with { Kind = NotificationKind.Failure });

        Assert.Equal(["failure"], await EventsAsync(1));
        await notify.StopAsync(CancellationToken.None);
    }

    private async Task<List<string>> EventsAsync(int expected)
    {
        for (var i = 0; i < 100 && _hook.Calls.Count < expected; i++) await Task.Delay(20);
        await Task.Delay(50); // nothing more comes
        return _hook.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("event").GetString()!).ToList();
    }
}

[Collection("app")]
public class NotificationFormTests
{
    private readonly AppFactory _app;
    public NotificationFormTests(AppFactory app) => _app = app;

    [Fact]
    public async Task The_settings_are_saved_the_password_is_kept_and_never_shown()
    {
        var config = _app.Services.GetRequiredService<ConfigService>();
        var admin = await _app.AdminAsync();
        (string, string)[] form =
        [
            ("OnChange", "true"), ("OnFailure", "true"), ("MailEnabled", "true"), ("MailHost", "smtp.example.com"), ("MailPort", "465"), ("MailTls", "Tls"),
            ("MailUsername", "relay"), ("MailPassword", "mail-secret-1"), ("MailFrom", "matddns@example.com"), ("MailTo", "ops@example.com"),
            ("WebhookEnabled", "true"), ("WebhookUrl", "https://gotify.example.com/message?token=gotify-secret"), ("WebhookFormat", "Gotify"),
        ];

        var saved = await AppFactory.PostForm(admin, "/System?handler=Notifications", "/System?tab=notifications", form);
        Assert.Equal("/System?tab=notifications", saved.Headers.Location!.ToString());

        // again without password and URL: both are kept
        await AppFactory.PostForm(admin, "/System?handler=Notifications", "/System?tab=notifications",
            form.Where(f => f.Item1 is not ("MailPassword" or "WebhookUrl")).ToArray());

        var n = config.Read(c => c.Settings.Notifications);
        Assert.Equal(("mail-secret-1", 465, MailTls.Tls), (n.Mail.Password, n.Mail.Port, n.Mail.Tls));
        Assert.Equal(("https://gotify.example.com/message?token=gotify-secret", WebhookFormat.Gotify), (n.Webhook.Url, n.Webhook.Format));

        var page = await (await admin.GetAsync("/System?tab=notifications")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("mail-secret-1", page);
        Assert.DoesNotContain("gotify-secret", page);
        Assert.Contains("https://gotify.example.com/", page); // scheme and host only
    }

    [Fact]
    public async Task A_webhook_without_an_http_url_is_refused()
    {
        var config = _app.Services.GetRequiredService<ConfigService>();
        config.Mutate(c => c.Settings.Notifications = new NotificationSettings());
        var admin = await _app.AdminAsync();

        await AppFactory.PostForm(admin, "/System?handler=Notifications", "/System?tab=notifications", ("WebhookEnabled", "true"), ("WebhookUrl", "file:///etc/passwd"));

        Assert.False(config.Read(c => c.Settings.Notifications.Webhook.Enabled));
    }
}
