# 窗口工作区约束实现记录

日期：2026-10-09。关联 T-29、TC-29／TC-30，OpenSpec任务3.1、8.5，UI设计规范9节。

## 实现

Windows层 WindowWorkAreaAdapter 使用 MonitorFromWindow 的最近显示器策略、GetMonitorInfoW 的 rcWork、GetDpiForWindow 以及实际窗口／客户区矩形。原生句柄及P/Invoke仅位于Windows和Desktop边界，Core没有增加WPF或句柄依赖。矩形大小与DPI经过验证，原生失败保留错误，不能当作有效工作区。

Desktop的 WindowWorkAreaLifetime 在窗口首次加载、正常还原、隐藏后重新显示，以及 DPI／显示器／系统设置变化后，通过Dispatcher队列收敛一次工作区；连续通知合并，关闭后取消订阅及原生消息钩子。移动窗口只刷新当前显示器的最小尺寸，不强制阻止用户拖动。最大化／最小化期间不改窗口位置和大小，也不截断WPF系统消息处理。

最小尺寸由640×400 DIP客户区加上实际非客户区尺寸构成，最多为当前工作区的宽高；小工作区不会被固定640×400的旧限制撑出屏幕。普通窗口的物理矩形在当前rcWork内限位，保留Z序和激活状态。默认尺寸仍为1180×760 DIP；当前没有持久化窗口位置功能，不宣称恢复持久化位置已实现。

异常在桌面事件边界记录WindowWorkArea阶段并投影常驻中文提示与错误编号；本次没有增加设备、驱动或固件动作。

API语义依据：[MonitorFromWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromwindow)、[GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)、[多显示器定位](https://learn.microsoft.com/en-us/windows/win32/gdi/positioning-objects-on-multiple-display-monitors)。

## 验证范围

目标命令：

```powershell
dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore -p:OutDir=E:/Source/Repos/MCloud/USBDisplayHost/.artifacts/build-validation/window-workarea/
```

四生产工程构建成功，0警告、0错误。未创建测试代码或执行测试，未安装驱动或操作设备。

当前运行进程仍是上一轮ui-runtime输出；本轮workarea及此前Application.Resources紧凑边距修正尚未在新输出运行。首次小屏、托盘恢复、最小化还原、显示器断开、跨DPI移动、任务栏工作区变化、失败提示与关闭取消队列的实际验收仍未完成。父项3.1、8.5保持未完成。
