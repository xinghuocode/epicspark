using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EpicSpark.AI.OpenAI
{
    /// <summary>
    /// 运行时冒烟测试。把该组件挂在场景任意物体上, 在 Inspector 中指定 OpenAISettings,
    /// 勾选 autoRunOnStart 后点 Play, 或在右键上下文菜单中手动触发每一项测试。
    /// 输出见 Console (Tag = [OpenAITest])。
    /// </summary>
    public class OpenAITester : MonoBehaviour
    {
        public OpenAISettings settings;
        public bool autoRunOnStart = true;

        OpenAIClient client;

        void Start()
        {
            if (!autoRunOnStart) return;
            if (settings == null)
            {
                Debug.LogError("[OpenAITest] 请在 Inspector 中指定 OpenAISettings 资源");
                return;
            }
            client = new OpenAIClient(settings);
            RunAll();
        }

        void OnDestroy() => client?.Dispose();

        [ContextMenu("Run All Tests")]
        public void RunAll()
        {
            if (client == null)
            {
                if (settings == null) { Debug.LogError("[OpenAITest] 未指定 OpenAISettings"); return; }
                client = new OpenAIClient(settings);
            }
            TestListModels();
            TestChatNonStream();
            TestChatStream();
        }

        [ContextMenu("Test 1: List Models")]
        public async void TestListModels()
        {
            try
            {
                var resp = await client.ListModelsAsync();
                var sb = new StringBuilder("[OpenAITest] 模型列表:\n");
                if (resp.data != null)
                    foreach (var m in resp.data) sb.AppendLine("  - " + m.id);
                Debug.Log(sb.ToString());
            }
            catch (Exception e) { Debug.LogError("[OpenAITest] ListModels 失败: " + e.Message); }
        }

        [ContextMenu("Test 2: Chat (Non-Stream)")]
        public async void TestChatNonStream()
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    ChatMessage.System("You are a helpful assistant."),
                    ChatMessage.User("用一句话回答: 1+1=?")
                };
                var options = new ChatOptions { temperature = 0.2f, maxTokens = 100 };
                var resp = await client.ChatAsync(messages, options: options);
                string content = resp.choices?[0]?.message?.content ?? "(空)";
                Debug.Log($"[OpenAITest] 非流式回复: {content}\n" +
                          $"  usage: prompt={resp.usage?.prompt_tokens} completion={resp.usage?.completion_tokens} total={resp.usage?.total_tokens}");
            }
            catch (Exception e) { Debug.LogError("[OpenAITest] Chat(非流式) 失败: " + e.Message); }
        }

        [ContextMenu("Test 3: Chat (Stream)")]
        public async void TestChatStream()
        {
            try
            {
                var messages = new List<ChatMessage> { ChatMessage.User("用中文说一段50字左右的欢迎语。") };
                var options = new ChatOptions { maxTokens = 200 };
                var sb = new StringBuilder();
                string role = null, finishReason = null;

                await client.ChatStreamAsync(
                    messages,
                    onDelta: (delta, r, fr) =>
                    {
                        if (r != null) role = r;
                        if (fr != null) finishReason = fr;
                        if (!string.IsNullOrEmpty(delta)) sb.Append(delta);
                    },
                    options: options);

                Debug.Log($"[OpenAITest] 流式回复完成 (role={role}, finish={finishReason}):\n{sb}");
            }
            catch (Exception e) { Debug.LogError("[OpenAITest] Chat(流式) 失败: " + e.Message); }
        }
    }
}
