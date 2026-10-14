# WinUSB 重叠 I/O 实施记录

关联：REQ-03／FR-03、REQ-09／FR-09、T-10／T-12、任务 4.3、TC-06／TC-16。日期：2026-10-08。

## 生产路径

Windows `WinUsbDisplayTransport` 实现 Core 的 IDisplayTransport，封装打开工厂及原生会话。一次最多一个打开或一个原生请求；原生类型不进入 Core。关闭先拒绝新工作，等待正在进行的打开，然后收敛在途请求，再释放接口和文件。成功打开所有权不会因用户取消等待而丢失。

`WinUsbOverlappedRequest` 为每次请求分配自有原生缓冲、OVERLAPPED 和独立手动复位事件。Bulk 写最多 min(245760, WinUSB 最大传输长度) 字节，拒绝空写；EP0 按 USB setup 的方向准备最多 4096 字节，支持明确的零长度 OUT。设备 recipient 使用默认接口，interface／endpoint recipient 使用目标接口；接口 recipient 核对接口编号。实际命令白名单仍由上层冻结契约负责，不能直接向页面公开通用控制接口。

WritePipe／ControlTransfer 以 OVERLAPPED 和空 LengthTransferred 调用；同步返回与 ERROR_IO_PENDING 分别处理，实际字节来自 GetOverlappedResult。该完成查询使用默认 WinUSB 接口。等待发生在唯一在途请求的后台任务，没有 UI 阻塞或每帧无界任务队列。签名依据 [WritePipe](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_writepipe)、[ControlTransfer](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_controltransfer) 与 [GetOverlappedResult](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_getoverlappedresult)。

## 策略、取消与释放

WinUsbIoPolicy 必须显式提供写／控制超时、SHORT_PACKET_TERMINATE 和证据引用，没有默认固件短包规则。打开时设置并读回 Bulk OUT 超时及短包策略；不填充未定义字节、不发额外手工空尾片。独立请求计时器限制从受理开始的等待，并对 EP0 提供主机取消期限；原生超时策略及主机计时器都不能证明驱动完成收敛有界。策略依据 [WinUSB 管道策略](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/winusb-functions-for-pipe-policy-modification)。

取消与请求提交在请求锁内串行，避免先 CancelIoEx 再提交导致漏取消；取消回调捕获普通异常，不释放请求。ERROR_NOT_FOUND 可能是完成竞争，不作为成功写入证据。调用方仍等待内核完成，计时器和用户取消回调结束之后才释放原生内存及危险接口引用。CancelIoEx 只请求取消，不代表请求已经终止，依据 [CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)。

完成不明确时保留原生请求／内存／事件／接口引用，禁止下一次 I/O。Drain 共享已开始的收敛任务，尝试取消并重新读取同一 OVERLAPPED 的完成结果；没有另建请求或重写原片。无法确认时返回 Unknown 并阻止 Dispose 释放句柄，工厂保留所有者且要求退出后重新连接。成功 Drain 仅证明请求结束，不证明固件 parser 清理。

UsbWriteResult 增加 WasTimedOut，仍保留实际原生错误、请求长度、确认完成长度与完成状态。Core 对超时可使用一次恢复周期，用户令牌取消不自动恢复。请求超时／失败时无法确认的字节不计入完整写入，零确认字节不证明设备毫无变化。EP0 已提交后的取消，以及 OUT 请求失败／短写返回效果 Unknown，不提示直接重放命令。LastIo 提供最近实际请求长度状态，没有呈现确认。

## 实际证据

执行 `./build.ps1 -Action Build`，目标 Desktop Release 及四生产工程通过，0 警告、0 错误。仅编译及静态审阅，未调用原生 API、打开设备、运行应用或测试。

尚未取得匹配样机／驱动和冻结策略，未验证结构 ABI、同步／pending／立即失败、短写、计时器／取消／关闭竞争、实际 EP0 及重复开关句柄。不以代码路径或编译替代上述行为证据。传输尚未组合 Desktop 生命周期与设备用例，握手／parser 清理／HID 独立未完成，4.3 父项保持未完成。
