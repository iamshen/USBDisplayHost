# AD102 V2 HID 编解码实施记录

日期：2026-10-09。关联 REQ-11～REQ-13／FR-11～FR-13、T-22、TC-22、任务 11.2。源码依据：固件提交 `b887bc192d1ef45ebe40662aecfbcddfbb6e516d` 的 `vendor/usb_camera/usb_v2_hid.c` 和 `usb_display.c`，详见 `docs/contracts/firmware-source-baseline.md`。

Core `Ad102V2HidCodec` 仅解析已核对的 V2.0 字节布局，不引用 WPF 或原生句柄。由完整 64 字节 Feature ID3、协议 2.0、合法模式／维护字段及零保留区建立实例；Input ID1 同样要求完整长度、当前布局和能力声明匹配。长度、ID、字段、结果映射或 token 关联不一致返回明确 Failure，不修改快照。输入错误码按源码的最近 HID 结果映射核对；未知命令的 Unsupported 不解释为成功。

保留 SCREEN、THEME、Maintenance、Switching 四种固件原始模式，尚未映射为设备 Ready。原始背光字节不擅自标为 0～100 百分比；display enabled／AUTORLX／切换标志为声明状态，不推断真实亮屏或推流。温度、FPS、帧计数的占位零没有导入业务指标；最近 Display 命令及 16 位计数作为原始字段保留，不作为请求 token、完整帧或呈现回执。

模式请求编码限制为 ID2、命令 31、非零 token、完整 64 字节缓冲；保留区清零，Theme 编码值 0、SecondaryDisplay 值 1，拒绝未知目标或错误长度且不写入缓冲。编码器没有整机软复位入口，也不发送任何报告。它不分配 token、不提供去重或自动重试；后续实际通道须绑定同设备／代次／操作，并处理 8 位 token 生命周期、回绕和旧句柄应答失效。

解析出的 Accepted 仅是受理结果。Feature／Input 当前模式不能单独证明 Windows 目标显示路径或 WinUSB 管线可操作；切换完成必须由实际重枚举／目标状态核验。创建 codec 不代表 collection、驱动或当前固件已经核实，Windows 适配器仍须检查 usage page FF00、usage 1、真实 caps 长度及身份。

运行中的旧验收窗口占用默认输出，执行独立目录目标构建：

`dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/hid-codec/`

四工程成功，0 警告、0 错误。未创建／运行测试，未调用 HID 或发送设备命令；实际采样、控制序列与模式切换尚未接入 App，11.2 父项保持未完成。
