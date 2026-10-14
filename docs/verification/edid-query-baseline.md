# AD102 状态与 EDID 双查询初始化

日期：2026-10-09。关联 REQ-02／FR-02、REQ-03／FR-03、T-10、TC-03、TC-07、任务 4.5。源码依据见 `docs/contracts/firmware-source-baseline.md`。

Core 的 `ThemeDataPlaneContract.CreateWithEdid` 支持固件已有的两条只读查询：同一接口上的 vendor IN C1/request 01/value 0 读取一个字节 status，C1/request 02/value 0 读取 128 字节 EDID 基础块。接口号、固件版本、证据引用、ready 掩码／期望、DTD 位置、熄屏安全性与颜色格式由显式契约提供，不构造默认兼容记录；request 03 不得进入该只读路径。

status 使用掩码判定，尺寸从契约指定的 18 字节 DTD 解出。仅接受完整基础块、EDID 标识、1.3／1.4 版本、基础块校验和、非零像素时钟及匹配目标尺寸；DTD 位置限制在 54／72／90／108。不遍历并挑选任意模式，不将 DTD 当作实际硬件呈现证明，也不声称完整解析扩展块或所有显示时序。单响应、独立宽高字段的原有契约路径保留。

初始化先 status 后 EDID，两次查询前后复用原操作／会话代次门禁。任意响应长度、掩码、结构、校验和或尺寸不匹配均停止后续初始化并收敛 I/O；Unknown 保留原结果分类，取消不继续读取或发送。Off 查询是否允许仍取显式契约；On 仍需原有 parser 清理收据，不因增加 EDID 解析绕过清理语义差异。

常规目标构建因验收进程占用 Desktop 输出依赖而复制失败（MSB3021／MSB3027）；未终止该进程。改用独立输出目录执行：

`dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/edid-query/`

四生产工程成功，0 警告、0 错误。未创建或执行测试，未运行新契约、查询设备或发送初始化命令；已启动的旧窗口不包含本次修改。当前 App 尚未创建实际管线，4.5 父项保持未完成。
