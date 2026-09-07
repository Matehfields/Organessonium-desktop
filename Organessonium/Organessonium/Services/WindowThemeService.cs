using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Organessonium.Services;

public static class WindowThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window, string? theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = IsDarkTheme(theme);
        var darkValue = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref darkValue, sizeof(int));
        }

        var caption = dark ? ColorRef(16, 16, 16) : ColorRef(248, 248, 248);
        var text = dark ? ColorRef(242, 242, 242) : ColorRef(32, 32, 32);
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(uint));
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(uint));
    }

    public static string InitialTheme()
    {
        return IsWindowsLightTheme() ? "light" : "dark";
    }

    public static bool IsDarkTheme(string? theme)
    {
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsWindowsLightTheme();
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue ? intValue != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static uint ColorRef(byte r, byte g, byte b)
    {
        return (uint)(r | (g << 8) | (b << 16));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);
}
