using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Organessonium.Services.Sync;

public static class StartupManager
{
    public const string TaskId = "OrganessoniumSync";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Organessônium";
    public static bool Packaged
    {
        get { uint length = 0; return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700; }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (Packaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && command == CommandLine(Environment.ProcessPath!);
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (Packaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!enabled) { task.Disable(); return; }
            var state = await task.RequestEnableAsync();
            if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                throw new InvalidOperationException("A inicialização está desativada pelo Windows. Ative Organessônium em Aplicativos de Inicialização.");
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, CommandLine(Environment.ProcessPath!), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
        // Never modify StartupApproved or override a Task Manager decision.
    }

    internal static string CommandLine(string executable)
    {
        var command = "\"" + System.IO.Path.GetFullPath(executable) + "\" --background";
        if (command.Length > 260) throw new InvalidOperationException("O caminho de instalação é longo demais para inicialização do Windows.");
        return command;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr packageFullName);
}
