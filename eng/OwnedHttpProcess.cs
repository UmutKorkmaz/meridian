using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Meridian.Tools
{
    // Only the private supervisor calls EstablishOwnership, before it launches any child.
    // Its Windows job handle is deliberately held until process exit; it is not inheritable.
    public static class OwnedHttpProcess
    {
        private static IntPtr supervisorJob;
        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;
        private const int NoSuchProcess = 3;

        public static string EstablishOwnership()
        {
            if (OperatingSystem.IsWindows())
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new JobLimits();
                limits.Basic.LimitFlags = KillOnJobClose;
                if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobLimits>()) ||
                    !AssignProcessToJobObject(job, GetCurrentProcess()))
                {
                    var error = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    throw new Win32Exception(error, "Cannot establish the private supervisor job; no child was launched.");
                }
                supervisorJob = job;
                return "windows-job";
            }
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("HTTP supervision supports Windows, Linux and macOS.");
            if (SetSession() < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return "unix-session";
        }

        // A durable PID/token receipt proves ownership even after the session leader exits.
        // Before that receipt, native session/group checks cover the setup/receipt race.
        public static bool TerminateUnixGroup(int supervisorPid, bool receiptValidated)
        {
            if (supervisorPid <= 1) throw new ArgumentOutOfRangeException(nameof(supervisorPid));
            if (!receiptValidated && (GetSession(supervisorPid) != supervisorPid || GetGroup(supervisorPid) != supervisorPid))
                return false;
            if (SendSignal(-supervisorPid, 9) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != NoSuchProcess) throw new Win32Exception(error, "Cannot terminate the owned HTTP process group.");
            }
            return true;
        }

        public static int Run(string executable, string[] arguments, string workingDirectory)
        {
            if (OperatingSystem.IsWindows() && supervisorJob == IntPtr.Zero)
                throw new InvalidOperationException("The supervisor must own its job before launching a child.");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start))
            using (var stdout = Console.OpenStandardOutput())
            using (var stderr = Console.OpenStandardError())
            {
                if (process == null) throw new InvalidOperationException("HTTP child did not start.");
                // Keep this supervisor alive while inherited inner pipes remain open,
                // independently of the immediate dotnet launcher's lifetime.
                var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                var errors = process.StandardError.BaseStream.CopyToAsync(stderr);
                var exit = process.WaitForExitAsync();
                Task.WhenAll(exit, output, errors).GetAwaiter().GetResult();
                return process.ExitCode;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct JobLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("libc", EntryPoint = "setsid", SetLastError = true)] private static extern int SetSession();
        [DllImport("libc", EntryPoint = "getsid", SetLastError = true)] private static extern int GetSession(int pid);
        [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)] private static extern int GetGroup(int pid);
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int SendSignal(int pid, int signal);
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref JobLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
