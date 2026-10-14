# WinUSB 数据面身份探测代码基线

关联 REQ-01／FR-01、D2、T-09、任务 4.2。只补齐发现链的数据面身份读取，不提供屏幕初始化、模式切换或帧发送。

`WinUsbInterfaceIdentityProbe` 接受 1～8 条显式接口 GUID、VID/PID、设备修订及证据来源记录，无设计值默认清单。最多 8 个强引用探测所有者，同接口 GUID 不允许重复接管，未知释放继续保留占用。创建本身不执行原生调用。

每次探测后台串行打开当前路径，FILE_FLAG_OVERLAPPED 后初始化默认 WinUSB 接口。读取实际 18 字节设备描述符、完整语言描述符和指定语言的序列号字符串描述符，验证类型／长度／修订和严格 UTF-16，VID＋真实序列号形成永久键。明确非目标 VID/PID 才返回成功 null；目标修订未知、无序列号、截断或访问失败拒绝整个发现结果。容器与位置通过当前 PnP 接口归属读取，不从路径、PID 或修订生成永久身份。

没有调用 AF 初始化、EP0 固件命令、Bulk OUT、HID report、模式或电源动作，也不设置 pipe policy。必须区分这些业务动作与 WinUSB 初始化自身的影响：[WinUsb_Initialize 官方说明](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_initialize) 说明初始化会重置接口策略。为此新增 WinUsbPathOwnership，共用忽略大小写路径的原子占用，正式 WinUsbSessionFactory 和本探测器均在 CreateFile／Initialize 之前预约；最多 32 条路径，已有探测、活动或未确认释放占用时拒绝再次初始化。活动接口身份复用尚需上层接入，不能以探测失败跳过活动设备。

探测结束先确认 WinUsb_Free，之后才关闭自有 SafeUsbFileHandle，并确认 CloseHandle。任一释放未知都保留完整文件／USB／路径所有者，阻止重探测，不能提前释放依赖文件或重复初始化。同步描述符调用不通过丢弃 Task 取消；drain 等待串行门，不能保证驱动卡住时有界退出。

正式会话路径占用跟随 OnReleased 确认回调，打开失败清理未知也保留预约。正式会话、打开失败和身份探测已统一自有 SafeUsbFileHandle，文件 CloseHandle 独立确认后才释放路径，详见 `usb-file-ownership-baseline.md`；不能把代码回调或编译表述为完整原生释放验收。路径占用只覆盖复用该注册表的本进程代码，外部进程仍可能操作接口。

仅执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四生产工程 0 警告、0 错误；差异空白检查通过。未打开接口、读取描述符、执行命令、运行测试或窗口。

显式组合路由代码见 `composite-identity-probe-baseline.md`。具体兼容记录、活动接口身份复用、正式文件释放行为证据、App 选择与连接、驱动／能力和原生行为证据仍待完成。4.2／4.3 父项和 TC-01～TC-04／TC-06／TC-16 不勾选。
