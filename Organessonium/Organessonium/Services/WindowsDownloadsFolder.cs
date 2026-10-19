using System.IO;
using System.Runtime.InteropServices;

namespace Organessonium.Services;

internal static class WindowsDownloadsFolder
{
    internal static string DefaultPath { get; } = FindDefaultPath();
    internal static string Resolve(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath) ? DefaultPath : configuredPath;

    private static string FindDefaultPath()
    {
        var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(ref folderId, 0x4000, IntPtr.Zero, out var path);
        try
        {
            if (result >= 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } value) return value;
        }
        finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
