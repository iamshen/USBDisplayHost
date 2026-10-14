# 本地运行记录

日期：2026-10-09。运行对象为启动时的 Release `UsbDisplayHost.Desktop.exe`；进程不会随之后独立目录构建自动更新。本地软件运行已获授权。未创建测试代码，未发送设备命令，未安装驱动或执行固件／更新操作。

通过 Computer Use 启动后，返回唯一 USB Display 主窗口；辅助技术树能读取标题栏、五页导航入口及首次设备页。当前未选择设备，连接为未连接，模式／电源为未知，推流为已停止；暂停、恢复、断开、核实恢复配置、发现与连接动作均禁用。未观察到启动错误提示。

窗口状态截图返回锁屏画面，不能作为实际应用布局或可交互证据。立即停止窗口输入；未继续切页、缩放、读屏器或退出决策操作。尚无当前版本窗口截图／视觉通过结论，未关闭 3.x、8.5 或相关运行验收父项。桌面解锁后继续实际窗口核对。

该版本目标已同步为 1920×1080／16:9，Release 构建为 0 警告、0 错误，OpenSpec strict validation 与差异检查通过。源码核对及目标变更见 `docs/contracts/firmware-source-baseline.md`，设备接入仍待处理已记录的 EP0 查询与清理语义差异。

## 解锁后的页面基线

后续只读检查确认桌面已解锁、同一 USB Display 窗口仍在运行（窗口 id 3809980）。通过 Computer Use 导航主题→应用设置→诊断→设备→设备设置→主题；未点击保存、重置、导入、导出、注册或设备动作。返回主题后仍显示图片／内置背景、完整显示、背景色 #000000 与未保存草稿；原草稿未被保存或替换。五页选中导航随切页变化，未出现启动／导航错误窗口。

实际 PNG 基线保存在忽略目录 `.artifacts/visual-qa/2026-10-09/`：themes-existing、app-settings-existing、diagnostics-existing、devices-existing、device-settings-existing。截图视口 1180×760；诊断页额外等待导航动画结束后重新捕获，避免把过渡透明度当作文字对比度问题。主题草稿预览可见 16:9 内置画面，设备页保持未连接／模式未知／电源未知／推流停止且控制禁用；应用设置对未验证切换和不可用更新服务给出限制说明；诊断设备呈现指标标为未提供。

旧输出 Desktop DLL SHA256 为 `46B404745DF0FB2CA9E53F6E99CA8E35241E303CADA43240BAB312CB5F215003`；最新 `.artifacts/build-validation/mode-target/` DLL 为 `FA9C26561B5D52F1F736811972B77B8EB6BCD97B8E11D06DF83EC7E40D3DCA3E`。两者不同，不能将本轮截图或交互结果归属于最新代码。原窗口保留未保存草稿，未强制关闭、重启或丢弃数据。最新版本仍须完整复验。

采用 visual-qa 的两路只读截图／源码复核，结论与已编译的布局修正见 [visual-baseline-review-2026-10-09.md](visual-baseline-review-2026-10-09.md)。当前没有用户目标图或已批准的可比基线，不执行无意义的自比，也不编造 image-diff 数值。尚未运行高 DPI／混合 DPI、紧凑窗口、长中文、错误场景、读屏器、系统菜单、贴靠、退出决策或真实设备场景；3.1、8.5 及最终版本验收父项保持未完成。

## 正常退出故障

后续已获授权放弃未保存草稿并正常重启。工具未返回可操作的通知区域，临时通过应用设置将关闭行为设为退出应用，界面确认已保存；没有修改主题草稿、开机自启或设备切换。点击关闭后旧窗口保留，显示退出未完成及错误编号 `c8fba53dde104c7597d572b795dd5e7b`，没有崩溃关闭。

诊断 WindowExit 记录定位到 Window.VerifyNotClosing／Window.Show／RequestExitAsync：设置初始化已完成时，Closing 处理中的 await 同步继续，RequestExitAsync 在关闭事件返回前调用 Show，触发 WPF 非关闭重入约束。与对话框呈现故障、存储失败的假设相比，实际日志直接支持关闭事件重入这一原因；不以源码猜测替代运行证据。

源码在设置初始化前等待 Dispatcher.Background，使被取消的 Closing 事件先返回，再执行 Show 或退出决策；保留关闭互斥及系统退出优先门禁。目标 Release 输出 `.artifacts/build-validation/ui-exit-fix/`，四工程成功，0 警告、0 错误。尚未在新构建运行复验，不能宣称故障已通过验收。

旧窗口已关闭错误提示，将关闭行为恢复收至托盘并确认已保存。主题草稿尚未被保存或丢弃，旧进程仍在运行；需要通过托盘退出入口正常结束后启动新构建。未强制终止进程、安装驱动或执行设备命令。用户授权的正常重启仍未完成。

## 新构建运行复验

用户确认旧应用已退出后，启动 `.artifacts/build-validation/ui-runtime/UsbDisplayHost.Desktop.exe`。该输出 Desktop DLL SHA256 为 `CC9408D7340FA4165CC08D88BC7D91E8B9EF3F3BC9EBFE3A316592D67AB25F48`，目标 Release 四工程成功，0 警告、0 错误。五页最新实际截图为 devices-latest、themes-latest、device-settings-latest、app-settings-latest、diagnostics-latest，均在同日 visual-qa 忽略目录；默认窗口1180×760。主按钮、统一常规边距、设备空预览折叠和顶部检测位置均已确认。未触发设备检测／连接、驱动安装或固件命令。

实际缩小至644×404后，诊断摘要可以滚动到导出／日志目录动作，事件列表仍保持独立可见区域；对应 diagnostics-compact-top 与 diagnostics-compact-actions。摘要高度限制为可用内容区60%，事件列表保留回收虚拟化，不把空闲状态复验扩展为长错误／高DPI验收。

为复验正常退出，临时保存关闭行为为退出应用，点击标题栏关闭。原进程62512及窗口2232254均消失，随后重新启动同一输出，默认设备页正常显示，新窗口4725090。未出现此前 Window.Show 的关闭重入错误；本次未出现草稿决策弹窗，不能据此认定保存／放弃草稿分支均已覆盖。重启后已将关闭行为恢复收至托盘并确认已保存，没有强制终止进程。

双路视觉复核对常规布局及紧凑诊断滚动一致通过，但其中一路指出紧凑页面仍呈现约24 DIP边距。随后将尺寸资源覆盖移至 Application.Resources，避免 Page 资源解析绕过 Window.Resources；独立输出 `.artifacts/build-validation/ui-margin-scope/` 目标 Release 0警告0错误。这一后续源码改动尚未运行截图复验，当前运行应用仍是 ui-runtime 构建。整体视觉和最终运行验收保持未完成。
