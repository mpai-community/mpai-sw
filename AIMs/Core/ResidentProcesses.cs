using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mpai.Core;

// A PROGRAM AN AIM KEEPS RUNNING DIES WITH THE PROCESS THAT STARTED IT. An AIM that
// starts a program once and keeps it - a model loaded once instead of every turn -
// must not leave it behind when the Service stops or fails: an orphan holds the
// model's memory and, on a restart, a second copy is loaded beside it.
//
// On Windows the program is put in a job that the system closes, killing what is in
// it, when this process ends however it ends. Everywhere, it is also killed when
// this process exits normally; on Linux a systemd unit stops its whole group.
public static class ResidentProcesses
{
    private static readonly ConcurrentDictionary<int, Process> adopted = new();
    private static readonly IntPtr job = OperatingSystem.IsWindows() ? CreateKillOnCloseJob() : IntPtr.Zero;

    static ResidentProcesses() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var p in adopted.Values)
                try { if (!p.HasExited) p.Kill(true); } catch { }
        };

    public static void Adopt(Process process)
    {
        adopted[process.Id] = process;
        process.Exited += (_, _) => adopted.TryRemove(process.Id, out _);
        if (job != IntPtr.Zero)
            try { AssignProcessToJobObject(job, process.Handle); } catch { }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)size) ? handle : IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                     ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
