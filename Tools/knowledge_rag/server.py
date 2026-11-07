"""知识库检索服务入口。

用法::

    python server.py                     # 默认 127.0.0.1:8787，自动加载/重建索引
    python server.py --port 8800
    python server.py --embed-provider local --rerank-provider local   # 完全离线
    EPICSPARK_RAG_API_KEY=sk-xxx python server.py                      # 走 ai.gitee.com

服务只读内存索引，重建索引通过 POST /v1/knowledge/reindex 触发热加载。
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

if __package__ in (None, ""):  # 支持 `python server.py` 直接运行
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from rag.config import load_config  # noqa: E402
from rag.http_api import create_server  # noqa: E402
from rag.retriever import KnowledgeBase  # noqa: E402


def configure_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )


def parse_args(argv: list | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="EpicSpark 知识库检索服务")
    parser.add_argument("--host", default=None)
    parser.add_argument("--port", type=int, default=None)
    parser.add_argument("--data-dir", default=None)
    parser.add_argument("--index-dir", default=None)
    parser.add_argument("--base-url", default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--embed-model", default=None)
    parser.add_argument("--rerank-model", default=None)
    parser.add_argument(
        "--embed-provider", default=None, choices=["auto", "openai", "local"]
    )
    parser.add_argument(
        "--rerank-provider", default=None, choices=["auto", "openai", "local"]
    )
    parser.add_argument("--top-k", type=int, default=None)
    parser.add_argument("--admin-token", default=None)
    parser.add_argument("--log-level", default=None)
    parser.add_argument("--no-probe", action="store_true", help="跳过启动时的远端探活")
    return parser.parse_args(argv)


def main(argv: list | None = None) -> int:
    args = parse_args(argv)
    cfg = load_config(
        host=args.host,
        port=args.port,
        data_dir=args.data_dir,
        index_dir=args.index_dir,
        base_url=(args.base_url.rstrip("/") if args.base_url else None),
        api_key=args.api_key,
        embed_model=args.embed_model,
        rerank_model=args.rerank_model,
        embed_provider=args.embed_provider,
        rerank_provider=args.rerank_provider,
        top_k=args.top_k,
        admin_token=args.admin_token,
        log_level=args.log_level,
    )
    configure_logging(cfg.log_level)
    logger = logging.getLogger("rag.server")

    logger.info(
        "启动知识库服务：嵌入=%s，重排=%s，语料=%s",
        "远端 " + cfg.embed_model if cfg.use_remote_embeddings() else "本地特征哈希",
        "远端 " + cfg.rerank_model if cfg.use_remote_rerank() else "本地词汇重排",
        cfg.events_dir,
    )
    try:
        kb = KnowledgeBase(cfg, probe=not args.no_probe)
    except Exception as exc:
        logger.error("知识库初始化失败: %s", exc)
        return 1

    server = create_server(cfg, kb)
    stats = kb.stats()
    logger.info(
        "已就绪 http://%s:%s ｜ %s 事件 / %s 文本块 / %s 维",
        cfg.host,
        cfg.port,
        stats["events"],
        stats["chunks"],
        stats["index"].get("dim", "?"),
    )
    if stats.get("degraded"):
        logger.warning("当前处于降级模式: %s", stats["degraded"])
    logger.info("健康检查: http://%s:%s/health", cfg.host, cfg.port)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("收到中断，停止服务")
    finally:
        server.shutdown()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
