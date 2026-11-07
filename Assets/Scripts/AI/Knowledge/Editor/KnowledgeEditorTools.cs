#if UNITY_EDITOR
// 编辑器内的语料校验入口，不需要 Python 或任何外部进程。
// 只使用 KnowledgeClient 的公开 API（Runtime 程序集里的 KnowledgeCorpus / KnowledgeUnityLoader 是 internal，
// 跨程序集不可见，原仓库的实现会在 Editor 程序集里编译失败）。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using EpicSpark.AI.Knowledge;

namespace EpicSpark.AI.Knowledge.Editor
{
    public static class KnowledgeEditorTools
    {
        [MenuItem("EpicSpark/Knowledge/Validate Corpus")]
        public static void ValidateCorpus()
        {
            _ = ValidateCorpusAsync();
        }

        private static async System.Threading.Tasks.Task ValidateCorpusAsync()
        {
            KnowledgeClient client = null;
            try
            {
                var settings = ScriptableObject.CreateInstance<KnowledgeSettings>();
                settings.name = "CorpusValidationSettings";
                client = new KnowledgeClient(settings);
                await client.InitializeAsync(default);

                IReadOnlyList<KnowledgeEventInfo> events = client.Events;
                KnowledgeHealth health = await client.HealthAsync(default);

                var titles = new HashSet<string>(StringComparer.Ordinal);
                var problems = new List<string>();
                foreach (KnowledgeEventInfo item in events)
                {
                    if (item == null) continue;
                    if (!titles.Add(item.title)) problems.Add("事件标题重复: " + item.title);
                    if (item.sources == null || item.sources.Length == 0) problems.Add("事件没有来源: " + item.id);
                    if (item.sections == null)
                    {
                        problems.Add("事件缺少章节: " + item.id);
                        continue;
                    }
                    if ((item.sections.background ?? "").Length < 40
                        || (item.sections.process ?? "").Length < 40
                        || (item.sections.impact ?? "").Length < 40)
                        problems.Add("必须章节内容过短: " + item.id);
                }

                // 每个事件的标题都应能解析回它自己
                int resolveFailures = 0;
                foreach (KnowledgeEventInfo item in events)
                {
                    if (item == null) continue;
                    KnowledgeMatch match = await client.ResolveAsync(item.title, default);
                    if (match == null || match.event_id != item.id) resolveFailures++;
                }
                if (resolveFailures > 0) problems.Add("标题解析失败的个数: " + resolveFailures);

                if (problems.Count == 0)
                {
                    Debug.Log("[Knowledge] 语料校验通过: " + events.Count + " 个事件 / 文本块 "
                              + (health != null ? health.chunks : 0) + " / 索引就绪 "
                              + (health != null && health.index_ready));
                }
                else
                {
                    for (int i = 0; i < problems.Count && i < 20; i++) Debug.LogError("[Knowledge] " + problems[i]);
                    Debug.LogError("[Knowledge] 语料校验发现问题共 " + problems.Count + " 条");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[Knowledge] 语料校验失败: " + exception);
            }
            finally
            {
                if (client != null) client.Dispose();
            }
        }

        [MenuItem("EpicSpark/Knowledge/Rebuild Index")]
        public static void RebuildIndex()
        {
            _ = RebuildIndexAsync();
        }

        private static async System.Threading.Tasks.Task RebuildIndexAsync()
        {
            try
            {
                var settings = ScriptableObject.CreateInstance<KnowledgeSettings>();
                using (var client = new KnowledgeClient(settings))
                {
                    await client.RebuildIndexAsync(default);
                    KnowledgeHealth health = await client.HealthAsync(default);
                    Debug.Log("[Knowledge] 索引重建完成: 事件 " + (health != null ? health.events : 0)
                              + " / 文本块 " + (health != null ? health.chunks : 0));
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[Knowledge] 索引重建失败: " + exception);
            }
        }
    }
}
#endif
