# Windows 发现服务创建代码基线

关联 REQ-01／FR-01、D1／D2、T-09，任务 4.2。

`WindowsDeviceDiscoveryFactory.CreateAsync` 将显式 `WindowsDiscoveryConfiguration` 组合为 WindowsDeviceDiscovery。配置明确携带 HID 接口 GUID／兼容记录和 WinUSB 兼容记录，没有设计 VID/PID 或接口 GUID 默认值。支持任一协议或双协议；未使用的清单必须明确为空，不能使用未初始化数组。HID GUID 与 WinUSB GUID 冲突、空清单和超过四个接口类在通知注册前拒绝，各探测器继续验证记录限额、重复项和证据引用。

创建顺序为实际探测器、组合路由、接口通知注册、发现服务。创建不执行枚举、身份读取、连接或设备命令；监视器注册会调用原生通知 API。调用方获得成功后拥有发现服务，必须先实际消费 WatchAsync 再 EnumerateAsync，关闭由 WindowsDeviceDiscovery 的单一关闭任务收敛。

中途失败或取消后等待已创建资源关闭，不以调用方取消丢弃清理任务。已创建监视器先注销，之后组合探测器收敛并关闭；组合创建前仍由入口逐项收敛并释放探测器。未知注销／释放转换为 Unknown，停止后续释放，相应底层强引用所有者保留预约，不能通过重新创建替代。监视器注册自身失败时的未知所有者由其原生回调所有权链保留。普通创建／清理异常保留错误编号及恢复建议。

入口未在 App 调用；真实接口／固件兼容记录、设备选择、连接和发现生命周期仍需接入。没有将身份清单解释为模式、控制协议或设备能力授权。驱动调用的有界关闭、热拔插与多设备行为未验证。

仅运行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四个生产工程 0 警告、0 错误。未创建或运行测试，未实例化监视器、调用原生 API、打开设备或运行窗口。4.2 父项及真实设备验收保持未完成。
