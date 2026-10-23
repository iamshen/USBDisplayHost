# USB Display 上位机

文档更新：2026-10-23。当前范围：五页原生工作区、设备协议组合、GitHub 更新与安装包自动打包，完整运行及硬件验收未完成。

本项目面向 AD102 与当前固件的 1920×1080 屏幕，开发 Windows 原生桌面上位机，提供主题显示、设备控制、硬件信息展示和主题态／副屏态切换。

仓库已建立四个生产工程，接入图片／时钟／仪表盘编辑与真实预览，并按 AD102 V2 固件源码组合设备协议链路。主题与设备设置通过明确应用生效，成功后自动保存对应设备的恢复配置；页面不提供草稿管理。五页工作区已统一页头、分组与操作层次，时钟和仪表盘采用分层排版与指标卡片；完整产品功能与设备联调仍在实施。

## 技术方向

- C# / .NET 10 LTS，WPF/XAML＋WPF UI 原生桌面界面，CommunityToolkit.Mvvm 组织交互。
- Desktop、Core、Windows、Rendering 四个生产工程组成模块化单体；Core 保持平台无关，适配器通过构造函数接入。
- 正常运行时单进程；Windows WinUSB 与 HID API 负责通信，安装更新由辅助进程交接。
- 独立内容模型，通过 WPF DrawingVisual / RenderTargetBitmap 离屏绘图，再编码 JPEG；不截取管理窗口。
- 原生绘图和编码先做真实屏幕验证，验证不通过时仅替换有问题的渲染或编码模块。
- 内存编辑、当前会话配置、本地恢复配置与设备实际反馈分开；会话代次和内容修订共同隔离迟到结果。
- WPF UI 提供窗口、导航、主题、设置控件、对话框和短时提示；统一资源与业务组件负责设备状态、真实比例预览及操作反馈，覆盖紧凑布局、高 DPI、键盘和高对比度验收。
- JSON 本地配置、Serilog 本地滚动日志、仓库内任务与验收记录。
- Velopack 安装与软件更新，通过 HTTPS 发布源检查版本；用户发起下载和重启安装，断网仍可使用核心功能。

以上为设计基线；窗口已接入 WPF UI、MVVM Toolkit 和结构化日志，Velopack 1.2.161 已接入安装生命周期、GitHub 检查下载与确认重启安装。签名和真实升级验收仍待任务 2.5／12.x。编码器兼容性、固件能力和驱动绑定仍须样机验证。

实际输入与未完成依赖记录：[样机／驱动](docs/contracts/device-baseline.md)、[主题数据面](docs/contracts/data-plane.md)、[HID 控制](docs/contracts/hid-control.md)、[安装更新通道](docs/release/update-channel.md)。这些记录区分已确定输入和待交付条件，不构成已验收的设备兼容契约。

## 工程与构建

| 工程 | 目标框架 | 直接项目引用 |
|---|---|---|
| `src/UsbDisplayHost.Core` | `net10.0` | 无 |
| `src/UsbDisplayHost.Windows` | `net10.0-windows` | Core |
| `src/UsbDisplayHost.Rendering` | `net10.0-windows`，WPF | Core |
| `src/UsbDisplayHost.Desktop` | `net10.0-windows`，WPF WinExe | Core、Windows、Rendering |

四个工程统一启用 nullable 和 .NET 分析器。Core 已建立身份、能力证据、结果、快照、配置分层、设置保存协调、有界设备协调器和适配接口；Windows 已有配置／应用设置及旧格式兼容存储、受限素材、自启和诊断适配；Rendering 已有 STA 离屏绘图、三模板、图片适配与有界 JPEG 编码。Desktop 已有 FluentWindow／TitleBar、五页导航、动态主题和持久错误提示，并组合唯一设备协调器及会话生命周期。主题／设备设置应用、主机预览、显式断开、本地保存处理及窗口／托盘暂停恢复入口已编译；App 已接入显式检测、连接和原生协议管线，已有无设备窗口操作证据，完整窗口及设备行为未验收。

2026-10-08 在 Windows 开发机使用 .NET SDK 10.0.401 执行以下目标构建，四工程均成功，0 警告、0 错误：

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release
```

SDK 已由 `global.json` 精确锁定；集中包版本及四份锁文件启用默认锁定还原。Windows 开发环境中可从仓库根目录使用：

```powershell
./build.ps1 -Action Restore
./build.ps1 -Action Build
./build.ps1 -Action Publish
./build.ps1 -Action Package
```

Restore 执行锁定还原；Build 执行目标 Release 构建；Publish 清理生成目录后输出 win-x64 自包含文件到 `.artifacts/publish/win-x64`，不启用裁剪或 AOT。Package 执行相同发布步骤，再恢复仓库固定版本的 vpk，生成 `.artifacts/installers/win-x64/<Version>/USBDisplayHost-win-x64-Setup.exe`。版本来自 `Directory.Build.props`，当前为 `0.1.0`。SDK 依赖与工具首次还原需要网络，目标机器安装无需在线下载 .NET。

所有入口均不运行应用、测试、设备操作或驱动安装；Package 使用 `--skipVeloAppCheck` 跳过会执行应用的入口探测。安装包未签名，不能把打包成功当作安装验收。使用方式、数据保留和驱动边界见 [离线安装说明](docs/release/offline-installation.md)。推送 `main` 自动生成 Actions 产物，推送与项目版本一致的 `vX.Y.Z` 标签自动发布 GitHub Release，详见 [发布及更新说明](docs/release/github-release.md)。测试工程及入口尚未创建，2.2／2.3／12.x 的完整验收仍待完成。

无设备 UI 运行使用以下入口。`--ui-only` 不附加设备协议，不启动 USB 发现、启动自动切换或设备电源生命周期；重新检测与设备控制保持不可用。应用偏好、素材导入和诊断仍使用当前用户的数据目录，相关保存属于真实本地写入；主题编辑本身只保留在内存。

```powershell
dotnet run --project src/UsbDisplayHost.Desktop -- --ui-only
```

2026-10-13 已完成五页布局、共享控件层级和编辑流程重排；最终目标 Release 编译成功，0 警告、0 错误。已在无设备 WPF 窗口操作五页导航、三模板预览、色板、日期开关、布局切换与撤销，以及部分窄窗口滚动。最后的紧凑菜单背景和诊断列对齐修正尚无最终截图。详见 [全页面重排记录](docs/verification/all-pages-redesign-2026-10-13.md)。2026-10-14 仅同步文档，未重新构建或运行应用。

2026-10-15 完成五页视觉细化、主题编辑响应式调整、时钟及仪表盘内容排版，修复短窗口正文过窄、导航选中、亮度初值及日志列错位。已完成五页常规／紧凑窗口的真实点击、滚动和编辑复核，并在本地桌面完成 125%／150%／200% 高 DPI 操作与截图，修复缩放触发的托盘注册错误，恢复原始 100%；最后生产修改后的 Release 构建 0 警告、0 错误。详见 [UI 完善记录](docs/verification/ui-polish-2026-10-15.md)、[真实窗口交互](docs/verification/native-window-review-2026-10-15.md) 和 [高 DPI 复核](docs/verification/dpi-native-review-2026-10-15.md)。混合 DPI、完整辅助技术和真实设备验收仍未完成。

2026-10-22 已将五页界面精简及离线安装打包与上述改动合并，保留响应式、导航、DPI／托盘和内容渲染修复。合并后四工程 Release 构建 0 警告、0 错误；真实窗口尚待复核，未重新生成安装包。见 [界面精简记录](docs/verification/ui-simplification-2026-10-22.md) 和 [另一台电脑的 UI 调试提示词](docs/verification/ui-debug-handoff-2026-10-22.md)。

详情见 [工程基线实施记录](docs/verification/engineering-baseline.md)、[生产依赖清单](docs/release/dependencies.md) 和 [范围／责任／需求追踪](docs/contracts/scope-baseline.md)。

## 模块实施记录

以下链接记录各模块实施时点的证据，历史运行边界不代表当前全部能力。当前页面与配置行为以 [UI 规范](docs/06_UI设计规范.md)、[配置与自动恢复](docs/07_配置与自动恢复.md) 和 [界面精简记录](docs/verification/ui-simplification-2026-10-22.md) 为准。

模块实施范围见 [本地存储状态](docs/verification/storage-baseline.md)、[离屏渲染状态](docs/verification/rendering-baseline.md)、[离线主题工作区](docs/verification/theme-workspace.md)、[主机指标](docs/verification/telemetry-baseline.md)、[单实例／托盘](docs/verification/lifecycle-baseline.md)、[应用设置／自启](docs/verification/application-settings-baseline.md) 和 [日志／诊断导出](docs/verification/diagnostics-baseline.md)。普通关闭使用已保存行为，默认收至托盘；显式退出分别处理未保存应用偏好、未应用编辑和待保存恢复配置。托盘已共用设备页的模式切换命令。模块编译不代表窗口、注册表、文件恢复、图像内容、采样、日志导出或真实设备已验收。

2026-10-12 已完成当前固件源码范围内的离线协议接入，详见 [协议接入、字段与固件边界](docs/verification/protocol-offline-integration.md)。包括发现／连接、主题初始化和发送、亮度／电源确认、模式重枚举及断线恢复。四工程 Release 构建通过；未运行应用或测试、未连接设备、未提交。以下模块记录保留各自实施时点的范围，其中“尚未组合”的历史描述以该接入记录为准。

同日补齐 [离线工作区功能](docs/verification/offline-workspace-completion-2026-10-12.md)：设备设置页即时亮度及最终值合并、启动自动发现和显式保存的自动模式策略、副屏切出提示与记忆、1Hz 串行状态刷新，以及 8 秒模式等待后的状态核实。目标 Release 编译通过；安装更新本轮跳过，真实窗口、设备和完整副屏显示路径仍未验收。

设备操作、状态迁移和帧写入已关联原错误编号，历史记录见 [诊断关联](docs/verification/offline-draft-context-2026-10-12.md)。当前页面使用内存编辑与应用后自动保存，使用说明见 [配置与自动恢复](docs/07_配置与自动恢复.md)。旧版文件和底层兼容接口保留，页面不再提供其读写入口。

设备逻辑基础见 [身份与单写者协调器](docs/verification/device-coordinator-baseline.md)。尚无原生发现／连接，不用设计中的 VID/PID 值冒充已冻结的匹配表。

Windows [WinUSB 打开与查询模块](docs/verification/winusb-opening-baseline.md) 已实现显式绑定、实际身份／接口／端点读回及安全句柄所有权并通过编译；尚未组合原生发现与 Core 连接用例，未调用设备接口，没有默认匹配表或驱动验收结论。

[WinUSB 重叠传输](docs/verification/winusb-io-baseline.md) 已实现 IDisplayTransport、单在途写入／EP0、显式超时／短包策略及取消收敛；原生请求完成后才释放内存，未知完成保留所有者。仍未调用原生接口或完成实际会话组合，固件握手与 parser 清理尚缺冻结输入。

[主题初始化](docs/verification/theme-initialization-baseline.md) 已实现显式 EP0 ready／尺寸核验、逐项亮屏命令与 Off 安全分支。无 parser 清理收据则不发亮屏序列，失败不继续发送。实际固件契约、清理路径及连接／恢复组合仍未完成。

数据面字节编码见 [命令与分片状态](docs/verification/frame-fragmentation-baseline.md)，发送前检查见 [JPEG 校验状态](docs/verification/jpeg-validation-baseline.md)。结构、固件格式匹配和完整像素解码模块已编译，尚未组合真实发送入口；无默认固件兼容清单，协议边界、实际 JPEG 解码与设备接收仍无运行证据。

Core [整帧传输执行者](docs/verification/frame-transfer-baseline.md) 已实现串行分片、实际长度核对、失败停片及一次恢复守卫。[帧边界调度](docs/verification/frame-scheduling-baseline.md) 已组合一个最新待发槽、首帧保护和有界控制队列，未知收敛保留所有者并拒绝新工作；USB 收据不代表屏幕呈现。原生传输、固件清理、活动事务和 UI 仍待组合。

[主题／设置应用事务](docs/verification/theme-application-baseline.md) 已共用候选准备、独立管线修订、受保护首帧、协调器活动及帧率提交和独立恢复保存；两种应用保留另一类当前字段，保存失败可仅重试本地，未知结果先核实。实际会话工作、活动产帧、其他配置写入协调和页面反馈仍待组合，尚无设备呈现证据。

[发送节奏](docs/verification/frame-pacing-baseline.md) 已组合用户帧率上限、连续慢帧降速及健康成功样本 30 秒逐级恢复。无真实测量策略时固定按用户上限；协议错误停流，静态空闲不算稳定运行。阈值、实际帧率与恢复行为尚无运行证据。

[活动产帧](docs/verification/active-frame-baseline.md) 已实现动态主题 1Hz 循环、静态首帧更新、与应用事务共用帧号及有界发送回执；产帧只读取当前活动配置，停止时等待已有工作。实际会话、指标来源、预览与生命周期尚未组合，未运行定时或设备操作。

[主题会话所有者](docs/verification/theme-session-pipeline-baseline.md) 已统一应用／保存互斥及产帧、调度、收敛、传输释放顺序；未知关闭保留未释放状态。实际适配器与原生会话创建尚未接入，关闭确认前保留整个会话所有者。

[Desktop 会话退出](docs/verification/device-session-lifetime-baseline.md) 已接入强引用保留和显式退出检查：会话关闭未确认则保留窗口与共享资源。尚无真实会话创建／附加，窗口行为和系统关机期限未验证。

[素材引用](docs/verification/asset-reference-baseline.md) 已接入进程内租约及关闭后的活动释放／待保存交接，普通退出提供仅本地保存或明确放弃指定候选。磁盘／备份及未知草稿保存引用核实、跨会话展示、无引用清理与实际行为验证尚未完成，未新增素材删除操作。

旧版 [主题草稿保存核实](docs/verification/theme-draft-save-baseline.md) 和 [设备设置草稿存储](docs/verification/device-settings-draft-baseline.md) 记录兼容接口及文件协议。当前 Desktop 已移除这些页面命令；现存文件不迁移、不删除，素材引用扫描继续考虑遗留文件。

[磁盘素材引用扫描](docs/verification/asset-disk-inventory-baseline.md) 已实现恢复／草稿／设备设置草稿的主文件及备份核实，未知格式或损坏条目阻止完整扫描。结果仅为只读快照，未接入删除入口；一致清理事务及实际文件行为尚未验证。

[共同存储边界](docs/verification/storage-coordination-baseline.md) 已组合配置与素材的同目录协调器和引用索引；导入结果持有租约，编辑采用或放弃后释放。删除事务尚未接入，互斥与转交行为未运行验证。

[设备设置页](docs/verification/device-settings-redesign-2026-10-13.md) 使用方向示意、四项角度选择、帧率及画质输入、独立即时亮度和应用设置底栏。参数属于当前会话；切换设备重新同步活动值，不保留另一台设备的未应用输入。

结构化日志位于 `%LocalAppData%/UsbDisplayHost.Data/diagnostics/logs/`，最多五个 10MiB 文件，后台队列 512 条；诊断页显示最近 500 条事件及写入／丢弃／失败状态，提供受限 ZIP 导出。启动应急新记录也使用同一五文件配额；旧版本遗留的 `diagnostics/last-error.txt` 不读写或导出。普通页面只显示动作建议和错误编号，后台提交不等同已经落盘。

托管 UI 异常保留窗口并提示失败；启动窗口初始化失败时尝试显示独立错误窗口。未观察任务异常记录后通知 UI，但未来后台任务仍须在所属操作边界捕获并返回失败。CLR 已判定终止的后台线程异常只能尽力记录；内存耗尽、栈溢出、原生进程故障及 WPF 运行环境无法初始化时，不能保证进程继续运行。全局处理不代表业务状态恢复，也不自动重试操作。以上路径仅完成编译验证，未运行应用或故障注入。

## 文档索引

| 文档 | 内容 |
|---|---|
| [01 需求分析](docs/01_需求分析.md) | 产品边界、来源能力、冲突、需求清单和固件前置条件 |
| [02 PRD](docs/02_PRD.md) | 页面、用户流程、功能行为、默认值和可测试的验收标准 |
| [03 软件技术架构](docs/03_软件技术架构.md) | 原生桌面方案、模块、线程、协议、设备状态和恢复 |
| [04 项目管理](docs/04_项目管理.md) | 阶段、任务、依赖、责任、风险与需求追踪 |
| [05 项目规范](docs/05_项目规范.md) | 开发约束、测试场景、代码评审和发布要求 |
| [06 UI 设计规范](docs/06_UI设计规范.md) | 深色分层、可配置顶部／侧边导航、五页布局、交互与 WPF 组织、DPI 和可访问性要求 |
| [07 配置与自动恢复](docs/07_配置与自动恢复.md) | 三模板、内存编辑、自动保存、应用结果及本地恢复 |

建议先评审需求与 PRD，再按架构实施，以项目管理中的任务和项目规范中的测试作为交付依据。

## 交付边界

内部验证版完成主题态出图、配置与恢复；正式首版还须完成 HID 双向控制、双模切换、纯熄屏、安装及软件更新验证。内部版本不能代替正式双模产品。

软件更新仅更新上位机应用。触摸、桌面镜像、设备固件 OTA、开机动画下载、多设备同时推流和主题编辑器列为后续候选。本版不承诺 30/60fps，不以源文中的性能陈述代替实测。

## 来源与本次验证

两份原始参考资料原样保留：

- [USB 副屏协议与局部刷新原理](docs/参考资料/USB_副屏协议与局部刷新原理.md)
- [上位机实现文档](docs/参考资料/上位机实现文档.md)

文档基线包含协议分析、架构、应用一致性和 UI 规则。已取得 AD102 V2 固件源码并记录对应提交，尚无真实设备及驱动验收结论。生产工程 Release 构建、部分无设备窗口操作及最终五页 WPF 布局渲染复核有证据；未编写或运行自动化测试。完整输入、DPI、性能与兼容结论仍须实际环境证据。

## 应用图标资源

EXE、主窗口、标题栏与托盘使用蓝灰几何 Logo v2。资源已包含在 Desktop 工程，普通构建无需重新生成。需要从已确认原稿更新尺寸时执行：

```powershell
./docs/design/2026-10-dark-ui/branding/export-icons.ps1
```

该命令生成 `Assets/Branding` 下的多尺寸 PNG 与 ICO；构建／发布会保留托盘所需 ICO 文件。接入与编译记录见 [品牌图标记录](docs/verification/application-branding-2026-10-12.md)。
