using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>从 Unity Resources 读取 JSONL 语料；Android 包内同样可用，不依赖服务器文件路径。</summary>
    internal static class KnowledgeUnityLoader
    {
        public static List<KnowledgeEventInfo> LoadEvents(string resourcePath)
        {
            TextAsset[] assets = Resources.LoadAll<TextAsset>(
                string.IsNullOrEmpty(resourcePath) ? "Knowledge/Events" : resourcePath);
            if (assets == null || assets.Length == 0)
                throw new InvalidOperationException("Resources/" + resourcePath + " 下没有知识库 .txt 语料");

            var events = new List<KnowledgeEventInfo>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TextAsset asset in assets.OrderBy(asset => asset.name, StringComparer.Ordinal))
            {
                string[] lines = asset.text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    string line = lines[lineIndex].Trim().TrimStart('\ufeff');
                    if (line.Length == 0 || line.StartsWith("//")) continue;
                    KnowledgeEventInfo item;
                    try
                    {
                        item = JsonUtility.FromJson<KnowledgeEventInfo>(line);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            asset.name + ".txt:" + (lineIndex + 1) + " JSON 解析失败: " + exception.Message,
                            exception);
                    }
                    Validate(item, asset.name, lineIndex + 1);
                    if (!ids.Add(item.id))
                        throw new InvalidOperationException("知识库事件 id 重复: " + item.id);
                    events.Add(item);
                }
            }
            return events;
        }

        private static void Validate(KnowledgeEventInfo item, string assetName, int line)
        {
            string where = assetName + ".txt:" + line;
            if (item == null) throw new InvalidOperationException(where + " 不是事件对象");
            if (string.IsNullOrEmpty(item.id)) throw new InvalidOperationException(where + " 缺少 id");
            if (string.IsNullOrEmpty(item.title)) throw new InvalidOperationException(where + " 缺少 title");
            if (item.sections == null) throw new InvalidOperationException(where + " 缺少 sections");
            if (string.IsNullOrWhiteSpace(item.sections.background)
                || string.IsNullOrWhiteSpace(item.sections.process)
                || string.IsNullOrWhiteSpace(item.sections.impact))
                throw new InvalidOperationException(where + " sections 必须包含 background/process/impact");
            if (item.end_year < item.start_year)
                throw new InvalidOperationException(where + " end_year 早于 start_year");
            if (item.aliases == null) item.aliases = new string[0];
            if (item.keywords == null) item.keywords = new string[0];
            if (item.timeline == null) item.timeline = new KnowledgeTimelineItem[0];
            if (item.gameplay_hooks == null) item.gameplay_hooks = new string[0];
            if (item.sources == null) item.sources = new KnowledgeSource[0];
        }
    }
}
