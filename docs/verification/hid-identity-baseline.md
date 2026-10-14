# HID 身份探测代码基线

关联 REQ-01／FR-01、D2、T-09、任务 4.2。此模块仅提供真实 HID 身份读取适配，不提供 HID 控制协议、collection 能力或实际兼容验收。

## 当前实现

`HidInterfaceIdentityProbe` 实现发现链的 IDeviceInterfaceIdentityProbe。创建要求 1～8 条不重复的 VID/PID、设备修订与证据引用，没有设计值默认清单。进程内最多 8 个强引用所有者，同 VID/PID 清单不允许被另一个探测器同时接管；只有关闭明确成功才释放预约。创建过程不调用原生接口。

每次探测在串行门内后台执行。先用 HidD_GetHidGuid 核对当前接口类，再以 metadata access=0、共享读写、OPEN_EXISTING 打开当前枚举路径。HidD_GetAttributes 获取实际 VID/PID 与设备修订；明确不匹配目标 VID/PID 才返回成功 null，匹配目标但修订未列入证据记录则返回失败。属性访问失败不能冒充非目标。

HidD_GetSerialNumberString 使用 512 字节自有缓冲，检查 UTF-16 字符串终止、严格解码和 Core 序列号规则，得到 VID＋真实序列号永久身份。不能从 HID 符号路径、PID、位置或设备修订拼接永久键。属性中的 VersionNumber 只是设备修订，不表示固件版本或控制协议已核验。

P/Invoke 与 SDK hidsdi.h 静态核对：HIDD_ATTRIBUTES 含 Size、三个 USHORT，保留默认结构对齐；两个 HidD 方法返回单字节 BOOLEAN（U1），不能声明为四字节 BOOL。序列号 BufferLength 以字节计且小于 4093 字节，不截断后继续使用。官方依据：[HidD_GetAttributes](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getattributes)、[HIDD_ATTRIBUTES](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/ns-hidsdi-_hidd_attributes)、[HidD_GetSerialNumberString](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getserialnumberstring)、[HidD_GetHidGuid](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_gethidguid)。

不发送 HID report、feature 或控制命令，也不进行 ReadFile/WriteFile 报告 I/O。元数据调用是同步调用，Task.Run 仅将它移到后台；取消只在调用前后检查，不能证明驱动中的调用已结束。串行门在调用返回及句柄关闭处理以后才释放，CancelAndDrain 等待该门。没有通过超时丢弃 Task 或在同步调用未返回时关闭句柄。

自有 SafeHandle 显式记录 CloseHandle 的确认结果。关闭未确认会保存句柄包装与错误，停止新探测，静态有界所有者表保留探测器；后续 drain/close 返回 Unknown，不重新关闭未知句柄，不让新探测器接管相同 VID/PID。普通异常进入诊断并返回业务原因、动作与错误编号。

## 未覆盖与验证

身份的容器与位置已接入当前接口对应 PnP 设备节点的系统属性读取，详见 `pnp-metadata-baseline.md`；仅明确不存在时保留 null，没有虚构同容器证据。容器缺失的同键多个 HID collection／接口继续被明确选择解析器按歧义拒绝。实际 collection、容器／位置关联证据、WinUSB 数据面身份探测、驱动／控制能力与两态兼容记录仍待补齐；同步元数据调用卡住时无法保证有界退出。

没有提供演示 VID/PID／修订清单，也未将探测器接入 App 启动。尚无实际设备读回、取消、访问权限、热拔插、失败关闭、ABI 或句柄增长证据；不得将编译表述为可信设备发现或控制面已完成。

仅执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四生产工程 0 警告、0 错误。未运行探测器、设备打开、注册通知、HID 命令、窗口或测试。差异空白检查通过；任务 4.2 父项和 TC-01～TC-04 保持未完成。
