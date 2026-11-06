using EpicSpark.AI.OpenAI;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.AI.EditorTools
{
    /// <summary>
    /// 在 OpenAISettings 资源的 Inspector 上增加便捷按钮,
    /// 可直接在编辑器内对配置的端点发起连通性测试(非流式 chat)。
    /// </summary>
    [CustomEditor(typeof(OpenAISettings))]
    public class OpenAISettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var s = (OpenAISettings)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "1. 填入 Base URL / API Key / 模型名\n" +
                "2. 点击下方按钮测试连通性\n" +
                "3. 运行时把该资源拖到 OpenAITester.settings 上即可使用",
                MessageType.Info);

            GUI.enabled = !EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode;
            if (GUILayout.Button("Ping (发送一条测试消息)", GUILayout.Height(28)))
            {
                Ping(s);
            }
            GUI.enabled = true;
        }

        async void Ping(OpenAISettings s)
        {
            try
            {
                using var client = new OpenAIClient(s);
                var resp = await client.ChatAsync(
                    new System.Collections.Generic.List<ChatMessage> { ChatMessage.User("ping, 请回复 pong") },
                    options: new ChatOptions { maxTokens = 10 });
                Debug.Log($"[OpenAITest] Ping 成功 → {resp.choices?[0]?.message?.content}");
                EditorUtility.DisplayDialog("Ping 成功", "端点连通正常, 模型已响应。", "OK");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[OpenAITest] Ping 失败: " + e.Message);
                EditorUtility.DisplayDialog("Ping 失败", e.Message, "OK");
            }
        }
    }
}
