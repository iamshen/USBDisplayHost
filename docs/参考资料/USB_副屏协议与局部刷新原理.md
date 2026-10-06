# USB Display 副屏协议与局部刷新原理

> 适用平台：AD100 (Darwin RTOS) / USB Display 副屏模式
> 最后更新：2026-10-05
> 协议来源：Macrosilicon（宏矽半导体）USB Display 方案（Windows 驱动为 `ingenic_display_driver.dll`，含 ffmpeg 编码器）
> 代码位置：`freertos/drivers/usb/gadget/display/f_display.c`、`freertos/vendor/usb_camera/usb_display.c`

**文档结构**

| 部分 | 内容 | 读者 |
|---|---|---|
| **Part A（§1-§11）** | 设备端协议原理：枚举、EDID、0xAF 数据面、rlx 局部刷新、诊断 | 固件开发 |
| **Part B（文末）** | **上位机（Host）0xAF 协议规范**：自研上位机取代 CDC 的完整接口规范 | 上位机开发 |
| **附录 C** | 驱动支持的复合设备形态（INF 清单）+ 双模切换设计 | 方案/架构 |

---

## 1. 协议架构总览

USB Display 是 **vendor 类设备**，由三层构成：

```
┌─────────────────────────────────────────────────────────┐
│  USB 描述符层 (f_display.c)                               │
│   - Interface: Vendor Spec 0xFF, 1 个 Bulk OUT 端点        │
│   - EP0 控制请求: GET_STATUS/GET_EDID/SOFT_RESET/          │
│     GET_BACKLIGHT + specific descriptor 查询               │
├─────────────────────────────────────────────────────────┤
│  厂商描述符层 (usb_display.c)                              │
│   - EDID (分辨率/时序) + specific descriptor (压缩能力)      │
│   - 0x20 命令协商 (BLANK/COLOR_FORMAT/W/H/BACKLIGHT/       │
│     AUTORLX/MJPEG)                                        │
├─────────────────────────────────────────────────────────┤
│  数据面 (usb_display.c 收帧线程)                           │
│   - 0xAF 帧头 + type 分发                                  │
│   - MJPEG 全帧: 0x68/0x69 分片拼帧 → jpegd 解码 → NV12 上屏 │
│   - rlx 局部刷新: 0x65/0x6B RLE / 0x67 raw / 0x6A copy     │
│     / 0x66 pan → RGB 全帧缓冲增量写 → 上屏                 │
└─────────────────────────────────────────────────────────┘
```

### 1.1 USB 枚举结构

`f_display.c:60-100`：

| 项目 | 值 |
|---|---|
| bInterfaceClass | `USB_CLASS_VENDOR_SPEC` (0xFF) |
| bInterfaceSubClass | `USB_SUBCLASS_VENDOR_SPEC` (0xFF) |
| 端点 | 1 个 **Bulk OUT**（仅收不发） |
| High-Speed wMaxPacketSize | 512 |
| VID/PID | `0xa108:0xad10` |
| USB_BUFSIZE | 256KB（4 个接收请求预提交） |

---

## 2. EP0 控制请求（枚举/协商阶段）

`f_display.c:49-53` 定义，`display_func_setup` 处理：

| bmRequestType | bRequest | 含义 | 响应 |
|---|---|---|---|
| `IN\|VENDOR\|INTERFACE` | 0x01 | **GET_STATUS** 设备状态 | 1 字节 `display_status` |
| `IN\|VENDOR\|INTERFACE` | 0x02 | **GET_EDID** 读 EDID | wValue=偏移，返回 EDID 块 |
| `IN\|VENDOR\|INTERFACE` | 0x03 | **SOFT_RESET** 软复位 | 重建 bulk 请求队列 |
| `IN\|VENDOR\|INTERFACE` | 0x04 | **GET_BACKLIGHT** 读背光 | 1 字节 backlight |
| `IN\|STANDARD\|INTERFACE` | GET_DESCRIPTOR wValue>>8=**0x1F** | 读 specific descriptor | 返回厂商描述符 |
| `IN\|CLASS\|INTERFACE` | 0x01 (0xC1.01) | 能力查询 | 4 字节能力位图（全 0） |
| `IN\|CLASS\|INTERFACE` | 0x02 (0xC1.02) | 设备信息查询 | 128 字节（全 0） |

**关键经验**：
- 0xC1.01 / 0xC1.02 是 Windows 驱动的协商查询，**必须响应**（返回全 0 能力位图），否则 STALL → 驱动降级/黑屏。
- GET_EDID 支持偏移分块读取（`offset = ctrl->wValue`）。

---

## 3. EDID — 宣告分辨率/时序

`usb_display.c:39-48`。Windows 读 EDID 后建立显示模式：

- DTD1：800x1280 @60Hz（pclk=0x1AE4=68.84MHz，**必须精确**）
- DTD2：800x1280 @30Hz（pclk=0x0D72=34.42MHz）
- 校验和 0xC4

> 陷阱：时序标称 <30Hz 时 Windows 拒绝建模式 → 不推流 → 黑屏。

---

## 4. specific descriptor — 宣告压缩能力

`usb_display.c:58-81`，通过 `IN|STANDARD|INTERFACE` + wValue 高字节 0x1F 读取：

```
0x31        length
0x5F        descriptor type
0x02 0x00   version 2.0
0x2F        length after type

key=0x0001  min_width_height:   240x240
key=0x0002  max_width_height:   2047x2047
key=0x0003  max_transfer:       256*1024-32   (必须与 USB_BUFSIZE 一致)
key=0x0100  compression:
    0x01 0x00 0x01 0xC0   = MJPEG + pandisplay(bit1) + compression_64(bit7=rlx 使能)
key=0x0200  resolution:        800x1280 @30
key=0x0200  resolution:        800x1280 @60
```

**compression 第 4 字节 bit7 是关键开关**：
- bit7=1 → 声明支持 rlx（局部刷新）→ Windows 对鼠标移动走 0x65/0x6B RLE 增量路径
- bit7=0 → 只推 MJPEG 全帧

只声明 MJPEG（不声明 h264），因为 AD100 无 h264 解码器。

---

## 5. 数据面：0xAF 协议帧

所有数据包以 `0xAF` 开头，后跟 1 字节 type：

```
[0xAF][type][payload...]
```

type 分发（`usb_display_thread` switch）：

| type | 名称 | 方向 | 说明 |
|---|---|---|---|
| 0x20 | 命令 | 驱动→设备 | `cmd(1)+val(1)` 参数 |
| 0x68 | MJPEG 分片 | 驱动→设备 | `src_offset(3B 大端)+mjpeg_size(3B 大端)+data` |
| 0x69 | MJPEG 帧尾 | 驱动→设备 | `src_offset(3B)+mjpeg_size(3B)+pixel_offset(3B)+data` |
| 0x65 | rlx 24bit RLE | 驱动→设备 | `pixel_offset(3B)+total_count(1B)+copy_start(1B)+RLE段` |
| 0x6B | rlx 16bit RLE | 驱动→设备 | 同上（RGB565） |
| 0x67 | rlx raw | 驱动→设备 | `pixel_offset(3B)+raw_size(3B)+原始字节` |
| 0x6A | rlx copy | 驱动→设备 | `src_offset(3B)+copy_size(3B)+dst_offset(3B)` |
| 0x66 | pandisplay | 驱动→设备 | rlx 增量上屏命令 |
| 0xAF | 连续包标记 | - | 同包内多帧（goto check_type） |

同包可包含多个帧（连续 0xAF 标记）；不同 type 交错出现（如 0x69 帧尾后紧跟 rlx）。

---

## 6. MJPEG 全帧推流（0x68/0x69）

### 6.1 分片格式

大帧（一帧 200-400KB）超过 USB 单请求 256KB，被分成多个 0x68 分片：

```
0x68: [0xAF][0x68][src_offset 3B BE][mjpeg_size 3B BE][jpeg data...]
0x69: [0xAF][0x69][src_offset 3B BE][mjpeg_size 3B BE][pixel_offset 3B BE][jpeg data...]
```

- `src_offset`：本分片在 JPEG 流中的字节偏移
- `mjpeg_size`：本分片数据字节数
- 0x69 多一个 `pixel_offset`：像素偏移（用于局部刷新帧，本实现忽略，整帧上屏）

### 6.2 拼帧流程

1. **分片连续**（`src_offset == 已拼字节数`）→ `memcpy` 到池块，更新进度
2. **分片不连续** → 整帧作废（`g_diag_seg_bad++`）
3. **0x69 帧尾到达** → 校验完整（`0xFFD8` 开头）→ push 就绪队列 → post semaphore
4. disp 线程消费 → `jpeg_display_show()`（jpegd 硬解 → NV12 直接层上屏）

### 6.3 跨 USB 包截断处理（关键机制）

Windows 全屏推流时，分片头或数据可能被 USB bulk 请求边界切开：

```
USB 包 1: [0xAF 0x68 头...数据截断到 包尾]
USB 包 2: [剩余数据...]
```

处理（`g_abnormal_data[512KB]` 残留缓冲）：
- 检测到截断（数据不满 0x68/0x69 声明长度）→ 保存**从 0xAF 起始**的残留（0x68 用 `pbuf-8`、0x69 用 `pbuf-11` 回退到 0xAF）
- 下包到达先 `memcpy` 拼接残留再解析
- 残留必须从 0xAF 起始保存，否则拼接后首字节非 0xAF → 外层 while 匹配失败丢包

---

## 7. rlx 局部刷新原理（核心）

### 7.1 为什么需要局部刷新

全屏 60Hz MJPEG：一帧 200-400KB，USB 带宽 3-10MB/s，解码 + 传输开销大。
Windows 对**小面积变化**（鼠标移动、滚动条、输入框）走 **rlx 增量路径**：
只传"变化的矩形区域"，大幅降低带宽。

### 7.2 工作模式：惰性烘焙（Lazy Bake）

设备侧维护一块 **RGB 全帧缓冲 `rlx_fb`**（800x1280x4=4MB），作为"当前屏幕内容"的 RGB 副本：

```
MJPEG 全帧 (0x69) ──→ NV12 直接层上屏 (性能不变)
                          │ 同时更新 rlx_mix_layer 指向最新帧, rlx_baked=0
                          ▼
rlx 数据到达 (0x65/0x67/0x6A/0x6B)
                          │
              ① jpeg_display_rlx_bake()   ← 惰性! 只在首次 rlx 数据时
              │  用 fb_layer_mixer 把最近 NV12 帧 → RGB 写入 rlx_fb
              │  (NV12→RGB 转换, 离屏烘焙不阻塞推流)
              │  切显示层: NV12 直接层 → RGB 层 (rlx_fb)
              │  置 rlx_baked=1 (防重复烘焙)
              ▼
              ② 增量写 rlx_fb (RLE/raw/copy)
              ③ 0x66 pandisplay → flush_dcache + fb_pan_display 上屏
```

**为什么要烘焙**：rlx 增量只改"变化区域"，其余像素需要"上一帧内容"。设备必须先把最近的完整帧转换到 RGB 缓冲，才能叠加增量。惰性 = 只在 rlx 首次到达时做，不干扰纯 MJPEG 模式的性能。

### 7.3 RLE 解码（0x65/0x6B）

头 7 字节后是 RLE 段，两种元素交错：

```
原始像素段 (0..copy_start): 24bit=3B大端 (RGB888) / 16bit=2B大端 (RGB565)
复制段 (copy_start..):     1B 重复次数, 重复前一个原始像素值
```

`jpeg_display_rlx_rle` 实现要点：
- `total_count==0 → 256`、`copy_start==0 → 256`（**必须 u16，u8 截断死循环**）
- `pixel_offset * bpp` 定位写入位置（bpp=2/4 由 COLOR_FORMAT 决定）
- 越界防御：目标超 `rlx_fb` 时只解析不写

### 7.4 raw（0x67）与 copy（0x6A）

- **raw**：`pixel_offset(3B)*bpp + raw_size(3B)*bpp + 原始字节` → 直接 memcpy 到 rlx_fb
- **copy**：`src_offset(3B)*bpp + copy_size(3B)*bpp + dst_offset(3B)*bpp` → `memmove` 帧内搬运（滚屏优化）

### 7.5 pandisplay（0x66）

CPU 写完 rlx_fb 后：
1. `flush_dcache(rlx_fb, size)` — LCDC DMA 读前必须刷缓存
2. `fb_pan_display(fb, 0)` — 上屏

### 7.6 并发与地址语义（关键陷阱）

| 项目 | 规则 |
|---|---|
| fb_set_config | 用**物理地址**（`virt_to_phys`） |
| fb_layer_mixer_* | 用**虚拟地址**（内部转物理） |
| 并发 | `pan_lock` 串行化：disp 线程 fb_pan_display 与 rx 线程 rlx_pan 互斥（同 DPU 硬件） |
| 解码器重建 | rlx_mix_layer 指向旧 NV12 缓冲 → 必须清 y.mem/uv.mem 防 use-after-free |

---

## 8. 命令协商序列（0x20）

Windows 连接后按序协商（`display_cmd_process`）：

```
COLOR_FORMAT → WIDTH_HIGH/LOW → HEIGHT_HIGH/LOW → BACKLIGHT
→ AUTORLX → BLANK_MODE ON → 推流 (MJPEG 全帧 / rlx 增量)
```

| cmd | 含义 | 设备动作 |
|---|---|---|
| 0x00 COLOR_FORMAT | RGB565(bit0)/XRGB888(bit1) | 设 `g_display_bpp_byte` (2/4) |
| 0x01-0x04 W/H | 分辨率 | 存 `g_display_width/height` |
| 0x05 BACKLIGHT | 背光 0-100 | 设背光 |
| 0x06 AUTORLX | rlx 使能 | `jpeg_display_rlx_set_autorlx` |
| 0x1f BLANK_MODE | ON=1/POWERDOWN=2 | ON: rlx 建缓冲+亮屏; OFF: 灭屏 |
| 0x11 MJPEG | 推流开关 | 日志 |

---

## 9. 收帧/解码双线程架构

```
USB Bulk OUT
   │  (4×256KB 预提交请求)
   ▼
rx 线程 (usb_disp_rx, 8KB 栈)
   │ 解析 0xAF → 拼帧 → push 就绪队列 → post sem → 立即归还 USB 缓冲
   ▼
就绪 FIFO (g_q[6], mutex 保护, 满丢最旧)
   ▼
disp 线程 (usb_disp, 16KB 栈)
   │ 等 sem → jpeg_display_show (jpegd 解码 + fb 上屏) → 释放块回池
   ▼
LCD (NV12 直接层 / rlx RGB 层)
```

**为什么双线程**：jpegd 解码 4.6ms + fb 上屏（flush_cache）在 60Hz 预算 16.6ms 内跑不完。单线程时 USB 请求缓冲不归还 → 无可用 req → 停流卡死。双线程解耦后解码慢只丢帧不掉 USB。

---

## 10. 诊断日志解读（[diag] 行）

```
[diag] 68=/69=/cmd=/other=/noaf=/abn=/hdrtr=/segbad=/fok=/fbad=
       typ= | rlx 65=/6b=/raw=/cp=/pan=/bad=/rlxb=
```

| 计数 | 含义 | 高值问题 |
|---|---|---|
| 68/69 | MJPEG 分片/帧尾数 | 69≈0 且 fps 低 = 驱动不走全帧 |
| fok/fbad | 完整帧成功/失败 | fbad 高 = 坏帧 |
| segbad | 分片不连续作废 | 高 = 丢包/协议错位 |
| hdrtr | 分片头被包边界截断 | 截断机制在工作 |
| noaf | 数据非 0xAF 开头 | 高 = 数据错位 |
| other/typ | 未知 type | 高 = 协议版本不匹配 |
| 65/6b | rlx RLE 包数 | 高 + 鼠标移动 = rlx 路径活跃 |
| bad | rlx 解析异常 | 高 = rlx 坐标/长度错位 |
| rlxb | rlx 消费字节数 | 流大小参考 |

---

## 11. 相关文件索引

| 文件 | 职责 |
|---|---|
| `freertos/drivers/usb/gadget/display/f_display.c` | USB function：描述符/EP0 请求/bulk 收包 |
| `freertos/include/usb/gadget_display.h` | 协议宏定义 |
| `freertos/vendor/usb_camera/usb_display.c` | EDID/specific descriptor/0xAF 收帧/拼帧/rlx 解析 |
| `freertos/vendor/usb_camera/jpeg_display.c` | jpegd 解码 + LCD 显示 + rlx 显示层（烘焙/RLE/pan） |
| `freertos/xburst2/soc-ad100/jpeg/jpegd_*.c` | JPEGD 硬件解码驱动 |
| `freertos/example/usb/device/gadget_usb_display.c` | 官方参考实现 |
| `ingenic_display_driver/` | Windows 驱动（二进制，含 ffmpeg MJPEG 编码器） |

---
---

# Part B：上位机（Host）0xAF 协议规范 — 替代 CDC

> 本部分面向**自研上位机**：不使用 Windows 显示驱动、不走 CDC 串口，
> 由上位机直接作为主机端（libusb / WinUSB）按 0xAF 协议向设备推帧。
> 所有字段均已对照 `usb_display.c` / `f_display.c` 逐条核实。

## B1. 设备标识与端点

| 项 | 值 | 代码位置 |
|---|---|---|
| VID / PID | `0xa108` / `0xad10` | `usb_display.c:166` |
| 设备类 | Vendor Specific（`bDeviceClass=0`，单 display 形态） | `display.c:26-28` |
| 接口 | `bInterfaceClass=0xFF`、`SubClass=0xFF`、`1 个 Bulk OUT`（**无 IN 端点**） | `f_display.c:58-73` |
| Bulk OUT 端点地址 | 由 `usb_ep_autoconfig` 分配（通常 `0x01`），**必须从描述符读取，禁止硬编码** | `f_display.c:491` |
| HS `wMaxPacketSize` | 512 | `f_display.c:92` |
| 接收缓冲 | 4 × 256KB 预提交请求 | `f_display.c:55-56` |
| 单帧上限 | **1 MB**（拼帧池 `DISPLAY_JPEG_BUF_SIZE`） | `usb_display.c:33` |
| 单请求上限 | **256 KB**（`USB_BUFSIZE`），建议按 **240 KB** 发包 | `f_display.c:56` |
| max_transfer（描述符声明） | `256*1024-32 = 262112` | `usb_display.c:69` |

⚠ **前提**：当前固件上电默认是 CDC 模式（`usb_camera_main.c:645`）。
不改代码的情况下，上位机需先用现有 CDC 通道发 `0x16 [1]` 让设备切到 Display composite，
USB 重枚举后才能按本协议通信。

## B2. 传输层通用规则

1. **所有数据以 `0xAF` 开头**：`[0xAF][type][payload...]`。
2. 一次 bulk 传输可包含**多个** 0xAF 帧（同包连续帧，见 `usb_display.c:832`）。
3. **每一片必须完整落在同一次 bulk 传输内**。跨传输边界虽有 512KB 残留缓冲兜底，
   但会走 `hdr_trunc` 慢路径且极易错位 —— 上位机应主动避免。
4. 单帧 JPEG **≤ 1 MB**，超了设备会因 `mjpeg_overflow` 丢弃整帧。
5. 若启用 WinUSB/libusb，每次 bulk 传输长度建议 ≤ 240KB，且**必须是 512 的整数倍或短包结尾**（HS bulk 语义）。

## B3. 帧类型总表

| type | 名称 | 方向 | payload |
|---|---|---|---|
| `0x20` | 命令 | Host→Dev | `cmd(1) + val(1)` |
| `0x68` | MJPEG 分片 | Host→Dev | `src_offset(3B BE) + mjpeg_size(3B BE) + data` |
| `0x69` | MJPEG 帧尾 | Host→Dev | `src_offset(3B BE) + mjpeg_size(3B BE) + pixel_offset(3B BE) + data` |
| `0x65` | rlx 24bit RLE | Host→Dev | （可选，见 Part A §7） |
| `0x6B` | rlx 16bit RLE | Host→Dev | （可选） |
| `0x67` | rlx raw | Host→Dev | （可选） |
| `0x6A` | rlx copy | Host→Dev | （可选） |
| `0x66` | pandisplay | Host→Dev | （可选，rlx 上屏） |
| `0xAF` | 连续包标记 | — | 同包内紧跟下一帧 |

> **所有 3 字节偏移/长度均为大端（BE）**。

## B4. 命令帧（type = 0x20）

格式：`AF 20 <cmd> <val>`

| cmd | 名称 | val 取值 | 设备动作 |
|---|---|---|---|
| `0x00` | COLOR_FORMAT | `0x01`=RGB565、`0x02`=XRGB888 | 设 `g_display_bpp_byte`（2/4，默认 2） |
| `0x01` | WIDTH_HIGH | 宽度高字节 | `g_display_width` |
| `0x02` | WIDTH_LOW | 宽度低字节 | 同上 |
| `0x03` | HEIGHT_HIGH | 高度高字节 | `g_display_height` |
| `0x04` | HEIGHT_LOW | 高度低字节 | 同上 |
| `0x05` | BACKLIGHT | `0-100` | 设背光（替代 CDC `0x03`） |
| `0x06` | AUTORLX | `0`/`1` | rlx 开关（**上位机建议恒 0**） |
| `0x11` | MJPEG | `0`/`1` | 推流开关（仅日志） |
| `0x1f` | BLANK_MODE | `0x01`=ON、`0x02`=POWERDOWN | ON：停动画 + 建缓冲 + **亮屏** |

### B4.1 必须严格遵守的协商序列

```
COLOR_FORMAT → WIDTH_HIGH → WIDTH_LOW → HEIGHT_HIGH → HEIGHT_LOW
→ BACKLIGHT → AUTORLX(0) → MJPEG(1) → BLANK_MODE(ON) → 开始推帧
```

**只有收到 `BLANK_MODE ON` 后设备才亮屏**（`usb_display.c:649-663`：
`boot_anim_stop()` + `rlx_setup/start` + `jpeg_display_set_power(1)`）。

### B4.2 示例（面板 1080x2400，RGB565，背光 100）

```
AF 20 00 01     COLOR_FORMAT = RGB565
AF 20 01 04     WIDTH_HIGH   1080 = 0x0438
AF 20 02 38     WIDTH_LOW
AF 20 03 09     HEIGHT_HIGH  2400 = 0x0960
AF 20 04 60     HEIGHT_LOW
AF 20 05 64     BACKLIGHT = 100
AF 20 06 00     AUTORLX = 0
AF 20 11 01     MJPEG   = 1
AF 20 1F 01     BLANK_MODE = ON   ← 亮屏
```

⚠ **`BLANK_MODE=0x02`(POWERDOWN) 会触发设备复位**
（`usb_display.c:664-674`：DISPLAY 模式下记录 screen-off，模式线程立即 `reset()`）。
上位机退出时**不要发 POWERDOWN**，直接 `libusb_release_interface()` 即可 ——
USB 停止推帧不会黑屏，LCDC 会保持最后一帧。

## B5. MJPEG 推帧（0x68 / 0x69）

### B5.1 小帧（单包可容纳，推荐）

```
AF 69 <src_offset:3B=0x000000> <mjpeg_size:3B=帧长> <pixel_offset:3B=0x000000> <JPEG 数据>
```

例：一帧 200000 字节 = `0x030D40`
```
AF 69 00 00 00 03 0D 40 00 00 00 FF D8 FF E0 ... FF D9
```

### B5.2 大帧（> 240KB 必须分片）

```
AF 68 <src_offset:3B> <size:3B> <data>     # 前 n-1 片
AF 69 <src_offset:3B> <size:3B> <pixel_offset:3B> <data>   # 最后一片
```

### B5.3 硬性判定规则（设备端实测逻辑）

| 规则 | 说明 | 违反后果 |
|---|---|---|
| `src_offset == 已拼字节数` | 必须严格连续 | `segbad++`，**整帧作废** |
| `pbuf + size <= 本传输末尾` | 分片不得跨传输 | 走残留缓冲，易错位 |
| `src_offset + size <= 1MB` | 帧不超池块 | `overflow`，整帧丢 |
| 首两字节 `FF D8` | JPEG SOI | `fbad++`，坏帧丢弃 |
| 分片顺序 68…68…69 | 0x69 即帧尾 | 无 0x69 则永不入队 |

> 设备端**只校验 `FF D8` 开头**，不校验 `FF D9` 结尾，但上位机仍应输出完整合法 JPEG。
> 0x68 单独到达（src_offset=0）也会申请新块；一帧只用 0x69 是最简形式。

## B6. rlx 局部刷新（可选，上位机不建议启用）

上位机若做主题屏（低频刷新），**直接整帧 MJPEG 最简单可靠**。
如需 60Hz 级桌面体验再考虑 rlx，协议见 Part A §7，要点：

- 0x65(24bpp) / 0x6B(16bpp) RLE：头 7 字节后 `total_count==0→256`、`copy_start==0→256`（必须 u16）
- 0x67 raw：直接 memcpy；0x6A copy：帧内 memmove
- 0x66 pandisplay 触发上屏
- 首次 rlx 前设备会把最近一帧 NV12 烘焙成 RGB（惰性 bake）
- 1080x2400 下 RGB 全帧缓冲：bpp4 ≈ **10.4MB** / bpp2 ≈ **5.2MB**

## B7. EP0 控制传输 —— 唯一的回传通道

0xAF 数据面是单向 OUT，**但 EP0 控制传输是双向的**，上位机可用它读少量状态
（`f_display.c:374-435`，`wIndex` = display 接口号，通常为 0）：

| bmRequestType | bRequest | wValue | 返回 |
|---|---|---|---|
| `0xC1` (IN\|VENDOR\|IF) | `0x01` GET_STATUS | — | 1 字节 `display_status`（正常 = `0x18` = INIT_COMPLETE\|READY） |
| `0xC1` | `0x02` GET_EDID | `offset` | EDID 块（128B，支持偏移分块） |
| `0xC1` | `0x03` SOFT_RESET | — | 0 字节（重建 bulk 请求队列） |
| `0xC1` | `0x04` GET_BACKLIGHT | — | 1 字节背光值 |
| `0x81` (IN\|STD\|IF) | `0x06` GET_DESCRIPTOR | `0x1F00` | specific descriptor（49 字节，含分辨率/压缩能力） |
| `0xC1` (IN\|CLASS\|IF) | `0x01` | — | 4 字节能力位图（全 0） |
| `0xC1` (IN\|CLASS\|IF) | `0x02` | — | 128 字节（全 0） |

**推荐上位机启动流程**：先读 specific descriptor（`0x81.06 wValue=0x1F00`）或 EDID，
拿到设备宣告的 `width/height` 再按该分辨率渲染，避免推错尺寸。

## B8. 完整会话状态机

```
[1] libusb_open(0xa108, 0xad10) → claim_interface(0)
[2] (可选) EP0 读 specific descriptor / EDID → 取分辨率
[3] EP0 读 GET_STATUS，确认 0x18
[4] 发送 B4.2 命令序列（末尾 BLANK_MODE ON）
[5] 循环：渲染 JPEG → 组 0x69（必要时 0x68 分片）→ bulk 写
      · 帧间隔按需求：主题屏 1~5fps，桌面镜像 15~30fps
      · 每秒至少 1 帧维持画面（停推不黑屏，但会定格）
[6] 退出：停止推帧 → libusb_release_interface → close
      （不要发 BLANK_MODE POWERDOWN）
```

### 断点续传 / 异常恢复
- 设备无"超时切回"机制：上位机崩溃后画面定格，重连时**从步骤 3 重发命令序列**即可；
- 若拼帧错乱（日志 `segbad` 飙升）：发 `EP0 SOFT_RESET(0xC1.03)` 重建设备端请求队列，再重发命令序列；
- 重连前建议先发一帧全黑 JPEG 清屏（可选）。

## B9. 与现有 CDC 协议的能力对照（迁移表）

> ⚠ **术语澄清**：下表"未实现"**不是协议限制**，而是指当前固件 `display_cmd_process()`
> （`usb_display.c:614-683`）里没有对应 `case`。**协议本身完全开放，自定义命令随时可加**，
> 详见 §B13。未知 cmd 走 `default: break` 静默忽略（`usb_display.c:680-681`），
> 所以现在就发自定义命令也不会报错，只是暂时没反应。

| CDC 命令 | 功能 | 0xAF 替代方案 | 状态 |
|---|---|---|---|
| `0x01` | 复位屏幕 | 自定义 `AF 20 30 xx` | 未实现 → **加个 case 即可** |
| `0x03` | 亮度 | `AF 20 05 <0-100>` | ✅ 已支持 |
| `0x06` | 设备信息（JSON） | 自定义 EP0 vendor IN（如 `0xC1.06`） | 需反向通道（见 §B13.3） |
| `0x0E` | 屏幕开关 | 开 `AF 20 1F 01`；**关建议自定义** `AF 20 36 00` | 关屏不建议用 `1F 02`（会复位） |
| `0x11` | 视频开始 | `AF 20 11 01` + 推帧 | ✅ 已支持 |
| `0x0C` | OTA 升级 | 自定义 `AF 20 32` + 扩展数据帧 `0x21` | 未实现 → 可补齐 |
| `0x12` | 开机动画下载 | 自定义 `AF 20 33` + 扩展数据帧 `0x21` | 未实现 → 可补齐 |
| `0x14/0x15` | 待机/息屏定时 | 自定义 `AF 20 35 <秒>` | 未实现 → 可补齐 |
| `0x16` | 切副屏 | 仍需经 CDC（当前固件默认 CDC 模式） | — |
| — | 旋转 | 设备端无旋转能力，由**上位机渲染时旋转** | ✅ |
| — | 心跳/保活 | 持续推帧，或 EP0 `0xC1.01` GET_STATUS 轮询 | ✅ |

## B10. 分辨率与渲染约定

- 当前面板 **VTDR6115 = 1080×2400**（竖屏）；基线 FW101 = 800×1280。
- 设备端 `IMG_WIDTH/IMG_HEIGHT`（`device_config.h`）目前是 **1920×1080，与面板不符**，
  会影响 EDID/描述符宣告；上位机模式下**以 EP0 读到的描述符或面板实际分辨率为准**。
- **设备端无旋转能力**（`device_config.json` 的 angle 仅作身份上报）。
  上位机要旋转必须在渲染阶段完成（先旋转再缩放/填充）。
- 主题屏建议直接按面板分辨率 1:1 渲染，避免走 DPU 缩放（缩放层高上限 2047，1080×2400 需双层拆分）。

## B11. 带宽与帧率预算

| 项目 | 数值 |
|---|---|
| 1080×2400 MJPEG 典型单帧 | 150 ~ 400 KB（质量依赖） |
| 30 fps 带宽 | 4.5 ~ 12 MB/s（USB HS 实际 ~30MB/s，够） |
| jpegd 解码 + DPU 双层缩放实测上限 | ≈ **26 fps** |
| 主题/温度屏推荐 | **1 ~ 5 fps**（温度 1Hz 更新即可） |
| 单次 bulk 传输 | ≤ 240 KB（设备请求 256 KB） |

## B12. 排错 checklist（配合设备串口 `[diag]` 日志）

| 现象 | 日志特征 | 原因 |
|---|---|---|
| 完全不出图 | `cmd` 计数增长但 `69=0` | 没发 `BLANK_MODE ON`，或命令序列不全 |
| 出图但错乱/花屏 | `segbad` 高 | `src_offset` 不连续 / 分片顺序错 |
| 收得到但拼不出帧 | `68` 高、`69` 低 | 大帧分片发了 0x68 却漏了 0x69 结尾 |
| `fbad` 高 | — | JPEG 头非 `FF D8`，或帧 >1MB 溢出 |
| `hdrtr` 高 | — | 分片跨了 bulk 传输边界 → 缩小分片 |
| `other/typ` 有值 | — | 发了未定义 type，检查字节序 |

## B13. 自定义命令扩展规范（协议完全开放）

**结论：0xAF 协议没有"官方命名空间"限制。** `0x20` 命令帧的 `cmd` 就是设备端
`display_cmd_process()`（`usb_display.c:614`）的一个 `switch`，加一个 `case` 立刻生效。
现有的 `0x00-0x1F` 只是 Windows 驱动会用到的那几个，**不是协议上限**。

### B13.1 cmd 空间划分（建议）

| 区间 | 归属 | 说明 |
|---|---|---|
| `0x00–0x06` | 驱动协商 | COLOR_FORMAT / W/H / BACKLIGHT / AUTORLX |
| `0x11–0x12` | 编码格式 | MJPEG / H264 |
| `0x1F` | BLANK_MODE | ON / POWERDOWN |
| `0x20–0x2F` | 保留 | — |
| **`0x30–0xFF`** | **自定义区** | 上位机专用，**Windows 驱动永远不会发这些，无冲突风险** |

### B13.2 建议的自定义命令表

| cmd | 功能 | val | 对应 CDC |
|---|---|---|---|
| `0x30` | 软复位 / 重连 | — | `0x01` |
| `0x31` | 模式切换（切 PID 重枚举：主题态 ↔ 副屏态） | `0`=主题 `1`=副屏 | `0x16` |
| `0x32` | 进入 OTA 裸流模式 | — | `0x0C` |
| `0x33` | 开机动画下载模式 | — | `0x12` |
| `0x34` | 屏幕旋转（若设备端实现） | `0/1/2/3` = 0/90/180/270 | — |
| `0x35` | 待机息屏定时 | 秒数 | `0x15` |
| `0x36` | **纯熄屏**（不复位！） | `0`=灭 `1`=亮 | `0x0E` |
| `0x37` | 主题屏画质 / 帧率档位 | 档位 | — |
| `0x38` | 心跳（上位机存活标记） | — | — |

> `0x36` 很重要：官方 `0x1F=02`(POWERDOWN) 在 DISPLAY 模式下会**触发系统复位**
> （`usb_display.c:664-674`），不适合做"关屏待机"。自己定义一条纯熄屏命令更安全。

### B13.3 三种扩展方式（按改动量递增）

| 方式 | 格式 | 改哪里 | 适用 |
|---|---|---|---|
| **① 加 cmd case** | `AF 20 <0x30+> <val>` | `display_cmd_process()` 加 case | 单字节参数（开关/档位/秒数） |
| **② 加扩展数据帧 type** | `AF 21 <cmd> <len:2B> <payload>` | `usb_display_thread` 的 type switch（`usb_display.c:815` 附近）加 `case 0x21` | OTA 固件、动画文件、长参数 |
| **③ 加 EP0 vendor 请求** | OUT：`0x41.<bRequest>`；IN：`0xC1.<bRequest>` | **必须同时**改 `display_req_match`（认领）和 `display_func_setup`（处理），否则 STALL（`f_display.c:452-455`） | 需要**应答/查询**的操作 |

### B13.4 唯一真正的限制：没有反向通道

0xAF 数据面是单向 OUT，**设备无法主动回话**。这条**不能靠定义命令解决**：

| 需求 | 解法 |
|---|---|
| 查询设备状态/信息 | EP0 vendor IN 请求：自定义 `0xC1.05` GET_VERSION、`0xC1.06` GET_INFO、`0xC1.07` GET_CAPS |
| OTA 需要 ACK/校验 | 走 EP0 控制传输（小数据）或加 HID/IN 端点（复合设备，见附录 C） |
| 大块数据上传 | 加 bulk IN 端点（需改 `f_display.c` 描述符 + 复合布局） |

**实践建议**：OTA 这类需要握手的操作，优先用 **EP0 控制传输**或 **HID 接口**（附录 C 的 `AD11`），
不要用单向 0xAF；单向 0xAF 只负责"推显示内容 + 单向开关命令"。

### B13.5 兼容性

- 自定义 cmd 用 `0x30+`，Windows 官方驱动不会发，绝不冲突；
- 未改固件时发自定义命令 → `default: break` 静默忽略，**安全无副作用**；
- 建议上位机启动后用 EP0 读一个 `GET_CAPS`/版本，判断固件支持哪些扩展命令，不支持则降级。

---
---

# 附录 C：复合设备形态与双模切换设计

## C1. 驱动已支持的设备形态（`ingenic_display_driver.inf:19-34`）

| PID | INF 硬件 ID | 形态 |
|---|---|---|
| `AD10` | `USB\VID_A108&PID_AD10` | 单 display（**不带 MI**，独占整个设备） |
| `AD11` | `USB\VID_A108&PID_AD11&MI_00` | display + **HID** |
| `AD12` | `...&PID_AD12&MI_00` | display + 声卡 |
| `AD13` | `...&PID_AD13&MI_00` | display + 声卡 + HID |
| `AD16` | `...&PID_AD16&MI_00` | display + **WinUSB** |
| `AD20` | `...&PID_AD20&MI_00` | display + usbip |
| `AD21` | `USB\VID_A108&PID_AD21` | wireless display + usbip |

**`&MI_00` 的含义**：驱动**只绑定接口 0**，其余接口留给别的驱动。
→ 约束：**display function 必须是第一个 `usb_add_function` 的接口**。

### C1.1 各形态逐条解读（主机侧视角）

| PID | 复合构成 | 主机侧表现 | 反向通道 | 主机侧驱动 | 适用 |
|---|---|---|---|---|---|
| `AD10` | display | 1 台显示器，独占整个设备 | **无** | 官方 INF | 纯副屏（**当前形态**） |
| `AD11` | display + **HID** | 显示器 + HID 设备 | ✅ HID **IN 中断** | display 官方 + HID **系统内置免驱** | **上位机主题屏（首选）** |
| `AD12` | display + 声卡(UAC) | 显示器 + USB 声卡 | ❌（音频主机→设备为主） | display + 系统 usbaudio | 副屏需要出声 |
| `AD13` | display + 声卡 + HID | 显示器 + 声卡 + HID | ✅ HID IN | 同上 + 免驱 HID | 出声 + 控制（**设备端已有实现**） |
| `AD16` | display + **WinUSB** | 显示器 + 通用 bulk 设备 | ✅ **bulk IN**（大带宽） | display + WinUSB（INF/WCID/Zadig） | 大块回传：OTA、日志、抓屏 |
| `AD20` | display + usbip | 显示器 + USB-over-IP | ✅（走网络） | display + usbip | 触摸屏/Hub 经网络回传主机 |
| `AD21` | wireless display + usbip | 无线显示器 | — | 专用 | 需 WiFi 硬件，**本项目不适用** |

### C1.2 选型结论：解决"没有返回命令" → 用 `AD11`（display + HID）

| 理由 | 说明 |
|---|---|
| **免驱** | HID 由 Windows 内置 `hidusb.sys` 按 `bInterfaceClass=0x03` 匹配，**与 PID 无关**，装过副屏驱动即可用，无需 INF/Zadig/签名 |
| **双向** | IN 中断端点（设备→主机）+ OUT/SET_REPORT（主机→设备）+ Feature Report（控制传输，双向） |
| **接口级隔离** | display 走 MI_00、HID 走 MI_01，两个驱动各管各的，**不冲突、不需要切换模式** |
| **带宽够用** | HS `bInterval=4`（1ms）× 64B ≈ **64KB/s**；`report_length` 设到 1024 可达 ~1MB/s。温度/状态/ACK/心跳绰绰有余 |
| **大块场景再叠** | 若还要 OTA 应答、日志、抓屏等大流量，再上 `AD16`（bulk IN 几十 MB/s），代价是要装 WinUSB 驱动 |

> **推荐组合**：
> - 画面 → 0xAF Bulk OUT（不变）
> - 下发 → `0x20` 自定义 cmd `0x30+`（不变）
> - **回传 → HID IN report（温度、亮度、FPS、状态、ACK、版本、错误码）** ← 补上"返回命令"
> - 大块 → 可选叠 WinUSB（`AD16`）

### C1.3 设备端现成代码

`drivers/usb/gadget/display_uac1_hid/` **就是 `AD13` 形态的完整实现**
（`do_config` 顺序 display → uac1 → hid = MI_00/01/02，见 `display_uac1_hid.c:83-105`）：

- `f_hid.c`：**IN + OUT 中断端点**（`hidg_hs_in_ep_desc` / `hidg_hs_out_ep_desc`），
  `report_length` 由 `hid_params` 注入，支持 GET/SET_REPORT、Feature Report
- `hid.h`：`hid_device_alloc(report, connect_cb, request_cb)`
- **去掉 `f_uac1` 即得 `AD11`** —— 这是最快的落地路径

### C1.4 建议的 HID 报告格式（自定义，示例）

| Report | 方向 | 长度 | 内容 |
|---|---|---|---|
| Input ID 1 | 设备 → 主机 | 64B | 温度、亮度、FPS、帧计数、状态位、错误码、固件版本 |
| Output ID 2 | 主机 → 设备 | 64B | 命令（与 `0x20 cmd 0x30+` 等价，但**可带 ACK**） |
| Feature ID 3 | 双向 | ≤64B | 查询/配置（EP0 控制传输，适合需要应答的操作） |

上位机库：`hidapi`（跨平台）或 Windows `HidD_GetInputReport` / `ReadFile` / `WriteFile`。

## C2. 设备端做复合设备的三要素

1. 设备描述符改为 MISC/IAD：`bDeviceClass=0xEF, SubClass=0x02, Protocol=0x01`
   （单形态是 `0/0/0`，见 `display.c:26-28` vs `display_uac1_hid.c:29-31`）
2. `do_config` 里 **display 先 add** 再 add 其它 function（决定 MI_00 归属）
3. PID 传 `AD11`/`AD13`/`AD16` 等复合档位

现成范例：`drivers/usb/gadget/display_uac1_hid/display_uac1_hid.c:73-121`
（display → uac1 → hid = MI_00/01/02，对应 `AD13`）。
HID function 可直接复用：`display_uac1_hid/hid.h` 的
`hid_device_alloc(report, connect_cb, request_cb)`（IN/OUT 中断端点，双向）。

## C3. 双模切换（主题模式 ↔ 副屏模式）设计要点

**核心约束**：0xAF 数据面是单向 Bulk OUT，同一时刻只有一个主机端能占用 display 端点。
→ **两路显示数据无法并存**，必须"两态 + 换 PID 重枚举"；
复合设备的 HID/WinUSB 接口只解决**控制通道**（尤其"副屏态 → 主题态"的回程命令）。

| | 副屏态 | 主题态 |
|---|---|---|
| PID | `0xAD11`（display + HID） | `0xADA0`（自定义，**不在 INF 列表内**，但仍带 HID） |
| Windows 绑定 | MI_00→display 驱动；MI_01→系统 HID（免驱） | MI_00 匹配不到 display 驱动 → WinUSB（需 WCID/MS OS 描述符或自写 INF）；**MI_01 → HID 照样免驱** |
| 上位机通道 | **HID 双向**（查询状态 / 下发"切主题"命令） | **WinUSB 独占 display 端点**推 0xAF **+ HID 双向控制** |
| 切出方式 | HID 命令 → 换 PID 重枚举 | HID 命令或 0xAF 扩展命令（如 `AF 20 30 01`）→ 换 PID 重枚举 |

> **关键**：HID 是按 **接口类 0x03** 匹配的，**与 PID 无关** ——
> 即使主题态用了 INF 列表外的自定义 PID `0xADA0`，Windows **依然会自动加载内置 HID 驱动**。
> 所以两个态可以**都保留 HID 接口**，双向控制通道始终在线，这比用 CDC 切模式干净得多。

> **⚠ 前提修复**：当前 `usb_display.c:664-674` 在收到 `BLANK_MODE=POWERDOWN` 时会**直接复位系统**。
> 若上位机走"禁用/卸载 Windows 显示器 → 自己接管 MI_00"这条路，会触发该复位分支。
> 需先加自定义纯熄屏命令 `AF 20 36 00`（不复位），并让 screen-off 不再自动重启。

**切换不黑屏**：USB 重枚举不影响 LCDC，最后一帧会定格在屏上；
两端各加 ~250ms alpha 淡化即可实现平滑过渡（设备端 layer 支持 `alpha.enable/value`，
见 `jpeg_display.c` rlx 层配置；1080×2400 已用 layer0/1 做双层缩放，需评估剩余层数）。

**代价**：每次切换 Windows 桌面会重排（扩展屏上的窗口被挪回主屏）；
两套 INF 需预装（`pnputil /add-driver xxx.inf /install`），否则每次切换触发驱动安装。

## C4. 落地顺序建议

1. 固件切 `AD11` + 加 HID function → 验证"驱动只绑 MI_00 且上位机能开 HID"；
2. 加 PID 切换函数（`gadget_display_cleanup` → 改 `product_id` → `gadget_display_init`）；
3. 验证主题态 WinUSB 能打开并推 0xAF（WCID 优先，不行上 INF）；
4. 最后加 alpha 淡化与上位机状态机。
