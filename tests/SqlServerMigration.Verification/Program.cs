using System.Windows.Interop;
using SqlServerMigration.App;
using SqlServerMigration.App.Models;
using SqlServerMigration.App.Services;
using SqlServerMigration.App.ViewModels;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("三档参数", VerifyProfilesAsync),
    ("运行中模式切换", VerifyModeSwitchAsync),
    ("持续高 CPU 暂停与恢复", VerifyCpuHysteresisAsync),
    ("CPU 监控不可用时降级", VerifyCpuFallbackAsync),
    ("等待期间可取消", VerifyCancellationAsync),
    ("Windows 实际 CPU 采样", VerifyWindowsCpuMonitorAsync),
    ("连接信息脱敏", VerifyConnectionRedactionAsync),
    ("迁移计划指纹", VerifyPlanFingerprintAsync),
    ("校验警告语义", VerifyValidationWarningAsync),
    ("WPF 主窗口构造", VerifyWindowCompositionAsync),
    ("界面默认低影响模式", VerifyViewModelDefaultAsync)
};

var failures = new List<string>();
foreach (var check in checks)
{
    try
    {
        await check.Run();
        Console.WriteLine($"PASS  {check.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{check.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {check.Name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"验收失败：{failures.Count} 项。");
    return 1;
}

Console.WriteLine($"全部通过：{checks.Count} 项；未连接或写入 SQL Server。");
return 0;

static Task VerifyProfilesAsync()
{
    var high = MigrationPerformanceProfile.For(MigrationPerformanceMode.HighSpeed);
    Equal(2, high.MaxConcurrentTables, "高速并发表数");
    Equal(50_000, high.BatchSize, "高速批大小");
    Equal(92d, high.PauseCpuThresholdPercent, "高速暂停阈值");
    Equal(82d, high.ResumeCpuThresholdPercent, "高速恢复阈值");
    Equal(2, high.MaxDegreeOfParallelism, "高速 MAXDOP");
    Equal(TimeSpan.Zero, high.InterBatchDelay, "高速批间延迟");

    var balanced = MigrationPerformanceProfile.For(MigrationPerformanceMode.Balanced);
    Equal(1, balanced.MaxConcurrentTables, "平衡并发表数");
    Equal(25_000, balanced.BatchSize, "平衡批大小");
    Equal(82d, balanced.PauseCpuThresholdPercent, "平衡暂停阈值");
    Equal(72d, balanced.ResumeCpuThresholdPercent, "平衡恢复阈值");
    Equal(1, balanced.MaxDegreeOfParallelism, "平衡 MAXDOP");
    Equal(TimeSpan.FromMilliseconds(50), balanced.InterBatchDelay, "平衡批间延迟");

    var low = MigrationPerformanceProfile.For(MigrationPerformanceMode.LowImpact);
    Equal(1, low.MaxConcurrentTables, "低影响并发表数");
    Equal(10_000, low.BatchSize, "低影响批大小");
    Equal(72d, low.PauseCpuThresholdPercent, "低影响暂停阈值");
    Equal(62d, low.ResumeCpuThresholdPercent, "低影响恢复阈值");
    Equal(1, low.MaxDegreeOfParallelism, "低影响 MAXDOP");
    Equal(TimeSpan.FromMilliseconds(250), low.InterBatchDelay, "低影响批间延迟");
    return Task.CompletedTask;
}

static async Task VerifyModeSwitchAsync()
{
    var monitor = new SequenceCpuMonitor(50, 50);
    var controller = new MigrationThrottleController(
        monitor,
        MigrationPerformanceMode.LowImpact,
        TimeSpan.FromMilliseconds(1),
        sustainedSampleCount: 2);

    var first = await controller.WaitForNextBatchAsync();
    Equal(MigrationPerformanceMode.LowImpact, first.Mode, "首批模式");

    controller.SetMode(MigrationPerformanceMode.HighSpeed);
    await Task.Delay(2);
    var second = await controller.WaitForNextBatchAsync();
    Equal(MigrationPerformanceMode.HighSpeed, second.Mode, "下一批模式");
}

static async Task VerifyCpuHysteresisAsync()
{
    var monitor = new SequenceCpuMonitor(95, 96, 80, 81, 50);
    var controller = new MigrationThrottleController(
        monitor,
        MigrationPerformanceMode.HighSpeed,
        TimeSpan.FromMilliseconds(1),
        sustainedSampleCount: 2);
    var sawPause = false;
    controller.StatusChanged += (_, eventArgs) =>
        sawPause |= eventArgs.CurrentStatus.State == MigrationThrottleState.WaitingForCpu;

    var first = await controller.WaitForNextBatchAsync();
    Equal(MigrationPerformanceMode.HighSpeed, first.Mode, "高压首样不应暂停当前批");
    await Task.Delay(2);
    var afterRecovery = await controller.WaitForNextBatchAsync();

    True(sawPause, "连续高 CPU 后没有进入暂停状态");
    Equal(MigrationPerformanceMode.HighSpeed, afterRecovery.Mode, "恢复后模式");
    True(!controller.IsPaused, "连续低 CPU 后仍处于暂停状态");
}

static async Task VerifyCpuFallbackAsync()
{
    var controller = new MigrationThrottleController(
        new UnavailableCpuMonitor(),
        MigrationPerformanceMode.HighSpeed,
        TimeSpan.FromMilliseconds(1),
        sustainedSampleCount: 2);

    var profile = await controller.WaitForNextBatchAsync();
    Equal(MigrationPerformanceMode.LowImpact, profile.Mode, "降级模式");
    True(controller.IsUsingFallback, "没有显式标记 CPU 监控降级");
}

static async Task VerifyCancellationAsync()
{
    var controller = new MigrationThrottleController(
        new SequenceCpuMonitor(99, 99, 99, 99, 99),
        MigrationPerformanceMode.HighSpeed,
        TimeSpan.FromMilliseconds(2),
        sustainedSampleCount: 2);

    _ = await controller.WaitForNextBatchAsync();
    await Task.Delay(3);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    try
    {
        _ = await controller.WaitForNextBatchAsync(cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        return;
    }

    throw new InvalidOperationException("CPU 等待没有响应取消请求。");
}

static async Task VerifyWindowsCpuMonitorAsync()
{
    var monitor = new WindowsSystemCpuMonitor();
    var first = await monitor.SampleAsync();
    Equal(CpuMonitorStatus.WarmingUp, first.Status, "Windows CPU 首样状态");
    await Task.Delay(100);
    var second = await monitor.SampleAsync();
    Equal(CpuMonitorStatus.Available, second.Status, "Windows CPU 次样状态");
    True(second.TotalCpuUsagePercent is >= 0 and <= 100, "CPU 百分比超出 0-100");
}

static Task VerifyConnectionRedactionAsync()
{
    var settings = new SqlConnectionSettings
    {
        Server = "localhost\\SQL2025",
        Database = "SourceDb",
        AuthenticationMode = SqlAuthenticationMode.SqlServer,
        UserName = "migration_user",
        Password = "do-not-print-this"
    };

    var text = settings.ToString();
    True(!text.Contains(settings.Password, StringComparison.Ordinal), "ToString 泄露密码");
    True(!text.Contains(settings.UserName, StringComparison.Ordinal), "ToString 泄露用户名");
    return Task.CompletedTask;
}

static Task VerifyPlanFingerprintAsync()
{
    var plan = CreatePlan("TargetDb");
    var same = CreatePlan("TargetDb");
    var different = CreatePlan("AnotherTargetDb");
    Equal(CheckpointStore.CreatePlanFingerprint(plan), CheckpointStore.CreatePlanFingerprint(same), "同计划指纹");
    True(
        !string.Equals(
            CheckpointStore.CreatePlanFingerprint(plan),
            CheckpointStore.CreatePlanFingerprint(different),
            StringComparison.Ordinal),
        "不同目标库生成了相同指纹");
    return Task.CompletedTask;
}

static async Task VerifyViewModelDefaultAsync()
{
    using var viewModel = new MainViewModel();
    Equal(MigrationPerformanceMode.LowImpact, viewModel.SelectedPerformanceMode, "界面默认选择");
    Equal(MigrationPerformanceMode.LowImpact, viewModel.EffectivePerformanceMode, "界面默认生效模式");
    True(viewModel.IsLowImpactSelected, "低影响模式卡片未选中");
    True(!viewModel.IsStaticSourceConfirmed, "静态源确认不应默认勾选");

    viewModel.IsStaticSourceConfirmed = true;
    viewModel.SourceDatabaseName = "SourceA";
    True(!viewModel.IsStaticSourceConfirmed, "更换源库后没有清除静态源确认");
    viewModel.IsStaticSourceConfirmed = true;
    viewModel.TargetDatabaseName = "TargetA";
    True(viewModel.IsStaticSourceConfirmed, "仅更换目标库不应清除静态源确认");

    await viewModel.SelectPerformanceModeCommand.ExecuteAsync(MigrationPerformanceMode.Balanced);
    Equal(MigrationPerformanceMode.Balanced, viewModel.SelectedPerformanceMode, "界面切换平衡模式");
    Equal(MigrationPerformanceMode.Balanced, viewModel.EffectivePerformanceMode, "平衡模式空闲时应立即生效");

    await viewModel.SelectPerformanceModeCommand.ExecuteAsync(MigrationPerformanceMode.HighSpeed);
    Equal(MigrationPerformanceMode.HighSpeed, viewModel.SelectedPerformanceMode, "界面切换高速模式");
    Equal(MigrationPerformanceMode.HighSpeed, viewModel.EffectivePerformanceMode, "高速模式空闲时应立即生效");
}

static Task VerifyValidationWarningAsync()
{
    var warningOnly = new ValidationReport
    {
        Tables =
        [
            new TableValidationResult
            {
                TableName = "dbo.Sample",
                SourceRowCount = 10,
                TargetRowCount = 10,
                QuickChecksumRequested = true,
                Status = TableValidationStatus.QuickChecksumUnavailable,
                Detail = "verification warning"
            }
        ]
    };
    True(warningOnly.IsSuccessful, "行数一致但快速校验不适用时不应伪装成数据不一致");

    var failed = new ValidationReport
    {
        Tables =
        [
            new TableValidationResult
            {
                TableName = "dbo.Sample",
                SourceRowCount = 10,
                TargetRowCount = 9,
                QuickChecksumRequested = false,
                Status = TableValidationStatus.Failed,
                Detail = "verification mismatch"
            }
        ]
    };
    True(!failed.IsSuccessful, "行数不一致不能标记成功");
    return Task.CompletedTask;
}

static async Task VerifyWindowCompositionAsync()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            var window = new MainWindow(operations: null);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            True(handle != IntPtr.Zero, "未能创建 WPF 主窗口句柄");
            Equal("SQL Server 2025 → 2019 数据迁移", window.Title, "主窗口标题");
            True(window.Width <= 1280 && window.Height <= 700, "主窗口尺寸超过 1366×768 验收范围");
            window.Close();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
}

static MigrationPlan CreatePlan(string targetDatabaseName) => new()
{
    Source = new SqlConnectionSettings { Server = "localhost\\SQL2025", Database = "SourceDb" },
    Target = new SqlConnectionSettings { Server = "localhost\\SQL2019", Database = targetDatabaseName },
    TargetDatabaseName = targetDatabaseName,
    OutputDirectory = "not-written-by-this-test"
};

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}：期望 {expected}，实际 {actual}。");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class SequenceCpuMonitor(params double[] values) : ISystemCpuMonitor
{
    private readonly double[] _values = values.Length > 0 ? values : [50d];
    private int _index;

    public ValueTask<SystemCpuSample> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Math.Min(Interlocked.Increment(ref _index) - 1, _values.Length - 1);
        return ValueTask.FromResult(new SystemCpuSample(
            CpuMonitorStatus.Available,
            _values[index],
            "verification sample",
            DateTimeOffset.UtcNow));
    }
}

file sealed class UnavailableCpuMonitor : ISystemCpuMonitor
{
    public ValueTask<SystemCpuSample> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SystemCpuSample(
            CpuMonitorStatus.Unavailable,
            null,
            "verification unavailable",
            DateTimeOffset.UtcNow));
    }
}
