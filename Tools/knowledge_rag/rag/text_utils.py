"""文本处理：归一化、中文二元分词、BM25 倒排索引、确定性特征哈希向量。

中文不做词典分词（避免引入 jieba 等依赖），改用「字符二元组 + 单字」作为词项：
对历史检索这类专名密集的场景，二元组的召回率与可解释性都足够，且完全确定性，
因此在没有 API Key 的离线环境下也能得到稳定、可复现的检索结果。
"""

from __future__ import annotations

import hashlib
import math
import re
import unicodedata
from collections import Counter
from typing import Dict, Iterable, List, Sequence, Tuple

CJK_RANGE = "\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff"
CJK_RUN_RE = re.compile(f"[{CJK_RANGE}]+")
ASCII_WORD_RE = re.compile(r"[a-z0-9][a-z0-9_'+.\-]*")
HAS_CJK_RE = re.compile(f"[{CJK_RANGE}]")

_ZERO_WIDTH = dict.fromkeys(map(ord, "\u200b\u200c\u200d\ufeff"), None)


def normalize(text: str) -> str:
    """NFKC 归一化 + 小写 + 去零宽字符 + 压缩空白。

    NFKC 会把全角字母数字（ＡＢＣ、１２３）转成半角，避免用户输入法差异导致漏召回。
    """
    if not text:
        return ""
    text = unicodedata.normalize("NFKC", text).translate(_ZERO_WIDTH).lower()
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def tokenize(text: str) -> List[str]:
    """中文二元组 + 英文/数字词 + 单字兜底。

    - 连续中文按二元组切分（"法国大革命" -> 法国/国大/大革/革命）
    - 单字中文串（如"秦"）保留单字本身
    - 英文与数字按词切分并保留原形（便于匹配 "1789"、"French Revolution"）
    """
    text = normalize(text)
    if not text:
        return []
    tokens: List[str] = []
    for match in CJK_RUN_RE.finditer(text):
        run = match.group(0)
        if len(run) == 1:
            tokens.append(run)
            continue
        for i in range(len(run) - 1):
            tokens.append(run[i : i + 2])
    tokens.extend(ASCII_WORD_RE.findall(text))
    return tokens


def token_set(text: str) -> set:
    return set(tokenize(text))


# 只剥离语气/结构助词，不动实词；用于「法国的革命」->「法国革命」这类口语归一
_FILLER_CHARS = "的了吗呢吧啊呀是在把被和与及之"


def strip_fillers(text: str) -> str:
    """去掉口语助词，便于把用户说法归一成事件名。纯符号查询保留原样。"""
    normalized = normalize(text)
    stripped = re.sub(f"[{_FILLER_CHARS}]", "", normalized).strip()
    return stripped or normalized


def format_year(year: int) -> str:
    """把整数年份格式化成中文习惯写法：-30000 -> 前30000年，1789 -> 1789年。"""
    try:
        year = int(year)
    except (TypeError, ValueError):
        return str(year)
    if year < 0:
        return f"前{-year}年"
    return f"{year}年"


def parse_year(value: object, default: int = 0) -> int:
    """宽容地解析年份：1789 / "1789" / "1789年" / "约前1600" / "-1600" / "BCE 500" 等。

    语料由人工与模型协作编写，年份写法不统一是常态；
    这里统一收口，避免一个"约前1600"就让整条时间线被静默丢弃。
    """
    if isinstance(value, bool):
        return default
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(value)
    text = normalize(str(value or ""))
    if not text:
        return default
    negative = any(marker in text for marker in ("前", "bce", "bc", "公元前"))
    match = re.search(r"-?\d+(?:\.\d+)?", text)
    if not match:
        return default
    number = float(match.group(0))
    year = int(number)
    if year < 0:
        return year
    return -year if negative else year


def jaccard(a: set, b: set) -> float:
    if not a or not b:
        return 0.0
    return len(a & b) / float(len(a | b))


def coverage(query_tokens: set, doc_tokens: set) -> float:
    """查询词项被文档覆盖的比例，衡量"问的东西查到没有"。"""
    if not query_tokens:
        return 0.0
    return len(query_tokens & doc_tokens) / float(len(query_tokens))


# --------------------------------------------------------------------------
# BM25
# --------------------------------------------------------------------------


class BM25Index:
    """内存 BM25（Okapi）。语料规模是千级文本块，内存倒排完全够用。"""

    def __init__(self, k1: float = 1.2, b: float = 0.75) -> None:
        self.k1 = k1
        self.b = b
        self._postings: Dict[str, List[Tuple[int, int]]] = {}
        self._doc_len: List[int] = []
        self._doc_ids: List[str] = []
        self._avg_len: float = 0.0
        self._idf: Dict[str, float] = {}
        self._finalized = False

    def __len__(self) -> int:
        return len(self._doc_ids)

    def add(self, doc_id: str, text: str) -> int:
        idx = len(self._doc_ids)
        self._doc_ids.append(doc_id)
        tokens = tokenize(text)
        self._doc_len.append(len(tokens))
        for term, tf in Counter(tokens).items():
            self._postings.setdefault(term, []).append((idx, tf))
        return idx

    def finalize(self) -> None:
        n = len(self._doc_ids)
        if n == 0:
            self._avg_len = 0.0
            self._finalized = True
            return
        self._avg_len = sum(self._doc_len) / float(n)
        self._idf = {}
        for term, postings in self._postings.items():
            df = len(postings)
            # Lucene 变体：保证 df == n 时 IDF 仍为正，避免公共词项被压成 0 分
            self._idf[term] = math.log(1.0 + (n - df + 0.5) / (df + 0.5))
        self._finalized = True

    def search(self, query: str, limit: int = 50) -> List[Tuple[str, float]]:
        if not self._finalized:
            self.finalize()
        q_terms = Counter(tokenize(query))
        if not q_terms or not self._doc_ids:
            return []
        scores = [0.0] * len(self._doc_ids)
        avg = self._avg_len or 1.0
        for term, qtf in q_terms.items():
            postings = self._postings.get(term)
            if not postings:
                continue
            idf = self._idf.get(term, 0.0)
            for doc_idx, tf in postings:
                dl = self._doc_len[doc_idx] or 1
                denom = tf + self.k1 * (1.0 - self.b + self.b * dl / avg)
                scores[doc_idx] += qtf * idf * (tf * (self.k1 + 1.0)) / (denom or 1.0)
        ranked = sorted(
            ((self._doc_ids[i], s) for i, s in enumerate(scores) if s > 0.0),
            key=lambda pair: pair[1],
            reverse=True,
        )
        return ranked[:limit]


# --------------------------------------------------------------------------
# 确定性特征哈希向量（离线回退）
# --------------------------------------------------------------------------


def _feature_buckets(text: str) -> Iterable[Tuple[str, float]]:
    """把文本拆成带权重的特征：单字 0.3、二元 1.0、三元 0.6、英文词 1.0。"""
    norm = normalize(text)
    for match in CJK_RUN_RE.finditer(norm):
        run = match.group(0)
        if len(run) == 1:
            yield run, 0.3
            continue
        for i, ch in enumerate(run):
            yield ch, 0.3
            if i + 1 < len(run):
                yield run[i : i + 2], 1.0
            if i + 2 < len(run):
                yield run[i : i + 3], 0.6
    for word in ASCII_WORD_RE.findall(norm):
        yield word, 1.0


def _hash_to_bucket(feature: str, dim: int) -> Tuple[int, int]:
    digest = hashlib.blake2b(feature.encode("utf-8"), digest_size=8).digest()
    index = int.from_bytes(digest[:4], "little") % dim
    sign = 1 if digest[4] & 1 else -1
    return index, sign


def hashing_embed(text: str, dim: int = 1024) -> List[float]:
    """确定性特征哈希向量（sublinear TF + L2 归一化）。

    不使用 Python 内建 hash()，因为它在每个进程带随机盐；
    blake2b 保证同一文本在任何时间、任何机器上得到同一向量。
    """
    if dim <= 0:
        raise ValueError("dim 必须为正整数")
    counts: Counter = Counter()
    for feature, weight in _feature_buckets(text):
        counts[feature] += weight
    vector = [0.0] * dim
    for feature, tf in counts.items():
        index, sign = _hash_to_bucket(feature, dim)
        vector[index] += sign * (1.0 + math.log(tf))
    norm = math.sqrt(sum(v * v for v in vector))
    if norm > 0:
        inv = 1.0 / norm
        vector = [v * inv for v in vector]
    return vector


def l2_normalize(vector: Sequence[float]) -> List[float]:
    norm = math.sqrt(sum(v * v for v in vector))
    if norm <= 0:
        return list(vector)
    inv = 1.0 / norm
    return [v * inv for v in vector]


def dot(a: Sequence[float], b: Sequence[float]) -> float:
    return float(sum(x * y for x, y in zip(a, b)))


def cosine(a: Sequence[float], b: Sequence[float]) -> float:
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    if na <= 0 or nb <= 0:
        return 0.0
    return dot(a, b) / (na * nb)
