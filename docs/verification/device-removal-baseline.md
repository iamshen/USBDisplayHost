# 设备移除通知接续代码基线

关联 REQ-01／FR-01、D2／D3、T-09，任务 4.2。

Windows 接口通知回调对 DEVICEINTERFACEREMOVAL 校验事件类型、保留字段、接口 GUID、声明长度与终止 UTF-16 符号路径。依据 [CM_NOTIFY_EVENT_DATA](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ns-cfgmgr32-cm_notify_event_data) 和 [CM_NOTIFY_ACTION](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ne-cfgmgr32-cm_notify_action)，并静态核对本机 SDK 10.0.26100.0 cfgmgr32.h；没有执行 ABI 或原生调用。

回调只复制路径到 16 个原子槽，推进变化代次并投递有界重扫信号，不执行日志／设备 I/O／协调器操作，不保存事件指针。通知消费者先取出移除路径，再发布重扫。路径不做竞态去重，重复事件可能占用多个槽；超限、无效或无法完整复制的事件置一位归属未知标记，后台发布 Removed(null)，不静默把重要移除当成普通重扫。单路径最多 32767 字符，声明数据最多 65560 字节。

发现生命周期在订阅启动前绑定唯一移除接续动作，后台撤回对应活动 WinUSB 身份再调用接续动作。未知归属撤回全部活动身份。工作区附加入口现接受已经创建的 WindowsDeviceDiscovery，并先创建带接续动作的生命周期，避免先消费通知再绑定回调的空档；成功后工作区拥有整条链。

工作区比较当前协调器 Identity 的接口路径。已匹配的移除按当次读取的设备键／会话代次 NotifyDisconnected；未知归属也使当前会话失效，并提供不同业务原因和错误编号。NotifyDisconnected 同步登记失效标记，已有 Core 存活检查停止沿用该代次，状态发布和在途操作收敛仍由协调器完成。接续不依赖窗口可见性，不自动重连或丢弃待保存结果。

完整选定接口集合匹配和按对应管线请求自动关闭已补齐，见 `session-interface-removal-baseline.md`。重新连接受理与通知代次共同门禁、模式切换后的集合受理及正式连接组合仍未完成。不能把失效登记当作所有句柄已释放，也不能凭编译证明拔出／快速重插和迟到通知无竞态。实际兼容记录与 App 发现启动仍未接入。

仅运行目标 Release 构建，四个生产工程 0 警告、0 错误。未创建或运行测试，未执行通知回调、原生接口、设备操作或 WPF 窗口；4.2 父项及真实设备验收保持未完成。
