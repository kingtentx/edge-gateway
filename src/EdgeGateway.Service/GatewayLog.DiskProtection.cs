using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayLog
{
    private readonly Func<string, long> _diskProbe;
    private readonly TimeProvider _clock;
    private long _diskFreeBytes = -1, _diskRejected, _diskCheckedAt, _bytesSinceDiskCheck;
    private int _diskPaused;
    private GatewayLoggingConfiguration? _diskPolicy;

    // Only the file worker calls this method. Acquisition threads never inspect the filesystem.
    private bool CanWriteToDisk(int bytes, GatewayLoggingConfiguration policy)
    {
        var now = _clock.GetTimestamp();
        if (!ReferenceEquals(policy, _diskPolicy) || _clock.GetElapsedTime(_diskCheckedAt, now) >= TimeSpan.FromSeconds(1))
        {
            _diskCheckedAt = now; _diskPolicy = policy; _bytesSinceDiskCheck = 0;
            try
            {
                var free = _diskProbe(_directory!);
                if (free < 0) throw new IOException("磁盘容量不可用。");
                Interlocked.Exchange(ref _diskFreeBytes, free);
            }
            catch (Exception exception) when (IsLoggingFailure(exception))
            {
                Interlocked.Exchange(ref _diskFreeBytes, -1);
                Volatile.Write(ref _diskPaused, 1);
                SetFileError("无法读取日志卷的可用空间，已暂停文件写入；运行记录仍可在界面查看。请检查目录权限或磁盘状态。");
                return false;
            }
        }
        var available = Interlocked.Read(ref _diskFreeBytes);
        if (available < 0) return false;
        var reserve = policy.MinimumFreeDiskSpaceMb * 1024L * 1024;
        if (Volatile.Read(ref _diskPaused) != 0) reserve += policy.DiskResumeMarginMb * 1024L * 1024;
        if (available - _bytesSinceDiskCheck - bytes < reserve)
        {
            Volatile.Write(ref _diskPaused, 1);
            SetFileError("日志磁盘空间保护已触发，文件写入暂停，本期间记录不补写；达到保护阈值加恢复余量后自动恢复。不会删除其他业务文件。");
            return false;
        }
        Volatile.Write(ref _diskPaused, 0);
        return true;
    }

    private static long ReadAvailableDiskBytes(string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = TrailingSeparator(Path.GetFullPath(directory));
        // Longest mount prefix matters when the data directory is a separate Linux volume.
        var drive = DriveInfo.GetDrives().Where(drive => path.StartsWith(
                TrailingSeparator(drive.RootDirectory.FullName), comparison))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length).FirstOrDefault();
        return drive?.AvailableFreeSpace ?? throw new IOException("找不到日志所在卷。");
    }
}
