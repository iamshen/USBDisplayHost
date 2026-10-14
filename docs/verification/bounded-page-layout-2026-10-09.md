# 页面滚动、颜色资源与时钟布局修正

日期：2026-10-09。关联3.1、3.2、6.6、8.5及TC-29／TC-30；仅记录本地主机软件证据。

## 实际发现

selection-resources输出的1180×760窗口已启动，窗口28646958、进程74100。模板布局示意、六色色板名称及勾选实际可见；应用设置主要动作完整。两路截图复核发现主题页底部动作被宿主裁切，模板／色板选中底色和Primary按钮仍为库系统蓝色。时钟草稿实际预览还显示日期与时间重叠。

证据位于忽略目录`.artifacts/visual-qa/2026-10-09/`：`themes-selection-resources.png`、`app-settings-selection-resources.png`、`clock-before-layout-fix.png`。两路结论为主题REVISE，仅应用设置当前截图未发现裁切；不代表紧凑、高对比、键盘或DPI通过。

## 实现修正

两个项目覆盖字典中供库使用的根StaticResource别名改为明确SolidColorBrush对象。浅色共享Color令牌并以DynamicResource绑定Color，高对比直接绑定相应SystemColors的ColorKey；保留库交互模板、禁用资源和主题字典替换机制。旧截图证明别名未达到预期，不把具体运行时资源对象类型写成已确认原因。

锁定WPF-UI将Page的ScrollViewer.CanContentScroll默认设为true，并据此选择包有DynamicScrollViewer的导航宿主模板。五页已有自身滚动容器或有限列表；根Page显式设置该属性为false，使用普通内容宿主，使页面的星号行获得有限高度，主体滚动与底部动作各自布局。没有增加固定页高或按钮坐标。

依据：[NavigationViewContentPresenter.cs](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/NavigationView/NavigationViewContentPresenter.cs)、[4.3.0宿主模板](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/NavigationView/NavigationViewContentPresenter.xaml)。源码和旧截图支持该布局原因，新版仍须实际截图复验。

ScenePainter的DrawText返回FormattedText实际高度；日期从时间顶部加实际时间高度及画布高度4%的间隔开始绘制，替代与时间字形重叠的固定48%纵坐标。保留颜色、时制／日期开关、像素尺寸、JPEG编码和草稿保存行为。

后续只读复核指出较长12小时制AM／PM文本可能换行。时间与日期现先测自然宽，超过画布92%宽时按比例缩小字号，限制为单行，再设置居中布局；日期仍跟随实际时间高度。当前页面预览1920×1080及活动帧双方向逻辑画布的这一换行路径已从源码消除，不宣称任意宽高比请求的通用高度自适应。

## 构建及重启证据

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/bounded-page-layout/
```

四生产工程成功，0警告、0错误。Desktop.dll SHA256：`2184437DF18D1FE2F65427CB8BAAE864D9D88F62374393E2884331CB8A7978A6`；Rendering.dll：`821ECC05F651F7E295FA34CA43C6CE9309A934E832D072B02BDC179FC930B8DB`。

按已有重启授权，旧应用关闭行为暂设退出应用，实际出现草稿确认且选择放弃并退出。随后查询未发现应用进程或窗口，再启动bounded-page-layout输出，实际窗口82972522；初次观察导航为浅蓝选中背景。恢复托盘偏好的操作遇到用户输入检测，停止窗口输入；临时退出偏好尚待恢复。没有保存本轮主题草稿、修改开机自启或执行设备操作。

目前尚无新版主题／时钟／紧凑完整截图；主按钮状态、模板选中、日期排列及footer修正不能据编译宣称验收。系统高对比、文字放大、DPI、辅助技术、真实设备及更新链路未验收。未创建或运行测试，未提交代码，相关父任务保持未完成。

后续观察检测到Chrome覆盖应用窗口，未继续输入。临时关闭偏好仍未恢复；下一次可操作时须先恢复收至托盘并保存，再继续主题复验。

最终单行修正目标输出为`.artifacts/build-validation/clock-single-line/`，相同Desktop Release --no-restore构建四工程0警告0错误。Rendering.dll SHA256：`F5DF709D076A7B7349D92E4481C1887558693672F300868A0389C084C4A3896A`。该输出尚未启动；当前进程52736仍为bounded-page-layout输出。两路源码复核确认明确画刷与滚动设置无新增明确问题，单行缩放关闭了长AM／PM换行风险；这些结论限于源码。

## 最新输出实际复验

2026-10-09，按正常重启且不保存旧草稿的授权，观察到旧窗口仍存在，正常关闭后再次查询确认其消失。通过 Computer Use 启动包含上述 UI 修正的`hid-overlapped-io` Release 输出，实际窗口24513846。Desktop.dll SHA256：`A69260D0AE90A799F51B475A1F4CE476624B4E29E4782DFBA08ADE9740216111`；Rendering.dll：`81680697778378DD37986ED4AA71EDC6A4857D4F2DBABAFA8DD029970920676C`。本轮未改生产代码，未重复构建。

在实际应用设置页选择“收至托盘”并保存，页面显示“已保存”，恢复了此前的临时关闭偏好。没有改变开机自启、设备选项或保存主题草稿。主题页1180×760截图显示浅蓝模板／色板选中底色、清晰边框及勾选；底部恢复默认／保存草稿完整。24小时及12小时当前上午文本和日期分行，无重叠；滚动到文字色色板后六个选项完整可达。

将窗口实际缩至1024×640后，三模板完整，预览移到正文下方；通过滚动条翻页可到达完整16:9预览、文字色色板和重试按钮，固定底部动作持续可见。最终窗口保持1024×640。后续观察到Chrome覆盖应用，停止窗口输入。

截图存放于忽略目录`.artifacts/visual-qa/2026-10-09/`：`app-settings-latest-restored.png`、`themes-latest-image.png`、`clock-latest-24h.png`、`clock-latest-12h.png`、`themes-latest-scrolled.png`、`themes-latest-compact-top.png`、`themes-latest-compact-preview.png`。两路只读复核对这七张截图所覆盖的修正范围均为PASS；没有批准的参考图，未计算像素相似度。这不代表全应用验收通过。

仍未覆盖下午／午夜、仪表盘最新预览、图片导入、640最小窗口、系统高对比、文字放大、多显示器／DPI、键盘／读屏器、保存故障、真实设备和更新链路。相关父任务保持未完成；未创建或运行测试，未提交代码。
