# AD102 V2 模式超时分类基础

日期：2026-10-09。关联FR-11、NFR-02、T-22／T-23、TC-20／TC-21，任务11.5；沿用AD102 V2.0源码字段契约。

## 原模式与等待时刻

Ad102V2ModeRequest新增OriginalMode。Prepare由本物理会话已读取的Feature冻结原模式；维护占用、current／target不一致、非Theme／Screen以及请求目标等于原模式均拒绝，不生成新切换报告。已有电源意图、设备键、会话代次、修订、操作号和不复用token规则保留。

控制记录创建必须注入TimeProvider和IErrorReporter。BeginWrite在批准首次写之前记录单调时间；取时间异常记录V2ModeWaitStart并返回同一错误编号，写入未被批准。正常等待预算8秒，不依赖系统日历时间。

## 超时后的真实结果分类

VerifyAfterTimeout只核验已收集的重查证据，不执行枚举、HID查询、初始化或发送。证据时刻必须来自同一TimeProvider，至少晚于开始写8秒，不能在当前单调时刻之后；控制记录须已Retire、原写完整收敛、仍持有请求且目标尚未结算。未经收敛不能以目标出现取代原I/O所有权证明。

TargetReady复用已有目标核验：同一永久设备键、当前发现选择、原操作／修订、新代次、真实Feature模式、电源意图、驱动和能力；副屏还需实际显示路径，亮屏主题还需同代次初始化和完整合法首帧。ACK是否丢失不改变目标证据规则。

OriginalReady单独核验同一设备的原模式，代次可与原会话相同或更新；Feature的current／target必须稳定一致，电源仍符合冻结意图、驱动及能力已核实、推流停止。副屏需实际显示路径；主题需1920×1080，亮屏时还需同上下文只读ProbeVerified收据。重查不接受亮屏初始化命令或首帧，熄屏不为核验而点亮。进入及交付前均核对发现选择和当前上下文，异常返回带诊断编号的Unknown。

OriginalReady表示本次观察中原态仍可用、切换未确认完成，不是反向操作或回滚证明。它不会清除请求或token历史，也不占用目标最终结算位置；之后真正到达且核实的目标仍可由VerifyTarget结算。无法核实身份、目标或原态可用性时返回Unknown，保留原请求，禁止自动重发或反向切换。

## 编译与未完成范围

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/mode-timeout-requery/
```

四生产工程成功，0警告、0错误。Core继续不依赖WPF、原生句柄或Windows类型。仓库没有控制记录工厂调用者需要迁移；后续组合根必须提供共同单调时钟和诊断服务。

Windows重新枚举／实际Feature与显示路径／只读probe采集、定时调度、操作结果与App组合尚未实现。本轮未调用设备、运行应用或创建／执行测试，也没有行为验收证据；不能把分类基础称为完整11.5或实机模式切换。11.5保持未完成。
