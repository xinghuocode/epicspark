// 运行时开关：新链路（LayoutSpec 执行器 / 合规校验）默认开启，但可以一键回退旧行为。
//
// 回退方式（优先级从高到低）：
//   1) 环境变量 EPICSPARK_LAYOUT_SPEC=0        → 完全走旧 GrayboxAgent 远端/本地逻辑
//   2) 环境变量 EPICSPARK_LAYOUT_SPEC=1        → 强制新链路
//   3) 代码里 EpicSparkFeatureFlags.UseLayoutSpecExecutor = false（编辑器/测试可改）
//
// 新链路内部还有自己的回退：
//   远端不合规 / 超时 / 异常 → GrayboxSpecExecutor 的确定性规格计划
//   规格计划也不可用        → 旧 GrayboxPlanner.Plan
using System;

namespace EpicSpark.Core
{
    public static class EpicSparkFeatureFlags
    {
        private static int? layoutSpecOverride;

        /// <summary>是否用 LayoutSpec 驱动灰盒（默认 true）。</summary>
        public static bool UseLayoutSpecExecutor
        {
            get
            {
                if (layoutSpecOverride.HasValue) return layoutSpecOverride.Value != 0;
                string env = Environment.GetEnvironmentVariable("EPICSPARK_LAYOUT_SPEC");
                if (!string.IsNullOrEmpty(env))
                {
                    if (env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return false;
                    if (env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return true;
            }
            set { layoutSpecOverride = value ? 1 : 0; }
        }

        /// <summary>远端结果不合规时是否强制回退规格执行器（默认 true；测试可关掉看远端原始结果）。</summary>
        public static bool StrictSpecCompliance
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("EPICSPARK_SPEC_STRICT");
                if (!string.IsNullOrEmpty(env))
                    return !(env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase));
                return true;
            }
        }

        /// <summary>
        /// 是否把「压路 / 建筑重叠」当成硬错误（默认 false，只作为提醒）。
        /// 剧情自身可能把道路画到建筑边缘（例如巴士底要塞的护城河），此时若当硬错误，
        /// 远端即使完整执行规格也会被误判回退；缺项/位移/数量/排屋/未 finish 仍是硬错误。
        /// 需要更严格时设 EPICSPARK_SPEC_GEOMETRY=1。
        /// </summary>
        public static bool StrictSpecGeometry
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("EPICSPARK_SPEC_GEOMETRY");
                if (!string.IsNullOrEmpty(env))
                    return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
                return false;
            }
        }

        /// <summary>Assembler 是否优先用 plan 里的锚点/NPC 槽位（默认 true）。</summary>
        public static bool PreferPlanUnitPositions
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("EPICSPARK_PLAN_UNIT_POS");
                if (!string.IsNullOrEmpty(env))
                    return !(env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase));
                return true;
            }
        }

        /// <summary>
        /// 是否用 SceneGen 求解器生成完整施工图（默认 true）。
        /// 打开后：剧情只给语义骨架（分区/街道/地标/锚点），地块切分、临街立面、围合、
        /// 道具散布全部由确定性求解器完成，灰盒不再需要"理解散文"。
        /// EPICSPARK_SCENEGEN=0 可回退到旧的 LayoutSpec 翻译链路。
        /// </summary>
        public static bool UseSceneGenSolver
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("EPICSPARK_SCENEGEN");
                if (!string.IsNullOrEmpty(env))
                    return !(env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase));
                return true;
            }
        }

        /// <summary>
        /// 当前阶段强制使用程序化几何体。旧 GLB 美术库、运行时模型检索和 AI 资产生成均禁用；
        /// 不再提供 EPICSPARK_PRIMITIVES=0 的隐式回退，避免测试与玩家跑到两条不同的链路。
        /// </summary>
        public static bool UsePrimitiveScene
        {
            get { return true; }
        }

        /// <summary>SceneGen KPI 门禁：不达标时自动补齐/收缩（默认 true）。EPICSPARK_SCENEGEN_GATE=0 关闭。</summary>
        public static bool SceneGenGate
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("EPICSPARK_SCENEGEN_GATE");
                if (!string.IsNullOrEmpty(env))
                    return !(env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase));
                return true;
            }
        }
    }
}
