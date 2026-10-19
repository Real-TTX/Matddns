using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;
using Matddns.Platform;
using Matddns.Services;
using Matddns.Support;
using MatPlatform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>Which server the mail of Matddns goes to, with and without the <c>MATSMTP_*</c> variables (MatDesign docs/PLATFORM.md "Mail through MatSMTP").</summary>
public sealed class MailServerTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly FakeMail _mail = new();

    public void Dispose() => _env.Dispose();

    private static readonly MailSettings Own = new()
    {
        Enabled = true,
        Host = "smtp.own.example",
        Port = 587,
        Tls = MailTls.StartTls,
        Username = "own-user",
        Password = "own-password",
        From = "matddns@own.example",
        To = "ops@own.example",
    };

    internal static MatSmtpSettings Smtp(params (string Name, string Value)[] variables)
        => MatSmtpSettings.From(new ConfigurationBuilder().AddInMemoryCollection(variables.Select(v => KeyValuePair.Create(v.Name, (string?)v.Value))).Build());

    internal static MatSmtpSettings Full(string tls = "starttls") => Smtp(
        ("MATSMTP_HOST", "mattiverse-matsmtp-matsmtp"),
        ("MATSMTP_PORT", "2025"),
        ("MATSMTP_USERNAME", "matddns"),
        ("MATSMTP_PASSWORD", "relay-password"),
        ("MATSMTP_FROM", "matddns@example.com"),
        ("MATSMTP_TLS", tls));

    [Fact]
    public void Without_the_variables_the_own_settings_are_used_as_before()
    {
        var mail = NotificationService.Effective(Own, Smtp());
        Assert.Equal(JsonSerializer.Serialize(Own), JsonSerializer.Serialize(mail));
        Assert.NotSame(Own, mail);
    }

    [Theory]
    [InlineData("none", MailTls.None)]
    [InlineData("starttls", MailTls.StartTls)]
    [InlineData("TLS", MailTls.Tls)]
    [InlineData("", MailTls.None)]
    public void With_the_variables_server_user_password_and_sender_come_from_matsmtp(string tls, MailTls expected)
    {
        var mail = NotificationService.Effective(Own, Full(tls));
        Assert.Equal(("mattiverse-matsmtp-matsmtp", 2025, expected, "matddns", "relay-password", "matddns@example.com"),
            (mail.Host, mail.Port, mail.Tls, mail.Username, mail.Password, mail.From));
        Assert.Equal((true, "ops@own.example"), (mail.Enabled, mail.To));
        Assert.Equal("smtp.own.example", Own.Host); // the own settings stay as they are
    }

    [Fact]
    public void Without_a_user_matsmtp_is_used_without_authentication_and_without_a_sender_the_own_one()
    {
        var mail = NotificationService.Effective(Own, Smtp(("MATSMTP_HOST", "matsmtp")));
        Assert.Equal(("matsmtp", MatSmtpSettings.DefaultPort, MailTls.None, "", "", "matddns@own.example"),
            (mail.Host, mail.Port, mail.Tls, mail.Username, mail.Password, mail.From));
    }

    [Fact]
    public async Task Notifications_are_sent_through_matsmtp()
    {
        _env.Config.Mutate(c => c.Settings.Notifications.Mail = new MailSettings { Enabled = true, To = "ops@example.com" });
        var service = new NotificationService(_env.Config, _env.Log, new StubHttpClientFactory(new StubHandler(_ => new HttpResponseMessage())), _mail, Full());

        Assert.True(service.MailThroughMatSmtp);
        Assert.Empty(await service.SendAsync(new Notification(NotificationKind.Test, "test", "text"), CancellationToken.None));

        var server = Assert.Single(_mail.Servers);
        Assert.Equal(("mattiverse-matsmtp-matsmtp", 2025, "matddns", "matddns@example.com", "ops@example.com"), (server.Host, server.Port, server.Username, server.From, server.To));
    }

    [Fact]
    public void With_matsmtp_a_change_of_the_server_is_refused_and_the_rest_is_saved()
    {
        _env.Config.Mutate(c => c.Settings.Notifications.Mail = new MailSettings { Host = "smtp.own.example", Port = 465, Password = "own-password", From = "own@example.com" });
        var settings = new SettingsService(_env.Config, _env.Log, Full());
        Assert.True(settings.MailFixed);

        var refused = settings.ChangeNotifications(new NotificationSettingsChange(MailHost: "evil.example", MailPassword: "evil"), "test");
        Assert.Equal(["mailHost", "mailPassword"], refused.Keys.Order());
        Assert.Contains("MatSMTP", refused["mailHost"]);
        Assert.DoesNotContain("relay-password", refused["mailHost"]);

        // mail on with only recipients: MatSMTP supplies server and sender
        Assert.Empty(settings.ChangeNotifications(new NotificationSettingsChange(MailEnabled: true, MailTo: "new@example.com", OnChange: false), "test"));
        var stored = _env.Config.Read(c => c.Settings.Notifications);
        Assert.Equal((true, "new@example.com", false), (stored.Mail.Enabled, stored.Mail.To, stored.OnChange));
        Assert.Equal(("smtp.own.example", 465, "own-password", "own@example.com"), (stored.Mail.Host, stored.Mail.Port, stored.Mail.Password, stored.Mail.From));
    }

    [Fact]
    public void Without_matsmtp_the_own_server_is_checked_and_saved()
    {
        var settings = new SettingsService(_env.Config, _env.Log);
        Assert.False(settings.MailFixed);

        var refused = settings.ChangeNotifications(new NotificationSettingsChange(MailEnabled: true, MailTo: "ops@example.com"), "test");
        Assert.True(refused.ContainsKey("mailHost"));
        Assert.False(_env.Config.Read(c => c.Settings.Notifications.Mail.Enabled));

        Assert.Empty(settings.ChangeNotifications(new NotificationSettingsChange(MailEnabled: true, MailHost: "smtp.example.com", MailFrom: "a@example.com", MailTo: "ops@example.com"), "test"));
        Assert.Equal("smtp.example.com", _env.Config.Read(c => c.Settings.Notifications.Mail.Host));
    }

}

/// <summary>Matddns with the <c>MATSMTP_*</c> variables.</summary>
public sealed class MatSmtpFactory() : MatddnsFactory(platformKey: true, engineKey: true, authority: null, smtp: Variables)
{
    public static readonly IReadOnlyDictionary<string, string> Variables = new Dictionary<string, string>
    {
        [MatSmtpSettings.HostVariable] = "mattiverse-matsmtp-matsmtp",
        [MatSmtpSettings.PortVariable] = "2025",
        [MatSmtpSettings.UsernameVariable] = "matddns",
        [MatSmtpSettings.PasswordVariable] = "relay-password-4711",
        [MatSmtpSettings.FromVariable] = "matddns@example.com",
        [MatSmtpSettings.TlsVariable] = "starttls",
    };
}

/// <summary>Matddns with MatSMTP: <c>/api/configure</c> and System, Notifications show the mail server as fixed.</summary>
public sealed class MatSmtpSiteTests(MatSmtpFactory site) : IClassFixture<MatSmtpFactory>
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_fixes_the_mail_server_fields_only_with_the_variables(string language)
    {
        string[] fixedKeys = ["mailHost", "mailPort", "mailTls", "mailUsername", "mailPassword", "mailFrom"];
        var smtp = MailServerTests.Full();
        var definition = MatPlatformBuilder.Build(o => MatddnsPlatform.Describe(o, smtp), language);
        var sprite = site.Services.GetRequiredService<Toolkit>().IconNames;
        Assert.Empty(MatPlatformValidator.Validate(definition, sprite));

        foreach (var field in definition.Configure)
        {
            Assert.Equal(fixedKeys.Contains(field.Key), field.Fixed == true);
            if (fixedKeys.Contains(field.Key)) Assert.Equal(smtp.Describe(language), field.Hint);
        }

        Assert.All(MatPlatformBuilder.Build(MatddnsPlatform.Describe, language).Configure, field => Assert.NotEqual(true, field.Fixed));
    }

    private void StoreOwn() => site.Configure(c => c.Settings.Notifications = new NotificationSettings
    {
        Mail = new MailSettings { Enabled = false, Host = "smtp.own.example", Port = 465, Password = "own-password", From = "own@example.com", To = "ops@example.com" },
    });

    [Fact]
    public async Task The_definition_marks_the_server_fixed_and_configure_answers_matsmtp()
    {
        StoreOwn();
        using (var doc = JsonDocument.Parse(await site.Anonymous().GetStringAsync("/api")))
        {
            var fields = doc.RootElement.GetProperty("configure").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
            Assert.True(fields["mailHost"].GetProperty("fixed").GetBoolean());
            Assert.True(fields["mailPassword"].GetProperty("fixed").GetBoolean());
            Assert.False(fields["mailTo"].TryGetProperty("fixed", out _));
            Assert.Contains("MatSMTP (mattiverse-matsmtp-matsmtp:2025, STARTTLS)", fields["mailHost"].GetProperty("hint").GetString());
        }

        var json = await site.Api().GetStringAsync("/api/configure");
        Assert.DoesNotContain("relay-password-4711", json);
        Assert.DoesNotContain("own-password", json);
        using (var doc = JsonDocument.Parse(json))
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal("mattiverse-matsmtp-matsmtp", values.GetProperty("mailHost").GetString());
            Assert.Equal(2025, values.GetProperty("mailPort").GetInt32());
            Assert.Equal("starttls", values.GetProperty("mailTls").GetString());
            Assert.Equal("matddns@example.com", values.GetProperty("mailFrom").GetString());
            Assert.True(values.GetProperty("mailPassword").GetProperty("isSet").GetBoolean());
            Assert.Equal("ops@example.com", values.GetProperty("mailTo").GetString());
        }
    }

    [Theory]
    [InlineData("mailHost", "\"evil.example\"")]
    [InlineData("mailPassword", "\"evil\"")]
    [InlineData("mailPort", "25")]
    public async Task A_change_of_a_fixed_field_is_400_and_nothing_is_saved(string key, string value)
    {
        StoreOwn();
        var response = await site.Api().PutAsync("/api/configure",
            new StringContent($"{{\"values\":{{\"{key}\":{value},\"mailTo\":\"x@example.com\"}}}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(key, out _));
        Assert.Equal(("smtp.own.example", 465, "own-password", "ops@example.com"),
            site.Read(c => (c.Settings.Notifications.Mail.Host, c.Settings.Notifications.Mail.Port, c.Settings.Notifications.Mail.Password, c.Settings.Notifications.Mail.To)));
    }

    [Fact]
    public async Task Mail_on_and_the_recipients_stay_editable()
    {
        StoreOwn();
        var response = await site.Api().PutAsJsonAsync("/api/configure", new { values = new { mailEnabled = true, mailTo = "noc@example.com", notifyOnChange = false } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var n = site.Read(c => c.Settings.Notifications);
        Assert.Equal((true, "noc@example.com", false, "smtp.own.example"), (n.Mail.Enabled, n.Mail.To, n.OnChange, n.Mail.Host));
    }

    [Fact]
    public async Task The_page_shows_the_sentence_and_the_server_disabled_and_saves_nothing_of_it()
    {
        StoreOwn();
        var browser = site.Browser();
        var login = await (await browser.GetAsync("/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Login", login)).StatusCode);

        var html = await (await browser.GetAsync("/System?tab=notifications")).Content.ReadAsStringAsync();
        Assert.Contains("Mail is sent through MatSMTP (mattiverse-matsmtp-matsmtp:2025, STARTTLS) as matddns@example.com.", html);
        Assert.Matches("id=\"MailHost\" name=\"MailHost\" value=\"mattiverse-matsmtp-matsmtp\" disabled=\"disabled\"", html);
        Assert.Matches("id=\"MailPassword\" name=\"MailPassword\" disabled=\"disabled\"", html);
        Assert.DoesNotContain("relay-password-4711", html);
        Assert.DoesNotContain("own-password", html);
        Assert.Matches("id=\"MailTo\" name=\"MailTo\" value=\"ops@example.com\" class", html);

        // a browser that sends the disabled fields anyway changes nothing of the server
        var saved = await AppFactory.PostForm(browser, "/System?handler=Notifications", "/System?tab=notifications",
            ("OnChange", "true"), ("MailEnabled", "true"), ("MailHost", "evil.example"), ("MailPort", "25"), ("MailPassword", "evil"), ("MailFrom", "evil@example.com"), ("MailTo", "noc@example.com"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var n = site.Read(c => c.Settings.Notifications);
        Assert.Equal((true, "noc@example.com"), (n.Mail.Enabled, n.Mail.To));
        Assert.Equal(("smtp.own.example", 465, "own-password", "own@example.com"), (n.Mail.Host, n.Mail.Port, n.Mail.Password, n.Mail.From));
    }
}
