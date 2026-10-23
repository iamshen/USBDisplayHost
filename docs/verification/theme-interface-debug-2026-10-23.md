# 主题模式接口发现修复

日期：2026-10-23。范围：同事调试记录分析、上位机源码修复与静态检查。追踪：REQ-01、REQ-02、REQ-09、REQ-11，FR-01、FR-02、FR-09、FR-10、FR-11，TC-03、TC-04、TC-19～TC-21。未修改固件或安装驱动。

## 现有证据

- 两份 `diagnostic-0.jsonl` 为重叠日志，第二份包含第一份全部记录并追加后续事件，不能据此推断同时运行两个应用。
- 2026-10-22 17:22:01 与 17:39:29 均已连接为 `Ready.SecondaryDisplay.On.Stopped`；两次切换分别在 24.0792 ms、14.9904 ms 记录 `ModeTargetPending`。代码在 HID 请求阶段主动返回该中间结果，之后才关闭旧会话并等待目标接口，因此这两条耗时不是整个切换的最终耗时。
- 53 秒录屏中，设备 PID 从 AD11 变成 ADA0；约第 16 秒出现“主题 Display WinUSB 接口未就绪或存在歧义”，错误编号 `4d5f6fdd02d643fa965758b13a4565b7`。该编号未进入两份 JSONL，因为原连接流程在进入协调器之前返回此错误。
- 静态发现配置只读取 `Device Parameters\DeviceInterfaceGUIDs`，漏掉 Windows 支持的单值 `DeviceInterfaceGUID`。[Microsoft WinUSB 安装文档](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/winusb-installation) 明确说明两种注册方式均有效。

当前开发机没有 VID A108 的 USB 注册项。附件未提供同事机器的 WinUSB 服务及 GUID 注册值，尚不能确认该机器是单值注册被漏读、未绑定 WinUSB、缺少 GUID，还是重复注册。

## 修复后的行为

- 读取 WinUSB 两种 GUID 注册项并合并去重；保留实际驱动服务、VID/PID、设备身份及 Display 描述符核验。
- 主题连接在打开 HID 之前检查 Display 候选，分别报告 `DisplayBindingMissing` 和 `DisplayBindingAmbiguous`，提供对应处理动作。
- 目标等待不再用“总接口数至少两个”推断 Display 可用，而是要求唯一的主题数据接口；超时重新核验发现接口不完整时返回具体未知结果，不先选择残缺设备并打开 HID。
- 模式发送之前发生的拒绝或收敛错误保留原始原因；HID 失败／取消不再统一覆盖为等待目标，也不会在明确取消后继续目标重连。
- 连接、模式切换及重连分别记录 `DeviceProtocol.Connect`、`DeviceProtocol.SwitchMode`、`DeviceProtocol.Reconnect` 最终结果；沿用页面错误编号，并记录匿名设备键、当前会话与状态。

## 验证记录

四个生产工程 Release 目标编译通过，0 警告、0 错误；差异检查与 OpenSpec 严格校验通过。实际命令：

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Repos/MCloud/Usb-Project/USBDisplayHost/.artifacts/build-validation/theme-interface-2026-10-23/
git diff --check
openspec validate build-usb-display-host-v1 --strict --no-interactive
```

遵循仓库 `AGENTS.md` 的测试授权限制，未编写或运行测试，未启动应用，未打开 USB 设备或发送命令。源码修复不代表同事机器的驱动绑定与实机切换已经通过。

后续在获准的同事设备上确认：主题 Display 实际绑定 WinUSB 且具有有效接口 GUID；同一物理设备可发现一个 V2 HID 和一个 Display；副屏切主题完成初始化与首帧；缺失接口时页面编号能对应最终协议日志。已有资料不能证明这些结果。
