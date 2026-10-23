# v0.1.2 发布与实际安装升级验证

日期：2026-10-23，以下时间为北京时间。关联 REQ-14／REQ-17、T-25／T-32／T-33、TC-34～TC-38 的成功路径。环境为当前 Windows 11 x64 电脑，保持默认窗口大小及原有系统缩放，无 VID A108 实物设备。

## 结果

v0.1.2 的 Actions 打包发布、应用内下载校验、确认退出、更新器安装及新版窗口启动均通过。GitHub Setup 的同版本覆盖修复安装也通过。真实设备会话关闭交由同事电脑验证；本记录不代表干净机、卸载、签名、故障注入或硬件验收完成。

修复提交：[9a7cef6](https://github.com/iamshen/USBDisplayHost/commit/9a7cef6b0f53efd9a4a9e4f68438306331610648)，标签 `v0.1.2` 指向同一提交。

| 发布检查 | 证据 |
|---|---|
| main 打包 | [Actions 39917239420](https://github.com/iamshen/USBDisplayHost/actions/runs/39917239420)，completed / success |
| 标签打包及发布 | [Actions 39917239701](https://github.com/iamshen/USBDisplayHost/actions/runs/39917239701)，completed / success |
| Release | [v0.1.2](https://github.com/iamshen/USBDisplayHost/releases/tag/v0.1.2)，15:23:15 发布，公开 Latest，非预发布，6 个附件齐全 |
| 匿名访问 | 未使用账号令牌获取 Latest、Setup、更新索引及 SHA256SUMS；应用使用公开 GitHub 源 |
| 本地构建 | 从本次暂存树导出源码，强制锁定还原及 `build.ps1 -Action Package` 成功，没有混入工作区其他 UI 改动 |

## 根因与测试基线

原正式 v0.1.1 的更新进度条默认双向绑定只读 `Progress`，在 `MainWindow.Show` 触发 `PropertyPathWorker.CheckReadOnly`，导致窗口打不开。v0.1.2 将该绑定显式改为 `Mode=OneWay`。

原正式 v0.1.1 无法操作在线更新，因此本次在线验证使用从 v0.1.1 标签源码导出、**仅补上同一行绑定修复**的本地 0.1.1 安装基线。它没有上传 GitHub；下载源、更新服务及退出逻辑未修改。目标是 Actions 发布的原始 v0.1.2 完整包，不是本地重打包目标。

已安装原正式 v0.1.1 的用户应退出故障实例后手动运行 v0.1.2 Setup，不能承诺从原故障界面直接升级。

## 应用内升级

1. 15:23:07，本地 0.1.1 基线安装并正常启动，进程 30560。打开应用设置手动检查，发现 0.1.2。
2. 点击“下载更新”，15:24:58 进入 `ReadyToInstall`。待安装记录、磁盘文件、公开索引、Release digest 和 SHA256SUMS 的大小及 SHA-256 一致。
3. 点击“重启并安装”并确认。15:25:38 记录 `AppUpdate.Install / Installing`，原进程退出，更新器 69164 使用 `--waitPid 30560` 及 `--updated-to=0.1.2`。
4. 更新器记录 `Package version 0.1.2 applied successfully`，15:25:39 启动新进程 11748，命令行携带 `--updated-to=0.1.2`。
5. 新窗口可操作，应用设置显示版本 0.1.2 和“已更新至 0.1.2”。待安装记录被清除，配置文件哈希与升级前相同，应用日志无新增 Error。

更新器日志中存在等待旧 PID 时的“拒绝访问”警告。同期进程检查已无原应用；随后更新器在安装目录枚举到的唯一进程是自身，并跳过终止自身。本次未发生强制终止旧应用，不能将该警告解释为活跃硬件会话已通过验证。

| 实际窗口 | 截图 |
|---|---|
| 发现新版 | [01-update-available.png](upgrade-v0.1.2-2026-10-23/01-update-available.png) |
| 下载校验完成 | [02-ready-to-install.png](upgrade-v0.1.2-2026-10-23/02-ready-to-install.png) |
| 安装确认 | [03-install-confirmation.png](upgrade-v0.1.2-2026-10-23/03-install-confirmation.png) |
| 重启后确认成功 | [04-updated-to-0.1.2.png](upgrade-v0.1.2-2026-10-23/04-updated-to-0.1.2.png) |

## 正式 Setup 安装

从公开 Release 下载 Setup 并核对 SHA-256。正常退出升级后的应用，再运行正式 Setup，选择同版本“修复”完成覆盖安装。15:28:11 安装器记录 `Installation completed successfully`，随后启动进程 43980，不带升级参数。

主窗口正常打开，软件更新页可操作；15:29:17 手动检查返回 `UpToDate`，页面显示“未发现更高的已发布版本”。安装程序集 ProductVersion 为 `0.1.2+9a7cef6b0f53efd9a4a9e4f68438306331610648`。截图：[05-setup-launched.png](upgrade-v0.1.2-2026-10-23/05-setup-launched.png)。

为正常退出进行安装验证，临时将“关闭主窗口时”改为退出应用并保存；确认进程退出后恢复原配置备份。最终“收至托盘”等偏好与测试前逐字节一致。保留正式 v0.1.2 在本机运行。

## 产物与记录

| GitHub 正式附件 | 字节数 | SHA-256 |
|---|---:|---|
| USBDisplayHost-win-x64-Setup.exe | 79019490 | `5f10c39fba1a4dd787b2465dd230b327daaaf229bf63b468119c7a58f91a54f9` |
| USBDisplayHost-0.1.2-win-x64-full.nupkg | 71438818 | `eb9c0967f1d96da675eae88d635cadf0ac7a7c9b7041e9525edc5cf4dce2d673` |

本次应用事件见 [application-events.jsonl](upgrade-v0.1.2-2026-10-23/application-events.jsonl)，更新器和 Setup 关键事件见 [installer-events.log](upgrade-v0.1.2-2026-10-23/installer-events.log)。归档只包含本次相关事件，用户目录已替换；完整本地日志、配置备份及下载产物保留在被 Git 忽略的 `.artifacts/release-v0.1.2/`。

## 同事电脑待验证

连接并明确选择目标 USB 屏，在活动推流会话中执行更新确认，核实停止推流及设备句柄释放先于安装交接；重启后确认窗口可用、设备当前模式未被自动切换，并能重新检测连接。保留更新前后进程、诊断日志和设备状态。当前本机的无设备结果不替代这一步。
