# 知识库语料格式

本目录下的 `*.jsonl` 是知识库的唯一事实来源（source of truth）。索引由
`build_knowledge_base.py` 从这里重建，因此**直接改 JSONL 即可增删知识**，
不需要动代码。

## 文件组织

| 文件 | 内容 |
|---|---|
| `china.jsonl` | 中国史（古代 12 个 + 近现代 12 个事件） |
| `world.jsonl` | 世界史（古代 / 中古 / 近代 / 现代） |

新增文件只要放在本目录并以 `.jsonl` 结尾就会被自动加载，加载顺序按文件名排序，
同 `id` 的后出现者覆盖先出现者（用于临时打补丁，不建议长期重复）。

## 一行一个事件

每行必须是一个完整 JSON 对象，**不能换行、不能有逗号粘连、不能包成数组**。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | ✅ | 小写字母/数字/下划线，全局唯一，如 `fr_french_revolution` |
| `title` | string | ✅ | 事件中文名 |
| `aliases` | string[] | | 别名、简称、英文名，用于把用户的不同说法映射到同一事件 |
| `era` | string | ✅ | 只能是：中国古代史 / 中国近现代史 / 世界古代史 / 世界中古史 / 世界近代史 / 世界现代史 |
| `region` | string | ✅ | 如 `法国/欧洲`、`中国/黄河中下游` |
| `period` | string | ✅ | 人类可读年代，如 `1789—1799年` |
| `start_year` | int | ✅ | 公元前为负数（`-1600`）；用于年代过滤与排序 |
| `end_year` | int | ✅ | 单点事件与 `start_year` 相同，不得早于 `start_year` |
| `one_liner` | string | | ≤40 字的一句话概括，会并入 `background` 块 |
| `sections` | object | ✅ | 至少包含 `background` / `process` / `impact` |
| `sections.background` | string | ✅ | 背景与起因，60—220 字 |
| `sections.process` | string | ✅ | 经过与关键节点，60—220 字 |
| `sections.impact` | string | ✅ | 影响与意义，60—220 字 |
| `sections.figures` | string | | 关键人物 |
| `sections.terms` | string | | 核心概念与制度 |
| `sections.sites` | string | | 地理与遗存 |
| `sections.misconceptions` | string | | 常见误区与史学辨析 |
| `timeline` | array | | `[{"year": 1789, "text": "攻占巴士底狱"}]`，3—6 条 |
| `keywords` | string[] | | 5—10 个检索关键词，参与别名块与事件解析 |
| `gameplay_hooks` | string[] | | 给策划的可玩场景/冲突点，供剧情 Agent 参考 |
| `sources` | array | | `[{"title": "...", "url": "..."}]`，会随检索结果一起返回给 Agent |

## 切块规则（代码侧）

`rag/corpus.py` 把每个事件切成若干文本块，每块都带事件级上下文头，
例如：

```
《法国大革命》（1789—1799年｜世界近代史｜法国/欧洲）经过与关键节点：1789年7月14日……
```

块类型：`background` / `process` / `impact` / `figures` / `terms` / `sites` /
`misconceptions` / `timeline` / `aliases`。`chunk_id` 形如
`fr_french_revolution##process`，检索结果与前端引用都基于它。

## 校验

```powershell
python build_knowledge_base.py --check
```

会输出事件数、块数、时代分布、章节分布与语料指纹；发现格式错误会直接报
「文件:行号 + 字段名」。

## 撰写口径

- 以中学历史与通行学术常识为准；数字不确定就用「约」，并在 `misconceptions` 中说明争议。
- 只做史实陈述，不做价值判断，避免现代政治敏感表述。
- 别名与关键词要覆盖用户可能输入的口语说法，否则检索会漏。
- 每个事件的 `sources` 尽量给可访问链接，便于教研复核。
