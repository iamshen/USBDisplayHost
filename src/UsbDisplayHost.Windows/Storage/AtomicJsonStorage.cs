using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Storage;

internal sealed record ConfigurationRead<T>(T? Value, ProfileRecovery Recovery, AppError? Error);
internal sealed record VersionedDocument(int SchemaVersion, JsonElement Data);

internal sealed class AtomicJsonStorage(IErrorReporter errorReporter)
{
    private const int SchemaVersion = 2;
    private const int MaximumFileBytes = 256 * 1024;
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    public async Task<ConfigurationRead<T>> ReadAsync<T>(string path,
        Func<JsonElement, OperationResult<T>> decode, CancellationToken cancellationToken)
    {
        var primary = await ReadCandidateAsync(path, decode, cancellationToken).ConfigureAwait(false);
        if (primary.Recovery is ProfileRecovery.Primary or ProfileRecovery.UnsupportedVersion)
        {
            return primary;
        }

        var backup = await ReadCandidateAsync(path + ".bak", decode, cancellationToken).ConfigureAwait(false);
        if (backup.Recovery == ProfileRecovery.UnsupportedVersion)
        {
            return backup;
        }
        if (primary.Recovery == ProfileRecovery.Defaults && primary.Error?.Code != "ConfigurationTooLarge")
        {
            PreserveCorruptCopy(path);
        }
        if (backup.Recovery == ProfileRecovery.Defaults && backup.Error?.Code != "ConfigurationTooLarge")
        {
            PreserveCorruptCopy(path + ".bak");
        }
        if (backup.Recovery == ProfileRecovery.Primary)
        {
            return backup with { Recovery = ProfileRecovery.Backup, Error = primary.Error ?? AppError.Create(
                "PrimaryMissing", "主配置缺失，已从备份恢复", "请检查本地配置目录并重新保存") };
        }
        return new ConfigurationRead<T>(default,
            primary.Recovery == ProfileRecovery.Missing && backup.Recovery == ProfileRecovery.Missing
                ? ProfileRecovery.Missing : ProfileRecovery.Defaults,
            primary.Error ?? backup.Error);
    }

    public async Task<OperationResult<Unit>> WriteAsync<T>(string path, object data,
        Func<JsonElement, OperationResult<T>> decode, CancellationToken cancellationToken)
    {
        var primary = await ReadCandidateAsync(path, decode, cancellationToken).ConfigureAwait(false);
        var backup = await ReadCandidateAsync(path + ".bak", decode, cancellationToken).ConfigureAwait(false);
        if (primary.Recovery == ProfileRecovery.UnsupportedVersion || backup.Recovery == ProfileRecovery.UnsupportedVersion)
        {
            return new OperationResult<Unit>.Failure(primary.Error ?? backup.Error ?? NewFormatError());
        }
        if (primary.Error?.Code == "ConfigurationTooLarge" || backup.Error?.Code == "ConfigurationTooLarge")
        {
            return new OperationResult<Unit>.Failure(primary.Error?.Code == "ConfigurationTooLarge"
                ? primary.Error : backup.Error!);
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new VersionedDocument(SchemaVersion, JsonSerializer.SerializeToElement(data, Options)), Options);
        if (bytes.Length > MaximumFileBytes)
        {
            return new OperationResult<Unit>.Failure(AppError.Create(
                "ConfigurationTooLarge", "配置超过大小上限，未覆盖原文件", "请减少配置内容后重试"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        bool commitStarted = false;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (primary.Recovery == ProfileRecovery.Primary)
            {
                // 仅有效主配置进入备份，不能用损坏文件覆盖最近可用备份。
                commitStarted = true;
                File.Replace(temporaryPath, path, path + ".bak");
            }
            else
            {
                if (primary.Recovery == ProfileRecovery.Defaults)
                {
                    PreserveCorruptCopy(path);
                }
                commitStarted = true;
                File.Move(temporaryPath, path, overwrite: true);
            }
            // 提交后不提前响应取消；核实实际主文件并关闭读取句柄后才报告保存成功。
            await using (var committed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (committed.Length != bytes.Length) { throw new IOException("主配置长度未匹配已提交候选。"); }
                var actual = new byte[bytes.Length];
                await committed.ReadExactlyAsync(actual, CancellationToken.None).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(bytes)) { throw new IOException("主配置字节未匹配已提交候选。"); }
            }
            return new OperationResult<Unit>.Success(default);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !commitStarted)
        {
            return new OperationResult<Unit>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, commitStarted ? "ConfigurationCommit" : "ConfigurationPrepare");
            var error = new AppError(report.ErrorId, commitStarted ? "ConfigurationCommitUnconfirmed" : "ConfigurationPrepareFailed",
                commitStarted ? "配置提交结果待核实，主文件可能已改变" : "配置准备未完成，未替换主文件",
                commitStarted ? "请核实本次保存，不直接重复写入" : "请检查磁盘空间与目录权限后重试");
            return commitStarted ? new OperationResult<Unit>.Unknown(error) : new OperationResult<Unit>.Failure(error);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // 清理失败单独记录，不能把已提交的保存改报成失败而诱发重复设备操作。
                errorReporter.Report(exception, "ConfigurationTemporaryCleanup");
            }
        }
    }

    private async Task<ConfigurationRead<T>> ReadCandidateAsync<T>(string path,
        Func<JsonElement, OperationResult<T>> decode, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumFileBytes)
            {
                return Corrupt<T>(AppError.Create("ConfigurationTooLarge", "配置文件超过读取上限",
                    "请保留损坏副本并使用可用备份"));
            }
            using var document = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
            {
                return Corrupt<T>(AppError.Create("InvalidSchema", "配置缺少有效版本号", "请使用可用备份"));
            }
            if (version > SchemaVersion)
            {
                return new ConfigurationRead<T>(default, ProfileRecovery.UnsupportedVersion, NewFormatError());
            }
            if (version is < 1 or > SchemaVersion || !root.TryGetProperty("data", out var payload))
            {
                return Corrupt<T>(AppError.Create("InvalidSchema", "配置版本或内容无效", "请使用可用备份"));
            }
            return decode(payload) switch
            {
                OperationResult<T>.Success success => new ConfigurationRead<T>(success.Value, ProfileRecovery.Primary, null),
                OperationResult<T>.Failure failure => Corrupt<T>(failure.Error),
                _ => Corrupt<T>(AppError.Create("InvalidConfiguration", "配置无法恢复", "请使用可用备份"))
            };
        }
        catch (FileNotFoundException)
        {
            return new ConfigurationRead<T>(default, ProfileRecovery.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new ConfigurationRead<T>(default, ProfileRecovery.Missing, null);
        }
        catch (JsonException exception)
        {
            var report = errorReporter.Report(exception, "ConfigurationDecode");
            return Corrupt<T>(new AppError(report.ErrorId, "CorruptConfiguration",
                "配置文件损坏，未应用其中的内容", "请使用可用备份或重新设置"));
        }
    }

    private static void PreserveCorruptCopy(string path)
    {
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        string copy = path + ".corrupt-" + hash;
        if (!File.Exists(copy))
        {
            File.Copy(path, copy, overwrite: false);
        }
    }

    private static ConfigurationRead<T> Corrupt<T>(AppError error) => new(default, ProfileRecovery.Defaults, error);
    private static AppError NewFormatError() => AppError.Create("UnsupportedConfigurationVersion",
        "配置由较新版本应用创建，已保留原文件", "请使用匹配的新版本或执行明确的配置恢复流程");
}
