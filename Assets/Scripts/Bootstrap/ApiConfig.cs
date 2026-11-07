// 运行时模型端点配置：玩家可以在设置界面自定义 API Key、Base URL、以及各环节使用的模型名。
// 只存本机 PlayerPrefs（%USERPROFILE%\AppData\LocalLow\<公司>\<工程>\prefs），不写进工程资源、不发送到别处。
using System;
using UnityEngine;

namespace EpicSpark.Bootstrap
{
    /// <summary>API Key / 端点 / 模型名的可编辑配置。</summary>
    [Serializable]
    public class ApiConfig
    {
        public const string DefaultBaseUrl = "https://ai.gitee.com/v1";
        public const string DefaultChatModel = "Qwen3-32B";
        public const string DefaultEmbedModel = "Qwen3-Embedding-8B";
        public const string DefaultImageModel = "z-image-turbo";
        public const string Default3DModel = "Hunyuan3D-2";

        public const string PrefApiKey = "EpicSpark.ApiKey";
        public const string PrefBaseUrl = "EpicSpark.BaseUrl";
        public const string PrefChatModel = "EpicSpark.ChatModel";
        public const string PrefEmbedModel = "EpicSpark.EmbedModel";
        public const string PrefImageModel = "EpicSpark.ImageModel";
        public const string Pref3DModel = "EpicSpark.ThreeDModel";
        public const string PrefOffline = "EpicSpark.ForceOffline";
        public const string PrefRemoteEmbedding = "EpicSpark.KnowledgeRemoteEmbedding";
        public const string PrefTimeout = "EpicSpark.ChatTimeoutSeconds";
        public const string PrefMaxTokens = "EpicSpark.MaxTokens";

        public const int DefaultTimeoutSeconds = 90;
        public const int DefaultMaxTokens = 4096;

        public string apiKey = "";
        public string baseUrl = DefaultBaseUrl;
        public string chatModel = DefaultChatModel;
        public string embedModel = DefaultEmbedModel;
        public string imageModel = DefaultImageModel;
        public string model3d = Default3DModel;
        public bool offline;
        /// <summary>
        /// 知识库是否用远端 embedding 建索引。默认关：打开后首次检索要把 3600+ 个文本块
        /// 分批请求 /embeddings（数百次请求、可能数分钟），建议先用编辑器菜单离线建好索引。
        /// </summary>
        public bool knowledgeRemoteEmbedding;
        /// <summary>单次模型请求超时（秒）。推理型模型要留够思考时间。</summary>
        public int chatTimeoutSeconds = DefaultTimeoutSeconds;
        /// <summary>单次回复的 token 上限；推理模型会把预算花在 reasoning 上，太小会拿到空 content。</summary>
        public int maxTokens = DefaultMaxTokens;

        public bool HasKey { get { return !string.IsNullOrWhiteSpace(apiKey); } }
        /// <summary>有 Key 且没被强制离线时，四个 Agent 才会走远端。</summary>
        public bool RemoteActive { get { return HasKey && !offline; } }

        public string ResolvedBaseUrl
        {
            get { return string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/'); }
        }

        public string ResolvedChatModel
        {
            get { return string.IsNullOrWhiteSpace(chatModel) ? DefaultChatModel : chatModel.Trim(); }
        }

        public string ResolvedEmbedModel
        {
            get { return string.IsNullOrWhiteSpace(embedModel) ? DefaultEmbedModel : embedModel.Trim(); }
        }

        public string ResolvedImageModel
        {
            get { return string.IsNullOrWhiteSpace(imageModel) ? DefaultImageModel : imageModel.Trim(); }
        }

        public string Resolved3DModel
        {
            get { return string.IsNullOrWhiteSpace(model3d) ? Default3DModel : model3d.Trim(); }
        }

        public int ResolvedTimeoutSeconds
        {
            get { return chatTimeoutSeconds < 5 ? DefaultTimeoutSeconds : (chatTimeoutSeconds > 300 ? 300 : chatTimeoutSeconds); }
        }

        public int ResolvedMaxTokens
        {
            get { return maxTokens < 128 ? DefaultMaxTokens : (maxTokens > 32768 ? 32768 : maxTokens); }
        }

        public static ApiConfig Defaults()
        {
            return new ApiConfig();
        }

        /// <summary>从 PlayerPrefs 读配置；Key 为空时回落到环境变量 EPICSPARK_API_KEY。</summary>
        public static ApiConfig Load()
        {
            var cfg = new ApiConfig();
            try
            {
                cfg.apiKey = PlayerPrefs.GetString(PrefApiKey, "").Trim();
                cfg.baseUrl = PlayerPrefs.GetString(PrefBaseUrl, DefaultBaseUrl).Trim();
                cfg.chatModel = PlayerPrefs.GetString(PrefChatModel, DefaultChatModel).Trim();
                cfg.embedModel = PlayerPrefs.GetString(PrefEmbedModel, DefaultEmbedModel).Trim();
                cfg.imageModel = PlayerPrefs.GetString(PrefImageModel, DefaultImageModel).Trim();
                cfg.model3d = PlayerPrefs.GetString(Pref3DModel, Default3DModel).Trim();
                cfg.offline = PlayerPrefs.GetInt(PrefOffline, 0) == 1;
                cfg.knowledgeRemoteEmbedding = PlayerPrefs.GetInt(PrefRemoteEmbedding, 0) == 1;
                cfg.chatTimeoutSeconds = PlayerPrefs.GetInt(PrefTimeout, DefaultTimeoutSeconds);
                cfg.maxTokens = PlayerPrefs.GetInt(PrefMaxTokens, DefaultMaxTokens);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[设置] 读取配置失败，使用默认值: " + e.Message);
            }

            if (cfg.apiKey.Length == 0)
            {
                try { cfg.apiKey = (Environment.GetEnvironmentVariable("EPICSPARK_API_KEY") ?? "").Trim(); }
                catch (Exception) { }
            }
            return cfg;
        }

        /// <summary>把 Inspector 上的默认值合并进来：PlayerPrefs 里没配的项才用 Inspector 的值。</summary>
        public ApiConfig WithInspectorDefaults(string inspectorKey, string inspectorUrl, string inspectorChat, bool inspectorOffline)
        {
            ApiConfig merged = Clone();
            if (string.IsNullOrWhiteSpace(merged.apiKey) && !string.IsNullOrWhiteSpace(inspectorKey))
                merged.apiKey = inspectorKey.Trim();
            if (string.IsNullOrWhiteSpace(merged.baseUrl) && !string.IsNullOrWhiteSpace(inspectorUrl))
                merged.baseUrl = inspectorUrl.Trim();
            if (string.IsNullOrWhiteSpace(merged.chatModel) && !string.IsNullOrWhiteSpace(inspectorChat))
                merged.chatModel = inspectorChat.Trim();
            merged.offline = merged.offline || inspectorOffline;
            return merged;
        }

        public ApiConfig Clone()
        {
            return new ApiConfig
            {
                apiKey = apiKey,
                baseUrl = baseUrl,
                chatModel = chatModel,
                embedModel = embedModel,
                imageModel = imageModel,
                model3d = model3d,
                offline = offline,
                knowledgeRemoteEmbedding = knowledgeRemoteEmbedding,
                chatTimeoutSeconds = chatTimeoutSeconds,
                maxTokens = maxTokens,
            };
        }

        public void CopyFrom(ApiConfig other)
        {
            if (other == null) return;
            apiKey = other.apiKey;
            baseUrl = other.baseUrl;
            chatModel = other.chatModel;
            embedModel = other.embedModel;
            imageModel = other.imageModel;
            model3d = other.model3d;
            offline = other.offline;
            knowledgeRemoteEmbedding = other.knowledgeRemoteEmbedding;
            chatTimeoutSeconds = other.chatTimeoutSeconds;
            maxTokens = other.maxTokens;
        }

        /// <summary>写入 PlayerPrefs 并立即落盘。</summary>
        public void Save()
        {
            try
            {
                PlayerPrefs.SetString(PrefApiKey, (apiKey ?? "").Trim());
                PlayerPrefs.SetString(PrefBaseUrl, ResolvedBaseUrl);
                PlayerPrefs.SetString(PrefChatModel, ResolvedChatModel);
                PlayerPrefs.SetString(PrefEmbedModel, ResolvedEmbedModel);
                PlayerPrefs.SetString(PrefImageModel, ResolvedImageModel);
                PlayerPrefs.SetString(Pref3DModel, Resolved3DModel);
                PlayerPrefs.SetInt(PrefOffline, offline ? 1 : 0);
                PlayerPrefs.SetInt(PrefRemoteEmbedding, knowledgeRemoteEmbedding ? 1 : 0);
                PlayerPrefs.SetInt(PrefTimeout, ResolvedTimeoutSeconds);
                PlayerPrefs.SetInt(PrefMaxTokens, ResolvedMaxTokens);
                PlayerPrefs.Save();
                Debug.Log("[设置] 已保存: " + Describe());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[设置] 保存失败: " + e.Message);
            }
        }

        /// <summary>清掉本机保存的配置（回到 Inspector / 默认值）。</summary>
        public static void ClearSaved()
        {
            try
            {
                PlayerPrefs.DeleteKey(PrefApiKey);
                PlayerPrefs.DeleteKey(PrefBaseUrl);
                PlayerPrefs.DeleteKey(PrefChatModel);
                PlayerPrefs.DeleteKey(PrefEmbedModel);
                PlayerPrefs.DeleteKey(PrefImageModel);
                PlayerPrefs.DeleteKey(Pref3DModel);
                PlayerPrefs.DeleteKey(PrefOffline);
                PlayerPrefs.DeleteKey(PrefRemoteEmbedding);
                PlayerPrefs.DeleteKey(PrefTimeout);
                PlayerPrefs.DeleteKey(PrefMaxTokens);
                PlayerPrefs.Save();
                Debug.Log("[设置] 已清除本机保存的模型配置");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[设置] 清除配置失败: " + e.Message);
            }
        }

        public string Describe()
        {
            string key = HasKey ? MaskKey(apiKey) : "（未设置）";
            string mode = offline ? "离线模式" : (HasKey ? "远端可用" : "无 Key，离线确定性模式");
            return "Key=" + key + " · 端点=" + ResolvedBaseUrl + " · 对话=" + ResolvedChatModel
                   + " · 向量=" + ResolvedEmbedModel + " · 超时=" + ResolvedTimeoutSeconds + "s"
                   + " · max_tokens=" + ResolvedMaxTokens
                   + (knowledgeRemoteEmbedding ? " · 知识库远端向量" : "")
                   + " · " + mode;
        }

        /// <summary>日志/UI 里只显示 Key 的头尾，避免整串泄漏到 Console。</summary>
        public static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "（未设置）";
            string trimmed = key.Trim();
            if (trimmed.Length <= 8) return "****";
            return trimmed.Substring(0, 4) + "…" + trimmed.Substring(trimmed.Length - 4);
        }
    }
}
