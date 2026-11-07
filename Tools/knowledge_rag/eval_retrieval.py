"""检索质量评估：用固定的查询集量化 hit@k / MRR。

用法::

    python eval_retrieval.py                 # 离线向量 + 本地重排
    python eval_retrieval.py --embed-provider openai   # 接 ai.gitee.com 官方模型

查询集刻意覆盖三类说法：
1. 事件标准名（「法国大革命」）
2. 口语/别称（「大革命」「甲午海战」）
3. 主题式提问（「工业革命为什么从英国开始」）——考察语义泛化能力
"""

from __future__ import annotations

import argparse
import logging
import sys
import time
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from rag.config import load_config  # noqa: E402
from rag.retriever import KnowledgeBase  # noqa: E402

# (查询, 期望命中的事件 id, 说明)
CASES = [
    ("法国大革命", "fr_french_revolution", "标准名"),
    ("大革命", "fr_french_revolution", "简称/别名"),
    ("巴士底狱", "fr_french_revolution", "关键词"),
    ("山顶洞人", "cn_shangdingdong", "标准名"),
    ("北京猿人", "cn_beijing_man", "别称"),
    ("甲骨文", "cn_shang_dynasty", "关键词"),
    ("商鞅变法", "cn_shang_yang", "标准名"),
    ("秦灭六国", "cn_qin_unification", "口语说法"),
    ("科举制是什么", "cn_keju", "主题式提问"),
    ("甲午海战", "cn_jiawu_war", "口语别称"),
    ("辛亥革命", "cn_xinhai_revolution", "标准名"),
    ("五四运动", "cn_new_culture_mayfourth", "标准名"),
    ("抗日战争", "cn_anti_japanese_war", "标准名"),
    ("金字塔", "eg_giza_pyramids", "关键词"),
    ("汉谟拉比法典", "mesopotamia_hammurabi_code", "标准名"),
    ("雅典民主", "gr_athenian_democracy", "主题"),
    ("文艺复兴", "renaissance_italy", "标准名"),
    ("工业革命为什么从英国开始", "industrial_revolution", "主题式提问"),
    ("新航路开辟", "age_of_discovery", "标准名"),
    ("启蒙运动", "enlightenment_europe", "标准名"),
    ("第一次世界大战", "world_war_i", "标准名"),
    ("十月革命", "russia_october_revolution", "标准名"),
    ("冷战", "cold_war_bipolar", "标准名"),
    ("苏联解体", "soviet_union_dissolution", "标准名"),
    ("玛雅", "world_maya_civilization", "简称"),
    ("印加", "world_inca_empire", "简称"),
    ("郑和下西洋", "cn_zheng_he_voyages", "标准名"),
    ("拜占庭", "world_byzantine_empire", "简称"),
    ("大宪章", "world_magna_carta", "简称"),
    ("丝绸之路", "cn_silk_road", "关键词"),
]


def parse_args(argv: list | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="知识库检索质量评估")
    parser.add_argument("--top-k", type=int, default=5)
    parser.add_argument("--data-dir", default=None)
    parser.add_argument("--index-dir", default=None)
    parser.add_argument("--embed-provider", default=None, choices=["auto", "openai", "local"])
    parser.add_argument("--rerank-provider", default=None, choices=["auto", "openai", "local"])
    parser.add_argument("--no-rerank", action="store_true")
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--log-level", default="ERROR")
    return parser.parse_args(argv)


def main(argv: list | None = None) -> int:
    args = parse_args(argv)
    cfg = load_config(
        data_dir=args.data_dir,
        index_dir=args.index_dir,
        embed_provider=args.embed_provider,
        rerank_provider=args.rerank_provider,
        log_level=args.log_level,
    )
    logging.basicConfig(level=getattr(logging, args.log_level.upper(), logging.ERROR))
    kb = KnowledgeBase(cfg, probe=False)

    known = {event["id"] for event in kb.events}
    missing = sorted({expected for _, expected, _ in CASES if expected not in known} - {"ww1", "cold_war", "eu_industrial_revolution"})
    if missing:
        print("警告：查询集的期望事件 id 不在语料中，请先核对语料 id：" + ", ".join(missing))

    hits = {1: 0, 3: 0, 5: 0}
    resolved_first = 0
    reciprocal_rank = 0.0
    total = 0
    elapsed = 0.0

    for query, expected, note in CASES:
        started = time.perf_counter()
        result = kb.search(query, top_k=args.top_k, use_rerank=not args.no_rerank)
        elapsed += time.perf_counter() - started
        total += 1

        ids = [item["event_id"] for item in result["items"]]
        first_rank = next((rank for rank, eid in enumerate(ids, start=1) if eid == expected), 0)
        for k in hits:
            if expected in ids[:k]:
                hits[k] += 1
        if first_rank:
            reciprocal_rank += 1.0 / first_rank
        resolved = (result.get("resolved") or {}).get("event_id")
        if resolved == expected:
            resolved_first += 1

        if args.verbose:
            top = result["items"][0] if result["items"] else None
            flag = "OK " if expected in ids[:3] else "MISS"
            print(
                f"{flag} {query:<18} 期望={expected:<28} 首位={top['event_id'] if top else '-':<28} "
                f"resolved={resolved or '-'}"
            )

    def pct(value: int) -> str:
        return f"{value / total * 100:5.1f}%"

    print(f"\n查询数: {total}    平均耗时: {elapsed / total * 1000:.1f} ms/query")
    print(f"hit@1: {pct(hits[1])}    hit@3: {pct(hits[3])}    hit@5: {pct(hits[5])}")
    print(f"事件解析命中(首位): {pct(resolved_first)}    MRR: {reciprocal_rank / total:.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
