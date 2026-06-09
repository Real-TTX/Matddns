namespace Matddns.Models;

public enum SourceKind
{
    PublicIp,
    Unifi
}

public class SourceGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; } = SourceKind.PublicIp;
    public int IntervalSeconds { get; set; } = 60;
    public UnifiSettings? Unifi { get; set; }
    public List<SourceEntry> Entries { get; set; } = new();
}

public class UnifiSettings
{
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Site { get; set; } = "default";
    public bool IgnoreCertificate { get; set; } = true;
}

public class SourceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string? InterfaceName { get; set; }
    public string? CurrentIp { get; set; }
    public DateTime? LastChecked { get; set; }
    public string? LastError { get; set; }
}
