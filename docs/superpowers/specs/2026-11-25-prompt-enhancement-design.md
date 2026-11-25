# 提示词增强：独立 AI 接口列表 + GUI 输入区增强按钮 — 设计文档

日期：2026-11-25

## 意图与为什么

在 ACP 图形界面（GUI）的输入卡片发送区，紧邻发送按钮前方增加一个「提示词增强」图标：点击后由用户在独立设置页配置的 AI 接口改写输入内容，回填供确认后自行发送。接口配置与编码智能体的模型/凭据解耦，并以列表支持多个 provider 并存。

## 目标

- 新增设置页「提示词增强」（设置 → AI 分组），列表管理多个接口（名称、协议、地址、密钥、模型、温度、最大 Token、系统提示词）。
- 列表包含内置虚拟条目「跟随智能体接口」，作为零配置默认；每个接口可设为默认，默认接口在顶部下拉选择。
- GUI 输入区：按钮位于发送按钮前，使用默认接口；右键菜单切换接口，写入默认（所有标签页生效）。
- 输入为空禁用；无可用接口禁用并说明原因；请求中显示 spinner，再次点击取消。
- 结果回填、不自动发送；失败保留原文；用户在请求期间改了文本则不覆盖。
- 不影响智能体模型/凭据/扫描与 ACP 启动等现有 AI 功能。

## 非目标

- 不自动发送；不参与图片附件。
- 多接口轮询 / 失败自动切换（由用户手动切换）。
- 每项目/每智能体覆盖。

## 约束

- 密钥只进系统钥匙串：`service = "Octopus/prompt-enhancement"`，`name = endpoint.Id`；永不写入 `settings.json`、日志、tooltip。
- NativeAOT：`JsonNode` 构造请求体；持久化类型在 `AppSettingsModel` 的源生成模型图内。
- 新 UI 文本需 `AppLangResourceKind` 成员 + en-US/zh-CN xlf 同 id `<unit>`。
- 默认语言 zh-CN。

## 方案（选定）

| 方案 | 取舍 |
|------|------|
| A 复用智能体会话新回合 | 污染上下文、慢、智能体忙碌时不可用 |
| B App 直连 + 单配置 | 干净但扩展性不足（用户否决） |
| **B' App 直连 + 多接口列表 + 内置“跟随会话”虚拟项** | 选定 |

**数据模型**（`Octopus.Core/Settings/PromptEnhancementSettings.cs`）

```csharp
PromptEnhancementSettings { List<PromptEnhancementEndpoint> Endpoints; string? DefaultEndpointId; }
PromptEnhancementEndpoint { Id; Name; Protocol("anthropic"|"openai"|null); BaseUrl; Model; Temperature?; MaxTokens?; SystemPrompt?; }
PromptEnhancementSources { SecretService; SessionEndpointId; Anthropic; OpenAi; IsKnownProtocol }
```

- `DefaultEndpointId` 为 null 或 `"session"`：使用「跟随智能体接口」。
- 规范化：剔除空/重复 Id，去 BaseUrl 尾斜杠，钳制 Temperature 0–2、MaxTokens 64–8192，默认指向不存在端点时归 null。

## 解析与优先级

- 端点列表 = 「跟随智能体接口」（虚拟） + 用户端点。
- 会话条目：Claude 系方言（claude/codebuddy）用 `ANTHROPIC_BASE_URL`/`ANTHROPIC_API_KEY`/`ANTHROPIC_AUTH_TOKEN`（token 走 Bearer），模型取会话选中模型或 `ANTHROPIC_*_MODEL`；Codex 方言用 `OPENAI_BASE_URL`/`OPENAI_API_KEY`；其它方言 `UnsupportedDialect`。
- 用户端点：Protocol 缺省 anthropic；BaseUrl 空 → `MissingBaseUrl`；钥匙串无密钥 → `MissingKey`；模型空用协议默认（`claude-sonnet-5-5` / `gpt-5.5`）。
- **不静默降级**：默认端点不可用则按钮禁用并说明缺什么。

## 请求层

- Anthropic Messages：`POST {base}/v1/messages`，`anthropic-version: 2023-06-01`，`x-api-key` 或 Bearer；响应取 `content[*].text`。
- OpenAI Chat Completions：`POST {base}/v1/chat/completions`，Bearer；响应取 `choices[0].message.content`。
- 60s 超时；非 2xx 抛 `PromptEnhancementException`（含状态码与 300 字正文）；空结果视为失败；去除整体代码围栏与成对引号。

## UI

- 设置页：默认接口下拉 + 端点卡片（名称/协议/地址/模型/密钥/温度/最大 Token/系统提示词/测试/删除）+ 添加接口。
- 输入区：`[Stop] [立即发送] [✨] [发送]`；`Classes="icon" Variant="Ghost"`；请求中 ArcSpinner；右键菜单在端点间切换默认。

## 测试

Storage 往返与规范化；`PromptEnhancer` 端点解析/两种协议请求/错误/取消；设置 VM 增删改与钥匙串；GUI VM 回填、失败保留、并发编辑不覆盖、取消、缺密钥禁用、切换默认；视图按钮位置。

## 遗留决定

- 端点名称可空（显示「未命名接口」）。
- 「跟随智能体接口」始终显示，不可用时禁用并说明。