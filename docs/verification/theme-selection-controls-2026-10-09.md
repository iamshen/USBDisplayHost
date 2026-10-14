# 主题选择与颜色控件实现记录

日期：2026-10-09。关联任务6.6、8.5，T-16／T-29，TC-09／TC-29／TC-30；依据UI设计规范第4节。

## 实现范围

三模板选项增加96×54（16:9）的本地矢量布局示意，保留原ListBox／SelectedTemplate选择语义。选中项显示系统高亮边框和可见勾选，各项明确绑定中文自动化名称。示意控件不抢键盘焦点，时钟和仪表盘文字至少12 DIP；布局示意不代表实时指标或活动画面，真实草稿预览仍通过既有独立渲染链路呈现。

新增共享ColorPalette控件，用六个带中文名称的可见色块编辑背景色和文字色。色板使用ListBox、换行面板、边框和勾选，不只通过颜色指示选中。SelectedColor默认双向绑定既有ViewModel字段；明确选择使用SetCurrentValue保留原绑定，随后沿用字段变更、输入校验和preview.Request。自定义或无效十六进制文本取消色板匹配但不被覆盖；已有文本框和校验入口保留。未新增设备写入、保存、重置或协议行为。

模板及色板容器样式继承锁定WPF-UI 4.3.0的ListBoxItem隐式样式，避免覆盖模板。资源键已由只读复核定位到官方锁定提交：[ListBoxItem资源](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/ListBox/ListBoxItem.xaml#L47)。

## 源码复核和构建

visual-qa两路只读源码复核发现缩略文字过小及模板项自动化名称缺失，两项均已修正并重新构建。绑定写回、无效输入保留和640宽度的源码布局未发现确定缺陷。没有这些新控件的运行截图，静态结论不替代视觉或辅助技术验收。

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/theme-controls/
```

四生产工程成功，0警告、0错误。首次构建因ThemeKind属于Desktop.ViewModels而非Core.Themes导致XAML类型解析失败，已修正到实际命名空间并完成最终成功构建。未创建测试代码、执行测试或提交。

## 待验

当前运行应用仍是ui-runtime构建，最新theme-controls输出尚未启动。模板边框／勾选、色板选择及字段同步、校验期间输入保留、预览刷新、键盘焦点／箭头选择、高对比度、文字放大、窄窗口色板换行与滚动仍须新窗口证据。不得使用旧截图签署这些新控件，6.6与8.5父项保持未完成。

## 后续实际截图

三个模板示意与六色色板中文和勾选已在selection-resources实际截图可见；两路复核发现蓝底选中和主题footer裁切，实际时钟预览日期重叠。明确画刷、五页有限宿主和日期按字形高度排列已通过目标Release，最新主题截图仍待复验，见[页面与颜色修正记录](bounded-page-layout-2026-10-09.md)。
