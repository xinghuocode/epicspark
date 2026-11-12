// 覆盖审计探针：不启动 Play Mode，直接用真实代码跑
//   剧情 layoutDescription → StoryLayoutSpecBuilder → GrayboxSpecExecutor → GrayboxPlan
// 对比「关闭覆盖审计（= 修复前）」与「开启覆盖审计（= 修复后）」两次结果，证明漏项被补回。
//
// 用法（batchmode）：
//   Unity.exe -batchmode -quit -projectPath <工程> \
//     -executeMethod EpicSpark.EditorTools.LayoutCoverageProbe.Run -logFile Logs\probe-coverage.log
// 也可以从菜单 EpicSpark/探针/覆盖审计（剧情→灰盒） 运行。
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using EpicSpark.Graybox;
using EpicSpark.Story;
using EpicSpark.Core;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.EditorTools
{
    public static class LayoutCoverageProbe
    {
        private const string DefaultSceneJson =
            @"C:\Users\wsy\AppData\LocalLow\DefaultCompany\epicsparktest4\EpicSpark\debug\20261108_085755_s_3b3e2f12_0_c\04_场景JSON.txt";

        [MenuItem("EpicSpark/探针/覆盖审计（剧情→灰盒）")]
        public static void Run()
        {
            string path = DefaultSceneJson;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-scenejson") path = args[i + 1];

            var sb = new StringBuilder();
            sb.AppendLine("场景 JSON: " + path);
            if (!File.Exists(path))
            {
                sb.AppendLine("找不到文件，探针结束。");
                Flush(sb.ToString());
                return;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            SceneSpec scene = new SceneSpec
            {
                sceneId = "probe_scene",
                sceneName = "圣安东街区广场（探针）",
                layoutDescription = ExtractString(json, "layoutDescription"),
                width = 180f,
                length = 200f,
                spawnPoint = new Vector3(0f, 1.2f, -24f),
            };
            sb.AppendLine("layoutDescription 长度: " + scene.layoutDescription.Length);
            sb.AppendLine();

            // ── A) 修复前：覆盖审计关闭 ──
            LayoutCoverage.Enabled = false;
            LayoutSpec specBefore = StoryLayoutSpecBuilder.Build(scene);
            string noteBefore;
            GrayboxPlan planBefore = GrayboxSpecExecutor.Build(scene, specBefore, 20261108, out noteBefore);
            Dump(sb, "A. 修复前（覆盖审计关闭）", specBefore, planBefore, noteBefore);

            // ── B) 修复后：覆盖审计开启 ──
            LayoutCoverage.Enabled = true;
            LayoutSpec specAfter = StoryLayoutSpecBuilder.Build(scene);
            string noteAfter;
            GrayboxPlan planAfter = GrayboxSpecExecutor.Build(scene, specAfter, 20261108, out noteAfter);
            Dump(sb, "B. 修复后（覆盖审计开启）", specAfter, planAfter, noteAfter);

            sb.AppendLine("覆盖审计明细：");
            sb.AppendLine(LayoutCoverage.LastDetail);
            sb.AppendLine();
            sb.AppendLine("差异：规格项 " + specBefore.items.Count + " → " + specAfter.items.Count
                          + "；槽位 " + planBefore.slots.Count + " → " + planAfter.slots.Count
                          + "；分区 " + planBefore.regions.Count + " → " + planAfter.regions.Count);
            Flush(sb.ToString());
        }

        private static void Dump(StringBuilder sb, string title, LayoutSpec spec, GrayboxPlan plan, string note)
        {
            sb.AppendLine("── " + title + " ──");
            sb.AppendLine("plannerNote: " + note);
            sb.AppendLine("规格项 " + spec.items.Count + " 个：");
            for (int i = 0; i < spec.items.Count; i++)
            {
                LayoutItemSpec it = spec.items[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-34} {1,-9} c=({2,7:0.0},{3,7:0.0}) size=({4,5:0.0},{5,5:0.0}) src={6}",
                    it.id, it.kind, it.center.x, it.center.y, it.size.x, it.size.y, it.source));
            }
            sb.AppendLine("分区 " + plan.regions.Count + " 个：");
            for (int i = 0; i < plan.regions.Count; i++)
            {
                GrayboxRegion r = plan.regions[i];
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int p = 0; p < r.polygon.Count; p++)
                {
                    x0 = Mathf.Min(x0, r.polygon[p].x); x1 = Mathf.Max(x1, r.polygon[p].x);
                    z0 = Mathf.Min(z0, r.polygon[p].y); z1 = Mathf.Max(z1, r.polygon[p].y);
                }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-30} kind={1,-8} x[{2,7:0.0},{3,7:0.0}] z[{4,7:0.0},{5,7:0.0}] {6}",
                    r.regionId, r.kind, x0, x1, z0, z1, r.description));
            }
            sb.AppendLine("槽位 " + plan.slots.Count + " 个：");
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot s = plan.slots[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-40} {1,-9} ({2,7:0.0},{3,7:0.0}) size=({4,5:0.0},{5,5:0.0}) seg={6}",
                    s.slotId, s.kind, s.position.x, s.position.y, s.sizeX, s.sizeY, s.segment));
            }
            sb.AppendLine();
        }

        private static void Flush(string text)
        {
            string dir = "Logs";
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "layout-coverage-probe.txt"), text, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[覆盖探针] 写文件失败: " + e.Message);
            }
            Debug.Log("[覆盖探针]\n" + text);
        }

        /// <summary>从 Unity JsonUtility 风格 JSON 里取字符串字段并反转义。</summary>
        private static string ExtractString(string json, string field)
        {
            Match m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}
