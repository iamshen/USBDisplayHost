# 应用品牌图标接入

日期：2026-10-12。

已确认的 Logo v2 原图统一导出为 16、20、24、32、40、48、64、128、256 像素 PNG，以及包含相同九帧的 app.ico。保持原图透明度与比例，未重新设计造型。资源生成脚本位于设计目录的 branding/export-icons.ps1。

## 接入

- Desktop 的 ApplicationIcon 将 ICO 嵌入 EXE；MainWindow.Icon 为任务栏及窗口切换提供相同资源。
- WPF UI TitleBar.ImageIcon 使用 64 像素 PNG，窗口标题和自动化名称不变。
- TrayService 从应用基目录定位 Assets/Branding/app.ico；Windows 的 NotificationAreaIcon 按系统小图标尺寸加载，实例持有句柄，初始化失败及销毁时释放。Explorer 重建沿用现有重新注册路径。
- ICO 同时作为 WPF Resource 和构建／发布 Content，不依赖工作目录或仓库路径。未改动菜单、连接、关闭、退出及设备控制行为。

## 已执行检查

- 图标生成完成，原图角落 alpha 为 0；查看实际尺寸排列图，16～128 像素保持几何轮廓可辨。
- `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`：成功，0 警告、0 错误。
- 构建输出含 Assets/Branding/app.ico；WPF 资源及 XAML 接入通过编译。
- `git diff --check` 与 OpenSpec 严格校验通过。

未运行应用、测试、设备操作或 Publish，未提交 Git。实际任务栏、托盘、Explorer 重启、跨 DPI 与窗口标题栏截图尚未核验；编译和图像静态审阅不替代这些运行证据。安装快捷方式由后续发布流程创建，默认继承 EXE 图标。
## 本次授权的 UI 运行检查

2026-10-12 启动上述 Release EXE，检查标题栏、任务栏和系统通知区域。启动前核对本地设置：启动切主题及退出切副屏均关闭。窗口显示未连接、未选择设备，未点击检测、连接、模式或电源控制；启动自身的发现流程未建立设备控制会话。

实际观察：标题栏、任务栏和托盘均显示蓝灰几何 Logo，无系统默认图标；小尺寸轮廓可辨，托盘银灰细节较弱。当前运行界面仍为浅色，黑灰亮蓝全页面方案尚未实施。应用保持打开供用户查看。

截图为真实运行捕获，局部图仅裁剪、不重绘：

- [完整窗口](screenshots/logo-2026-10-12/window.png)
- [标题栏](screenshots/logo-2026-10-12/titlebar-logo.png)
- [任务栏](screenshots/logo-2026-10-12/taskbar-logo.png)
- [托盘](screenshots/logo-2026-10-12/tray-logo.png)

本次只覆盖当前桌面缩放下的静态图标显示；未检查跨 DPI、Explorer 重启或安装快捷方式，未执行设备验收及自动化测试。