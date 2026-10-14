# 控制命令与原操作归属核验

日期：2026-10-12。关联 FR-12、T-22、TC-22，任务 11.3。源实现与目标编译证据。

DeviceCoordinator 新增 ValidateControlRequest，Desktop 交付串行队列前调用。核验当前物理键／代次、操作 ID／修订、取消状态，以及原操作受理前的 Ready／已核验能力快照。SwitchMode、SetPower、SetBrightness 分别只能归属于对应协调器动作；只读 QueryState 只在这些控制动作内受理，必须由原能力声明并使用值 0。模式目标为 Theme／SecondaryDisplay 且不能与原模式相同，原电源意图必须已知；电源值为 On／Off，亮度为 0～100。未知命令、动作错配、过期或取消在进入队列前返回 Failure，不发替代命令。

该门禁补充控制通道对原上下文的验证。排队后的当前性核验和 Windows 实际提交检查继续保留；校验成功不是发送、受理或完成证明。协调器仍只有收到并核验 DeviceObservation 才能发布 Ready。

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/control-operation-admission/
```

四生产工程 0 警告、0 错误。Core.dll SHA256：`C8CF2C3AAFDD9EBBF66FD756F49FBD0DF70C50D73667BA711CBB36991E9BB0E2`。未运行本次输出、设备或测试，未创建测试代码。

当前模式流程仍缺少协调器原操作跨目标代次的交接。现有接口移除会失效原会话并取消正在执行的工作；目标 verifier 要求新代次证据，不能用旧上下文绕过。下一步应将原 I/O 收敛、旧应答失效、同一物理设备新发现凭据与原操作交接组合起来，再接模式工作及界面入口。实际目标证据与竞态验收未完成，11.3 不关闭。
