# 离线安装包实施与静态核对

日期：2026-10-22。关联 REQ-14／FR-14、REQ-17／FR-17，T-07、T-25、T-31、T-32，OpenSpec 2.3.3、2.5.1、12.1.1、12.2.1。TC-24／TC-25／TC-33～TC-38 运行验收均未执行。

本记录描述离线打包实施时的产物；后续与 GitHub 界面修改合并后仅重新执行生产构建，未重新打包。以下安装包与哈希不能代表合并后的界面版本。

## 实现范围

- Velopack SDK 与仓库本地 vpk 工具固定为 1.2.161，更新集中版本及四工程锁文件。SDK 只由 Desktop 引用，Core 不引入平台或安装 SDK 依赖。
- `Directory.Build.props` 统一产品版本 0.1.0 及 win-x64 还原目标，使常规 Build 与带 RID 的自包含 Publish 都可锁定还原。
- STA Main 在单实例、日志服务和 WPF Application 初始化前处理安装 hooks，显式 `SetAutoApplyOnStartup(false)`；未实现检查、下载或在线更新交接。
- 卸载回调仅清理与当前程序命令完全一致的 HKCU 自启项；冲突项、独立数据目录及驱动保持原样。现有自启在安装环境指向 `current\UsbDisplayHost.Desktop.exe`；其路径不含版本号，实际登录／覆盖安装／卸载行为仍待验证。
- `build.ps1 -Action Package` 先清理生成目录并自包含发布，再生成当前用户 Setup；每个 dotnet 步骤失败立即停止。打包使用 `--skipVeloAppCheck`，不执行主程序探测；不生成便携或增量包，不上传本地包及索引。
- 随包包含中文安装／驱动说明、现有依赖许可、Velopack 及 .NET 运行时原始声明。

## 执行结果

| 命令或检查 | 实际结果 |
|---|---|
| `dotnet restore src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj --force-evaluate -p:RestoreLockedMode=false` | 依赖维护时重新生成锁文件，成功；常规入口仍启用锁定模式 |
| `./build.ps1 -Action Restore` | 四工程锁定还原成功 |
| `./build.ps1 -Action Build` | Release 成功，0 警告、0 错误 |
| `./build.ps1 -Action Package` | 自包含发布、固定工具恢复及 Setup 打包成功；日志明确跳过应用入口执行检查 |
| `openspec validate build-usb-display-host-v1 --strict --no-interactive` | 通过 |
| `git diff --check` | 通过 |
| `ZipFile.OpenRead` 读取完整包及 XML 清单 | 包身份、版本、架构和必需内容核对完成，没有执行包内程序 |
| `FileVersionInfo` 读取发布 EXE | FileVersion 为 `0.1.0.0`，ProductVersion 主版本为 `0.1.0` |
| `Get-AuthenticodeSignature` | Setup、Desktop EXE、包内 Squirrel.exe 均为 `NotSigned` |
| `Get-FileHash -Algorithm SHA256` | 结果如下，仅表示文件完整性，不是发布者签名 |

自包含发布最初出现 NU1004：引用工程未声明 win-x64，锁文件与带 RID 发布不一致。已通过公共 `RuntimeIdentifiers` 和四份锁文件修正，随后普通 Build 与 Package 均成功。相关注意事项已记录到根目录 `HANDOFF.MD`。

打包器提示未提供签名参数，未对 408 个候选文件及 Setup 进行签名。这是当前未签名内部产物的明确边界，不能视为正式签名发布成功。

## 产物

目录：`.artifacts/installers/win-x64/0.1.0/`；由 `.gitignore` 排除，不进入版本控制。

| 文件 | 字节数 | SHA-256 |
|---|---:|---|
| `USBDisplayHost-win-x64-Setup.exe` | 78,996,548 | `FFEA9A24B99D11BAFFD73C6E3FFC5D480A44A27531302E4B11778AB657E727E1` |
| `USBDisplayHost-0.1.0-win-x64-full.nupkg` | 71,415,876 | `44A5EDA09FDAF9692F81B09FB7A26AD0D3523DB672591D9D7256DE89D8A831D4` |

同时生成 `assets.win-x64.json`、`RELEASES-win-x64`、`releases.win-x64.json`；它们只保留在本地构建目录，未配置为更新源。

完整包清单中身份为 `USBDisplayHost`，显示名称 `USB Display`，版本 `0.1.0`，通道与 RID 均为 `win-x64`，入口 `UsbDisplayHost.Desktop.exe`。已静态查看以下内容：

- `coreclr.dll`、`hostfxr.dll`、`PresentationFramework.dll` 和 `UsbDisplayHost.Desktop.runtimeconfig.json`；后者列明内含 Microsoft.NETCore.App／Microsoft.WindowsDesktop.App 10.0.12。
- `Velopack.dll`、`Squirrel.exe`、`USB Display_ExecutionStub.exe`。
- `Assets/Branding/app.ico`、`INSTALLATION.zh-CN.md`。
- `licenses/` 下的 WPF UI、MVVM Toolkit、Serilog、Velopack、.NET Runtime 与 Windows Desktop 声明。

包内安装辅助程序以 Squirrel.exe 命名，安装后的管理入口由 Velopack 布局使用 Update.exe；命名属于固定工具行为，不表示在线更新服务已接入。

## 尚未执行

上述打包阶段没有编写或执行测试，没有启动应用、安装包或安装 hooks，没有安装／卸载、修改实际自启注册表、访问设备或安装驱动。源码提交记录以 Git 历史为准。

干净机离线安装、普通权限运行、托盘退出、覆盖安装、卸载数据保留、自启项清理、签名链与真实硬件行为均无运行证据。自动更新服务、实际 HTTPS 源、签名流水线、配置迁移和跨版本修复留待后续实施。2.5、12.1、12.2 及 13.x 父项仍未完成。
