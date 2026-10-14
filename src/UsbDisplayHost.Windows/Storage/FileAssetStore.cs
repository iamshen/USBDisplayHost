using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

public sealed partial class FileAssetStore : IAssetStore, IAssetCleanupStore
{
    private readonly ApplicationDataPaths paths;
    private readonly IImageValidator validator;
    private readonly StorageBoundary boundary;
    private readonly IErrorReporter errorReporter;
    private readonly AssetReferenceRegistry references;

    public OperationResult<AssetReferenceLease> AcquireReference(string assetId, AssetReferenceOwner owner) => references.Acquire(assetId, owner);

    public FileAssetStore(ApplicationDataPaths paths, IImageValidator validator, StorageCoordinator coordinator)
    {
        coordinator.VerifyPaths(paths);
        this.paths = paths;
        this.validator = validator;
        errorReporter = coordinator.Reporter;
        boundary = coordinator.Boundary;
        references = coordinator.References;
    }

    public Task<OperationResult<ImportedAsset>> ImportAsync(string sourcePath, CancellationToken cancellationToken) =>
        boundary.RunAsync<ImportedAsset>(async () =>
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = await Task.Run(() => ImageInputInspector.Inspect(source, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (header is OperationResult<ImageInfo>.Failure failure)
            {
                return new OperationResult<ImportedAsset>.Failure(failure.Error);
            }

            ImageInfo importedImage;
            string assetId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(paths.Assets);
            string destination = Path.Combine(paths.Assets, assetId + ".image");
            string temporaryPath = destination + ".tmp";
            AssetReferenceLease? importReference = null;
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {
                    source.Position = 0;
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Position = 0;
                    var validation = await validator.ValidateAsync(output, cancellationToken).ConfigureAwait(false);
                    if (validation is OperationResult<ImageInfo>.Failure invalid)
                    {
                        return new OperationResult<ImportedAsset>.Failure(invalid.Error);
                    }
                    if (validation is OperationResult<ImageInfo>.Unknown unknown)
                    {
                        return new OperationResult<ImportedAsset>.Failure(unknown.Error);
                    }
                    if (validation is not OperationResult<ImageInfo>.Success image)
                    {
                        return new OperationResult<ImportedAsset>.Cancelled();
                    }
                    output.Flush(flushToDisk: true);
                    importedImage = image.Value;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var retained = AcquireReference(assetId, AssetReferenceOwner.Importing);
                if (retained is OperationResult<AssetReferenceLease>.Failure referenceFailure)
                {
                    return new OperationResult<ImportedAsset>.Failure(referenceFailure.Error);
                }
                if (retained is not OperationResult<AssetReferenceLease>.Success reference)
                {
                    return new OperationResult<ImportedAsset>.Failure(AppError.Create("ImportReferenceUnconfirmed",
                        "导入素材引用未取得，未提交素材", "请等待当前素材操作收敛后重试"));
                }
                importReference = reference.Value;
                File.Move(temporaryPath, destination);
                var imported = new ImportedAsset(assetId, importedImage, importReference);
                importReference = null;
                return new OperationResult<ImportedAsset>.Success(imported);
            }
            finally
            {
                importReference?.Dispose();
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    errorReporter.Report(exception, "AssetTemporaryCleanup");
                }
            }
        }, "AssetImport", cancellationToken);

    public Task<OperationResult<Stream>> OpenReadAsync(string assetId, CancellationToken cancellationToken) =>
        boundary.RunAsync<Stream>(() =>
        {
            var acquired = AcquireReference(assetId, AssetReferenceOwner.Rendering);
            if (acquired is OperationResult<AssetReferenceLease>.Failure failure)
            {
                return Task.FromResult<OperationResult<Stream>>(new OperationResult<Stream>.Failure(failure.Error));
            }
            var lease = ((OperationResult<AssetReferenceLease>.Success)acquired).Value;
            try
            {
                Stream stream = new AssetReadStream(new FileStream(Path.Combine(paths.Assets, lease.AssetId + ".image"),
                    FileMode.Open, FileAccess.Read, FileShare.Read), lease);
                return Task.FromResult<OperationResult<Stream>>(new OperationResult<Stream>.Success(stream));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }, "AssetRead", cancellationToken);
}
