// 知识库桥接：把已有的 EpicSpark.AI.Knowledge（Unity 进程内 RAG，600 事件）接到 ILlmService 体系。
// 设计文档第四节：历史事件名称用于检索，重排序确保最相关信息优先呈现给剧情 Agent。
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.AI.Knowledge;
using EpicSpark.Core;

namespace EpicSpark.Bootstrap
{
    public sealed class KnowledgeService : IKnowledgeService, IDisposable
    {
        private readonly KnowledgeSettings _settings;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private KnowledgeClient _client;
        private bool _ready;

        public KnowledgeService(KnowledgeSettings settings)
        {
            _settings = settings;
        }

        public bool IsReady { get { return _ready; } }

        /// <summary>
        /// 配置变更（例如设置界面改了 Key / 端点 / 向量模型）后丢弃已建客户端，
        /// 下次检索时用同一份（已被修改的）KnowledgeSettings 重新初始化。
        /// </summary>
        public void Invalidate()
        {
            _ready = false;
            try { if (_client != null) _client.Dispose(); }
            catch (Exception) { }
            _client = null;
            Debug.Log("[知识库] 配置已变更，将在下次检索时重建客户端");
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            if (_ready) return;
            await _gate.WaitAsync(ct);
            try
            {
                if (_ready) return;
                _client = new KnowledgeClient(_settings);
                await _client.InitializeAsync(ct);
                _ready = true;
                KnowledgeHealth health = await _client.HealthAsync(ct);
                Debug.Log("[知识库] 就绪: " + (health != null ? health.service : "unknown")
                          + " 事件数=" + (_client.Events != null ? _client.Events.Count : 0));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[知识库] 初始化失败，剧情将不使用 RAG 上下文: " + e.Message);
                _ready = false;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetContextAsync(string userEvent, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userEvent)) return string.Empty;
            try
            {
                await InitializeAsync(ct);
                if (!_ready || _client == null) return string.Empty;

                KnowledgeEventCard card = await _client.GetEventCardAsync(userEvent, 0, 0, ct);
                if (card == null) return string.Empty;

                if (card.HasEvent)
                {
                    var sb = new StringBuilder();
                    sb.Append("【知识库事件】").Append(card.@event.title)
                      .Append("（").Append(card.@event.period).Append("｜").Append(card.@event.era)
                      .Append("｜").Append(card.@event.region).Append("）\n");
                    if (!string.IsNullOrEmpty(card.context)) sb.Append(card.context).Append('\n');
                    if (card.@event.gameplay_hooks != null && card.@event.gameplay_hooks.Length > 0)
                    {
                        sb.Append("可供转化的剧情冲突点：\n");
                        foreach (string hook in card.@event.gameplay_hooks)
                            if (!string.IsNullOrEmpty(hook)) sb.Append("- ").Append(hook).Append('\n');
                    }
                    return sb.ToString();
                }

                if (!string.IsNullOrEmpty(card.context))
                    return "【知识库未收录该事件，以下为相近知识】\n" + card.context;
                return string.Empty;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[知识库] 事件卡片获取失败: " + e.Message);
                return string.Empty;
            }
        }

        public async Task<string> SearchAsync(string query, int topK, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;
            try
            {
                await InitializeAsync(ct);
                if (!_ready || _client == null) return string.Empty;
                KnowledgeSearchResponse response = await _client.SearchAsync(query, topK <= 0 ? 5 : topK, true, ct);
                if (response == null || response.items == null || response.items.Length == 0) return string.Empty;
                var sb = new StringBuilder();
                foreach (KnowledgeItem item in response.items)
                {
                    if (item == null || string.IsNullOrEmpty(item.text)) continue;
                    sb.Append("《").Append(item.title).Append("》").Append(item.section_label).Append("：");
                    sb.Append(item.text).Append('\n');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[知识库] 检索失败: " + e.Message);
                return string.Empty;
            }
        }

        /// <summary>把事件的时代与起始年解析出来，供 GameClock 初始化。</summary>
        public async Task<int> TryGetStartYearAsync(string userEvent, CancellationToken ct)
        {
            try
            {
                await InitializeAsync(ct);
                if (!_ready || _client == null) return 0;
                KnowledgeEventCard card = await _client.GetEventCardAsync(userEvent, 0, 0, ct);
                if (card != null && card.HasEvent) return card.@event.start_year;
            }
            catch (Exception) { }
            return 0;
        }

        public void Dispose()
        {
            try { if (_client != null) _client.Dispose(); } catch (Exception) { }
            _client = null;
            _ready = false;
            try { _gate.Dispose(); } catch (Exception) { }
        }
    }
}
