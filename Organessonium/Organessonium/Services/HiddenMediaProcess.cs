using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Organessonium.Services;

// CreateNoWindow alone does not suppress the Windows GUI-process feedback cursor.
// Inherit only the three pipe handles, keep codec isolation and the media job limit.
internal sealed class HiddenMediaProcess : IDisposable
{
    internal Process Process { get; }
    internal AnonymousPipeServerStream Output { get; }
    internal StreamReader Error { get; }
    private readonly IDisposable _budget;

    private HiddenMediaProcess(Process process, AnonymousPipeServerStream output, AnonymousPipeServerStream error, IDisposable budget)
    { Process = process; Output = output; Error = new StreamReader(error); _budget = budget; }

    internal static HiddenMediaProcess Start(string executable, params string[] arguments)
    {
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var handles = new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
        IntPtr attributes = IntPtr.Zero, inherited = IntPtr.Zero;
        var attributesInitialized = false;
        Process? process = null;
        IDisposable? budget = null;
        var information = new ProcessInformation();
        try
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            attributesInitialized = true;
            inherited = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, inherited, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x00020002, inherited,
                (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx
            {
                Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000181, // USESTDHANDLES | FORCEOFFFEEDBACK | USESHOWWINDOW
                    Input = handles[0], Output = handles[1], Error = handles[2] },
                Attributes = attributes
            };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            const uint flags = 0x08080004; // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, null, ref startup, out information))
                throw new Win32Exception();
            output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle(); input.DisposeLocalCopyOfClientHandle();
            process = Process.GetProcessById(information.Id);
            _ = process.Handle;
            budget = AppMemoryBudget.RegisterMediaWorker(process);
            if (ResumeThread(information.Thread) == uint.MaxValue) throw new Win32Exception();
            return new HiddenMediaProcess(process, output, error, budget);
        }
        catch
        {
            if (information.Process != IntPtr.Zero) TerminateProcess(information.Process, 1);
            process?.Dispose(); budget?.Dispose(); output.Dispose(); error.Dispose();
            throw;
        }
        finally
        {
            if (information.Thread != IntPtr.Zero) CloseHandle(information.Thread);
            if (information.Process != IntPtr.Zero) CloseHandle(information.Process);
            if (attributes != IntPtr.Zero)
            {
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (inherited != IntPtr.Zero) Marshal.FreeHGlobal(inherited);
        }
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose() { Error.Dispose(); Output.Dispose(); Process.Dispose(); _budget.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, Width, Height, Columns, Rows, Fill;
        internal uint Flags;
        internal short Show, ReservedSize;
        internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo Info; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal int Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
