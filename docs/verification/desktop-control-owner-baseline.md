# Desktop 控制通道所有权组合

日期：2026-10-12。关联 FR-12、T-22、TC-22，任务 11.3。仅为源实现与目标编译证据。

工作区已有 DeviceSessionLifetime 新增显式 AttachControl 入口，接收已创建、核验且持续读取有效的 AD102 V2 模式控制所有者。仅当前 Ready、能力已核验且声明支持模式切换、没有在途操作，并且物理设备键／会话代次与现有主题管线一致时受理。Desktop 组合 Windows Ad102V2ControlChannel 与 Core SerializedControlChannel；创建队列之前先保存原通道，后续组合异常仍由工作区负责关闭，不丢弃已交接所有者。交付前再次核验当前会话与快照版本，期间变化则保留所有者并返回 Unknown，不能沿用该通道执行命令。

DeviceCoordinator 新增 CaptureControlPowerIntent。只允许当前 SwitchMode 操作读取受理前已核验 Ready 快照中的 On／Off；未知电源意图不发切换。回调与控制队列复用协调器 IsCurrent，命令必须属于原键、代次、操作 ID 和内容修订。Desktop ExecuteControlAsync 仅向已接管的串行队列交付；观察入口仍供原操作消费实际受理、拒绝与迟到结果，不修改 DeviceSnapshot，不将 ACK 转换为 Ready。

控制所有者纳入 HasRetainedSession 与 IsBusy，阻止旧所有者未释放时重选／重建。主题管线关闭确认原生资源已释放后，再关闭原 HID 通道与队列；两者确认后才发布 Released／NativeClosed。主题本地结果待处理时也先关闭已可关闭的 HID。控制关闭 Failure／Unknown 保留原所有者及错误，不创建替代连接。控制单独存在时也会发布关闭阶段；正常退出、设备移除与休眠共用已有 CloseCoreAsync，CloseDisconnectedAsync 现在匹配主题或控制所有者，不能跳过仅 HID 的会话。

验证命令：

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/desktop-control-owner/
```

最终构建四生产工程 0 警告、0 错误。Desktop.dll SHA256：`4606B30E3143C1B7378347195A35778C260ECD519FDF8D8487847EEF80A12AD3`。Core 新增的是平台无关电源意图与队列繁忙查询，不依赖 WPF 或原生句柄。

未启动本次输出、打开设备、创建或运行测试。正式连接工作尚未调用 AttachControl；模式按钮／托盘入口、原操作消费、目标重枚举与完成证据仍未接入。普通启动不自动创建 HID 或切换设备。本记录不代表模式切换、退出竞态、拔插或休眠验收成功，11.3 保持未完成。
