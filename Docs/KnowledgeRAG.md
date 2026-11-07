# EpicSpark 知识库与检索服务（RAG）

> 对应架构文档 [`ARCHITECT.md`](../ARCHITECT.md) 第 5 章「知识库」与 4.1 节「剧情 Agent 输入」。
> 本文是**主程序的对接契约**：接口字段以本文为准，`Tools/knowledge_rag` 为实现。

## 1. 目标

剧情 Agent 在开局与场景切换时需要「该历史事件的背景、经过、影响」等知识。
本服务把可编辑的结构化史实语料，变成**可检索、可重排、可拼进提示词**的上下文，
对外只暴露 HTTP 接口，Unity 侧通过 `KnowledgeClient` 调用。

关键决策：

| 决策 | 选择 | 理由 |
|---|---|---|
| 部署形态 | 本机/局域网独立进程（Python 标准库） | 单机游戏，不引入云依赖；Android 侧可跑在 PC 端或开发机上 |
| 依赖 | **零第三方依赖**（numpy 可选加速） | 与 `Assets/Scripts/AI` 的「零外部依赖」原则一致，避免打包与合规负担 |
| 检索 | 向量余弦 + BM25 → RRF 融合 → 重排 | 纯向量对专名/年代不敏感，纯 BM25 对同义表述不敏感；融合后两者互补 |
| 嵌入 | Qwen3-Embedding-8B（ai.gitee.com，OpenAI 兼容） | 架构文档指定；未配 Key 时自动降级本地向量，保证离线可开发 |
| 重排 | Qwen3-Reranker-8B（ai.gitee.com） | 架构文档指定；不可用时降级本地词汇重排 |
| 存储 | 扁平 float32 矩阵 + JSON 元数据 | 千级文本块下精确检索几毫秒，比 ANN 更简单、结果确定 |

## 2. 架构

```mermaid
flowchart LR
    U[用户输入<br/>"法国大革命"] --> S[Unity 主程序]
    S -->|POST /v1/knowledge/event| API[RAG HTTP 服务]
    subgraph RAG[Tools/knowledge_rag]
        API --> R[KnowledgeBase<br/>编排]
        R --> D[向量召回<br/>余弦 Top-N]
        R --> B[BM25 召回]
        D --> F[RRF 融合]
        B --> F
        F --> RR[重排<br/>Qwen3-Reranker-8B]
        RR --> C[块去重 + 上下文拼装]
    end
    C -->|event 卡片 + context| S
    S -->|提示词| P[剧情 Agent]
    DB[(data/events/*.jsonl<br/>结构化史实)] --> R
    R --> IDX[(data/index<br/>vectors.f32)]
```

## 3. 目录

```
Tools/knowledge_rag/
├── server.py                 # 服务入口（HTTP）
├── build_knowledge_base.py   # 校验语料 / 构建索引
├── query.py                  # 命令行检索（不启服务）
├── eval_retrieval.py         # 检索质量评估（hit@k / MRR）
├── rag/
│   ├── config.py             # 环境变量配置
│   ├── text_utils.py         # 归一化、中文二元分词、BM25、特征哈希向量
│   ├── corpus.py             # JSONL 加载/校验/切块/事件解析
│   ├── embedding.py          # Qwen3-Embedding-8B + 本地回退 + SQLite 缓存
│   ├── vector_store.py       # 向量索引读写与余弦检索
│   ├── reranker.py           # Qwen3-Reranker-8B + 本地回退
│   ├── retriever.py          # 检索编排（融合/重排/上下文/事件卡片）
│   └── http_api.py           # 路由与协议转换
├── data/events/*.jsonl       # 语料（唯一事实来源，可直接编辑）
├── data/index/               # 构建产物（不入库）
└── tests/test_rag.py         # 离线冒烟与契约测试
```

## 4. 快速开始

```powershell
# 0) 只用系统自带 Python 3.10+，无需 pip
$py = "python"

# 1) 校验语料
python Tools/knowledge_rag/build_knowledge_base.py --check

# 2) 离线构建索引（不调用任何 API）
python Tools/knowledge_rag/build_knowledge_base.py --embed-provider local

# 3) 启动服务（离线模式）
python Tools/knowledge_rag/server.py --embed-provider local --rerank-provider local

# 4) 冒烟
curl http://127.0.0.1:8787/health
curl -X POST http://127.0.0.1:8787/v1/knowledge/event ^
     -H "Content-Type: application/json" ^
     -d "{\"name\":\"法国大革命\"}"
```

接入 ai.gitee.com 的官方模型（默认即为此模式，只要给了 Key）：

```powershell
$env:EPICSPARK_RAG_API_KEY = "sk-xxxx"
$env:EPICSPARK_RAG_BASE_URL = "https://ai.gitee.com/v1"
python Tools/knowledge_rag/server.py
```

首次启动会用远端模型对所有文本块做一次向量化，结果写入
`data/cache/embeddings.sqlite3`；之后增删语料只对新增文本调用 API。

## 5. 语料

格式见 [`Tools/knowledge_rag/data/events/README.md`](../Tools/knowledge_rag/data/events/README.md)。
每个事件切成带上下文的 6—9 个文本块，块内保留「标题 + 年代 + 时代 + 地区」前缀，
使短块也能独立成义。语料指纹（chunk_id + 文本的 SHA-256）写入索引元数据，
**语料一改，索引自动判定为过期并在启动/重建时刷新**，避免"改了文档但检索没变"。

## 6. 检索算法

1. **事件解析与查询扩展**：先把用户输入映射到事件卡片（别名 / 包含 / 词项覆盖 / 编辑相似度，
   会先剥掉"的、了、是"等助词）。命中置信度 ≥ 0.6 时，把事件标准名并入检索词
   （例如「法国的革命」→「法国的革命 法国大革命」），弥补口语表述与标准名的字面差异。
2. **双路召回**：向量余弦 Top-`2×candidates`；BM25 Top-`2×candidates`。
3. **RRF 融合**：`score = Σ 1 / (k + rank)`，`k` 默认 60；两路权重各 1.0。
4. **重排**：取融合前 `rerank_top_n`（默认 24）块送 Qwen3-Reranker-8B；
   失败则退回融合序，不影响可用性。
5. **加权**：`final = 0.75 × rerank + 0.25 × normalized_rrf`；
   查询命中已收录事件（置信度 ≥ 0.9）时给该事件的块 `+0.05` 加成。
   只有参与重排的候选进入最终排序——未重排条目拿的是 RRF 分，量纲不同，混排会让弱条目压过强条目。
6. **相对阈值**：最终得分低于「最高分 × `relative_cutoff`（默认 0.45）」的条目直接丢弃，
   宁可少给也不拿无关内容填满 `top_k`。
7. **多样性**：单事件最多 2 块（`max_chunks_per_event`），不做轮转填空（那会塞进低分无关事件）。
8. **上下文拼装**：按字符预算拼接 `【标题｜年代｜章节】正文`（去掉块内重复的《标题》前缀），
   首块超预算时硬截断，保证送给 LLM 的文本不超预算。

## 7. 接口

服务默认 `127.0.0.1:8787`，全部返回 `application/json; charset=utf-8`。
错误统一为：

```json
{"error": {"code": "invalid_argument", "message": "query 不能为空", "status": 400}}
```

### GET /health

```json
{
  "status": "ok",
  "service": "EpicSpark Knowledge RAG",
  "version": "1.0.0",
  "events": 64,
  "chunks": 563,
  "index_ready": true,
  "embedding": {"kind": "openai-compatible", "model": "Qwen3-Embedding-8B", "dim": 4096, "degraded": false, "degraded_reason": ""},
  "reranker": {"kind": "cascade", "model": "Qwen3-Reranker-8B", "degraded": false, "degraded_reason": ""},
  "server_time": "2026-11-07T13:30:00+08:00"
}
```

`status` 为 `degraded` 表示某个模型不可用、已回退本地实现；`degraded` 字段给出原因。
游戏内不建议因降级而报错，但应在调试面板提示。

### POST /v1/knowledge/event —— 剧情 Agent 开局（主用）

请求：

```json
{"name": "法国大革命", "top_k": 6, "budget_chars": 2400}
```

响应（节选）：

```json
{
  "query": "法国大革命",
  "took_ms": 42.7,
  "matched": {"event_id": "fr_french_revolution", "title": "法国大革命", "confidence": 0.99, "matched_by": "exact"},
  "event": {
    "id": "fr_french_revolution",
    "title": "法国大革命",
    "aliases": ["大革命", "1789年革命"],
    "era": "世界近代史",
    "region": "法国/欧洲",
    "period": "1789—1799年",
    "start_year": 1789,
    "end_year": 1799,
    "one_liner": "推翻波旁王朝与封建等级制度的资产阶级革命。",
    "keywords": ["巴士底狱", "人权宣言", "雅各宾派"],
    "timeline": [{"year": 1789, "text": "攻占巴士底狱"}],
    "gameplay_hooks": ["以巴黎市民身份参与攻占巴士底狱"],
    "sources": [{"title": "人民网理论频道", "url": "http://theory.people.com.cn/..."}]
  },
  "evidences": [
    {"chunk_id": "fr_french_revolution##process", "section": "process", "section_label": "经过与关键节点", "text": "《法国大革命》（…）经过与关键节点：…", "score": 0.93}
  ],
  "context": "【法国大革命｜1789—1799年｜经过与关键节点】…\n【法国大革命｜1789—1799年｜背景与起因】…",
  "degraded": {}
}
```

未命中的历史事件不会报错：`matched` 与 `event` 为 `null`，但 `evidences` / `context`
仍返回通用检索结果，剧情 Agent 可以据此自由发挥（这也符合"允许用户输入任意历史事件"的设计）。

### POST /v1/knowledge/search

```json
{"query": "巴士底狱", "top_k": 5, "use_rerank": true, "filters": {"era": "世界近代史"}}
```

`filters` 支持：`era`（字符串或数组）、`region`（子串）、`year`（事件区间包含该年）、
`event_id`、`year_from`、`year_to`。响应含 `items[]`（每项带 `scores.dense/sparse/fused/rerank/final`）
与拼好的 `context`，便于调试排序质量。

### POST /v1/knowledge/context

只要上下文，不要元数据：`{"query": "...", "top_k": 8, "budget_chars": 1200}` →
`{"context": "...", "used_chars": 1187, "items": [...]}`。

### 其它

| 接口 | 说明 |
|---|---|
| `GET /v1/knowledge/stats` | 事件/块/时代分布、嵌入与重排信息、索引指纹 |
| `GET /v1/knowledge/events` | 全部事件摘要（可用于 UI 提示"已收录哪些事件"） |
| `GET /v1/knowledge/events/{id}` | 单事件全量卡片（含全部 chunk） |
| `GET /v1/knowledge/search?q=…` | 浏览器里快速试检索 |
| `POST /v1/knowledge/reindex` | `{"force": true}` 重建索引；配置了 `admin_token` 时需 `X-RAG-Token` |

## 8. Unity 集成

`Assets/Scripts/AI/Knowledge/`：

| 文件 | 说明 |
|---|---|
| `KnowledgeSettings.cs` | `ScriptableObject`：服务地址、超时、默认条数、上下文预算 |
| `KnowledgeModels.cs` | 与接口一一对应的 DTO（`JsonUtility` 可直接反序列化） |
| `KnowledgeClient.cs` | `HealthAsync` / `GetEventCardAsync` / `SearchAsync`，零依赖 |

```csharp
using EpicSpark.AI.Knowledge;

var client = new KnowledgeClient(settings);          // 复用，勿每次 new

// 开局：把用户输入变成知识上下文
var card = await client.GetEventCardAsync("法国大革命");
if (card.HasEvent)
{
    Debug.Log($"{card.event.title}｜{card.event.period}｜置信度 {card.matched.confidence:F2}");
    // 直接把 card.context 拼进剧情 Agent 的 system/user 消息
    // card.event.timeline / gameplay_hooks 可作为剧本与场景生成的骨架
}
else
{
    Debug.LogWarning("未收录该事件，使用通用检索结果：" + card.context);
}

// 过程中：NPC 或场景追问
var hits = await client.SearchAsync("巴士底狱陷落后民众做了什么", topK: 5);
foreach (var item in hits.items)
    Debug.Log($"{item.title} / {item.section_label} / {item.scores.final:F3}");
```

注意：`JsonUtility` 不做 null 保护，`matched` / `event` 未命中时为 `null`；
`KnowledgeEventCard.HasEvent` 已封装该判断。

## 9. 配置项

全部可用环境变量覆盖（前缀 `EPICSPARK_RAG_`），也可用命令行参数覆盖环境变量。

| 变量 | 默认 | 说明 |
|---|---|---|
| `HOST` / `PORT` | `127.0.0.1` / `8787` | 监听地址；`0.0.0.0` 可供 Android 真机访问 |
| `BASE_URL` | `https://ai.gitee.com/v1` | OpenAI 兼容端点 |
| `API_KEY` | 空 | 为空则自动使用本地回退 |
| `EMBED_MODEL` | `Qwen3-Embedding-8B` | 嵌入模型 |
| `RERANK_MODEL` | `Qwen3-Reranker-8B` | 重排模型 |
| `EMBED_PROVIDER` / `RERANK_PROVIDER` | `auto` | `auto` / `openai` / `local` |
| `LOCAL_EMBED_DIM` | `1024` | 本地向量维度 |
| `EMBED_DIMENSIONS` | `0` | 远端降维参数，0 表示不传 |
| `RERANK_PATHS` | `/rerank,/rerankings` | 重排路径候选，自动探测 |
| `TOP_K` / `CANDIDATES` / `RERANK_TOP_N` | `8` / `40` / `24` | 检索参数 |
| `CONTEXT_BUDGET_CHARS` | `2400` | 上下文字符预算 |
| `RELATIVE_CUTOFF` | `0.45` | 相对得分阈值（低于最高分该比例的结果丢弃） |
| `MAX_CHUNKS_PER_EVENT` | `2` | 单事件最多块数 |
| `ADMIN_TOKEN` | 空 | 管理接口令牌 |
| `AUTO_REINDEX` | `1` | 语料/模型变化时自动重建 |
| `DATA_DIR` / `INDEX_DIR` / `CACHE_DIR` | `data/` | 语料、索引、向量缓存 |

## 10. 验证

```powershell
$py = "C:\Users\wangr\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"  # 任意 3.10+
& $py -m unittest discover -s Tools/knowledge_rag/tests -t Tools/knowledge_rag -v

# 检索质量（30 条查询：标准名 / 口语别称 / 主题式提问）
& $py Tools/knowledge_rag/eval_retrieval.py --verbose
```

测试覆盖：分词/BM25/哈希向量确定性、年份解析、语料结构与必需字段、索引读写往返、
混合检索排序、事件解析与别名、过滤、上下文预算、HTTP 健康检查与四个检索接口、
400/404 错误契约。全部离线，不消耗 API 额度。

**当前基线**（64 个事件 / 563 个文本块，离线向量 + 本地重排，30 条查询集）：

| 指标 | 结果 |
|---|---|
| hit@1 / hit@3 / hit@5 | 100% / 100% / 100% |
| 事件解析命中（首位） | 100% |
| MRR | 1.000 |
| 平均耗时 | 约 6.5 ms/query |

> 该查询集偏"专名可信"：本地特征哈希向量本质是词面匹配，换成
> 无共同字面的说法（如「国王被送上了断头台」）会依赖事件解析与常识,
> 生产环境请接 Qwen3-Embedding-8B。

## 11. 降级与运维

- **远端不可用**：`embedding`/`reranker` 的 `degraded_reason` 会记录原因，服务继续可用；
  修复 Key 或网络后 `POST /v1/knowledge/reindex {"force": true}` 即可切回远端向量。
- **索引与配置不一致**（换了嵌入模型/维度）：`AUTO_REINDEX=1` 时自动重建；
  生产环境可设 `0`，让服务启动即报错，避免悄悄改变检索行为。
- **语料热更新**：`POST /v1/knowledge/reindex {"force": false}` 重新读语料并复用向量缓存。

## 12. 已知边界 / 后续可做

- 语料是**人工整理的课程级事实**，不是全量史料库；未收录事件走通用检索，不会编造条目。
  当前收录 64 个事件（中国史 30 / 世界史 34），覆盖中学历史主干。
- 本地回退向量是特征哈希，本质是词面匹配：实测「国王被送上了断头台」这类**无共同字面**的
  说法会落到相关但不同的事件上（如命中英国资产阶级革命而非法国大革命）。
  离线开发够用，生产务必接 Qwen3-Embedding-8B；它也支持用 `EMBED_DIMENSIONS` 降维省钱。
- 远端重排接口路径按 `POST {base}/rerank` 实现，并内置 `/rerank,/rerankings` 自动探测；
  若你的网关路径不同，改 `RERANK_PATHS` 即可，无需改代码。
- 未实现：中文分词词典（专名更准）、增量索引（目前整库重建，千级块 < 1s）、
  多模态知识（图片/遗址）、按玩家年级做难度分层、多语言语料。
