"""HTTP 接口层：只做协议转换与校验，业务编排全部在 retriever.KnowledgeBase。

只依赖标准库 http.server，因此可以在没有 pip 的机器上直接跑
（Windows 自带 Python、Android 侧调试机、CI 容器都一样）。

接口一览（详见 Docs/KnowledgeRAG.md）：

    GET  /health
    GET  /v1/knowledge/stats
    GET  /v1/knowledge/events
    GET  /v1/knowledge/events/{id}
    GET  /v1/knowledge/search?q=法国大革命&top_k=5
    POST /v1/knowledge/search
    POST /v1/knowledge/event      ← 剧情 Agent 开局用
    POST /v1/knowledge/context    ← 只要提示词上下文
    POST /v1/knowledge/reindex    ← 管理接口，需 X-RAG-Token
"""

from __future__ import annotations

import json
import logging
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, Optional
from urllib.parse import parse_qs, urlparse

from .config import RagConfig
from .retriever import KnowledgeBase

logger = logging.getLogger("rag.http")

MAX_BODY_BYTES = 1 << 20  # 1 MiB，检索请求不可能更大
VERSION = "1.0.0"


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.code = code


def _as_int(payload: Dict[str, Any], key: str) -> Optional[int]:
    value = payload.get(key)
    if value is None or value == "":
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        raise ApiError(400, "invalid_argument", f"{key} 必须是整数")


def _as_bool(payload: Dict[str, Any], key: str, default: bool) -> bool:
    value = payload.get(key)
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


class Handler(BaseHTTPRequestHandler):
    server_version = f"EpicSparkKnowledgeRAG/{VERSION}"
    protocol_version = "HTTP/1.1"

    # 由 create_server 注入
    kb: KnowledgeBase
    cfg: RagConfig

    # ---------------- 基础工具 ----------------
    def log_message(self, fmt: str, *args: Any) -> None:  # noqa: A003 - 覆盖基类
        logger.debug("%s - %s", self.address_string(), fmt % args)

    def _send(self, status: int, payload: Any) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-RAG-Token, Authorization")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.end_headers()
        self.wfile.write(body)

    def _send_error(self, status: int, code: str, message: str) -> None:
        self._send(status, {"error": {"code": code, "message": message, "status": status}})

    def _read_json(self) -> Dict[str, Any]:
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            raise ApiError(400, "invalid_argument", "Content-Length 非法")
        if length > MAX_BODY_BYTES:
            raise ApiError(413, "payload_too_large", "请求体过大")
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            raise ApiError(400, "invalid_json", f"请求体不是合法 JSON: {exc}")
        if not isinstance(payload, dict):
            raise ApiError(400, "invalid_json", "请求体必须是 JSON 对象")
        return payload

    def _require_admin(self) -> None:
        token = self.cfg.admin_token
        if not token:
            return
        provided = self.headers.get("X-RAG-Token") or ""
        if not provided.startswith("Bearer "):
            provided = provided or ""
        if provided.startswith("Bearer "):
            provided = provided[len("Bearer ") :]
        if provided != token:
            raise ApiError(401, "unauthorized", "管理接口需要正确的 X-RAG-Token")

    def _dispatch(self, method: str) -> None:
        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/") or "/"
        query = parse_qs(parsed.query)
        try:
            if method == "OPTIONS":
                self._send(204, {})
                return
            if path == "/" and method == "GET":
                self._send(
                    200,
                    {
                        "service": "EpicSpark Knowledge RAG",
                        "version": VERSION,
                        "endpoints": [
                            "GET /health",
                            "GET /v1/knowledge/stats",
                            "GET /v1/knowledge/events",
                            "GET /v1/knowledge/events/{id}",
                            "POST /v1/knowledge/search",
                            "POST /v1/knowledge/event",
                            "POST /v1/knowledge/context",
                            "POST /v1/knowledge/reindex",
                        ],
                    },
                )
                return
            if path == "/health" and method == "GET":
                self._send(200, self._health())
                return
            if path == "/v1/knowledge/stats" and method == "GET":
                self._send(200, self.kb.stats())
                return
            if path == "/v1/knowledge/events" and method == "GET":
                items = self.kb.list_events()
                self._send(200, {"count": len(items), "items": items})
                return
            if path.startswith("/v1/knowledge/events/") and method == "GET":
                event_id = path.rsplit("/", 1)[-1]
                event = self.kb.get_event(event_id)
                if event is None:
                    raise ApiError(404, "not_found", f"事件 {event_id} 不存在")
                self._send(200, event)
                return
            if path == "/v1/knowledge/search" and method == "GET":
                q = (query.get("q") or query.get("query") or [""])[0]
                self._send(200, self._search({"query": q, **{k: v[0] for k, v in query.items() if k != "q"}}))
                return
            if path == "/v1/knowledge/search" and method == "POST":
                self._send(200, self._search(self._read_json()))
                return
            if path == "/v1/knowledge/event" and method == "POST":
                payload = self._read_json()
                name = str(payload.get("name") or payload.get("query") or "").strip()
                if not name:
                    raise ApiError(400, "invalid_argument", "name 不能为空")
                self._send(
                    200,
                    self.kb.event_card(
                        name,
                        top_k=_as_int(payload, "top_k") or 6,
                        budget=_as_int(payload, "budget_chars"),
                    ),
                )
                return
            if path == "/v1/knowledge/context" and method == "POST":
                payload = self._read_json()
                query = str(payload.get("query") or "").strip()
                if not query:
                    raise ApiError(400, "invalid_argument", "query 不能为空")
                result = self.kb.search(
                    query,
                    top_k=_as_int(payload, "top_k") or self.cfg.top_k,
                    filters=payload.get("filters") or {},
                    use_rerank=_as_bool(payload, "use_rerank", True),
                )
                budget = _as_int(payload, "budget_chars") or self.cfg.context_budget_chars
                items = result["items"]
                trimmed: list = []
                used = 0
                for item in items:
                    if used + len(item["text"]) > budget:
                        break
                    trimmed.append(item)
                    used += len(item["text"])
                self._send(
                    200,
                    {
                        "query": query,
                        "took_ms": result["took_ms"],
                        "resolved": result["resolved"],
                        "budget_chars": budget,
                        "used_chars": used,
                        "context": self.kb.build_context(trimmed, budget),
                        "items": trimmed,
                        "degraded": result["degraded"],
                    },
                )
                return
            if path == "/v1/knowledge/reindex" and method == "POST":
                self._require_admin()
                payload = self._read_json()
                force = _as_bool(payload, "force", False)
                started = time.perf_counter()
                if force:
                    stats = self.kb.rebuild()
                else:
                    self.kb.reload()
                    stats = self.kb.stats()
                stats["took_ms"] = round((time.perf_counter() - started) * 1000, 2)
                self._send(200, {"ok": True, **stats})
                return
            raise ApiError(404, "not_found", f"未知路径: {method} {path}")
        except ApiError as exc:
            self._send_error(exc.status, exc.code, str(exc))
        except ValueError as exc:
            self._send_error(400, "bad_request", str(exc))
        except Exception as exc:  # 兜底：不让异常打断服务
            logger.exception("处理请求失败: %s %s", method, self.path)
            self._send_error(500, "internal_error", f"{type(exc).__name__}: {exc}")

    # ---------------- 具体接口 ----------------
    def _health(self) -> Dict[str, Any]:
        stats = self.kb.stats()
        return {
            "status": "ok" if not stats.get("degraded") else "degraded",
            "service": "EpicSpark Knowledge RAG",
            "version": VERSION,
            "events": stats["events"],
            "chunks": stats["chunks"],
            "index_ready": bool(stats.get("index")),
            "embedding": stats["embedding"],
            "reranker": stats["reranker"],
            "degraded": stats["degraded"],
            "server_time": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        }

    def _search(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        query = str(payload.get("query") or payload.get("q") or "").strip()
        if not query:
            raise ApiError(400, "invalid_argument", "query 不能为空")
        filters = payload.get("filters")
        if filters is not None and not isinstance(filters, dict):
            raise ApiError(400, "invalid_argument", "filters 必须是对象")
        return self.kb.search(
            query,
            top_k=_as_int(payload, "top_k") or self.cfg.top_k,
            filters=filters or {},
            use_rerank=_as_bool(payload, "use_rerank", True),
            candidates=_as_int(payload, "candidates"),
        )

    # ---------------- HTTP 动词 ----------------
    def do_GET(self) -> None:  # noqa: N802 - 基类约定
        self._dispatch("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._dispatch("POST")

    def do_OPTIONS(self) -> None:  # noqa: N802
        self._dispatch("OPTIONS")


def create_server(cfg: RagConfig, kb: KnowledgeBase) -> ThreadingHTTPServer:
    handler = type("BoundHandler", (Handler,), {"kb": kb, "cfg": cfg})
    server = ThreadingHTTPServer((cfg.host, cfg.port), handler)
    server.daemon_threads = True
    return server
