#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.AI.Knowledge.Editor
{
    /// <summary>Unity 编辑器内的语料校验入口，不需要 Python 或外部进程。</summary>
    public static class KnowledgeEditorTools
    {
        [MenuItem("EpicSpark/Knowledge/Validate Corpus")]
        public static void ValidateCorpus()
        {
            try
            {
                List<KnowledgeEventInfo> events = KnowledgeUnityLoader.LoadEvents("Knowledge/Events");
                List<KnowledgeChunk> chunks = KnowledgeCorpus.BuildChunks(events);
                var titles = new HashSet<string>(StringComparer.Ordinal);
                foreach (KnowledgeEventInfo item in events)
                {
                    if (!titles.Add(item.title)) throw new InvalidOperationException("事件标题重复: " + item.title);
                    if (item.sources == null || item.sources.Length == 0)
                        throw new InvalidOperationException("事件没有来源: " + item.id);
                    if (item.sections.background.Length < 40
                        || item.sections.process.Length < 40
                        || item.sections.impact.Length < 40)
                        throw new InvalidOperationException("必需章节内容过短: " + item.id);

                    KnowledgeMatch match = KnowledgeCorpus.Resolve(events, item.title);
                    if (match == null || match.event_id != item.id)
                        throw new InvalidOperationException(
                            "事件标题解析冲突: " + item.title + " -> "
                            + (match == null ? "null" : match.event_id));
                }
                Debug.Log("[Knowledge] 语料校验通过: " + events.Count + " 个事件 / " + chunks.Count
                    + " 个文本块 / 指纹 " + KnowledgeCorpus.Fingerprint(chunks).Substring(0, 12));
            }
            catch (Exception exception)
            {
                Debug.LogError("[Knowledge] 语料校验失败: " + exception);
            }
        }
    }
}
#endif
