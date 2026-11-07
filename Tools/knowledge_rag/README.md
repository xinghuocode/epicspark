# EpicSpark 知识库检索服务

给剧情 Agent 提供历史知识的 RAG 服务：**双路召回（向量 + BM25）→ RRF 融合 → Qwen3 重排 → 提示词上下文**。
零第三方依赖（numpy 可选加速），没有 API Key 也能完全离线跑通。

接口契约、算法与 Unity 集成说明见 [`Docs/KnowledgeRAG.md`](../../Docs/KnowledgeRAG.md)。
语料格式见 [`data/events/README.md`](data/events/README.md)。

## 30 秒上手

```powershell
$py = "python"   # 任意 Python 3.10+

# 校验语料（不联网）
& $py build_knowledge_base.py --check

# 离线构建索引
& $py build_knowledge_base.py --embed-provider local

# 命令行试检索（不起服务）
& $py query.py "巴士底狱" --top-k 3
& $py query.py --event "法国大革命"

# 起服务（离线）
& $py server.py --embed-provider local --rerank-provider local

# 起服务（接 ai.gitee.com 的 Qwen3-Embedding-8B / Qwen3-Reranker-8B）
$env:EPICSPARK_RAG_API_KEY = "sk-xxx"
& $py server.py
```

## 主要文件

| 文件 | 作用 |
|---|---|
| `server.py` | HTTP 服务入口 |
| `build_knowledge_base.py` | 语料校验 + 索引构建 |
| `query.py` | 命令行检索 / 事件卡片 |
| `eval_retrieval.py` | 检索质量评估（hit@k / MRR，30 条查询集） |
| `rag/retriever.py` | 检索编排核心（可直接 `import rag` 当库用） |
| `rag/embedding.py` | Qwen3-Embedding-8B + 本地回退 + SQLite 缓存 |
| `rag/reranker.py` | Qwen3-Reranker-8B + 本地词汇重排 |
| `tests/test_rag.py` | 离线测试 |

## 测试

```powershell
# 在 Tools/knowledge_rag 目录下
& $py -m unittest discover -s tests -v
```

## 约束

- `data/index/` 与 `data/cache/` 是派生数据，已 gitignore；语料改动后重建即可。
- 服务默认只监听 `127.0.0.1`；Android 真机联调时用 `--host 0.0.0.0`，并配 `EPICSPARK_RAG_ADMIN_TOKEN`。
