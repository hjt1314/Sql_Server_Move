using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SqlServerMigration.App.Infrastructure;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMigrationUiOperations? _operations;
    private readonly SynchronizationContext? _uiContext;
    private readonly Dictionary<MigrationUiStage, MigrationStepItem> _stepByStage;
    private Func<MigrationConnectionSecrets>? _secretProvider;
    private CancellationTokenSource? _migrationCancellation;
    private string _machineName = "localhost";
    private string _sourceInstanceName = string.Empty;
    private string _sourceDatabaseName = string.Empty;
    private SqlAuthenticationMode _sourceAuthenticationMode = SqlAuthenticationMode.Windows;
    private string _sourceUserName = string.Empty;
    private string _targetInstanceName = string.Empty;
    private string _targetDatabaseName = string.Empty;
    private SqlAuthenticationMode _targetAuthenticationMode = SqlAuthenticationMode.Windows;
    private string _targetUserName = string.Empty;
    private bool _isStaticSourceConfirmed;
    private bool _sourceHasPassword;
    private bool _targetHasPassword;
    private bool _isPrimaryOperationBusy;
    private bool _isControlRequestBusy;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isCpuThrottled;
    private bool _isCancellationRequested;
    private MigrationPerformanceMode _selectedPerformanceMode = MigrationPerformanceMode.LowImpact;
    private MigrationPerformanceMode _effectivePerformanceMode = MigrationPerformanceMode.LowImpact;
    private bool _isModeChangePending;
    private string _modeStatusText = "当前：低影响模式";
    private string _statusBannerText = "请填写源端和目标端连接信息。";
    private string _statusBannerForeground = "#475569";
    private string _statusBannerBackground = "#F8FAFC";
    private string _inputGuidanceText = "请先填写两个数据库名称。";
    private string _inputGuidanceColor = "#64748B";
    private double _overallProgressPercent;
    private double? _cpuUsagePercent;
    private double? _rowsPerSecond;
    private TimeSpan? _estimatedRemaining;
    private long _completedRows;
    private long? _totalRows;
    private string? _currentObject;
    private bool _disposed;

    public MainViewModel(IMigrationUiOperations? operations = null)
    {
        _operations = operations;
        _uiContext = SynchronizationContext.Current;

        AuthenticationOptions =
        [
            new UiSelectionOption<SqlAuthenticationMode>("Windows 认证", SqlAuthenticationMode.Windows),
            new UiSelectionOption<SqlAuthenticationMode>("SQL Server 认证", SqlAuthenticationMode.SqlServer),
        ];

        Steps =
        [
            new MigrationStepItem(MigrationUiStage.Connection, "1  连接", "等待连接测试"),
            new MigrationStepItem(MigrationUiStage.Preflight, "2  预检", "等待兼容性检查"),
            new MigrationStepItem(MigrationUiStage.Schema, "3  架构", "等待创建对象"),
            new MigrationStepItem(MigrationUiStage.Data, "4  数据", "等待复制数据"),
            new MigrationStepItem(MigrationUiStage.Validation, "5  验证", "等待结果核对"),
        ];
        _stepByStage = Steps.ToDictionary(step => step.Stage);

        Logs = [];

        TestConnectionsCommand = new AsyncRelayCommand(
            _ => TestConnectionsAsync(),
            _ => CanTestConnections,
            HandleCommandException);
        RunPreflightCommand = new AsyncRelayCommand(
            _ => RunPreflightAsync(),
            _ => CanRunPreflight,
            HandleCommandException);
        StartMigrationCommand = new AsyncRelayCommand(
            _ => StartMigrationAsync(),
            _ => CanStartMigration,
            HandleCommandException);
        PauseResumeCommand = new AsyncRelayCommand(
            _ => TogglePauseAsync(),
            _ => CanPauseOrResume,
            HandleCommandException);
        CancelCommand = new AsyncRelayCommand(
            _ => CancelMigrationAsync(),
            _ => CanCancel,
            HandleCommandException);
        SelectPerformanceModeCommand = new AsyncRelayCommand(
            ChangePerformanceModeAsync,
            CanSelectPerformanceMode,
            HandleCommandException);

        if (_operations is not null)
        {
            _operations.ProgressChanged += OperationsOnProgressChanged;
            _operations.LogReceived += OperationsOnLogReceived;
        }

        UpdateInputGuidance();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<UiSelectionOption<SqlAuthenticationMode>> AuthenticationOptions { get; }

    public ObservableCollection<MigrationStepItem> Steps { get; }

    public ObservableCollection<MigrationLogEntry> Logs { get; }

    public AsyncRelayCommand TestConnectionsCommand { get; }

    public AsyncRelayCommand RunPreflightCommand { get; }

    public AsyncRelayCommand StartMigrationCommand { get; }

    public AsyncRelayCommand PauseResumeCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand SelectPerformanceModeCommand { get; }

    public string MachineName
    {
        get => _machineName;
        set => SetSourceConnectionProperty(ref _machineName, value);
    }

    public string SourceInstanceName
    {
        get => _sourceInstanceName;
        set => SetSourceConnectionProperty(ref _sourceInstanceName, value);
    }

    public string SourceDatabaseName
    {
        get => _sourceDatabaseName;
        set => SetSourceConnectionProperty(ref _sourceDatabaseName, value);
    }

    public SqlAuthenticationMode SourceAuthenticationMode
    {
        get => _sourceAuthenticationMode;
        set
        {
            if (!SetProperty(ref _sourceAuthenticationMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSourceSqlAuthentication));
            SourceConnectionInputChanged();
        }
    }

    public string SourceUserName
    {
        get => _sourceUserName;
        set => SetSourceConnectionProperty(ref _sourceUserName, value);
    }

    public string TargetInstanceName
    {
        get => _targetInstanceName;
        set => SetConnectionProperty(ref _targetInstanceName, value);
    }

    public string TargetDatabaseName
    {
        get => _targetDatabaseName;
        set => SetConnectionProperty(ref _targetDatabaseName, value);
    }

    public SqlAuthenticationMode TargetAuthenticationMode
    {
        get => _targetAuthenticationMode;
        set
        {
            if (!SetProperty(ref _targetAuthenticationMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTargetSqlAuthentication));
            ConnectionInputChanged();
        }
    }

    public string TargetUserName
    {
        get => _targetUserName;
        set => SetConnectionProperty(ref _targetUserName, value);
    }

    public bool IsStaticSourceConfirmed
    {
        get => _isStaticSourceConfirmed;
        set
        {
            if (SetProperty(ref _isStaticSourceConfirmed, value))
            {
                ResetStepsFrom(MigrationUiStage.Preflight);
                UpdateInputGuidance();
                RefreshCommands();
            }
        }
    }

    public bool IsSourceSqlAuthentication => SourceAuthenticationMode == SqlAuthenticationMode.SqlServer;

    public bool IsTargetSqlAuthentication => TargetAuthenticationMode == SqlAuthenticationMode.SqlServer;

    public string SourceServerAddress => BuildServerAddress(SourceInstanceName);

    public string TargetServerAddress => BuildServerAddress(TargetInstanceName);

    public bool IsPrimaryOperationBusy
    {
        get => _isPrimaryOperationBusy;
        private set
        {
            if (SetProperty(ref _isPrimaryOperationBusy, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(CanEditConnections));
                RefreshCommands();
            }
        }
    }

    public bool IsControlRequestBusy
    {
        get => _isControlRequestBusy;
        private set
        {
            if (SetProperty(ref _isControlRequestBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(CanEditConnections));
                RefreshCommands();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
            }
        }
    }

    public bool IsCpuThrottled
    {
        get => _isCpuThrottled;
        private set
        {
            if (SetProperty(ref _isCpuThrottled, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                RefreshCommands();
            }
        }
    }

    public bool IsCancellationRequested
    {
        get => _isCancellationRequested;
        private set
        {
            if (SetProperty(ref _isCancellationRequested, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsWorking => IsPrimaryOperationBusy || IsRunning;

    public bool CanEditConnections => !IsWorking;

    public string PauseButtonText => IsCpuThrottled ? "CPU 等待" : IsPaused ? "继续" : "暂停";

    public MigrationPerformanceMode SelectedPerformanceMode
    {
        get => _selectedPerformanceMode;
        private set
        {
            if (!SetProperty(ref _selectedPerformanceMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsHighSpeedSelected));
            OnPropertyChanged(nameof(IsBalancedSelected));
            OnPropertyChanged(nameof(IsLowImpactSelected));
            OnPropertyChanged(nameof(SelectedModeDisplayName));
        }
    }

    public MigrationPerformanceMode EffectivePerformanceMode
    {
        get => _effectivePerformanceMode;
        private set
        {
            if (SetProperty(ref _effectivePerformanceMode, value))
            {
                OnPropertyChanged(nameof(EffectiveModeDisplayName));
            }
        }
    }

    public bool IsHighSpeedSelected => SelectedPerformanceMode == MigrationPerformanceMode.HighSpeed;

    public bool IsBalancedSelected => SelectedPerformanceMode == MigrationPerformanceMode.Balanced;

    public bool IsLowImpactSelected => SelectedPerformanceMode == MigrationPerformanceMode.LowImpact;

    public string SelectedModeDisplayName => GetModeDisplayName(SelectedPerformanceMode);

    public string EffectiveModeDisplayName => GetModeDisplayName(EffectivePerformanceMode);

    public bool IsModeChangePending
    {
        get => _isModeChangePending;
        private set
        {
            if (SetProperty(ref _isModeChangePending, value))
            {
                OnPropertyChanged(nameof(ModeStatusColor));
            }
        }
    }

    public string ModeStatusText
    {
        get => _modeStatusText;
        private set => SetProperty(ref _modeStatusText, value);
    }

    public string ModeStatusColor => IsModeChangePending ? "#B45309" : "#475569";

    public string StatusBannerText
    {
        get => _statusBannerText;
        private set => SetProperty(ref _statusBannerText, value);
    }

    public string StatusBannerForeground
    {
        get => _statusBannerForeground;
        private set => SetProperty(ref _statusBannerForeground, value);
    }

    public string StatusBannerBackground
    {
        get => _statusBannerBackground;
        private set => SetProperty(ref _statusBannerBackground, value);
    }

    public string InputGuidanceText
    {
        get => _inputGuidanceText;
        private set => SetProperty(ref _inputGuidanceText, value);
    }

    public string InputGuidanceColor
    {
        get => _inputGuidanceColor;
        private set => SetProperty(ref _inputGuidanceColor, value);
    }

    public double OverallProgressPercent
    {
        get => _overallProgressPercent;
        private set
        {
            if (SetProperty(ref _overallProgressPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public double? CpuUsagePercent
    {
        get => _cpuUsagePercent;
        private set
        {
            if (SetProperty(ref _cpuUsagePercent, value))
            {
                OnPropertyChanged(nameof(CpuUsageText));
            }
        }
    }

    public double? RowsPerSecond
    {
        get => _rowsPerSecond;
        private set
        {
            if (SetProperty(ref _rowsPerSecond, value))
            {
                OnPropertyChanged(nameof(RowsPerSecondText));
            }
        }
    }

    public TimeSpan? EstimatedRemaining
    {
        get => _estimatedRemaining;
        private set
        {
            if (SetProperty(ref _estimatedRemaining, value))
            {
                OnPropertyChanged(nameof(EstimatedRemainingText));
            }
        }
    }

    public long CompletedRows
    {
        get => _completedRows;
        private set
        {
            if (SetProperty(ref _completedRows, value))
            {
                OnPropertyChanged(nameof(RowsText));
            }
        }
    }

    public long? TotalRows
    {
        get => _totalRows;
        private set
        {
            if (SetProperty(ref _totalRows, value))
            {
                OnPropertyChanged(nameof(RowsText));
            }
        }
    }

    public string? CurrentObject
    {
        get => _currentObject;
        private set
        {
            if (SetProperty(ref _currentObject, value))
            {
                OnPropertyChanged(nameof(CurrentObjectText));
            }
        }
    }

    public string CpuUsageText => CpuUsagePercent is null ? "--" : $"{CpuUsagePercent:0}%";

    public string RowsPerSecondText => RowsPerSecond is null
        ? "--"
        : $"{RowsPerSecond:N0} 行/秒";

    public string ProgressText => $"{OverallProgressPercent:0.0}%";

    public string EstimatedRemainingText => EstimatedRemaining is null
        ? "--"
        : FormatDuration(EstimatedRemaining.Value);

    public string RowsText => TotalRows is > 0
        ? $"{CompletedRows:N0} / {TotalRows:N0}"
        : CompletedRows > 0 ? $"{CompletedRows:N0}" : "--";

    public string CurrentObjectText => string.IsNullOrWhiteSpace(CurrentObject) ? "尚未开始" : CurrentObject;

    public bool HasLogs => Logs.Count > 0;

    private bool CanTestConnections =>
        !IsPrimaryOperationBusy && !IsRunning && ValidateConnectionInput() is null;

    private bool CanRunPreflight =>
        !IsPrimaryOperationBusy &&
        !IsRunning &&
        IsStaticSourceConfirmed &&
        _stepByStage[MigrationUiStage.Connection].Status == MigrationStepStatus.Succeeded &&
        ValidateConnectionInput() is null;

    private bool CanStartMigration =>
        !IsPrimaryOperationBusy &&
        !IsRunning &&
        IsStaticSourceConfirmed &&
        _stepByStage[MigrationUiStage.Preflight].Status == MigrationStepStatus.Succeeded &&
        ValidateConnectionInput() is null;

    private bool CanPauseOrResume =>
        IsRunning && !IsCpuThrottled && !IsControlRequestBusy && !IsCancellationRequested;

    private bool CanCancel => IsRunning && !IsCancellationRequested;

    public void SetSecretProvider(Func<MigrationConnectionSecrets> secretProvider)
    {
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
    }

    public void NotifySecretPresence(MigrationEndpointRole role, bool hasPassword)
    {
        ref bool field = ref (role == MigrationEndpointRole.Source
            ? ref _sourceHasPassword
            : ref _targetHasPassword);

        if (field == hasPassword)
        {
            return;
        }

        field = hasPassword;
        if (role == MigrationEndpointRole.Source)
        {
            SourceConnectionInputChanged();
        }
        else
        {
            ConnectionInputChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _migrationCancellation?.Cancel();
        _migrationCancellation?.Dispose();
        _migrationCancellation = null;

        if (_operations is not null)
        {
            _operations.ProgressChanged -= OperationsOnProgressChanged;
            _operations.LogReceived -= OperationsOnLogReceived;
        }
    }

    private async Task TestConnectionsAsync()
    {
        if (!TryBeginRealOperation("连接测试"))
        {
            return;
        }

        IsPrimaryOperationBusy = true;
        ResetStepsFrom(MigrationUiStage.Connection);
        SetStepStatus(MigrationUiStage.Connection, MigrationStepStatus.Running, "正在测试源端与目标端");
        SetBanner("正在测试两个 SQL Server 实例的连接……", BannerKind.Information);
        AppendLog(MigrationLogLevel.Information, "开始测试源端和目标端连接。未记录密码。 ");

        try
        {
            using MigrationConnectionRequest request = CreateConnectionRequest();
            MigrationUiOperationResult result = await _operations!.TestConnectionsAsync(request, CancellationToken.None);

            if (result.Succeeded)
            {
                SetStepStatus(MigrationUiStage.Connection, MigrationStepStatus.Succeeded, result.Message);
                SetBanner(result.Message, BannerKind.Success);
                AppendLog(MigrationLogLevel.Success, result.Message);
            }
            else
            {
                ApplyUnsuccessfulResult(MigrationUiStage.Connection, result);
            }
        }
        catch (OperationCanceledException)
        {
            SetStepStatus(MigrationUiStage.Connection, MigrationStepStatus.Cancelled, "连接测试已取消");
            SetBanner("连接测试已取消。", BannerKind.Warning);
            AppendLog(MigrationLogLevel.Warning, "连接测试已取消。");
        }
        catch (Exception exception)
        {
            HandleOperationFailure(MigrationUiStage.Connection, "连接测试", exception);
        }
        finally
        {
            IsPrimaryOperationBusy = false;
            RefreshCommands();
        }
    }

    private async Task RunPreflightAsync()
    {
        if (!TryBeginRealOperation("迁移预检", requireStaticSourceConfirmation: true))
        {
            return;
        }

        IsPrimaryOperationBusy = true;
        ResetStepsFrom(MigrationUiStage.Preflight);
        SetStepStatus(MigrationUiStage.Preflight, MigrationStepStatus.Running, "正在检查版本与对象兼容性");
        SetBanner("正在执行只读预检，不会写入数据库……", BannerKind.Information);
        AppendLog(MigrationLogLevel.Information, "开始只读迁移预检。");

        try
        {
            using MigrationConnectionRequest request = CreateConnectionRequest();
            MigrationUiOperationResult result = await _operations!.RunPreflightAsync(request, CancellationToken.None);

            if (result.Succeeded)
            {
                SetStepStatus(MigrationUiStage.Preflight, MigrationStepStatus.Succeeded, result.Message);
                SetBanner(result.Message, BannerKind.Success);
                AppendLog(MigrationLogLevel.Success, result.Message);
            }
            else
            {
                ApplyUnsuccessfulResult(MigrationUiStage.Preflight, result);
            }
        }
        catch (OperationCanceledException)
        {
            SetStepStatus(MigrationUiStage.Preflight, MigrationStepStatus.Cancelled, "迁移预检已取消");
            SetBanner("迁移预检已取消。", BannerKind.Warning);
            AppendLog(MigrationLogLevel.Warning, "迁移预检已取消。");
        }
        catch (Exception exception)
        {
            HandleOperationFailure(MigrationUiStage.Preflight, "迁移预检", exception);
        }
        finally
        {
            IsPrimaryOperationBusy = false;
            RefreshCommands();
        }
    }

    private async Task StartMigrationAsync()
    {
        if (!TryBeginRealOperation("迁移", requireStaticSourceConfirmation: true))
        {
            return;
        }

        _migrationCancellation?.Dispose();
        _migrationCancellation = new CancellationTokenSource();
        IsPrimaryOperationBusy = true;
        IsRunning = true;
        IsPaused = false;
        IsCpuThrottled = false;
        IsCancellationRequested = false;
        EffectivePerformanceMode = SelectedPerformanceMode;
        IsModeChangePending = false;
        UpdateModeStatus();
        ResetMigrationMetrics();
        ResetStepsFrom(MigrationUiStage.Schema);
        SetStepStatus(MigrationUiStage.Schema, MigrationStepStatus.Running, "正在准备目标架构");
        SetBanner($"迁移已启动，当前使用{EffectiveModeDisplayName}。", BannerKind.Information);
        AppendLog(MigrationLogLevel.Information, $"开始迁移，初始性能模式：{EffectiveModeDisplayName}。");

        try
        {
            using MigrationConnectionRequest connections = CreateConnectionRequest();
            var request = new MigrationStartRequest(connections, SelectedPerformanceMode);
            MigrationUiOperationResult result = await _operations!.StartMigrationAsync(
                request,
                _migrationCancellation.Token);

            if (result.Succeeded)
            {
                CompleteAllMigrationSteps(result.Message);
                OverallProgressPercent = 100;
                SetBanner(result.Message, BannerKind.Success);
                AppendLog(MigrationLogLevel.Success, result.Message);
            }
            else
            {
                ApplyUnsuccessfulResult(FindCurrentMigrationStage(), result);
            }
        }
        catch (OperationCanceledException)
        {
            MarkCurrentMigrationStep(MigrationStepStatus.Cancelled, "迁移已取消，已提交批次保持不变");
            SetBanner("迁移已取消；请查看日志确认最后一个已提交批次。", BannerKind.Warning);
            AppendLog(MigrationLogLevel.Warning, "迁移已取消；程序未将取消解释为迁移成功。");
        }
        catch (Exception exception)
        {
            HandleOperationFailure(FindCurrentMigrationStage(), "迁移", exception);
        }
        finally
        {
            IsPrimaryOperationBusy = false;
            IsRunning = false;
            IsPaused = false;
            IsCpuThrottled = false;
            IsCancellationRequested = false;
            IsModeChangePending = false;
            UpdateModeStatus();
            _migrationCancellation?.Dispose();
            _migrationCancellation = null;
            RefreshCommands();
        }
    }

    private async Task TogglePauseAsync()
    {
        if (_operations is null)
        {
            ReportMissingService("暂停或继续迁移");
            return;
        }

        bool shouldPause = !IsPaused;
        IsControlRequestBusy = true;

        try
        {
            MigrationUiOperationResult result = await _operations.SetPausedAsync(shouldPause, CancellationToken.None);
            if (!result.Succeeded)
            {
                SetBanner(result.Message, BannerKind.Error);
                AppendLog(MigrationLogLevel.Error, result.Message);
                return;
            }

            IsPaused = shouldPause;
            MigrationUiStage stage = FindCurrentMigrationStage();
            SetStepStatus(
                stage,
                shouldPause ? MigrationStepStatus.Paused : MigrationStepStatus.Running,
                result.Message);
            SetBanner(result.Message, shouldPause ? BannerKind.Warning : BannerKind.Information);
            AppendLog(MigrationLogLevel.Information, result.Message);
        }
        catch (Exception exception)
        {
            HandleOperationFailure(FindCurrentMigrationStage(), shouldPause ? "暂停迁移" : "继续迁移", exception, false);
        }
        finally
        {
            IsControlRequestBusy = false;
        }
    }

    private async Task CancelMigrationAsync()
    {
        if (_operations is null)
        {
            ReportMissingService("取消迁移");
            return;
        }

        IsCancellationRequested = true;
        SetBanner("正在请求取消；当前批次可能需要先完成回滚或提交……", BannerKind.Warning);
        AppendLog(MigrationLogLevel.Warning, "已请求取消迁移。");
        _migrationCancellation?.Cancel();

        try
        {
            MigrationUiOperationResult result = await _operations.CancelAsync(CancellationToken.None);
            AppendLog(
                result.Succeeded ? MigrationLogLevel.Information : MigrationLogLevel.Error,
                result.Message);
            if (!result.Succeeded)
            {
                SetBanner(result.Message, BannerKind.Error);
            }
        }
        catch (Exception exception)
        {
            HandleOperationFailure(FindCurrentMigrationStage(), "取消迁移", exception, false);
        }
    }

    private async Task ChangePerformanceModeAsync(object? parameter)
    {
        if (!TryReadPerformanceMode(parameter, out MigrationPerformanceMode requestedMode))
        {
            return;
        }

        MigrationPerformanceMode previousMode = SelectedPerformanceMode;
        SelectedPerformanceMode = requestedMode;

        if (!IsRunning)
        {
            EffectivePerformanceMode = requestedMode;
            IsModeChangePending = false;
            UpdateModeStatus();
            AppendLog(MigrationLogLevel.Information, $"已选择{SelectedModeDisplayName}，将在迁移启动时使用。");
            return;
        }

        if (_operations is null)
        {
            SelectedPerformanceMode = previousMode;
            ReportMissingService("切换性能模式");
            return;
        }

        IsControlRequestBusy = true;
        IsModeChangePending = requestedMode != EffectivePerformanceMode;
        UpdateModeStatus();

        try
        {
            MigrationUiOperationResult result = await _operations.ChangePerformanceModeAsync(
                requestedMode,
                CancellationToken.None);

            if (!result.Succeeded)
            {
                SelectedPerformanceMode = previousMode;
                IsModeChangePending = SelectedPerformanceMode != EffectivePerformanceMode;
                UpdateModeStatus();
                SetBanner(result.Message, BannerKind.Error);
                AppendLog(MigrationLogLevel.Error, result.Message);
                return;
            }

            SetBanner(result.Message, BannerKind.Information);
            AppendLog(MigrationLogLevel.Information, result.Message);
        }
        catch (Exception exception)
        {
            SelectedPerformanceMode = previousMode;
            IsModeChangePending = SelectedPerformanceMode != EffectivePerformanceMode;
            UpdateModeStatus();
            HandleOperationFailure(FindCurrentMigrationStage(), "切换性能模式", exception, false);
        }
        finally
        {
            IsControlRequestBusy = false;
        }
    }

    private bool CanSelectPerformanceMode(object? parameter) =>
        !IsControlRequestBusy && TryReadPerformanceMode(parameter, out _);

    private void OperationsOnProgressChanged(object? sender, MigrationUiProgressEventArgs eventArgs) =>
        PostToUi(() => ApplyProgress(eventArgs.Snapshot));

    private void OperationsOnLogReceived(object? sender, MigrationUiLogEventArgs eventArgs) =>
        PostToUi(() => AppendLog(eventArgs.Entry));

    private void ApplyProgress(MigrationUiProgressSnapshot snapshot)
    {
        OverallProgressPercent = snapshot.OverallProgressPercent;
        CpuUsagePercent = snapshot.CpuUsagePercent;
        RowsPerSecond = snapshot.RowsPerSecond;
        EstimatedRemaining = snapshot.EstimatedRemaining;
        CompletedRows = snapshot.CompletedRows;
        TotalRows = snapshot.TotalRows;
        CurrentObject = snapshot.CurrentObject;

        AdvancePreviousSteps(snapshot.Stage);
        SetStepStatus(snapshot.Stage, snapshot.StageStatus, snapshot.Message);

        if (snapshot.EffectiveMode is { } effectiveMode)
        {
            EffectivePerformanceMode = effectiveMode;
        }

        if (snapshot.RequestedMode is { } requestedMode)
        {
            SelectedPerformanceMode = requestedMode;
        }

        IsModeChangePending = snapshot.IsModeChangePending ||
                              SelectedPerformanceMode != EffectivePerformanceMode;
        UpdateModeStatus();
        IsCpuThrottled = snapshot.IsCpuThrottled;

        if (snapshot.StageStatus == MigrationStepStatus.Paused)
        {
            IsPaused = true;
        }
        else if (snapshot.StageStatus == MigrationStepStatus.Running)
        {
            IsPaused = false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Message))
        {
            SetBanner(
                snapshot.Message,
                snapshot.StageStatus == MigrationStepStatus.Failed
                    ? BannerKind.Error
                    : snapshot.StageStatus == MigrationStepStatus.Paused
                        ? BannerKind.Warning
                        : BannerKind.Information);
        }
    }

    private MigrationConnectionRequest CreateConnectionRequest()
    {
        string? validationError = ValidateConnectionInput();
        if (validationError is not null)
        {
            throw new InvalidOperationException(validationError);
        }

        MigrationConnectionSecrets secrets = _secretProvider?.Invoke() ??
            new MigrationConnectionSecrets(null, null);

        if (SourceAuthenticationMode == SqlAuthenticationMode.SqlServer &&
            secrets.SourcePassword is null)
        {
            secrets.Dispose();
            throw new InvalidOperationException("源端 SQL Server 认证密码未提供。");
        }

        if (TargetAuthenticationMode == SqlAuthenticationMode.SqlServer &&
            secrets.TargetPassword is null)
        {
            secrets.Dispose();
            throw new InvalidOperationException("目标端 SQL Server 认证密码未提供。");
        }

        var source = new SqlEndpointDefinition(
            "源端 SQL Server 2025",
            SourceServerAddress,
            SourceDatabaseName.Trim(),
            SourceAuthenticationMode,
            NormalizeOptional(SourceUserName));
        var target = new SqlEndpointDefinition(
            "目标端 SQL Server 2019",
            TargetServerAddress,
            TargetDatabaseName.Trim(),
            TargetAuthenticationMode,
            NormalizeOptional(TargetUserName));

        return new MigrationConnectionRequest(source, target, secrets);
    }

    private string? ValidateConnectionInput()
    {
        if (string.IsNullOrWhiteSpace(MachineName))
        {
            return "请填写同一台服务器的主机名或 IP。";
        }

        if (string.IsNullOrWhiteSpace(SourceDatabaseName) || string.IsNullOrWhiteSpace(TargetDatabaseName))
        {
            return "请填写源数据库和目标数据库名称。";
        }

        if (SourceAuthenticationMode == SqlAuthenticationMode.SqlServer)
        {
            if (string.IsNullOrWhiteSpace(SourceUserName))
            {
                return "请填写源端 SQL Server 登录名。";
            }

            if (!_sourceHasPassword)
            {
                return "请填写源端 SQL Server 登录密码。";
            }
        }

        if (TargetAuthenticationMode == SqlAuthenticationMode.SqlServer)
        {
            if (string.IsNullOrWhiteSpace(TargetUserName))
            {
                return "请填写目标端 SQL Server 登录名。";
            }

            if (!_targetHasPassword)
            {
                return "请填写目标端 SQL Server 登录密码。";
            }
        }

        if (string.Equals(SourceServerAddress, TargetServerAddress, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SourceDatabaseName.Trim(), TargetDatabaseName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "源端与目标端不能是同一个实例中的同一个数据库。";
        }

        return null;
    }

    private void ConnectionInputChanged()
    {
        OnPropertyChanged(nameof(SourceServerAddress));
        OnPropertyChanged(nameof(TargetServerAddress));
        ResetStepsFrom(MigrationUiStage.Connection);
        ResetMigrationMetrics();
        UpdateInputGuidance();
        RefreshCommands();
    }

    private void SourceConnectionInputChanged()
    {
        if (_isStaticSourceConfirmed)
        {
            _isStaticSourceConfirmed = false;
            OnPropertyChanged(nameof(IsStaticSourceConfirmed));
        }

        ConnectionInputChanged();
    }

    private void UpdateInputGuidance()
    {
        string? error = ValidateConnectionInput();
        if (error is null)
        {
            InputGuidanceText = IsStaticSourceConfirmed
                ? "连接信息完整，可以测试连接。密码仅在本次操作中使用。"
                : "连接信息完整；测试连接后，请确认源库为停止写入的备份还原库。";
            InputGuidanceColor = IsStaticSourceConfirmed ? "#15803D" : "#B45309";
            if (!IsRunning && !IsPrimaryOperationBusy)
            {
                SetBanner("连接信息已准备好，请先测试连接。", BannerKind.Neutral);
            }
        }
        else
        {
            InputGuidanceText = error;
            InputGuidanceColor = "#64748B";
            if (!IsRunning && !IsPrimaryOperationBusy)
            {
                SetBanner(error, BannerKind.Neutral);
            }
        }
    }

    private bool TryBeginRealOperation(
        string operationName,
        bool requireStaticSourceConfirmation = false)
    {
        string? validationError = ValidateConnectionInput();
        if (validationError is not null)
        {
            SetBanner(validationError, BannerKind.Error);
            AppendLog(MigrationLogLevel.Error, validationError);
            return false;
        }

        if (requireStaticSourceConfirmation && !IsStaticSourceConfirmed)
        {
            const string message = "必须先确认源数据库来自备份还原且迁移期间停止写入。";
            SetBanner(message, BannerKind.Error);
            AppendLog(MigrationLogLevel.Error, message);
            return false;
        }

        if (_operations is null)
        {
            ReportMissingService(operationName);
            return false;
        }

        return true;
    }

    private void ReportMissingService(string operationName)
    {
        string message = $"{operationName}尚未执行：迁移服务还未接入此窗口。";
        SetBanner(message, BannerKind.Warning);
        AppendLog(MigrationLogLevel.Warning, message);
    }

    private void ApplyUnsuccessfulResult(MigrationUiStage stage, MigrationUiOperationResult result)
    {
        MigrationStepStatus status = result.Outcome == MigrationUiOutcome.Cancelled
            ? MigrationStepStatus.Cancelled
            : MigrationStepStatus.Failed;
        SetStepStatus(stage, status, result.Message);
        SetBanner(result.Message, status == MigrationStepStatus.Cancelled ? BannerKind.Warning : BannerKind.Error);
        AppendLog(status == MigrationStepStatus.Cancelled ? MigrationLogLevel.Warning : MigrationLogLevel.Error, result.Message);
    }

    private void HandleOperationFailure(
        MigrationUiStage stage,
        string operationName,
        Exception exception,
        bool markStepFailed = true)
    {
        string message = $"{operationName}失败：{exception.Message}";
        if (markStepFailed)
        {
            SetStepStatus(stage, MigrationStepStatus.Failed, message);
        }

        SetBanner(message, BannerKind.Error);
        AppendLog(MigrationLogLevel.Error, message);
    }

    private void HandleCommandException(Exception exception) =>
        PostToUi(() =>
        {
            string message = $"界面操作失败：{exception.Message}";
            SetBanner(message, BannerKind.Error);
            AppendLog(MigrationLogLevel.Error, message);
        });

    private void SetStepStatus(MigrationUiStage stage, MigrationStepStatus status, string? detail = null)
    {
        if (_stepByStage.TryGetValue(stage, out MigrationStepItem? step))
        {
            step.SetStatus(status, detail);
        }

        RefreshCommands();
    }

    private void ResetStepsFrom(MigrationUiStage stage)
    {
        int startIndex = (int)stage;
        foreach (MigrationStepItem step in Steps.Where(step => (int)step.Stage >= startIndex))
        {
            step.SetStatus(MigrationStepStatus.Pending, GetDefaultStepDetail(step.Stage));
        }
    }

    private void AdvancePreviousSteps(MigrationUiStage stage)
    {
        foreach (MigrationStepItem step in Steps.Where(step => (int)step.Stage < (int)stage))
        {
            if (step.Status is MigrationStepStatus.Pending or MigrationStepStatus.Running or MigrationStepStatus.Paused)
            {
                step.SetStatus(MigrationStepStatus.Succeeded, "已完成");
            }
        }
    }

    private void CompleteAllMigrationSteps(string message)
    {
        foreach (MigrationStepItem step in Steps.Where(step => (int)step.Stage >= (int)MigrationUiStage.Schema))
        {
            step.SetStatus(MigrationStepStatus.Succeeded, step.Stage == MigrationUiStage.Validation ? message : "已完成");
        }
    }

    private void MarkCurrentMigrationStep(MigrationStepStatus status, string detail) =>
        SetStepStatus(FindCurrentMigrationStage(), status, detail);

    private MigrationUiStage FindCurrentMigrationStage()
    {
        MigrationStepItem? active = Steps.FirstOrDefault(step =>
            (int)step.Stage >= (int)MigrationUiStage.Schema &&
            step.Status is MigrationStepStatus.Running or MigrationStepStatus.Paused);
        return active?.Stage ?? MigrationUiStage.Schema;
    }

    private void ResetMigrationMetrics()
    {
        OverallProgressPercent = 0;
        CpuUsagePercent = null;
        RowsPerSecond = null;
        EstimatedRemaining = null;
        CompletedRows = 0;
        TotalRows = null;
        CurrentObject = null;
    }

    private void UpdateModeStatus()
    {
        ModeStatusText = IsModeChangePending
            ? $"已请求{SelectedModeDisplayName}，下一批生效；当前{EffectiveModeDisplayName}"
            : $"当前：{EffectiveModeDisplayName}";
        OnPropertyChanged(nameof(ModeStatusColor));
    }

    private void AppendLog(MigrationLogLevel level, string message) =>
        AppendLog(new MigrationLogEntry(DateTimeOffset.Now, level, message.Trim()));

    private void AppendLog(MigrationLogEntry entry)
    {
        const int maximumLogEntries = 200;
        while (Logs.Count >= maximumLogEntries)
        {
            Logs.RemoveAt(0);
        }

        Logs.Add(entry);
        OnPropertyChanged(nameof(HasLogs));
    }

    private void SetBanner(string message, BannerKind kind)
    {
        StatusBannerText = message;
        (StatusBannerForeground, StatusBannerBackground) = kind switch
        {
            BannerKind.Success => ("#166534", "#F0FDF4"),
            BannerKind.Warning => ("#9A3412", "#FFF7ED"),
            BannerKind.Error => ("#991B1B", "#FEF2F2"),
            BannerKind.Information => ("#1D4ED8", "#EFF6FF"),
            _ => ("#475569", "#F8FAFC"),
        };
    }

    private void RefreshCommands()
    {
        TestConnectionsCommand.NotifyCanExecuteChanged();
        RunPreflightCommand.NotifyCanExecuteChanged();
        StartMigrationCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectPerformanceModeCommand.NotifyCanExecuteChanged();
    }

    private void PostToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            action();
            return;
        }

        _uiContext.Post(_ =>
        {
            if (!_disposed)
            {
                action();
            }
        }, null);
    }

    private string BuildServerAddress(string instanceName)
    {
        string machine = MachineName.Trim();
        string instance = instanceName.Trim().TrimStart('\\');
        return string.IsNullOrWhiteSpace(instance) ? machine : $"{machine}\\{instance}";
    }

    private static string GetDefaultStepDetail(MigrationUiStage stage) => stage switch
    {
        MigrationUiStage.Connection => "等待连接测试",
        MigrationUiStage.Preflight => "等待兼容性检查",
        MigrationUiStage.Schema => "等待创建对象",
        MigrationUiStage.Data => "等待复制数据",
        MigrationUiStage.Validation => "等待结果核对",
        _ => "等待",
    };

    private static string GetModeDisplayName(MigrationPerformanceMode mode) => mode switch
    {
        MigrationPerformanceMode.HighSpeed => "高速模式",
        MigrationPerformanceMode.Balanced => "平衡模式",
        _ => "低影响模式",
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}小时 {duration.Minutes}分钟";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}分钟 {duration.Seconds}秒";
        }

        return $"{Math.Max(0, duration.Seconds)}秒";
    }

    private static bool TryReadPerformanceMode(object? parameter, out MigrationPerformanceMode mode)
    {
        if (parameter is MigrationPerformanceMode typedMode)
        {
            mode = typedMode;
            return true;
        }

        return Enum.TryParse(parameter?.ToString(), true, out mode);
    }

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool SetConnectionProperty(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value ?? string.Empty, propertyName))
        {
            return false;
        }

        ConnectionInputChanged();
        return true;
    }

    private bool SetSourceConnectionProperty(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value ?? string.Empty, propertyName))
        {
            return false;
        }

        SourceConnectionInputChanged();
        return true;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private enum BannerKind
    {
        Neutral,
        Information,
        Success,
        Warning,
        Error,
    }
}
