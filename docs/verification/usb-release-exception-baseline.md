# USB 释放异常边界代码基线

关联 REQ-01、D3、T-10／T-17，任务 4.3／8.1。

SafeWinUsbHandle.ReleaseHandle 不调用日志或其他外部诊断服务，避免诊断实现抛出普通异常时再次从释放／终结器边界抛出。原生释放失败和普通托管异常缓存为 ReleaseError，返回未确认；诊断在正式会话、打开失败或身份探测的上层处理路径执行。

NativeReleaseConfirmed 单独表示 WinUsb_Free 返回成功。只有原生释放成功且文件 DangerousAddRef 引用解除完成，ReleaseConfirmed 才为 true。文件引用解除的普通异常也被同一边界捕获，保留原始错误并返回未确认；不能把原生释放成功当作全部所有权已解除。SafeHandle 关闭状态不构成释放确认，未知结果继续保留所有者与路径预约，不重复 Free。

打开失败先保存 OpeningOwner，再将原始释放错误作为 CleanupUnconfirmedException 的 InnerException 交给上层诊断；文件关闭失败不在保留所有者之前调用日志。正式会话关闭也保留原始释放原因，最终释放回调仍收到 confirmed=false，依赖文件不会提前释放。身份探测使用缓存的真实释放错误，不用泛化错误覆盖其来源。

生产 DiagnosticLog.ReportCore 已捕获普通记录异常，返回诊断写入未确认状态；该事实不替代 SafeHandle 自身的异常隔离。没有承诺 CLR 致命故障或内存耗尽后继续运行。

仅运行目标 Release 构建，四个生产工程 0 警告、0 错误。未创建或执行测试、故障注入、终结器实验或原生释放；ABI、失败分支和句柄稳定性的运行证据仍缺失，4.3 父项保持未完成。
