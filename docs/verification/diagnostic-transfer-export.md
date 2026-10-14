# 主机传输摘要导出代码基线

关联 REQ-10／FR-10、T-19，任务 8.4。仅有代码与目标 Release 编译证据。

诊断页在目标路径选择完成后读取一次 CaptureTransport，将同一已接管 owner 的计数和资源阶段冻结进 DiagnosticExportRequest.Transfer。后续后台导出不再读取设备或会话，不随发送／断开更新该请求，也不延长管线生命周期；捕获期间仅使用已有接管门禁和不可变状态快照。UTC CapturedAt 表示请求捕获时间，不是末帧呈现或计数更新时刻。

Core DiagnosticTransferSummary 是封闭数据记录，仅有捕获时间、两个非负 long 计数和帧通道／资源阶段枚举。没有设备键、序列号、接口路径、驱动路径、素材、错误消息或额外文本字段。Windows 在获取日志快照、创建临时文件之前拒绝负计数、未知枚举和非 UTC 时间，不归零伪造数据。没有 owner 时 Transfer 为 null，不填入零记录。

summary.json 的 SchemaVersion 改为 2，新增 HostTransfer：CapturedAt、UsbCompletedFrames、ConfirmedPacketBytes、ChannelState、ResourceState，以及固定的 FrameScope=CompleteUsbFramesOnly／ByteScope=CompleteAfPacketsOnly。设备身份不序列化，通道与资源状态输出验证过的枚举名称。DeviceFeedback.Availability=NotProvided；DeviceState 明确未提供，不声称已取得实际模式／电源或呈现反馈。原软件、OS、架构、配置概要、存储状态和日志白名单保持原职责。

计数含义与 `diagnostic-transfer-workspace.md` 一致：完整帧尾片已确认才计帧，字节仅计完整 AF 分片，包括包头，不包含短写／未知写入或呈现结果。原资源已关闭但保留本地结果时导出 ClosedRetained，不将其当作在线推流；Unknown 仍导出 Unconfirmed。新摘要体积固定，不导出列表、帧或图片，原受限日志读取／流式 ZIP／取消／临时文件责任不变。

目标 Release 构建四生产工程 0 警告、0 错误，`git diff --check` 通过。未创建或运行测试、导出 ZIP、操作目标文件、启动窗口、实际会话或设备请求。实际 ZIP 内容／隐私核查、取消／磁盘失败和并发捕获行为未运行验收；8.4 父项保持未完成。
