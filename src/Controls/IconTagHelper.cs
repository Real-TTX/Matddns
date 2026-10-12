using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Matddns.Controls;

/// <summary>
/// Renders an icon of the page's inline sprite: <c>&lt;icon name="save" /&gt;</c> becomes <c>&lt;svg class="mat-icon"&gt;&lt;use href="#mat-i-save"&gt;</c>
/// (rules ICON-01, ICON-02). The name must exist in <c>matui-icons.svg</c>; a unit test checks every name the pages use.
/// </summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed partial class IconTagHelper : TagHelper
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>Icon name from the sprite (e.g. "save", "trash").</summary>
    public string Name { get; set; } = string.Empty;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!NameRegex().IsMatch(Name))
        {
            throw new InvalidOperationException($"'{Name}' is not an icon name.");
        }

        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-icon" : $"mat-icon {existingClass}");
        output.Attributes.SetAttribute("aria-hidden", "true");
        output.Attributes.SetAttribute("focusable", "false");
        output.Content.SetHtmlContent($"<use href=\"#mat-i-{Name}\"></use>");
    }
}
