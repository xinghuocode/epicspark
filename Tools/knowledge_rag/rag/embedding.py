"""向量化：远端 OpenAI 兼容端点（Qwen3-Embedding-8B） + 本地确定性回退 + SQLite 缓存。

设计要点：
1. 远端不可用时（无 Key、网络不通、模型未部署）不阻塞服务，自动降级到本地特征哈希向量；
   健康检查接口会明确报告 ``degraded`` 与降级原因，避免"看起来在工作其实检索退化了"。
2. 所有向量入库前统一 L2 归一化，索引里存的是归一化向量，检索时直接用点积当余弦用。
3. 文本 -> 向量结果落 SQLite，重建索引时只对新增/改动文本调用远端 API。
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import sqlite3
import struct
import time
import urllib.error
import urllib.request
from array import array
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

from .config import RagConfig
from .text_utils import hashing_embed, l2_normalize

logger = logging.getLogger("rag.embedding")


class EmbeddingError(RuntimeError):
    """向量化失败（远端拒绝、鉴权失败、维度异常等）。"""


class BaseEmbeddings:
    kind = "base"

    def __init__(self, name: str) -> None:
        self.name = name
        self.dim = 0
        self.degraded_reason = ""

    def embed(self, texts: Sequence[str]) -> List[List[float]]:  # pragma: no cover - 抽象
        raise NotImplementedError

    def describe(self) -> Dict[str, Any]:
        return {
            "kind": self.kind,
            "model": self.name,
            "dim": self.dim,
            "degraded": bool(self.degraded_reason),
            "degraded_reason": self.degraded_reason,
        }


class HashingEmbeddings(BaseEmbeddings):
    """离线回退：确定性特征哈希向量（字符 1/2/3-gram + 英文词）。"""

    kind = "local-hashing"

    def __init__(self, dim: int = 1024) -> None:
        super().__init__(name=f"local-hashing-{dim}")
        self.dim = dim

    def embed(self, texts: Sequence[str]) -> List[List[float]]:
        return [hashing_embed(text, self.dim) for text in texts]


class RemoteEmbeddings(BaseEmbeddings):
    """OpenAI 兼容 /embeddings 客户端（ai.gitee.com 的 Qwen3-Embedding-8B）。"""

    kind = "openai-compatible"

    def __init__(
        self,
        base_url: str,
        api_key: str,
        model: str,
        timeout: float = 30.0,
        batch_size: int = 16,
        dimensions: int = 0,
        attempts: int = 3,
    ) -> None:
        super().__init__(name=model)
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.timeout = timeout
        self.batch_size = batch_size
        self.dimensions = dimensions
        self.attempts = attempts

    # -- 单批请求 -----------------------------------------------------------
    def _request(self, batch: Sequence[str]) -> List[List[float]]:
        payload: Dict[str, Any] = {
            "model": self.name,
            "input": list(batch),
            "encoding_format": "float",
        }
        if self.dimensions:
            payload["dimensions"] = self.dimensions
        body = json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(
            f"{self.base_url}/embeddings",
            data=body,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.api_key}",
            },
            method="POST",
        )

        last_error = ""
        for attempt in range(1, self.attempts + 1):
            try:
                with urllib.request.urlopen(request, timeout=self.timeout) as response:
                    raw = response.read().decode("utf-8", errors="replace")
                break
            except urllib.error.HTTPError as exc:
                detail = exc.read().decode("utf-8", errors="replace")[:400]
                last_error = f"HTTP {exc.code}: {detail}"
                if exc.code in (429, 500, 502, 503, 504) and attempt < self.attempts:
                    time.sleep(0.6 * attempt)
                    continue
                raise EmbeddingError(f"嵌入接口 {self.base_url}/embeddings 返回错误 {last_error}") from exc
            except (urllib.error.URLError, TimeoutError) as exc:
                last_error = f"{type(exc).__name__}: {exc}"
                if attempt < self.attempts:
                    time.sleep(0.6 * attempt)
                    continue
                raise EmbeddingError(f"无法连接嵌入端点 {self.base_url}: {last_error}") from exc
        else:  # pragma: no cover - 循环必然 break 或 raise
            raise EmbeddingError(last_error)

        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise EmbeddingError(f"嵌入接口返回非 JSON: {raw[:200]}") from exc

        data = parsed.get("data")
        if not isinstance(data, list) or not data:
            raise EmbeddingError(f"嵌入接口响应缺少 data 字段: {raw[:200]}")

        vectors: List[List[float]] = []
        for item in data:
            vector = item.get("embedding") if isinstance(item, dict) else item
            if not isinstance(vector, list) or not vector:
                raise EmbeddingError("嵌入接口返回的 embedding 不是非空数组")
            vectors.append([float(v) for v in vector])
        if len(vectors) != len(batch):
            raise EmbeddingError(f"嵌入返回条数不匹配: 请求 {len(batch)} 条，返回 {len(vectors)} 条")
        return vectors

    def embed(self, texts: Sequence[str]) -> List[List[float]]:
        if not self.api_key:
            raise EmbeddingError("未配置 API Key，无法调用远端嵌入模型")
        out: List[List[float]] = []
        for start in range(0, len(texts), self.batch_size):
            batch = list(texts[start : start + self.batch_size])
            vectors = self._request(batch)
            for vector in vectors:
                if not self.dim:
                    self.dim = len(vector)
                elif len(vector) != self.dim:
                    raise EmbeddingError(
                        f"嵌入维度不一致: 期望 {self.dim}，实际 {len(vector)}"
                    )
                out.append(l2_normalize(vector))
        return out


class CachedEmbeddings(BaseEmbeddings):
    """SQLite 向量缓存：以「提供方 + 模型 + 文本」为键，重建索引时避免重复计费。"""

    def __init__(self, inner: BaseEmbeddings, cache_path: str | Path) -> None:
        super().__init__(name=inner.name)
        self.inner = inner
        self.kind = inner.kind
        self.dim = inner.dim
        self.degraded_reason = inner.degraded_reason
        self.path = Path(cache_path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(self.path), check_same_thread=False)
        self._conn.execute(
            "CREATE TABLE IF NOT EXISTS embeddings ("
            "key TEXT PRIMARY KEY, dim INTEGER NOT NULL, vec BLOB NOT NULL, created REAL NOT NULL)"
        )
        self._conn.commit()
        self.hits = 0
        self.misses = 0

    def _key(self, text: str) -> str:
        return hashlib.sha256(
            f"{self.inner.kind}|{self.inner.name}|{self.inner.dim}|{text}".encode("utf-8")
        ).hexdigest()

    @staticmethod
    def _pack(vector: Sequence[float]) -> bytes:
        return struct.pack(f"<{len(vector)}f", *vector)

    @staticmethod
    def _unpack(blob: bytes) -> List[float]:
        values = array("f")
        values.frombytes(blob)
        return list(values)

    def embed(self, texts: Sequence[str]) -> List[List[float]]:
        keys = [self._key(text) for text in texts]
        cached: Dict[str, List[float]] = {}
        placeholders = ",".join("?" for _ in keys) or "''"
        cursor = self._conn.execute(
            f"SELECT key, vec FROM embeddings WHERE key IN ({placeholders})", keys
        )
        for key, blob in cursor.fetchall():
            cached[key] = self._unpack(blob)
        self.hits += len(cached)

        pending = [(key, text) for key, text in zip(keys, texts) if key not in cached]
        if pending:
            self.misses += len(pending)
            fresh = self.inner.embed([text for _, text in pending])
            now = time.time()
            rows = []
            for (key, _), vector in zip(pending, fresh):
                cached[key] = vector
                rows.append((key, len(vector), self._pack(vector), now))
            self._conn.executemany(
                "INSERT OR REPLACE INTO embeddings (key, dim, vec, created) VALUES (?,?,?,?)",
                rows,
            )
            self._conn.commit()
            self.dim = self.inner.dim

        result = [cached[key] for key in keys]
        for vector in result[:1]:
            if vector and not self.dim:
                self.dim = len(vector)
        return result

    def describe(self) -> Dict[str, Any]:
        info = self.inner.describe()
        info.update({"cache": str(self.path), "cache_hits": self.hits, "cache_misses": self.misses})
        return info


def build_embeddings(cfg: RagConfig) -> BaseEmbeddings:
    """按配置构造嵌入提供方；auto 模式下优先远端。"""
    local = HashingEmbeddings(cfg.local_embed_dim)
    if not cfg.use_remote_embeddings():
        logger.info("嵌入提供方: 本地特征哈希 (dim=%s)", cfg.local_embed_dim)
        return local

    remote = RemoteEmbeddings(
        base_url=cfg.base_url,
        api_key=cfg.api_key,
        model=cfg.embed_model,
        timeout=cfg.request_timeout,
        dimensions=cfg.embed_dimensions,
        attempts=cfg.remote_attempts,
    )
    logger.info("嵌入提供方: %s @ %s", cfg.embed_model, cfg.base_url)
    return CachedEmbeddings(remote, os.path.join(cfg.cache_dir, "embeddings.sqlite3"))


def probe_embeddings(provider: BaseEmbeddings, cfg: RagConfig) -> BaseEmbeddings:
    """做一次真实调用验证远端可用；失败时降级到本地并记录原因。"""
    if provider.kind == "local-hashing":
        return provider
    try:
        provider.embed(["法国大革命"])
        return provider
    except EmbeddingError as exc:
        fallback = HashingEmbeddings(cfg.local_embed_dim)
        fallback.degraded_reason = f"远端嵌入不可用，已降级为本地向量: {exc}"
        logger.warning(fallback.degraded_reason)
        return fallback
