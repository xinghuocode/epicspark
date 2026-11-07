"""命令行检索工具：不启服务也能验证知识库效果。

用法::

    python query.py "法国大革命"
    python query.py "山顶洞人" --top-k 3 --json
    python query.py --event "工业革命"        # 事件卡片模式（剧情 Agent 视角）
    python query.py "青铜器" --era 中国古代史 --no-rerank
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from rag.config import load_config  # noqa: E402
from rag.retriever import KnowledgeBase  # noqa: E402


def parse_args(argv: list | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="EpicSpark 知识库命令行检索")
    parser.add_argument("query", nargs="?", default="", help="检索词或事件名")
    parser.add_argument("--event", action="store_true", help="按事件卡片模式返回")
    parser.add_argument("--top-k", type=int, default=5)
    parser.add_argument("--era", default=None)
    parser.add_argument("--region", default=None)
    parser.add_argument("--no-rerank", action="store_true")
    parser.add_argument("--data-dir", default=None)
    parser.add_argument("--index-dir", default=None)
    parser.add_argument("--embed-provider", default=None, choices=["auto", "openai", "local"])
    parser.add_argument("--rerank-provider", default=None, choices=["auto", "openai", "local"])
    parser.add_argument("--json", action="store_true", dest="as_json")
    parser.add_argument("--log-level", default="WARNING")
    return parser.parse_args(argv)


def main(argv: list | None = None) -> int:
    args = parse_args(argv)
    if not args.query:
        print("请提供检索词，例如: python query.py \"法国大革命\"")
        return 2
    cfg = load_config(
        data_dir=args.data_dir,
        index_dir=args.index_dir,
        embed_provider=args.embed_provider,
        rerank_provider=args.rerank_provider,
        log_level=args.log_level,
    )
    logging.basicConfig(level=getattr(logging, args.log_level.upper(), logging.WARNING))
    kb = KnowledgeBase(cfg, probe=False)

    if args.event:
        result = kb.event_card(args.query, top_k=args.top_k)
        if args.as_json:
            print(json.dumps(result, ensure_ascii=False, indent=2))
            return 0
        matched = result.get("matched") or {}
        print(f"命中事件: {matched.get('title', '未命中')} （置信度 {matched.get('confidence', 0)}）")
        if result.get("event"):
            event = result["event"]
            print(f"年代: {event['period']} | {event['era']} | {event['region']}")
            print(f"一句话: {event['one_liner']}")
            print("时间线: " + "；".join(f"{item['year']} {item['text']}" for item in event["timeline"]))
        print("\n--- 上下文 ---")
        print(result.get("context", ""))
        return 0

    filters = {}
    if args.era:
        filters["era"] = args.era
    if args.region:
        filters["region"] = args.region
    result = kb.search(
        args.query, top_k=args.top_k, filters=filters, use_rerank=not args.no_rerank
    )
    if args.as_json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    print(f"查询: {result['query']}  用时 {result['took_ms']}ms  候选 {result['total_candidates']}")
    if result.get("resolved"):
        print(f"事件解析: {result['resolved']['title']} ({result['resolved']['matched_by']})")
    for item in result["items"]:
        scores = item["scores"]
        print(
            f"\n[{item['rank']}] {item['title']} | {item['section_label']} | "
            f"final={scores['final']} dense={scores['dense']} rerank={scores['rerank']}"
        )
        print("    " + item["text"][:160].replace("\n", " "))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
