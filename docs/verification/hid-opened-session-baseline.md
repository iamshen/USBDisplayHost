# HID 控制会话打开与关闭基础

日期：2026-10-09。关联FR-11、T-22、TC-22，任务11.2／11.3。仅记录源实现与目标编译。

## 同一文件核验与交接

HidInterfaceIdentityProbe新增显式OpenAd102V2SessionAsync入口；创建探测器不打开设备，App尚未调用新入口。必须提供当前DeviceSelectionAdmission、选定真实DeviceIdentity、完整V2 collection兼容记录、TimeProvider及1～60000ms等待预算。路径预约前后及最终交接检查发现凭据；打开前检查凭据，实际身份不一致则不查询Feature；原生读写提交前再次检查当前凭据和关闭／收敛状态。

控制文件采用读写访问及FILE_FLAG_OVERLAPPED。复用既有实际HID类、VID／PID／设备修订、usage、三类HIDP_CAPS长度、真实序列号及PnP容器／位置核验，在同一文件读取ID 3 Feature并交由Core解析；核验通过后直接交付该文件，不先关闭再打开另一个未经核验的句柄。当前V2适配器仅接受实际caps与已核对64字节契约一致；任一长度不匹配则拒绝，不将常量作为未核验设备的动态长度。

所有探测与控制会话共享大小写不敏感的HID路径预约，上限32。每个探测所有者最多交付一个活动控制会话；原会话存在时拒绝新的身份／Feature探测与控制会话打开。只有确认文件关闭才移除路径预约；预解析数据释放、打开后的关闭或原生请求资源清理未知时，保留原所有者，禁止用新实例绕开。探测器静态登记持有活动会话及未知资源，不依赖调用方继续引用才能保留资源。

依据：[Opening HID collections](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/opening-hid-collections)、[HidD_GetFeature](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getfeature)。Feature长度来自同一collection caps核验，首字节指定报告ID。

## 读写及收敛

HidOpenedSession为Windows层公开会话，构造器为内部，仅探测器核验后交付；不向Core暴露句柄。Input／Output分别最多一条在途原生请求，可独立等待。读取返回不可变实际字节及UsbWriteResult；短读保持真实长度，后续解码不能据此更新设备状态。写入口只接受现有V2模式命令ID 2／0x31、目标0或1、非零token和零保留字段；私有快照同时用于验证与发送，未知命令不会进入原生层。原生完整写入仅为传输收据，不表示固件受理或模式完成。

等待超时／取消返回观察结果后，原请求保持占位。CancelAndDrainAsync先取消两个方向，再等待各自原任务及必要的ReapAsync；不能等待读取时忘记取消写入。LastWriteReceipt提供本次观察及收敛后的实际最终写收据，供后续Core ledger关联原命令，收敛不重新提交命令。收据更新核对原请求引用，完成已知后拒绝迟到未知结果覆盖，新请求受理时清除上次收据。CloseAsync先拒绝新请求，收敛两个方向，再关闭文件；任一阶段未知则返回Unknown，不移除所有者或路径。CloseAsync结果由调用方检查；DisposeAsync不因一般关闭故障抛出而结束应用，未知会话仍由探测器保留。

诊断阶段包含报告准备、会话打开、I/O、收敛和关闭。身份不符、无效报告、已停止、已有同方向请求、发现凭据失效等拒绝不会写入设备；取消发生在同步元数据查询期间时，不取消等待Task并遗弃后台文件，而在原查询返回后处理关闭。打开清理未知覆盖原Failure／Cancelled结果为Unknown。

## 构建与剩余工作

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-opened-session/
```

四生产工程成功，0警告、0错误。Windows.dll SHA256：`98346836873F64F2647C38AA058E5F24CCAB4D4CBC78CCE979172F6CC9DD0DDF`。Core没有新增WPF或原生句柄依赖。未启动此输出、打开设备、运行原生故障／拔插验收或创建／运行测试；当前已启动UI仍为hid-overlapped-io输出。

持续Input循环、有界分发、串行控制队列、Core token ledger连接、Feature重新查询、设备指标／各模式亮度及App组合尚未完成；当前只有显式会话API，没有自动设备访问。11.2／11.3父任务保持未完成。本记录补充hid-overlapped-io-baseline的后续调用者与路径所有权，不能以编译结果宣称硬件或双向模式切换通过。

后续持续Input循环、有界状态分发与单执行者模式控制／Core ledger连接已实现，另增加控制文件交接前的collection输入队列清理、初始应答token基线及8秒观察所有权；最终状态见`hid-input-control-baseline.md`。Input改为等待报告或取消，写预算保持有限。没有新增原生运行证据，App和完整控制功能仍未完成。
