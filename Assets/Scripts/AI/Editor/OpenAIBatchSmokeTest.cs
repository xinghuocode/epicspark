using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.AI.EditorTools
{
    /// <summary>
    /// 命令行冒烟测试: 在 Unity 编辑器内真实发起网络请求, 验证脚手架端到端可用。
    /// 用法 (注意: 不要加 -quit, 脚本完成后自行调用 EditorApplication.Exit):
    ///   Unity -batchmode -projectPath &lt;项目&gt; \
    ///     -executeMethod EpicSpark.AI.EditorTools.OpenAIBatchSmokeTest.Run \
    ///     -logFile smoke.log
    /// 结果写入 /tmp/epicspark_ai_smoke.txt (每行 PASS/FAIL + 详情), 退出码 0=全过, 1=有失败。
    /// 实现说明: Run 为同步入口, 将异步测试挂在 EditorApplication.update 上驱动,
    /// 避免 batchmode 下主线程等待与 Task 续体泵送相互死锁。
    /// </summary>
    public static class OpenAIBatchSmokeTest
    {
        const string ResultPath = "/tmp/epicspark_ai_smoke.txt";
        static readonly StringBuilder Report = new StringBuilder();
        static Task current;
        static double startTime;
        static bool finished;

        public static void Run()
        {
            startTime = EditorApplication.timeSinceStartup;
            current = RunAllAsync();
            EditorApplication.update += Pump;
        }

        static void Pump()
        {
            if (finished) return;
            if (current.IsCompleted)
            {
                finished = true;
                EditorApplication.update -= Pump;
                File.WriteAllText(ResultPath, Report.ToString());
                Debug.Log("[SmokeTest] 报告已写入 " + ResultPath + "\n" + Report);
                EditorApplication.Exit(ContainsFail() ? 1 : 0);
                return;
            }
            if (EditorApplication.timeSinceStartup - startTime > 300)
            {
                finished = true;
                EditorApplication.update -= Pump;
                Fail("Fatal", "整体超时(300s)");
                File.WriteAllText(ResultPath, Report.ToString());
                EditorApplication.Exit(2);
            }
        }

        static async Task RunAllAsync()
        {
            try
            {
                var settings = FindSettings();
                string baseUrl = settings != null ? settings.baseUrl : "http://127.0.0.1:1234/v1";
                string apiKey = settings != null ? settings.apiKey : "";
                string model = settings != null ? settings.model : "qwen/qwen3-coder-30b";
                Info($"endpoint={baseUrl} model={model}");

                using var client = new OpenAIClient(baseUrl, apiKey, model, 120);

                // 1. models
                try
                {
                    var models = await client.ListModelsAsync();
                    int n = models.data?.Length ?? 0;
                    Pass("ListModels", n > 0 ? $"共 {n} 个模型: {string.Join(", ", Array.ConvertAll(models.data, m => m.id))}" : "模型列表为空");
                }
                catch (Exception e) { Fail("ListModels", e.Message); }

                // 2. chat 非流式
                try
                {
                    var resp = await client.ChatAsync(
                        new List<ChatMessage> { ChatMessage.User("用一句话回答: 1+1=?") },
                        options: new ChatOptions { temperature = 0f, maxTokens = 50 });
                    string content = resp.choices?[0]?.message?.content;
                    Pass("ChatNonStream", $"回复: {content} | usage.total={resp.usage?.total_tokens}");
                }
                catch (Exception e) { Fail("ChatNonStream", e.Message); }

                // 3. chat 流式
                try
                {
                    var sb = new StringBuilder();
                    int deltaCount = 0;
                    string finish = null;
                    await client.ChatStreamAsync(
                        new List<ChatMessage> { ChatMessage.User("从1数到5, 用逗号分隔") },
                        onDelta: (delta, role, fr) =>
                        {
                            if (!string.IsNullOrEmpty(delta)) { sb.Append(delta); deltaCount++; }
                            if (fr != null) finish = fr;
                        },
                        options: new ChatOptions { maxTokens = 60 });
                    bool ok = deltaCount > 0 && sb.Length > 0;
                    Pass("ChatStream", ok ? $"收到 {deltaCount} 个增量, finish={finish}, 内容: {sb}" : "未收到任何增量");
                }
                catch (Exception e) { Fail("ChatStream", e.Message); }

                // 4. embeddings (预期本地 chat 模型不支持, 记录行为即可)
                try
                {
                    var emb = await client.CreateEmbeddingAsync("测试");
                    Pass("Embeddings", $"维度: {emb.data?[0]?.embedding?.Length}");
                }
                catch (Exception e) { Fail("Embeddings", e.Message); }
            }
            catch (Exception e)
            {
                Fail("Fatal", e.ToString());
            }
        }

        static OpenAISettings FindSettings()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:OpenAISettings"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var s = AssetDatabase.LoadAssetAtPath<OpenAISettings>(path);
                if (s != null) return s;
            }
            return null;
        }

        static void Pass(string name, string detail) => Report.AppendLine($"PASS {name} | {detail}");
        static void Fail(string name, string detail) => Report.AppendLine($"FAIL {name} | {detail}");
        static void Info(string msg) => Report.AppendLine($"INFO {msg}");
        static bool ContainsFail() => Report.ToString().Contains("FAIL");
    }
}
