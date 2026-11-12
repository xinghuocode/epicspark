// 美术资产生成链路的编辑器自测入口：
//   菜单 EpicSpark/测试美术资产生成 → 读 assetgen_config.json → 文生图 → 抠图 → 图生 3D → 落盘 GLB。
// 不依赖 Play Mode、不依赖场景里有没有缺资产的槽位，用来快速确认 Key/端点/模型是否可用。
// 注意：一次真实生成约 1–2 分钟（Hunyuan3D 异步任务），Console 会打印每一步。
using System;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AssetGen;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.EditorTools
{
    public static class AssetGenTestMenu
    {
        private const string TestPrompt = "a wooden medieval barrel, single object, centered, plain white background, product photo, front view, no people, no scenery";

        [MenuItem("EpicSpark/测试美术资产生成（文生图→抠图→3D）")]
        public static async void Run()
        {
            AssetGenSettings settings = AssetGenSettings.LoadOrCreate();
            if (!settings.HasRemote)
            {
                EditorUtility.DisplayDialog("美术资产生成", "未配置 baseUrl / apiKey / 模型名，当前是离线模式。\n\n"
                    + "请先在游戏内 F10 设置界面填写 API Key，或直接编辑：\n" + AssetGenSettings.ConfigPath, "好");
                return;
            }

            Debug.Log("[测试] 美术资产生成开始：" + settings.Describe());
            var pipeline = new AssetGenPipeline(settings);
            try
            {
                AssetGenResult result = await pipeline.RunAsync(TestPrompt, "prop", CancellationToken.None);
                if (result.success && !string.IsNullOrEmpty(result.glbPath))
                {
                    Debug.Log("[测试] 资产生成成功：\n  GLB = " + result.glbPath
                              + "\n  透明图 = " + result.transparentImagePath
                              + "\n  提示词 = " + result.prompt);
                    EditorUtility.RevealInFinder(result.glbPath);
                }
                else
                {
                    Debug.LogError("[测试] 资产生成失败（stage=" + result.stage + "）：" + result.error);
                    EditorUtility.DisplayDialog("美术资产生成失败",
                        "失败阶段：" + result.stage + "\n" + result.error + "\n\n详见 Console 与 Editor.log。", "好");
                }
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[测试] 资产生成被取消。");
            }
            catch (Exception e)
            {
                Debug.LogError("[测试] 资产生成异常：" + e);
                EditorUtility.DisplayDialog("美术资产生成异常", e.Message + "\n\n详见 Console。", "好");
            }
        }
    }
}
