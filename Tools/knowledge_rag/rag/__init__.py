"""EpicSpark 知识库 RAG 服务。

纯标准库实现（numpy 可选加速），不依赖任何第三方服务即可离线跑通：
- 嵌入：ai.gitee.com 的 Qwen3-Embedding-8B（OpenAI 兼容 /v1/embeddings），
  未配置 API Key 时自动回退到本地确定性特征哈希向量。
- 重排：ai.gitee.com 的 Qwen3-Reranker-8B，未配置时回退到本地词汇重排。
- 检索：向量余弦 + BM25 -> RRF 融合 -> 重排。
"""

__version__ = "1.0.0"
