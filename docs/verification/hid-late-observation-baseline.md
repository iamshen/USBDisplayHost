# HID 迟到应答观察分发

日期：2026-10-09。关联FR-12、T-22、TC-22，任务11.3。仅为源实现与目标编译证据。

## 原任务与上下文

Ad102V2ModeController为本次应答任务保存原OperationContext。CaptureAcknowledgement只捕获同一上下文的原Task；没有原在途请求且应答也未完成时不创建虚假的等待。模式控制返回Unknown后，Ad102V2ControlChannel可继续观察该Task，不能根据最近token再拼装另一个请求或重发命令。

模式命令发送前同时预留本次返回观察及潜在迟到观察的两个位置。不能取得第二个位置时不发送；立即得到受理／明确拒绝时释放未使用位置。Unknown先进入结果队列，随后将额外位置交给原应答观察任务，保证后续结果在原未知观察之后发布。该任务保存于通道字典，保留的每个等待者都对应一个预留位置，总内存受16个结果位置约束，不以DropOldest丢弃重要控制结果。尚有迟到等待时拒绝复用同一操作上下文。

迟到Accepted发布原上下文的Accepted=true、Completed=false，模式／电源／亮度仍未知；明确拒绝发布对应错误。只有ledger核实原完整写收据及应答关联后才有有效受理结果，不能用ACK单独替代原生收敛证明。发布前检查原物理归属、当前操作和控制所有者有效性；观察消费仍经过既有上下文检查，旧应答不能完成新操作。

## 取消、释放与关闭

等待使用原应答Task的WaitAsync，只取消这一观察，不取消或丢弃原生I/O／控制记录。原会话失效、通道停止、取消观察或没有可交付结果时释放预留位置；已进入队列的位置由实际消费者释放。普通观察异常记录V2LateObservation诊断阶段，不能让后台异常关闭应用。

通道CloseAsync停止受理，取消生命周期并关闭原控制所有者，等待原ExecuteAsync结束，再捕获并等待剩余迟到观察任务，最后关闭观察队列和成功关闭后的取消源。原控制所有者仍负责原I/O、应答和关闭未知资源；观察结束不能解除其所有权。每个迟到任务在完成路径移除自身登记，避免已完成Task无限积累。

## 构建与剩余范围

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-late-observations/
```

四生产工程0警告、0错误。Windows.dll SHA256：`ED8E16DE200E4F7FD2A117F12F573BAE9105DF39B21958DC822BFEA440244841`。Core接口不变，没有增加WPF／Windows／原生句柄依赖。

未运行输出、打开设备或创建／运行测试。Desktop／协调器、完整控制队列、目标重枚举证据、指标／亮度／纯熄屏及真实竞态验收仍未完成。11.3父任务保持未完成。本记录补充hid-control-channel-baseline中迟到应答未自动分发的后续实现，不代表模式切换或真实设备验收成功。
