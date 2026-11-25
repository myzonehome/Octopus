# 提示词增强（独立 AI 接口列表 + GUI 增强按钮）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 ACP GUI 输入区加入「提示词增强」按钮，调用用户在独立设置页配置的提示词增强接口列表中的默认端点，改写输入并回填。

**Architecture:** `PromptEnhancementSettings` 挂在 `AppSettingsModel`（元数据入 settings.json，密钥入 `ISecretStore`）；`Octopus.Agents` 的 `PromptEnhancer` 负责端点解析与 Anthropic/OpenAI 协议调用；App 层设置页与 GUI 按钮只依赖 `IPromptEnhancer`。

**Tech Stack:** .NET 10 / C#、Avalonia 12 + TKCode.Arc、CommunityToolkit.Mvvm、System.Text.Json（源生成 + JsonNode）、xunit.v3。

**Spec:** `docs/superpowers/specs/2026-11-25-prompt-enhancement-design.md`

## Global Constraints

- 密钥只进 `ISecretStore`（`service = "Octopus/prompt-enhancement"`，`name = endpoint.Id`），不写入 settings.json / 日志 / tooltip。
- NativeAOT：请求体用 `JsonObject`；新增持久化类型在 `OctopusJsonContext` 的模型图内。
- 新 UI 文本必须同时加 `AppLangResourceKind` 成员与 en-US、zh-CN 两个 xlf 的同 id `<unit>`，否则构建报 `ARCF021`。
- 不修改 `AgentSettings`、`ThirdPartyModelDiscovery`、ACP 启动路径。

## Review Focus

- 默认端点缺密钥时必须禁用并说明缺什么，不能静默回落。
- 密钥不得出现在日志、tooltip、异常消息、settings.json。
- 「跟随智能体接口」在订阅登录（无 API key）时显示不可用原因。
- 取消后原文原样保留、不显示错误。
- 请求期间用户改了文本则结果不覆盖。

---

### Task 1: 设置模型 + 规范化 + 序列化往返

**Files:** `src/Octopus.Core/Settings/PromptEnhancementSettings.cs`（新建）、`AppSettingsModel.cs`、`src/Octopus.Storage/SettingsNormalizer.cs`、`tests/Octopus.Storage.Tests/StorageTests.cs`

**Produces:** `PromptEnhancementSettings`、`PromptEnhancementEndpoint`、`PromptEnhancementSources`（含 `SecretService`）。

- [x] 测试：空/重复 Id 剔除、BaseUrl 去尾斜杠、坏默认清空；缺 section 时默认空。
- [x] 实现规范化并运行 `dotnet test tests/Octopus.Storage.Tests`（39/39）。
- [x] 提交 `feat(settings): add prompt enhancement endpoint list model and normalization`

### Task 2: 增强引擎（端点解析 + 协议请求）

**Files:** `src/Octopus.Agents/PromptEnhancement/PromptEnhancer.cs`、`AgentsServiceCollectionExtensions.cs`、`tests/Octopus.Agents.Tests/PromptEnhancerTests.cs`

**Produces:** `PromptEnhancementEndpointStatus`、`PromptEnhancementEndpointView`、`PromptEnhancementException`、`IPromptEnhancer{ListAsync,ResolveDefaultAsync,EnhanceAsync}`。

- [x] 测试：会话端点环境解析、缺密钥、方言不支持；自定义端点缺密钥不回落；默认解析顺序；两种协议请求体/头；围栏与引号清理；空结果；非 2xx；参数覆盖；取消。
- [x] 实现并运行 `dotnet test tests/Octopus.Agents.Tests`（265 通过）。
- [x] 提交 `feat(agents): add prompt enhancer with endpoint list resolution and protocol clients`

### Task 3: 设置页 VM + 本地化成员

**Files:** `src/Octopus.App/Settings/PromptEnhancement/*ViewModel.cs`、`SettingsModule.cs`、`Localization/*`、`tests/Octopus.App.Tests/PromptEnhancementSettingsTests.cs`

**Produces:** `PromptEnhancementSettingsViewModel`（Endpoints、DefaultChoices、DefaultEndpointId、AddCommand）、`PromptEnhancementEndpointViewModel`（字段即存、密钥、测试、删除）。

- [x] 测试：首端点自动设默认；字段保存且密钥只进钥匙串；删除清默认并删钥匙串；测试连接成功/失败。
- [x] 实现并运行（5/5）。本地化清单（43 条）同步加入枚举与两个 xlf。
- [x] 提交 `feat(settings): add prompt enhancement settings view model and strings`

### Task 4: 设置页视图 + 导航

**Files:** `PromptEnhancementPageView.axaml(.cs)`、`SettingsView.axaml`、`SettingsViewModel.cs`、`SettingsSectionViewModel.cs`

- [x] 测试：导航含 `prompt-enhancement`，页面 VM 类型正确，页面视图可构造。
- [x] 实现（`Ai` 分组 + `Sparkles` 图标 + DataTemplate）并运行（6/6）。
- [x] 提交 `feat(settings): add prompt enhancement settings page`

### Task 5: GUI VM 增强命令

**Files:** `src/Octopus.App/AgentGui/AgentGuiTabViewModel.cs`、`Workspace/TabContentRegistry.cs`、`tests/Octopus.App.Tests/TestApp.cs`、`PromptEnhancementGuiTests.cs`

**Produces:** `IsEnhancingPrompt`、`PromptEnhancementError`、`CanEnhancePrompt`、`EnhancePromptTooltip`、`EnhancementEndpoints`、`EnhancePromptCommand`、`SetDefaultEnhancementEndpointCommand`、`PromptEnhancementEndpointOption`。

- [x] 测试：空输入禁用、回填；失败保留原文；并发编辑不覆盖；再次点击取消；缺密钥禁用并给原因；切换默认。
- [x] 实现（会话 `Ready` 后解析端点，监听设置变更）并运行（6/6）。
- [x] 提交 `feat(agent-gui): add prompt enhancement command to composer`

### Task 6: GUI 视图按钮 + 右键切换

**Files:** `AgentGuiTabView.axaml`、`AgentGuiTabViewModel.cs`（选项带 `SelectCommand`）、`PromptEnhancementGuiTests.cs`

- [x] 测试：按钮位于 SendButton 之前、空输入禁用、有输入启用。
- [x] 实现（图标 + spinner + 右键菜单）并运行（13/13）。
- [x] 提交 `feat(agent-gui): show prompt enhancement button with endpoint menu in composer`

### Task 7: 全量回归 + CHANGELOG

- [x] `dotnet build Octopus.slnx`（0 错误）；`dotnet test Octopus.slnx`（仅 4 个基线既有失败：FileSystem 符号链接 ×3、PluginScreenshots ×1，均在 a19c358 复现）。
- [x] CHANGELOG 顶部新增「未发布」条目。
- [x] 提交 `docs(changelog): note prompt enhancement button and endpoint settings`