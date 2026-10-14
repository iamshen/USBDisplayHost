# 模式操作跨重枚举代次交接

日期：2026-10-12。关联 FR-12、T-22、TC-22，任务 11.3／11.4。源实现与目标编译证据。

Desktop PrepareModeTransition 通过当前实际控制所有者请求协调器 ArmModeTransition。该入口复用原控制命令门禁，冻结唯一原上下文、目标模式、电源意图及工作所有者，并强引用实际原 IControlChannel。重复相同交接返回同一 lease；不同通道、目标或操作不能覆盖。预备交接不证明命令已发送或设备已切换。

只有属于原选定接口、明确带路径的移除通知才可进入模式交接分支。协调器仍先撤销原上下文有效性；随后提升会话代次，保留原操作 ID／修订和工作，发布 Recovering，清除身份／接口集合／能力／实际模式／电源／亮度及活动配置。旧上下文无法完成新操作。一般断开、无法归属的通知、取消、退出或未预备模式操作沿用完整失效与取消流程；新目标再次失效也取消原操作，不放行旧应答。

ModeTransitionLease.CloseOriginalControlAsync 关闭绑定的实际原通道，并且只在 CloseAsync 返回 Success 后记录关闭确认；Failure／Unknown／普通异常保持未确认，不凭 ACK、观察超时或 DisposeAsync 声明关闭。AdmitModeTarget 还要求旧主题管线已经完成原生关闭及本地交接、没有待接管连接。随后核验当前操作、新代次、取消状态和同一物理键的完整新发现凭据，受理前后双检，保存目标接口集合。它仅返回新的 OperationContext，不发布 Ready。

原工作最终完成时，协调器识别原 lease 与新上下文的关联，要求原控制关闭确认、当前新发现凭据、实际身份属于新集合，以及实际模式／电源匹配冻结目标和意图。验证失败返回 Unknown；验证通过仍复用 DeviceObservation 的驱动／能力／实际状态门禁。旧代次结果不能单独通过完成核验。工作结算或一般失效后清除交接登记；未知资源仍由原工作／Desktop 通道所有者保留。

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/mode-generation-handoff/
```

四生产工程 0 警告、0 错误。Core.dll SHA256：`AAE7C05A7F62C1BD70B8C2EB72428BC0480B5A55823057C1840CA1AE144C52C1`。Core 保持无 WPF／原生句柄依赖。

本次未启动输出、调用设备、创建或运行测试。模式工作尚未组合预备、发送、原控制关闭与新目标受理；真实目标采集、ledger 完整写入证明／Feature／驱动／首帧核验及界面入口仍未接通。该协调器机制不能替代原 ledger 的实际写收据与目标证据核验，11.3／11.4 保持未完成。本记录补充 control-operation-admission-baseline 中尚缺代次交接机制的后续实现，不代表重枚举或竞态验收完成。
