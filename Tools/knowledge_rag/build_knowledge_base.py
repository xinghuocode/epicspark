"""离线构建/校验知识库索引。

用法::

    python build_knowledge_base.py --check          # 只校验语料，不做向量化
    python build_knowledge_base.py                  # 增量构建（复用 SQLite 向量缓存）
    python build_knowledge_base.py --force           # 忽略已有索引，重建
    python build_knowledge_base.py --embed-provider local   # 完全离线构建

构建产物::

    data/index/meta.json     chunk 元数据 + 嵌入模型信息 + 语料指纹
    data/index/vectors.f32   归一化 float32 向量矩阵
"""

from __future__ import annotations

import argparse
import datetime
import json
import logging
import sys
from collections import Counter
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from rag.config import load_config  # noqa: E402
from rag.corpus import build_chunks, corpus_fingerprint, load_events  # noqa: E402
from rag.embedding import EmbeddingError, build_embeddings  # noqa: E402
from rag.vector_store import VectorIndex  # noqa: E402


def parse_args(argv: list | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="构建 EpicSpark 知识库向量索引")
    parser.add_argument("--data-dir", default=None)
    parser.add_argument("--index-dir", default=None)
    parser.add_argument("--base-url", default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--embed-model", default=None)
    parser.add_argument("--embed-provider", default=None, choices=["auto", "openai", "local"])
    parser.add_argument("--check", action="store_true", help="只校验语料结构，不向量化")
    parser.add_argument("--force", action="store_true", help="忽略现有索引，强制重建")
    parser.add_argument("--log-level", default="INFO")
    return parser.parse_args(argv)


def validate_corpus(cfg) -> int:
    events = load_events(cfg.events_dir)
    chunks = build_chunks(events)
    eras = Counter(event["era"] for event in events)
    sections = Counter(chunk.section for chunk in chunks)
    print(f"语料目录: {cfg.events_dir}")
    print(f"事件数: {len(events)}，文本块数: {len(chunks)}")
    print("时代分布: " + "，".join(f"{era} {count}" for era, count in sorted(eras.items())))
    print("章节分布: " + "，".join(f"{name} {count}" for name, count in sections.most_common()))
    print(f"语料指纹: {corpus_fingerprint(chunks)}")
    short = [chunk for chunk in chunks if len(chunk.text) < 40]
    if short:
        print(f"警告: {len(short)} 个文本块过短（<40 字），可能影响召回质量")
        for chunk in short[:5]:
            print(f"  - {chunk.chunk_id}: {len(chunk.text)} 字")
    empty = [event["id"] for event in events if not event["keywords"]]
    if empty:
        print(f"警告: {len(empty)} 个事件缺少 keywords: {', '.join(empty[:5])}")
    print("校验通过 ✔")
    return 0


def main(argv: list | None = None) -> int:
    args = parse_args(argv)
    cfg = load_config(
        data_dir=args.data_dir,
        index_dir=args.index_dir,
        base_url=(args.base_url.rstrip("/") if args.base_url else None),
        api_key=args.api_key,
        embed_model=args.embed_model,
        embed_provider=args.embed_provider,
        log_level=args.log_level,
    )
    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )
    logger = logging.getLogger("rag.build")

    if args.check:
        try:
            return validate_corpus(cfg)
        except Exception as exc:
            logger.error("语料校验失败: %s", exc)
            return 1

    events = load_events(cfg.events_dir)
    chunks = build_chunks(events)
    fingerprint = corpus_fingerprint(chunks)
    logger.info("读取语料: %s 个事件 / %s 个文本块", len(events), len(chunks))

    if not args.force:
        try:
            existing = VectorIndex.load(cfg.index_dir)
            if existing.fingerprint == fingerprint:
                logger.info(
                    "索引已是最新（%s 块），如需重建请加 --force", len(existing.chunks)
                )
                print(json.dumps(existing.stats(), ensure_ascii=False, indent=2))
                return 0
        except Exception:
            pass

    provider = build_embeddings(cfg)
    logger.info("嵌入提供方: %s", provider.describe())
    try:
        vectors = provider.embed([chunk.text for chunk in chunks])
    except EmbeddingError as exc:
        logger.error("向量化失败: %s", exc)
        logger.error("可加 --embed-provider local 使用离线向量化")
        return 2

    index = VectorIndex.from_vectors(
        chunks,
        vectors,
        embed_info=provider.describe(),
        fingerprint=fingerprint,
        created_at=datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
    )
    index.save(cfg.index_dir)
    print(json.dumps(index.stats(), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
