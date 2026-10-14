# AD102 V2 固件源码核对

## 2026-10-12 协议开发基线

后续同日已完成 [上位机离线协议接入](../verification/protocol-offline-integration.md)：源码能力配置、实际接口发现、HID 状态探测、Display 恢复与纯电源控制、主题发送和模式重枚举均已组合。下文早期“尚未组合／需接入”记录由该实施记录补充，实机核验仍未执行。

上位机协议实现以当前固件提交及 `usb_display_ad102/上位机实现文档.md` 等配套资料为开发依据，源码一致性检查与目标 Release 构建作为软件开发证据。实机身份、驱动绑定、实际收发和稳定性联调另行记录，设备可调试后执行，不再将这些实机记录作为编写协议适配器的前置条件。源码与文档冲突时优先记录实际实现，并明确尚未实现的固件能力。

新增 Core `Ad102V2SourceProtocol`：按所选 Display function 实际接口号构建只读 status／EDID 查询，status 长度 1，ready 掩码为 INIT_COMPLETE 0x08 与 READY 0x10；EDID 基块 128 字节，首 DTD 偏移 54；XRGB888 值 2。另提供只读背光查询 0xC1／04。依据 `include/usb/gadget_display.h`、`drivers/usb/gadget/display/f_display.c` 与 `vendor/usb_camera/usb_display.c`，关联提交保持 `b887bc192d1ef45ebe40662aecfbcddfbb6e516d`。配置构造不发 I/O、不固定接口号、不授予实际驱动核验或当前设备兼容资格。

验证：`dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/ad102-source-protocol/`，四生产工程 0 警告、0 错误。未启动输出、调用设备或创建／运行测试。

日期：2026-10-09。关联 1.2～1.5、4.x、8.1、11.x。只读仓库：`E:/Source/Repos/MCloud/USB_DISPLAY_AD102`，分支 master，提交 `b887bc192d1ef45ebe40662aecfbcddfbb6e516d`。未改动、编译、烧录或运行固件；下列记录是源码事实，不是当前样机兼容证明。

## 目标与身份

主机目标调整为 1920×1080，预览比例 16:9。依据当前固件配置、显示几何与项目方确认；旧参考资料中的 1080×2400 保留为历史输入，不继续作为当前验收目标。旋转只改变逻辑内容，四个方向最终输出仍为同一物理 1920×1080。

`configs/ad102n_usb_v2_nor_defconfig` 启用 DISPLAY_V2、AD102N、LT8911B EDP 显示与 JPEG 解码。`vendor/usb_camera/usb_display.c` 的 V2 身份使用 VID A108，SCREEN PID AD11、THEME PID ADA0；`drivers/usb/gadget/display_v2/display_v2.c` 按芯片 ID 生成序列号，注册顺序为 Display、HID、可选 Maintenance。当前实际芯片序列号、分配后的接口／端点、Windows 驱动绑定与发现 GUID 仍待实际描述符核对。Maintenance 是独立维护通道，不能作为主题 Display 接口发送目标。

## HID 与命令隔离

`vendor/usb_camera/usb_v2_hid.c` 定义 usage page FF00、usage 1；Input ID1、Output ID2、Feature ID3 均为 64 字节（含 Report ID）。Feature 返回协议版本 2.0、当前／目标模式与能力位；Input 返回状态及最近命令／结果／token。Output 的字节 1／2／3 分别为命令、值、非零 8 位 token，字节 4～63 要求零。模式切换 HID 31 的参数 0 选择 THEME、1 选择 SCREEN，不能直接套用内部枚举 SCREEN=0、THEME=1。相同 token 只与最近命令比较，不能解释为跨重枚举的完整幂等保证。

HID 30 是整机 SOFT_RESET，受理后延时调用 reset；Display `AF 20 30 00` 则在 THEME 清理拼帧队列／残留，关闭显示使能和 AUTORLX。二者必须按通道隔离，不能将 HID 复位用于恢复半帧。Display 清理会改变显示状态，故也不能满足当前主机“清理保留电源意图”的假设，需明确处理与确认后再接入。

THEME 的 Display 扩展 34 为旋转（0～3），36 为电源（0／1），38 为 heartbeat（值 0）；Input 的最近 Display 命令／结果和 16 位计数位于字节 24～27。该计数不是请求 token，也不是完整帧解码／呈现确认；温度、FPS 和帧计数的零占位不能作为真实指标。

## 数据面

`drivers/usb/gadget/display/f_display.c` 的 vendor IN 接口查询定义 request 01=status、02=EDID（wValue 为偏移）、03=soft reset、04=backlight；03 虽然是 IN 仍有副作用，不能因方向为 IN 就当作只读。接口号应取实际分配值。尺寸来自 EDID，与 status 为不同查询，尚不能直接填入主机现有单响应尺寸／ready 契约。

`vendor/usb_camera/usb_display.c` 接受 AF 68／69，offset 与本片长度为 24 位大端；69 再带 24 位 pixel_offset，当前完整帧路径忽略局部刷新。主机分片器使用本片实际长度，与源码读取方式一致；remaining-1 只是主机避免空尾片的分片算法，不是线上的 length 字段。接收拼帧块为 1MiB，源码 bulk 缓冲为 256KiB；实际传输限额、短包／ZLP、JPEG 可接受格式及异常清理完成仍需结合设备记录确认。

`USB_V2_设备端实施调试记录.md` 含较早上板与 Windows 故障记录，但不能证明当前提交、当前镜像及主机链路已验收。1.2～1.5 保持未完成；下一步补齐实机身份／描述符与命令完成语义，并对照实际固件版本接入适配器。
