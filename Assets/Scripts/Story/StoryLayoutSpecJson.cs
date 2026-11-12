// 剧情&核心 Agent 直接给出的结构化布局（layoutSpec）→ 施工图 LayoutSpec。
//
// 为什么需要这一层：
//   此前剧情 Agent 只输出散文，施工图必须由 `StoryLayoutParser` 用正则重新"翻译"，
//   翻译丢了什么（喷泉直径、钟楼、四块地面、边界墙、NPC 走位）灰盒就少什么，
//   灰盒看起来"不听指挥"，其实是它收到的那份指挥本身就是残件。
//   现在剧情 Agent 必须同时给出 layoutSpec：灰盒只投影，不再二次解读散文。
//
// 解析原则：
//   1. 宽容：坐标/尺寸缺省、名称空缺、kind 大小写混写都要能接住；
//   2. 有界：所有坐标夹到场景范围内，数量设上限，防止模型输出把内存撑爆；
//   3. 不猜：模型没给的东西就留空（进 spec.gaps / 覆盖审计），不要用散文偷偷补。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EpicSpark.Core;
using UnityEngine;

#pragma warning disable 0649

namespace EpicSpark.Story
{
    // ─────────────────────────── 模型输出 DTO ───────────────────────────

    [Serializable]
    public class StoryLayoutDto
    {
        public StoryZoneDto[] zones;
        public StoryStreetDto[] streets;
        public StoryLandmarkDto[] landmarks;
        public StoryItemDto[] items;
        public StoryPathDto[] paths;
        public StoryNpcPlaceDto[] npcs;
    }

    /// <summary>地面分区：矩形 (x1,z1)—(x2,z2)。openness = 开敞程度（1 = 广场/荒地，0 = 密排建筑）。</summary>
    [Serializable]
    public class StoryZoneDto
    {
        public string name;
        public string kind;
        public float x1;
        public float z1;
        public float x2;
        public float z2;
        public float groundHeight;
        public float openness;
        public string material;
    }

    /// <summary>
    /// 街道：只给"从哪到哪"的折线 + 街宽 + 两侧是否连续临街。
    /// 开间数、进深、退线由求解器算——模型不该也不需要数房子。
    /// </summary>
    [Serializable]
    public class StoryStreetDto
    {
        public string name;
        public string nodes;
        public float width;
        public bool frontageBoth;
        public string style;
        public float depth;
        public int floors;
        public bool continuous = true;
    }

    /// <summary>
    /// 语义地标：只说"是什么、在哪个分区、朝向哪"，不给坐标和尺寸。
    /// 求解器负责把它贴到该分区的对应边缘，并保证立面朝向与临街关系一致。
    /// </summary>
    [Serializable]
    public class StoryLandmarkDto
    {
        public string name;
        public string kind;
        public string on;
        public string facing;
        public float width;
        public float depth;
        public float height;
        public string hint;
        public string semantic;
        public string anchorFor;
        public float x;
        public float z;
        public bool pinned;
    }

    /// <summary>一件地物：中心 (x,z) + 占地 width×depth（可带高度 height 与朝向 rotation）。</summary>
    [Serializable]
    public class StoryItemDto
    {
        public string name;
        public string kind;
        public float x;
        public float z;
        public float width;
        public float depth;
        public float height;
        public float rotation;
        public string hint;
        public string semantic;
        public string rowGroup;
        public int rowIndex;
        public int rowCount;
    }

    /// <summary>道路/墙体：nodes = "x,z;x,z;…"。</summary>
    [Serializable]
    public class StoryPathDto
    {
        public string name;
        public string kind;
        public float width;
        public string nodes;
    }

    /// <summary>NPC 走位：起点 (x,z) + 可选 waypoints = "x,z;x,z;…"。</summary>
    [Serializable]
    public class StoryNpcPlaceDto
    {
        public string name;
        public float x;
        public float z;
        public string waypoints;
    }

    /// <summary>layoutSpec 的解析、校验与归一化。</summary>
    public static class StoryLayoutSpecJson
    {
        public const int MaxItems = 120;
        public const int MaxZones = 16;
        public const int MaxPaths = 40;
        public const int MaxNpcs = 16;
        public const int MaxPoints = 24;

        /// <summary>模型输出的 layoutSpec → 施工图；没有任何可施工内容时返回 null。</summary>
        public static LayoutSpec Convert(StoryLayoutDto dto, SceneSpec scene)
        {
            if (dto == null || scene == null) return null;
            float halfW = Mathf.Max(10f, scene.width * 0.5f);
            float halfL = Mathf.Max(10f, scene.length * 0.5f);

            var spec = new LayoutSpec
            {
                sceneId = scene.sceneId ?? "",
                source = "storyLayoutSpec",
                items = new List<LayoutItemSpec>(),
                paths = new List<LayoutPathSpec>(),
                zones = new List<LayoutZoneSpec>(),
                npcs = new List<LayoutNpcSpec>(),
                gaps = new List<string>(),
            };

            var usedIds = new HashSet<string>(StringComparer.Ordinal);

            // ── 分区 ──
            if (dto.zones != null)
            {
                int zc = 0;
                for (int i = 0; i < dto.zones.Length && zc < MaxZones; i++)
                {
                    StoryZoneDto z = dto.zones[i];
                    if (z == null) continue;
                    float minX = Mathf.Min(z.x1, z.x2);
                    float maxX = Mathf.Max(z.x1, z.x2);
                    float minZ = Mathf.Min(z.z1, z.z2);
                    float maxZ = Mathf.Max(z.z1, z.z2);
                    if (maxX - minX < 2f || maxZ - minZ < 2f) continue;
                    minX = Mathf.Clamp(minX, -halfW, halfW);
                    maxX = Mathf.Clamp(maxX, -halfW, halfW);
                    minZ = Mathf.Clamp(minZ, -halfL, halfL);
                    maxZ = Mathf.Clamp(maxZ, -halfL, halfL);
                    string name = Clean(z.name, 40);
                    if (string.IsNullOrEmpty(name)) name = "分区" + zc.ToString(CultureInfo.InvariantCulture);
                    spec.zones.Add(new LayoutZoneSpec
                    {
                        zoneId = "zone_" + SafeId(name),
                        kind = NormalizeZoneKind(z.kind, name),
                        description = name,
                        polygon = new List<Vector2>
                        {
                            new Vector2(minX, minZ),
                            new Vector2(maxX, minZ),
                            new Vector2(maxX, maxZ),
                            new Vector2(minX, maxZ),
                        },
                        groundHeight = Mathf.Clamp(z.groundHeight, -3f, 3f),
                        required = true,
                        source = "storyLayoutSpec",
                        openness = Mathf.Clamp01(z.openness),
                        material = Clean(z.material, 24),
                    });
                    zc++;
                }
            }

            // ── 街道（语义）：模型只说走向/宽度/是否两侧连续，开间由求解器数 ──
            if (dto.streets != null)
            {
                spec.streets = new List<LayoutStreetSpec>();
                int sc = 0;
                for (int i = 0; i < dto.streets.Length && sc < MaxPaths; i++)
                {
                    StoryStreetDto s = dto.streets[i];
                    if (s == null) continue;
                    List<Vector2> nodes = ParsePoints(s.nodes, halfW, halfL);
                    if (nodes.Count < 2) continue;
                    string name = Clean(s.name, 40);
                    if (string.IsNullOrEmpty(name)) name = "街道" + sc.ToString(CultureInfo.InvariantCulture);
                    spec.streets.Add(new LayoutStreetSpec
                    {
                        pathId = "street_" + SafeId(name) + "_" + sc.ToString(CultureInfo.InvariantCulture),
                        name = name,
                        nodes = nodes,
                        width = Mathf.Clamp(s.width > 0.5f ? s.width : 8f, 3f, 30f),
                        frontageBoth = s.frontageBoth,
                        style = string.IsNullOrEmpty(s.style) ? "classical_row" : s.style,
                        depth = s.depth,
                        floors = s.floors,
                        continuous = s.continuous,
                    });
                    sc++;
                }
            }

            // ── 语义地标：不给坐标；pinned=true 时才用 x/z（剧情点名"必须在原地"的场合）──
            if (dto.landmarks != null)
            {
                spec.landmarks = new List<LayoutLandmarkSpec>();
                int lc = 0;
                for (int i = 0; i < dto.landmarks.Length && lc < MaxItems; i++)
                {
                    StoryLandmarkDto l = dto.landmarks[i];
                    if (l == null) continue;
                    string name = Clean(l.name, 60);
                    if (string.IsNullOrEmpty(name)) continue;
                    spec.landmarks.Add(new LayoutLandmarkSpec
                    {
                        name = name,
                        kind = string.IsNullOrEmpty(l.kind) ? "building" : l.kind,
                        on = Clean(l.on, 24),
                        facing = Clean(l.facing, 16),
                        width = Mathf.Clamp(l.width, 0f, halfW * 2f),
                        depth = Mathf.Clamp(l.depth, 0f, halfL * 2f),
                        height = Mathf.Clamp(l.height, 0f, 120f),
                        pinned = l.pinned,
                        pin = new Vector2(
                            Mathf.Clamp(l.x, -halfW, halfW),
                            Mathf.Clamp(l.z, -halfL, halfL)),
                        hint = Clean(l.hint, 120),
                        semantic = Clean(l.semantic, 160),
                        anchorBinding = Clean(l.anchorFor, 60),
                    });
                    lc++;
                }
            }

            // ── 地物 ──
            if (dto.items != null)
            {
                int ic = 0;
                for (int i = 0; i < dto.items.Length && ic < MaxItems; i++)
                {
                    StoryItemDto it = dto.items[i];
                    if (it == null) continue;
                    string name = Clean(it.name, 60);
                    if (string.IsNullOrEmpty(name)) continue;
                    float w = Mathf.Clamp(it.width > 0.2f ? it.width : 4f, 0.2f, halfW * 2f);
                    float d = Mathf.Clamp(it.depth > 0.2f ? it.depth : (it.width > 0.2f ? it.width : 4f), 0.2f, halfL * 2f);
                    Vector2 center = new Vector2(
                        Mathf.Clamp(it.x, -halfW + w * 0.5f, halfW - w * 0.5f),
                        Mathf.Clamp(it.z, -halfL + d * 0.5f, halfL - d * 0.5f));
                    string id = Unique("story_" + SafeId(name) + "_" + ic.ToString(CultureInfo.InvariantCulture), usedIds);
                    ic++;
                    spec.items.Add(new LayoutItemSpec
                    {
                        id = id,
                        name = name,
                        semantic = Trim((name + "，" + Clean(it.semantic, 120)).Trim('，'), 160),
                        kind = NormalizeSlotKind(it.kind, w, d),
                        center = center,
                        size = new Vector2(w, d),
                        rotation = NormalizeRotation(it.rotation),
                        expectedModelHint = Clean(string.IsNullOrEmpty(it.hint) ? name : it.hint, 120),
                        rowId = Clean(it.rowGroup, 40),
                        rowIndex = Mathf.Max(0, it.rowIndex),
                        rowCount = Mathf.Max(1, it.rowCount),
                        height = Mathf.Clamp(it.height, 0f, 120f),
                        required = true,
                        source = "storyLayoutSpec",
                    });
                }
            }

            // ── 道路 / 墙体 ──
            if (dto.paths != null)
            {
                int pc = 0;
                for (int i = 0; i < dto.paths.Length && pc < MaxPaths; i++)
                {
                    StoryPathDto p = dto.paths[i];
                    if (p == null) continue;
                    List<Vector2> nodes = ParsePoints(p.nodes, halfW, halfL);
                    if (nodes.Count < 2) continue;
                    string kind = NormalizePathKind(p.kind);
                    spec.paths.Add(new LayoutPathSpec
                    {
                        pathId = Unique("storypath_" + SafeId(Clean(p.name, 24)) + "_" + pc.ToString(CultureInfo.InvariantCulture), usedIds),
                        kind = kind,
                        width = Mathf.Clamp(p.width > 0.2f ? p.width : (kind == "road" ? 6f : 1.2f), 0.2f, 40f),
                        nodes = nodes,
                        required = true,
                    });
                    pc++;
                }
            }

            // ── NPC 走位 ──
            if (dto.npcs != null)
            {
                int nc = 0;
                for (int i = 0; i < dto.npcs.Length && nc < MaxNpcs; i++)
                {
                    StoryNpcPlaceDto n = dto.npcs[i];
                    if (n == null) continue;
                    string name = Clean(n.name, 40);
                    if (string.IsNullOrEmpty(name)) continue;
                    List<Vector2> points = ParsePoints(n.waypoints, halfW, halfL);
                    if (points.Count == 0)
                    {
                        Vector2 start = new Vector2(
                            Mathf.Clamp(n.x, -halfW + 1f, halfW - 1f),
                            Mathf.Clamp(n.z, -halfL + 1f, halfL - 1f));
                        points.Add(start);
                        points.Add(new Vector2(Mathf.Clamp(start.x + 3f, -halfW + 1f, halfW - 1f), start.y));
                    }
                    spec.npcs.Add(new LayoutNpcSpec
                    {
                        npcId = "npc_" + SafeId(name),
                        name = name,
                        waypoints = points,
                        loop = true,
                        required = true,
                    });
                    nc++;
                }
            }

            // 语义模式：只要给出分区/街道/语义地标之一就够求解器开工（不需要模型数房子）。
            bool usable = spec.items.Count > 0
                          || (spec.zones != null && spec.zones.Count > 0)
                          || (spec.paths != null && spec.paths.Count > 0)
                          || (spec.streets != null && spec.streets.Count > 0)
                          || (spec.landmarks != null && spec.landmarks.Count > 0);
            if (!usable) return null;
            return spec;
        }

        /// <summary>"x,z;x,z" / "x,z x,z" / 全角标点 → 坐标列表（夹到场景内）。</summary>
        public static List<Vector2> ParsePoints(string text, float halfW, float halfL)
        {
            var points = new List<Vector2>();
            if (string.IsNullOrEmpty(text)) return points;
            string normalized = text.Replace('；', ';').Replace('，', ',').Replace('|', ';')
                .Replace('（', '(').Replace('）', ')').Replace(' ', ';').Replace("\n", ";");
            string[] chunks = normalized.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < chunks.Length && points.Count < MaxPoints; i++)
            {
                string chunk = chunks[i].Trim().Trim('(', ')');
                if (chunk.Length == 0) continue;
                string[] pair = chunk.Split(',');
                if (pair.Length < 2) continue;
                float x, z;
                if (!float.TryParse(pair[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                if (!float.TryParse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                points.Add(new Vector2(Mathf.Clamp(x, -halfW, halfW), Mathf.Clamp(z, -halfL, halfL)));
            }
            return points;
        }

        // ─────────────────────────── 归一化 ───────────────────────────

        private static string NormalizeZoneKind(string kind, string name)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "square" || k == "field" || k == "water" || k == "church" || k == "market"
                || k == "residential" || k == "harbor" || k == "road" || k == "grass" || k == "stone")
                return k == "stone" || k == "grass" ? "field" : k;
            // 模型写中文/写空：按名字猜
            string n = name ?? "";
            if (n.IndexOf("水", StringComparison.Ordinal) >= 0 || n.IndexOf("河", StringComparison.Ordinal) >= 0
                || n.IndexOf("池", StringComparison.Ordinal) >= 0) return "water";
            if (n.IndexOf("广场", StringComparison.Ordinal) >= 0 || n.IndexOf("石板", StringComparison.Ordinal) >= 0) return "square";
            return "field";
        }

        private static SlotKind NormalizeSlotKind(string kind, float w, float d)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "building" || k == "建筑" || k == "房屋" || k == "house") return SlotKind.Building;
            if (k == "prop" || k == "道具" || k == "物件") return SlotKind.Prop;
            if (k == "anchor" || k == "锚点") return SlotKind.Anchor;
            if (k == "npc" || k == "人物") return SlotKind.Npc;
            if (k == "decoration" || k == "decor" || k == "装饰") return SlotKind.Decoration;
            // 没写 kind：按占地猜（占地 ≥ 25 ㎡ 基本是房子）
            return w * d >= 25f ? SlotKind.Building : SlotKind.Prop;
        }

        private static string NormalizePathKind(string kind)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "wall" || k == "墙" || k == "围墙") return "wall";
            if (k == "fence" || k == "栅栏" || k == "围栏") return "fence";
            if (k == "trench" || k == "战壕") return "trench";
            return "road";
        }

        private static float NormalizeRotation(float rotation)
        {
            if (rotation > 360f || rotation < -360f) rotation %= 360f;
            return Mathf.DeltaAngle(0f, rotation);
        }

        // ─────────────────────────── 文本 ───────────────────────────

        private static string Clean(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string value = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return max > 0 && value.Length > max ? value.Substring(0, max) : value;
        }

        private static string Trim(string text, int max)
        {
            return Clean(text, max);
        }

        private static string SafeId(string text)
        {
            if (string.IsNullOrEmpty(text)) return "x";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
            }
            string result = sb.ToString().Trim('_');
            return result.Length == 0 ? "x" : result;
        }

        private static string Unique(string baseId, HashSet<string> used)
        {
            string id = baseId;
            int n = 1;
            while (used != null && used.Contains(id))
            {
                n++;
                id = baseId + "_" + n.ToString(CultureInfo.InvariantCulture);
            }
            if (used != null) used.Add(id);
            return id;
        }
    }
}

#pragma warning restore 0649
