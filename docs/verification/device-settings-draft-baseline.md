# 设备设置草稿存储实施记录

关联：REQ-06／FR-06、REQ-08／FR-08、T-18、任务 7.1／7.5。日期：2026-10-08。

Core 增加不可变 DeviceSettingsDraft 与独立 IDeviceSettingsDraftStore，仅包含设备归属、修订及方向／帧率／画质。它不含亮度、电源或主题字段，不代表活动配置或恢复配置。

Windows FileProfileStore 实现此接口，文件位于用户数据目录 `device-settings-drafts/offline.json` 或 `device-settings-drafts/<设备键 SHA256>.json`。路径由所有者构造，不接受外部文件名；不写主题草稿、恢复配置或安装目录。与现有 JSON 存储共用 schemaVersion=2、原子替换、备份、损坏副本及未知高版本拒写；没有自动迁移旧文件。

负载要求明确的 device-settings 类型、设备键（无设备时必须明确为 null）、非负修订及设置对象。读取匹配所选归属，并通过 DeviceSettings.Create 验证 0／90／180／270°、1～5fps 和画质 70～85。损坏／备份／高版本结果与可用草稿同时返回，不把恢复失败静默当作空草稿。普通文件异常仍由存储边界记录错误编号并返回失败／未知。

执行 `./build.ps1 -Action Build`，目标 Desktop Release 及四生产工程通过，0 警告、0 错误。未实际读取／写入草稿、运行应用、测试或设备操作。

无设备设置页面、期间新编辑保留和退出决策已由 [设置页工作区](device-settings-workspace-baseline.md) 组合并编译；选定设备切换及设备设置应用事务仍待组合。原子中断、实际恢复及文件归属隔离没有运行证据，7.1／7.5 父项保持未完成。
