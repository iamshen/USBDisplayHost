# PNG 方向元数据代码基线

关联：T-14、任务 6.2，REQ-04／FR-04。

Core PngOrientationInspector 在既有 20MiB 输入上限内检查 PNG 块边界，读取唯一 eXIf 的 TIFF 主 IFD 方向。支持 II／MM 字节序、方向 1～8；不存在方向时使用原始方向。方向项必须为 SHORT、count 1，重复块、重复方向项、非法值、越界偏移、截断或该元数据块 CRC 不匹配均返回 InvalidPngOrientation。读取不遍历缩略图、GPS 或任意扩展 IFD，不导出元数据。

eXIf 内容没有 JPEG APP1 的标记和 Exif ID 前缀，按 TIFF 数据直接解析；依据 [PNG 第三版 eXIf 定义](https://www.w3.org/TR/png-3/#eXIf)。未知块按有界长度跳过，像素完整性继续由既有 WPF 解码和逐行读取核实。本模块只验证 eXIf CRC，不声称检查所有块的 CRC。

没有按元数据块声明长度分配数组；CRC 使用 4KiB 缓冲，IFD 项复用 12 字节栈空间，读取受文件大小、条目数、边界和取消约束。正常返回、损坏或取消均恢复原流位置；I/O 异常由实际导入／渲染错误边界记录并返回业务错误，不关闭窗口。

WpfImageLoader 在 PNG 解码前调用该检查器，使用其方向结果复用既有旋转／镜像与物理像素处理。JPEG 沿用现有 WIC 元数据路径。导入验证和草稿／活动渲染均经过同一个加载器，不引入仅预览旋转的路径；方向变换后的 ImageInfo 使用实际像素宽高。

目标 Desktop Release 编译通过，0 警告、0 错误，`git diff --check` 通过。未创建或运行测试，未制作演示图片、解码文件、启动窗口或操作设备。实际八方向输出、透明背景、预览／帧一致性及损坏输入行为未验证，6.2 父项保持未完成。
