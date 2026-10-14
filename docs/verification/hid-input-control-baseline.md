# HID 持续读取与模式应答接入

日期：2026-10-09。关联FR-11／FR-12、T-22、TC-22，任务11.2／11.3／11.5。

## 输入读取和状态分发

HidInputPump只接收显式交付的已核验HidOpenedSession，不发现或打开设备。会话保留唯一读取所有者；接入后其他直接读取或替代读取器被拒绝。Input原生请求持续等待报告或取消，只有Output使用配置的有限写入预算；不把没有Input报告当作控制超时，也不通过轮询HidD_GetInputReport模拟连续读取。

每次检查实际完成状态、真实长度及完整Core V2解码。短报告、未知ID、非法字段及能力声明不一致均拒绝，不更新状态或调用控制处理器；记录拒绝计数，同类诊断只记录首次，并对连续坏报告限制处理频率。读取原生故障、发现凭据失效、处理器或时钟异常结束循环，发布无有效状态的诊断快照。正常停止同样发布终止快照，不能让缓冲中旧状态被当作仍有效。一般异常在读取、控制处理或关闭边界记录诊断，不作为未观察后台异常传播至应用。

有效报告先同步交给只处理内存记录的控制处理器，之后进入容量1、DropOldest的状态Channel；界面较慢不会阻塞原生读取。快照包含DeviceKey、会话代次、版本和单调采集时刻；接入UI时仍必须核对当前代次。该Channel是单一状态消费流，不是广播订阅。控制处理器调用不持有分发锁，避免与控制关闭锁形成反向锁序。CloseAsync取消循环、等待原读取工作，再通过原会话收敛两个方向及关闭文件，未知所有者仍保留。

## 模式发送与应答

Ad102V2ModeController要求已验证的SwitchMode能力、匹配真实序列号的DeviceKey和物理会话代次。它独占会话写入位置，创建上述Input循环并直接连接Core Ad102V2ModeControlLedger。只提供模式发送／受理结果，不把传输完整或Accepted当作目标模式已就绪；Busy／Invalid／Unsupported为收到的明确应答，其枚举保留给业务层分类。

控制文件在完成身份／collection／Feature核验后清理同一collection输入队列，失败不交付会话。开始首个模式请求前必须取得稳定有效Input，保留其中最近应答token作为基线；Core Prepare必须先建立基线，跳过该token，并继续禁止本物理会话token回绕／复用。纯身份或只读Feature探测不执行队列清理。依据：[HidD_FlushQueue](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_flushqueue)，该API清理collection的待处理输入报告，不发送模式控制命令。

每个请求冻结上下文、目标、电源意图及报告；唯一写入经BeginWrite批准。Input中的命令／token／结果先关联原请求，早于写入完成的应答留在ledger；完整写收据到达后重核对同一报告，冲突受理／拒绝保留未知。控制应答通过单请求TaskCompletionSource保存，独立于最新状态Channel；LastAcknowledgement可读取等待窗口结束后收到的关联结果。

8秒观察期限覆盖发送收据和应答等待。即使原写入仍在等待，它也继续由控制所有者的原任务持有；观察返回未知，不重发或丢弃Task。关闭先使ledger失效、结束应答观察，等待输入及原写入任务，补交session.LastWriteReceipt的真实最终收据。旧会话失效后不交付旧受理结果；真实目标及超时重查仍通过已有Core核验器处理，不能只根据Input中的模式字段宣称切换完成。

## 编译与未完成范围

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-input-pump/
```

最终四生产工程0警告、0错误。Windows.dll SHA256：`B2C2E60E322DE116071B2DF7F33866983D95AD2109577BE7952E09C4AA897613`；Core.dll：`7A896F9B643059CB62B0BBC858AC585822171E017BEC8A48057E614D122B4AC5`。Core没有增加WPF、Windows或原生句柄依赖。

本轮未运行此输出、打开设备、调用新增原生方法或创建／运行测试；所有结论限于源实现和目标编译。App／IControlChannel组合、Feature重新查询、设备指标和各模式亮度、完整控制队列、数据面停止与重枚举目标证据采集、实机取消／拔插／坏报告／超时行为尚未完成。11.2／11.3／11.5父任务仍待完成。先前hid-opened-session-baseline中持续读取与Core ledger未接入的状态由本记录补充。

后续同文件Feature重查、控制写入互斥、关闭等待原查询和协议／能力失效后立即清除输入状态已实现并编译，见`hid-feature-requery-baseline.md`；仍无原生运行证据或App接入。
