# HID 活动会话 Feature 重查

日期：2026-10-09。关联FR-11／FR-12、T-22、TC-22，任务11.2／11.5。证据限于源实现与目标编译。

## 原文件查询与所有权

HidOpenedSession新增QueryFeatureAsync，模式控制所有者通过QueryFeatureOwnedAsync调用同一会话文件上的ID 3查询。独立调用者不能绕过已接入的控制写所有者。Output未收敛、已有Feature查询、正在关闭／收敛、原资源未知或发现凭据失效时拒绝查询；Feature执行期间拒绝新的Output及写所有者接入。Input可独立持续读取，不因只读Feature查询被取消。

同步HidD_GetFeature在后台工作中执行，不取消等待Task或提前释放文件。执行前后检查原会话凭据；缓冲非零填充，按已核验的实际V2长度设置ID 3，由Core完整解析。成功返回新解析对象及单调采集时刻，不替换打开时Codec，不改变冻结的模式请求、电源意图、token或声明已切换。依据：[HidD_GetFeature](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getfeature)，Feature长度使用原collection的HIDP_CAPS契约核验。

取消在驱动返回后交付Cancelled；普通驱动、解析和时钟异常通过诊断及结果返回。查询调用前已取消则不调用设备。CloseAsync／CancelAndDrainAsync捕获原Feature任务，等待其返回后再完成原文件释放；查询失败表示状态不可用，不等同原生资源仍在使用，也不以读取失败伪造写入效果。

## 协议信任失效

非法Feature、协议字段不匹配或声明能力不同于打开基线，会使当前控制信任失效。后续读写、查询和控制所有者接入被拒绝；当前读取器立即发布原错误的无有效状态快照并发出读取取消，不能在等待下份Input时仍显示有效旧状态。原生读取仍由原请求保留并在关闭时收敛，信任失效不提前释放文件或丢弃内核请求。读取器关闭等待由Feature触发的原取消任务再清理取消源。

Ad102V2ModeController仅在无执行者且未失效时提供Feature重查入口。返回的是实际协议观察；同设备重枚举、目标驱动、显示路径、电源感知初始化及首帧核验仍由目标证据流程证明，不能仅据Feature当前模式报告切换完成。

## 构建与剩余工作

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-feature-requery/
```

四生产工程0警告、0错误。Windows.dll SHA256：`F73311DBAC2A17466B96C9D9DEDC2B5E6B6BBD31F4AAA44D77D1F9827218ECD4`。Core没有新增WPF、Windows或原生句柄依赖；新增查询没有在App启动时启用。

未运行此输出、打开设备、创建／运行测试或调用新增原生方法。App／IControlChannel接入、目标证据采集及真实超时分类、完整控制队列、设备指标／各模式亮度仍未完成；同步驱动调用挂起及取消／拔插／协议变化没有运行证据。11.2／11.5父任务保持未完成。本记录补充hid-input-control-baseline中Feature重查尚未接入的后续实现。
