# 生产依赖与分发声明

日期：2026-10-08。版本来源为 nuget.org 包元数据，完整解析版本及内容哈希保存在各生产工程 `packages.lock.json`。

| 依赖 | 固定版本 | 工程／用途 | 许可 | 运行分发文件 |
|---|---|---|---|---|
| WPF-UI | 4.3.0 | Desktop，原生 FluentWindow／TitleBar 及后续导航控件 | MIT；包含单独第三方声明 | Wpf.Ui.dll |
| WPF-UI.Abstractions | 4.3.0（传递） | Desktop，WPF UI 服务契约 | MIT | Wpf.Ui.Abstractions.dll |
| CommunityToolkit.Mvvm | 8.4.2 | Desktop，状态通知及后续命令／校验 | MIT | CommunityToolkit.Mvvm.dll；生成器仅在构建时使用 |
| Serilog | 4.4.0 | Windows，结构化日志适配 | Apache-2.0 | Serilog.dll |
| Serilog.Sinks.File | 7.0.0 | Windows，锁定依赖；当前严格字节配额由独立 Serilog sink 实现，此库未接入 | Apache-2.0 | Serilog.Sinks.File.dll |
| Serilog.Sinks.Async | 2.1.0 | Windows，512 条有界后台写入及丢弃监视 | Apache-2.0 | Serilog.Sinks.Async.dll |

采用已有技术设计选定的组件，没有引入替代库。Serilog 与有界 Async 已接入，严格大小配额适配器已编译；运行、轮转和压力证据仍由 8.3 完成。应急错误文件不作为完整日志交付，详见 `docs/verification/diagnostics-baseline.md`。

SDK 精确固定为 10.0.401，禁止预览和自动滚动；未来 SDK／依赖更新须同时更新集中版本、锁文件和本表。Core 与 Rendering 均无直接 NuGet 包依赖；测试和 Velopack 依赖尚未添加。

`licenses/` 保留 NuGet 包内原始许可及第三方声明，英文内容不翻译。WPF UI 的声明包含图标、字体与派生代码的许可；软件发布需随包保留这些内容，不把全部素材概括成项目主许可。Serilog Apache-2.0 文本来自对应源码提交 `497f80fda4f9e8f98b9c13ba34b1f0530f8c4449`。应用发布入口配置复制 `licenses/`，但自包含目录尚未实际发布核对；.NET 运行时分发许可与最终 DLL 清单需在 12.1 检查。

来源：[WPF UI](https://github.com/lepoco/wpfui)、[MVVM Toolkit](https://github.com/CommunityToolkit/dotnet)、[Serilog](https://github.com/serilog/serilog)、[File sink](https://github.com/serilog/serilog-sinks-file)、[Async sink](https://github.com/serilog/serilog-sinks-async)。
