using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Organessonium.Services;

internal static class AppMemoryBudget
{
    internal const long LimitBytes = 1_000_000_000;
    internal const long AdmissionBytes = 800L * 1024 * 1024;
    private const uint JobMemoryLimit = 0x200;
    private static readonly object Sync = new();
    private static SafeFileHandle? _job;
    private static int[] _webViewIds = [];
    private static int[] _mediaWorkerIds = [];

    internal static IDisposable RegisterMediaWorker(Process process)
    {
        Initialize();
        lock (Sync)
        {
            if (!IsProcessInJob(process.Handle, _job!, out var attached)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!attached && !AssignProcessToJobObject(_job!, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _mediaWorkerIds = [.. _mediaWorkerIds, process.Id];
        }
        return new MediaWorkerRegistration(process.Id);
    }

    private sealed class MediaWorkerRegistration(int id) : IDisposable
    {
        public void Dispose()
        {
            lock (Sync) _mediaWorkerIds = _mediaWorkerIds.Where(value => value != id).ToArray();
        }
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_job != null) return;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = JobMemoryLimit }, JobMemory = (UIntPtr)LimitBytes };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using var process = Process.GetCurrentProcess();
                if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _job = job;
            }
            catch { job.Dispose(); throw; }
        }
    }

    internal static void RegisterWebViewProcesses(IEnumerable<int> processIds)
    {
        Initialize();
        var ids = processIds.Distinct().ToArray();
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!IsProcessInJob(process.Handle, _job!, out var attached)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!attached && !AssignProcessToJobObject(_job!, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (ArgumentException) { } // Process exited between the WebView notification and query.
        }
        Volatile.Write(ref _webViewIds, ids);
    }

    internal static long CommittedBytes()
    {
        using var current = Process.GetCurrentProcess();
        var bytes = current.PrivateMemorySize64;
        foreach (var id in Volatile.Read(ref _webViewIds).Concat(Volatile.Read(ref _mediaWorkerIds)).Distinct())
        {
            if (id == current.Id) continue;
            try { using var child = Process.GetProcessById(id); bytes += child.PrivateMemorySize64; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return bytes;
    }

    internal static long PeakCommittedBytes
    {
        get
        {
            Initialize();
            if (!QueryInformationJobObject(_job!, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return checked((long)limits.PeakJobMemory.ToUInt64());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out ExtendedLimits value, uint size, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
