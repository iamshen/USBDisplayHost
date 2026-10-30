# 项目交接要点

## Issue 机器人标签

- 表单齐全不代表信息足以复现。机器人仅维护 `form-incomplete`，不能移除维护者用于索要日志或澄清的 `needs-info`；自动修复遇到任一标签都先等待。验证覆盖缺项添加、重复检查不刷屏、补齐移除和人工标签保留。

## GitHub Release 更新

- 更新源是公开的 `iamshen/USBDisplayHost`，通道 `win-x64`。vpk 1.2.161 的完整包名包含通道：`USBDisplayHost-<Version>-win-x64-full.nupkg`；不要凭示例省略中间字段。
- SDK 的 `DownloadUpdatesAsync` 遇到已有文件会直接返回，必须自行复核缓存的大小、SHA-256 和包内身份；待安装记录保存在独立数据目录，恢复及安装前仍需复核。启动保持 `SetAutoApplyOnStartup(false)`。
- 安装复用退出保存决策；先关闭设备及探测句柄，再启动等待退出的更新器。交接失败只有确认所有者已释放才恢复新建会话；更新重启参数禁止自动切换模式。不能将启动更新器当作更新成功。
- `main` 推送保存 Actions 产物；与项目版本一致的 `vX.Y.Z` 标签才发布 Release。草稿完整上传后转公开，同版本不覆盖。v0.1.2 已完成无设备下的 GitHub 下载、确认退出、安装重启及 Setup 覆盖安装；签名和实物会话验证仍待完成。见 `docs/verification/upgrade-v0.1.2-2026-10-23.md`。
- 递增 `Directory.Build.props/Version` 后也要重新评估锁文件：Desktop 锁文件包含项目引用的最低版本，本地增量还原可能继续使用旧值。用 `dotnet restore ... --force-evaluate -p:RestoreLockedMode=false` 更新，再以 `--force --locked-mode` 确认版本一致，避免云端全新还原与本机结果不同。
- 更新进度是只读派生属性，`ProgressBar.Value` 必须显式 `Mode=OneWay`。v0.1.1 安装后在 `MainWindow.Show` 触发 `PropertyPathWorker.CheckReadOnly`，导致主窗口打不开；仅编译打包无法发现。修复后需实际启动安装版，并从低版本测试基线下载正式包，确认旧进程退出、新版本窗口打开及“已更新”状态。原正式 v0.1.1 无法打开更新入口，恢复须手动安装新 Setup。

## 主题重枚举与 WinUSB 接口发现

- 主题首帧完整发送并提交活动配置后，成功核验为 Theme／On 时，应将主机推流状态从 Stopped 转为 Running；否则每秒状态查询会保持帧通道暂停，下一次应用报 `CandidateChannelUnavailable`，尚未进入保存阶段。保留 Paused／Faulted／Off 和观察失败，不将 Running 当作设备呈现反馈。回归应串联“首帧应用→状态查询→再次应用”，不能只验证首帧发送。
- 显式模式切换在旧会话结束前捕获 PowerIntent，正常目标连接与超时后核验都传递同一值；普通连接／重连不覆盖原有恢复规则。重枚举后的 DisplayEnabled 不能替代切换前的电源意图，HID／EP0 背光一致性检查仍须保留。
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

## 导航布局选项的键盘切换

- 导航 `RadioButton` 的 `GroupName` 只维护选中互斥；现有滚动容器内还需显式处理方向键切换与焦点。将 `PreviewKeyDown` 限定在该选项组，使用 `SetCurrentValue` 保留双向绑定，并聚焦目标选项，沿用预览与撤销流程。
- 2026-10-23 在默认打开大小实际复核四个方向键、Tab／Shift+Tab 和撤销，选中、焦点与布局同步，磁盘配置未改写。原始截图和运行记录仅在本机忽略目录归档；此处为历史验证摘要，不代表当前版本重新验收。其他尺寸与 DPI 按 AGENTS.md 的当前任务范围处理。

## 真实窗口与渲染证据

- 系统显示设置可能禁止远程会话修改缩放，应使用可实际调整缩放的本地桌面。先记录原值，完成后恢复并确认。
- `RenderTargetBitmap` 只验证进程内布局；真实点击、滚动和 DPI 变化必须操作实际窗口。桌面工具返回逻辑尺寸图像时，不能用图片宽高推断 DPI 或原始物理像素清晰度。
- 截图前确认窗口处于稳定、未被遮挡的状态；不要将过渡帧、其他窗口或含账户信息的系统设置截图归档为应用证据。
- Computer Use 报 `GetCursorPos` 拒绝访问且捕获全黑时，即使能读取可访问树，也不能认定真实窗口可交互或视觉检查通过。停止输入，确认用户桌面已解锁且会话可交互后重新获取窗口；不操作锁屏、不用进程内渲染替代。2026-10-22 原始运行记录仅在本机忽略目录归档。

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

## 调试产物入库

- 原始调试输出曾放在未忽略的 docs/verification 下，容易被批量暂存。今后写入 .artifacts/<task>/，文档只保留有维护价值的结论；提交前检查暂存文件列表及新增内容。忽略规则不影响已跟踪文件，历史清理须单独核对引用。
