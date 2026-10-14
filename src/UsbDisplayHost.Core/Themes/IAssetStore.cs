using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

public enum ImageFileFormat { Png, Jpeg }
public sealed record ImageInfo(int Width, int Height, ImageFileFormat Format);
// 调用方取得自己的编辑／配置引用之后再释放导入结果，包含未采用结果的分支。
public sealed record ImportedAsset(string AssetId, ImageInfo Image, AssetReferenceLease Reference) : IDisposable
{
    public void Dispose() => Reference.Dispose();
}

public interface IImageValidator
{
    // 验证返回前完成解码；流仍由调用方拥有，不提前关闭或归还缓冲。
    Task<OperationResult<ImageInfo>> ValidateAsync(Stream source, CancellationToken cancellationToken);
}

public interface IAssetStore
{
    OperationResult<AssetReferenceLease> AcquireReference(string assetId, AssetReferenceOwner owner);
    Task<OperationResult<ImportedAsset>> ImportAsync(string sourcePath, CancellationToken cancellationToken);
    // 成功返回的流由读取方负责释放，资产标识不能被当作外部路径。
    Task<OperationResult<Stream>> OpenReadAsync(string assetId, CancellationToken cancellationToken);
}
