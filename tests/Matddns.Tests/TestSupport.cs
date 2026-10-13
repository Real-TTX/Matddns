using System.Text.Json;
using Matddns.Services;

namespace Matddns.Tests;

/// <summary>A throw-away data directory with config, log and auth services wired up against it.</summary>
internal sealed class TestEnvironment : IDisposable
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("matddns-tests-").FullName;
    public PathOptions Paths { get; }
    public AuthService Auth { get; } = new();
    public ConfigService Config { get; }
    public LogService Log { get; }

    /// <param name="configJson">Optional content for config.json, written before the config is loaded.</param>
    public TestEnvironment(string? configJson = null)
    {
        Paths = new PathOptions(DataDirectory);
        if (configJson != null) File.WriteAllText(Paths.ConfigFile, configJson);
        Config = new ConfigService(Paths, Auth);
        Config.EnsureLoaded();
        Log = new LogService(Paths, Config);
    }

    public void Dispose()
    {
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}

internal record RecordedCall(Uri Uri, string? Authorization, string Body, string Method = "GET");

/// <summary>Answers every request from a delegate and records what was sent.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<RecordedCall, HttpResponseMessage> _respond;
    public List<RecordedCall> Calls { get; } = new();

    public StubHandler(Func<RecordedCall, HttpResponseMessage> respond) => _respond = respond;

    public static StubHandler Text(string body) => new(_ => new HttpResponseMessage { Content = new StringContent(body) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new RecordedCall(request.RequestUri!, request.Headers.Authorization?.ToString(), body, request.Method.Method);
        Calls.Add(call);
        return _respond(call);
    }
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

internal static class NetcupStub
{
    /// <summary>A Netcup API that has no records yet and accepts every action.</summary>
    public static StubHandler EmptyZone() => new(call =>
    {
        var action = JsonDocument.Parse(call.Body).RootElement.GetProperty("action").GetString();
        var json = action switch
        {
            "login" => """{"status":"success","responsedata":{"apisessionid":"SID"}}""",
            "infoDnsRecords" => """{"status":"success","responsedata":{"dnsrecords":[]}}""",
            _ => """{"status":"success"}"""
        };
        return new HttpResponseMessage { Content = new StringContent(json) };
    });
}
