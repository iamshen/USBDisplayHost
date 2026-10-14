using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Desktop.Services;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed record DiagnosticRow(long Sequence, string Timestamp, string Severity, string Stage, string Code, string ErrorId);

public partial class DiagnosticsPageViewModel(IDiagnosticService diagnostics, IDiagnosticExporter exporter,
    IDiagnosticFolderOpener folderOpener, IExportDestinationPicker destinationPicker,
    Func<DiagnosticConfigurationSummary> captureConfiguration, DeviceSessionLifetime sessions) : ObservableObject
{
    [ObservableProperty] private string connectionSummary = "未连接";
    [ObservableProperty] private string completedFramesText = "—";
    [ObservableProperty] private string logStateText = "正在读取";
    [ObservableProperty] private string storageText = "尚未取得日志状态";
    [ObservableProperty] private string hostTransferText = "未提供（尚无已接管会话）";
    [ObservableProperty] private string transferErrorText = string.Empty;
    [ObservableProperty] private string activeProductionText = "未提供";
    public string DeviceFeedbackText => "呈现帧数、呈现帧率及设备错误计数：未提供";
    [ObservableProperty] private string eventCountText = "最近事件：0 / 500";
    [ObservableProperty] private string actionText = "";
    [ObservableProperty] private double exportProgress;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardExportVerificationCommand))]
    private bool isBusy;
    private bool stopping;
    private long exportGeneration;
    public ObservableCollection<DiagnosticRow> Events { get; } = [];

    private bool CanAct() => !IsBusy && !stopping;
    public bool HasUnconfirmedExport => exporter.Pending is not null;
    private bool CanExport() => CanAct() && !HasUnconfirmedExport;
    private bool CanVerifyExport() => CanAct() && HasUnconfirmedExport;
    private void RefreshExportState()
    {
        OnPropertyChanged(nameof(HasUnconfirmedExport));
        ExportCommand.NotifyCanExecuteChanged(); VerifyExportCommand.NotifyCanExecuteChanged(); DiscardExportVerificationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExport), IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        long generation = ++exportGeneration;
        try
        {
            string? path = destinationPicker.Pick();
            if (path is null) { ActionText = "已取消导出"; return; }
            ExportProgress = 0;
            ActionText = "正在导出诊断";
            var progress = new Progress<DiagnosticExportProgress>(value =>
            {
                if (stopping || !IsBusy || generation != exportGeneration) { return; }
                ExportProgress = value.TotalBytes > 0 ? Math.Clamp(100.0 * value.ReadBytes / value.TotalBytes, 0, 100) : 0;
            });
            var transfer = sessions.CaptureTransport();
            var request = new DiagnosticExportRequest(path, captureConfiguration())
            {
                Transfer = transfer is null ? null : new(DateTimeOffset.UtcNow, transfer.Transfer.UsbCompletedFrames,
                    transfer.Transfer.ConfirmedPacketBytes, transfer.Transfer.State,
                    transfer.NativeClosed ? DiagnosticSessionResourceState.ClosedRetained : transfer.Stage switch
                    {
                        SessionLifecycleStage.Active => DiagnosticSessionResourceState.Active,
                        SessionLifecycleStage.Closing => DiagnosticSessionResourceState.Closing,
                        _ => DiagnosticSessionResourceState.Unconfirmed
                    })
            };
            var result = await exporter.ExportAsync(request, progress, cancellationToken);
            ActionText = result switch
            {
                OperationResult<DiagnosticExportSummary>.Success success =>
                    $"已导出：{success.Value.OutputPath}（事件 {success.Value.ExportedEvents}；省略记录 {success.Value.OmittedRecords}）",
                OperationResult<DiagnosticExportSummary>.Failure failure => Format(failure.Error),
                OperationResult<DiagnosticExportSummary>.Unknown unknown => Format(unknown.Error),
                _ => "已取消导出，原目标文件保留"
            };
            if (result is OperationResult<DiagnosticExportSummary>.Success) { ExportProgress = 100; }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowActionFailure(exception, "DiagnosticExportCommand"); }
        finally { exportGeneration++; IsBusy = false; RefreshExportState(); }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyExport), IncludeCancelCommand = true)]
    private async Task VerifyExportAsync(CancellationToken token)
    {
        if (exporter.Pending is not { } pending) { return; }
        IsBusy = true;
        try
        {
            var result = await exporter.VerifyAsync(pending.VerificationId, token);
            ActionText = result switch
            {
                OperationResult<DiagnosticExportSummary>.Success success => $"已核实导出：{success.Value.OutputPath}。未重新写入目标文件",
                OperationResult<DiagnosticExportSummary>.Failure failure => Format(failure.Error),
                OperationResult<DiagnosticExportSummary>.Unknown unknown => Format(unknown.Error),
                _ => "核实已取消，原确认记录保留"
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowActionFailure(exception, "DiagnosticExportVerificationCommand"); }
        finally { IsBusy = false; RefreshExportState(); }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyExport))]
    private void DiscardExportVerification()
    {
        if (exporter.Pending is not { } pending) { return; }
        try
        {
            var result = exporter.DiscardVerification(pending.VerificationId);
            ActionText = result is OperationResult<Unit>.Success ? "已放弃本次导出核实，目标文件未删除或回滚"
                : result is OperationResult<Unit>.Failure failure ? Format(failure.Error) : "原导出确认记录仍保留";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowActionFailure(exception, "DiagnosticExportVerificationDiscard"); }
        finally { RefreshExportState(); }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task OpenLogsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await folderOpener.OpenAsync(CancellationToken.None);
            ActionText = result switch
            {
                OperationResult<Unit>.Success => "已请求打开日志目录",
                OperationResult<Unit>.Failure failure => Format(failure.Error),
                OperationResult<Unit>.Unknown unknown => Format(unknown.Error),
                _ => "已取消打开日志目录"
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowActionFailure(exception, "OpenLogsCommand"); }
        finally { IsBusy = false; }
    }

    private static string Format(AppError error) => $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}";
    private void ShowActionFailure(Exception exception, string stage)
    {
        var report = diagnostics.Report(exception, stage);
        ActionText = $"诊断操作未完成，请重试。错误编号：{report.ErrorId}";
    }

    public async Task StopAsync()
    {
        stopping = true;
        ExportCommand.NotifyCanExecuteChanged();
        OpenLogsCommand.NotifyCanExecuteChanged();
        ExportCommand.Cancel();
        VerifyExportCommand.Cancel(); RefreshExportState();
        if (ExportCommand.ExecutionTask is { } export) { await export; }
        if (OpenLogsCommand.ExecutionTask is { } opening) { await opening; }
        if (VerifyExportCommand.ExecutionTask is { } verification) { await verification; }
    }

    public void Refresh()
    {
        var transfer = sessions.CaptureTransport();
        if (transfer is null)
        {
            ConnectionSummary = "未连接";
            CompletedFramesText = "—";
            HostTransferText = "未提供（尚无已接管会话）";
            TransferErrorText = string.Empty;
            ActiveProductionText = string.Empty;
        }
        else
        {
            string lifecycle = transfer.NativeClosed ? "原生会话已关闭" : transfer.Stage switch
            {
                SessionLifecycleStage.Active => "会话已接管", SessionLifecycleStage.Closing => "正在关闭",
                SessionLifecycleStage.Unknown => "关闭未确认", _ => "会话资源仍被保留"
            };
            string channel = transfer.Transfer.State switch
            {
                FrameTransferState.Ready => "就绪", FrameTransferState.RecoveryRequired => "需要恢复",
                FrameTransferState.IoUnconfirmed => "I/O 未确认", _ => "状态未提供"
            };
            if (transfer.NativeClosed) { channel = "已关闭"; }
            else if (transfer.Stage is SessionLifecycleStage.Closing or SessionLifecycleStage.Unknown)
            { channel = "关闭中或关闭未确认，不受理发送"; }
            ConnectionSummary = transfer.NativeClosed ? "已关闭" : transfer.Stage == SessionLifecycleStage.Active ? channel : lifecycle;
            CompletedFramesText = transfer.Transfer.UsbCompletedFrames.ToString("N0");
            HostTransferText = $"设备 {transfer.DeviceKey.Value}，会话 {transfer.Generation} · {lifecycle}\n"
                + $"USB 完整发送帧数：{transfer.Transfer.UsbCompletedFrames:N0}；完整分片确认字节：{transfer.Transfer.ConfirmedPacketBytes:N0} B；帧通道：{channel}";
            string producing = transfer.Producer.IsStopping ? "已停止"
                : transfer.Producer.RequiresReceiptRecovery ? "发送回执待核实"
                : transfer.Producer.Error is not null ? "活动刷新故障" : "产帧循环已创建";
            string duration = transfer.FrameRate.LastFrameDuration is { } elapsed
                ? $"{elapsed.TotalMilliseconds:F1} ms" : "未提供";
            ActiveProductionText = $"活动刷新：{producing}；已渲染 {transfer.Producer.RenderedFrames:N0} 帧（不含应用首帧）；待结算回执 {transfer.Producer.PendingReceipts}\n"
                + $"帧率上限（用户／当前）：{transfer.FrameRate.UserFramesPerSecond} / {transfer.FrameRate.EffectiveFramesPerSecond} fps；末次完整帧发送耗时：{duration}"
                + (transfer.FrameRate.RequiresRecovery ? "；发送节奏需要恢复" : string.Empty);
            TransferErrorText = string.Join(Environment.NewLine, new[]
                { transfer.Transfer.Error, transfer.Producer.Error, transfer.FrameRate.Error }
                .OfType<AppError>().DistinctBy(error => error.ErrorId).Select(Format));
        }
        var snapshot = diagnostics.Journal.GetSnapshot();
        long oldest = snapshot.IsEmpty ? long.MaxValue : snapshot[0].Sequence;
        while (Events.Count > 0 && Events[0].Sequence < oldest) { Events.RemoveAt(0); }
        long latest = Events.Count == 0 ? 0 : Events[^1].Sequence;
        foreach (var item in snapshot)
        {
            if (item.Sequence <= latest) { continue; }
            Events.Add(new DiagnosticRow(item.Sequence, item.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                item.Severity switch { DiagnosticSeverity.Error => "错误", DiagnosticSeverity.Warning => "警告", _ => "信息" },
                item.Stage, item.Code, item.ErrorId ?? "—"));
        }
        EventCountText = $"最近事件：{Events.Count} / {DiagnosticJournal.Capacity}";
        var status = diagnostics.GetStorageStatus();
        string state = status.IsStopped ? "已停止" : status.IsStorageAvailable ? "可写入" : "存储不可用";
        LogStateText = status.IsStopped ? "已停止" : status.IsStorageAvailable ? "正常" : "不可用";
        StorageText = $"日志：{state}　已写入 {status.WrittenEvents}　待写入 {status.PendingEvents}　丢弃 {status.DroppedEvents}　写入失败 {status.WriteFailures}";
    }
}
