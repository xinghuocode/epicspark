"""语料层：加载 JSONL 事件卡片，校验字段，切分为可检索的文本块。

事件卡片格式（每行一个 JSON 对象）见 ``data/events/README.md``。切块规则很朴素但有效：
一个事件拆成「背景 / 经过 / 影响 / 人物 / 概念 / 地理 / 辨析」等语义块，
每个块都带上事件级上下文（标题、年代、时代、地区），使短文本块也能独立成义，
既利于向量检索，也让重排模型拿到完整语境。
"""

from __future__ import annotations

import difflib
import hashlib
import json
import re
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from .text_utils import coverage, format_year, normalize, parse_year, strip_fillers, token_set

REQUIRED_SECTIONS = ("background", "process", "impact")

SECTION_LABELS: Dict[str, str] = {
    "background": "背景与起因",
    "process": "经过与关键节点",
    "impact": "影响与意义",
    "figures": "关键人物",
    "terms": "核心概念与制度",
    "sites": "地理与遗存",
    "misconceptions": "常见误区与史学辨析",
    "timeline": "时间线",
    "aliases": "别名与关键词",
}

SECTION_ORDER = [
    "background",
    "process",
    "impact",
    "figures",
    "terms",
    "sites",
    "misconceptions",
    "timeline",
    "aliases",
]

ERA_WHITELIST = {
    "中国古代史",
    "中国近现代史",
    "世界古代史",
    "世界中古史",
    "世界近代史",
    "世界现代史",
}


class CorpusError(ValueError):
    """语料格式错误，异常信息里带上文件与行号，便于直接定位。"""


@dataclass
class Chunk:
    chunk_id: str
    event_id: str
    title: str
    section: str
    section_label: str
    text: str
    era: str
    region: str
    period: str
    start_year: int
    end_year: int
    keywords: List[str] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, raw: Dict[str, Any]) -> "Chunk":
        return cls(
            chunk_id=raw["chunk_id"],
            event_id=raw["event_id"],
            title=raw["title"],
            section=raw["section"],
            section_label=raw.get("section_label", raw["section"]),
            text=raw["text"],
            era=raw.get("era", ""),
            region=raw.get("region", ""),
            period=raw.get("period", ""),
            start_year=int(raw.get("start_year", 0)),
            end_year=int(raw.get("end_year", 0)),
            keywords=list(raw.get("keywords") or []),
        )


# --------------------------------------------------------------------------
# 加载与校验
# --------------------------------------------------------------------------


def _require_str(payload: Dict[str, Any], key: str, where: str) -> str:
    value = payload.get(key)
    if not isinstance(value, str) or not value.strip():
        raise CorpusError(f"{where}: 字段 {key} 缺失或不是非空字符串")
    return value.strip()


def validate_event(payload: Any, where: str) -> Dict[str, Any]:
    if not isinstance(payload, dict):
        raise CorpusError(f"{where}: 顶层的每一行必须是 JSON 对象")
    event = dict(payload)

    event["id"] = _require_str(event, "id", where)
    if not re.fullmatch(r"[a-z0-9_]+", event["id"]):
        raise CorpusError(f"{where}: id 只能包含小写字母、数字与下划线: {event['id']}")
    event["title"] = _require_str(event, "title", where)
    event["era"] = _require_str(event, "era", where)
    if event["era"] not in ERA_WHITELIST:
        raise CorpusError(f"{where}: era 不在允许集合内: {event['era']}")
    event["region"] = _require_str(event, "region", where)
    event["period"] = _require_str(event, "period", where)

    try:
        event["start_year"] = parse_year(event["start_year"])
        event["end_year"] = parse_year(event["end_year"], default=event["start_year"])
    except (KeyError, TypeError, ValueError):
        raise CorpusError(f"{where}: start_year / end_year 必须是年份（整数，公元前用负数）")
    if event["end_year"] < event["start_year"]:
        raise CorpusError(f"{where}: end_year 早于 start_year")

    sections = event.get("sections")
    if not isinstance(sections, dict) or not sections:
        raise CorpusError(f"{where}: sections 必须是非空对象")
    missing = [name for name in REQUIRED_SECTIONS if not str(sections.get(name, "")).strip()]
    if missing:
        raise CorpusError(f"{where}: sections 缺少必需章节 {missing}")
    event["sections"] = {
        str(k): str(v).strip() for k, v in sections.items() if str(v).strip()
    }

    for key in ("aliases", "keywords", "gameplay_hooks"):
        value = event.get(key) or []
        if not isinstance(value, list):
            raise CorpusError(f"{where}: {key} 必须是字符串数组")
        event[key] = [str(item).strip() for item in value if str(item).strip()]

    event["one_liner"] = str(event.get("one_liner") or "").strip()
    event.setdefault("sources", [])
    if not isinstance(event["sources"], list):
        raise CorpusError(f"{where}: sources 必须是数组")

    timeline = event.get("timeline") or []
    if not isinstance(timeline, list):
        raise CorpusError(f"{where}: timeline 必须是数组")
    cleaned_timeline = []
    for item in timeline:
        if not isinstance(item, dict):
            continue
        year = parse_year(item.get("year"), default=0)
        text = str(item.get("text") or "").strip()
        if text:
            cleaned_timeline.append({"year": year, "text": text})
    event["timeline"] = cleaned_timeline
    return event


def load_events(events_dir: str | Path) -> List[Dict[str, Any]]:
    """读取目录下所有 *.jsonl，按 id 去重（后者覆盖前者并记录告警）。"""
    directory = Path(events_dir)
    if not directory.exists():
        raise CorpusError(f"语料目录不存在: {directory}")

    events: Dict[str, Dict[str, Any]] = {}
    order: List[str] = []
    files = sorted(directory.glob("*.jsonl"))
    if not files:
        raise CorpusError(f"语料目录下没有 *.jsonl 文件: {directory}")

    for path in files:
        # utf-8-sig：兼容 Windows 记事本 / PowerShell 写入的 BOM，否则首行必然解析失败
        with path.open("r", encoding="utf-8-sig") as handle:
            for lineno, line in enumerate(handle, start=1):
                line = line.strip()
                if not line or line.startswith("//"):
                    continue
                where = f"{path.name}:{lineno}"
                try:
                    payload = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise CorpusError(f"{where}: JSON 解析失败 ({exc})") from exc
                event = validate_event(payload, where)
                if event["id"] in events:
                    order.remove(event["id"])
                events[event["id"]] = event
                order.append(event["id"])
    return [events[event_id] for event_id in order]


# --------------------------------------------------------------------------
# 切块
# --------------------------------------------------------------------------


def _chunk_text(event: Dict[str, Any], label: str, body: str) -> str:
    header = f"《{event['title']}》（{event['period']}｜{event['era']}｜{event['region']}）"
    return f"{header}{label}：{body}"


def chunk_event(event: Dict[str, Any]) -> List[Chunk]:
    chunks: List[Chunk] = []

    def add(section: str, body: str) -> None:
        body = (body or "").strip()
        if not body:
            return
        chunks.append(
            Chunk(
                chunk_id=f"{event['id']}##{section}",
                event_id=event["id"],
                title=event["title"],
                section=section,
                section_label=SECTION_LABELS.get(section, section),
                text=_chunk_text(event, SECTION_LABELS.get(section, section), body),
                era=event["era"],
                region=event["region"],
                period=event["period"],
                start_year=event["start_year"],
                end_year=event["end_year"],
                keywords=event["keywords"],
            )
        )

    if event["one_liner"]:
        add("background", event["one_liner"] + "。" + event["sections"].get("background", ""))

    for section in SECTION_ORDER:
        if section in ("background", "timeline", "aliases"):
            continue
        add(section, event["sections"].get(section, ""))

    if event["timeline"]:
        timeline_text = "；".join(
            f"{format_year(item['year'])} {item['text']}" for item in event["timeline"]
        )
        add("timeline", timeline_text)

    alias_bits = []
    if event["aliases"]:
        alias_bits.append("别名：" + "、".join(event["aliases"]))
    if event["keywords"]:
        alias_bits.append("关键词：" + "、".join(event["keywords"]))
    if event["one_liner"]:
        alias_bits.append("概要：" + event["one_liner"])
    add("aliases", "；".join(alias_bits))

    # one_liner 已并入 background 首句，避免重复块
    if not event["one_liner"]:
        add("background", event["sections"].get("background", ""))

    return chunks


def build_chunks(events: Sequence[Dict[str, Any]]) -> List[Chunk]:
    chunks: List[Chunk] = []
    seen: set = set()
    for event in events:
        for chunk in chunk_event(event):
            if chunk.chunk_id in seen:
                # 同一事件同一章节只允许一个块（例如 one_liner 合并逻辑的边界情况）
                continue
            seen.add(chunk.chunk_id)
            chunks.append(chunk)
    return chunks


def corpus_fingerprint(chunks: Sequence[Chunk]) -> str:
    digest = hashlib.sha256()
    for chunk in chunks:
        digest.update(chunk.chunk_id.encode("utf-8"))
        digest.update(b"\x1f")
        digest.update(chunk.text.encode("utf-8"))
        digest.update(b"\x1e")
    return digest.hexdigest()


# --------------------------------------------------------------------------
# 事件解析（用户输入 -> 事件卡片）
# --------------------------------------------------------------------------


def resolve_event(
    query: str,
    events: Sequence[Dict[str, Any]],
    min_ratio: float = 0.55,
) -> Tuple[Optional[Dict[str, Any]], float, str]:
    """把用户输入的任意说法映射到事件卡片。

    依次尝试：id/标题/别名精确匹配 -> 互相包含 -> 词项覆盖率 -> 编辑相似度。
    返回 (event, confidence, matched_by)。
    """
    q_raw = normalize(query)
    if not q_raw:
        return None, 0.0, "empty"
    # 「法国的革命」「山顶洞人的遗址」这类说法先去掉助词再匹配
    q = strip_fillers(query)

    best: Tuple[Optional[Dict[str, Any]], float, str] = (None, 0.0, "none")

    def consider(event: Dict[str, Any], score: float, how: str) -> None:
        nonlocal best
        if score > best[1]:
            best = (event, score, how)

    for event in events:
        if q_raw == normalize(event["id"]) or q == normalize(event["id"]):
            consider(event, 1.0, "id")
            continue
        names = [event["title"], *event["aliases"], *event["keywords"]]
        for name in names:
            n = normalize(name)
            if not n:
                continue
            ns = strip_fillers(name) or n
            if q_raw == n or q == ns:
                consider(event, 0.99, "exact")
            elif len(n) >= 2 and n in q_raw:
                consider(event, 0.92 + min(0.06, len(n) / 200.0), "contains")
            elif len(ns) >= 2 and ns in q:
                consider(event, 0.90 + min(0.06, len(ns) / 200.0), "contains")
            elif len(q_raw) >= 2 and q_raw in n:
                consider(event, 0.88, "contained_by")
            elif len(q) >= 2 and q in ns:
                consider(event, 0.86, "contained_by")

        q_tokens = token_set(q)
        name_tokens = token_set(strip_fillers(" ".join([event["title"], *event["aliases"]])))
        cov = coverage(q_tokens, name_tokens)
        if cov >= 0.5:
            consider(event, 0.6 + 0.3 * cov, "coverage")

        ratio = difflib.SequenceMatcher(None, q, strip_fillers(event["title"])).ratio()
        if ratio >= min_ratio:
            consider(event, 0.5 + 0.45 * (ratio - min_ratio) / (1 - min_ratio), "similarity")

    return best
