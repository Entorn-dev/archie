using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Archie.Runner;

public static class ScannerSupervisor
{
    private const int SupervisorFailureExitCode = 125;
    public const int ContainmentUnavailableExitCode = 126;
    private const string UnixBootstrapReady = "ARCHIE_SCANNER_GROUP_READY";

    internal static (string Executable, string? AssemblyArgument) ResolveSelfInvocation(
        string processPath,
        string entryAssemblyPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var appHostAssembly = Path.ChangeExtension(processPath, ".dll");
        return string.Equals(Path.GetFullPath(appHostAssembly), Path.GetFullPath(entryAssemblyPath), comparison)
            ? (processPath, null)
            : (processPath, entryAssemblyPath);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var separator = Array.IndexOf(args, "--");
            if (separator < 0 || separator + 1 >= args.Length) return SupervisorFailureExitCode;
            var graceIndex = Array.IndexOf(args, "--grace-ms");
            if (graceIndex < 0 || graceIndex + 1 >= separator || !int.TryParse(args[graceIndex + 1], out var graceMilliseconds))
                return SupervisorFailureExitCode;
            var target = args[separator + 1];
            var targetArguments = args[(separator + 2)..];
            if (OperatingSystem.IsWindows())
                return await RunWindowsSupervisorAsync(target, targetArguments, TimeSpan.FromMilliseconds(graceMilliseconds));
            if (!OperatingSystem.IsLinux()) return await ContainmentUnavailableAsync();
            var unshareIndex = Array.IndexOf(args, "--linux-unshare");
            if (unshareIndex < 0 || unshareIndex + 1 >= separator) return await ContainmentUnavailableAsync();
            return await RunLinuxSupervisorAsync(args[unshareIndex + 1], target, targetArguments, TimeSpan.FromMilliseconds(graceMilliseconds));
        }
        catch
        {
            await Console.Error.WriteLineAsync("Trusted scanner supervisor failed.");
            return SupervisorFailureExitCode;
        }
    }

    public static async Task<int> RunBootstrapAsync(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        if (separator < 0 || separator + 1 >= args.Length) return SupervisorFailureExitCode;
        if (OperatingSystem.IsWindows())
        {
            var eventIndex = Array.IndexOf(args, "--release-event");
            if (eventIndex < 0 || eventIndex + 1 >= separator) return SupervisorFailureExitCode;
            return await RunWindowsBootstrapAsync(args[eventIndex + 1], args[separator + 1], args[(separator + 2)..]);
        }

        if (setsid() < 0) return SupervisorFailureExitCode;
        var marker = Encoding.ASCII.GetBytes($"{UnixBootstrapReady}\n");
        var output = Console.OpenStandardOutput();
        output.Write(marker);
        output.Flush();
        return Exec(args[separator + 1], args[(separator + 1)..]);
    }

    private static async Task<int> RunLinuxSupervisorAsync(string unshare, string target, string[] targetArguments, TimeSpan grace)
    {
        Process process;
        try { process = StartLinuxBootstrap(unshare, target, targetArguments); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return await ContainmentUnavailableAsync();
        }
        using (process)
        {
            var marker = await ReadControlLineAsync(process.StandardOutput.BaseStream);
            if (marker != UnixBootstrapReady)
            {
                await process.WaitForExitAsync();
                return await ContainmentUnavailableAsync();
            }

            return await ProxyAndSuperviseAsync(process, grace, async () =>
            {
                if (process.HasExited) return;
                kill(process.Id, SignalTerm);
                var exit = process.WaitForExitAsync();
                if (await Task.WhenAny(exit, Task.Delay(grace)) == exit)
                {
                    await exit;
                    return;
                }
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            });
        }
    }

    private static async Task<int> ContainmentUnavailableAsync()
    {
        await Console.Error.WriteLineAsync("Scanner containment is unavailable; Linux requires delegated user and PID namespaces.");
        return ContainmentUnavailableExitCode;
    }

    private static async Task<string?> ReadControlLineAsync(Stream stream)
    {
        using var line = new MemoryStream();
        var buffer = new byte[1];
        while (line.Length <= 128)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
            if (buffer[0] == (byte)'\n') return Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r');
            line.WriteByte(buffer[0]);
        }
        return null;
    }

    private static async Task<int> RunWindowsSupervisorAsync(string target, string[] targetArguments, TimeSpan grace)
    {
        using var job = CreateKillOnCloseJob();
        var eventName = $"archie-scanner-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var process = StartWindowsBootstrap(eventName, target, targetArguments);
        if (!AssignProcessToJobObject(job, process.SafeHandle))
        {
            try { process.Kill(); } catch { }
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        release.Set();
        return await ProxyAndSuperviseAsync(process, grace, () =>
        {
            if (!TerminateJobObject(job, 1) && Marshal.GetLastPInvokeError() != NoSuchProcess)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return Task.CompletedTask;
        });
    }

    private static Process StartLinuxBootstrap(string unshare, string target, IReadOnlyList<string> targetArguments)
    {
        var start = RedirectedStart(unshare, []);
        start.ArgumentList.Add("--user");
        start.ArgumentList.Add("--map-current-user");
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add("--fork");
        start.ArgumentList.Add("--kill-child=KILL");
        start.ArgumentList.Add("--mount-proc");
        AddBootstrapCommand(start, null, target, targetArguments);
        return Process.Start(start) ?? throw new InvalidOperationException();
    }

    private static Process StartWindowsBootstrap(string releaseEvent, string target, IReadOnlyList<string> targetArguments)
    {
        var self = CurrentSelfInvocation();
        var start = new ProcessStartInfo(self.Executable)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        AddBootstrapCommand(start, releaseEvent, target, targetArguments, includeHost: false);
        return Process.Start(start) ?? throw new InvalidOperationException();
    }

    private static void AddBootstrapCommand(
        ProcessStartInfo start,
        string? releaseEvent,
        string target,
        IReadOnlyList<string> targetArguments,
        bool includeHost = true)
    {
        var self = CurrentSelfInvocation();
        if (includeHost) start.ArgumentList.Add(self.Executable);
        if (self.AssemblyArgument is not null) start.ArgumentList.Add(self.AssemblyArgument);
        start.ArgumentList.Add("__scanner-bootstrap");
        if (releaseEvent is not null)
        {
            start.ArgumentList.Add("--release-event");
            start.ArgumentList.Add(releaseEvent);
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(target);
        foreach (var argument in targetArguments) start.ArgumentList.Add(argument);
    }

    private static (string Executable, string? AssemblyArgument) CurrentSelfInvocation() =>
        ResolveSelfInvocation(
            Environment.ProcessPath ?? throw new InvalidOperationException(),
            Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException());

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsBootstrapAsync(string releaseEvent, string target, string[] targetArguments)
    {
        using (var release = EventWaitHandle.OpenExisting(releaseEvent)) release.WaitOne();
        var start = RedirectedStart(target, targetArguments);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var input = CopyInputAsync(process);
        var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        await process.WaitForExitAsync();
        try { process.StandardInput.Close(); } catch { }
        await Task.WhenAll(IgnorePipeClosure(output), IgnorePipeClosure(error));
        return process.ExitCode;
    }

    private static async Task<int> ProxyAndSuperviseAsync(Process process, TimeSpan grace, Func<Task> terminateOwnedUnit)
    {
        var input = CopyInputAsync(process);
        var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        var exit = process.WaitForExitAsync();
        if (await Task.WhenAny(exit, input) == input && !process.HasExited)
        {
            try { process.StandardInput.Close(); } catch { }
            await Task.WhenAny(exit, Task.Delay(grace));
        }
        await terminateOwnedUnit();
        await exit;
        await Task.WhenAll(IgnorePipeClosure(output), IgnorePipeClosure(error));
        return process.ExitCode;
    }

    private static async Task CopyInputAsync(Process process)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static async Task IgnorePipeClosure(Task task)
    {
        try { await task; }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static ProcessStartInfo RedirectedStart(string target, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(target)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static int Exec(string executable, IReadOnlyList<string> arguments)
    {
        var pointers = new IntPtr[arguments.Count + 1];
        try
        {
            for (var index = 0; index < arguments.Count; index++) pointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            unsafe
            {
                fixed (IntPtr* pointer = pointers) execv(executable, (IntPtr)pointer);
            }
            return SupervisorFailureExitCode;
        }
        finally
        {
            foreach (var pointer in pointers) if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, 9, pointer, (uint)size)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { Marshal.FreeHGlobal(pointer); }
        return job;
    }

    private const int SignalTerm = 15;
    private const int NoSuchProcess = 3;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
