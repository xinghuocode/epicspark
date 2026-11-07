"""检索编排：向量召回 + BM25 召回 -> RRF 融合 -> 重排 -> 去重与上下文拼装。

这是「知识库」对外的能力内核，HTTP 层只是薄壳。把编排放在这里的好处是：
单元测试可以直接构造 KnowledgeBase，不需要起服务；主程序也可以把本包
当库用（import rag），而不只是走 HTTP。
"""

from __future__ import annotations

import logging
import re
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence

from .config import RagConfig
from .corpus import (
    Chunk,
    build_chunks,
    chunk_event,
    corpus_fingerprint,
    load_events,
    resolve_event,
)
from .embedding import BaseEmbeddings, EmbeddingError, HashingEmbeddings, build_embeddings, probe_embeddings
from .reranker import BaseReranker, build_reranker, probe_reranker
from .text_utils import BM25Index, normalize
from .vector_store import IndexError_, VectorIndex

logger = logging.getLogger("rag.kb")

RRF_DENSE_WEIGHT = 1.0
RRF_SPARSE_WEIGHT = 1.0
RESOLVED_EVENT_BOOST = 0.05


def _now_iso() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")


@dataclass
class SearchHit:
    chunk: Chunk
    dense: float
    sparse: float
    fused: float
    rerank: Optional[float]
    final: float
    event: Dict[str, Any]

    def to_dict(self, rank: int) -> Dict[str, Any]:
        chunk = self.chunk
        return {
            "rank": rank,
            "chunk_id": chunk.chunk_id,
            "event_id": chunk.event_id,
            "title": chunk.title,
            "section": chunk.section,
            "section_label": chunk.section_label,
            "era": chunk.era,
            "region": chunk.region,
            "period": chunk.period,
            "start_year": chunk.start_year,
            "end_year": chunk.end_year,
            "text": chunk.text,
            "keywords": chunk.keywords,
            "scores": {
                "dense": round(self.dense, 6),
                "sparse": round(self.sparse, 6),
                "fused": round(self.fused, 6),
                "rerank": None if self.rerank is None else round(self.rerank, 6),
                "final": round(self.final, 6),
            },
            "sources": self.event.get("sources", []),
        }


class KnowledgeBase:
    def __init__(self, cfg: RagConfig, probe: bool = True) -> None:
        self.cfg = cfg
        self.lock = threading.RLock()
        self.events: List[Dict[str, Any]] = []
        self.chunks: List[Chunk] = []
        self.fingerprint = ""
        self.index: Optional[VectorIndex] = None
        self.bm25 = BM25Index()
        self.embeddings: BaseEmbeddings
        self.reranker: BaseReranker
        self.embeddings = build_embeddings(cfg)
        self.reranker = build_reranker(cfg)
        if probe:
            self.embeddings = probe_embeddings(self.embeddings, cfg)
            self.reranker = probe_reranker(self.reranker, cfg)
        # 运行期兜底：远端嵌入中途挂掉时，若维度与索引一致就临时用本地向量，避免整条链路报错
        self._fallback_embeddings = HashingEmbeddings(cfg.local_embed_dim)
        self._runtime_degraded: List[str] = []
        self._event_index: Dict[str, Dict[str, Any]] = {}
        self.reload()

    # ------------------------------------------------------------------
    # 生命周期
    # ------------------------------------------------------------------
    def reload(self) -> None:
        """重新读取语料并加载/重建索引。可被 reindex 接口热调用。"""
        with self.lock:
            self.events = load_events(self.cfg.events_dir)
            self.chunks = build_chunks(self.events)
            self.fingerprint = corpus_fingerprint(self.chunks)
            self._event_index = {event["id"]: event for event in self.events}

            self.bm25 = BM25Index()
            for chunk in self.chunks:
                self.bm25.add(chunk.chunk_id, chunk.text)
            self.bm25.finalize()

            rebuilt = self._load_or_build_index()
            logger.info(
                "知识库就绪: %s 个事件 / %s 个文本块%s",
                len(self.events),
                len(self.chunks),
                "（已重建索引）" if rebuilt else "",
            )

    def rebuild(self) -> Dict[str, Any]:
        """强制重建向量索引（忽略磁盘缓存）。"""
        with self.lock:
            self._build_index()
            return self.stats()

    def _load_or_build_index(self) -> bool:
        index_dir = Path(self.cfg.index_dir)
        try:
            index = VectorIndex.load(index_dir)
        except IndexError_ as exc:
            logger.info("未找到可用索引，开始重建: %s", exc)
            self._build_index()
            return True

        if index.fingerprint != self.fingerprint:
            logger.info("语料发生变化，重建索引")
            self._build_index()
            return True

        info = index.embed_info or {}
        current_kind = getattr(self.embeddings, "kind", "")
        current_name = getattr(self.embeddings, "name", "")
        dim_mismatch = bool(self.embeddings.dim) and index.dim != self.embeddings.dim
        kind_mismatch = bool(info.get("kind")) and info.get("kind") != current_kind
        name_mismatch = bool(info.get("model")) and info.get("model") != current_name
        if dim_mismatch or kind_mismatch or name_mismatch:
            message = (
                f"索引与当前嵌入配置不匹配（索引 {info.get('kind')}/{info.get('model')}/{index.dim} 维，"
                f"当前 {current_kind}/{current_name}/{self.embeddings.dim or '未知'} 维）"
            )
            if not self.cfg.auto_reindex:
                raise IndexError_(message + "；请运行 build_knowledge_base.py 重建")
            logger.warning(message + "，自动重建")
            self._build_index()
            return True

        self.index = index
        self.embeddings.dim = index.dim
        return False

    def _build_index(self) -> None:
        vectors = self.embeddings.embed([chunk.text for chunk in self.chunks])
        self.embeddings.dim = len(vectors[0]) if vectors else 0
        index = VectorIndex.from_vectors(
            self.chunks,
            vectors,
            embed_info=self.embeddings.describe(),
            fingerprint=self.fingerprint,
            created_at=_now_iso(),
        )
        Path(self.cfg.index_dir).mkdir(parents=True, exist_ok=True)
        index.save(self.cfg.index_dir)
        self.index = index

    def _embed_query(self, query: str) -> List[float]:
        """查询向量化；远端异常时按维度一致性决定是否临时降级到本地向量。"""
        try:
            return self.embeddings.embed([query])[0]
        except EmbeddingError as exc:
            index_dim = self.index.dim if self.index else 0
            if index_dim and index_dim == self._fallback_embeddings.dim:
                note = f"查询向量化临时降级为本地向量: {exc}"
                if note not in self._runtime_degraded:
                    self._runtime_degraded.append(note)
                    logger.warning(note)
                return self._fallback_embeddings.embed([query])[0]
            raise

    # ------------------------------------------------------------------
    # 检索
    # ------------------------------------------------------------------
    def _passes_filters(self, chunk: Chunk, filters: Dict[str, Any]) -> bool:
        if not filters:
            return True
        era = filters.get("era")
        if era:
            eras = [era] if isinstance(era, str) else list(era)
            if chunk.era not in eras:
                return False
        region = filters.get("region")
        if region and str(region) not in chunk.region:
            return False
        year = filters.get("year")
        if year is not None:
            try:
                year = int(year)
            except (TypeError, ValueError):
                year = None
            if year is not None and not (chunk.start_year <= year <= chunk.end_year):
                return False
        event_id = filters.get("event_id")
        if event_id and chunk.event_id != event_id:
            return False
        year_from = filters.get("year_from")
        if year_from is not None and chunk.end_year < int(year_from):
            return False
        year_to = filters.get("year_to")
        if year_to is not None and chunk.start_year > int(year_to):
            return False
        return True

    def resolve(self, query: str) -> Optional[Dict[str, Any]]:
        event, confidence, how = resolve_event(query, self.events)
        if event is None:
            return None
        return {
            "event_id": event["id"],
            "title": event["title"],
            "confidence": round(confidence, 4),
            "matched_by": how,
        }

    def search(
        self,
        query: str,
        top_k: Optional[int] = None,
        filters: Optional[Dict[str, Any]] = None,
        use_rerank: bool = True,
        candidates: Optional[int] = None,
    ) -> Dict[str, Any]:
        started = time.perf_counter()
        query = (query or "").strip()
        if not query:
            raise ValueError("query 不能为空")
        if self.index is None:
            raise IndexError_("索引尚未就绪")

        top_k = max(1, min(int(top_k or self.cfg.top_k), 50))
        candidates = max(top_k, min(int(candidates or self.cfg.candidates), 200))
        filters = filters or {}

        # 先解析事件：命中已收录事件时用标题做查询扩展，弥补「法国的革命」这类口语表述
        resolved = self.resolve(query)
        expand = bool(resolved and resolved["confidence"] >= 0.6)
        retrieval_query = f"{query} {resolved['title']}" if expand else query

        query_vector = self._embed_query(retrieval_query)
        dense_hits = self.index.search(query_vector, candidates * 2)
        sparse_hits = self.bm25.search(retrieval_query, candidates * 2)

        chunk_by_id = {chunk.chunk_id: chunk for chunk in self.chunks}
        id_to_pos = {chunk.chunk_id: pos for pos, chunk in enumerate(self.chunks)}

        fused: Dict[str, float] = {}
        dense_scores: Dict[str, float] = {}
        sparse_scores: Dict[str, float] = {}

        for rank, (position, score) in enumerate(dense_hits):
            chunk = self.chunks[position]
            if not self._passes_filters(chunk, filters):
                continue
            dense_scores[chunk.chunk_id] = score
            fused[chunk.chunk_id] = fused.get(chunk.chunk_id, 0.0) + RRF_DENSE_WEIGHT / (
                self.cfg.rrf_k + rank + 1
            )
        for rank, (chunk_id, score) in enumerate(sparse_hits):
            chunk = chunk_by_id.get(chunk_id)
            if chunk is None or not self._passes_filters(chunk, filters):
                continue
            sparse_scores[chunk_id] = score
            fused[chunk_id] = fused.get(chunk_id, 0.0) + RRF_SPARSE_WEIGHT / (
                self.cfg.rrf_k + rank + 1
            )

        if not fused:
            return {
                "query": query,
                "took_ms": round((time.perf_counter() - started) * 1000, 2),
                "resolved": resolved,
                "items": [],
                "context": "",
                "total_candidates": 0,
                "rerank_used": False,
                "degraded": self.degraded_report(),
            }

        ordered_ids = sorted(fused, key=lambda cid: fused[cid], reverse=True)[:candidates]
        rerank_used = False
        rerank_scores: Dict[str, float] = {}
        if use_rerank and ordered_ids:
            head = ordered_ids[: self.cfg.rerank_top_n]
            documents = [chunk_by_id[cid].text for cid in head]
            try:
                # 重排也用扩展后的查询：否则「法国的革命」这种口语会被判为与《法国大革命》不相似
                values = self.reranker.score(retrieval_query, documents)
                rerank_used = True
                for cid, value in zip(head, values):
                    rerank_scores[cid] = float(value)
            except Exception as exc:  # 重排失败不能拖垮检索
                logger.warning("重排失败，退回融合序: %s", exc)

        resolved_id = resolved["event_id"] if resolved and resolved["confidence"] >= 0.9 else None

        if rerank_used:
            # 只保留参与过重排的候选：未重排的条目只有 RRF 分数，和重排分不同量纲，
            # 混在一起排序会让「没被重排的弱条目」反而压过「重排后的强条目」。
            ordered_ids = [cid for cid in ordered_ids if cid in rerank_scores]

        hits: List[SearchHit] = []
        for cid in ordered_ids:
            chunk = chunk_by_id[cid]
            dense = dense_scores.get(cid, 0.0)
            sparse = sparse_scores.get(cid, 0.0)
            fused_score = fused[cid]
            rerank = rerank_scores.get(cid)
            if rerank is not None:
                final = 0.75 * rerank + 0.25 * _normalize_fused(fused_score, fused)
            else:
                final = _normalize_fused(fused_score, fused)
            if resolved_id and chunk.event_id == resolved_id:
                final += RESOLVED_EVENT_BOOST
            hits.append(
                SearchHit(
                    chunk=chunk,
                    dense=dense,
                    sparse=sparse,
                    fused=fused_score,
                    rerank=rerank,
                    final=final,
                    event=self._event_index.get(chunk.event_id, {}),
                )
            )

        hits.sort(key=lambda hit: hit.final, reverse=True)
        # 相对阈值：得分断崖式的尾部（通常是与查询毫不相关的事件）直接丢弃，
        # 不要用低分内容把 top_k 填满——剧情 Agent 拿到无关知识会编得更离谱。
        if hits and self.cfg.relative_cutoff > 0:
            threshold = hits[0].final * self.cfg.relative_cutoff
            hits = [hit for hit in hits if hit.final >= threshold]
        selected = self._diversify(hits, top_k)
        items = [hit.to_dict(rank) for rank, hit in enumerate(selected, start=1)]
        if self.cfg.min_score > 0:
            items = [item for item in items if item["scores"]["final"] >= self.cfg.min_score]
            for rank, item in enumerate(items, start=1):
                item["rank"] = rank

        payload = {
            "query": query,
            "took_ms": round((time.perf_counter() - started) * 1000, 2),
            "resolved": resolved,
            "items": items,
            "total_candidates": len(fused),
            "rerank_used": rerank_used,
            "degraded": self.degraded_report(),
        }
        payload["context"] = self.build_context(items)
        return payload

    def _diversify(self, hits: Sequence[SearchHit], top_k: int) -> List[SearchHit]:
        """按得分顺序挑选，但限制单事件的块数，避免一个事件霸占全部名额。

        名额不足时再用被跳过的同事件块回填（例如语料里只有两个事件命中）。
        这里刻意不做「轮转取块」：那只会在结果里塞进低分的无关事件，
        对剧情 Agent 反而更糟。
        """
        limit = max(1, self.cfg.max_chunks_per_event)
        per_event: Dict[str, int] = {}
        selected: List[SearchHit] = []
        overflow: List[SearchHit] = []
        for hit in hits:
            count = per_event.get(hit.chunk.event_id, 0)
            if count >= limit:
                overflow.append(hit)
                continue
            per_event[hit.chunk.event_id] = count + 1
            selected.append(hit)
            if len(selected) >= top_k:
                break
        if len(selected) < top_k:
            # 只有「按事件限额后一条都没选到」时才回填，否则宁可少给也不拿同一事件灌满
            if not selected:
                selected = list(hits[:top_k])
        return selected[:top_k]

    def build_context(self, items: Sequence[Dict[str, Any]], budget: Optional[int] = None) -> str:
        """把检索结果拼成可直接塞进提示词的中文上下文。

        文本块本身带《标题》（年代｜时代｜地区）章节：前缀，这里把它换成更短的
        【标题｜年代｜章节】标记，既保留溯源信息，又避免 LLM 看到重复标题。
        """
        budget = int(budget or self.cfg.context_budget_chars)
        lines: List[str] = []
        used = 0
        for item in items:
            line = _context_line(item)
            if not lines and len(line) > budget:
                # 首块超预算时硬截断，保证返回的上下文字符数不会失控
                line = line[: max(0, budget - 1)] + "…"
            if used + len(line) > budget and lines:
                break
            lines.append(line)
            used += len(line) + 1
        return "\n".join(lines)

    # ------------------------------------------------------------------
    # 事件级接口（剧情 Agent 开局用）
    # ------------------------------------------------------------------
    def event_card(self, name: str, top_k: int = 6, budget: Optional[int] = None) -> Dict[str, Any]:
        started = time.perf_counter()
        resolved = self.resolve(name)
        if resolved is None:
            fallback = self.search(name, top_k=top_k)
            fallback["matched"] = None
            fallback["event"] = None
            fallback["took_ms_event"] = round((time.perf_counter() - started) * 1000, 2)
            return fallback

        event = self._event_index[resolved["event_id"]]
        chunks = [chunk for chunk in self.chunks if chunk.event_id == event["id"]]
        evidences: List[Dict[str, Any]] = []
        for chunk in chunks:
            evidences.append(
                {
                    "chunk_id": chunk.chunk_id,
                    "section": chunk.section,
                    "section_label": chunk.section_label,
                    "text": chunk.text,
                }
            )
        # 事件内部的块也过一遍重排，让"最能回答用户输入"的块排在前面
        try:
            values = self.reranker.score(name, [item["text"] for item in evidences])
            for item, value in zip(evidences, values):
                item["score"] = round(float(value), 6)
            evidences.sort(key=lambda item: item["score"], reverse=True)
        except Exception as exc:
            logger.warning("事件内重排失败，保持语料顺序: %s", exc)

        top_evidences = evidences[: max(1, top_k)]
        context_items = [
            {
                "title": event["title"],
                "period": event["period"],
                "section_label": item["section_label"],
                "text": item["text"],
            }
            for item in top_evidences
        ]
        context = self.build_context(context_items, budget)
        return {
            "query": name,
            "took_ms": round((time.perf_counter() - started) * 1000, 2),
            "matched": resolved,
            "event": {
                "id": event["id"],
                "title": event["title"],
                "aliases": event["aliases"],
                "era": event["era"],
                "region": event["region"],
                "period": event["period"],
                "start_year": event["start_year"],
                "end_year": event["end_year"],
                "one_liner": event["one_liner"],
                "keywords": event["keywords"],
                "timeline": event["timeline"],
                "gameplay_hooks": event["gameplay_hooks"],
                "sources": event["sources"],
            },
            "evidences": top_evidences,
            "context": context,
            "degraded": self.degraded_report(),
        }

    # ------------------------------------------------------------------
    # 运维视图
    # ------------------------------------------------------------------
    def degraded_report(self) -> Dict[str, Any]:
        report: Dict[str, Any] = {}
        if getattr(self.embeddings, "degraded_reason", ""):
            report["embeddings"] = self.embeddings.degraded_reason
        if getattr(self.reranker, "degraded_reason", ""):
            report["rerank"] = self.reranker.degraded_reason
        if self._runtime_degraded:
            report["runtime"] = self._runtime_degraded[-1]
        return report

    def stats(self) -> Dict[str, Any]:
        eras: Dict[str, int] = {}
        for event in self.events:
            eras[event["era"]] = eras.get(event["era"], 0) + 1
        index_stats = self.index.stats() if self.index else {}
        return {
            "events": len(self.events),
            "chunks": len(self.chunks),
            "eras": eras,
            "embedding": self.embeddings.describe(),
            "reranker": self.reranker.describe(),
            "index": index_stats,
            "degraded": self.degraded_report(),
            "config": self.cfg.summary(),
        }

    def list_events(self) -> List[Dict[str, Any]]:
        return [
            {
                "id": event["id"],
                "title": event["title"],
                "aliases": event["aliases"],
                "era": event["era"],
                "region": event["region"],
                "period": event["period"],
                "start_year": event["start_year"],
                "end_year": event["end_year"],
                "one_liner": event["one_liner"],
                "keywords": event["keywords"],
            }
            for event in self.events
        ]

    def get_event(self, event_id: str) -> Optional[Dict[str, Any]]:
        event = self._event_index.get(event_id)
        if event is None:
            return None
        payload = dict(event)
        payload["chunks"] = [chunk.to_dict() for chunk in self.chunks if chunk.event_id == event_id]
        return payload


def _normalize_fused(score: float, fused: Dict[str, float]) -> float:
    """把 RRF 分数压到 0~1，便于和重排分数加权。"""
    if not fused:
        return 0.0
    low = min(fused.values())
    high = max(fused.values())
    if high - low <= 1e-12:
        return 1.0
    return (score - low) / (high - low)


_CHUNK_HEADER_RE = re.compile(r"^《([^》]+)》（([^）]*)）([^：]{1,12})：")


def _context_line(item: Dict[str, Any]) -> str:
    """把「《标题》（年代｜时代｜地区）章节：正文」压成「【标题｜年代｜章节】正文」。"""
    text = str(item.get("text", ""))
    match = _CHUNK_HEADER_RE.match(text)
    if match:
        title, meta, section = match.group(1), match.group(2), match.group(3)
        period = meta.split("｜")[0] if "｜" in meta else meta
        return f"【{title}｜{period}｜{section}】{text[match.end():]}"
    return f"【{item.get('title', '')}｜{item.get('period', '')}｜{item.get('section_label', '')}】{text}"
