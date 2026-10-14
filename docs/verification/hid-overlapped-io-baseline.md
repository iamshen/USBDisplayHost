# HID 重叠 I/O 请求基础

日期：2026-10-09。关联FR-11、T-22、TC-22，任务11.2／11.3。

## 请求与资源归属

Windows新增内部HidIoNative与HidOverlappedRequest，用kernel32 ReadFile／WriteFile读取Input及发送Output，独立于WinUSB接口句柄。连续Input不使用HidD_GetInputReport。后续会话工厂必须在同一FILE_FLAG_OVERLAPPED文件句柄核实身份、collection及HIDP_CAPS，并以实际报告长度创建请求；本基础类不代替这些核验。

请求接受1～4096字节报告长度，Output必须精确匹配长度，Input使用零初始化缓冲。输出先复制成请求私有数据，输入只在内核完成且长度合法后复制为不可变结果。每个请求独立持有非托管缓冲、唯一OVERLAPPED、手动重置事件及文件DangerousAddRef引用。短读／短写保留实际长度，不补齐成完整报告，也不自动补写。

静态登记强引用同一SafeUsbFileHandle的Input／Output各一个请求位置，总计上限32；未知完成保留位置及全部资源。它不提供同路径不同文件句柄的所有权门禁，该门禁仍须由后续会话工厂建立。没有通过增加请求数量绕过读／写串行位置。

## 超时、取消与回收

RunAsync只启动一次原生请求。超时采用注入TimeProvider的Delay，等待预算1～60000ms；CancellationToken回调与提交锁串行，避免请求尚未发布时取消丢失。等待期限或取消先结束本次结果观察，返回完成未知和超时／取消分类，发出仅针对原OVERLAPPED的CancelIoEx；原完成任务继续持有内存和文件引用，不能据观察结束重建请求。

CancelIoEx的ERROR_NOT_FOUND不证明操作完成。GetOverlappedResult在独立后台工作中等待原请求，成功或非pending结果与完成事件共同确认内核终止；ERROR_IO_INCOMPLETE／IO_PENDING仍为未知。必须再等待取消回调DisposeAsync结束，才释放缓冲与位置。不持有请求锁等待回调收敛。

ReapAsync等待同一完成任务；之前完成查询未知且原完成工作已结束时，仅重新查询原OVERLAPPED，不再次ReadFile／WriteFile。未Run的请求也可Reap，清理资源且不执行设备I/O。受理失败和无效参数的路径经过相同回调清理边界。准备、执行、取消和清理异常记录独立诊断阶段；清理未确认时返回未知并保留登记，不抛出一般异常让上层进程退出。

来源：[HID报告读取](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports)、[ReadFile缓冲与重叠约束](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-readfile)、[CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)、[GetOverlappedResult](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getoverlappedresult)。

## 编译与未完成范围

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-overlapped-io/
```

四生产工程0警告、0错误。最终Windows.dll SHA256为`8CEF9B6CD1D26686BB660B6B57380BE8A8C45D466B943668095255584D9F86B3`。后续最终失败收据保留已过等待期限的标记；真实完整成功收据不因先前观察超时被改写为失败。未创建或执行测试、打开设备、调用新原生方法或修改驱动。新类型为Windows内部实现；Core无新增Windows／原生句柄依赖。

目前没有请求类实际调用者。经过核验的overlapped会话工厂、路径所有权、持续Input读取与有界分发、串行控制／Feature操作、Core token／超时记录及App组合尚未接入，原生故障／取消／拔插行为没有运行证据。11.2／11.3父项保持未完成。

后续源实现已由HidOpenedSession调用该请求类，并由HidInterfaceIdentityProbe核验同一文件后交付会话，补齐路径所有权和安全关闭。请求提交前另核对会话／发现凭据；最终状态见`hid-opened-session-baseline.md`。此后续仍没有原生运行证据，读取循环／控制队列／App组合尚未完成。
