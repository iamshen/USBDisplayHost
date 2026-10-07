# Repository Guidelines

## Project Structure & Module Organization

USBDisplayHost is currently a design-stage Windows USB display application; no solution, source projects, tests, or application assets exist yet.

- `README.md` summarizes scope and links the documentation.
- `docs/01_需求分析.md` through `docs/06_UI设计规范.md` define requirements, behavior, architecture, tasks, engineering rules, and UI design.
- `docs/参考资料/` contains original references; preserve them unchanged.
- `openspec/changes/build-usb-display-host-v1/` contains the implementation proposal, design, capability specs, and task checklist.
- Planned `src/UsbDisplayHost.{Desktop,Core,Windows,Rendering}/` projects separate WPF/MVVM, platform-independent logic, native adapters, and offscreen rendering. The design proposes MSTest projects under `tests/UsbDisplayHost.<Module>.Tests/`; creating or running tests requires explicit user authorization.

Keep Core independent of WPF and native handles. Windows and Rendering depend on Core; Desktop composes all three.

## Build and Development Commands

Run from the repository root:

- `git diff --check`: check tracked changes for whitespace errors.
- `openspec validate build-usb-display-host-v1 --strict --no-interactive`: validate the current specifications; requires the OpenSpec CLI.

Application commands are **planned**, pending engineering-baseline tasks 2.1–2.3 and a Windows machine with the pinned .NET 10 SDK:

- `dotnet restore src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj --locked-mode`: restore locked dependencies once the project and lockfiles exist.
- `dotnet build src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj -c Release --no-restore`: compile the affected project; replace the path with the relevant project.
- `dotnet run --project src/UsbDisplayHost.Desktop`: launch the WPF application.

Document verified commands in README when scaffolding lands. Ordinary builds must not run tests, open devices, or install drivers.

## Coding Style & Naming Conventions

For new C#/XAML files, use four-space indentation. Use PascalCase for types/public members, camelCase for locals, and the `Async` suffix for asynchronous methods. Use bindings and commands; keep native calls outside ViewModels. Enable nullable references and .NET analyzers during scaffolding; no formatter or `.editorconfig` is configured yet.

Keep project documentation in Chinese and preserve REQ/FR/T/TC traceability when changing behavior.

## 测试与验证

除非用户主动明确允许，严禁编写任何单元测试、测试用例及测试代码，严禁主动执行或运行任何测试。优先仅对受影响项目执行目标构建与编译验证。

现有文档、OpenSpec 任务或验收清单中的测试要求不构成用户授权；未获授权的测试保持未执行状态。获准执行硬件测试时，必须显式选择环境和设备。

## 提交、安全与交付

开发分支使用 `feature/<short-name>`。提交使用简洁的 Conventional Commits，例如 `feat(recycle): 增加取货交接记录`、`fix(auth): 限制跨商户访问`；实际 scope 应对应目标仓库模块。每个仓库单独提交；PR 写明业务影响、涉及仓库、关联工作项/OpenSpec、验证命令与结果，UI 改动附目标视口的实际 WPF 截图。协议改动同时更新字段说明和匹配固件版本。保留无关工作区改动。

不得提交或复述客户端密钥、令牌、证书密码、测试账号密码及生产配置。敏感值使用目标仓库既有环境变量、Secret、Secure Files 或安全存储链路。用户明确提供并授权保存的本地验收登录凭据，仅可按授权范围存放于本地，并确保不进入版本控制。

不要把本地构建、静态检查或模拟数据结果表述为生产部署、真实设备或端到端验收成功。

不得自行提交，所有提交应该交由用户自行执行。
