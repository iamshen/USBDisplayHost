# 原模式期限与目标上下文等待

日期：2026-10-12。关联 FR-12、T-22、TC-21／TC-22，任务 11.3／11.5。源实现与目标编译证据。

Core mode ledger 新增 CaptureRemainingWait，Windows 控制所有者转交该查询。只允许原已 BeginWrite 的请求读取原单调起点剩余预算；八秒到期返回零，未开始写入、上下文错配、时钟负间隔或普通时钟异常不能伪造预算。调用方应在等待前立即读取，不能为 ACK 与重枚举分别重给八秒。

ModeTransitionLease 新增唯一异步目标上下文完成槽，提升代次时交付同一操作 ID／修订的新上下文；一般失效、取消和工作结算结束等待。WaitForTargetContextAsync 使用注入 TimeProvider 和原剩余预算，仅取消观察，不取消或释放原通道。到期、取消、过期交付与普通观察异常返回 Unknown；交付前检查取消及协调器当前性。无轮询、重发或根据等待结束推断原生 I/O 已完成。获得新上下文也不是实际目标 Ready 证明，后续仍须原资源关闭、新发现凭据受理和目标证据核验。

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/mode-target-wait/
```

四生产工程 0 警告、0 错误。Core.dll SHA256：`05AAC8A16BE73D5E5C3CEE79291E7CD3D806F0802BEC38C2ADFC5AEE23576B2E`。未启动输出、调用设备、创建或运行测试。模式工作与实际目标采集仍未组合，真实期限／竞态行为未验证；11.3／11.5 保持未完成。
