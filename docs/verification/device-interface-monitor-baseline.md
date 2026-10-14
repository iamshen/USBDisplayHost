# Windows 接口通知与枚举代码基线

关联 REQ-01／FR-01、D2／D3、T-09、任务 4.2。当前只实现 Windows 接口路径监视模块，不等同于完整 IDeviceDiscovery、身份匹配或已可连接设备。

## 已实现

`WindowsDeviceInterfaceMonitor.CreateAsync` 必须提供冻结记录中的 1～4 个不同非空接口 GUID，没有默认设备值。最多 8 个监视所有者，共用进程内接口类预约；已被任何所有者持有的 GUID 拒绝重复监视。只有全部通知注销明确成功才释放预约，未知关闭保留上下文与占用，不用新实例替代。

所有接口类先调用 CM_Register_Notification，注册完全成功后才开放枚举。原生回调推进变化代次并 TryWrite 容量 1 的重扫信号；移除事件另以 16 个原子槽保留经有界结构／路径校验的移除路径，无法完整记录时发布归属未知信号。详见 `device-removal-baseline.md`。不打开设备、不在系统通知线程执行日志／I/O、不保留事件指针；一个消费方读取变化流。

枚举通过 CM_Get_Device_Interface_List_SizeW／CM_Get_Device_Interface_ListW 读取 PRESENT 接口，每类最多 65536 个 UTF-16 字符，BUFFER_SMALL 最多重取三次。验证路径终止与 MULTI_SZ 终止、仅接受设备符号路径、单路径最多 32767 字符，总列表最多 256 个接口；按忽略大小写路径去重，同路径冲突归属拒绝整次列表。取消／普通异常不返回部分成功列表。

返回路径、接口 GUID、结束变化代次以及 ChangedDuringEnumeration。枚举期间事件发生会再次投递重扫；后续身份探测与选择不能把有变化的清单当作稳定连接事实，必须核实当前代次与路径。路径不携带推断出来的 VID/PID、序列号、模式、固件或驱动支持结论。

注销在后台串行门执行，与枚举互斥；不在回调中注销。CM_Unregister_Notification 返回成功之后才释放对应句柄记录，全部成功后才释放 GCHandle 回调所有者。注册部分失败也走相同收敛；失败的注销保留未知所有者，后续 Close 共用已完成结果，不自动重试。CONFIGRET 只进入诊断，返回业务原因、动作与错误编号。

ABI 使用本机 Windows SDK 10.0.26100.0 的 cfgmgr32.h 静态核对：MAX_DEVICE_ID_LEN=200，CM_NOTIFY_FILTER 大小 416，接口 GUID 位于偏移 16；Callback 返回 DWORD，参数为两个指针、枚举、事件指针及 DWORD 大小；PRESENT=0，CR_BUFFER_SMALL=0x1A。没有执行 ABI 探针或原生调用。

接口依据：[CM_Register_Notification](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification)、[CM_Unregister_Notification](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_unregister_notification)、[CM_NOTIFY_FILTER](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ns-cfgmgr32-cm_notify_filter)、[CM_Get_Device_Interface_ListW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_interface_listw)。

## 验证范围与剩余项

仅执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四生产工程 0 警告、0 错误；差异空白检查通过。没有创建或运行测试、调用 PnP、枚举本机设备、打开 WinUSB/HID、安装驱动或启动窗口。

模块未在 App 启动时创建，因为实际接口 GUID 与兼容记录尚未冻结。仍需描述符／可信序列号／容器与位置探测、驱动与能力核验、IDeviceDiscovery 适配、明确选择及连接、离线优先失效、热拔插竞态与通知句柄增长验证。注册／注销失败、回调竞态、真实多设备、ABI 和资源证据均未具备，任务 4.2 父项与 TC-01～TC-04 保持未完成。
