using System.Collections.Frozen;
using System.Windows.Controls;
using Wpf.Ui.Abstractions;

namespace UsbDisplayHost.Desktop.Services;

// 只缓存组合根创建的五个页面，不在导航时创建会话或后台服务。
public sealed class WorkspacePageProvider(IEnumerable<Page> pages) : INavigationViewPageProvider
{
    private readonly FrozenDictionary<Type, Page> pagesByType = pages.ToFrozenDictionary(page => page.GetType());

    public object? GetPage(Type pageType) => pagesByType.GetValueOrDefault(pageType);
}
