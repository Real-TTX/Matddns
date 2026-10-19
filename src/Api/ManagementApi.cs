using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Matddns.Models;
using Matddns.Services;
using LogLevel = Matddns.Services.LogLevel;

namespace Matddns.Api;

/// <summary>
/// The management API (<c>/api/v1</c>) that lets Mattiverse provision Matddns: sources, domain groups (provider
/// account + records) and rules, i.e. exactly what the UI manages. Guarded by the <c>X-Api-Key</c> header against
/// <c>MATDDNS_API_KEY</c> (config <c>Matddns:ApiKey</c>); the cookie session is ignored. The writes are upserts
/// (repeatable without side effects) and go through <see cref="ConfigService.Transact"/>, so a rejected request
/// changes nothing and an accepted one is saved atomically and picked up by the updater without a restart.
/// </summary>
public static class ManagementApi
{
    public const int ApiVersion = 1;

    private const string DefaultFritzboxUrl = "http://fritz.box:49000"; // as the UI

    // Same camelCase JSON the config file uses (enums by name).
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Outcome(JsonNode Result, string Message, bool Changed);

    public static void MapManagementApi(this WebApplication app)
    {
        var configured = Environment.GetEnvironmentVariable("MATDDNS_API_KEY");
        if (string.IsNullOrWhiteSpace(configured)) configured = app.Configuration["Matddns:ApiKey"];
        var apiKey = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        app.Logger.LogInformation("Management API (/api/v1): {State}",
            apiKey == null ? "disabled - set MATDDNS_API_KEY to enable it" : "enabled");

        // Anonymous for the cookie auth (no login redirect): the API key is the only guard. No login rate limit applies.
        var api = app.MapGroup("/api/v1").AllowAnonymous();
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (apiKey == null)
                return Error(503, "The management API is disabled: no API key is configured (set MATDDNS_API_KEY).");
            if (!KeyEquals(apiKey, http.Request.Headers["X-Api-Key"].FirstOrDefault()))
                return Error(401, "Invalid or missing X-Api-Key header.");
            try { return await next(ctx); }
            catch (ApiException ex) { return Error(ex.Status, ex.Message); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                http.RequestServices.GetRequiredService<LogService>().Log(LogLevel.Error, "api", $"{http.Request.Method} {http.Request.Path}: {ex}");
                return Error(500, "Internal error: " + ex.Message);
            }
        });

        api.MapGet("/status", (ConfigService config) =>
        {
            var counts = config.Read(c => new { sources = c.Sources.Count, domains = c.Domains.Count, rules = c.Rules.Count(r => !r.Dynamic) });
            return Results.Json(new { ok = true, app = "matddns", version = BuildInfo.Version, apiVersion = ApiVersion, counts });
        });

        // sources
        api.MapGet("/sources", (ConfigService config) =>
            Reply(config.Read(c => new JsonArray(c.Sources.Select(s => (JsonNode)SourceOut(s)).ToArray()))));
        api.MapPost("/sources", async (HttpRequest req, ConfigService config, LogService log) =>
        {
            var body = await Body.ReadAsync(req);
            return Reply(log, config.Transact(c => UpsertSource(c, body)));
        });
        api.MapDelete("/sources/{id}", (string id, ConfigService config, LogService log) =>
            Deleted(log, "source", config.Transact(c => DeleteSource(c, id))));

        // domain groups (provider account + records)
        api.MapGet("/domains", (ConfigService config) =>
            Reply(config.Read(c => new JsonArray(c.Domains.Select(d => (JsonNode)DomainOut(d)).ToArray()))));
        api.MapPost("/domains", async (HttpRequest req, ConfigService config, LogService log) =>
        {
            var body = await Body.ReadAsync(req);
            return Reply(log, config.Transact(c => UpsertDomain(c, body)));
        });
        api.MapDelete("/domains/{id}", (string id, ConfigService config, LogService log) =>
            Deleted(log, "domain group", config.Transact(c => DeleteDomain(c, id))));

        // rules
        api.MapGet("/rules", (ConfigService config) =>
            Reply(config.Read(c => new JsonArray(c.Rules.Where(r => !r.Dynamic).Select(r => (JsonNode)RuleOut(r)).ToArray()))));
        api.MapPost("/rules", async (HttpRequest req, ConfigService config, LogService log) =>
        {
            var body = await Body.ReadAsync(req);
            return Reply(log, config.Transact(c => UpsertRule(c, body)));
        });
        api.MapDelete("/rules/{id}", (string id, ConfigService config, LogService log) =>
            Deleted(log, "rule", config.Transact(c => DeleteRule(c, id))));

        // evaluate now, like the periodic check
        api.MapPost("/run", async (HttpContext ctx, UpdaterService updater, LogService log) =>
        {
            await updater.RunNowAsync(ctx.RequestAborted);
            log.Log(LogLevel.Debug, "api", "run: evaluated all rules");
            return Results.Json(new { ok = true });
        });

        // anything else below /api/v1 (after the key check) is a JSON 404 rather than a redirect to the login page
        api.Map("/{**path}", (HttpContext ctx) =>
            Error(404, $"No such endpoint or method: {ctx.Request.Method} {ctx.Request.Path}"));
    }

    // ---------------------------------------------------------------- plumbing

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static IResult Reply(JsonNode node) => Results.Text(node.ToJsonString(), "application/json");

    private static IResult Reply(LogService log, Outcome o)
    {
        log.Log(o.Changed ? LogLevel.Info : LogLevel.Debug, "api", o.Message);
        return Reply(o.Result);
    }

    private static IResult Deleted(LogService log, string what, string name)
    {
        log.Log(LogLevel.Info, "api", $"{what} '{name}' deleted");
        return Results.NoContent();
    }

    /// <summary>Constant-time comparison (hashes first, so the key length does not leak either).</summary>
    private static bool KeyEquals(string expected, string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
    }

    private static string Fingerprint(object o) => JsonSerializer.Serialize(o, Json);

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);

    /// <summary>A credential field: trimmed, and null when empty or not supplied (= keep the stored value, like the UI).</summary>
    private static string? Secret(Body? b, string name)
    {
        var v = b?.Str(name, allowNumber: true)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static void ApplyManagedBy(Body b, Action<string?> set)
    {
        var v = b.Str("managedBy");
        if (v == null) return; // not supplied = unchanged
        v = v.Trim();
        if (v.Length > 64) throw ApiException.BadRequest("'managedBy' must be at most 64 characters.");
        set(v.Length == 0 ? null : v); // "" clears it
    }

    private static string ValidName(string name)
    {
        if (name.Length > 200) throw ApiException.BadRequest("'name' must be at most 200 characters.");
        return name;
    }

    /// <summary>Upsert lookup: by id when one is given (an unknown id is a 404), else by name (case-insensitive; 409 if ambiguous).</summary>
    private static T? FindByIdOrName<T>(List<T> list, Func<T, string> idOf, Func<T, string> nameOf, string? id, string? name, string what) where T : class
    {
        if (!string.IsNullOrEmpty(id))
            return list.FirstOrDefault(x => idOf(x) == id) ?? throw new ApiException(404, $"{what} '{id}' not found.");
        if (string.IsNullOrWhiteSpace(name)) throw ApiException.BadRequest("Name missing (send 'name', or the 'id' of an existing " + what + ").");
        var matches = list.Where(x => nameOf(x).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (matches.Count > 1) throw new ApiException(409, $"Several {what}s are named '{name}'; address the one you mean by its id.");
        return matches.FirstOrDefault();
    }

    // ---------------------------------------------------------------- validation (hostnames, IPs, URLs)

    /// <summary>An FQDN as DNS knows it: dot-separated labels of letters, digits, '-' and '_' (a leading "*" label is a wildcard).</summary>
    private static bool IsHostname(string s)
    {
        if (s.Length is 0 or > 253) return false;
        var labels = s.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            var l = labels[i];
            if (l.Length is 0 or > 63) return false;
            if (l == "*") { if (i == 0 && labels.Length > 1) continue; return false; }
            if (l[0] == '-' || l[^1] == '-') return false;
            foreach (var ch in l)
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return false;
        }
        return true;
    }

    private static void RequireIp(string value, AddressFamily family, string field)
    {
        var syntaxOk = family == AddressFamily.InterNetwork ? value.Count(ch => ch == '.') == 3 : value.Contains(':');
        if (!syntaxOk || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != family)
            throw ApiException.BadRequest($"'{field}' must be a valid IPv{(family == AddressFamily.InterNetwork ? "4" : "6")} address.");
    }

    private static void RequireUrl(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw ApiException.BadRequest($"'{field}' must be an absolute http(s) URL.");
    }

    // ---------------------------------------------------------------- sources

    private static JsonObject SourceOut(SourceGroup g)
    {
        var o = (JsonObject)JsonSerializer.SerializeToNode(g, Json)!;
        o["managedBy"] = g.ManagedBy; // explicit null when the source is not managed
        // secrets are never returned: a credentialsSet flag says whether they are stored
        Strip(o, "unifi", "password");
        Strip(o, "fritzbox", "password");
        Strip(o, "unifiCloud", "apiKey");
        Strip(o, "push", "token");
        Strip(o, "matddns", "token");
        bool? set = g.Kind switch
        {
            SourceKind.Unifi => Has(g.Unifi?.Password),
            SourceKind.Fritzbox => Has(g.Fritzbox?.Password),
            SourceKind.UnifiCloud => Has(g.UnifiCloud?.ApiKey),
            SourceKind.Push => Has(g.Push?.Token),
            SourceKind.Matddns => Has(g.Matddns?.Token),
            _ => null // PublicIp, Static and Dns have no credentials
        };
        if (set != null) o["credentialsSet"] = set;
        return o;
    }

    private static void Strip(JsonObject o, string section, params string[] keys)
    {
        if (o[section] is not JsonObject s) return;
        foreach (var k in keys) s.Remove(k);
    }

    private static string SourceSettings(SourceGroup g) =>
        Fingerprint(new object?[] { g.Unifi, g.Static, g.Push, g.Dns, g.Fritzbox, g.UnifiCloud, g.Matddns });

    private static Outcome UpsertSource(AppConfig c, Body b)
    {
        var id = b.Str("id")?.Trim();
        var name = b.Str("name")?.Trim();
        var kindIn = b.EnumOf<SourceKind>("kind");
        var found = FindByIdOrName(c.Sources, x => x.Id, x => x.Name, id, name, "source");
        var isNew = found == null;
        if (found == null && kindIn == null)
            throw ApiException.BadRequest("kind missing (" + string.Join(", ", Enum.GetNames<SourceKind>()) + ").");
        if (found != null && kindIn != null && kindIn != found.Kind)
            throw ApiException.BadRequest($"The kind of an existing source cannot be changed (it is {found.Kind}); delete it and create a new one.");
        var g = found ?? new SourceGroup { Name = ValidName(name!), Kind = kindIn!.Value };

        var before = isNew ? null : Fingerprint(g);
        var settingsBefore = SourceSettings(g);

        if (!string.IsNullOrWhiteSpace(name)) g.Name = ValidName(name);
        ApplyManagedBy(b, v => g.ManagedBy = v);

        // interval: push is push-driven (never polled); the others need at least 15 s (the updater's cycle), default 60 s
        var interval = b.Int("intervalSeconds");
        if (g.Kind == SourceKind.Push) g.IntervalSeconds = 0;
        else if (interval is < 0 or (> 0 and < 15)) throw ApiException.BadRequest("'intervalSeconds' must be at least 15.");
        else if (interval is >= 15) g.IntervalSeconds = interval.Value;
        else if (isNew) g.IntervalSeconds = 60;

        ApplySourceSettings(g, b);
        ApplySourceEntries(c, g, b);

        // like the UI after saving settings: fetch again on the next poll (Static writes its value right away)
        if (!isNew && g.Kind != SourceKind.Static && SourceSettings(g) != settingsBefore)
            foreach (var e in g.Entries) e.LastChecked = null;

        if (isNew) c.Sources.Add(g);
        var changed = before != Fingerprint(g);
        return new Outcome(SourceOut(g), $"source '{g.Name}' {(isNew ? "created" : changed ? "updated" : "unchanged")}", changed);
    }

    private static void ApplySourceSettings(SourceGroup g, Body b)
    {
        SourceEntry Single(string label)
        {
            var e = g.Entries.FirstOrDefault();
            if (e == null) { e = new SourceEntry { Label = label }; g.Entries.Add(e); }
            return e;
        }

        switch (g.Kind)
        {
            case SourceKind.Unifi:
            {
                var s = b.Obj("unifi");
                var u = g.Unifi ??= new UnifiSettings();
                if (s?.Str("baseUrl") is { } url) u.BaseUrl = url.Trim();
                if (s?.Str("site") is { } site) u.Site = string.IsNullOrWhiteSpace(site) ? "default" : site.Trim();
                if (s?.Str("username") is { } user) u.Username = user;
                if (Secret(s, "password") is { } pass) u.Password = pass;
                if (s?.Bool("ignoreCertificate") is { } ic) u.IgnoreCertificate = ic;
                RequireUrl(u.BaseUrl, "unifi.baseUrl");
                // the WAN entries appear automatically on the first successful fetch
                break;
            }
            case SourceKind.Static:
            {
                var s = b.Obj("static");
                var st = g.Static ??= new StaticSettings();
                if (s?.Str("ip") is { } ip) st.Ip = ip.Trim();
                if (s?.Str("ipv6") is { } ip6) st.Ipv6 = ip6.Trim();
                if (st.Ip.Length == 0 && st.Ipv6.Length == 0)
                    throw ApiException.BadRequest("A static source needs 'static.ip' and/or 'static.ipv6'.");
                if (st.Ip.Length > 0) RequireIp(st.Ip, AddressFamily.InterNetwork, "static.ip");
                if (st.Ipv6.Length > 0) RequireIp(st.Ipv6, AddressFamily.InterNetworkV6, "static.ipv6");
                // the entry carries the value right away (as the UI does)
                var e = Single("Static IP");
                var v4 = st.Ip.Length == 0 ? null : st.Ip;
                var v6 = st.Ipv6.Length == 0 ? null : st.Ipv6;
                if (e.CurrentIp != v4 || e.CurrentIpv6 != v6 || e.LastChecked == null)
                {
                    e.CurrentIp = v4;
                    e.CurrentIpv6 = v6;
                    e.LastChecked = DateTime.UtcNow;
                    e.LastError = null;
                }
                break;
            }
            case SourceKind.Push:
            {
                var s = b.Obj("push");
                var p = g.Push ??= new PushSettings();
                if (Secret(s, "token") is { } token)
                {
                    if (token.Length < 16) throw ApiException.BadRequest("'push.token' must be at least 16 characters.");
                    p.Token = token;
                }
                else if (string.IsNullOrEmpty(p.Token)) p.Token = PushReceiver.NewToken();
                Single("Pushed IP");
                break;
            }
            case SourceKind.Dns:
            {
                var s = b.Obj("dns");
                var d = g.Dns ??= new DnsSettings();
                if (s?.Str("hostname") is { } host) d.Hostname = host.Trim().TrimEnd('.');
                if (!IsHostname(d.Hostname)) throw ApiException.BadRequest("'dns.hostname' must be a valid host name (e.g. xxxxxxxx.myfritz.net).");
                Single(d.Hostname).Label = d.Hostname;
                break;
            }
            case SourceKind.Fritzbox:
            {
                var s = b.Obj("fritzbox");
                var f = g.Fritzbox ??= new FritzboxSettings();
                if (s?.Str("baseUrl") is { } url) f.BaseUrl = url.Trim();
                if (string.IsNullOrWhiteSpace(f.BaseUrl)) f.BaseUrl = DefaultFritzboxUrl;
                if (s?.Str("username") is { } user) f.Username = user;
                if (Secret(s, "password") is { } pass) f.Password = pass;
                if (s?.Bool("ignoreCertificate") is { } ic) f.IgnoreCertificate = ic;
                RequireUrl(f.BaseUrl, "fritzbox.baseUrl");
                Single("FRITZ!Box WAN");
                break;
            }
            case SourceKind.UnifiCloud:
            {
                var s = b.Obj("unifiCloud");
                var u = g.UnifiCloud ??= new UnifiCloudSettings();
                if (Secret(s, "apiKey") is { } key) u.ApiKey = key;
                // the gateways are the entries (label = name, interfaceName = host id)
                break;
            }
            case SourceKind.Matddns:
            {
                var s = b.Obj("matddns");
                var m = g.Matddns ??= new MatddnsLinkSettings();
                if (s?.Str("baseUrl") is { } url) m.BaseUrl = url.Trim();
                if (Secret(s, "token") is { } token) m.Token = token;
                RequireUrl(m.BaseUrl, "matddns.baseUrl");
                // the entries are mirrored from the peer on the first successful pull
                break;
            }
            default: // PublicIp: its single entry, exactly as the UI creates it
                Single("Public IP");
                break;
        }
    }

    /// <summary>
    /// <c>entries</c> is the complete list for the kinds whose entries are not derived from the settings (Unifi, UnifiCloud, Matddns):
    /// matched by id, else by interfaceName; entries that are not in the body are removed and dropped from the rules' failover lists.
    /// For the other kinds the engine owns the single entry, as in the UI, and the body's list is ignored.
    /// </summary>
    private static void ApplySourceEntries(AppConfig c, SourceGroup g, Body b)
    {
        if (g.Kind is not (SourceKind.Unifi or SourceKind.UnifiCloud or SourceKind.Matddns)) return;
        var items = b.Items("entries");
        if (items == null) return;

        var keep = new HashSet<string>();
        var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<SourceEntry>();
        foreach (var it in items)
        {
            var id = it.Str("id")?.Trim();
            var label = it.Str("label")?.Trim();
            var iface = it.Str("interfaceName")?.Trim();
            if (!string.IsNullOrEmpty(iface) && !interfaces.Add(iface))
                throw ApiException.BadRequest($"{it.Path}: interfaceName '{iface}' is listed twice.");

            SourceEntry? e;
            if (!string.IsNullOrEmpty(id))
                e = g.Entries.FirstOrDefault(x => x.Id == id) ?? throw ApiException.BadRequest($"{it.Path}: unknown entry id '{id}' in this source.");
            else if (!string.IsNullOrEmpty(iface))
                e = g.Entries.FirstOrDefault(x => string.Equals(x.InterfaceName, iface, StringComparison.OrdinalIgnoreCase));
            else
                throw ApiException.BadRequest($"{it.Path}: 'id' or 'interfaceName' is required to tell the entries apart.");

            if (e == null)
            {
                if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(iface))
                    throw ApiException.BadRequest($"{it.Path}: 'label' and 'interfaceName' are required for a new entry.");
                e = new SourceEntry { Label = label, InterfaceName = iface };
                fresh.Add(e);
            }
            else
            {
                if (!string.IsNullOrEmpty(label)) e.Label = label;
                if (!string.IsNullOrEmpty(iface)) e.InterfaceName = iface;
            }
            if (!keep.Add(e.Id)) throw ApiException.BadRequest($"{it.Path}: the entry is listed twice.");
        }

        var removed = g.Entries.Where(e => !keep.Contains(e.Id)).Select(e => e.Id).ToHashSet();
        g.Entries.RemoveAll(e => removed.Contains(e.Id));
        DropSourceRefs(c, removed);
        g.Entries.AddRange(fresh);
    }

    /// <summary>Removes source entries from the rules' failover lists (with their CNAME targets), as the UI does when an entry or source is deleted.</summary>
    private static void DropSourceRefs(AppConfig c, ICollection<string> entryIds)
    {
        if (entryIds.Count == 0) return;
        foreach (var r in c.Rules)
            for (var i = r.SourceEntryIdsInOrder.Count - 1; i >= 0; i--)
                if (entryIds.Contains(r.SourceEntryIdsInOrder[i]))
                {
                    r.SourceEntryIdsInOrder.RemoveAt(i);
                    if (i < r.CnameTargets.Count) r.CnameTargets.RemoveAt(i);
                }
    }

    private static string DeleteSource(AppConfig c, string id)
    {
        var g = c.Sources.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, $"source '{id}' not found.");
        c.Sources.Remove(g);
        DropSourceRefs(c, g.Entries.Select(e => e.Id).ToHashSet());
        return g.Name;
    }

    // ---------------------------------------------------------------- domain groups

    private static JsonObject DomainOut(DomainGroup g)
    {
        var o = (JsonObject)JsonSerializer.SerializeToNode(g, Json)!;
        o["managedBy"] = g.ManagedBy;
        // credentials are accepted but never returned: a credentialsSet flag says whether they are stored
        Strip(o, "cloudflare", "apiToken");
        Strip(o, "netcup", "customerNumber", "apiKey", "apiPassword");
        Strip(o, "hetzner", "apiToken");
        Strip(o, "goDaddy", "apiKey", "apiSecret");
        Strip(o, "dynDns", "updateUrl", "username", "password");
        Strip(o, "matddns", "token");
        o["credentialsSet"] = g.Kind switch
        {
            DomainKind.Cloudflare => Has(g.Cloudflare?.ApiToken),
            DomainKind.Netcup => Has(g.Netcup?.CustomerNumber) && Has(g.Netcup?.ApiKey) && Has(g.Netcup?.ApiPassword),
            DomainKind.Hetzner => Has(g.Hetzner?.ApiToken),
            DomainKind.GoDaddy => Has(g.GoDaddy?.ApiKey) && Has(g.GoDaddy?.ApiSecret),
            DomainKind.DynDns => Has(g.DynDns?.UpdateUrl),
            DomainKind.Matddns => Has(g.Matddns?.BaseUrl) && Has(g.Matddns?.Token),
            _ => false
        };
        return o;
    }

    private static Outcome UpsertDomain(AppConfig c, Body b)
    {
        var id = b.Str("id")?.Trim();
        var name = b.Str("name")?.Trim();
        var kindIn = b.EnumOf<DomainKind>("kind");
        var found = FindByIdOrName(c.Domains, x => x.Id, x => x.Name, id, name, "domain group");
        var isNew = found == null;
        if (found == null && kindIn == null)
            throw ApiException.BadRequest("kind missing (" + string.Join(", ", Enum.GetNames<DomainKind>()) + ").");
        if (found != null && kindIn != null && kindIn != found.Kind)
            throw ApiException.BadRequest($"The kind of an existing domain group cannot be changed (it is {found.Kind}); delete it and create a new one.");
        var g = found ?? new DomainGroup { Name = ValidName(name!), Kind = kindIn!.Value };

        var before = isNew ? null : Fingerprint(g);

        if (!string.IsNullOrWhiteSpace(name)) g.Name = ValidName(name);
        ApplyManagedBy(b, v => g.ManagedBy = v);
        ApplyDomainSettings(g, b);

        var items = b.Items("entries");
        if (items != null) ApplyDomainEntries(c, g, items);

        if (isNew) c.Domains.Add(g);
        var changed = before != Fingerprint(g);
        return new Outcome(DomainOut(g), $"domain group '{g.Name}' {(isNew ? "created" : changed ? "updated" : "unchanged")}", changed);
    }

    /// <summary>The kind's settings object. A credential that is omitted (or empty) keeps the stored one, like the UI's "leave empty = unchanged".</summary>
    private static void ApplyDomainSettings(DomainGroup g, Body b)
    {
        switch (g.Kind)
        {
            case DomainKind.DynDns:
            {
                var s = b.Obj("dynDns");
                var d = g.DynDns ??= new DynDnsSettings();
                if (Secret(s, "updateUrl") is { } url) d.UpdateUrl = url;
                if (Secret(s, "username") is { } user) d.Username = user;
                if (Secret(s, "password") is { } pass) d.Password = pass;
                if (d.UpdateUrl.Length > 0) RequireUrl(d.UpdateUrl, "dynDns.updateUrl");
                break;
            }
            case DomainKind.Cloudflare:
            {
                var s = b.Obj("cloudflare");
                var cf = g.Cloudflare ??= new CloudflareSettings();
                if (Secret(s, "apiToken") is { } token) cf.ApiToken = token;
                if (s?.Bool("allowDynamic") is { } ad) cf.AllowDynamic = ad;
                break;
            }
            case DomainKind.Hetzner:
            {
                var s = b.Obj("hetzner");
                var hz = g.Hetzner ??= new HetznerSettings();
                if (Secret(s, "apiToken") is { } token) hz.ApiToken = token;
                if (s?.Bool("allowDynamic") is { } ad) hz.AllowDynamic = ad;
                break;
            }
            case DomainKind.GoDaddy:
            {
                var s = b.Obj("goDaddy");
                var gd = g.GoDaddy ??= new GoDaddySettings();
                if (Secret(s, "apiKey") is { } key) gd.ApiKey = key;
                if (Secret(s, "apiSecret") is { } secret) gd.ApiSecret = secret;
                if (s?.Bool("allowDynamic") is { } ad) gd.AllowDynamic = ad;
                break;
            }
            case DomainKind.Matddns:
            {
                var s = b.Obj("matddns");
                var m = g.Matddns ??= new MatddnsLinkSettings();
                if (s?.Str("baseUrl") is { } url) m.BaseUrl = url.Trim();
                if (Secret(s, "token") is { } token) m.Token = token;
                if (m.BaseUrl.Length > 0) RequireUrl(m.BaseUrl, "matddns.baseUrl");
                break;
            }
            default: // Netcup
            {
                var s = b.Obj("netcup");
                var nc = g.Netcup ??= new NetcupSettings();
                if (Secret(s, "customerNumber") is { } customer) nc.CustomerNumber = customer;
                if (Secret(s, "apiKey") is { } key) nc.ApiKey = key;
                if (Secret(s, "apiPassword") is { } pass) nc.ApiPassword = pass;
                if (s?.Bool("allowDynamic") is { } ad) nc.AllowDynamic = ad;
                break;
            }
        }
    }

    private sealed record WantedRecord(DomainEntry? Existing, string Hostname, DnsRecordType Type, string? RecordName, string? Zone);

    /// <summary>
    /// <c>entries</c> is the complete record list of the group. An entry is matched by id, else by (hostname, type);
    /// a match keeps its id, an unmatched one is created, an existing one that is not listed is removed. Removing a
    /// record that a rule still uses is refused with 409 (the API never deletes a rule as a side effect: delete the
    /// rule first). The UI, in contrast, deletes the rules together with the record.
    /// </summary>
    private static void ApplyDomainEntries(AppConfig c, DomainGroup g, List<Body> items)
    {
        var wanted = new List<WantedRecord>();
        var keepIds = new HashSet<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            var (hostname, type, recordName, zone) = ParseRecord(g, it);
            var id = it.Str("id")?.Trim();
            DomainEntry? existing;
            if (!string.IsNullOrEmpty(id))
                existing = g.Entries.FirstOrDefault(e => e.Id == id) ?? throw ApiException.BadRequest($"{it.Path}: unknown record id '{id}' in this domain group.");
            else
                existing = g.Entries.FirstOrDefault(e => e.Type == type && e.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase));

            if (existing != null && !keepIds.Add(existing.Id)) throw ApiException.BadRequest($"{it.Path}: the record '{existing.Hostname}' is listed twice.");
            if (!keys.Add($"{type}|{hostname}")) throw ApiException.BadRequest($"{it.Path}: the record {hostname} ({type}) is listed twice.");
            wanted.Add(new WantedRecord(existing, hostname, type, recordName, zone));
        }

        var removed = g.Entries.Where(e => !keepIds.Contains(e.Id)).ToList();
        foreach (var e in removed)
        {
            var used = c.Rules.Where(r => r.DomainGroupId == g.Id && r.DomainEntryId == e.Id).Select(r => r.Id).ToList();
            if (used.Count > 0)
                throw new ApiException(409, $"The record {e.Hostname} ({e.Type}) cannot be removed from the domain group '{g.Name}': " +
                    $"rule {string.Join(", ", used)} still uses it. Delete the rule first (DELETE /api/v1/rules/<id>), then send the shorter entries list.");
        }

        g.Entries.RemoveAll(e => !keepIds.Contains(e.Id));
        foreach (var w in wanted)
        {
            if (w.Existing != null)
            {
                w.Existing.Hostname = w.Hostname;
                w.Existing.Type = w.Type;
                w.Existing.RecordName = w.RecordName;
                w.Existing.Domain = w.Zone;
            }
            else
                g.Entries.Add(new DomainEntry { Hostname = w.Hostname, Type = w.Type, RecordName = w.RecordName, Domain = w.Zone });
        }
    }

    /// <summary>
    /// One record of the body: <c>{id?, hostname, type, recordName?, domain?}</c>. The record rules are the UI's: a zone-based provider
    /// (Netcup, Cloudflare, Hetzner, GoDaddy) stores record + zone next to the FQDN and may have a CNAME; DynDNS and Matddns-peer groups
    /// store just the FQDN and can only set A/AAAA. Without <c>domain</c> the zone is the last two labels (what the updater derives too).
    /// </summary>
    private static (string Hostname, DnsRecordType Type, string? RecordName, string? Zone) ParseRecord(DomainGroup g, Body it)
    {
        var hostname = (it.Str("hostname") ?? "").Trim().TrimEnd('.');
        if (hostname.Length == 0) throw ApiException.BadRequest($"{it.Path}: 'hostname' (FQDN) missing.");
        if (!IsHostname(hostname)) throw ApiException.BadRequest($"{it.Path}: '{hostname}' is not a valid host name (FQDN).");
        var type = it.EnumOf<DnsRecordType>("type") ?? DnsRecordType.A;

        if (!g.IsZoneBased)
        {
            if (type == DnsRecordType.CNAME)
                throw ApiException.BadRequest($"{it.Path}: CNAME records are only supported by zone-based providers (Netcup, Cloudflare, Hetzner, GoDaddy), not by {g.Kind}.");
            return (hostname, type, null, null);
        }

        var zone = it.Str("domain")?.Trim().TrimEnd('.');
        var rec = it.Str("recordName")?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(zone))
        {
            if (!string.IsNullOrEmpty(rec)) throw ApiException.BadRequest($"{it.Path}: 'recordName' needs 'domain' (the zone) as well.");
            var (z, r) = NetcupClient.SplitHostname(hostname);
            return (hostname, type, r, z);
        }

        if (!IsHostname(zone)) throw ApiException.BadRequest($"{it.Path}: '{zone}' is not a valid domain/zone.");
        var apex = hostname.Equals(zone, StringComparison.OrdinalIgnoreCase);
        if (!apex && !hostname.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest($"{it.Path}: the hostname '{hostname}' is not inside the domain '{zone}'.");
        var derived = apex ? "@" : hostname[..^(zone.Length + 1)];
        if (string.IsNullOrEmpty(rec)) rec = derived;
        else if (!rec.Equals(derived, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest($"{it.Path}: recordName '{rec}' does not match the hostname '{hostname}' in the domain '{zone}' (expected '{derived}').");
        return (hostname, type, rec, zone);
    }

    private static string DeleteDomain(AppConfig c, string id)
    {
        var g = c.Domains.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, $"domain group '{id}' not found.");
        var used = c.Rules.Where(r => r.DomainGroupId == id).Select(r => r.Id).ToList();
        if (used.Count > 0)
            throw new ApiException(409, $"The domain group '{g.Name}' is still used by {used.Count} rule(s) ({string.Join(", ", used)}). " +
                "Delete the rules first (DELETE /api/v1/rules/<id>).");
        c.Domains.Remove(g);
        return g.Name;
    }

    // ---------------------------------------------------------------- rules

    private static JsonObject RuleOut(Rule r)
    {
        var o = (JsonObject)JsonSerializer.SerializeToNode(r, Json)!;
        o["managedBy"] = r.ManagedBy;
        return o;
    }

    private static Outcome UpsertRule(AppConfig c, Body b)
    {
        var id = b.Str("id")?.Trim();
        var groupIn = b.Str("domainGroupId")?.Trim();
        var entryIn = b.Str("domainEntryId")?.Trim();

        Rule? found;
        if (!string.IsNullOrEmpty(id))
            found = c.Rules.FirstOrDefault(x => x.Id == id && !x.Dynamic) ?? throw new ApiException(404, $"rule '{id}' not found.");
        else
        {
            if (string.IsNullOrEmpty(groupIn) || string.IsNullOrEmpty(entryIn))
                throw ApiException.BadRequest("'domainGroupId' and 'domainEntryId' are required (or the 'id' of an existing rule).");
            var matches = c.Rules.Where(x => !x.Dynamic && x.DomainGroupId == groupIn && x.DomainEntryId == entryIn).Take(2).ToList();
            if (matches.Count > 1) throw new ApiException(409, "Several rules target this record; address the one you mean by its id.");
            found = matches.FirstOrDefault();
        }
        var isNew = found == null;
        var r = found ?? new Rule();
        var before = isNew ? null : Fingerprint(r);

        // the record it writes: both ids must exist (and the record must belong to the group)
        var groupId = string.IsNullOrEmpty(groupIn) ? r.DomainGroupId : groupIn;
        var entryId = string.IsNullOrEmpty(entryIn) ? r.DomainEntryId : entryIn;
        var group = c.Domains.FirstOrDefault(d => d.Id == groupId) ?? throw ApiException.BadRequest($"domainGroupId '{groupId}' does not exist.");
        var record = group.Entries.FirstOrDefault(e => e.Id == entryId)
            ?? throw ApiException.BadRequest($"domainEntryId '{entryId}' is not a record of the domain group '{group.Name}'.");
        r.DomainGroupId = groupId;
        r.DomainEntryId = entryId;

        if (b.Bool("enabled") is { } enabled) r.Enabled = enabled;
        if (b.Bool("onChange") is { } onChange) r.OnChange = onChange;
        if (b.Int("intervalSeconds") is { } interval)
        {
            if (interval < 0 || interval is > 0 and < 15) throw ApiException.BadRequest("'intervalSeconds' must be 0 (off) or at least 15.");
            r.IntervalSeconds = interval;
        }
        if (b.Bool("validatePing") is { } ping) r.ValidatePing = ping;
        if (b.Bool("validateTcp") is { } tcp) r.ValidateTcp = tcp;
        if (b.Int("validationPort") is { } port)
        {
            if (port is < 1 or > 65535) throw ApiException.BadRequest("'validationPort' must be between 1 and 65535.");
            r.ValidationPort = port;
        }
        r.NormalizeTriggers(); // the UI's rules: validation needs a re-check interval, and one trigger stays on

        ApplyRuleSources(c, r, b);
        ApplyManagedBy(b, v => r.ManagedBy = v);

        if (isNew) c.Rules.Add(r);
        var changed = before != Fingerprint(r);
        return new Outcome(RuleOut(r), $"rule for {record.Hostname} ({record.Type}) {(isNew ? "created" : changed ? "updated" : "unchanged")}", changed);
    }

    /// <summary>The failover order. A source that stays in the list keeps its CNAME target; new ones get an empty target (as in the UI).</summary>
    private static void ApplyRuleSources(AppConfig c, Rule r, Body b)
    {
        var ids = b.Strings("sourceEntryIdsInOrder");
        var targets = b.Strings("cnameTargets")?.Select(t => t.Trim().TrimEnd('.')).ToList();
        if (targets != null)
            foreach (var t in targets)
                if (t.Length > 0 && !IsHostname(t)) throw ApiException.BadRequest($"cnameTargets: '{t}' is not a valid host name.");

        if (ids != null)
        {
            var known = c.Sources.SelectMany(s => s.Entries).Select(e => e.Id).ToHashSet();
            var seen = new HashSet<string>();
            var list = new List<string>();
            foreach (var sid in ids.Select(x => x.Trim()))
            {
                if (!known.Contains(sid)) throw ApiException.BadRequest($"sourceEntryIdsInOrder: unknown source entry '{sid}'.");
                if (!seen.Add(sid)) throw ApiException.BadRequest($"sourceEntryIdsInOrder: '{sid}' is listed twice.");
                list.Add(sid);
            }
            var oldTargets = new Dictionary<string, string>();
            for (var i = 0; i < r.SourceEntryIdsInOrder.Count; i++)
                oldTargets[r.SourceEntryIdsInOrder[i]] = i < r.CnameTargets.Count ? r.CnameTargets[i] : "";
            r.SourceEntryIdsInOrder = list;
            r.CnameTargets = targets ?? list.Select(s => oldTargets.TryGetValue(s, out var t) ? t : "").ToList();
        }
        else if (targets != null)
            r.CnameTargets = targets;

        if (targets != null && targets.Count != r.SourceEntryIdsInOrder.Count)
            throw ApiException.BadRequest("'cnameTargets' needs one entry per source in 'sourceEntryIdsInOrder'.");
        while (r.CnameTargets.Count < r.SourceEntryIdsInOrder.Count) r.CnameTargets.Add("");
    }

    private static string DeleteRule(AppConfig c, string id)
    {
        var r = c.Rules.FirstOrDefault(x => x.Id == id && !x.Dynamic) ?? throw new ApiException(404, $"rule '{id}' not found.");
        c.Rules.Remove(r);
        return r.Id;
    }
}
