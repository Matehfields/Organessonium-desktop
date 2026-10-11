using System.Text.RegularExpressions;
using Windows.UI.ViewManagement;

namespace Organessonium.Services;

public static class WindowsAccentService
{
    public const string DefaultColor = "#0078D4";

    public static UISettings? TryCreate()
    {
        try { return new UISettings(); }
        catch { return null; }
    }

    public static string Read(UISettings? settings = null)
    {
        try
        {
            var color = (settings ?? TryCreate())?.GetColorValue(UIColorType.Accent);
            return color.HasValue ? $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}" : DefaultColor;
        }
        catch { return DefaultColor; }
    }

    public static string NormalizeColor(string? value) =>
        Regex.IsMatch(value ?? "", "^#[0-9a-fA-F]{6}$") ? value!.ToUpperInvariant() : DefaultColor;

    public static string NormalizeMode(string? value) => value is "preset" or "custom" ? value : "system";
}
