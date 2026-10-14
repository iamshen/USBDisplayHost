# PnP 容器与位置元数据代码基线

关联 REQ-01／FR-01、D2、T-09、任务 4.2。模块仅读取系统接口归属与属性，不替代设备真实序列号、驱动支持或控制能力核验。

`PnpInterfaceMetadataReader` 已接入 HID 属性和真实序列号读取之后。SetupDiGetClassDevsW 使用 PRESENT＋DEVICEINTERFACE，为明确接口 GUID 建立本机信息集；SetupDiOpenDeviceInterfaceW 使用 DIODI_NO_ADD 打开当前枚举路径，不把未知接口加入已存在清单。检查实际接口 GUID、ACTIVE 与 REMOVED 标志，再查询当前接口所属 DEVINST。按照官方约定，路径 detail 缓冲为 null、大小为零，ERROR_INSUFFICIENT_BUFFER 时仍取得初始化的 SP_DEVINFO_DATA，不解析设备符号路径。

对该 DEVINST 读取 DEVPKEY_Device_ContainerId 和 DEVPKEY_Device_LocationInfo。属性先查询字节数及 DEVPROPTYPE，再读取实际值；缓冲变更最多重取三次。容器仅接受 GUID 类型和 16 字节非空 GUID；位置仅接受 STRING 类型、最多 2050 字节、完整终止 UTF-16、最多 1024 字符且无控制字符。仅 CR_NO_SUCH_VALUE 表示属性缺失，可保留 null；访问失败、类型／大小异常、节点失效或解码失败不能变成缺失成功。

容器与位置只作接口辅助归属，不进入永久键，VID＋真实设备序列号继续作为身份依据。容器缺失时多个同键接口仍被选择解析器拒绝；同容器属性存在也不证明可发送控制报告、打开 WinUSB 或支持屏幕模式。设备变化仍由发现链变化代次守卫核实，属性读取不是跨热拔插的原子快照。

所有元数据读取在后台串行，最多一个当前 PnP 信息集。自有 SafeHandle 核实 SetupDiDestroyDeviceInfoList 的 BOOL 结果；未知释放时静态保留一个信息集与错误，拒绝继续分配，并传递到 HID 探测和 drain／close，不能以关闭托管包装当作资源释放证明。没有自动重试未知销毁，不调用安装、驱动更新或设备配置写入接口。

ABI、GUID／PID 和标志通过本机 Windows SDK 10.0.26100.0 的 setupapi.h、devpkey.h、devpropdef.h、cfgmgr32.h 静态核对；CONFIGRET 与 Win32 LastError 分别处理，不混用。官方依据：[SetupDiGetDeviceInterfaceDetailW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceinterfacedetailw)、[SetupDiOpenDeviceInterfaceW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdiopendeviceinterfacew)、[CM_Get_DevNode_PropertyW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw)、[DEVPKEY_Device_ContainerId](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid)。

仅执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四生产工程 0 警告、0 错误；差异空白检查通过。未调用 SetupAPI／CfgMgr32／HID、探测设备、注入数据、启动窗口或编写／运行测试。

实际容器关联、多 HID collection、两台设备重复键、缺失属性、热拔插、权限、ABI 与信息集增长未有运行证据。Windows 数据面探测、驱动／能力、App 选择与连接、离线优先仍未完成；4.2 父项和 TC-01～TC-04 保持未完成。
