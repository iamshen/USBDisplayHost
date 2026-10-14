# AD102 V2 模式控制记录

日期：2026-10-09。关联 REQ-11～REQ-13／FR-11～FR-13、T-22、TC-22，任务 11.3。

Core `Ad102V2ModeControlLedger` 绑定永久设备键与物理 HID 会话代次，最多持有一个不可变模式请求；操作号与内容修订共同核对。请求报告始终为 64 字节。token 从 1 到 255 只递增，取消前未发送、已确认业务拒绝也不回收 token；耗尽后拒绝，不回绕、不清零或自动重建记录。

BeginWrite 在调用实际适配器之前标记唯一发送；同请求不能再次 BeginWrite。RecordWrite 只接受原操作的实际完成收据，完整 64 字节、无超时／取消／原生错误且完成已知才能确认完整写入。未知完成仍保留请求，原 I/O 所有者可补交同操作最终收据；这不授权第二次发送，最终已知收据只能结算一次。记录不管理原生句柄／缓冲，收敛和关闭仍归实际适配器所有。

Input 先由同实例 V2 codec 核对，再匹配原模式命令 31 和 token。旧设备／代次／操作、其他命令或迟到 token 不改变当前请求。收到应答但写入未确认时返回 Unknown，不释放请求；Accepted 还核对原目标，保留请求供重枚举和实际目标可操作性核验。返回的 acknowledgement.Result 明确区分 Accepted、Busy、Invalid、Unsupported；OperationResult.Success 只表示应答关联已核实，不表示模式切换完成。已知完整写入且尚未确认受理时，明确拒绝可结束该请求并受理下一项。已确认 Accepted 后收到 Busy／Invalid／Unsupported 属于冲突，返回 Unknown 并保留请求，不撤销已有受理事实或证明未执行。

CancelBeforeWrite 只清理尚未 BeginWrite 的请求。Retire 仅使记录失效，保留待确认请求，不取消原生 I/O、释放缓冲或声明设备已切换。失效后仍允许原设备键／代次／操作号／修订的在途写补交最终实际收据，仅结算一次；Prepare、BeginWrite、ACK 与取消仍被拒绝，不把旧 I/O 收敛当成重新授权控制。真实通道必须将记录与同一物理会话所有者一起持有，不能每次 Feature 查询就新建以重置 token。后续新增的冻结电源意图与 VerifyTarget 目标核验入口见 [ad102-v2-mode-target-baseline.md](ad102-v2-mode-target-baseline.md)；尚未接入 Windows HID 和 App，关闭／重枚举后证据采集及归属接续仍待实际通道实现。

执行目标 Release 构建并使用独立输出目录 `.artifacts/build-validation/hid-ledger/`，四工程通过，0 警告、0 错误。未创建或运行测试、发送报告或运行 token 循环；编译不证明取消、回绕拒绝、迟到应答或原生完成竞态验收成功，11.3 父项保持未完成。

2026-10-09 补齐失效后最终收据与受理后冲突应答保留规则。匹配命令／token 的 Accepted 即使早于本机完成收据或返回了不一致的目标，也保留已观察到的受理事实并返回 Unknown；不能等到完整写入收据之后才记住受理，或用随后拒绝清除可能已执行的请求。目标 Release 使用独立输出目录 `.artifacts/build-validation/hid-ledger-retirement/`，四工程通过，0 警告、0 错误。未调用设备或运行行为验收；目标重枚举核验与父项完成证据仍缺失。
