# 真实窗口截图与交互复核

日期：2026-10-15。范围：五页导航、紧凑窗口、滚动、键盘焦点与高 DPI。关联 T-29、TC-29、TC-30；本记录不代表完整产品或硬件验收。

## 环境与证据

运行正常 Release 输出，参数为 `--ui-only`。通过 Computer Use 的窗口截图、鼠标点击／拖动／滚动和键盘输入操作实际 Windows 窗口；本目录 23 张 JPEG 均为桌面工具原始截图，没有使用 `RenderTargetBitmap` 或设计图替代。22、23 为工具返回的菜单弹出区域截图。

本记录的 23 张截图来自 Windows 11 RDP 会话。工具报告常规窗口 1180×820、拖动后紧凑窗口 644×404、中等宽度窗口 860×820；尺寸是工具返回的截图范围，不用于推断实际 DPI。后续已在本地桌面完成 125%／150%／200% 实际缩放复核并恢复 100%，详见 [高 DPI 真实窗口复核](dpi-native-review-2026-10-15.md)。

## 已完成的实际操作

| 操作 | 观察与证据 |
|---|---|
| 鼠标依次进入五页 | 页头与选中态一致，未发现裁切；[设备](screenshots/ui-interaction-2026-10-15/01-devices-native.jpg)、[主题](screenshots/ui-interaction-2026-10-15/02-themes-native.jpg)、[设备设置](screenshots/ui-interaction-2026-10-15/03-device-settings-native.jpg)、[诊断](screenshots/ui-interaction-2026-10-15/04-diagnostics-native.jpg)、[应用设置](screenshots/ui-interaction-2026-10-15/05-application-settings-native.jpg) |
| 应用设置切换侧栏，再点击撤销更改 | 侧栏立即呈现，状态显示未保存；撤销后回到顶部导航，未保存设置；[侧栏截图](screenshots/ui-interaction-2026-10-15/06-side-navigation-native.jpg) |
| 拖动真实窗口到最小尺寸 | 自动切为页面菜单；应用设置底栏完整；[紧凑顶部](screenshots/ui-interaction-2026-10-15/07-settings-compact-top-native.jpg) |
| 滚轮及拖动实际滚动条 | 内容可滚动到软件更新分组，保存／撤销仍可见；[紧凑页尾](screenshots/ui-interaction-2026-10-15/08-settings-compact-bottom-native.jpg) |
| 展开菜单、按 Esc、按空格重新打开、按 Home | 五个入口可见；Esc 收起并显示菜单按钮焦点框；空格重新打开，Home 进入设备页并自动收起；[菜单](screenshots/ui-interaction-2026-10-15/09-compact-menu-native.jpg)、[设备页](screenshots/ui-interaction-2026-10-15/10-devices-compact-native.jpg) |
| 点击设备页“先预览主题”，选择时钟并拖动滚动条 | 进入主题页，时钟选中并显示实时预览，底栏可达；[模板选中](screenshots/ui-interaction-2026-10-15/11-themes-compact-top-native.jpg)、[时钟预览](screenshots/ui-interaction-2026-10-15/12-clock-compact-native.jpg) |
| 紧凑菜单进入设备设置并拖动到页尾 | 图像输出、亮度与固定操作区可达，没有出现新的裁切；[页首](screenshots/ui-interaction-2026-10-15/13-device-settings-compact-top-native.jpg)、[页尾](screenshots/ui-interaction-2026-10-15/14-device-settings-compact-bottom-native.jpg) |
| 紧凑菜单进入诊断并拖动到页尾 | 运行详情、导出／日志目录按钮、日志表均可访问，列头与数据对齐；[页首](screenshots/ui-interaction-2026-10-15/15-diagnostics-compact-top-native.jpg)、[页尾](screenshots/ui-interaction-2026-10-15/16-diagnostics-compact-bottom-native.jpg) |
| 主题文字色 Ctrl+A、键入 `#2563EB`、Tab，再点击撤销 | 实际输入更新了文本与蓝色色板勾选；撤销后恢复原始图片草稿，撤销按钮禁用；[键盘编辑](screenshots/ui-interaction-2026-10-15/17-theme-keyboard-color-native.jpg)、[撤销完成](screenshots/ui-interaction-2026-10-15/18-theme-undo-native.jpg)。此项不代表全流程仅键盘或读屏器验收 |
| 拖动到中等宽度并通过更多菜单切换诊断／应用设置 | 按钮文案与当前页一致，主要导航不残留旧选中；[诊断](screenshots/ui-interaction-2026-10-15/19-diagnostics-medium-native.jpg)、[应用设置](screenshots/ui-interaction-2026-10-15/20-settings-medium-native.jpg) |
| 修复后重新启动 Release，实际点击更多菜单、重复点击当前页、再切换另一页 | 当前页勾选可见；重复点击诊断仍保持勾选；切至应用设置后只有对应项勾选；可访问按钮名称同步当前页；[诊断勾选](screenshots/ui-interaction-2026-10-15/21-more-diagnostics-checked-native.jpg)、[重复点击](screenshots/ui-interaction-2026-10-15/22-more-current-page-repeat-native.jpg)、[应用设置勾选](screenshots/ui-interaction-2026-10-15/23-more-settings-checked-native.jpg) |

设备设置中的滑块与精确亮度均显示 50，读回仍明确为“尚未读回”；诊断列头与数据行对齐，未出现此前隐藏导航的蓝色残留。应用设置与主题草稿修改均已撤销，没有保存应用设置、操作硬件、编写或执行自动化测试、提交 Git。该 RDP 阶段未更改系统缩放；后续本地缩放操作及恢复结果单独记录。

## 本轮发现与修复

更多菜单虽已设置 `IsChecked`，实际控件却未显示当前页勾选。为两个入口启用 `IsCheckable`，并在每次菜单导航后同步当前页，包括重复点击同一页的情况；更多按钮的可访问名称补充当前页。修改限于 `MainWindow.xaml` 与 `MainWindow.xaml.cs`，没有改变页面业务或持久化设置。

修复后执行 `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`：0 警告、0 错误。21～23 为该构建的实际菜单复验；01～20 为本轮菜单修复前的实际截图，五页主体未在该次修复中修改。

`git diff --check` 与 `openspec validate build-usb-display-host-v1 --strict --no-interactive` 均通过。

## 高 DPI 后续结果与待验收范围

本记录采集时，Windows「设置 → 系统 → 屏幕」明确显示 **“无法从远程会话更改显示器设置。”**，缩放、分辨率和方向控件禁用。该限制已通过切换到本地 `console` 会话解除。

后续已通过系统显示设置完成 125%／150%／200% 下五页、紧凑窗口和控件交互，修复缩放触发的托盘注册错误，保存 31 张截图并恢复原始 100%。证据见 [高 DPI 真实窗口复核](dpi-native-review-2026-10-15.md)。混合 DPI 跨屏、系统文字放大、读屏器、高对比及真实设备端到端流程仍未覆盖，T-29／TC-29／TC-30 的完整验收保持未完成。
