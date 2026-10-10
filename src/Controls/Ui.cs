namespace Matddns.Controls;

/// <summary>Small helpers shared by the views.</summary>
public static class Ui
{
    /// <summary>CSS class of a status pill: "ok", "err" and "warn" are coloured, anything else is neutral.</summary>
    public static string Pill(string state) => state switch
    {
        "ok" => "pill--online",
        "err" => "pill--offline",
        "warn" => "pill--warn",
        _ => "pill--unknown"
    };
}
