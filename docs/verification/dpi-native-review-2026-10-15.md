# 本地桌面高 DPI 真实窗口复核

日期：2026-10-15。范围：125%／150%／200% 系统显示缩放下的五页导航、紧凑窗口、滚动与实际控件操作，以及完成后的原比例恢复。关联 T-29、TC-29、TC-30、FR-07。上述范围已完成，本轮未发现剩余的明确裁切、重叠、导航选中错误或点击偏移；不表示完整产品或硬件验收通过。

## 环境与操作方式

| 项目 | 实际环境 |
|---|---|
| Windows 会话 | 本地 `console`，Session 3 |
| 活动显示器 | 显示器 2，系统模式为“仅在 2 上显示” |
| 显示器分辨率 | 2560×1440 |
| 原始／最终系统缩放 | 100%；完成后已通过显示设置恢复并确认，分辨率仍为 2560×1440 |
| 目标系统缩放 | 125%、150%、200%，通过 Windows 显示设置实际切换 |
| 应用 | 正常 Desktop Release 输出，`--ui-only`；应用清单声明 `PerMonitorV2` |
| 操作与截图 | Computer Use 对真实 Windows 窗口执行点击、拖动、滚动和键盘输入，并保存工具返回的原始 JPEG |

环境元数据见 [environment.json](screenshots/dpi-native-2026-10-15/environment.json)。缩放比例依据系统显示设置确认；工具截图报告逻辑尺寸，不能从截图宽高推断实际 DPI，也不能据这些 JPEG 判断原始物理像素清晰度。644×404 等尺寸仅表示工具返回的窗口截图范围。本轮未以进程内缩放、`RenderTargetBitmap` 或设计图替代真实桌面操作。

本记录补充此前 [真实窗口截图与交互复核](native-window-review-2026-10-15.md)；此前 RDP 会话的缩放限制不适用于本次已确认的本地会话。

## 本轮发现与最小修复

从 100% 切至 125% 后，窗口出现“托盘操作未完成，请从窗口重试”。[故障现场](screenshots/dpi-native-2026-10-15/125-initial-tray-error.jpg) 对应错误编号 `e6e5c6930c4344c59ba6362597563a26`。本地诊断记录时间为 `2026-10-15T15:36:12.6400184+08:00`，阶段为 `Tray`，异常类型为 `System.InvalidOperationException`，调用栈为 `NotificationAreaIcon.Add → TrayService.OnMessage`。

`TrayService` 收到 `TaskbarCreated` 后调用图标注册。Windows 在主显示器 DPI 改变时也会广播该消息；它不只用于 Explorer 启动后的任务栏重建。[微软 TaskbarCreated 文档](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)

修改仅限 `src/UsbDisplayHost.Windows/Lifecycle/NotificationAreaIcon.cs`：先调用 `NIM_ADD`；若添加失败，再以相同 HWND／Id 调用 `NIM_MODIFY`，确认已有图标能够更新。任务栏重建且图标不存在时仍正常添加；重复注册时不再直接将图标标记为不可用。两次操作均失败仍抛出异常，`NIM_SETVERSION` 初始化及其失败清理保留。[Shell_NotifyIcon 操作语义](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona)

原日志不含异常文本，不能仅由该日志区分添加失败与版本初始化失败。修复后启动的同一实例从 125% 切至 150%、200%，再恢复 100%，未再出现提示。该实例自 `15:40:04.5106326` 启动至 `15:56:21.6119186` 的本地日志只有 1 条 `Application/Starting`，`Tray`、`Error`、`Fatal` 均为 0；[恢复后的诊断页](screenshots/dpi-native-2026-10-15/100-restored-diagnostics.jpg) 同样显示最近事件 1 / 500。日志来源为 `%LOCALAPPDATA%/UsbDisplayHost.Data/diagnostics/logs/diagnostic-0.jsonl`。本轮未实际点击托盘图标或重启 Explorer，不能据此关闭这些验收项。

修复后执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`：**0 警告、0 错误**。文档与规格同步后，`git diff --check` 和 `openspec validate build-usb-display-host-v1 --strict --no-interactive` 均通过。未编写或运行自动化测试，未操作硬件，未提交 Git。

## 已取得证据与结果

| 系统缩放／步骤 | 结果 | 证据 |
|---|---|---|
| 100% 基线 | 已记录环境与设备页 | [基线窗口](screenshots/dpi-native-2026-10-15/00-baseline-100-devices.jpg) |
| 125% 五页导航 | 修复后已完成真实切页与截图 | [设备](screenshots/dpi-native-2026-10-15/125-01-devices.jpg)、[主题](screenshots/dpi-native-2026-10-15/125-02-themes.jpg)、[设备设置](screenshots/dpi-native-2026-10-15/125-03-device-settings.jpg)、[诊断](screenshots/dpi-native-2026-10-15/125-04-diagnostics.jpg)、[应用设置](screenshots/dpi-native-2026-10-15/125-05-application-settings.jpg) |
| 125% 紧凑窗口 | 已完成 644×404 应用设置滚动、五项菜单与 Esc 返回菜单按钮焦点 | [设置页首](screenshots/dpi-native-2026-10-15/125-06-compact-settings-top.jpg)、[设置页尾](screenshots/dpi-native-2026-10-15/125-07-compact-settings-bottom.jpg)、[五项菜单](screenshots/dpi-native-2026-10-15/125-08-compact-menu.jpg) |
| 150% 五页导航与紧凑布局 | 真实点击五页；紧凑窗口及最大化布局正常 | [紧凑设置](screenshots/dpi-native-2026-10-15/150-01-compact-settings.jpg)、[最大化设置](screenshots/dpi-native-2026-10-15/150-02-settings-maximized.jpg)、[设备](screenshots/dpi-native-2026-10-15/150-03-devices.jpg)、[时钟](screenshots/dpi-native-2026-10-15/150-04-clock.jpg)、[设备设置](screenshots/dpi-native-2026-10-15/150-06-device-settings.jpg)、[诊断](screenshots/dpi-native-2026-10-15/150-07-diagnostics.jpg) |
| 150% 时钟交互 | 实际点击 24 小时制复选框，预览切换为带下午标识的 12 小时制；随后撤销 | [12 小时制点击结果](screenshots/dpi-native-2026-10-15/150-05-clock-12h-interaction.jpg) |
| 200% 紧凑五页导航 | 五项菜单完整；切页后当前页同步；设备页“先预览主题”可进入编辑页 | [菜单](screenshots/dpi-native-2026-10-15/200-03-compact-navigation.jpg)、[设备](screenshots/dpi-native-2026-10-15/200-08-compact-devices.jpg)、[主题](screenshots/dpi-native-2026-10-15/200-09-compact-theme-navigation.jpg) |
| 200% 紧凑长内容与操作栏 | 实际拖动滚动条，诊断日志与按钮、亮度设置均可达；设备和应用设置底栏保持可见 | [诊断页首](screenshots/dpi-native-2026-10-15/200-01-diagnostics.jpg)／[页尾](screenshots/dpi-native-2026-10-15/200-02-compact-diagnostics-bottom.jpg)、[设备设置页首](screenshots/dpi-native-2026-10-15/200-04-compact-device-settings.jpg)／[页尾](screenshots/dpi-native-2026-10-15/200-05-compact-device-settings-bottom.jpg)、[应用设置页首](screenshots/dpi-native-2026-10-15/200-07-compact-application-settings-top.jpg)／[页尾](screenshots/dpi-native-2026-10-15/200-06-compact-application-settings.jpg) |
| 200% 仪表盘与最大化交互 | 实际切换模板、滚动到预览、最大化、点击主机温度复选框；预览即时新增“不可用”温度卡片；滚回页首后点击撤销，恢复原图片草稿 | [紧凑预览](screenshots/dpi-native-2026-10-15/200-10-compact-dashboard.jpg)、[指标点击结果](screenshots/dpi-native-2026-10-15/200-11-dashboard-interaction.jpg)、[最大化页首](screenshots/dpi-native-2026-10-15/200-12-maximized-dashboard.jpg) |
| 托盘修复与恢复原始 100% | 同一运行实例完成 125% → 150% → 200% → 100%；系统值确认恢复，诊断未新增错误 | [最终诊断](screenshots/dpi-native-2026-10-15/100-restored-diagnostics.jpg)、[环境记录](screenshots/dpi-native-2026-10-15/environment.json) |

## 证据归档约定

31 张原始 JPEG 保存在 `docs/verification/screenshots/dpi-native-2026-10-15/`，包含 2 张修复前故障现场。命名中的 `125`、`150`、`200` 对应已在系统设置中选择的比例，不来自图片尺寸推断。系统设置截图包含账户信息，未归档到仓库；比例恢复由实际显示设置观察确认并记入元数据。

- `00-baseline-100-devices.jpg`：原始 100% 环境基线。
- `125-initial-tray-error.jpg`、`125-clock-before-fix.jpg`：修复前现场，不能作为修复通过证据。
- `125-01` 至 `125-08`：托盘修复后构建的五页和紧凑窗口证据。
- `150-01` 至 `150-07`：五页、紧凑设置与时钟交互。
- `200-01` 至 `200-12`：紧凑五页、滚动、导航与最大化仪表盘交互。
- `100-restored-diagnostics.jpg`：恢复原比例后的最终诊断页。

## 结论与范围

125%、150%、200% 下的实际页面布局、导航和所列交互通过本轮复核。高 DPI 切换触发的托盘注册错误已修复并完成缩放往返复验。系统已恢复 100%，主题临时编辑已撤销；未保存新的应用偏好。

混合 DPI 跨显示器移动、托盘图标点击与 Explorer 重建、系统文字放大、读屏器、高对比度和真实硬件端到端流程尚未由本轮证据覆盖。关联 T-29／TC-29／TC-30 的完整验收保持未完成。
