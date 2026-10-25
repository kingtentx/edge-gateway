using System.ComponentModel;
using System.Runtime.InteropServices;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Hosting;

/// <summary>On-demand host measurements. Native/disk calls run off the UI thread; no sampling loop.</summary>
public sealed class ServerResourceMonitor : IServerResourceMonitor
{
    private readonly object _sync = new();
    private CpuTimes? _previous;
    private readonly Func<CpuTimes> _cpu;
    private readonly Func<ServerMemoryUsage> _memory;
    private readonly Func<IReadOnlyList<ServerDiskUsage>> _disks;

    public ServerResourceMonitor() : this(ReadCpu, ReadMemory, ReadDisks) { }
    internal ServerResourceMonitor(Func<CpuTimes> cpu, Func<ServerMemoryUsage> memory,
        Func<IReadOnlyList<ServerDiskUsage>> disks) { _cpu = cpu; _memory = memory; _disks = disks; }

    public Task<ServerResourceSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new List<string>();
            double? cpu = null;
            ServerMemoryUsage? memory = null;
            IReadOnlyList<ServerDiskUsage> disks = [];
            try
            {
                var current = _cpu();
                cpu = CalculateCpu(_previous, current);
                _previous = current;
            }
            catch (Exception exception) { _previous = null; errors.Add("CPU：" + exception.Message); }
            try { memory = _memory(); }
            catch (Exception exception) { errors.Add("内存：" + exception.Message); }
            try { disks = _disks(); }
            catch (Exception exception) { errors.Add("磁盘：" + exception.Message); }
            cancellationToken.ThrowIfCancellationRequested();
            return new ServerResourceSnapshot(DateTimeOffset.UtcNow, Environment.MachineName, Environment.ProcessorCount,
                cpu, memory, disks, errors);
        }
    }, cancellationToken);

    internal static double? CalculateCpu(CpuTimes? previous, CpuTimes current)
    {
        if (previous is not { } old || current.Idle < old.Idle || current.Kernel < old.Kernel || current.User < old.User) return null;
        // Windows kernel time includes idle time. Sum deltas across processors, without dividing again by core count.
        var total = (double)(current.Kernel - old.Kernel) + (current.User - old.User);
        var idle = (double)(current.Idle - old.Idle);
        return total <= 0 || idle > total ? null : Math.Clamp(100d * (total - idle) / total, 0, 100);
    }

    private static CpuTimes ReadCpu()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前仅支持 Windows 整机 CPU 监控。");
        if (GetActiveProcessorCount(ushort.MaxValue) > 64)
            throw new NotSupportedException("超过 64 个逻辑处理器的整机 CPU 监控暂不可用。");
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(idle, kernel, user);
    }

    private static ServerMemoryUsage ReadMemory()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前仅支持 Windows 物理内存监控。");
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (memory.TotalPhysical == 0 || memory.AvailablePhysical > memory.TotalPhysical) throw new IOException("系统内存读数无效。");
        return new(checked((long)memory.TotalPhysical), checked((long)memory.AvailablePhysical));
    }

    private static IReadOnlyList<ServerDiskUsage> ReadDisks()
    {
        var result = new List<ServerDiskUsage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var total = drive.TotalSize;
                var free = drive.TotalFreeSpace;
                if (total <= 0 || free < 0 || free > total) throw new IOException("磁盘容量读数无效。");
                result.Add(new(drive.Name, total, free));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { result.Add(new(drive.Name, null, null, exception.Message)); }
        }
        return result;
    }

    internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
