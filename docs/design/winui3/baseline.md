# WinUI 3 迁移样板与视觉基线

关联 [#8](https://github.com/iamshen/USBDisplayHost/issues/8) 和 [#9](https://github.com/iamshen/USBDisplayHost/issues/9)。

## 视觉参考

Mole Windows 官方产品页的 Clean 截图采用顶部居中的胶囊导航，用浅色填充突出当前页。深蓝画布上只有一个主状态区，标题、解释和下一步动作沿一条中心线排布。USB Display 延用这个层级，把设备和它的连接状态放在主区，诊断入口跟随状态。品牌使用 USB Display 现有图标。

Apps 截图把选项、筛选和排序集中到列表上方，行内先显示名称，再显示版本和体积，几乎没有逐行描边。诊断页借鉴这一阅读顺序，优先让事件级别、标题和时间可扫读。Status 截图把同类指标集中到一格，数值与单位相邻，进程明细统排在下方。设备页沿用主状态优先和相关信息靠近的做法。

- [Mole Windows 产品页](https://mole.fit/windows) · [Clean](https://mole.fit/img/windows/clean.jpg) · [Apps](https://mole.fit/img/windows/uninstall.jpg) · [Status](https://mole.fit/img/windows/status.jpg)
- [Mole Windows 更新历史](https://mole.fit/windows/releases) 在 2026-11-16 列出的最新版本为 0.22.0。产品页未标注各截图的构建版本。
- [Tw93 的客户端开发文章](https://tw93.fun/2026-10-22/desktop.html) 提供分层与设计资源复用的背景。[V2EX 原讨论链接](https://www.v2ex.com/t/1254590) 本次访问仍重定向到首页。

## 五页布局草图

顶部横向导航通过选中填充、文字和原生选择语义标记当前页。五页职责与控件分布如下。

| 页面 | 主区 | 次要信息与动作 |
| --- | --- | --- |
| 设备 | 未连接时居中呈现 USB 连接状态与下一步。连接后显示当前设备、模式和主机活动画面 | 候选列表与当前会话分开。设备资源关闭后若仍保留本地结果，连接状态区继续提供「完成本地交接」。失败附错误编号 |
| 主题 | 主预览优先，三种模板排在预览下方。属性列随模板变化 | 颜色细节可展开，应用与撤销聚在编辑区底部。活动预览与设备读回各有明确位置 |
| 设备设置 | 当前会话标识和可读写参数。每行保持标签、范围及单位 | 警告和不可用原因就近显示。设备应用、本地恢复保存与读回分别反馈 |
| 诊断 | 列表或分组记录为主体，明确事件级别、时间和错误编号 | 详情按需查看，刷新、打开目录、导出和验证集中在列表操作区 |
| 应用设置 | 使用对齐的表单行分组显示外观、启动和退出偏好 | 更新单独成组显示版本、进度与下一步。撤销和保存跟随偏好编辑 |

默认参照 1180×820 DIP，受当前工作区可用大小约束。导航与状态优先用原生 WinUI 控件。正文用系统 UI 字体及中文回退，标题、状态正文与辅助文字按层级区分。页面外边距以 32 DIP 为起点，邻近控制项以 8–12 DIP 分隔，内容分组以 24 DIP 分隔。顶部导航使用胶囊选中样式，表单沿用原生控件样式。

浅色和深色用 `ThemeResource` 切换画布、表面、文字、边界与选中状态。高对比映射系统窗口、文本和高亮颜色。当前页靠选中语义及可见填充识别，设备和错误状态始终提供明确文本。交互控件保留可访问名称、默认焦点提示和键盘操作。

## 构建与分发

- 样板为 `UsbDisplayHost.WinUI`，沿用 `global.json` 的 .NET SDK 10.0.401，锁定稳定版 Microsoft.WindowsAppSDK 2.6.5。项目目标为 `net10.0-windows10.0.19041.0`。
- 样板以 Windows 10 build 19041 为目标下限，使用本机 Windows SDK 构建 win-x64。最低版本的安装和运行仍需实际环境验证。
- 非 MSIX 路径分别设置 .NET 与 Windows App SDK 自包含。Beta 使用独立 Velopack 安装身份 `USBDisplayHost.Beta` 与 `win-x64-beta` 通道，配置、素材和诊断使用 `%LocalAppData%\UsbDisplayHost.Beta.Data`。
- 仓库 `build.ps1 -Action Build` 编译两个入口。`Publish` 和 `Package` 默认使用 WPF，带 `-Channel Beta` 时使用 WinUI。构建与打包均不启动程序。
- 无设备验证从仓库根目录运行 `dotnet run --project src/UsbDisplayHost.WinUI -c Release --no-build -- --ui-only`。该模式跳过设备发现和连接，应用数据写入独立临时目录。设备页入口和顶部导航都能打开诊断，查看本次启动记录。启动记录保留在 `%TEMP%\UsbDisplayHost.WinUI\startup-*.log` 的五个槽中。

[Windows App SDK 稳定版](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) · [Windows 版本与 SDK](https://learn.microsoft.com/windows/apps/get-started/versioning-overview) · [部署说明](https://learn.microsoft.com/windows/apps/package-and-deploy/deploy-overview)

## 复用与适配

| 范围 | WPF 入口 | WinUI 迁移实现 |
| --- | --- | --- |
| 窗口与托盘 | MainWindow、TrayService、单实例门禁、HWND 消息钩子、WindowPowerLifetime | 保留现有单实例、托盘、发现和电源服务。用 WinUI HWND 与 AppWindow 承接激活、系统消息和关闭。异步退出先取消原关闭事件，保存决策及关闭资源完成后再退出 |
| 调度与绑定 | WPF Dispatcher 和 DispatcherTimer、绑定转换器、WPF UI SymbolRegular | 用 DispatcherQueue 承接 UI 发布和计时器。共享 ViewModel 的命令、通知、会话及修订规则，UI 类型在页面边界转换。更新进度使用 OneWay |
| 文件选择与对话框 | WPF OpenFileDialog、SaveFileDialog、退出对话框 | 打开和保存选择器由 AppWindow 持有，使用异步流程。ContentDialog 绑定当前 XamlRoot。取消保留原编辑并跳过写入 |
| 设备 #10 | DevicesPageViewModel、发现/会话协作、DeviceProtocolService | 保留候选、当前会话、USB 发现、模式切换电源意图及会话代次。设备已显示仍由对应反馈表达 |
| 主题 #11 | ThemePageViewModel、DraftPreviewController、IImagePicker、WPF BitmapSource | 沿用模板、素材与保存规则。WinUI 通过编码帧生成 UI 位图，加载完成后重新核对设备、代次及修订。同一离屏逻辑服务预览与输出 |
| 设备设置 #12 | DeviceSettingsPageViewModel | 复用读回、校验和失败保留编辑。每一步的结果对应当前会话，保存失败时单独处理本地保存 |
| 诊断 #13 | DiagnosticsPageViewModel、IExportDestinationPicker | 复用刷新、目录、导出、校验及放弃未确认导出。离开页面时解除视图订阅 |
| 应用设置 #14 | ApplicationSettingsPageViewModel、AppUpdateViewModel、WindowExitService | 保留偏好预览、撤销与保存。复用下载校验与关闭资源的顺序，由隔离后的更新源选择 Beta 包 |

## 离屏渲染

首个 WinUI Beta 保留 Rendering 中的 WPF 离屏实现。DrawingVisual、RenderTargetBitmap、图像解码和 JPEG 编码运行在专用 STA 线程，Core 继续通过平台无关接口取得编码帧。现有 1920×1080 设备输出、旋转、帧限和编码质量回退可以复用，窗口界面用 WinUI 3。

Beta 自包含分发需要同时携带对应的 .NET Desktop Runtime 与 Windows App SDK 原生文件和许可。只有在替代离屏实现通过同一输入的布局、尺寸、旋转、JPEG 帧限、取消与版本覆盖验证，且 USB 实物复核通过后，才能移除 WPF 离屏依赖。

[WPF 与 WinUI 3 模式对应](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/wpf-patterns-winui3) · [Windows App SDK 文件选取器](https://learn.microsoft.com/windows/apps/develop/files/)
