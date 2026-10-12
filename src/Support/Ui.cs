using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Matddns.Support;

/// <summary>Small helpers for the markup of the MatUi toolkit: icons, the logo tile and the status pills.</summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite. The name must exist in <c>matui-icons.svg</c> (a unit test checks the pages).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>The logo tile of the toolkit: the accent of the app with its glyph (the toolkit's registry says which).</summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString($"<span class=\"mat-logo mat-logo--{size}\" role=\"img\" aria-label=\"{App.Name}\"><svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{App.Glyph}\"></use></svg></span>");

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string name)
        => string.Concat(name.Split([' ', '-', '_', '.', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>Text with the characters of HTML escaped, for the few places that build markup in C#.</summary>
    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>The classes of a status pill (TABLE-03): "ok", "err" and "warn" are coloured, "info" is blue, anything else is neutral.</summary>
    public static string Pill(string state) => state switch
    {
        "ok" => "mat-pill mat-pill--ok",
        "err" => "mat-pill mat-pill--error",
        "warn" => "mat-pill mat-pill--warn",
        "info" => "mat-pill mat-pill--info",
        _ => "mat-pill"
    };

    /// <summary>How the toolkit's registry knows Matddns (the accent itself comes from <c>data-app</c> in <c>matui.css</c>).</summary>
    public static class App
    {
        public const string Id = "matddns";
        public const string Name = "Matddns";
        public const string Tagline = "DynDNS client with failover";
        public const string Glyph = "globe";

        /// <summary>The accent as the toolkit registry has it; it colours the bar of the browser. A unit test compares it with <c>matui.css</c>.</summary>
        public const string Accent = "#4173d1";
    }
}
