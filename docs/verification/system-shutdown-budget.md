# 系统结束会话清理观察预算

关联：D8、REQ-08／FR-08、T-17，任务 9.4。

WindowExitService.NotifySystemExit 设置系统结束标志并启动唯一 StopSystemAsync。App 在正常组合根及启动失败后的已有工作区恢复路径中显式注入 2 秒等待预算和 TimeProvider.System。预算是应用清理结果观察策略，没有当作操作系统承诺的退出期限；构造契约接受 250ms～10s，超出范围返回配置失败而不启动该观察流程。

实际资源关闭仍使用同一个 ThemeWorkspaceLifetime.StopAsync 任务及其既有顺序。ObserveSystemCleanupAsync 捕获普通异常为带编号的 Unknown，WindowExitService 持有该实际观察任务；WaitAsync 只限制本次等待，不取消关闭、不释放原生缓冲或句柄，也不放弃待保存配置。超时记录 SystemCleanupDeadline 并返回 SystemCleanupTimedOut；保留实际任务直到操作系统终止或其自行完成，不能从超时推导无 I/O、已关闭或重试授权。没有重复发起关闭链。超时观察结果不在后来任务完成时改写为当时已成功。

系统结束路径不弹保存决策，不等待 Dispatcher 的同步阻塞循环，不试图延迟操作系统关机。普通收托盘和显式退出的异常处理在系统结束已经开始时只记录诊断，不再唤起窗口或显示阻塞对话框。正常用户退出仍检查资源结果并在失败时保留窗口。SystemCleanupResult 提供本次结果任务，不能把它当作完整资源所有者的完成状态。

如果操作系统立即停止进程或 Dispatcher，这个异步期限观察可能没有机会运行至期限；已关闭日志的后续记录也不能保证落盘。OnExit 仍进行尽力桌面资源释放；本实现不证明数据刷盘、句柄释放、最后一帧表现或系统进程退出时长。UpdateRestart、退出切副屏、操作系统实际期限和设备资源验收仍未完成，9.4 父任务保持未完成。

仅执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，0 警告、0 错误，及 `git diff --check`。未运行窗口、关机、超时流程、设备接口或测试。
