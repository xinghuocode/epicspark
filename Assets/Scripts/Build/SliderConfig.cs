// 搭建 Agent 的「滑块」规则。
// 设计文档：BuildRequest.sliderValue ∈ [0,1]，数值越高越严格（越倾向现生成新资产）。
//   滑块 = 1 → 容忍度为 0（最严格）：要求的相似度 = 1.0，正常检索永远达不到，于是总是生成新资产。
//   滑块 = 0 → 容忍度为 1（最宽松）：要求的相似度 = 0.0，任何现有资产都接受，永不生成。
// 推导：tolerance = 1 - sliderValue；requiredSimilarity = 1 - tolerance = sliderValue。
// （注意不能把 requiredSimilarity 直接写成 1 - sliderValue：那样滑块为 1 时阈值是 0，语义会完全反过来。）
using UnityEngine;

namespace EpicSpark.Build
{
    /// <summary>把 BuildRequest.sliderValue 换算成「可复用的最低相似度」，供搭建 Agent 判命中。</summary>
    public static class SliderConfig
    {
        /// <summary>把任意输入收进 [0,1]；NaN → 0.5（中性）。</summary>
        public static float Clamp01(float sliderValue)
        {
            if (float.IsNaN(sliderValue)) return 0.5f;
            if (sliderValue < 0f) return 0f;
            if (sliderValue > 1f) return 1f;
            return sliderValue;
        }

        /// <summary>容忍度 = 1 - 滑块值。0 = 完全无法容忍不匹配，1 = 完全容忍。</summary>
        public static float Tolerance(float sliderValue)
        {
            return 1f - Clamp01(sliderValue);
        }

        /// <summary>
        /// 可直接复用缓存资产所需要的最低相似度（0..1）。
        /// 滑块为 1 时返回 1.0 且比较使用 &gt;=（相似度 1.0 只在文本近似完全相同时出现），故实际等价于「总是生成」。
        /// </summary>
        public static float RequiredSimilarity(float sliderValue)
        {
            return 1f - Tolerance(sliderValue);
        }

        /// <summary>
        /// 滑块推到最右端（=1）时，设计文档要求「总是生成新资产」，因此即使检索到相似度恰好 1.0 的资产也不复用。
        /// </summary>
        public static bool AlwaysGenerate(float sliderValue)
        {
            return Clamp01(sliderValue) >= 0.999f;
        }

        /// <summary>相似度是否达到复用门槛：similarity &gt;= requiredSimilarity；滑块为 1 时恒不复用。</summary>
        public static bool Accepts(float similarity, float sliderValue)
        {
            if (float.IsNaN(similarity)) return false;
            if (AlwaysGenerate(sliderValue)) return false;
            return similarity >= RequiredSimilarity(sliderValue);
        }

        /// <summary>给日志/HUD 看的中文说明。</summary>
        public static string Describe(float sliderValue)
        {
            float slider = Clamp01(sliderValue);
            string level;
            if (slider >= 0.999f) level = "最严格（总是生成新资产）";
            else if (slider >= 0.75f) level = "严格";
            else if (slider >= 0.45f) level = "均衡";
            else if (slider >= 0.25f) level = "宽松";
            else level = "最宽松（总是复用现有资产）";
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "滑块 {0:0.00} / 容忍度 {1:0.00} / 复用门槛相似度 {2:0.00} —— {3}",
                slider, Tolerance(slider), RequiredSimilarity(slider), level);
        }
    }
}
