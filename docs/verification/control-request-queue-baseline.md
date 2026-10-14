# 控制请求串行队列

日期：2026-10-12。关联 FR-12、T-22、TC-22，任务 11.3。仅为主机侧源实现与目标编译证据。

Core 新增 SerializedControlChannel，显式接收原 IControlChannel 的独占所有权，不枚举或打开设备。待处理和执行中的操作上下文总量最多 16，同一上下文不能并行重复受理；容量不足立即拒绝，不丢弃已受理请求。单消费者逐个调用原通道，每个请求最多执行一次。排队取消通过原子状态争用立即交付 Cancelled；若调度已经取得请求，则取消令牌交给原通道，由原所有者分类实际结果。

受理、实际执行前及成功交付时核验当前操作。失效的排队请求不发 I/O；执行后缺失结果、上下文错配或成功交付前失效返回 Unknown，不发布 Ready。观察流保留底层迟到结果，过期观察清除受理、完成和实际模式／电源／亮度字段。明确的底层 Failure、Cancelled 和 Unknown 不自动重发；底层模式 ledger 继续负责未结算请求的独占约束。调用原执行边界发生普通异常时，当前结果为 Unknown，后续排队请求拒绝，保留原通道，不创建替代所有者。

IControlChannel 增加已有 Windows 实现所提供的 CloseAsync 类型化关闭契约。队列关闭停止受理并完成入口，先调用原通道关闭以收敛其原生请求，再等待单消费者结束；未执行请求取消。关闭结果沿用原所有者的 Success／Failure／Unknown，缺失结果或普通关闭异常返回 Unknown。DisposeAsync 不是资源收敛证明，调用方必须检查 CloseAsync。排队注册和观察释放异常记录诊断，不向后台传播普通异常。

验证命令：

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/control-queue/
```

四生产工程 0 警告、0 错误。Core.dll SHA256：`9F2E7917BE27677E772A8B126C8E3EC94FDB3837FCE21DF36CE9EDEA7E7E3D6C`。Core 无 WPF 或原生句柄依赖。

本次队列尚未接入 Desktop／DeviceCoordinator，目标重枚举证据及真实取消／竞态行为未验证。未创建或运行测试，未调用设备。11.3 父任务保持未完成。

另已确认旧应用进程不存在，并启动此前 hid-late-observations Release 输出，返回实际 USB Display 窗口。后续窗口截图复验被用户 Esc 中止；这不是 control-queue 输出或完整界面验收证据。
