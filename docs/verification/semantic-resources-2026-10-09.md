# 语义资源与主题覆盖实现记录

日期：2026-10-09。关联3.2、8.5，T-29、TC-29／TC-30；依据UI设计规范第6节。

## 资源与组合

新增ThemeOverrides.Light.xaml，集中声明规范中的15个浅色语义画刷：Canvas／Surface、主／次文字、Accent／OnAccent／Hover／Pressed、Selection、Border／ControlBorder、Success／Warning／Error及Focus。新增ThemeOverrides.HighContrast.xaml，以SystemColors的动态ColorKey映射相同键；进入高对比度时替换浅色覆盖，退出时恢复Light，不随系统深色偏好新增应用深色模式。

公共SemanticResources集中声明4／8／12／16／24／32间距值、36／32最小控件高度、20／16图标尺寸、6／8圆角、24／16／14／12文字样式以及双1 DIP轮廓组成的2 DIP焦点框（Focus与OnAccent），避免选中背景与焦点颜色相同。资源加载顺序为WPF UI ThemesDictionary、ControlsDictionary、公共语义资源、项目主题覆盖；Common样式通过DynamicResource读取当前画刷。紧凑PageContentMargin仍由Application.Resources覆盖，不依赖Window作用域。

当前实际接入：主窗口Canvas及主文字、页标题／正文、InlineMessage容器、模板／色板选中边框和键盘焦点、主题颜色输入及主要动作最小36高度、应用设置分组16字号。没有覆盖库交互模板。设备草稿／模板示意和色块内容保持实际内容颜色，不把管理UI高对比色映射成设备输出颜色。

主题服务在UI线程先完整加载候选库主题与项目覆盖，再按原索引替换两者；项目替换异常时恢复原库字典。资源加载失败保留原字典。Dispatcher回调异常记录ThemeResourcesChange并显示常驻错误编号，避免回调异常直接关闭应用。服务退出取消系统属性订阅，排队回调检查disposed。

## 编译与运行边界

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/semantic-resources/
```

最终四生产工程成功，0警告、0错误。中途一次增量构建报告缺少WPF生成源文件；仅对相同目标使用-t:Rebuild重新生成后通过，再次正常目标构建成功。没有创建或运行测试、没有设备／驱动／固件操作。

本轮观察到前台正被用户其他窗口占用后停止Computer Use输入，没有修改关闭行为或强制退出应用；当前仍运行旧ui-runtime输出。新增字典、字号、焦点、控件高度及色板／工作区最新源码尚无实际窗口证据。

尚未关闭完整语义资源验收：所有页面的零散间距、控件高度、主要动作与库Accent键映射、焦点覆盖、系统文字放大、动态高对比度／对话框切换及最小视口均需继续核对。3.2、8.5父项保持未完成。

## 锁定依赖路径修正

两路静态复核确认项目资源类型与加载顺序正确，但发现WPF-UI 4.3.0的ThemesDictionary将HighContrast拼成包内不存在的HighContrast.xaml。包Wpf.Ui.g.resources只读清单确认为Light、Dark、HC1、HC2、HCBlack、HCWhite，没有该别名。此前直接使用Theme=HighContrast的启动和切换路径已修正。

当前刷新SystemThemeManager缓存，按锁定官方映射选择实际HC1／HC2／HCBlack／HCWhite（未知高对比主题同官方回退HCWhite），以明确pack URI预加载ThemesDictionary.Source，不再调用有缺失路径的Theme setter。系统属性通知合并单个Dispatcher刷新；比较模式和资源URI，因此同为HighContrast时切换具体HC主题也可刷新。该修正通过目标Release，不代表已开启系统高对比度进行验收。

依据：[ThemesDictionary](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Markup/ThemesDictionary.cs)、[ApplicationThemeManager的HC映射](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Appearance/ApplicationThemeManager.cs)。

库Primary的Hover／Pressed和导航／选择底色尚未绑定项目精确令牌，其他页面主动作高度仍待接入；不能以字典存在声称这些交互状态已经符合规范。两色焦点轮廓的可见性也仍待高对比键盘截图。

修正后同一路只读复核确认缺失HighContrast.xaml路径缺陷已消除；两字典候选预加载、顺序保留、回滚、通知合并和退出门禁未发现新的明确源码缺陷。该结论限于源码及依赖资源清单，不是运行验收。

## 主按钮状态和高度接入

后续按锁定Button.xaml的实际动态键，为两个项目覆盖字典加入AccentButtonBackground／PointerOver／Pressed与AccentButtonForeground／PointerOver别名，分别引用同字典中的Accent／Hover／Pressed和OnAccent。库Primary模板可直接消费，未复制模板或覆盖ButtonBackgroundDisabled等禁用资源。依据：[锁定Button.xaml](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/Button/Button.xaml#L285)。

公共隐式ui:Button样式继承库DefaultUiButtonStyle，增加最小36 DIP高度及项目焦点样式；普通WPF Button继承DefaultButtonStyle，增加最小32 DIP工具高度及同一焦点样式。当前五页七个Primary动作无需逐个重写样式，原命令、条件可见性和禁用行为保留。只有MinHeight，不限制文字放大后的实际高度。

目标输出primary-button-resources，Desktop Release --no-restore构建四工程成功，0警告0错误。两路只读源码复核均未发现新增问题，并确认前一轮主按钮颜色令牌未消费及其他Primary未统一36高度的源码问题已处理。

本轮仍没有新窗口截图。普通／悬停／按下／禁用、焦点、高对比度切换与真实尺寸尚待实际窗口验证；导航／列表SelectionBrush映射和其他输入字段高度仍待核实，完整视觉结论保持未完成。未创建或执行测试，未操作设备。

## 导航／列表选中态及输入尺寸

后续按锁定ListBoxItem模板消费键，将普通前景、选中背景／前景分别映射到项目语义画刷。浅色选中使用SelectionBrush（#EAF0FD）与TextPrimary；高对比使用Highlight／HighlightText配对。保留ListBox的IsSelected、模板勾选、颜色勾选和原UIA选择语义，不以颜色替代选中标识。

导航同时接入常规LeftFluent和紧凑模板使用的选中背景键、文字及指示条键。锁定LeftFluent在选中时仍使用共用文字资源，因此高对比导航不把背景单独换成Highlight而留下WindowText；使用动态Window背景／WindowText前景，并保留库ActiveRectangle选中指示条。浅色导航背景使用SelectionBrush，指示条使用Accent。该配对是基于锁定模板的明确适配，不宣称实际焦点和四类高对比主题均已通过。

依据：[ListBoxItem](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/ListBox/ListBoxItem.xaml)、[NavigationLeftFluent](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/NavigationView/NavigationLeftFluent.xaml)。

WPF TextBox与ComboBox隐式样式继承库DefaultTextBoxStyle／DefaultComboBoxStyle，仅增加最小36 DIP及项目焦点；设备画质NumberBox局部接入同一最小高度。原文本校验、下拉／编辑／禁用模板、上下限与绑定均保留。依据：[TextBox](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/TextBox/TextBox.xaml)、[ComboBox](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/ComboBox/ComboBox.xaml)。

目标Release输出selection-resources，四工程成功，0警告0错误。尚无新窗口截图，实际选中底色、展开／折叠导航、焦点、弹出下拉、文字放大及高对比动态切换仍待验；不以构建替代验收。

两路只读复核均确认本轮选中画刷配对、展开／紧凑导航键和输入基础样式类型正确，未发现新的明确源码问题；运行视觉结论仍未通过，不据旧截图关闭3.2／8.5。

## 后续实际复验与修正

selection-resources真实截图发现根StaticResource别名未达到预期颜色；已改明确SolidColorBrush对象并通过目标Release。后续bounded-page-layout窗口已启动且导航浅蓝可见，主按钮／模板选中／动态高对比完整复验尚未完成。旧的源码复核结论不能替代实际呈现，见[页面与颜色修正记录](bounded-page-layout-2026-10-09.md)。
