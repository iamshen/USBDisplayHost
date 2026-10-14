using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

// 一个用户数据目录共用同一写入边界和素材索引；实例由 Desktop 组合根持有。
public sealed class StorageCoordinator
{
    public StorageCoordinator(ApplicationDataPaths paths, IErrorReporter reporter)
    {
        Root = paths.Root;
        Reporter = reporter;
        Boundary = new StorageBoundary(reporter);
    }

    internal string Root { get; }
    internal IErrorReporter Reporter { get; }
    internal StorageBoundary Boundary { get; }
    internal AssetReferenceRegistry References { get; } = new();

    internal void VerifyPaths(ApplicationDataPaths paths)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Root, paths.Root))
        {
            throw new ArgumentException("A storage coordinator cannot combine different user data directories.", nameof(paths));
        }
    }
}
