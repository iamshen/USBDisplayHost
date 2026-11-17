# WinUI 3 与 Mole 视觉迁移

## Why

WinUI 3 迁移涉及五页、窗口服务、共享渲染和更新分发，改动依赖同一可运行基线。用一个本地提案推进代码、验证和 Beta 交付，已有 GitHub Issue 继续提供需求与历史追踪。

## What Changes

- 以 Mole Windows 官方展示为视觉参考，交付 USB Display 的设备、主题、设备设置、诊断和应用设置五页。
- 延续 USB 协议、会话恢复、主题保存、日志导出、托盘、单实例和退出决策。复用既有 Core、Windows 适配及离屏 Rendering。
- 交付可与正式版并存的 WinUI Beta 候选，分开安装身份、更新通道和用户数据。现行正式安装入口仍为 WPF。
- 用本地任务清单记录实现和证据，按可运行的软件路径验收。产品视觉、干净环境安装、真实设备及公开发布保留各自的确认步骤。

## Capabilities

### New Capabilities

- `winui-beta-workspace`：WinUI 设备工作区五页及共享窗口生命周期。
- `winui-beta-delivery`：隔离的 Beta 安装、更新、数据和验证流程。

### Modified Capabilities

主规格目录当前为空。已有 `build-usb-display-host-v1` 变更继续定义正式版业务、设备契约和追踪编号，本提案衔接其要求。

## Impact

影响 WinUI、Desktop 的共享 ViewModel 和生命周期、Windows 更新与设备适配、离屏渲染、构建和发布说明。设计资料沿用 Mole Windows 官网与版本记录。范围及历史见 [总任务 #8](https://github.com/iamshen/USBDisplayHost/issues/8) 和 [样板 PR #16](https://github.com/iamshen/USBDisplayHost/pull/16)。
