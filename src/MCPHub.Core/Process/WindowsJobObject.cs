using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiagProcess = System.Diagnostics.Process;

namespace MCPHub.Core.Process;

/// <summary>
/// A Windows Job Object configured to kill all assigned processes when the job handle closes — which
/// happens when MCPHub exits for any reason, including a crash or force-kill. This guarantees child
/// MCP servers don't outlive MCPHub even when the graceful shutdown hook never runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    public WindowsJobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose },
        };

        var length = Marshal.SizeOf(info);
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, pointer, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>Assigns a started process to the job so it is killed when the job closes.</summary>
    public bool AssignProcess(DiagProcess process)
    {
        if (_handle == IntPtr.Zero)
            return false;

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false; // process already exited
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle); // closing the last handle kills assigned processes
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
