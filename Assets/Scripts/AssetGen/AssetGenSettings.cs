// 资产生成 Agent 的端点配置。
// 约束：baseUrl / apiKey / 模型名任一未配置时绝不发请求，直接走确定性兜底（克隆模型库中最接近的资产）。
// 端点路径全部可改，方便 ai.gitee.com 实际接口形态调整而不用改代码。
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace EpicSpark.AssetGen
{
    /// <summary>z-image-turbo / RMBG-2.0 / Hunyuan3D-2 的 HTTP 端点与超时配置。</summary>
    [Serializable]
    public class AssetGenSettings
    {
        [Tooltip("OpenAI 兼容 Base URL，不带结尾斜杠，例如 https://ai.gitee.com/v1")]
        public string baseUrl = "https://ai.gitee.com/v1";

        [Tooltip("API Key。留空 = 离线兜底，绝不发请求。")]
        public string apiKey = string.Empty;

        [Tooltip("文生图模型名。")]
        public string textToImageModel = "z-image-turbo";

        [Tooltip("背景透明化（抠图）模型名。")]
        public string backgroundRemovalModel = "RMBG-2.0";

        [Tooltip("图生 3D 模型名。")]
        public string imageTo3DModel = "Hunyuan3D-2";

        [Tooltip("文生图接口路径（gitee：JSON 提交）。")]
        public string textToImagePath = "/images/generations";

        [Tooltip("抠图接口路径（gitee：multipart 提交，实测 /images/mattings 可用）。")]
        public string backgroundRemovalPath = "/images/mattings";

        [Tooltip("抠图兜底接口路径（旧网关可能是 /images/edits；失败会自动尝试）。")]
        public string backgroundRemovalFallbackPath = "/images/edits";

        [Tooltip("图生 3D 提交接口路径（gitee：multipart 提交异步任务）。")]
        public string imageTo3DPath = "/async/image-to-3d";

        [Tooltip("异步任务轮询接口前缀（其后追加 task_id）。")]
        public string taskPath = "/task/";

        [Tooltip("单次 HTTP 请求超时（秒）。")]
        public int requestTimeoutSeconds = 180;

        [Tooltip("3D 任务轮询间隔（秒）。")]
        public int pollIntervalSeconds = 5;

        [Tooltip("3D 任务轮询总超时（秒，默认 15 分钟）。")]
        public int pollTimeoutSeconds = 900;

        [Tooltip("文生图边长（像素）。")]
        public int imageSize = 1024;

        [Tooltip("是否调用 RMBG 抠图。关闭后 Hunyuan3D 可能只生成方盒。")]
        public bool removeBackground = true;

        [Tooltip("追加到提示词末尾的通用后缀，保证白底、单物体、无人物。")]
        public string promptSuffix = "single object, centered, plain white background, product photo, front view, no people, no scenery";

        /// <summary>是否配置了可用的远端生成端点（缺一不可）。</summary>
        public bool HasRemote
        {
            get
            {
                return !string.IsNullOrEmpty((baseUrl ?? string.Empty).Trim())
                    && !string.IsNullOrEmpty((apiKey ?? string.Empty).Trim())
                    && !string.IsNullOrEmpty((textToImageModel ?? string.Empty).Trim())
                    && !string.IsNullOrEmpty((imageTo3DModel ?? string.Empty).Trim());
            }
        }

        /// <summary>拼接完整 URL。</summary>
        public string ResolveUrl(string path)
        {
            string root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(path)) return root;
            return root + "/" + path.Trim().TrimStart('/');
        }

        /// <summary>把中文语义描述补成适合文生图模型的完整提示词。</summary>
        public string BuildImagePrompt(string prompt)
        {
            string text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0) text = "木制道具，陈旧，粗糙木纹";
            string suffix = (promptSuffix ?? string.Empty).Trim();
            if (suffix.Length == 0) return text;
            if (text.IndexOf("plain white background", StringComparison.OrdinalIgnoreCase) >= 0) return text;
            return text + ", " + suffix;
        }

        /// <summary>配置文件路径：persistentDataPath/EpicSpark/assetgen_config.json。</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(Application.persistentDataPath, "EpicSpark", "assetgen_config.json"); }
        }

        /// <summary>读取本地配置文件；不存在或解析失败时返回默认配置（默认配置即离线）。</summary>
        public static AssetGenSettings LoadOrCreate()
        {
            var settings = new AssetGenSettings();
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return settings;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return settings;
                AssetGenSettings loaded = JsonUtility.FromJson<AssetGenSettings>(json);
                if (loaded != null) settings = loaded;
                if (settings.MigrateLegacyEndpoints()) settings.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 读取 assetgen 配置失败，使用默认（离线）配置: " + e.Message);
            }
            return settings;
        }

        /// <summary>
        /// 把旧版（跑不通的）配置迁移到实测可用端点：
        /// 旧版把抠图写成 /images/background/removal、图生 3D 写成 /3d/generations，
        /// 且轮询 3s / 5min。已存在的 assetgen_config.json 不会因为代码默认值改变而自动更新，
        /// 因此加载时做一次性迁移；用户自定义过的其它端点不会被覆盖。
        /// </summary>
        /// <returns>是否发生了迁移（true 时调用方应把新配置写回磁盘）。</returns>
        public bool MigrateLegacyEndpoints()
        {
            bool changed = false;

            if (string.Equals(backgroundRemovalPath, "/images/background/removal", StringComparison.OrdinalIgnoreCase))
            {
                backgroundRemovalPath = "/images/mattings";
                changed = true;
            }
            if (string.Equals(imageTo3DPath, "/3d/generations", StringComparison.OrdinalIgnoreCase))
            {
                imageTo3DPath = "/async/image-to-3d";
                changed = true;
            }
            if (string.IsNullOrEmpty(backgroundRemovalFallbackPath))
            {
                backgroundRemovalFallbackPath = "/images/edits";
                changed = true;
            }
            if (string.IsNullOrEmpty(taskPath))
            {
                taskPath = "/task/";
                changed = true;
            }
            if (pollIntervalSeconds <= 3 && pollTimeoutSeconds <= 300)
            {
                pollIntervalSeconds = 5;
                pollTimeoutSeconds = 900;
                changed = true;
            }

            if (changed)
            {
                Debug.Log("[资产生成] 已把旧版 assetgen 配置迁移到实测可用的 gitee 端点："
                          + "抠图=" + backgroundRemovalPath + " 图生3D=" + imageTo3DPath
                          + " 轮询=" + taskPath + "{" + pollIntervalSeconds + "s, ≤" + pollTimeoutSeconds + "s}");
            }
            return changed;
        }

        /// <summary>把当前配置写盘（方便编辑器里手填 Key 后持久化）。</summary>
        public bool Save()
        {
            try
            {
                string path = ConfigPath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonUtility.ToJson(this, true), Encoding.UTF8);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 写入 assetgen 配置失败: " + e.Message);
                return false;
            }
        }

        public AssetGenSettings Clone()
        {
            var clone = new AssetGenSettings
            {
                baseUrl = baseUrl,
                apiKey = apiKey,
                textToImageModel = textToImageModel,
                backgroundRemovalModel = backgroundRemovalModel,
                imageTo3DModel = imageTo3DModel,
                textToImagePath = textToImagePath,
                backgroundRemovalPath = backgroundRemovalPath,
                backgroundRemovalFallbackPath = backgroundRemovalFallbackPath,
                imageTo3DPath = imageTo3DPath,
                taskPath = taskPath,
                requestTimeoutSeconds = requestTimeoutSeconds,
                pollIntervalSeconds = pollIntervalSeconds,
                pollTimeoutSeconds = pollTimeoutSeconds,
                imageSize = imageSize,
                removeBackground = removeBackground,
                promptSuffix = promptSuffix,
            };
            return clone;
        }

        /// <summary>给日志用的简短摘要。</summary>
        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "baseUrl={0} | 文生图={1}{2} | 抠图={3}{4} | 图生3D={5}{6} | 轮询={7}{8} | 远端可用={9}",
                string.IsNullOrEmpty(baseUrl) ? "(空)" : baseUrl,
                textToImageModel, textToImagePath,
                backgroundRemovalModel, backgroundRemovalPath,
                imageTo3DModel, imageTo3DPath,
                taskPath, pollIntervalSeconds,
                HasRemote ? "是" : "否（离线兜底）");
        }
    }
}
