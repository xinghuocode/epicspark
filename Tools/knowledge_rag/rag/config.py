"""配置：全部通过环境变量覆盖，便于在本地、CI 与打包环境中切换。

命名统一使用 EPICSPARK_RAG_ 前缀，例如::

    EPICSPARK_RAG_API_KEY=sk-xxx
    EPICSPARK_RAG_BASE_URL=https://ai.gitee.com/v1
    EPICSPARK_RAG_PORT=8787
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, Optional

PREFIX = "EPICSPARK_RAG_"

# Tools/knowledge_rag
PACKAGE_ROOT = Path(__file__).resolve().parent.parent


def _env(name: str, default: Optional[str] = None) -> Optional[str]:
    value = os.environ.get(PREFIX + name)
    if value is None or value == "":
        return default
    return value


def _env_int(name: str, default: int) -> int:
    raw = _env(name)
    if raw is None:
        return default
    try:
        return int(raw)
    except ValueError:
        return default


def _env_float(name: str, default: float) -> float:
    raw = _env(name)
    if raw is None:
        return default
    try:
        return float(raw)
    except ValueError:
        return default


def _env_bool(name: str, default: bool) -> bool:
    raw = _env(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def _env_list(name: str, default: list) -> list:
    raw = _env(name)
    if raw is None:
        return list(default)
    return [item.strip() for item in raw.split(",") if item.strip()]


@dataclass
class RagConfig:
    """运行期配置。字段顺序即 env 变量语义顺序。"""

    # ---------- HTTP 服务 ----------
    host: str = _env("HOST", "127.0.0.1")
    port: int = _env_int("PORT", 8787)
    # 管理接口（reindex / 语料热加载）令牌；为空则不校验（仅建议本地开发）
    admin_token: str = _env("ADMIN_TOKEN", "")

    # ---------- 模型端点（OpenAI 兼容）----------
    base_url: str = _env("BASE_URL", "https://ai.gitee.com/v1").rstrip("/")
    api_key: str = _env("API_KEY", "")
    embed_model: str = _env("EMBED_MODEL", "Qwen3-Embedding-8B")
    rerank_model: str = _env("RERANK_MODEL", "Qwen3-Reranker-8B")
    # auto: 有 api_key 用远端，否则本地；也可强制 openai / local
    embed_provider: str = _env("EMBED_PROVIDER", "auto")
    rerank_provider: str = _env("RERANK_PROVIDER", "auto")
    # 本地回退嵌入维度（仅本地提供方与旧版 bge-m3 等 1024 维模型对齐）
    local_embed_dim: int = _env_int("LOCAL_EMBED_DIM", 1024)
    # 远端 embedding 可选 dimensions 参数（Qwen3 支持 32~4096），0 表示不传
    embed_dimensions: int = _env_int("EMBED_DIMENSIONS", 0)
    # 远端重排接口路径，可逗号分隔多个候选，按顺序自动探测
    rerank_paths: list = field(
        default_factory=lambda: _env_list("RERANK_PATHS", ["/rerank", "/rerankings"])
    )
    request_timeout: float = _env_float("REQUEST_TIMEOUT", 30.0)
    remote_attempts: int = _env_int("REMOTE_ATTEMPTS", 3)

    # ---------- 语料与索引 ----------
    data_dir: str = _env("DATA_DIR", str(PACKAGE_ROOT / "data"))
    events_dir: str = _env("EVENTS_DIR", "")  # 空 -> <data_dir>/events
    index_dir: str = _env("INDEX_DIR", "")  # 空 -> <data_dir>/index
    cache_dir: str = _env("CACHE_DIR", "")  # 空 -> <data_dir>/cache

    # ---------- 检索参数 ----------
    top_k: int = _env_int("TOP_K", 8)
    candidates: int = _env_int("CANDIDATES", 40)  # 融合后进入重排的候选数
    rerank_top_n: int = _env_int("RERANK_TOP_N", 24)
    rrf_k: int = _env_int("RRF_K", 60)
    max_chunks_per_event: int = _env_int("MAX_CHUNKS_PER_EVENT", 2)
    context_budget_chars: int = _env_int("CONTEXT_BUDGET_CHARS", 2400)
    min_score: float = _env_float("MIN_SCORE", 0.0)
    # 相对得分阈值：低于「最高分 × 该系数」的结果直接丢弃，避免用无关内容填满 top_k
    relative_cutoff: float = _env_float("RELATIVE_CUTOFF", 0.45)
    # 语料/索引不匹配时是否强制重建
    auto_reindex: bool = _env_bool("AUTO_REINDEX", True)
    log_level: str = _env("LOG_LEVEL", "INFO")

    # ---------- 派生路径 ----------
    def __post_init__(self) -> None:
        self.events_dir = self.events_dir or str(Path(self.data_dir) / "events")
        self.index_dir = self.index_dir or str(Path(self.data_dir) / "index")
        self.cache_dir = self.cache_dir or str(Path(self.data_dir) / "cache")
        self.embed_provider = self.embed_provider.lower()
        self.rerank_provider = self.rerank_provider.lower()

    def use_remote_embeddings(self) -> bool:
        if self.embed_provider == "local":
            return False
        if self.embed_provider == "openai":
            return True
        return bool(self.api_key)

    def use_remote_rerank(self) -> bool:
        if self.rerank_provider == "local":
            return False
        if self.rerank_provider == "openai":
            return True
        return bool(self.api_key)

    def summary(self) -> Dict[str, Any]:
        """健康检查与日志用的脱敏视图。"""
        return {
            "host": self.host,
            "port": self.port,
            "base_url": self.base_url,
            "api_key": "***" if self.api_key else "",
            "embed_model": self.embed_model,
            "embed_provider": "openai" if self.use_remote_embeddings() else "local",
            "local_embed_dim": self.local_embed_dim,
            "rerank_model": self.rerank_model,
            "rerank_provider": "openai" if self.use_remote_rerank() else "local",
            "data_dir": self.data_dir,
            "index_dir": self.index_dir,
            "top_k": self.top_k,
            "candidates": self.candidates,
            "rerank_top_n": self.rerank_top_n,
            "context_budget_chars": self.context_budget_chars,
        }


def load_config(**overrides: Any) -> RagConfig:
    """构造配置；overrides 用于命令行参数覆盖环境变量。

    注意：`events_dir` / `index_dir` / `cache_dir` 是 `data_dir` 的派生值，
    单独覆盖 `data_dir` 时必须清掉已派生的旧值，否则会悄悄沿用默认目录。
    """
    cfg = RagConfig()
    data_dir_overridden = bool(overrides.get("data_dir"))
    for key, value in overrides.items():
        if value is not None and hasattr(cfg, key):
            setattr(cfg, key, value)
    if data_dir_overridden:
        for name in ("events_dir", "index_dir", "cache_dir"):
            if not overrides.get(name):
                setattr(cfg, name, "")
    cfg.__post_init__()
    return cfg
