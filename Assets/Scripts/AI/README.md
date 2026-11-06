# EpicSpark.AI — OpenAI 兼容端点脚手架

零外部依赖, 纯 `UnityEngine.Networking` (UnityWebRequest) 实现, 适配 Unity 2022.3。

## 文件结构

| 文件 | 说明 |
|---|---|
| `OpenAI/OpenAISettings.cs` | `ScriptableObject` 配置: Base URL / API Key / 模型名 / 超时 |
| `OpenAI/OpenAIModels.cs` | 请求/响应 DTO + 手工 JSON 构造器 |
| `OpenAI/OpenAIClient.cs` | 客户端: Chat(非流式/流式) / ListModels / Embeddings |
| `OpenAI/StreamingDownloadHandler.cs` | SSE 流式数据处理器 |
| `OpenAI/OpenAITester.cs` | 运行时冒烟测试组件 |
| `Editor/OpenAISettingsEditor.cs` | 配置资源的 Inspector 扩展 (编辑器内 Ping 测试) |

## 快速开始

1. **创建配置资源**
   Project 窗口右键 → Create → EpicSpark → OpenAI Settings,
   填入:
   - Base URL: `http://127.0.0.1:1234/v1` (LM Studio 本地端点)
   - API Key: 留空 (本地端点无需鉴权) 或填入 `sk-xxx`
   - Model: `qwen/qwen3-coder-30b` (以 `/v1/models` 返回为准)

2. **编辑器内测试**
   选中该资源, Inspector 底部点 **Ping** 按钮, 确认连通。

3. **运行时测试**
   场景任意物体挂 `OpenAITester`, 把 Settings 资源拖入 `settings` 字段, Play。

## 代码使用示例

```csharp
using EpicSpark.AI.OpenAI;

// 构造 client (复用, 勿每次 new)
var client = new OpenAIClient(settings);

// 1. 列出模型
var models = await client.ListModelsAsync();

// 2. 非流式聊天
var messages = new List<ChatMessage> {
    ChatMessage.System("你是一个历史老师。"),
    ChatMessage.User("解释一下法国大革命")
};
var resp = await client.ChatAsync(messages,
    options: new ChatOptions { temperature = 0.7f, maxTokens = 512 });
string answer = resp.choices[0].message.content;

// 3. 流式聊天 (逐 token 回调)
var sb = new StringBuilder();
await client.ChatStreamAsync(messages, onDelta: (delta, role, finish) => {
    if (!string.IsNullOrEmpty(delta)) sb.Append(delta);
});

// 4. 向量化 (需要端点支持 embedding 模型, 如 ai.gitee.com 的 Qwen3-Embedding-8B)
var emb = await client.CreateEmbeddingAsync("山顶洞人");
float[] vector = emb.data[0].embedding;

client.Dispose();
```

## 设计决策记录

### 为什么不直接用第三方库?

| 库 | 问题 |
|---|---|
| `OpenAI` (官方 .NET SDK) | 依赖 .NET 6+ / `System.ClientModel`, Unity 2022.3 的 .NET Standard 2.1 兼容层无法直接使用 |
| `Newtonsoft.Json` | 与 `UnityEngine.JsonUtility` 并存会增加包体与序列化行为不一致风险; 当前 DTO 简单, `JsonUtility` 足够 |
| `RestSharp` / `Flurl` | Unity WebGL / Android IL2CPP 裁剪兼容性差, UnityWebRequest 是官方推荐 |

**结论**: 封装层极薄 (~300 行), 无依赖, 便于后续按需替换为带 Function Calling 的完整实现。

## 下一步 (未实现, 按需补充)

- [ ] Function Calling / Tool Call (剧情 Agent、场景 Agent 的工具调用依赖)
- [ ] 结构化输出 (response_format: json_schema)
- [ ] 多模态输入 (image_url / audio)
- [ ] 重试 / 退避策略 / 限流
- [ ] 对话历史持久化

## 已验证的端点行为 (LM Studio v0.x, http://127.0.0.1:1234/v1)

- `GET /v1/models` → 返回模型列表
- `POST /v1/chat/completions` → 非流式 / 流式 (SSE) 均正常
- `POST /v1/embeddings` → **不支持** (未加载 embedding 模型时返回 400 "No models loaded")
- 鉴权: 本地端点无需 API Key, 请求头中不带 `Authorization` 也能正常响应
