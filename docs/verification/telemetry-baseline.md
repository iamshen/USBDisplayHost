# 主机指标实施状态

日期：2026-10-08。关联 REQ-05／FR-05、T-15、TC-10、OpenSpec 6.4。

## 实现与数据口径

WindowsHostMetricProvider 通过基础系统 API 获取 PC CPU／内存，不引入管理员权限、温度驱动或进程性能计数器包。适配器同步段互斥；Core MetricSamplingService 在一个后台循环按 1Hz 顺序调用，不重叠，不等待 UI。快照不可变，最多保留最新数组。

单组 CPU 由两次 GetSystemTimes 的计数差计算，Kernel 包含 Idle，分母是 Kernel＋User 差值。首次读取、计数回退、无增量及系统 API 失败均不可用。GetSystemTimes 在多处理器组机器只覆盖调用线程所属组；参见 [GetSystemTimes 官方说明](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)。多组机器改用一次创建的 PDH `\Processor Information(_Total)\% Processor Time` 查询，使用语言中立的 [PdhAddEnglishCounterW](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw)，不改变线程亲和性、不退回单组计数。Processor Information 用于跨组处理器的依据见 [微软 OpenJDK 多组修复说明](https://devblogs.microsoft.com/java/java-openjdk-july-2026-patch-security-update/)。本项目选择其全机 `_Total`，实际 Windows／多组支持仍待验证。

PDH 首次收集只建立基线，至少两次成功收集之后调用 [PdhGetFormattedCounterValue](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue)，检查函数返回、CStatus、有限值及 0～100 范围。收集失败、处理器组数量变化或跨单组／多组路径切换后重新建立基线；缺失计数器／权限不足不显示零、不提权、不反复创建查询。普通错误只在持续故障首次写入诊断，仍按原 1Hz 串行循环采集内存。

IMetricProvider 增加异步释放边界；MetricSamplingService 的停止任务共享，先取消并等待采样循环，后关闭提供器，最后释放取消源。Windows 提供器和采样共用互斥门，退出时只调用一次 [PdhCloseQuery](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhclosequery)。查询在原生创建前进入最多八个所有者的强引用集合，成功关闭才移除；关闭返回失败或普通异常保留查询、缓存该结果，不重放关闭。该失败进入原工作区关闭错误边界，不能当作日志已安全停止或资源已全部释放。

内存为系统物理内存占用百分比 `(TotalPhysical - AvailablePhysical) / TotalPhysical`，不是应用工作集或提交内存。初始化结构长度，失败及异常边界返回不可用。参见 [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex) 和 [MEMORYSTATUSEX 字段](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)。原生调用失败在每次持续故障首次记录，避免 1Hz 重复写相同故障。

样本包含 Host 来源、百分比单位、UTC 采样时间、单调时间戳和可用状态。采样服务暂时失败保留上次样本；读取时通过注入 TimeProvider 判断超过 3 秒过期，不刷新旧样本时间。接口显式不可用结果不伪装成正常零值。设备 FPS／温度和 PC 温度／风扇没有输入，仍不可用。

Desktop 在生成仪表盘草稿时读取快照，指标采集不依赖主题页可见性；隐藏页面暂停预览，采集保持一个循环。退出停止采集并等待当前调用完成。基础 Win32 调用没有可取消句柄；当前不构成系统退出有界清理证据。

## 验证范围

只执行目标 Release 构建，四工程 0 警告、0 错误。未运行应用或采集实验，未创建或运行测试。实际 CPU／内存数值、1Hz 调度、失败恢复、3 秒停更、系统时间调整和长期资源行为尚未验证，6.4 父项保持未完成。
