"""重排序：Qwen3-Reranker-8B（远端） + 本地词汇重排（离线回退）。

架构文档要求重排使用 Qwen3-Reranker-8B。远端接口按 OpenAI 风格的
``POST {base}/rerank`` 约定实现（body: model / query / documents / top_n），
并支持配置多个候选路径自动探测，以兼容不同网关的路径差异。

本地回退不是占位符：它用「查询词项覆盖率 + 词项密度 + Jaccard + 短语命中」
组合打分，作为 cross-encoder 的廉价近似，保证离线时排序仍然明显优于原始向量序。
"""

from __future__ import annotations

import json
import logging
import time
import urllib.error
import urllib.request
from typing import Any, Dict, List, Optional, Sequence

from .config import RagConfig
from .text_utils import coverage, jaccard, normalize, token_set

logger = logging.getLogger("rag.rerank")


class RerankError(RuntimeError):
    """重排失败。"""


class BaseReranker:
    kind = "base"

    def __init__(self, name: str) -> None:
        self.name = name
        self.degraded_reason = ""

    def score(self, query: str, documents: Sequence[str]) -> List[float]:  # pragma: no cover
        raise NotImplementedError

    def describe(self) -> Dict[str, Any]:
        return {
            "kind": self.kind,
            "model": self.name,
            "degraded": bool(self.degraded_reason),
            "degraded_reason": self.degraded_reason,
        }


class LocalLexicalReranker(BaseReranker):
    """基于词项匹配的可解释重排器，输出 0~1 相关性。"""

    kind = "local-lexical"

    def __init__(self) -> None:
        super().__init__(name="local-lexical-reranker")

    def score(self, query: str, documents: Sequence[str]) -> List[float]:
        q = normalize(query)
        q_tokens = token_set(q)
        if not documents:
            return []
        scores: List[float] = []
        for document in documents:
            d = normalize(document)
            d_tokens = token_set(d)
            cov = coverage(q_tokens, d_tokens)
            jac = jaccard(q_tokens, d_tokens)
            overlap = len(q_tokens & d_tokens)
            # 词项密度：命中的查询词项在文档里"被稀释"的程度，抑制长块天然占优
            density = overlap / ((len(d_tokens) ** 0.5) + 1.0)
            phrase = 1.0 if q and q in d else 0.0
            value = 0.50 * cov + 0.25 * jac + 0.15 * min(1.0, density) + 0.10 * phrase
            scores.append(round(min(1.0, max(0.0, value)), 6))
        return scores


class RemoteReranker(BaseReranker):
    """OpenAI 兼容 /rerank 客户端（ai.gitee.com 的 Qwen3-Reranker-8B）。"""

    kind = "openai-compatible-rerank"

    def __init__(
        self,
        base_url: str,
        api_key: str,
        model: str,
        paths: Sequence[str],
        timeout: float = 30.0,
        attempts: int = 2,
    ) -> None:
        super().__init__(name=model)
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.paths = list(paths) or ["/rerank"]
        self.timeout = timeout
        self.attempts = attempts
        self._working_path: Optional[str] = None

    def _post(self, path: str, body: Dict[str, Any]) -> Dict[str, Any]:
        data = json.dumps(body).encode("utf-8")
        request = urllib.request.Request(
            f"{self.base_url}{path}",
            data=data,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.api_key}",
            },
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            raw = response.read().decode("utf-8", errors="replace")
        return json.loads(raw)

    def score(self, query: str, documents: Sequence[str]) -> List[float]:
        if not documents:
            return []
        if not self.api_key:
            raise RerankError("未配置 API Key，无法调用远端重排模型")

        body = {
            "model": self.name,
            "query": query,
            "documents": [{"text": document} for document in documents],
            "top_n": len(documents),
            "return_documents": False,
        }
        candidates = [self._working_path] if self._working_path else list(self.paths)
        last_error = ""
        payload: Optional[Dict[str, Any]] = None
        for path in candidates:
            for attempt in range(1, self.attempts + 1):
                try:
                    payload = self._post(path, body)
                    self._working_path = path
                    break
                except urllib.error.HTTPError as exc:
                    detail = exc.read().decode("utf-8", errors="replace")[:300]
                    last_error = f"{path} -> HTTP {exc.code}: {detail}"
                    if exc.code == 404:
                        break  # 换下一个候选路径
                    if exc.code in (429, 500, 502, 503, 504) and attempt < self.attempts:
                        time.sleep(0.5 * attempt)
                        continue
                    raise RerankError(f"重排接口错误: {last_error}") from exc
                except (urllib.error.URLError, TimeoutError) as exc:
                    last_error = f"{path} -> {type(exc).__name__}: {exc}"
                    if attempt < self.attempts:
                        time.sleep(0.5 * attempt)
                        continue
                    raise RerankError(f"无法连接重排端点: {last_error}") from exc
                except json.JSONDecodeError as exc:
                    raise RerankError(f"重排接口返回非 JSON（{path}）") from exc
            if payload is not None:
                break
        if payload is None:
            raise RerankError(f"重排接口不可用，已尝试路径 {self.paths}: {last_error}")

        return self._parse(payload, len(documents))

    @staticmethod
    def _parse(payload: Dict[str, Any], count: int) -> List[float]:
        """兼容三种常见返回结构：results[{index,relevance_score}] / data / scores[]。"""
        items = payload.get("results") or payload.get("data")
        scores = [-1.0] * count
        if isinstance(items, list):
            for position, item in enumerate(items):
                if not isinstance(item, dict):
                    continue
                index = item.get("index", position)
                value = item.get("relevance_score", item.get("score"))
                if value is None:
                    continue
                try:
                    index = int(index)
                except (TypeError, ValueError):
                    index = position
                if 0 <= index < count:
                    scores[index] = float(value)
        elif isinstance(payload.get("scores"), list):
            for index, value in enumerate(payload["scores"][:count]):
                scores[index] = float(value)

        if all(score < 0 for score in scores):
            raise RerankError(f"重排响应无法解析出分数: {json.dumps(payload, ensure_ascii=False)[:300]}")
        filled = [score for score in scores if score >= 0]
        floor = min(filled) if filled else 0.0
        scores = [score if score >= 0 else floor for score in scores]
        # 归一到 0~1：部分网关返回 logits 而非概率
        low, high = min(scores), max(scores)
        if high - low > 1e-6 and (high > 1.0 or low < 0.0):
            scores = [(score - low) / (high - low) for score in scores]
        return [round(score, 6) for score in scores]


class CascadeReranker(BaseReranker):
    """优先远端，失败自动降级本地，并记住降级原因供健康检查输出。"""

    kind = "cascade"

    def __init__(self, primary: Optional[BaseReranker], fallback: BaseReranker) -> None:
        super().__init__(name=primary.name if primary else fallback.name)
        self.primary = primary
        self.fallback = fallback
        self.failures = 0

    def score(self, query: str, documents: Sequence[str]) -> List[float]:
        if self.primary is not None:
            try:
                return self.primary.score(query, documents)
            except RerankError as exc:
                self.failures += 1
                if self.failures == 1:
                    logger.warning("远端重排不可用，降级为本地重排: %s", exc)
                self.degraded_reason = str(exc)
        return self.fallback.score(query, documents)

    def describe(self) -> Dict[str, Any]:
        info = super().describe()
        if self.primary is not None:
            info["primary"] = self.primary.describe()
        info["fallback"] = self.fallback.describe()
        return info


def build_reranker(cfg: RagConfig) -> BaseReranker:
    local = LocalLexicalReranker()
    if not cfg.use_remote_rerank():
        logger.info("重排提供方: 本地词汇重排")
        return local
    remote = RemoteReranker(
        base_url=cfg.base_url,
        api_key=cfg.api_key,
        model=cfg.rerank_model,
        paths=cfg.rerank_paths,
        timeout=cfg.request_timeout,
        attempts=cfg.remote_attempts,
    )
    logger.info("重排提供方: %s @ %s", cfg.rerank_model, cfg.base_url)
    return CascadeReranker(remote, local)


def probe_reranker(reranker: BaseReranker, cfg: RagConfig) -> BaseReranker:
    """启动时探活：远端不可达则整体退回本地，避免每次请求都先失败一次。"""
    if not isinstance(reranker, CascadeReranker):
        return reranker
    try:
        reranker.primary.score("法国大革命", ["法国大革命推翻了波旁王朝。"])  # type: ignore[union-attr]
        return reranker
    except RerankError as exc:
        logger.warning("重排探活失败，本次运行使用本地重排: %s", exc)
        local = LocalLexicalReranker()
        local.degraded_reason = f"远端重排不可用: {exc}"
        return local
