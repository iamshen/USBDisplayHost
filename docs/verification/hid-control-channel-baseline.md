# HID Core 控制接口适配

日期：2026-10-09。关联FR-11／FR-12、T-22、TC-22，任务11.2／11.3。证据为源实现及目标编译。

## 接口与结果含义

Windows新增Ad102V2ControlChannel实现Core IControlChannel，显式接收已创建的Ad102V2ModeController、当前OperationContext核验服务、电源意图捕获服务和诊断服务。控制所有者最多接入一个通道并保留它；创建不发现／打开设备，Desktop尚未创建该适配器。Core接口和原生句柄隔离保持不变。

请求必须匹配控制所有者的DeviceKey、物理会话代次、非空操作ID及有效内容修订，并通过调用方当前上下文核验。仅适配SwitchMode和QueryState；尚无已核实的亮度／电源写契约时返回明确不支持，不使用草案命令试写。电源意图捕获失败／未知保留原诊断编号；查询值限定0，模式目标限定主题或副屏。

模式请求冻结原上下文与电源意图，经既有控制记录唯一发送；Accepted返回Accepted=true、Completed=false，模式／电源仍为Unknown。明确固件拒绝转为失败；取消、未知和诊断错误分别返回。QueryState执行原文件Feature查询；Completed=true仅表示该查询完成，不能据此结算另一操作的模式切换。Feature不稳定或维护中返回Unknown模式，不能证明目标可操作；电源和亮度没有本次读回证据，保持未知。

## 有界观察与关闭

通道最多一个执行者，忙时在受理前返回明确失败，尚未实现通用控制排队。ControlObservation使用容量16的有界队列；执行设备操作前先预留结果位置，队列已满则拒绝受理，不能通过DropOldest丢弃重要执行结果。ExecuteAsync直接返回结果，同时将成功／失败／未知观察交给单一ObserveAsync消费者；消费释放位置，取消订阅结束枚举。交付和消费均重新核对上下文，过期结果不能继续携带Accepted／Completed或当前设备状态。

执行、观察、关闭异常记入相应诊断阶段。原执行任务保存于通道，CloseAsync先停止受理、取消等待、关闭原控制所有者，再等待原执行结束并关闭观察队列。关闭未知保持原资源所有者；成功关闭后才清理生命周期取消源。受理及关闭异步工作使用ForceYielding且不要求回到UI上下文，避免在字段保存前执行完整工作或依赖关闭中的Dispatcher。依据：[ConfigureAwaitOptions](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.configureawaitoptions?view=net-10.0)。

## 构建与剩余范围

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-control-channel/
```

四生产工程0警告、0错误。Windows.dll SHA256：`0323B9035D3540C7F3EBF00B68E0E4DD77858069495DF272B3D1B50353EB23C0`。Core没有新增WPF／Windows／原生句柄依赖；OpenSpec父项不据接口编译完成而勾选。

当前ObserveAsync仅分发ExecuteAsync返回的观察；等待窗口后迟到应答仍保存在控制所有者LastAcknowledgement，尚未自动接入通道观察流。设备指标、各模式亮度／纯熄屏、通用控制队列、App／协调器／目标重枚举证据组合及真实运行验收尚未完成。未运行输出、打开设备、创建／运行测试或提交代码。

后续已将同一原应答Task的迟到结果接入ObserveAsync，新增发送前的额外结果位置预留、上下文核对和关闭等待所有观察任务，见`hid-late-observation-baseline.md`。仍无App或实际设备运行证据。
