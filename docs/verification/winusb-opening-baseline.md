# WinUSB 打开与接口查询实施记录

关联：REQ-01／FR-01、REQ-03／FR-03、T-10、任务 4.3、TC-06／TC-16。日期：2026-10-08。

## 已实现模块

Windows `WinUsbSessionFactory` 接受明确选择的 DeviceIdentity 和显式 WinUsbBinding；后者没有默认 VID/PID、bcdDevice、接口 GUID、关联接口索引、替代设置索引／编号或 Bulk OUT 端点。证据引用必须提供，构造记录本身不能证明它已经经过设备验收。

打开前核对选定身份和设备接口 GUID 后缀；CreateFile 使用 Unicode、读写访问、读写共享、OPEN_EXISTING 和 FILE_FLAG_OVERLAPPED，然后 WinUsb_Initialize。原生调用放在一个受监督后台任务；取消仅在调用间检查，不提前结束等待并丢失后台打开的句柄。依据 [WinUsb_Initialize](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_initialize)。

使用默认接口读取 18 字节设备描述符，校验 VID/PID、bcdDevice 和非零序列号索引；读取有效 LANGID 列表和首个语言的严格 UTF-16 序列号，按精确序列号核对选定设备。长度、类型、编码及截断失败均拒绝，不通过宽松路径匹配推断实际身份。依据 [WinUsb_GetDescriptor](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_getdescriptor)。ContainerId、位置和驱动服务尚须发现层核实，当前不把传入属性当作新证据。

如契约指定关联接口索引，仅打开这一关联接口，不扫描或猜测替代接口；查询指定替代设置索引，核对实际 bAlternateSetting、当前替代设置和接口编号。不调用 SetCurrentAlternateSetting，不擅自改变设备。依据 [关联接口 API](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_getassociatedinterface) 与 [接口设置查询](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_queryinterfacesettings)。

枚举描述符声明的有限端点数，拒绝重复地址、零端点、保留位、无效类型或包长；要求冻结端点为 Bulk OUT。分别读取 MaximumPacketSize 和 MAXIMUM_TRANSFER_SIZE，后者采用 4 字节策略缓冲并核对实际返回长度，不能用包长代替一次传输上限。当前要求额外提供显式 I/O 策略，并设置／读回超时和 SHORT_PACKET_TERMINATE；不手工添加 ZLP 或启用 RAW_IO。依据 [端点信息结构](https://learn.microsoft.com/en-us/windows/win32/api/winusbio/ns-winusbio-winusb_pipe_information) 与 [管道策略](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/winusb-functions-for-pipe-policy-modification)。

## 所有权与异常

原生句柄只存在于 Windows。SafeWinUsbHandle 持有底层 SafeFileHandle 的危险引用，WinUsb_Free 确认后才释放此引用；关联接口先于默认接口，最后关闭文件。成功返回 WinUsbOpenedSession 时转交所有权，查询失败／取消则回收局部所有者。工厂只允许一个打开工作和一个成功会话；会话释放完成前不再打开。普通原生／解码异常记录编号并返回失败，不自动重试。

Free 失败返回 Unknown 或使显式 Dispose 失败，工厂保留未知所有者并拒绝后续打开；不把句柄释放失败误报为完成。释放不确定时，底层文件引用保持到进程退出，不能据此宣称不存在资源遗留。WinUsbOpenedSession 已增加 [重叠 I/O 与 Drain 生命周期](winusb-io-baseline.md)，关闭先等待实际内核完成及取消回调结束。

## 实际验证边界

执行 `./build.ps1 -Action Build`，Desktop 目标 Release 及四个生产工程成功，0 警告、0 错误。仅编译，未调用 CreateFile／WinUSB，未打开设备或安装驱动，未运行应用或测试。

样机兼容表、描述符／串号／关联接口的实际读回、驱动绑定与签名、重复开关句柄、原生异步写入／控制／取消／超时均无运行证据。API 的静态签名与结构检查不能证明实际 ABI 或设备访问成功；不会自动生成 DriverVerified 或固件兼容能力。工厂已由 IDisplayTransport 适配器封装，设备协调器和 Desktop 尚未组合实际连接用例，4.3 父项保持未完成。
