# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

Octopus 是跨平台的终端管理与 AI 编码智能体工作台（Avalonia 12 + TKCode.Arc 1.3 + .NET 10），按项目组织终端、40 款命令行编码智能体、文件/Diff 查看器、Git、SSH 主机、远程设备互访与插件。桌面端发布为 NativeAOT 原生程序；仓库同时包含 Octopus.Mobile（Android/iOS 手机端，独立解决方案）。

## 常用命令

```bash
dotnet build Octopus.slnx                      # 桌面端整体构建（CI 用 -c Release）
dotnet test  Octopus.slnx                       # 全部单元测试
dotnet test tests/Octopus.App.Tests             # 只跑某一模块的测试
dotnet test tests/Octopus.App.Tests --filter "FullyQualifiedName~ShellTests"  # 单个测试类/方法
dotnet run --project src/Octopus.App            # 运行桌面端
dotnet run --project src/Octopus.Mobile.Desktop # 手机端的桌面预览壳
dotnet run --project tests/Octopus.Terminal.Smoke -- --script  # 真机终端冒烟测试（会弹窗，输出 SMOKE OK）
dotnet build Octopus.Mobile.slnx                # 手机端（需先 dotnet workload install android ios）
```

- SDK 版本由 `global.json` 固定（10.0.400，`rollForward: latestFeature`）。
- 所有 NuGet 包版本集中在 `Directory.Packages.props`：Avalonia 各包必须同一版本，TKCode.Arc 各包必须同一 Arc 版本；`xunit.v3` 被钉在 3.2.2（4.x 会让 Avalonia.Headless.XUnit 在运行期崩溃）。
- CI（`.github/workflows/ci.yml`）在 Linux/macOS/Windows 上执行 restore → `build -c Release --no-restore` → `test -c Release --no-build`。
- 发版：用户说"发版/打 tag"时遵循 `.agents/skills/publish-release/SKILL.md`（版本号 = `主.次.月×100+日[-preview.N]`，推 `vX.Y.Z` 标签触发 Release 工作流）。**推标签前必须让用户确认版本号**；`CHANGELOG.md` 顶部按版本记录变更。
- 图标资源由 `tools/generate-*.py` 从 `icon.png` 生成；更新清单签名见 `tools/make-update-manifest.py` / `make-update-key.sh`。

## 架构要点

### 分层与依赖方向

`src/` 下是按领域拆分的库，App 与 Mobile 是两个 UI 外壳：

- **Octopus.Core**：领域模型与接口定义（项目、设置、智能体、插件、远程），不依赖 UI。
- **Octopus.Storage**：`projects.json` / `settings.json` / 账户 / 插件状态的持久化，原子写入、损坏文件隔离（`IProjectStore`、`ISettingsStore` 等 `Abstractions.cs`）。
- **Octopus.Platform / .MacOS / .Linux / .Windows**：Shell 解析、`IAppPaths`、钥匙串（`ISecretStore`）、进程运行、路径比较等平台抽象。
- **Octopus.Terminal / .MiniPty / .Avalonia**：PTY 抽象（`ITerminalConnection`）、MiniPty 后端、终端控件与 `TerminalSessionRegistry`。
- **Octopus.Agents**：`Catalog/*.json` 内置智能体定义、扫描（`AgentScanner`）、命令编译（`AgentCommandCompiler`）、账户、会话、状态信号。
- **Octopus.Acp**：ACP（Agent Client Protocol）JSON-RPC 引擎、方言适配与会话宿主，支撑智能体的图形界面模式。
- **Octopus.FileSystem / Octopus.Git / Octopus.Ssh**：文件树与搜索、Git 状态/Diff/Worktree、`~/.ssh/config` 管理与隧道。
- **Octopus.Plugins / .Jaunt**：插件清单、权限授予、`Runtime/Modules/*` 宿主模块，Jaunt JS 运行时。
- **Octopus.Remote / .Host / .Client**：设备身份、加密通道；Host 端把本机工作区（终端、文件、ACP 对话）提供给配对设备，Client 端在本机镜像远端。
- **Octopus.Updates**：签名更新清单校验与自动更新状态机。
- **Octopus.App**：桌面端（Avalonia）；**Octopus.Mobile\***：手机端共享代码与 Android/iOS/Desktop 外壳。
- **Octopus.SignalForwarder**：独立的 `octopus-signal` 可执行文件，智能体钩子通过它向 App 上报运行状态。

### 组合根与模块注册（Octopus.App）

- `Composition/AppHost.cs` 是唯一的组合根：`AppHost.Services`（懒加载的 `IServiceProvider`）按序注册各层库，再调用 `AddOctopusApp()`；启动前就构建，因为首帧主题/语言来自已保存的设置。`AppHost.ConfigureOverrides` 供测试替换服务后重建容器；`AppHost.Reset()` 释放容器。
- `Composition/PlatformRegistration.cs` 是**全 App 唯一判断操作系统的位置**；其他代码只依赖 `Octopus.Platform` 接口。
- `Composition/AppRegistration.cs` 按功能区调用 `ShellModule` / `SettingsModule` / `ExplorerModule` / `PluginsAppModule` / `UpdatesAppModule` 的 `Register`。新增服务/视图模型时在其所属模块注册，不要散落在各处。
- 每个库用 `AddOctopusXxx()` 扩展方法注册自身服务（如 `AddOctopusStorage`、`AddOctopusAgents`、`AddOctopusAcp`、`AddOctopusFileSystem`）。

### UI 与标签页

- `AppSetup.UseArcApp()` 是 Avalonia/Arc 初始化的唯一入口（字体、Arc Blocks/Dock、主题），`Program.cs` 与测试宿主都从它开始。
- `Workspace/TabContentRegistry`（`ITabContentRegistry`）按项目项 id 懒创建内容控件（终端、文件预览、插件工作台、远程镜像终端、ACP 图形界面）；切换标签/分组/项目只换显示，不打断进程。`ProjectDockController` 基于 Arc Dock 管理标签组、拆分与布局持久化。
- ACP 能力且 `AgentSurface.Gui` 的智能体走 `AgentGui/` 图形界面；**没有 ACP 支持的智能体永远在终端中打开**（硬约束，见 `TabContentRegistry`）。
- 平台差异（窗口 chrome、快捷键）通过 `Shell/Chrome`、`Shell/Keymap` 的工厂在 ShellModule 中注入。

### 智能体定义（数据驱动）

- 内置智能体在 `src/Octopus.Agents/Catalog/<id>.json`（当前 40 个，`schema: 1`），描述探测方式、模型/推理强度/权限模式参数、恢复会话、环境变量、特性和信号钩子；`AgentCatalog` 把内置定义与用户 `agents/*.json` 和插件提供的定义合并。
- 新增或修改智能体通常只需改 JSON（含 `localized` 中文文案），启动参数由 `AgentCommandCompiler` 从 `capabilities` 生成，而不是在 C# 里拼命令行。

### 本地化

- `Localization/AppLangResourceKind.cs` 每个成员对应一条 UI 文本；Arc 的 Foundation 生成器把它与 `en-US.xlf`（源语言）、`zh-CN.xlf` 编译成 `AppLangResources.Get(...)` 和 XAML 的 `{l:AppLangResource ...}`；默认界面语言为简体中文。
- 新增文本必须同时添加枚举成员和**每个** xlf 文件的同 id `<unit>`，缺少会让构建报 `ARCF021`。

### 发布形态约束（NativeAOT）

Release + 指定 `RuntimeIdentifier` 时启用 `PublishAot`。反射式访问在 AOT 下不可用，因此 App 代码中：

- 用编译绑定（`AvaloniaUseCompiledBindingsByDefault=true`），不要反射绑定；
- XAML 用 `x:Class` 构造，不要按 URI 加载；
- JSON 用 `JsonNode` 重载或源生成的 `JsonSerializerContext`（如 `OctopusJsonContext`、`AgentJsonContext`），避免 `JsonValue.Create<T>` 处理非基础类型。

### 测试约定（Octopus.App.Tests）

- 测试跑在 Avalonia Headless + Skia 上，**不打开窗口**、测试串行执行。程序集级 `[assembly: FreshApp]` 让每个测试获得全新容器。
- `TestApp.BuildAvaloniaApp()` 通过 `AppHost.ConfigureOverrides` 替换为内存存储、临时数据目录（`TempAppPaths`，含假的 home）、假终端连接、假智能体扫描、假对话框、关闭更新器等；**测试绝不触碰真实用户的配置、钥匙串或网络**。插件与信号中枢仅在相关测试中显式启动。
- 新测试沿用 `[AvaloniaFact]`，需要干净状态时加 `[FreshApp]`。

### 环境变量

- `OCTOPUS_DATA_DIR`：独立数据目录，便于并行运行第二个实例。
- `OCTOPUS_DEV_PLUGINS`：以 PATH 分隔的开发插件目录（插件开发时热加载）。
- `-p:OctopusServiceUrl=...`：覆盖编译进程序的官方服务地址（默认 `https://octoterm.si`）。
