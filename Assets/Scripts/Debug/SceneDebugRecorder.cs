// 场景生成调试录制：每轮生成把「剧情推理/输出/详细描述 + 灰盒规划截图 + 拼装截图」落到磁盘。
// 目录：<persistentDataPath>/EpicSpark/debug/<时间戳>_<sceneId>/
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Debugging
{
    public static class SceneDebugRecorder
    {
        private const string PrefsKey = "EpicSpark.DebugRecord";

        /// <summary>调试录制开关（默认开）。可在模型设置界面切换，或 PlayerPrefs 改 EpicSpark.DebugRecord。</summary>
        public static bool Enabled
        {
            get { return PlayerPrefs.GetInt(PrefsKey, 1) == 1; }
            set { PlayerPrefs.SetInt(PrefsKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>本轮调试目录（没有开始录制时为空）。</summary>
        public static string CurrentDir = "";

        private static readonly string[] Head =
        {
            "================ 场景生成调试记录 ================",
            "时间: ",
            "场景: ",
            "说明: 01 剧情提示词 · 02 剧情原始输出 · 03 剧情推理过程 · 04 场景 JSON · 05 布局详细描述",
            "      06 灰盒规划 JSON · 07 灰盒规划截图 · 08 搭建结果 JSON · 09 拼装顶视图 · 10 拼装玩家视角",
            "",
        };

        public static string BeginTurn(string sceneId)
        {
            CurrentDir = "";
            if (!Enabled) return "";
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string safeId = Sanitize(string.IsNullOrEmpty(sceneId) ? "scene" : sceneId);
                string dir = Path.Combine(PersistentRoot(), stamp + "_" + safeId);
                Directory.CreateDirectory(dir);
                CurrentDir = dir;

                var sb = new StringBuilder();
                for (int i = 0; i < Head.Length; i++) sb.AppendLine(Head[i] + (i == 1 ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : ""));
                File.WriteAllText(Path.Combine(dir, "00_信息.txt"), sb.ToString(), new UTF8Encoding(false));
                Debug.Log("[调试] 本轮录制目录: " + dir);
                return dir;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[调试] 创建录制目录失败: " + e.Message);
                return "";
            }
        }

        public static void WriteText(string dir, string fileName, string text)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                File.WriteAllText(Path.Combine(dir, fileName), text ?? "", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[调试] 写文件失败 " + fileName + ": " + e.Message);
            }
        }

        // ─────────────────────────── 剧情 Agent ───────────────────────────

        public static void WriteStory(string dir, EpicSpark.Story.StoryAgent story, SceneSpec spec, string narrative)
        {
            if (string.IsNullOrEmpty(dir) || spec == null) return;

            if (story != null)
            {
                WriteText(dir, "01_剧情提示词.txt",
                    "【System】\n" + (story.LastSystemPrompt ?? "") + "\n\n【User】\n" + (story.LastUserPrompt ?? ""));
                WriteText(dir, "02_剧情原始输出.txt", string.IsNullOrEmpty(story.LastRawOutput) ? "（本轮走本地兜底，无远端输出）" : story.LastRawOutput);
                WriteText(dir, "03_剧情推理过程.txt", string.IsNullOrEmpty(story.LastReasoning)
                    ? "（模型未返回 reasoning_content，或本轮为本地兜底/非推理模型）"
                    : story.LastReasoning);
            }
            else
            {
                WriteText(dir, "01_剧情提示词.txt", "（未取到剧情 Agent 实例）");
            }

            WriteText(dir, "04_场景JSON.txt", ToJson(spec));
            WriteText(dir, "05_布局详细描述.txt",
                "场景: " + (spec.sceneName ?? "") + "\n"
                + "叙述:\n" + (narrative ?? "") + "\n\n"
                + "布局描述(layoutDescription):\n" + (spec.layoutDescription ?? "（空）") + "\n");

            var sb = new StringBuilder();
            sb.AppendLine("场景 id: " + spec.sceneId);
            sb.AppendLine("题目: " + spec.sceneName);
            sb.AppendLine("布局文本长度: " + (spec.layoutDescription == null ? 0 : spec.layoutDescription.Length));
            sb.AppendLine("叙述长度: " + (narrative == null ? 0 : narrative.Length));
            WriteText(dir, "00_概要.txt", sb.ToString());
        }

        // ─────────────────────────── 灰盒 / 搭建 ───────────────────────────

        public static void WriteGrayboxPlan(string dir, GrayboxPlan plan)
        {
            if (string.IsNullOrEmpty(dir) || plan == null) return;
            WriteText(dir, "06_灰盒规划JSON.txt",
                "plannerNote: " + (plan.plannerNote ?? "") + "\n"
                + "尺寸: " + plan.width.ToString("F1") + " x " + plan.length.ToString("F1") + " 米\n"
                + "分区: " + (plan.regions == null ? 0 : plan.regions.Count)
                + " · 道路: " + (plan.paths == null ? 0 : plan.paths.Count)
                + " · 槽位: " + (plan.slots == null ? 0 : plan.slots.Count) + "\n\n"
                + ToJson(plan));
        }

        public static void WriteBuild(string dir, BuildResult build)
        {
            if (string.IsNullOrEmpty(dir) || build == null) return;
            WriteText(dir, "08_搭建结果JSON.txt", ToJson(build));
        }

        // ─────────────────────────── 截图 ───────────────────────────

        /// <summary>玩家视角截图（拷贝主相机参数渲染到 RT，不依赖屏幕尺寸）。</summary>
        public static bool CaptureView(string dir, string fileName, Camera source, float fov = 60f)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            Camera cam = null;
            GameObject go = null;
            try
            {
                go = new GameObject("DebugCaptureCam_View");
                cam = go.AddComponent<Camera>();
                Camera src = source != null ? source : Camera.main;
                if (src != null) cam.CopyFrom(src);
                else
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 1f);
                    cam.transform.position = new Vector3(0f, 6f, -14f);
                    cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
                }
                if (cam.clearFlags == CameraClearFlags.Nothing || cam.clearFlags == CameraClearFlags.Depth)
                    cam.clearFlags = CameraClearFlags.SolidColor;
                if (cam.backgroundColor.a <= 0f) cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 1f);
                cam.fieldOfView = fov;
                cam.enabled = false;
                return RenderToPng(cam, Path.Combine(dir, fileName), 1280, 720);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[调试] 视角截图失败: " + e.Message);
                return false;
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>顶视正交截图：判断"地编有没有思路"最直观的一张图。</summary>
        public static bool CaptureTopDown(string dir, string fileName, GameObject root, Vector3 center, float size, bool includeInactive = false)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            GameObject go = null;
            try
            {
                go = new GameObject("DebugCaptureCam_Top");
                var cam = go.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(8f, size);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 1f);
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 600f;
                cam.cullingMask = ~0;
                cam.transform.position = new Vector3(center.x, center.y + 120f, center.z);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.enabled = false;

                // 排除自己与 UI
                Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                bool[] canvasStates = new bool[canvases.Length];
                for (int i = 0; i < canvases.Length; i++)
                {
                    canvasStates[i] = canvases[i].enabled;
                    canvases[i].enabled = false;
                }
                bool captured;
                try { captured = RenderToPng(cam, Path.Combine(dir, fileName), 1400, Mathf.RoundToInt(1400f * 0.78f)); }
                finally
                {
                    for (int i = 0; i < canvases.Length; i++)
                        if (canvases[i] != null) canvases[i].enabled = canvasStates[i];
                }
                if (!captured) Debug.LogWarning("[调试] 顶视截图渲染失败: " + fileName);
                return captured;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[调试] 顶视截图异常: " + e.Message);
                return false;
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
        }

        private static bool RenderToPng(Camera cam, string path, int width, int height)
        {
            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                rt = new RenderTexture(Mathf.Max(64, width), Mathf.Max(64, height), 24, RenderTextureFormat.ARGB32);
                rt.Create();
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();

                File.WriteAllBytes(path, tex.EncodeToPNG());
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[调试] 截图写入失败: " + e.Message);
                return false;
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        // ─────────────────────────── 工具 ───────────────────────────

        public static string ToJson(object value)
        {
            if (value == null) return "{}";
            try { return JsonUtility.ToJson(value, true); }
            catch (Exception e) { return "{\"error\":\"" + e.Message + "\"}"; }
        }

        public static string PersistentRoot()
        {
            string root = Path.Combine(Application.persistentDataPath, "EpicSpark", "debug");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string Sanitize(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length && sb.Length < 40; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            }
            return sb.Length == 0 ? "scene" : sb.ToString();
        }
    }
}
