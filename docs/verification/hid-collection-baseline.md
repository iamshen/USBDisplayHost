# Windows HID collection 身份门禁

日期：2026-10-09。关联 REQ-02／FR-02、T-09、T-22、TC-04、TC-22，任务 4.2、11.2。

HidIdentityCompatibility 可附带显式 HidCollectionCompatibility，指定 usage page／usage 及 Input、Output、Feature 长度。字段要求完整、各长度 1～4096；无 collection 契约时保持原有纯身份探测范围，不将该结果当作控制通道验证。AD102 V2 的 FF00／1、64／64／64 来源于固件源码，仍须由实际兼容记录显式传入，不在探测器中设置默认身份或控制兼容条目。

同步身份探测核对实际 VID／PID／设备修订后，读取 preparsed data 并调用 HidP_GetCaps；长度以实际 top-level collection 数据核对，包含 Report ID。usage 不同的 collection 不作为目标；目标 collection 的报告长度不匹配返回业务错误及编号，不能继续以固定长度读写。创建／编译没有原生调用，取消只在同步调用返回后检查，不丢弃仍在执行的任务。

HIDP_CAPS 使用 64 字节布局与前五个 ushort 的显式偏移，预解析数据采用 SafeHandle。释放成功之后才能继续返回身份；释放失败保留预解析数据、原文件句柄和静态探测所有者，返回 Unknown，禁止重复探测／替代所有者。HidP_GetCaps 返回的 NTSTATUS 使用 ExternalException 的 HResult 留在诊断，不作为 Win32 成功码解释或展示给用户。

依据：[HIDP_CAPS 布局与长度](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_caps)、[HidP_GetCaps](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getcaps)。该实现不读取 Feature／Input 或写 Output；原生 ABI、collection 选择和释放行为尚未运行核验，控制会话打开时仍需重新核对，不能复用探测结论绕过身份变化。

目标 Release 使用独立输出目录 `.artifacts/build-validation/hid-collection/`，四生产工程成功，0 警告、0 错误。未创建或运行测试，未调用 HID 或发送设备命令；App 未启用该路径，相关父项保持未完成。

后续增加的显式 V2 Feature 查询见 [hid-feature-query-baseline.md](hid-feature-query-baseline.md)。默认身份探测仍不读取报告，该入口在同一文件句柄核验身份与 collection 后查询 Feature，不能把本页的 collection 基线当作报告查询的运行证据。
