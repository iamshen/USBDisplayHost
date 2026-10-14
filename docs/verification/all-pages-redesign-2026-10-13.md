# 五页工作区重排与编辑流程简化

实现与运行日期：2026-10-13。文档同步：2026-10-14。关联 REQ／FR-04、06、07、08、10，NFR-11，T-16、T-18、T-19、T-29、T-30；OpenSpec 3.5、3.6、3.7、6.6、7.1、7.6、8.2、8.4。TC-14、TC-29～TC-33 仅用于追踪，未据此标记完整验收通过。

当前状态：页面及编辑流程已有实现，最后一次代码调整后 Release 编译通过；已有部分实际 WPF 运行证据，最终视觉复核未完成。

## 实现范围

| 范围 | 当前实现 | 主要源码 |
|---|---|---|
| 公共样式与导航 | 统一正文前景、主要按钮文字、面板、分隔、反馈和焦点；图标加中文导航，184 DIP 侧边布局、紧凑菜单；选中项与当前页面同步 | `Desktop/Resources/`、`Desktop/MainWindow.xaml` 及 code-behind |
| 设备 | 页头检测、紧凑连接提示、可用设备列表；活动预览与控制并列；连接详情折叠，错误及可用恢复入口独立展示 | `Desktop/Views/DevicesPage.xaml` |
| 主题 | 三模板横排选择，左侧大预览、右侧内容设置；精确颜色输入和六色圆形色板；恢复默认、撤销、应用到设备 | `Desktop/Views/ThemesPage.xaml`、`Desktop/Controls/ColorPalette.xaml` |
| 设备设置 | 方向示意及四项角度选择，帧率、画质输入／滑块；独立即时亮度；固定应用设置底栏及窄屏换行 | `Desktop/Views/DeviceSettingsPage.xaml` 及资源、code-behind |
| 诊断 | 传输状态、USB 已发送帧数、日志写入三项摘要，运行详情、导出工具栏和有界日志列表 | `Desktop/Views/DiagnosticsPage.xaml`、`DiagnosticsPageViewModel.cs` |
| 应用设置 | 窗口与启动、设备模式、软件更新三个紧凑分组；撤销更改／保存设置，异常时提供重读或核实 | `Desktop/Views/ApplicationSettingsPage.xaml` |
| 内置图片 | 未选择图片时使用屏幕轮廓和 USB Display 标识，按背景明暗选择文字颜色；内容由真实离屏管线生成 | `Rendering/Scenes/ScenePainter.cs` |

表中 Desktop、Rendering 分别位于 `src/UsbDisplayHost.Desktop/`、`src/UsbDisplayHost.Rendering/`。

## 编辑与保存行为

主题及设备设置不再暴露草稿读取、保存、归属和核实命令。主题可离线编辑预览；设备参数属于当前会话，设备或会话切换后重新同步。两页应用期间禁用本页编辑，固定内容与会话代次，沿用原首帧应用事务及自动保存恢复配置。

发送成功但本地保存未完成时，保留结果和仅本地重试入口；本地重试不重发设备命令。主题或设备参数尚未应用时，明确退出需确认放弃；应用偏好继续使用保存／放弃／取消，待保存恢复配置独立处理。

影响 `ThemePageViewModel`、`DeviceSettingsPageViewModel`、`App` 组合、`WindowExitService` 及相关页面文案。Core 的不可变候选类型、原设备门禁和 Windows 的旧格式兼容接口保留；旧文件未迁移、未删除。字段默认值和范围不变，未修改协议字段、设备驱动或依赖版本。

## 已有 WPF 截图

截图来自 `--ui-only` 的实际 WPF 窗口。文件尺寸为截图像素尺寸，未记录完整显示器 DPI／系统文字缩放，不能据此认定全部 DPI 条件通过。采集输出为 JPEG，归档扩展名与原始编码一致，图片内容未编辑。

| 画面 | 证据 | 覆盖及限制 |
|---|---|---|
| 设备，1180×820 | [截图](screenshots/all-pages-2026-10-13/01-devices-1180x820.jpg) | 未连接状态；不包含真实候选或设备预览 |
| 图片，1180×820 | [截图](screenshots/all-pages-2026-10-13/02-themes-image-1180x820.jpg) | 内置图片实际渲染 |
| 时钟，1180×820 | [显示日期](screenshots/all-pages-2026-10-13/02-themes-clock-1180x820.jpg)、[隐藏日期](screenshots/all-pages-2026-10-13/02-themes-clock-no-date-1180x820.jpg) | 日期开关与背景色变化反映在预览 |
| 仪表盘，1180×820 | [截图](screenshots/all-pages-2026-10-13/02-themes-dashboard-1180x820.jpg) | CPU／内存为本机来源，不是设备回传 |
| 设备设置，1180×820 | [截图](screenshots/all-pages-2026-10-13/03-device-settings-1180x820.jpg) | 无设备字段禁用与原因，未执行设备写入 |
| 诊断，1180×820 | [截图](screenshots/all-pages-2026-10-13/04-diagnostics-1180x820.jpg) | 实际启动日志；最后列对齐修正之前的画面 |
| 应用设置，1180×820 | [截图](screenshots/all-pages-2026-10-13/05-application-settings-1180x820.jpg) | 分组、开关、页内滚动与保存栏 |
| 侧边导航，1180×820 | [截图](screenshots/all-pages-2026-10-13/06-side-navigation-1180x820.jpg) | 预览侧边布局，随后撤销，未保存偏好 |
| 主题，640×600 | [截图](screenshots/all-pages-2026-10-13/07-themes-640x600.jpg) | 模板、预览及底栏；字段滚动到底的复核未完成 |
| 应用设置，640×600 | [顶部](screenshots/all-pages-2026-10-13/08-application-settings-640x600.jpg)、[滚动底部](screenshots/all-pages-2026-10-13/08-application-settings-bottom-640x600.jpg) | 底部操作可见，更新分组可滚动访问 |

以上截图并非最终构建的全量快照：主题／侧栏截图早于最后滚动条留白调整；全部截图早于最后导航 ListBox 模板修正。最终紧凑菜单背景及诊断列对齐修正仅完成编译，尚未取得修正后的截图。不能将当前图片表述为全部页面最终验收通过。

## 验证记录

| 日期 | 检查 | 结果 |
|---|---|---|
| 2026-10-13 | `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore` | 最后代码修正后成功，四工程输出，0 警告、0 错误 |
| 2026-10-13 | 启动 Release EXE，参数 `--ui-only` | 五页可进入；操作三模板、色板、日期开关、导航布局与撤销，部分窄窗口滚动；留存上表截图 |
| 2026-10-14 | 文档及源码静态核对 | 更新 README、需求／PRD／架构、UI 规范、配置恢复说明和 OpenSpec；本日未修改生产代码、未重新构建或运行应用 |
| 2026-10-14 | `openspec validate build-usb-display-host-v1 --strict --no-interactive` | 通过；规格校验不代表功能验收 |
| 2026-10-14 | `git diff --check` | 通过；存在 LF／CRLF 转换提示，无空白错误 |
| 2026-10-14 | 更新文档的本地相对链接及截图索引核对 | 23 个新增／更新文档的相对目标存在，12 个截图文件按 JPEG 原始编码归档 |

未跟踪文档另与本轮修改前副本核对，避免只检查 Git 已跟踪文件而漏掉新增说明。

## 尚未覆盖

- 最后导航模板和诊断列对齐修正的实际截图；全部页面的紧凑尺寸、长错误文本及键盘操作。
- 高对比度、系统文字放大、各档及混合 DPI、读屏器、标题栏系统菜单和贴靠。
- 导航偏好保存后重启、自启注册、退出分支、文件保存失败／未知结果及恢复。
- 真实设备发现、连接、主题发送、亮度／模式／电源控制与设备呈现。
- 软件安装更新、完整端到端与性能验收。

未编写或执行自动化测试，未安装驱动、未执行设备联调，未提交 Git。文档及编译检查不替代上述验收。
