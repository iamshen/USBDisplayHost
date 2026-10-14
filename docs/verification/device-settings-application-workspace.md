# 设备设置页应用入口代码基线

关联 REQ-06／REQ-08、FR-06／FR-08、D3、T-16／T-30，任务 8.2／7.6。仅有代码与目标 Release 编译证据。

App 将现有唯一协调器／会话生命周期和主机指标来源注入设备设置页；页头从协调器当前设备键呈现，不再固定显示未选择。原有方向、1～5fps、70～85 整数画质校验和离线草稿存储不变。亮度不属于 DeviceSettings，不新增猜测的 EP0／HID 控制或默认读回值。

同设备／代次的 Active owner、Ready／Theme／On、已验证能力且未暂停或故障时，主动作显示“应用到设备”；其他状态保持“保存草稿”。输入非法、草稿只读／未知保存、在途操作、待保存恢复候选或退出时不受理应用。有界快照 presenter 在 UI 呈现阶段刷新两个页面动作，不增加页级设备订阅或会话。

显式应用冻结当次方向／帧率／质量与修订，并绑定实际所选设备键／代次；通过 DeviceSessionLifetime.ApplySettingsAsync 交给原管线，使用同一指标快照。Core 保留当前主题，按候选首帧边界提交，不把草稿字段直接写入已确认活动配置。应用过程中字段仍可编辑，结算不 Restore 表单，不修改 saved／savedRevision／LastSaveSucceeded，不把恢复配置已保存冒充独立草稿已保存。

主题页与设备设置页共用 ThemeApplicationResultFormatter：标明原目标设备和冻结修订，分别显示发送、活动提交、恢复配置保存与“呈现反馈未提供”。失败输入保留，已受理意外异常显示结果未确认和错误编号，不建议直接重发。提供取消应用，页面停止取消并等待实际应用／重试任务；UI 取消不构成原生收敛证据。

“重试保存”绑定原 SaveRetryId，并核对当前 PendingSave 标识与忙碌状态，只调用原会话本地 RetrySaveAsync。成功更新保存结果，保留原发送／活动结果和新编辑，不重新发送或控制设备。两页共同受同一 owner 的忙碌和待保存门禁限制，不能通过另一页覆盖尚未处理的保存候选。

执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`，四工程 0 警告、0 错误。未创建或运行测试，未启动 WPF 窗口、实际管线、设备请求或保存操作。按仅运行目标 Release 构建限制，视觉 QA／截图未执行；App 尚未创建真实控制／parser 与管线，运行仍走离线草稿，8.2／7.6 父项保持未完成。
