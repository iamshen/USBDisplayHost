# GitHub 更新与打包实现记录

日期：2026-10-23；版本：0.1.1。关联 REQ-17／FR-17、T-32、TC-34～TC-38 和 OpenSpec 12.3～12.6。这里只记录源码、构建及静态检查，不代表上述运行验收通过。

## 实现

- 仓库 `iamshen/USBDisplayHost` 已按用户要求由 private 改为 public，GitHub CLI 读取确认 `visibility: PUBLIC`。
- Windows 适配器使用公开 GitHub Releases 和 `win-x64` 通道，无客户端凭据；手动检查、持久化 24 小时自动间隔、完整包流式下载与取消、缓存恢复、大小／SHA-256／包内产品及架构核对。
- 设置页提供当前版本、更新状态、字节进度、取消、确认重启和下载页入口。后台失败不弹窗，源码构建不误报最新版。
- 安装沿用未保存内容决策，待确认的操作及原生关闭失败阻止交接；关闭会话和发现探测后才启动等待正常退出的更新器。启动失败保留诊断并在所有者确认释放后允许新会话；普通退出不安装，更新重启跳过自动模式策略。
- Actions 主分支推送／手动触发构建，匹配版本标签触发草稿上传后发布；项目、SDK、NuGet 与 vpk 版本锁定，同版本不覆盖。

## 已执行

| 检查 | 结果 |
|---|---|
| 更新依赖锁文件 | Windows 新增 Velopack 直接依赖；Desktop 项目依赖图同步，正常构建继续锁定还原 |
| Desktop Release 编译 | 成功，0 警告、0 错误 |
| `./build.ps1 -Action Package` | 最终源码锁定还原、win-x64 自包含编译及 vpk 打包成功；没有启动程序 |
| 发布内容核对 | Setup、完整包、JSON／兼容索引、SHA256SUMS 均生成；索引大小及 SHA-256 与完整包一致 |
| 包身份核对 | nuspec 为 USBDisplayHost、0.1.1、win-x64、Windows x64；程序文件版本 0.1.1.0 |
| 工作流静态检查 | actionlint 1.7.12 通过；所用 checkout v7、setup-dotnet v6、upload-artifact v7 的官方 action.yml 均存在 |
| 文档与规格 | `git diff --check`、`openspec validate build-usb-display-host-v1 --strict --no-interactive` 通过 |

最终本地产物位于 `.artifacts/installers/win-x64/0.1.1/`：

| 文件 | 字节数 | SHA-256 |
|---|---:|---|
| USBDisplayHost-win-x64-Setup.exe | 79020434 | `0002624D85C8F471C2E3F72EF314FDF086E2C38DB94A808E6740876CE7BA3802` |
| USBDisplayHost-0.1.1-win-x64-full.nupkg | 71439762 | `5862E794483E7FAD5874677ADBA0EE892EBEBD432027AD519E3AB5E8395AE9AF` |

Setup Authenticode 状态为 `NotSigned`。这些哈希对应当前未提交工作区生成物；GitHub 从新提交构建时产物哈希可能不同。

## 尚未执行

本地实现阶段未编写或运行测试，未启动 WPF 应用、安装器或设备操作；没有真实 UI 截图、旧版到新版升级或安装失败恢复证据。本文的本地构建结果不代表云端发布成功；发布记录以 [GitHub Actions](https://github.com/iamshen/USBDisplayHost/actions/workflows/package.yml) 和 [v0.1.1 Release](https://github.com/iamshen/USBDisplayHost/releases/tag/v0.1.1) 为准。代码签名及真实升级验收保持未完成。

操作说明见 [GitHub 发布](../release/github-release.md)。0.1.0 需要先手动安装带更新功能的版本，后续才可使用应用内更新。
