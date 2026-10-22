# 项目交接要点

## 系统 DPI 变化与托盘注册

- `TaskbarCreated` 也可能随系统 DPI 变化触发，不能假定原图标已经消失。仅调用 `NIM_ADD` 会将已有图标的重复注册误报为托盘不可用。
- `NotificationAreaIcon.Add` 先添加，失败后以相同 HWND／Id 更新；两者均失败仍报告错误，保留版本初始化和失败清理。
- 验证方法：在本地桌面实际切换系统缩放，再恢复原值，检查同一实例的诊断日志。2026-10-15 已完成 125% → 150% → 200% → 100%，修复后无新增 Tray／Error 事件；托盘点击及 Explorer 重建尚未覆盖。详见 [高 DPI 复核](docs/verification/dpi-native-review-2026-10-15.md)。

## 导航菜单的当前页勾选

- WPF 菜单项仅设置 `IsChecked` 不足以显示勾选，需要启用 `IsCheckable`；重复点击当前页也必须重新同步选中状态。
- 验证方法：实际打开更多菜单，点击当前页后再次展开，再切至另一页，确认只勾选当前页且按钮的可访问名称同步。证据见 [真实窗口复核](docs/verification/native-window-review-2026-10-15.md)。

## 真实窗口与渲染证据

- 系统显示设置可能禁止远程会话修改缩放，应使用可实际调整缩放的本地桌面。先记录原值，完成后恢复并确认。
- `RenderTargetBitmap` 只验证进程内布局；真实点击、滚动和 DPI 变化必须操作实际窗口。桌面工具返回逻辑尺寸图像时，不能用图片宽高推断 DPI 或原始物理像素清晰度。
- 截图前确认窗口处于稳定、未被遮挡的状态；不要将过渡帧、其他窗口或含账户信息的系统设置截图归档为应用证据。

## 离线安装打包

- `dotnet publish -r win-x64` 会向引用工程传播运行时标识。仅在 Desktop 声明 RID 会使其他工程在锁定还原时出现 NU1004；四工程统一在 `Directory.Build.props` 声明 `RuntimeIdentifiers`，各锁文件同时保留常规与 win-x64 目标。Core 仍保持 `net10.0`，不引入 Windows API 依赖。
- Velopack 的 `vpk pack` 默认会执行主程序核验入口。当前 `Package` 使用 `--skipVeloAppCheck`，避免打包执行应用；不得将这一跳过的检查报告为通过。用户禁止测试时，不运行安装包、主程序或安装回调。
- Velopack 默认允许启动时应用缓存更新；入口必须保持 `SetAutoApplyOnStartup(false)`，安装回调先于单实例锁和设备服务。
- .NET 自包含发布未自动复制运行时许可文本。`licenses/dotnet-runtime`、`licenses/dotnet-desktop` 来自 10.0.12 原包；更新 SDK／运行时后同步核对并更新原始声明。
- `Package` 生成的 `.nupkg` 和本地索引是打包器产物，不代表已接入或发布自动更新。当前 Setup 未签名，不代表安装、卸载或硬件验收通过。

当前用法与版本见 `docs/release/offline-installation.md`，实际验证证据见 `docs/verification/offline-installer-2026-10-22.md`。

## 界面精简与运行核对

- 顶部设备候选与设备设置的当前会话归属可能不同。精简时可移除装饰徽章，但应保留当前设备标识；主机预览、实际读回和禁用原因也不是重复说明。
- XAML 中的分组尺寸要与页面 `SizeChanged` 处理一起核对；移除容器内边距后仍需保留窄窗口堆叠间距。目标编译只能核验 XAML 与代码，不能证明视觉效果。
- 单实例锁在安装版与源码构建间共用。传入 `--ui-only` 前先核实驻留进程；新启动请求可能只唤起旧实例，不能把旧窗口截图当作新构建证据。不得为截图直接终止有未确认会话或编辑的实例。
