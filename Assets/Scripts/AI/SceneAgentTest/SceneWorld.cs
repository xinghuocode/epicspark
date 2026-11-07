using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>模型库中的一条模型记录(对应 Assets/Generated/Models/model_library.json 的 entries 元素)。</summary>
    public class ModelEntry
    {
        public string id;
        public string name;
        public string prompt;
        public string category;
        /// <summary>GLB 文件工程内路径, 例如 Assets/Generated/Models/mdl_tavern_001.glb。</summary>
        public string glb;
    }

    /// <summary>
    /// 缓存模型库。测试用途: 直接读取 model_library.json;
    /// generate_model 为模拟实现(不真正调用文生图+图生3D管线, 仅登记新模型)。
    /// </summary>
    public class ModelLibrary
    {
        public const string DefaultLibraryPath = "Assets/Generated/Models/model_library.json";

        readonly List<ModelEntry> entries = new List<ModelEntry>();
        readonly Dictionary<string, ModelEntry> byId = new Dictionary<string, ModelEntry>();

        public static ModelLibrary Load(string path = DefaultLibraryPath)
        {
            var lib = new ModelLibrary();
            string fullPath = Path.Combine(Application.dataPath, "..", path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("模型库文件不存在: " + fullPath);
            var root = MiniJson.AsDict(MiniJson.Parse(File.ReadAllText(fullPath)));
            var list = MiniJson.GetList(root, "entries") ?? new List<object>();
            foreach (var item in list)
            {
                var d = MiniJson.AsDict(item);
                if (d == null) continue;
                var e = new ModelEntry
                {
                    id = MiniJson.GetString(d, "id"),
                    name = MiniJson.GetString(d, "name"),
                    prompt = MiniJson.GetString(d, "prompt"),
                    category = MiniJson.GetString(d, "category"),
                    glb = MiniJson.GetString(d, "glb"),
                };
                if (string.IsNullOrEmpty(e.id)) continue;
                lib.entries.Add(e);
                lib.byId[e.id] = e;
            }
            return lib;
        }

        public bool TryGet(string id, out ModelEntry entry) => byId.TryGetValue(id ?? "", out entry);

        /// <summary>按关键词搜索 id/name/prompt(不区分大小写); 空关键词返回全部。最多返回 maxCount 条。</summary>
        public List<ModelEntry> Search(string word, int maxCount = 20)
        {
            var result = new List<ModelEntry>();
            word = (word ?? "").Trim().ToLowerInvariant();
            foreach (var e in entries)
            {
                bool hit = word.Length == 0
                           || (e.id ?? "").ToLowerInvariant().Contains(word)
                           || (e.name ?? "").ToLowerInvariant().Contains(word)
                           || (e.prompt ?? "").ToLowerInvariant().Contains(word);
                if (!hit) continue;
                result.Add(e);
                if (result.Count >= maxCount) break;
            }
            return result;
        }

        public int Count => entries.Count;
    }

    // ============ 场景世界 ============

    /// <summary>
    /// 场景世界(模拟)。实现 ARCHITECT.md §4.2 场景Agent的全部 9 个工具。
    /// 坐标为平面 x, y(对应 Unity 的 x, z); 高度由地形函数自动计算, 保证放置物不穿透地形。
    /// 每个工具返回一段文本结果: 成功以 "OK" 开头, 失败以 "ERROR:" 开头(供 LLM 自我纠正)。
    /// </summary>
    public class SceneWorld
    {
        public readonly int Width;
        public readonly int Length;
        public readonly string SceneName;
        public readonly ModelLibrary Library;

        readonly HashSet<string> anchorNames = new HashSet<string>();
        readonly List<PlacedModel> placedModels = new List<PlacedModel>();
        readonly List<BarObject> barObjects = new List<BarObject>();
        readonly Dictionary<string, string> anchorBindings = new Dictionary<string, string>();
        readonly List<Mountain> mountains = new List<Mountain>();
        readonly List<River> rivers = new List<River>();

        Vector2d? startPoint;
        bool terrainInitialized;
        bool finished;

        /// <summary>每次工具调用后触发(工具名, 结果文本), 用于测试输出逐步展示场景。</summary>
        public event Action<string, string, SceneWorld> ToolExecuted;

        /// <summary>场景内容发生有效变更(工具调用成功)后触发, SceneBuilder3D 据此重建3D场景。</summary>
        public event Action<SceneWorld> Changed;

        // ---- 供 SceneBuilder3D 读取的场景状态 ----
        public bool IsTerrainInitialized => terrainInitialized;
        public IReadOnlyList<Mountain> Mountains => mountains;
        public IReadOnlyList<River> Rivers => rivers;
        public IReadOnlyList<PlacedModel> PlacedModels => placedModels;
        public IReadOnlyList<BarObject> BarObjects => barObjects;
        public IReadOnlyDictionary<string, string> AnchorBindings => anchorBindings;
        public bool HasStartPoint => startPoint.HasValue;
        public double StartX => startPoint?.x ?? 0;
        public double StartY => startPoint?.y ?? 0;

        public bool Finished => finished;

        public SceneWorld(string sceneName, int width, int length, IEnumerable<string> anchors, ModelLibrary library)
        {
            SceneName = sceneName ?? "(未命名场景)";
            Width = Math.Max(1, width);
            Length = Math.Max(1, length);
            Library = library ?? new ModelLibrary();
            if (anchors != null)
                foreach (var a in anchors)
                    if (!string.IsNullOrEmpty(a)) anchorNames.Add(a);
        }

        // ============ 工具入口 ============

        public string ExecuteToolCall(string tool, Dictionary<string, object> args)
        {
            string result;
            try
            {
                switch (tool)
                {
                    case "init_terrain": result = InitTerrain(args); break;
                    case "place_model": result = PlaceModel(args); break;
                    case "place_bar_shaped_object": result = PlaceBarShapedObject(args); break;
                    case "set_auchor_point": result = SetAuchorPoint(args); break;
                    case "set_start_point": result = SetStartPoint(args); break;
                    case "search_model": result = SearchModel(args); break;
                    case "finish_scene": result = FinishScene(args); break;
                    default:
                        result = $"ERROR: 未知工具 '{tool}'。可用工具: init_terrain, place_model, " +
                                 "place_bar_shaped_object, set_auchor_point, set_start_point, search_model, finish_scene";
                        break;
                }
            }
            catch (Exception e)
            {
                result = "ERROR: 工具执行异常: " + e.Message;
            }
            ToolExecuted?.Invoke(tool, result, this);
            if (result.StartsWith("OK", StringComparison.Ordinal))
                Changed?.Invoke(this);
            return result;
        }

        // ============ 工具实现 (ARCHITECT.md §4.2) ============

        string InitTerrain(Dictionary<string, object> args)
        {
            string baseTerrain = MiniJson.GetString(args, "base") ?? "";
            if (baseTerrain != "plain")
                return "ERROR: base 只能选择 'plain'(平原)";

            mountains.Clear();
            rivers.Clear();

            foreach (var mObj in MiniJson.GetList(args, "mountains") ?? new List<object>())
            {
                var m = MiniJson.AsDict(mObj);
                var nodes = ParseNodes(MiniJson.GetList(m, "nodes"), 4);
                if (nodes == null) return "ERROR: 山脉节点格式必须为 [x, y, height, width] 的数组";
                if (!CheckBounds(nodes, 0, 1)) return OutOfBoundsError("山脉节点");
                double? sf = MiniJson.GetDouble(m, "smoothing_factor") ?? 0.5;
                if (sf < 0 || sf > 1) return "ERROR: smoothing_factor 必须在 0-1 之间";
                mountains.Add(new Mountain { nodes = nodes, smoothingFactor = sf.Value });
            }

            foreach (var rObj in MiniJson.GetList(args, "rivers") ?? new List<object>())
            {
                var r = MiniJson.AsDict(rObj);
                var nodes = ParseNodes(MiniJson.GetList(r, "nodes"), 4);
                if (nodes == null) return "ERROR: 河流节点格式必须为 [x, y, depth, width] 的数组";
                if (!CheckBounds(nodes, 0, 1)) return OutOfBoundsError("河流节点");
                double? sf = MiniJson.GetDouble(r, "smoothing_factor") ?? 0.5;
                if (sf < 0 || sf > 1) return "ERROR: smoothing_factor 必须在 0-1 之间";
                rivers.Add(new River { nodes = nodes, smoothingFactor = sf.Value });
            }

            terrainInitialized = true;
            return $"OK: 地形已初始化(基础地形=plain, 山脉 {mountains.Count} 条, 河流 {rivers.Count} 条)。场景范围 x: 0~{Width}, y: 0~{Length}";
        }

        string PlaceModel(Dictionary<string, object> args)
        {
            string modelId = RequireString(args, "model_id", out string err); if (err != null) return err;
            string placedName = RequireString(args, "placed_name", out err); if (err != null) return err;
            double? x = MiniJson.GetDouble(args, "x"), y = MiniJson.GetDouble(args, "y");
            double? sizeX = MiniJson.GetDouble(args, "size_x"), sizeY = MiniJson.GetDouble(args, "size_y");
            if (x == null || y == null) return "ERROR: 缺少 x 或 y 坐标";
            if (!InBounds(x.Value, y.Value)) return OutOfBoundsError("模型位置");
            if (sizeX == null || sizeY == null || sizeX <= 0 || sizeY <= 0)
                return "ERROR: size_x / size_y 必须为正数";
            if (!Library.TryGet(modelId, out var entry))
                return $"ERROR: 模型 '{modelId}' 不存在于模型库。请先调用 search_model 搜索并选择库中已有的模型";
            if (FindPlaced(placedName) != null)
                return $"ERROR: 放置实例名 '{placedName}' 已存在, 请换一个名称";

            double height = TerrainHeight(x.Value, y.Value);
            var p = new PlacedModel
            {
                modelId = modelId, modelName = entry.name, placedName = placedName,
                x = x.Value, y = y.Value, height = height, sizeX = sizeX.Value, sizeY = sizeY.Value,
            };
            placedModels.Add(p);
            return $"OK: 已放置模型 '{entry.name}'({modelId}), 实例名 '{placedName}', 位置 ({p.x:F1}, {p.y:F1}), 落点高度 {height:F2}, 尺寸 {sizeX:F1}x{sizeY:F1}";
        }

        string PlaceBarShapedObject(Dictionary<string, object> args)
        {
            string type = MiniJson.GetString(args, "type") ?? "";
            if (type != "wall" && type != "fence" && type != "road" && type != "trench")
                return "ERROR: type 只能选择 wall(墙)/fence(栅栏)/road(道路)/trench(战壕)";
            var nodes = ParseNodes(MiniJson.GetList(args, "nodes"), 3);
            if (nodes == null || nodes.Count < 2)
                return "ERROR: nodes 必须至少包含 2 个 [x, y, width] 节点";
            if (!CheckBounds(nodes, 0, 1)) return OutOfBoundsError("条状物体节点");
            foreach (var n in nodes)
                if (n[2] <= 0) return "ERROR: 节点 width 必须为正数";

            barObjects.Add(new BarObject { type = type, nodes = nodes });
            return $"OK: 已放置条状物体 '{type}', 共 {nodes.Count} 个节点";
        }

        string SetAuchorPoint(Dictionary<string, object> args)
        {
            string anchorName = RequireString(args, "auchor_name", out string err); if (err != null) return err;
            string placedName = RequireString(args, "placed_name", out err); if (err != null) return err;
            if (anchorNames.Count > 0 && !anchorNames.Contains(anchorName))
                return $"ERROR: 传送锚点 '{anchorName}' 不在剧情Agent提供的锚点列表 [{string.Join(", ", anchorNames)}] 中";
            var placed = FindPlaced(placedName);
            if (placed == null)
                return $"ERROR: 放置实例 '{placedName}' 不存在。请先用 place_model 放置模型, 再用其 placed_name 绑定锚点";
            anchorBindings[anchorName] = placedName;
            return $"OK: 传送锚点 '{anchorName}' 已绑定到模型实例 '{placedName}'({placed.modelName})";
        }

        string SetStartPoint(Dictionary<string, object> args)
        {
            double? x = MiniJson.GetDouble(args, "x"), y = MiniJson.GetDouble(args, "y");
            if (x == null || y == null) return "ERROR: 缺少 x 或 y 坐标";
            if (!InBounds(x.Value, y.Value)) return OutOfBoundsError("起始点");
            startPoint = new Vector2d(x.Value, y.Value);
            return $"OK: 玩家起始点已设置 ({x:F1}, {y:F1}), 高度 {TerrainHeight(x.Value, y.Value):F2}";
        }

        string SearchModel(Dictionary<string, object> args)
        {
            string word = MiniJson.GetString(args, "search_word", "") ?? "";
            var hits = Library.Search(word);
            var sb = new StringBuilder($"OK: 模型库共 {Library.Count} 个模型, 匹配 '{word}' 的结果 {hits.Count} 条(最多显示 20 条):\n");
            foreach (var e in hits)
                sb.Append($"  - id={e.id} | 名称={e.name} | 类别={e.category} | 提示词={e.prompt}\n");
            if (hits.Count == 0)
                sb.Append("  (无匹配, 可尝试 generate_model 生成新模型)");
            return sb.ToString();
        }

        string FinishScene(Dictionary<string, object> args)
        {
            var problems = new List<string>();
            if (!terrainInitialized) problems.Add("尚未调用 init_terrain 初始化地形");
            if (startPoint == null) problems.Add("尚未调用 set_start_point 设置玩家起始点");
            foreach (var name in anchorNames)
                if (!anchorBindings.ContainsKey(name))
                    problems.Add($"传送锚点 '{name}' 尚未绑定模型(set_auchor_point)");
            if (placedModels.Count == 0) problems.Add("场景中没有任何已放置的模型(place_model)");

            if (problems.Count > 0)
                return "ERROR: 场景生成不完整:\n  - " + string.Join("\n  - ", problems);

            finished = true;
            return "OK: 场景生成完成!\n" + DescribeScene();
        }

        // ============ 场景描述输出 ============

        /// <summary>生成当前场景的完整文字描述(供逐步输出/最终展示)。</summary>
        public string DescribeScene()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"====== 场景「{SceneName}」 ({Width} x {Length}) ======");
            sb.AppendLine($"地形: {(terrainInitialized ? "plain" : "(未初始化)")}, 山脉 {mountains.Count} 条, 河流 {rivers.Count} 条");
            sb.AppendLine($"玩家起始点: {(startPoint.HasValue ? $"({startPoint.Value.x:F1}, {startPoint.Value.y:F1})" : "(未设置)")}");
            if (placedModels.Count > 0)
            {
                sb.AppendLine($"模型放置 ({placedModels.Count}):");
                foreach (var p in placedModels)
                    sb.AppendLine($"  - [{p.placedName}] {p.modelName}({p.modelId}) @ ({p.x:F1}, {p.y:F1}) 高 {p.height:F2} 尺寸 {p.sizeX:F1}x{p.sizeY:F1}");
            }
            if (barObjects.Count > 0)
            {
                sb.AppendLine($"条状物体 ({barObjects.Count}):");
                foreach (var b in barObjects)
                    sb.AppendLine($"  - {b.type}: {b.nodes.Count} 个节点");
            }
            if (anchorNames.Count > 0)
            {
                sb.AppendLine("传送锚点:");
                foreach (var name in anchorNames)
                    sb.AppendLine(anchorBindings.TryGetValue(name, out var pn)
                        ? $"  - {name} → 绑定模型实例 '{pn}'"
                        : $"  - {name} → (未绑定)");
            }
            return sb.ToString();
        }

        // ============ 地形高度(自动计算落点高度, 防止穿透) ============

        /// <summary>战壕深度(米): place_bar_shaped_object 的 trench 会真实下切地形。</summary>
        public const double TrenchDepth = 1.8;

        /// <summary>
        /// 地形高度函数: 平原基准高度 0; 山脉/河流沿各自节点连线形成连续轮廓
        /// (节点即折线顶点, 非独立的圆包); 战壕(trench)沿线段下切 1.8m。
        /// 节点 width 越大越平缓, smoothing_factor 越小过渡越宽越平滑。
        /// </summary>
        public double TerrainHeight(double x, double y)
        {
            double h = 0;
            foreach (var m in mountains)
                h += PolylineProfile(x, y, m.nodes, m.smoothingFactor);
            foreach (var r in rivers)
                h -= PolylineProfile(x, y, r.nodes, r.smoothingFactor);
            foreach (var bar in barObjects)
            {
                if (bar.type != "trench") continue;
                var nodes = bar.nodes;
                for (int i = 0; i < nodes.Count - 1; i++)
                    h -= TrenchProfile(x, y, nodes[i], nodes[i + 1]);
            }
            return h;
        }

        /// <summary>
        /// 沿节点折线的连续截面: 取各线段上最近点的截面, height/width 沿段插值,
        /// 宽度范围内 smoothstep 过渡; 多线段取最大贡献避免节点处叠加。
        /// 单节点退化为点状高斯包。
        /// </summary>
        static double PolylineProfile(double px, double py, List<double[]> nodes, double smoothingFactor)
        {
            if (nodes == null || nodes.Count == 0) return 0;
            if (nodes.Count == 1)
            {
                var n = nodes[0];
                return Gaussian(px, py, n[0], n[1], n[3] * (0.5 + smoothingFactor) + 0.1) * n[2];
            }
            double best = 0, bestH = 0;
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                var a = nodes[i];
                var b = nodes[i + 1];
                double ax = a[0], ay = a[1], bx = b[0], by = b[1];
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                double t = len2 < 1e-6 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
                double cx = ax + dx * t, cy = ay + dy * t;
                double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                double w = (a[3] + (b[3] - a[3]) * t) * (0.5 + smoothingFactor);
                if (w < 0.05) continue;
                double f = 1.0 - dist / w;
                if (f <= 0) continue;
                f = f * f * (3 - 2 * f); // smoothstep 平滑坡面
                double hv = a[2] + (b[2] - a[2]) * t;
                if (f > best) { best = f; bestH = hv; }
            }
            return bestH * best;
        }

        /// <summary>战壕断面: 沿线段距离越近下切越深, 宽度范围内 smoothstep 过渡。</summary>
        static double TrenchProfile(double px, double py, double[] a, double[] b)
        {
            double ax = a[0], ay = a[1], aw = Math.Max(a[2], 0.1);
            double bx = b[0], by = b[1], bw = Math.Max(b[2], 0.1);
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-6 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
            double cx = ax + dx * t, cy = ay + dy * t;
            double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            double w = aw + (bw - aw) * t; // 段内插值宽度
            double f = 1.0 - dist / w;
            if (f <= 0) return 0;
            f = f * f * (3 - 2 * f); // smoothstep 平滑沟沿
            return TrenchDepth * f;
        }

        static double Gaussian(double x, double y, double cx, double cy, double sigma)
        {
            double dx = x - cx, dy = y - cy;
            return Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
        }

        // ============ 辅助 ============

        PlacedModel FindPlaced(string placedName) => placedModels.Find(p => p.placedName == placedName);

        bool InBounds(double x, double y) => x >= 0 && x <= Width && y >= 0 && y <= Length;

        string OutOfBoundsError(string what) =>
            $"ERROR: {what}超出场景范围。场景范围 x: 0~{Width}, y: 0~{Length}(坐标为平面 x, y)";

        bool CheckBounds(List<double[]> nodes, int xi, int yi)
        {
            foreach (var n in nodes)
                if (n[xi] < 0 || n[xi] > Width || n[yi] < 0 || n[yi] > Length) return false;
            return true;
        }

        static List<double[]> ParseNodes(List<object> nodeList, int expectedLen)
        {
            if (nodeList == null) return null;
            var result = new List<double[]>();
            foreach (var item in nodeList)
            {
                var arr = MiniJson.AsDoubleArray(item);
                if (arr == null || arr.Length != expectedLen) return null;
                result.Add(arr);
            }
            return result;
        }

        static string RequireString(Dictionary<string, object> args, string key, out string error)
        {
            string v = MiniJson.GetString(args, key);
            if (string.IsNullOrEmpty(v))
            {
                error = $"ERROR: 缺少参数 '{key}'(string)";
                return null;
            }
            error = null;
            return v;
        }

        // ============ 数据结构 ============

        public class Mountain { public List<double[]> nodes; public double smoothingFactor; }
        public class River { public List<double[]> nodes; public double smoothingFactor; }

        public class PlacedModel
        {
            public string modelId, modelName, placedName;
            public double x, y, height, sizeX, sizeY;
        }

        public class BarObject { public string type; public List<double[]> nodes; }

        public struct Vector2d { public double x, y; public Vector2d(double x, double y) { this.x = x; this.y = y; } }
    }
}
