# 结构化日志与诊断导出实施状态

日期：2026-10-08。关联 REQ-10／FR-10、T-19、TC-19，OpenSpec 8.3～8.4。只记录生产实现和目标构建证据。

## 日志与可见状态

Windows DiagnosticLog 使用锁定的 Serilog 4.4.0 和 Async 2.1.0；后台缓冲 512 条，满时丢弃而不等待 UI／设备。监视器独立读取待写及丢弃计数，写入失败只更新状态，不递归写失败日志。参见 [Async 2.1.0 缓冲／监视 API](https://github.com/serilog/serilog-sinks-async/blob/v2.1.0/README.md) 和 [该版本后台写者实现](https://github.com/serilog/serilog-sinks-async/blob/v2.1.0/src/Serilog.Sinks.Async/Sinks/Async/BackgroundWorkerSink.cs)。

StrictRollingFileSink 是 Serilog 的本地配额适配器，输出 UTF-8 JSONL；写入前计算整行字节数，每条不超过 64KiB、每文件不超过 10MiB。固定 `diagnostic-0.jsonl`～`diagnostic-4.jsonl` 五个文件，0 为当前文件；轮转先删最旧，再移动，包含在写文件共不超过 50MiB。打开时不向超配额文件继续追加，部分行写入失败尽力截回原长度；无法截回则下一次轮转。配额目录为 `%LocalAppData%/UsbDisplayHost.Data/diagnostics/logs/`。

使用独立配额适配器是因为 [File sink 7.0.0 的限制为近似值](https://github.com/serilog/serilog-sinks-file/blob/v7.0.0/src/Serilog.Sinks.File/Sinks/File/FileSink.cs)，会允许末条完整记录跨过大小边界；该依赖仍锁定但当前未接入。没有替换 Serilog 或放宽规格大小上限。

每个错误生成编号，记录阶段、应用程序集版本、类型、HResult、原生错误及最多三层内部错误码，方法栈限定 4096 字符。日志不存异常消息、个人完整路径或图片内容。可选关联字段为匿名设备键、会话代次、操作 ID、状态、实际字节、耗时和固件／驱动版本；设备链路未接入时留空，不编造实机关联。内存 Journal 最多 500 条。

ErrorReport 区分已经记录、提交后台和未记录；异步返回不能证明落盘，UI 不再将提交等同成功。诊断页显示写入、待写、丢弃、失败计数和存储状态，最近事件增量追加／移除，不主动滚动、选择或设置焦点。页级 1Hz 状态更新在不可见时停止。

BootstrapErrorReporter 只在结构化日志服务未初始化时使用同步应急路径；新记录复用 StrictRollingFileSink，在相同 `logs/` 目录内遵守五文件／每文件 10MiB／每条 64KiB 的写前配额，不再创建独立的 `last-error.txt`。仅记录时间、错误编号、受限阶段与类型、HResult 和可选原生错误码，不记录原始异常消息、堆栈或路径。写入和流关闭均未报错才返回已记录；失败返回未记录，不递归报告、不自动重试。磁盘和文件共享失败的实际行为仍未验证；旧版本可能遗留的 `diagnostics/last-error.txt` 未读取、修改或删除，不属于新记录配额或导出范围。

## 导出与生命周期

导出从后台取得五文件受限快照；仅短暂锁定写者来打开读取句柄，复制／ZIP 操作不持有写者锁。读取句柄允许重命名／删除，轮转可继续；最多读取 50MiB，每行最多 64KiB，超长、损坏或不完整记录省略并计数。导出仅序列化白名单字段，不原样复制本地日志文本、堆栈、异常消息或应急文件。

包包含软件版本／Windows 版本／架构、已知兼容状态、用户选择时冻结的离线配置摘要、日志健康、过滤事件和最近 500 条事件。设备、固件和驱动没有真实输入时明确未提供。摘要只有模板／未保存／有效输入／是否有导入图片，不含素材 ID、图片路径、素材内容、原始配置或凭据。

用户选择应用数据目录外的 ZIP 位置；同目录临时文件完成后提交，取消或提交前失败保留原目标并尽力删除临时文件。进度依据实际读取字节，迟到 UI 进度按操作代次丢弃；导出、取消和目录打开有明确状态及错误编号。目录打开和文件对话框分别隔离在 Windows／Desktop 服务，ViewModel 不执行原生调用。

显式退出取消并等待导出／目录命令，再停止预览、渲染和指标，最后后台清空日志队列。启动线程兜底最多等日志 3 秒；系统关机仍可能早于所有 I/O 完成，未达到完整有界清理验收。

## 证据与未完成项

执行 `./build.ps1 -Action Build`，四生产工程 Release 0 警告、0 错误；`git diff --check` 通过。未启动应用、创建或执行测试，未实际生成日志／ZIP、执行目录打开或故障注入。

轮转总量、磁盘／共享冲突、满队列和日志恢复尚无运行记录；应急配额统一的代码已编译，真实设备反馈／阶段关联、真实诊断导出内容和 UI 焦点／辅助技术验收仍待完成。8.3／8.4 父项保持未完成。

应急路径改动后执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四生产工程成功，0 警告、0 错误。没有实际写入日志、运行窗口或故障注入；编译不证明文件配额、权限失败或落盘行为验收通过。
