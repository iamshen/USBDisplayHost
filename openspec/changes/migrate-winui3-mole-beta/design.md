# WinUI 3 迁移设计

## Context

候选 worktree 使用 `feature/winui3-beta-candidate`，基于 `79da041`。工作区已集成五页、隔离打包和设备断开后的本地交接。历史审核与本轮验证见 [任务](tasks.md)。

## Goals / Non-Goals

- 交付同一工作区内可构建、可验收的 WinUI Beta，保持用户已有设备工作与数据可用。
- 软件验收以默认窗口、现有缩放及已授权的无设备路径进行。公开 Release、正式版切换和真实设备行为分别取得用户确认。

## Decisions

1. **窗口和业务**。使用 .NET 10.0.401、Windows App SDK 2.6.5、win-x64 非 MSIX 自包含。WinUI 负责窗口、调度、对话框、图像呈现和文件选择。Core 保持平台无关，两个入口共享 ViewModel 以及会话和退出保存规则。正式发布入口继续采用 WPF。
2. **视觉**。采用 Mole Windows 的导航层级、状态聚焦与平行列表，保留 USB Display 品牌。当前 WinUI Beta 的固定顶部横向导航沿用 Figma 改版规格。语义资源覆盖两套应用配色和高对比，诊断、错误及反馈留在相关位置。实际窗口作为设计验收依据。[Mole Windows](https://mole.fit/windows)
3. **内容**。共享 WPF 专用 STA 离屏 Rendering 继续负责模板、1920×1080、旋转和 JPEG。WinUI 使用编码帧呈现已核验会话及修订的预览。Beta 包同时携带 .NET Desktop Runtime 与 Windows App SDK。替换离屏引擎前先对照布局、编码和实物。
4. **交付**。Beta 使用 `X.Y.Z-beta.N` 版本，当前候选为 `0.2.0-beta.4`。Velopack 身份 `USBDisplayHost.Beta`，更新通道 `win-x64-beta`，数据根 `%LocalAppData%\UsbDisplayHost.Beta.Data`。`--ui-only` 使用临时数据根和独立实例标识。发布目标显式携带包含 App、主窗口及五页 XBF 的 PRI。
5. **实施方式**。按 [任务](tasks.md) 在本地推进，统一编译、运行和评审候选。保留 #8–#15 与 PR #16 作为需求和既有审核的追踪入口。一轮候选成熟后再决定汇总的远端评审和合并安排。

## Risks / Trade-offs

- WinUI 安装包包含两套桌面运行依赖。记录大小、冷启动和空闲占用的对照环境，再评价性能。
- 旧正式客户端查询最近 10 个 GitHub Release 后才筛选稳定版。Beta 在线更新由打包参数指定独立 GitHub 仓库，禁止使用正式仓库；无配置的本地包使用手动安装。[Velopack 查询实现](https://github.com/velopack/velopack/blob/1.2.161/src/lib-csharp/Sources/GithubSource.cs)
- 接口独占补丁有 Win32 普通文件验证，USB 驱动、实际枚举和重连需实物验收。

## Migration Plan

1. 修复设备页本地交接入口并接入 Beta 专属 USB 占用处理。
2. 完成锁定构建和无设备五页操作、托盘、单实例及默认窗口实拍。
3. 用独立数据副本完成净机安装、卸载、与正式版并存和 Beta 升级验证，再确认 Beta 发布。正式版安装和数据仍可作为恢复基线。
