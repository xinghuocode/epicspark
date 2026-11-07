"""向量索引：扁平 float32 矩阵 + JSON 元数据。

语料规模在千级 chunk，暴力余弦（精确检索）比 ANN 更合适：
零依赖、结果确定、可解释，几毫秒内可以算完。若将来语料上到十万级，
这里可以换成 HNSW/IVF，接口保持不变。

索引目录结构::

    index/meta.json      # chunk 元数据 + 语料指纹 + 嵌入模型信息
    index/vectors.f32    # 行主序 little-endian float32，第 i 行对应 meta.chunks[i]
"""

from __future__ import annotations

import json
import logging
import struct
import sys
from array import array
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

from .corpus import Chunk
from .text_utils import l2_normalize

logger = logging.getLogger("rag.index")

INDEX_VERSION = 1
META_FILE = "meta.json"
VECTORS_FILE = "vectors.f32"

try:  # numpy 只用于加速，缺失不影响功能
    import numpy as _np
except Exception:  # pragma: no cover - 环境无 numpy
    _np = None


class IndexError_(RuntimeError):
    """索引缺失/损坏/与语料不匹配。"""


class VectorIndex:
    def __init__(
        self,
        chunks: Sequence[Chunk],
        dim: int,
        matrix: array,
        embed_info: Optional[Dict[str, Any]] = None,
        fingerprint: str = "",
        created_at: str = "",
    ) -> None:
        if len(chunks) * dim != len(matrix):
            raise IndexError_(
                f"向量矩阵尺寸不匹配: {len(chunks)} 块 × {dim} 维 ≠ {len(matrix)} 个浮点数"
            )
        self.chunks: List[Chunk] = list(chunks)
        self.dim = dim
        self.matrix = matrix
        self.embed_info = embed_info or {}
        self.fingerprint = fingerprint
        self.created_at = created_at
        self._np_matrix = None
        if _np is not None:
            self._np_matrix = _np.frombuffer(matrix.tobytes(), dtype=_np.float32).reshape(
                len(chunks), dim
            )

    # ------------------------------------------------------------------
    # 构造
    # ------------------------------------------------------------------
    @classmethod
    def from_vectors(
        cls,
        chunks: Sequence[Chunk],
        vectors: Sequence[Sequence[float]],
        embed_info: Optional[Dict[str, Any]] = None,
        fingerprint: str = "",
        created_at: str = "",
    ) -> "VectorIndex":
        if len(chunks) != len(vectors):
            raise IndexError_(f"chunk 数与向量数不一致: {len(chunks)} vs {len(vectors)}")
        if not vectors:
            raise IndexError_("向量为空，无法建立索引")
        dim = len(vectors[0])
        flat = array("f")
        for chunk, vector in zip(chunks, vectors):
            if len(vector) != dim:
                raise IndexError_(f"向量维度不一致: {chunk.chunk_id} 是 {len(vector)}，期望 {dim}")
            flat.extend(l2_normalize(vector))
        return cls(list(chunks), dim, flat, embed_info, fingerprint, created_at)

    # ------------------------------------------------------------------
    # 检索
    # ------------------------------------------------------------------
    def search(self, query_vector: Sequence[float], limit: int = 50) -> List[Tuple[int, float]]:
        """返回 [(chunk 下标, 余弦相似度)]，按分数倒序。"""
        if len(query_vector) != self.dim:
            raise IndexError_(f"查询向量维度 {len(query_vector)} 与索引维度 {self.dim} 不一致")
        query = l2_normalize(query_vector)
        count = len(self.chunks)
        if count == 0:
            return []
        limit = max(1, min(limit, count))

        if self._np_matrix is not None:
            q = _np.asarray(query, dtype=_np.float32)
            scores = self._np_matrix @ q
            top = _np.argpartition(-scores, limit - 1)[:limit]
            top = top[_np.argsort(-scores[top])]
            return [(int(i), float(scores[i])) for i in top]

        rows = struct.Struct(f"<{self.dim}f")
        raw = self.matrix.tobytes()
        scored: List[Tuple[int, float]] = []
        for i in range(count):
            row = struct.unpack_from(f"<{self.dim}f", raw, i * self.dim * 4)
            scored.append((i, float(sum(a * b for a, b in zip(row, query)))))
        scored.sort(key=lambda pair: pair[1], reverse=True)
        return scored[:limit]

    # ------------------------------------------------------------------
    # 持久化
    # ------------------------------------------------------------------
    def save(self, index_dir: str | Path) -> None:
        directory = Path(index_dir)
        directory.mkdir(parents=True, exist_ok=True)
        if sys.byteorder != "little":
            data = array("f", self.matrix)
            data.byteswap()
            with (directory / VECTORS_FILE).open("wb") as handle:
                handle.write(data.tobytes())
        else:
            with (directory / VECTORS_FILE).open("wb") as handle:
                handle.write(self.matrix.tobytes())
        meta = {
            "version": INDEX_VERSION,
            "created_at": self.created_at,
            "fingerprint": self.fingerprint,
            "dim": self.dim,
            "count": len(self.chunks),
            "byteorder": "little",
            "dtype": "float32",
            "embed": self.embed_info,
            "chunks": [chunk.to_dict() for chunk in self.chunks],
        }
        with (directory / META_FILE).open("w", encoding="utf-8") as handle:
            json.dump(meta, handle, ensure_ascii=False, indent=1)
        logger.info("索引已写入 %s（%s 块 × %s 维）", directory, len(self.chunks), self.dim)

    @classmethod
    def load(cls, index_dir: str | Path) -> "VectorIndex":
        directory = Path(index_dir)
        meta_path = directory / META_FILE
        vectors_path = directory / VECTORS_FILE
        if not meta_path.exists() or not vectors_path.exists():
            raise IndexError_(f"索引文件缺失，请先运行 build_knowledge_base.py: {directory}")
        with meta_path.open("r", encoding="utf-8") as handle:
            meta = json.load(handle)
        if meta.get("version") != INDEX_VERSION:
            raise IndexError_(f"索引版本不支持: {meta.get('version')}")
        chunks = [Chunk.from_dict(raw) for raw in meta.get("chunks", [])]
        dim = int(meta.get("dim", 0))
        expected = len(chunks) * dim
        raw_bytes = vectors_path.read_bytes()
        if len(raw_bytes) != expected * 4:
            raise IndexError_(
                f"向量文件损坏: {vectors_path} 期望 {expected * 4} 字节，实际 {len(raw_bytes)}"
            )
        matrix = array("f")
        matrix.frombytes(raw_bytes)
        if sys.byteorder != "little":
            matrix.byteswap()
        return cls(
            chunks,
            dim,
            matrix,
            embed_info=meta.get("embed", {}),
            fingerprint=meta.get("fingerprint", ""),
            created_at=meta.get("created_at", ""),
        )

    # ------------------------------------------------------------------
    # 其它
    # ------------------------------------------------------------------
    def stats(self) -> Dict[str, Any]:
        events = {chunk.event_id for chunk in self.chunks}
        return {
            "chunks": len(self.chunks),
            "events": len(events),
            "dim": self.dim,
            "created_at": self.created_at,
            "embed": self.embed_info,
            "fingerprint": self.fingerprint[:12],
        }
