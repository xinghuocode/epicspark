using System;
using System.Threading.Tasks;
using UnityEngine;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>运行时冒烟测试：不启动任何服务，直接在 Unity 进程内初始化并检索。</summary>
    public class KnowledgeTester : MonoBehaviour
    {
        [Tooltip("知识库配置；为空时仅用 Unity 内本地向量与重排")]
        public KnowledgeSettings settings;

        [Tooltip("开局要检索的历史事件")]
        public string sampleEvent = "法国大革命";

        public bool runOnStart = true;

        private KnowledgeClient client;

        async void Start()
        {
            if (runOnStart) await RunAsync();
        }

        [ContextMenu("Run Knowledge Smoke Test")]
        public async Task RunAsync()
        {
            try
            {
                if (client == null && settings != null)
                {
                    client = new KnowledgeClient(settings);
                }
                else if (client == null)
                {
                    var localSettings = ScriptableObject.CreateInstance<KnowledgeSettings>();
                    localSettings.embedProvider = KnowledgeProviderMode.Local;
                    localSettings.rerankProvider = KnowledgeProviderMode.Local;
                    client = new KnowledgeClient(localSettings);
                }

                var health = await client.HealthAsync();
                Debug.Log($"[Knowledge] 模式={health.service} 状态={health.status} 事件={health.events} 块={health.chunks} " +
                          $"嵌入={health.embedding?.model}({health.embedding?.dim}) 重排={health.reranker?.model}");
                if (health.status != "ok")
                    Debug.LogWarning("[Knowledge] 远端模型不可用，已使用 Unity 内实现：" + health.embedding?.degraded_reason);

                var card = await client.GetEventCardAsync(sampleEvent);
                if (card.HasEvent)
                {
                    Debug.Log($"[Knowledge] 命中 {card.@event.title}（{card.@event.period}，" +
                              $"置信度 {card.matched.confidence:F2}），证据 {card.evidences?.Length ?? 0} 条");
                    Debug.Log("[Knowledge] 上下文预览: " +
                              (card.context.Length > 160 ? card.context.Substring(0, 160) + "…" : card.context));
                }
                else
                {
                    Debug.LogWarning($"[Knowledge] 未收录「{sampleEvent}」，回退通用检索：" +
                                     (card.context.Length > 120 ? card.context.Substring(0, 120) + "…" : card.context));
                }

                var hits = await client.SearchAsync("巴士底狱", topK: 3);
                foreach (var item in hits.items)
                {
                    Debug.Log($"[Knowledge] #{item.rank} {item.title} / {item.section_label} / " +
                              $"final={item.scores.final:F3}（dense={item.scores.dense:F3}, rerank={item.scores.rerank:F3}）");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[Knowledge] Unity 内知识库初始化或检索失败: " + exception);
            }
        }

        [ContextMenu("Rebuild Knowledge Index")]
        public async Task RebuildAsync()
        {
            if (client == null) return;
            var health = await client.RebuildIndexAsync();
            Debug.Log($"[Knowledge] 索引已重建：{health.chunks} 块，{health.embedding.dim} 维");
        }

        private void OnDestroy()
        {
            client?.Dispose();
            client = null;
        }
    }
}
