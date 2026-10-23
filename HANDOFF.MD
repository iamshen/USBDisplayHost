# 项目交接要点

## GitHub Release 更新

- 更新源是公开的 `iamshen/USBDisplayHost`，通道 `win-x64`。vpk 1.2.161 的完整包名包含通道：`USBDisplayHost-<Version>-win-x64-full.nupkg`；不要凭示例省略中间字段。
- SDK 的 `DownloadUpdatesAsync` 遇到已有文件会直接返回，必须自行复核缓存的大小、SHA-256 和包内身份；待安装记录保存在独立数据目录，恢复及安装前仍需复核。启动保持 `SetAutoApplyOnStartup(false)`。
- 安装复用退出保存决策；先关闭设备及探测句柄，再启动等待退出的更新器。交接失败只有确认所有者已释放才恢复新建会话；更新重启参数禁止自动切换模式。不能将启动更新器当作更新成功。
- `main` 推送保存 Actions 产物；与项目版本一致的 `vX.Y.Z` 标签才发布 Release。草稿完整上传后转公开，同版本不覆盖。编译／打包与实际升级分开记录，当前仍无签名及端到端升级证据。见 `docs/release/github-release.md`。
- 递增 `Directory.Build.props/Version` 后也要重新评估锁文件：Desktop 锁文件包含项目引用的最低版本，本地增量还原可能继续使用旧值。用 `dotnet restore ... --force-evaluate -p:RestoreLockedMode=false` 更新，再以 `--force --locked-mode` 确认版本一致，避免云端全新还原与本机结果不同。
- 更新进度是只读派生属性，`ProgressBar.Value` 必须显式 `Mode=OneWay`。v0.1.1 安装后在 `MainWindow.Show` 触发 `PropertyPathWorker.CheckReadOnly`，导致主窗口打不开；仅编译打包无法发现。修复后需实际启动安装版，并从低版本测试基线下载正式包，确认旧进程退出、新版本窗口打开及“已更新”状态。原正式 v0.1.1 无法打开更新入口，恢复须手动安装新 Setup。

## 主题重枚举与 WinUSB 接口发现

- Windows 支持 `DeviceInterfaceGUID`（字符串）与 `DeviceInterfaceGUIDs`（多字符串）两种 WinUSB 注册方式。只读后者会漏掉已安装的数据接口；两者合并去重后仍须按实际描述符区分 Display 与 Maintenance。
- 模式切换的 HID 受理和目标连接分属两个阶段。日志中的毫秒级 `ModeTargetPending` 不是八秒等待已结束；PID 从 AD11 变成 ADA0 也不能证明 Display 通道可用。目标接口缺失／歧义应在打开 HID 前拦截，超时核验不选中残缺接口。
- 发生在协调器前的连接错误也必须记录，协议最终结果沿用页面错误编号，避免录屏中的错误无法在 JSONL 找到。核对方式：检查两种 GUID 注册、同设备接口数量、最终 `DeviceProtocol.*` 事件及错误编号。2026-10-23 的源码修复、编译与尚待同事机器确认的项目见 [调试记录分析](docs/verification/theme-interface-debug-2026-10-23.md)。

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
- `Package` 生成的 `.nupkg` 和索引是打包器产物；上传 Release 后才可被更新源发现。当前 Setup 未签名，不代表安装、卸载或硬件验收通过。

当前用法与版本见 `docs/release/offline-installation.md`，实际验证证据见 `docs/verification/offline-installer-2026-10-22.md`。

## 界面精简与运行核对

- 顶部设备候选与设备设置的当前会话归属可能不同。精简时可移除装饰徽章，但应保留当前设备标识；主机预览、实际读回和禁用原因也不是重复说明。
- XAML 中的分组尺寸要与页面 `SizeChanged` 处理一起核对；移除容器内边距后仍需保留窄窗口堆叠间距。目标编译只能核验 XAML 与代码，不能证明视觉效果。
- 单实例锁在安装版与源码构建间共用。传入 `--ui-only` 前先核实驻留进程；新启动请求可能只唤起旧实例，不能把旧窗口截图当作新构建证据。不得为截图直接终止有未确认会话或编辑的实例。
