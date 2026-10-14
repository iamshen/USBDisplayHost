# 单实例与托盘实施状态

日期：2026-10-08。关联 REQ-07／FR-07、T-17、TC-13／TC-33，OpenSpec 9.1～9.4。

## 已实施生产路径

显式 STA Main 在创建 WPF Application 前取得同用户会话的 Local 命名互斥锁。名称使用用户 SID 与登录会话号的哈希；锁由启动 STA 线程持有至 Run 返回并释放。废弃锁允许接管，权限或其他取得失败进入启动错误提示，不绕过锁启动设备服务。Velopack hooks 尚未实现，仍须在此锁之前接入。

二次启动只通过同名、CurrentUserOnly 的异步双向命名管道请求打开原窗口，最多等待 3 秒确认；失败给出原实例仍在运行及托盘打开建议，不创建另一套 WPF／设备服务。服务端单连接顺序处理，客户端读取有 2 秒时限；唤起只投递 Dispatcher，不在通知线程等待 UI，启动尚无窗口时保留一次请求。参见 [PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)。跨权限级别连接是否受限仍需实机核验，失败不启动第二实例。

Desktop 持有一个隐藏的 HwndSource，Windows 适配器持有 Shell 通知区图标。图标使用系统共享应用图标，不释放共享 HICON；退出删除图标并释放消息源。注册 TaskbarCreated 后尝试重建图标，失败显示窗口和错误编号。版本 4 通知只解析低 16 位事件。参见 [NOTIFYICONDATAW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) 和 [Shell_NotifyIconW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)。

普通关闭默认隐藏到托盘，保持离线编辑、素材操作及主机采样；窗口隐藏同时暂停草稿预览，不创建新页面或服务。托盘提供打开、退出和明确禁用的未连接设备操作。不存在模式切换或暂停命令实现，不冒充可操作设备。

首次收托盘提交通知，并使用已保存应用设置仅更新 HasSeenTrayNotice。高版本／损坏恢复提示不被静默覆盖；保存失败保留错误编号，本次进程已提示但下一次启动仍可能再次提示。系统设置可能抑制 Shell 通知的实际展示，当前仅记录调用与保存代码，不能声称用户已看见提示。

显式退出先等待在途首次提示存储，再处理草稿保存／放弃／取消，按顺序停止文件命令、预览、渲染和指标；不发送 POWERDOWN。关闭期间拒绝重复关闭或退出。托盘创建失败时保留窗口并提示关闭将执行退出；运行中托盘不可用则保留窗口并提供退出决定。系统退出不弹草稿确认，仍只有尽力异步清理。

## 证据与剩余范围

四生产工程目标 Release 构建 0 警告、0 错误，差异空白检查通过。未运行应用、多进程、Explorer 重启或测试；没有窗口截图、实际管道／通知消息或句柄测量证据。

自启保存／撤销适配、关闭行为与未保存应用设置决策已接入，详见 `application-settings-baseline.md`；结构化日志退出清空见 `diagnostics-baseline.md`。统一包含设备／更新的 ApplicationLifetime、跨程序占用、托盘设备动作、启动自动切换、退出切副屏及关机有界清理仍未完成；已有代码无运行证据，9.1～9.4 父项保持未勾选。
