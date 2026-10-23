# GitHub 打包与应用更新

更新源：[iamshen/USBDisplayHost Releases](https://github.com/iamshen/USBDisplayHost/releases)。仓库已公开，客户端无需账号或令牌。当前版本为 0.1.2，使用 Velopack 1.2.161、Windows x64、`win-x64` 通道。

## 用户操作

在“应用设置 → 软件更新”检查版本，发现更高的稳定版本后点击“下载更新”。下载显示实际字节数，可以取消；校验通过后点击“重启并安装”，确认后处理未保存编辑、停止设备会话，再交接更新器。

自动检查使用已保存的偏好，默认开启；自动尝试的时间落盘，失败也至少间隔 24 小时。只检查，不自动下载。已下载目标不会被后台检查替换。选择“稍后”、普通退出和再次启动都不会自行安装；待安装包会重新校验后恢复。

更新过程跳过退出切副屏和启动切主题策略，不主动改变设备模式、电源或固件。交接前关闭设备及发现探测句柄，交接失败保留窗口和诊断，可重新检测设备。只有重新启动版本符合目标、配置可读且窗口与托盘初始化成功，才显示“已更新”。

0.1.0 尚无在线更新入口，第一次需要退出旧程序后安装新的 Setup。0.1.1 存在更新进度条绑定引起的启动故障，无法从该版本正常打开更新入口；请退出故障实例后手动安装 0.1.2 或更高版本的 Setup。0.1.2 已修正该绑定。源码构建／便携目录不支持应用内更新，页面提供 GitHub 下载入口。

## 提交与发布

Git 提交由维护者执行。工作流 `.github/workflows/package.yml`：

| 触发 | 结果 |
|---|---|
| 推送到 `main` | 锁定还原、Release 自包含打包，在 Actions 保存产物 14 天 |
| Actions 手动运行 | 同样生成产物；选择版本标签运行时也执行发布 |
| 推送 `vX.Y.Z` 标签 | 标签必须与 `Directory.Build.props` 的 `Version` 完全一致，打包后发布 Release |

发布新版本时，先递增 `Directory.Build.props`，同步版本说明并重新评估包含项目引用版本的锁文件，再提交、推送并给同一提交打标签。例如：

```powershell
dotnet restore src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj --force-evaluate -p:RestoreLockedMode=false
dotnet restore src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj --force --locked-mode
# 将版本、锁文件及相关改动提交后发布
git push origin main
git tag v0.1.2
git push origin v0.1.2
```

工作流使用仓库提供的 `GITHUB_TOKEN` 和 `contents: write`，无需添加个人令牌；不执行测试、应用、安装器或设备操作。`global.json`、NuGet 锁文件与工具清单固定构建版本。

先创建 Release 草稿，全部附件上传成功后才发布为 Latest。上传失败保留草稿，不向客户端暴露不完整索引。已存在同名 Release 时停止，不覆盖旧版本；失败草稿可由维护者检查并删除后重跑。

附件包含 Setup、`USBDisplayHost-<Version>-win-x64-full.nupkg`、`releases.win-x64.json`、兼容索引和 `SHA256SUMS.txt`。终端用户手动安装选择 Setup；完整包和索引供应用内更新使用。不要只上传 Setup 或手工修改索引版本与哈希。

## 数据与当前交付边界

配置、素材和日志位于 `%LocalAppData%\UsbDisplayHost.Data`，更新替换程序目录时保留。检查时间和已下载包描述位于其 `updates` 子目录；实际包由 Velopack 管理。失败原因和错误编号记录在 `diagnostics\logs` 的 `AppUpdate.*` 阶段。

当前发布链路没有代码签名证书，产物为未签名构建，SHA-256 仅确认完整性，不证明发布者身份。正式 V1 仍需签名、Windows 兼容性及真实跨版本升级验收；没有声明自动回滚。修复时先备份独立数据目录、明确退出应用，再使用同仓库可信的完整 Setup；旧版读取新配置的兼容性未验证。

实现依据：[Velopack GitHub 源](https://docs.velopack.io/integrating/update-sources)、[更新生命周期](https://docs.velopack.io/integrating/overview)。SDK 行为已按仓库锁定版本源码核对。
