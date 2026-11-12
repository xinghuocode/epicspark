// 灰盒 Agent 的确定性工具执行器。
// 远端/离线都只提供场景需求；真正的空间状态只能由 GrayboxToolWorkspace 的工具产生。
using EpicSpark.Core;

namespace EpicSpark.SceneGen
{
    public static class GrayboxToolPipeline
    {
        public static GrayboxPlan Build(SceneSpec scene, LayoutSpec semantic, int seed)
        {
            GrayboxToolReport report;
            return Build(scene, semantic, seed, out report);
        }

        public static GrayboxPlan Build(SceneSpec scene, LayoutSpec semantic, int seed, out GrayboxToolReport report)
        {
            var workspace = new GrayboxToolWorkspace(scene, semantic, seed);
            report = workspace.Report;

            // 理解剧情需求 → 操作灰盒 → 自检；顺序即工具协议，不允许后期改写剧情对象。
            workspace.ImportStoryRequirements();
            workspace.EnsureDistrictStructure();
            workspace.CreateStreetRows();
            workspace.DecorateZones();
            workspace.BindStoryUnits();
            return workspace.Complete();
        }
    }
}
