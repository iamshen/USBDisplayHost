# Windows AD102 V2 Feature 查询

日期：2026-10-09。关联 REQ-02／FR-02／FR-12、T-09、T-22、TC-04、TC-22，任务 11.2。

HidInterfaceIdentityProbe 新增显式 ProbeAd102V2Async。原 ProbeAsync 仍只读身份和可选 collection 元数据。V2 入口要求全部显式兼容记录带 FF00／1、Input／Output／Feature 各 64 字节契约；缺失契约在打开设备前拒绝，不自行创建 VID／PID／修订兼容条目。

同一串行门内使用当前枚举路径打开元数据句柄，依次核对 HID 类、实际 VID／PID／修订、实际 collection usage 和三类报告长度、真实序列号与 PnP 元数据。非目标接口返回空结果；目标字段不匹配返回业务错误。只有身份核验成功才在同一文件句柄上调用 HidD_GetFeature，首字节指定报告 ID 3，缓冲长度使用已核对的 64 字节契约，结果交给 Core Ad102V2HidCodec 解析 V2.0 版本、模式、维护状态、保留字段和声明能力。

缓冲预填非零值，未写回的保留字段无法因托管默认零值通过解析。HidD_GetFeature 返回 BOOLEAN，不提供独立的实际传输字节数；该实现依照成功调用的报告契约与字段校验交付协议识别结果，不将其当作 Input 短报告处理、命令完成或原生实际计数的验证证据。后续独立 Input 读取仍须核对实际完成字节数。API 失败立即保留原生错误到诊断，业务层只返回原因、动作和编号，不回退为“非目标设备”。

取消在各同步调用前后检查；驱动调用已开始时等待它真实返回，不取消 Task 或丢弃其结果以提前释放门。查询返回后执行原有关闭确认；文件或预解析数据释放未确认时返回 Unknown，保留原资源及静态所有者，不能交付成功结果或创建替代探测器。关闭等待共用串行门，停止后的查询被拒绝。输出的 Codec 仅保留不可变协议字段，不持有原生句柄，能力声明不等于已验证控制能力，控制会话打开时仍需重新核验身份。

依据：[HidD_GetFeature](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getfeature) 对首字节报告 ID、HIDP_CAPS 的 FeatureReportByteLength 和 BOOLEAN 返回语义的定义。

目标 Release：`dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-feature/`。四生产工程成功，0 警告、0 错误。未创建／运行测试，未调用原生探测或设备查询；App 未启用该入口。实际驱动权限、报告样本、取消与关闭行为仍待验收；11.2 父项保持未完成。
