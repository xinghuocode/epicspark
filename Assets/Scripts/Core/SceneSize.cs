// 剧情驱动的场景尺寸契约。
// 宽度（世界 X）与纵深（世界 Z）相互独立，不再使用固定模板或 120…300 米的推荐硬限制。
using UnityEngine;

namespace EpicSpark.Core
{
    public static class SceneSize
    {
        /// <summary>
        /// 接受剧情给出的任意有限正尺寸；只有无法构成有意义场景的 <=1 米、NaN、Infinity 才回退。
        /// 不设置最大边长。具体几何复杂度由灰盒工具的对象预算控制，而不是篡改场地尺寸。
        /// </summary>
        public static float SideOrDefault(float value, float fallback = 60f)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 1f) return fallback;
            return value;
        }
    }
}
