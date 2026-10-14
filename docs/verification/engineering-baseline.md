# 工程基线实施记录

日期：2026-10-08。关联 T-07、OpenSpec 2.1～2.4。

## 已实现

- 四生产工程、解决方案与编译边界；Core 为 net10.0，不引用 Windows、WPF 或第三方包。
- nullable、.NET 分析器、SDK 10.0.401 精确锁定、集中包版本和四份包锁文件。
- NuGet 来源固定 nuget.org；WPF UI／MVVM Toolkit 仅进 Desktop，Serilog 及两个 sink 仅直接进 Windows。
- `build.ps1` 提供 Restore、Build、Publish 三个入口；默认只做目标 Release 构建，没有测试入口，也不运行程序、操作设备或安装驱动。
- FluentWindow／TitleBar、五页 NavigationView、固定五实例页面提供器、平级导航清理历史及 MVVM 状态投影。App 已组合页面业务表单、唯一协调器与会话生命周期、草稿预览、主机指标和诊断；真实发现与原生设备管线尚未创建或附加，不能据此认定设备服务已经可用。
- 按实际窗口宽度切换 Left／LeftMinimal 导航；系统高对比度变化更新 WPF UI 主题；语义文字使用系统动态画刷，持久错误提示独立于连接状态。PerMonitorV2 与普通用户权限已声明，运行效果未验收。
- Core 的稳定身份、能力证据、不可变快照、操作结果、草稿／活动／恢复配置、字段校验、质量递减与适配接口。指标过期使用注入 TimeProvider 的单调时间，独立于日历时间。

## 构建证据

首次接入版本后，使用 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release -p:RestoreLockedMode=false` 生成依赖锁文件，0 警告、0 错误。

随后通过 `./build.ps1 -Action Build -PackageCache .artifacts/nuget/be38f5a6676c49418375ca3ada504e5f` 在新建独立包目录执行锁定还原及目标 Release 构建：四工程成功，0 警告、0 错误。该目录开始时不存在；它隔离 NuGet 全局包目录，不声称禁用了 NuGet HTTP 缓存。

接入五页骨架、动态资源、错误控件和应用清单后，再次执行 `./build.ps1 -Action Build`，0 警告、0 错误。页面提供器使用锁定 WPF UI 4.3.0 的 `INavigationViewPageProvider`，固定五个页面实例；导航只清理历史，不创建新业务会话。真实窗口、选择状态、紧凑菜单、贴靠和辅助技术未执行验收。

## 待验证与缺失输入

启动入口异常边界补充：`Program.Main` 对启动失败提示框和 `finally` 内的日志停止等待分别捕获普通异常。提示失败仅提交诊断，不递归打开提示；日志停止异常只尽力保留错误编号，不重建写者或弹出退出对话框。日志等待仍限定 3 秒，超时不取消后台写者，也不声明已落盘或已清理。启动失败仍返回失败退出码；不能将没有可用 Application 的初始化失败表述为可继续运行。OutOfMemoryException 等进程级致命情况不承诺恢复。

该补充执行目标 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四工程成功，0 警告、0 错误；未启动应用、运行测试或故障注入，因此只证明编译通过。

生产编译不证明 FluentWindow 运行、控件交互、Core 行为边界、原生 I/O 或设备验收成功。应用未运行，测试未创建或执行，Publish 入口未执行，自包含发布内容待核对。测试依赖也未添加。

1.2～1.5、11.1 需要项目方／固件方提供实际设备、描述符、匹配固件、冻结协议和驱动包；当前没有这些交付证据。涉及真实命令时不能使用猜测字段补齐。

2.5、12.x、13.x 还需要发布方提供正式 packId、HTTPS 源、签名责任及签名产物、干净验证环境；当前没有这些交付证据。下一次核对时点为收到实际资料或环境后，尚无项目方约定日期。
