# USB Display 离线安装说明

当前版本：0.1.1；目标架构：Windows x64。当前安装包未签名，安装、卸载及真实设备兼容性尚未验证，不作为正式发布验收通过。

## 安装与使用

交付文件为 `USBDisplayHost-win-x64-Setup.exe`。安装包包含 .NET 10 与 WPF 运行时，不需要在目标电脑安装开发 SDK 或在线下载 .NET。目标 Windows build 和驱动兼容表仍以实际验收为准。

安装前保存编辑内容，并从托盘菜单明确退出正在运行的 USB Display；关闭窗口默认只是收至托盘。再次安装或手动更换版本前也应先退出，避免安装器结束正在使用设备的进程。

运行安装包会安装到当前用户的 `%LocalAppData%\USBDisplayHost`，创建桌面和开始菜单快捷方式，并在安装完成后启动应用。应用安装与日常使用采用普通用户权限。不要将当前未签名包视为已验证发布者的正式版本，也不要为运行它关闭系统安全保护。

## 驱动

安装包仅安装上位机，不携带或安装驱动，不刷写设备固件。

- 主题模式需要正确的 WinUSB 接口绑定；能否由 Windows 自动配置取决于固件描述符及设备条件。
- HID 控制使用系统 HID 驱动。
- 副屏桌面输出需要厂商提供、匹配设备且具有分发授权的签名显示驱动。

驱动缺失时先按厂商说明安装匹配驱动；需要权限时仅驱动安装环节提权。不要批量替换复合设备的驱动，不要求普通用户使用 Zadig。驱动安装失败和真实屏幕输出尚未验收。

## 数据、卸载与修复

配置、素材和日志保存在 `%LocalAppData%\UsbDisplayHost.Data`，不在程序替换目录中。应用日志位于该目录的 `diagnostics\logs`。

通过 Windows“已安装的应用”卸载 USB Display。卸载回调只删除与当前程序路径完全匹配的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\USBDisplayHost` 自启项，不改其他路径的同名项；用户数据默认保留，共享驱动不删除。需要完全移除个人数据时，先备份，再自行删除上述独立数据目录。实际卸载行为待验证。

安装版可在“应用设置 → 软件更新”从 GitHub 检查并下载新版，用户确认后关闭设备会话并重启安装；应用启动不会自动应用缓存包。0.1.0 没有在线更新入口，第一次需手动安装新 Setup。操作及发布说明见 [GitHub 更新](github-release.md)。需要手动更换版本时，先备份独立数据目录、确认配置格式兼容并退出程序，再使用可信的完整安装包。跨版本修复和回退尚未验证，不保证旧程序能读取新格式配置。

## 开发侧生成

在仓库根目录执行：

```powershell
./build.ps1 -Action Package
```

该命令锁定还原并编译 win-x64 自包含目录，恢复仓库固定版本的 `vpk`，再生成离线安装包。开发机首次还原 SDK 依赖或打包工具需要网络；“离线”指目标机器安装应用无需下载运行时。构建过程不运行应用、安装器、测试或驱动操作。

| 项目 | 值 |
|---|---|
| SDK / vpk | Velopack 1.2.161，SDK 在集中版本和锁文件中固定，工具在 `.config/dotnet-tools.json` 固定 |
| 包身份 / 显示名称 | `USBDisplayHost` / `USB Display` |
| 版本来源 | `Directory.Build.props` 的 `Version`；应用程序集与安装包共用 |
| 包通道 | `win-x64`，公开源为 `iamshen/USBDisplayHost` 的 GitHub Releases |
| 发布目录 | `.artifacts/publish/win-x64/`；每次 Publish / Package 清理后生成 |
| 安装包目录 | `.artifacts/installers/win-x64/<Version>/` |
| 对外交付入口 | `USBDisplayHost-win-x64-Setup.exe`，本轮仅作未签名内部产物 |
| 工具附带产物 | 完整 `.nupkg`、更新索引和 `SHA256SUMS.txt`；标签触发 Actions 上传到 Release |

版本使用三段数字，如 `0.1.1`；后续递增时同步更新本文；版本或依赖变化时重新生成受影响锁文件，项目引用版本也需要同步。包身份保持稳定，不因版本变化重新命名。调整使用显式解除锁定的维护命令，正常构建继续锁定还原：

```powershell
dotnet restore src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj --force-evaluate -p:RestoreLockedMode=false
```

打包使用 `--noPortable`、`--delta None`，不生成便携包或增量包；使用 `--skipVeloAppCheck` 禁止打包器启动主程序探测入口。因此打包成功只证明产物生成，不证明安装生命周期已运行验证。包内含 `INSTALLATION.zh-CN.md` 与 `licenses/`。

当前没有签名参数或证书。GitHub 发布和应用内更新已接入源码；正式发布验收仍须完成应用和安装辅助程序的签名验签，以及获授权的安装、卸载、跨版本和设备验收。

参考：[Velopack 安装器](https://docs.velopack.io/packaging/installer)、[Windows 安装目录](https://docs.velopack.io/packaging/operating-systems/windows)、[C# 生命周期](https://docs.velopack.io/getting-started/csharp)。实际行为以锁定的 1.2.161 源码和后续运行证据为准。
