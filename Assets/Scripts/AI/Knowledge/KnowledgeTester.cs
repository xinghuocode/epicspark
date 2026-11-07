using System.Threading.Tasks;
using UnityEngine;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// 运行时冒烟测试组件：挂到场景任意物体上，Play 后打印服务健康状况与一次真实检索结果。
    /// 与 OpenAITester 同款用法，便于主程序接入前先确认 RAG 服务是否可用。
    /// </summary>
    public class KnowledgeTester : MonoBehaviour
    {
        [Tooltip("知识库服务配置资源；为空时使用 http://127.0.0.1:8787")]
        public KnowledgeSettings settings;

        [Tooltip("开局要检索的历史事件，用来验证事件卡片链路")]
        public string sampleEvent = "法国大革命";

        [Tooltip("Play 时自动执行")]
        public bool runOnStart = true;

        async void Start()
        {
            if (runOnStart)
                await RunAsync();
        }

        [ContextMenu("Run Knowledge Smoke Test")]
        public async Task RunAsync()
        {
            var client = settings != null
                ? new KnowledgeClient(settings)
                : new KnowledgeClient("http://127.0.0.1:8787");

            try
            {
                var health = await client.HealthAsync();
                Debug.Log($"[Knowledge] 状态={health.status} 事件={health.events} 块={health.chunks} " +
                          $"嵌入={health.embedding?.model}({health.embedding?.dim}) 重排={health.reranker?.model}");
                if (health.status != "ok")
                    Debug.LogWarning("[Knowledge] 服务处于降级模式，请检查 API Key / 网络 / 索引");

                var card = await client.GetEventCardAsync(sampleEvent);
                if (card.HasEvent)
                {
                    Debug.Log($"[Knowledge] 命中 {card.event.title}（{card.event.period}，" +
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
            catch (KnowledgeApiException exc)
            {
                Debug.LogError("[Knowledge] 服务返回错误: " + exc.Message);
            }
            catch (System.Exception exc)
            {
                Debug.LogError("[Knowledge] 无法连接知识库服务（是否已运行 Tools/knowledge_rag/server.py？）: " + exc.Message);
            }
        }
    }
}
