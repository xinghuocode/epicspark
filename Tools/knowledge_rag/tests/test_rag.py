"""知识库冒烟与契约测试。

运行::

    C:\\...\\python.exe -m unittest discover -s tests -v
    C:\\...\\python.exe tests\\test_rag.py

全部离线运行（local 嵌入 + local 重排），不消耗任何 API 额度。
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import threading
import unittest
import urllib.error
import urllib.request
import uuid
from pathlib import Path

PACKAGE_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PACKAGE_ROOT))

from rag.config import load_config  # noqa: E402
from rag.corpus import build_chunks, load_events  # noqa: E402
from rag.http_api import create_server  # noqa: E402
from rag.retriever import KnowledgeBase  # noqa: E402
from rag.text_utils import (  # noqa: E402
    BM25Index,
    format_year,
    hashing_embed,
    normalize,
    parse_year,
    tokenize,
)
from rag.vector_store import VectorIndex  # noqa: E402

DATA_DIR = PACKAGE_ROOT / "data"
# 测试临时目录放在包内（已 gitignore）。
# 注意：这里不用 tempfile.mkdtemp —— 它在 Windows 上创建 0700 的目录会切断 ACL 继承，
# 在受限/沙箱环境里该目录会立刻不可写；os.makedirs 创建的目录正常继承权限。
TMP_ROOT = PACKAGE_ROOT / ".test-tmp"


def make_temp(prefix: str) -> str:
    path = TMP_ROOT / (prefix + uuid.uuid4().hex[:10])
    path.mkdir(parents=True, exist_ok=True)
    return str(path)


def offline_config(index_dir: str):
    return load_config(
        data_dir=str(DATA_DIR),
        index_dir=index_dir,
        embed_provider="local",
        rerank_provider="local",
        cache_dir=str(Path(index_dir) / "cache"),
        log_level="WARNING",
    )


class TextUtilsTest(unittest.TestCase):
    def test_normalize_fullwidth_and_space(self):
        self.assertEqual(normalize("  法国　大革命　"), "法国 大革命")
        self.assertEqual(normalize("ＡＢＣ１２３"), "abc123")

    def test_tokenize_chinese_bigram(self):
        tokens = tokenize("法国大革命")
        self.assertIn("法国", tokens)
        self.assertIn("革命", tokens)

    def test_tokenize_ascii_word(self):
        self.assertIn("1789", tokenize("1789年革命"))
        self.assertIn("french", tokenize("French Revolution"))

    def test_hashing_embed_deterministic_and_unit_norm(self):
        a = hashing_embed("法国大革命", 256)
        b = hashing_embed("法国大革命", 256)
        self.assertEqual(a, b)
        norm = sum(v * v for v in a) ** 0.5
        self.assertAlmostEqual(norm, 1.0, places=5)
        self.assertEqual(len(a), 256)

    def test_bm25_prefers_relevant_document(self):
        index = BM25Index()
        index.add("a", "法国大革命攻占巴士底狱")
        index.add("b", "山顶洞人使用打制石器")
        index.finalize()
        ranked = index.search("巴士底狱", limit=2)
        self.assertEqual(ranked[0][0], "a")

    def test_parse_year_variants(self):
        self.assertEqual(parse_year(1789), 1789)
        self.assertEqual(parse_year("1789年"), 1789)
        self.assertEqual(parse_year("约前1600"), -1600)
        self.assertEqual(parse_year("-30000"), -30000)
        self.assertEqual(parse_year("BCE 500"), -500)
        self.assertEqual(parse_year("不详", default=7), 7)

    def test_format_year(self):
        self.assertEqual(format_year(-30000), "前30000年")
        self.assertEqual(format_year(1789), "1789年")


class CorpusTest(unittest.TestCase):
    def test_corpus_loads_and_chunks(self):
        events = load_events(DATA_DIR / "events")
        self.assertGreaterEqual(len(events), 20, "知识库事件数量不足")
        chunks = build_chunks(events)
        self.assertGreater(len(chunks), len(events))
        ids = {chunk.chunk_id for chunk in chunks}
        self.assertEqual(len(ids), len(chunks), "chunk_id 必须唯一")
        for event in events:
            for required in ("background", "process", "impact"):
                self.assertIn(required, event["sections"], f"{event['id']} 缺少 {required}")
        for chunk in chunks:
            self.assertIn("《", chunk.text)
            self.assertGreater(len(chunk.text), 20)

    def test_corpus_covers_documented_examples(self):
        events = load_events(DATA_DIR / "events")
        titles = " ".join(event["title"] for event in events)
        self.assertIn("法国大革命", titles)
        self.assertIn("山顶洞人", titles)


class IndexTest(unittest.TestCase):
    def setUp(self):
        self.tmp = make_temp("rag-index-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_vector_index_roundtrip(self):
        events = load_events(DATA_DIR / "events")[:3]
        chunks = build_chunks(events)
        vectors = [hashing_embed(chunk.text, 64) for chunk in chunks]
        index = VectorIndex.from_vectors(chunks, vectors, embed_info={"kind": "test"}, fingerprint="x")
        index.save(self.tmp)
        loaded = VectorIndex.load(self.tmp)
        self.assertEqual(loaded.dim, 64)
        self.assertEqual(len(loaded.chunks), len(chunks))
        hits = loaded.search(vectors[0], limit=2)
        self.assertEqual(hits[0][0], 0)
        self.assertGreater(hits[0][1], 0.99)


class RetrievalTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = make_temp("rag-kb-")
        cls.kb = KnowledgeBase(offline_config(cls.tmp), probe=False)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_search_returns_ranked_items(self):
        result = self.kb.search("法国大革命", top_k=5)
        self.assertTrue(result["items"])
        titles = [item["title"] for item in result["items"]]
        self.assertIn("法国大革命", titles)
        scores = [item["scores"]["final"] for item in result["items"]]
        self.assertEqual(scores, sorted(scores, reverse=True))
        self.assertTrue(result["context"])

    def test_resolved_event_detection(self):
        result = self.kb.search("巴士底狱", top_k=5)
        self.assertIsNotNone(result["resolved"])
        self.assertEqual(result["resolved"]["event_id"], "fr_french_revolution")

    def test_search_handles_alias(self):
        result = self.kb.search("大革命", top_k=3)
        self.assertIn("法国大革命", [item["title"] for item in result["items"]])

    def test_event_card_for_documented_example(self):
        card = self.kb.event_card("山顶洞人", top_k=4)
        self.assertIsNotNone(card["matched"])
        self.assertEqual(card["event"]["title"], "山顶洞人")
        self.assertTrue(card["evidences"])
        self.assertIn("山顶洞人", card["context"])
        self.assertTrue(card["event"]["timeline"])

    def test_event_card_unknown_event_falls_back_to_search(self):
        card = self.kb.event_card("某个不存在的历史事件名", top_k=3)
        self.assertIsNone(card["matched"])
        self.assertIsNone(card["event"])

    def test_era_filter(self):
        result = self.kb.search("革命", top_k=10, filters={"era": "世界近代史"})
        for item in result["items"]:
            self.assertEqual(item["era"], "世界近代史")

    def test_context_budget_respected(self):
        result = self.kb.search("工业革命", top_k=8)
        context = self.kb.build_context(result["items"], budget=200)
        self.assertLessEqual(len(context), 200)

    def test_diversity_limit(self):
        result = self.kb.search("改革", top_k=10)
        counts = {}
        for item in result["items"]:
            counts[item["event_id"]] = counts.get(item["event_id"], 0) + 1
        self.assertLessEqual(max(counts.values()), self.kb.cfg.max_chunks_per_event + 2)

    def test_stats(self):
        stats = self.kb.stats()
        self.assertGreater(stats["events"], 20)
        self.assertGreater(stats["chunks"], stats["events"])
        self.assertIn("eras", stats)


class HttpApiTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = make_temp("rag-http-")
        cfg = offline_config(cls.tmp)
        cfg.host = "127.0.0.1"
        cfg.port = 0
        cls.cfg = cfg
        cls.kb = KnowledgeBase(cfg, probe=False)
        cls.server = create_server(cfg, cls.kb)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=3)
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def _get(self, path: str):
        with urllib.request.urlopen(f"http://127.0.0.1:{self.port}{path}", timeout=10) as response:
            return response.status, json.loads(response.read().decode("utf-8"))

    def _post(self, path: str, payload: dict):
        request = urllib.request.Request(
            f"http://127.0.0.1:{self.port}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=15) as response:
            return response.status, json.loads(response.read().decode("utf-8"))

    def test_health(self):
        status, payload = self._get("/health")
        self.assertEqual(status, 200)
        self.assertEqual(payload["status"], "ok")
        self.assertTrue(payload["index_ready"])

    def test_search_post(self):
        status, payload = self._post("/v1/knowledge/search", {"query": "法国大革命", "top_k": 3})
        self.assertEqual(status, 200)
        self.assertTrue(payload["items"])
        self.assertIn("context", payload)

    def test_search_get(self):
        status, payload = self._get("/v1/knowledge/search?q=%E5%B1%B1%E9%A1%B6%E6%B4%9E%E4%BA%BA&top_k=2")
        self.assertEqual(status, 200)
        self.assertEqual(payload["items"][0]["title"], "山顶洞人")

    def test_event_endpoint(self):
        status, payload = self._post("/v1/knowledge/event", {"name": "工业革命", "top_k": 4})
        self.assertEqual(status, 200)
        self.assertEqual(payload["event"]["title"], "工业革命")

    def test_context_endpoint(self):
        status, payload = self._post(
            "/v1/knowledge/context", {"query": "辛亥革命", "budget_chars": 300}
        )
        self.assertEqual(status, 200)
        self.assertLessEqual(len(payload["context"]), 320)

    def test_unknown_path_404(self):
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            self._get("/nope")
        self.assertEqual(ctx.exception.code, 404)

    def test_bad_request_400(self):
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            self._post("/v1/knowledge/search", {"query": ""})
        self.assertEqual(ctx.exception.code, 400)


if __name__ == "__main__":
    unittest.main(verbosity=2)
