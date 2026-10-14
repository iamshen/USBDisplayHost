# USB 文件关闭确认代码基线

关联 D3、T-10／T-11、任务 4.3。此记录补齐正式会话及身份探测的文件释放确认代码，不是原生取消竞态或句柄增长验收。

WinUSB 正式会话、打开失败清理、同步身份探测与重叠请求均已改用自有 `SafeUsbFileHandle`，统一 CreateFile、WinUsb_Initialize、CancelIoEx 的句柄类型。句柄保持 SafeHandle 的调用引用保护、WinUSB 接口对文件的 DangerousAddRef 所有权，不将 raw HANDLE 或 WPF 类型泄露到 Core。

SafeUsbFileHandle 在 ReleaseHandle 保存 CloseHandle 的 BOOL 结果和普通释放异常。只有 ReleaseConfirmed=true 才作为已确认文件关闭；IsClosed、调用 Dispose 或托管对象不可用均不能替代该证据。不重新调用未知 CloseHandle，避免猜测句柄数值是否已经失效或复用。

打开失败清理按 associated WinUSB、primary WinUSB、文件顺序执行。任一接口释放未知停止后续依赖释放；文件已打开但关闭未知时保存 OpeningOwner（文件、接口及路径预约）、返回 Unknown，拒绝原工厂再次打开。无效打开结果不需要释放未获得的原生句柄，只有明确清理完成才释放路径预约。

正常会话关闭先确认在途请求与取消回调收敛，再释放 associated／primary，最后确认文件关闭。文件关闭未知写入诊断、关闭任务失败、OnReleased(confirmed=false) 保留原会话和路径预约。路径不在 USB 接口释放之后提前开放，因此探测器不能在文件仍未知时重新初始化接口策略。

同步身份探测也复用相同文件句柄；WinUSB 探测先释放 USB 接口，失败时保留文件引用，不能关闭依赖文件。所有未知所有者保留策略沿用原模块生命周期；普通异常由适配器／管线返回业务失败或 Unknown，不由全局异常处理器冒充收敛成功。

仅执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四工程 0 警告、0 错误；差异空白检查通过。未执行 CreateFile、CancelIoEx、CloseHandle、WinUSB、故障注入、测试或窗口。实际 ABI、同步／pending／取消、释放失败与句柄增长证据仍缺，4.3 父项、TC-06／TC-16 保持未完成。
