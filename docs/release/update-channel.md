# 安装与更新发布输入记录

状态：已完成 v0.1.2 Actions 发布、本机无设备在线升级后启动及 Setup 覆盖安装。关联任务 2.5、12.x、13.x。记录日期：2026-10-23。签名、更新故障场景及实物会话验证未完成；签名输入提供方为产品／发布／主机，责任人和交付日期尚未提供。

| 输入 | 当前状态 | 影响 |
|---|---|---|
| Velopack SDK 与打包工具的实际锁定版本及许可 | SDK／vpk 均为 1.2.161，MIT，已保存许可及工具第三方声明 | 已实现 GitHub 更新适配器及安装生命周期 |
| packId、产品版本规则、通道标识 | `USBDisplayHost`；`Directory.Build.props` 中的三段版本，当前 `0.1.2`；通道 `win-x64` | 固定身份，稳定版只允许向更高版本升级 |
| HTTPS 源和独立隔离验证源 | 用户指定公开的 `https://github.com/iamshen/USBDisplayHost` Releases；隔离验收源未提供 | 客户端无需令牌；当前发布不代表签名及正式 V1 验收 |
| 签名主体、证书链、时间戳策略与安全签名链路 | 未提供 | 应用、Setup、Update 和实际包验签 |
| 架构、目标 Windows 范围、当前用户安装位置 | win-x64；默认 `%LocalAppData%\USBDisplayHost`；实际支持的 Windows build 未验收 | 仅生成 Windows x64 安装包，不宣称兼容性验收完成 |
| schema、旧版兼容范围、迁移与回退规则 | 未冻结 | 配置备份及更新后恢复 |
| 同 packId 的两个实际签名版本与哈希 | 未提供 | 安装、升级、失败修复依据 |

现有四生产工程和 Velopack 已锁定 SDK／依赖；见 `dependencies.md`。`build.ps1 -Action Package` 生成包含 .NET／WPF 运行时的未签名离线 Setup，使用方法见 `offline-installation.md`。入口在单实例与设备服务之前处理安装 hooks，并显式关闭启动自动应用更新；卸载只清理属于当前应用路径的自启项，保留独立数据和驱动。`IAppUpdateService` 在 Windows 层由 GitHub 适配器实现，设置页提供检查、下载取消、校验和安装确认，应用生命周期负责保存决策与设备关闭。

打包器生成完整包、索引与 SHA-256 清单。main 推送保存 Actions 产物，版本标签触发 Release 草稿创建，所有附件上传后才转公开，客户端使用 HTTPS 稳定源。正式签名交付仍需证书公有验证信息，私钥、令牌和证书密码不进入仓库。当前未签名构建不冒充正式签名产物；正式 V1 发布验收和隔离源仍需补齐。

打包显式跳过会启动主程序的入口探测。已另行获得授权并执行本机安装及在线升级成功路径，结果见 `../verification/upgrade-v0.1.2-2026-10-23.md`；无设备结果不替代硬件会话验证。2.5、12.x、13.x 的签名、故障场景和正式发布父项保持未完成。当前发布操作见 `github-release.md`，初始实现与构建记录见 `../verification/github-update-2026-10-23.md`。
