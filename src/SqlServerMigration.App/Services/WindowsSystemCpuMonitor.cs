using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SqlServerMigration.App.Services;

/// <summary>
/// Reads total Windows CPU usage from the cumulative counters exposed by GetSystemTimes.
/// </summary>
public sealed class WindowsSystemCpuMonitor : ISystemCpuMonitor
{
    private readonly object _syncRoot = new();
    private ulong? _previousIdleTime;
    private ulong? _previousKernelTime;
    private ulong? _previousUserTime;

    /// <inheritdoc />
    public ValueTask<SystemCpuSample> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            return ValueTask.FromResult(ReadSample());
        }
    }

    private SystemCpuSample ReadSample()
    {
        var sampledAtUtc = DateTimeOffset.UtcNow;

        if (!OperatingSystem.IsWindows())
        {
            return new SystemCpuSample(
                CpuMonitorStatus.Unavailable,
                null,
                "当前操作系统不支持 Windows GetSystemTimes CPU 采样。",
                sampledAtUtc);
        }

        try
        {
            if (!GetSystemTimesNative(out var idleTime, out var kernelTime, out var userTime))
            {
                var errorCode = Marshal.GetLastWin32Error();
                var errorMessage = new Win32Exception(errorCode).Message;

                return new SystemCpuSample(
                    CpuMonitorStatus.Unavailable,
                    null,
                    $"GetSystemTimes 失败（Win32 {errorCode}：{errorMessage}）。",
                    sampledAtUtc);
            }

            var currentIdleTime = idleTime.ToUInt64();
            var currentKernelTime = kernelTime.ToUInt64();
            var currentUserTime = userTime.ToUInt64();

            if (!_previousIdleTime.HasValue ||
                !_previousKernelTime.HasValue ||
                !_previousUserTime.HasValue)
            {
                SaveBaseline(currentIdleTime, currentKernelTime, currentUserTime);
                return new SystemCpuSample(
                    CpuMonitorStatus.WarmingUp,
                    null,
                    "已记录 CPU 计数器基线，需要下一次采样才能计算使用率。",
                    sampledAtUtc);
            }

            if (currentIdleTime < _previousIdleTime.Value ||
                currentKernelTime < _previousKernelTime.Value ||
                currentUserTime < _previousUserTime.Value)
            {
                SaveBaseline(currentIdleTime, currentKernelTime, currentUserTime);
                return new SystemCpuSample(
                    CpuMonitorStatus.WarmingUp,
                    null,
                    "CPU 计数器已重置，正在重新建立采样基线。",
                    sampledAtUtc);
            }

            var idleDelta = currentIdleTime - _previousIdleTime.Value;
            var kernelDelta = currentKernelTime - _previousKernelTime.Value;
            var userDelta = currentUserTime - _previousUserTime.Value;
            var totalDelta = kernelDelta + userDelta;

            if (totalDelta == 0)
            {
                return new SystemCpuSample(
                    CpuMonitorStatus.WarmingUp,
                    null,
                    "CPU 采样间隔过短，正在等待有效计数器变化。",
                    sampledAtUtc);
            }

            SaveBaseline(currentIdleTime, currentKernelTime, currentUserTime);

            // GetSystemTimes includes idle time in the kernel counter.
            var busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
            var usagePercent = Math.Clamp(busyDelta * 100d / totalDelta, 0d, 100d);

            return new SystemCpuSample(
                CpuMonitorStatus.Available,
                usagePercent,
                "CPU 采样正常。",
                sampledAtUtc);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException)
        {
            return new SystemCpuSample(
                CpuMonitorStatus.Unavailable,
                null,
                $"GetSystemTimes 不可用：{exception.Message}",
                sampledAtUtc);
        }
    }

    private void SaveBaseline(ulong idleTime, ulong kernelTime, ulong userTime)
    {
        _previousIdleTime = idleTime;
        _previousKernelTime = kernelTime;
        _previousUserTime = userTime;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimesNative(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;
    }
}
