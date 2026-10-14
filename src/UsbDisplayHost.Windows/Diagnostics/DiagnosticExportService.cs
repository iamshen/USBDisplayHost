using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Windows.Storage;

namespace UsbDisplayHost.Windows.Diagnostics;

public sealed class DiagnosticExportService(ApplicationDataPaths paths, DiagnosticLog diagnostics,
    TimeProvider timeProvider) : IDiagnosticExporter
{
    private const int MaximumLineBytes = 64 * 1024;
    private int busy;
    private PendingDiagnosticExport? pending;
    public PendingDiagnosticExport? Pending => Volatile.Read(ref pending);

    public async Task<OperationResult<DiagnosticExportSummary>> ExportAsync(DiagnosticExportRequest request,
        IProgress<DiagnosticExportProgress>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Unavailable("已有诊断导出或核实正在处理"); }
        try
        {
            if (Pending is not null) { return Unavailable("上次导出提交结果待核实，不能覆盖待确认记录"); }
            return await Task.Run(() => ExportCoreAsync(request, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    public async Task<OperationResult<DiagnosticExportSummary>> VerifyAsync(Guid verificationId, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Unavailable("已有诊断操作正在处理"); }
        try
        {
            return await Task.Run(() => VerifyCoreAsync(verificationId, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private async Task<OperationResult<DiagnosticExportSummary>> VerifyCoreAsync(Guid verificationId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = Pending;
            if (selected is null || selected.VerificationId != verificationId) { return Unavailable("待确认导出标识已变化"); }
            await using (var file = new FileStream(selected.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (file.Length != selected.Length) { return Unavailable("目标文件长度未匹配本次导出，原确认记录已保留"); }
                var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(Convert.ToHexString(hash), selected.Sha256))
                { return Unavailable("目标文件哈希未匹配本次导出，原确认记录已保留"); }
            }
            Volatile.Write(ref pending, null);
            return new OperationResult<DiagnosticExportSummary>.Success(selected.Summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new OperationResult<DiagnosticExportSummary>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = diagnostics.Report(exception, "DiagnosticExportVerification");
            return new OperationResult<DiagnosticExportSummary>.Unknown(new(report.ErrorId, "DiagnosticExportStillUnconfirmed",
                "目标 ZIP 未能确认，原导出记录已保留", "请检查目标文件后再次核实，不直接覆盖"));
        }
    }

    public OperationResult<Unit> DiscardVerification(Guid verificationId)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        { return new OperationResult<Unit>.Failure(AppError.Create("DiagnosticExportBusy", "诊断操作正在处理", "请等待操作结束")); }
        try
        {
            if (Pending?.VerificationId != verificationId)
            { return new OperationResult<Unit>.Failure(AppError.Create("DiagnosticExportVerificationChanged", "待确认导出标识已变化", "请读取当前结果")); }
            Volatile.Write(ref pending, null);
            return new OperationResult<Unit>.Success(new());
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private static OperationResult<DiagnosticExportSummary> Unavailable(string reason) =>
        new OperationResult<DiagnosticExportSummary>.Failure(AppError.Create("DiagnosticExportUnavailable", reason, "请核实或明确放弃原确认记录"));

    private async Task<OperationResult<DiagnosticExportSummary>> ExportCoreAsync(DiagnosticExportRequest request,
        IProgress<DiagnosticExportProgress>? progress, CancellationToken cancellationToken)
    {
        string? temporary = null;
        LogFileSnapshot[] snapshots = [];
        PendingDiagnosticExport? committing = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Transfer is { } transfer && (transfer.UsbCompletedFrames < 0 || transfer.ConfirmedPacketBytes < 0
                || !Enum.IsDefined(transfer.ChannelState) || !Enum.IsDefined(transfer.ResourceState)
                || transfer.CapturedAt.Offset != TimeSpan.Zero))
            {
                return new OperationResult<DiagnosticExportSummary>.Failure(AppError.Create("InvalidDiagnosticTransferSummary",
                    "主机传输摘要无效，未开始导出", "请刷新当前会话状态后重新导出"));
            }
            string destination = Path.GetFullPath(request.OutputPath);
            string root = Path.TrimEndingDirectorySeparator(paths.Root) + Path.DirectorySeparatorChar;
            if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return new OperationResult<DiagnosticExportSummary>.Failure(AppError.Create("InvalidDiagnosticDestination",
                    "导出位置无效", "请选择应用数据目录之外的 ZIP 文件"));
            }
            string directory = Path.GetDirectoryName(destination) ?? throw new IOException("导出位置无效");
            temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.diagnostics.tmp");
            snapshots = diagnostics.CaptureLogSnapshot();
            long total = snapshots.Sum(item => item.Length);
            long read = 0, exported = 0, omitted = 0;
            int completed = 0;
            progress?.Report(new DiagnosticExportProgress(0, total, 0, snapshots.Length));
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var metadata = archive.CreateEntry("summary.json");
                    await using (var entry = metadata.Open())
                    {
                        await JsonSerializer.SerializeAsync(entry, new
                        {
                            SchemaVersion = 2,
                            ExportedAt = timeProvider.GetUtcNow(),
                            ApplicationVersion = typeof(DiagnosticExportService).Assembly.GetName().Version?.ToString(),
                            WindowsVersion = Environment.OSVersion.Version.ToString(),
                            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                            FirmwareVersion = "未提供", DriverVersion = "未提供",
                            DeviceState = "设备状态未提供",
                            HostTransfer = request.Transfer is { } captured ? new
                            {
                                captured.CapturedAt, captured.UsbCompletedFrames, captured.ConfirmedPacketBytes,
                                ChannelState = captured.ChannelState.ToString(), ResourceState = captured.ResourceState.ToString(),
                                ByteScope = "CompleteAfPacketsOnly", FrameScope = "CompleteUsbFramesOnly"
                            } : null,
                            DeviceFeedback = new { Availability = "NotProvided" },
                            ConfigurationSummary = request.Configuration with
                            {
                                Workspace = SafeToken(request.Configuration.Workspace) ?? "Unknown",
                                Template = SafeToken(request.Configuration.Template) ?? "Unknown"
                            },
                            Storage = diagnostics.GetStorageStatus()
                        }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    var logEntry = archive.CreateEntry("events.jsonl", CompressionLevel.Fastest);
                    await using (var entry = logEntry.Open())
                    {
                        await using var writer = new StreamWriter(entry, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
                        foreach (var snapshot in snapshots)
                        {
                            await foreach (var line in ReadLinesAsync(snapshot, cancellationToken).ConfigureAwait(false))
                            {
                                read += line.Bytes;
                                string? sanitized = line.Content is null ? null : SanitizeRecord(line.Content);
                                if (sanitized is null) { omitted++; }
                                else { await writer.WriteLineAsync(sanitized.AsMemory(), cancellationToken).ConfigureAwait(false); exported++; }
                                // 每块回报而非每行创建 UI 通知，完成时补充实际末值。
                                if (read / (256 * 1024) != (read - line.Bytes) / (256 * 1024))
                                {
                                    progress?.Report(new DiagnosticExportProgress(read, total, completed, snapshots.Length));
                                }
                            }
                            completed++;
                            progress?.Report(new DiagnosticExportProgress(read, total, completed, snapshots.Length));
                        }
                    }
                    var recent = archive.CreateEntry("recent-events.json");
                    await using (var entry = recent.Open())
                    {
                        await JsonSerializer.SerializeAsync(entry, diagnostics.Journal.GetSnapshot(),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            byte[] hash;
            long length;
            await using (var file = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                length = file.Length;
                hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            committing = new(Guid.NewGuid(), destination, length, Convert.ToHexString(hash), new(destination, exported, omitted));
            Volatile.Write(ref pending, committing);
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            // 提交已发生；取消不能把目标已变化描述为原文件保留，等待实际只读核实结算。
            var verified = await VerifyCoreAsync(committing.VerificationId, CancellationToken.None).ConfigureAwait(false);
            if (verified is not OperationResult<DiagnosticExportSummary>.Success)
            {
                var error = verified switch
                {
                    OperationResult<DiagnosticExportSummary>.Failure failure => failure.Error,
                    OperationResult<DiagnosticExportSummary>.Unknown unknown => unknown.Error,
                    _ => AppError.Create("DiagnosticExportTargetUnconfirmed", "目标 ZIP 提交后未确认", "请核实目标，不重复覆盖")
                };
                return new OperationResult<DiagnosticExportSummary>.Unknown(error);
            }
            diagnostics.Write(DiagnosticSeverity.Information, "DiagnosticExport", "Completed");
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (committing is not null)
            {
                Volatile.Write(ref pending, committing);
                return new OperationResult<DiagnosticExportSummary>.Unknown(AppError.Create("DiagnosticExportCancelledAtCommit",
                    "提交期间取消结果待核实，原导出记录已保留", "请核实目标 ZIP，不直接重复覆盖"));
            }
            diagnostics.Write(DiagnosticSeverity.Information, "DiagnosticExport", "Cancelled");
            return new OperationResult<DiagnosticExportSummary>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = diagnostics.Report(exception, "DiagnosticExport");
            if (committing is not null)
            {
                Volatile.Write(ref pending, committing);
                return new OperationResult<DiagnosticExportSummary>.Unknown(new(report.ErrorId, "DiagnosticExportCommitUnconfirmed",
                    "目标 ZIP 提交结果待核实，确认记录已保留", "请使用核实导出，不直接重复覆盖目标文件"));
            }
            return new OperationResult<DiagnosticExportSummary>.Failure(new AppError(report.ErrorId, "DiagnosticExportFailed",
                "诊断导出未完成", "请检查目标目录权限和磁盘空间后重试"));
        }
        finally
        {
            foreach (var snapshot in snapshots)
            {
                try { snapshot.Stream.Dispose(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { diagnostics.Report(exception, "DiagnosticReadClose"); }
            }
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { diagnostics.Report(exception, "DiagnosticTemporaryCleanup"); }
            }
        }
    }

    private static async IAsyncEnumerable<ExportLine> ReadLinesAsync(LogFileSnapshot snapshot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        byte[] line = new byte[MaximumLineBytes];
        int lineLength = 0;
        long lineBytes = 0;
        bool tooLong = false;
        long remaining = snapshot.Length;
        while (remaining > 0)
        {
            int count = await snapshot.Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (count == 0) { break; }
            remaining -= count;
            for (int index = 0; index < count; index++)
            {
                byte value = buffer[index];
                lineBytes++;
                if (value == (byte)'\n')
                {
                    yield return new ExportLine(tooLong ? null : Encoding.UTF8.GetString(line, 0, lineLength), lineBytes);
                    lineLength = 0; lineBytes = 0; tooLong = false;
                }
                else if (lineLength < line.Length) { line[lineLength++] = value; }
                else { tooLong = true; }
            }
        }
        if (lineBytes > 0) { yield return new ExportLine(null, lineBytes); }
    }

    private static string? SanitizeRecord(string record)
    {
        try
        {
            using var document = JsonDocument.Parse(record, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object) { return null; }
            return JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.TryParse(Text(root, "Timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var timestamp) ? timestamp : (DateTimeOffset?)null,
                Level = SafeToken(Text(root, "Level")), Stage = SafeToken(Text(properties, "Stage")),
                Code = SafeToken(Text(properties, "Code")),
                ErrorId = Guid.TryParseExact(Text(properties, "ErrorId"), "N", out var errorId) ? errorId.ToString("N") : null,
                HResult = Integer(properties, "HResult"), NativeError = Integer(properties, "NativeError"),
                Association = ReadContext(properties)
            });
        }
        catch (JsonException) { return null; }
    }

    private static DiagnosticContext? ReadContext(JsonElement properties)
    {
        if (!properties.TryGetProperty("Association", out var association) || association.ValueKind != JsonValueKind.Object) { return null; }
        string? key = Text(association, "AnonymousDeviceKey");
        long? bytes = Integer(association, "ActualBytes");
        long? generation = Integer(association, "SessionGeneration");
        double? elapsed = association.TryGetProperty("ElapsedMilliseconds", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double duration) && double.IsFinite(duration) && duration >= 0 ? duration : null;
        return new DiagnosticContext(key is { Length: 64 } && key.All(Uri.IsHexDigit) ? key : null,
            generation is >= 0 ? generation : null,
            Guid.TryParse(Text(association, "OperationId"), out var operation) ? operation : null,
            SafeToken(Text(association, "State")), bytes is >= 0 ? bytes : null, elapsed,
            SafeToken(Text(association, "FirmwareVersion")), SafeToken(Text(association, "DriverVersion")));
    }

    private static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Integer(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
    private static string? SafeToken(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? value : null;
    private sealed record ExportLine(string? Content, long Bytes);
}
