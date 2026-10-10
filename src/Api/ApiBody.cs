using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matddns.Api;

/// <summary>An error that the management API answers as <c>{"error": "..."}</c> with the given HTTP status.</summary>
public sealed class ApiException : Exception
{
    public int Status { get; }
    public ApiException(int status, string message) : base(message) => Status = status;

    public static ApiException BadRequest(string message) => new(400, message);
}

/// <summary>
/// Typed, defensive access to a JSON request body. An absent property and an explicit <c>null</c> both read as
/// "not supplied" (null), which the upserts treat as "keep the stored value". A property of the wrong type is a 400.
/// Property names match case-insensitively; unknown properties are ignored (so a client can echo a GET result back).
/// </summary>
public sealed class Body
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly JsonObject _obj;

    public string Path { get; }

    private Body(JsonObject obj, string path)
    {
        _obj = obj;
        Path = path;
    }

    public static async Task<Body> ReadAsync(HttpRequest request)
    {
        string text;
        using (var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8))
            text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) throw ApiException.BadRequest("The request body is empty; send a JSON object.");
        JsonNode? node;
        try { node = JsonNode.Parse(text, NodeOptions); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw ApiException.BadRequest("The request body is not valid JSON: " + ex.Message);
        }
        if (node is not JsonObject obj) throw ApiException.BadRequest("The request body must be a JSON object.");
        return new Body(obj, "");
    }

    private string Name(string name) => Path.Length == 0 ? name : $"{Path}.{name}";

    private JsonNode? Get(string name) => _obj.TryGetPropertyValue(name, out var v) ? v : null;

    /// <summary>A string; null when not supplied. <paramref name="allowNumber"/> also accepts a JSON integer (e.g. a Netcup customer number).</summary>
    public string? Str(string name, bool allowNumber = false)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (allowNumber && jv.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }
        throw ApiException.BadRequest($"'{Name(name)}' must be a string.");
    }

    public int? Int(string name)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        throw ApiException.BadRequest($"'{Name(name)}' must be an integer.");
    }

    public bool? Bool(string name)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        throw ApiException.BadRequest($"'{Name(name)}' must be true or false.");
    }

    /// <summary>An enum member by name (case-insensitive; numbers are not accepted); null when not supplied or blank.</summary>
    public T? EnumOf<T>(string name) where T : struct, Enum
    {
        var s = Str(name)?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var names = Enum.GetNames<T>();
        var match = names.FirstOrDefault(n => n.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw ApiException.BadRequest($"'{Name(name)}' must be one of: {string.Join(", ", names)}.");
        return Enum.Parse<T>(match);
    }

    public Body? Obj(string name)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is JsonObject o) return new Body(o, Name(name));
        throw ApiException.BadRequest($"'{Name(name)}' must be an object.");
    }

    public List<Body>? Items(string name)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is not JsonArray arr) throw ApiException.BadRequest($"'{Name(name)}' must be an array.");
        var list = new List<Body>(arr.Count);
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject o) throw ApiException.BadRequest($"'{Name(name)}[{i}]' must be an object.");
            list.Add(new Body(o, $"{Name(name)}[{i}]"));
        }
        return list;
    }

    public List<string>? Strings(string name)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is not JsonArray arr) throw ApiException.BadRequest($"'{Name(name)}' must be an array of strings.");
        var list = new List<string>(arr.Count);
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue jv && jv.TryGetValue<string>(out var s)) list.Add(s);
            else throw ApiException.BadRequest($"'{Name(name)}[{i}]' must be a string.");
        }
        return list;
    }
}
