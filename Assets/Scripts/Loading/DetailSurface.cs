using System;
using UnityEngine;

namespace EpicSpark.Loading
{
    /// <summary>
    /// 近景细节层（Detail Surface）。
    ///
    /// 背景：GLB 自带的 2048 图集在"整栋入画"时约 1:1 清晰，但被等比放大到 12 m 建筑后
    /// 纹素密度只剩约 38 texel/m。第一人称贴近 1–2 m 时每个纹素占 12–25 屏幕像素，
    /// 于是糊成大色块。单张贴图无法再提高密度（2048→8192 需要 357 MB/模型）。
    ///
    /// 方案：复用 URP Lit 内置的 detail map 通道（_DETAIL_MULX2），把一张"可平铺高频颗粒"
    /// 按 UV 平铺若干次叠乘到 albedo 上，并叠加细节法线。平铺次数与物体尺寸解耦，
    /// 因此不管模型被放大多少倍，颗粒在屏幕上的物理尺寸都基本恒定。
    ///
    /// 回退：
    ///   1) 运行期 PlayerPrefs "epicspark.detail" = 0，或环境变量 EPICSPARK_DETAIL=0；
    ///   2) 或把 DefaultEnabled 改成 false 重新编译；
    ///   3) 或删掉本文件 + 还原 GlbModelLoader 中的一行调用（见 GlbModelLoader.CreateMaterial）。
    /// 关闭后材质与改造前完全一致。
    /// </summary>
    public static class DetailSurface
    {
        /// <summary>默认是否启用。false = 完全回到改造前行为。</summary>
        public const bool DefaultEnabled = true;

        /// <summary>是否叠加细节法线。若观察到光照异常（法线打包格式不匹配）可置 false。</summary>
        public const bool UseNormalMap = true;

        /// <summary>细节图尺寸（正方形）。256 足够覆盖 1 cm 级颗粒。</summary>
        public const int TextureSize = 256;

        /// <summary>每个模型上的平铺次数。26 表示"一个模型上重复 26 次"，与放置尺寸无关。</summary>
        public const float TilesPerModel = 26f;

        /// <summary>albedo 乘子幅度：0.035 → 亮度 1±7%（URP detail 内部会 ×2）。</summary>
        public const float AlbedoAmplitude = 0.035f;

        /// <summary>法线强度（高度场梯度放大）。</summary>
        public const float NormalStrength = 2.0f;

        public const string PrefKey = "epicspark.detail";

        private static bool? _enabledOverride;
        private static Texture2D _albedo;
        private static Texture2D _normal;
        private static bool _buildFailed;

        /// <summary>开关。优先取运行期 SetEnabled，其次环境变量，最后 PlayerPrefs（缺省 1）。</summary>
        public static bool Enabled
        {
            get
            {
                if (_enabledOverride.HasValue) return _enabledOverride.Value;
                string env = Environment.GetEnvironmentVariable("EPICSPARK_DETAIL");
                if (!string.IsNullOrEmpty(env))
                {
                    if (env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    if (env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return PlayerPrefs.GetInt(PrefKey, DefaultEnabled ? 1 : 0) != 0;
            }
        }

        /// <summary>运行期开关（同时写入 PlayerPrefs，下次启动保持）。</summary>
        public static void SetEnabled(bool on)
        {
            _enabledOverride = on;
            PlayerPrefs.SetInt(PrefKey, on ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>把细节层挂到材质上。mesh 用来估算该模型的 UV 密度，从而决定平铺次数。</summary>
        public static void Apply(Material material, Mesh mesh)
        {
            if (!Enabled || material == null) return;
            // 只有 URP Lit（或复杂 Lit）有这套属性；Standard / Sprites 直接跳过。
            if (!material.HasProperty("_DetailAlbedoMap")) return;
            if (!EnsureTextures()) return;

            float tiling = ComputeTiling(mesh);
            material.SetTexture("_DetailAlbedoMap", _albedo);
            material.SetTextureScale("_DetailAlbedoMap", new Vector2(tiling, tiling));
            material.SetTextureOffset("_DetailAlbedoMap", Vector2.zero);
            material.SetFloat("_DetailAlbedoMapScale", 1f);
            if (UseNormalMap && _normal != null)
            {
                material.SetTexture("_DetailNormalMap", _normal);
                material.SetFloat("_DetailNormalMapScale", 1f);
            }
            // _DETAIL_MULX2 / _DETAIL_SCALED 由 shader_feature 控制，二者都没有 = 不采样细节图。
            material.DisableKeyword("_DETAIL_SCALED");
            material.EnableKeyword("_DETAIL_MULX2");
        }

        /// <summary>
        /// ST 缩放 = 目标平铺数 / (UV 密度 × 模型对角线)。
        /// UV 密度单位是 UV/原生米，对角线单位是原生米，两者相乘 ≈ 整个模型在 UV 空间跨过的距离，
        /// 因此结果就是"每个模型重复多少次"，与后续放置尺寸无关。
        /// </summary>
        private static float ComputeTiling(Mesh mesh)
        {
            if (mesh == null) return TilesPerModel;
            float uvDensity = EstimateUvDensity(mesh);
            float diag = mesh.bounds.size.magnitude;
            if (uvDensity <= 1e-5f || diag <= 1e-5f) return TilesPerModel;
            float tiling = TilesPerModel / (uvDensity * diag);
            return Mathf.Clamp(tiling, 1f, 8192f);
        }

        /// <summary>用"UV 周长 / 三维周长"的中位数估算 UV 密度（UV 单位 / 模型原生米）。</summary>
        private static float EstimateUvDensity(Mesh mesh)
        {
            try
            {
                Vector3[] v = mesh.vertices;
                Vector2[] uv = mesh.uv;
                int[] tri = mesh.triangles;
                if (v == null || uv == null || tri == null || v.Length == 0 || uv.Length != v.Length) return 0f;

                int triCount = tri.Length / 3;
                int step = Mathf.Max(1, triCount / 4000); // 大模型抽样，避免加载卡顿
                var samples = new System.Collections.Generic.List<float>(triCount / step + 1);
                for (int t = 0; t < triCount; t += step)
                {
                    int i0 = tri[t * 3], i1 = tri[t * 3 + 1], i2 = tri[t * 3 + 2];
                    float d3 = Vector3.Distance(v[i0], v[i1]) + Vector3.Distance(v[i1], v[i2]) + Vector3.Distance(v[i2], v[i0]);
                    if (d3 < 1e-6f) continue;
                    float d2 = Vector2.Distance(uv[i0], uv[i1]) + Vector2.Distance(uv[i1], uv[i2]) + Vector2.Distance(uv[i2], uv[i0]);
                    samples.Add(d2 / d3);
                }
                if (samples.Count == 0) return 0f;
                samples.Sort();
                return samples[samples.Count / 2];
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Detail] UV 密度估算失败: " + e.Message);
                return 0f;
            }
        }

        private static bool EnsureTextures()
        {
            if (_albedo != null && (_normal != null || !UseNormalMap)) return true;
            if (_buildFailed) return false;
            try
            {
                float[] height = BuildTileableHeight(TextureSize);
                _albedo = BuildAlbedoTexture(height);
                if (UseNormalMap) _normal = BuildNormalTexture(height);
                return _albedo != null;
            }
            catch (Exception e)
            {
                _buildFailed = true;
                Debug.LogWarning("[Detail] 细节图生成失败，已跳过: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 可平铺高度场：四个八度的周期值噪声叠加，再 z-score 归一到均值 0 / 标准差 1。
        /// 周期格点保证左右上下严格无缝，wrapMode=Repeat 不会露出接缝。
        /// </summary>
        private static float[] BuildTileableHeight(int size)
        {
            int[] freqs = { 8, 16, 32, 64 };
            float[] weights = { 0.5f, 0.28f, 0.15f, 0.07f };
            var acc = new float[size * size];
            float weightSum = 0f;

            for (int o = 0; o < freqs.Length; o++)
            {
                int f = freqs[o];
                var lattice = new float[f * f];
                uint seed = (uint)(0x9E3779B9u * (uint)(o + 1) + 0x85EBCA6Bu);
                for (int i = 0; i < lattice.Length; i++)
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; // xorshift32
                    lattice[i] = (seed & 0xFFFFFF) / 16777215f;
                }

                float amp = weights[o];
                weightSum += amp;
                for (int y = 0; y < size; y++)
                {
                    float fy = (float)y / size * f;
                    int y0 = (int)fy % f;
                    int y1 = (y0 + 1) % f;
                    float ty = fy - Mathf.Floor(fy);
                    ty = ty * ty * (3f - 2f * ty); // smoothstep

                    int row0 = y0 * f, row1 = y1 * f, dst = y * size;
                    for (int x = 0; x < size; x++)
                    {
                        float fx = (float)x / size * f;
                        int x0 = (int)fx % f;
                        int x1 = (x0 + 1) % f;
                        float tx = fx - Mathf.Floor(fx);
                        tx = tx * tx * (3f - 2f * tx);

                        float a = Mathf.Lerp(lattice[row0 + x0], lattice[row0 + x1], tx);
                        float b = Mathf.Lerp(lattice[row1 + x0], lattice[row1 + x1], tx);
                        acc[dst + x] += amp * Mathf.Lerp(a, b, ty);
                    }
                }
            }

            // 归一化：均值 0，标准差 1
            float inv = 1f / Mathf.Max(weightSum, 1e-6f);
            double sum = 0.0;
            for (int i = 0; i < acc.Length; i++) { acc[i] *= inv; sum += acc[i]; }
            float mean = (float)(sum / acc.Length);

            double varSum = 0.0;
            for (int i = 0; i < acc.Length; i++) { float d = acc[i] - mean; varSum += d * d; }
            float std = Mathf.Sqrt((float)(varSum / acc.Length));
            std = Mathf.Max(std, 1e-5f);

            for (int i = 0; i < acc.Length; i++) acc[i] = (acc[i] - mean) / std;
            return acc;
        }

        /// <summary>
        /// 细节 albedo：均值 0.5（URP 内部 ×2 → 中性 1.0），幅度 ±AlbedoAmplitude。
        /// 必须 linear=true，否则 0.5 灰会被 sRGB 解成 0.214 线性，整个模型变暗。
        /// </summary>
        private static Texture2D BuildAlbedoTexture(float[] height)
        {
            int size = TextureSize;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true);
            tex.name = "EpicSpark_DetailAlbedo";
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = 0.5f + AlbedoAmplitude * Mathf.Clamp(height[i], -3f, 3f);
                byte g = (byte)(Mathf.Clamp01(v) * 255f + 0.5f);
                pixels[i] = new Color32(g, g, g, 255);
            }
            tex.SetPixels32(pixels);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// 细节法线：Sobel 求高度梯度，按 Unity 的 DXT5nm 布局打包（A=x, G=y），
        /// 这样 UnpackNormal 走 UnpackNormalDXT5nm 分支时能正确还原。
        /// </summary>
        private static Texture2D BuildNormalTexture(float[] height)
        {
            int size = TextureSize;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true);
            tex.name = "EpicSpark_DetailNormal";
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                int ym = (y - 1 + size) % size;
                int yp = (y + 1) % size;
                for (int x = 0; x < size; x++)
                {
                    int xm = (x - 1 + size) % size;
                    int xp = (x + 1) % size;
                    float dx = (height[y * size + xp] - height[y * size + xm]) * 0.5f * NormalStrength;
                    float dy = (height[yp * size + x] - height[ym * size + x]) * 0.5f * NormalStrength;
                    var n = new Vector3(-dx, -dy, 1f).normalized;
                    byte a = (byte)(Mathf.Clamp01(n.x * 0.5f + 0.5f) * 255f + 0.5f);
                    byte g = (byte)(Mathf.Clamp01(n.y * 0.5f + 0.5f) * 255f + 0.5f);
                    pixels[y * size + x] = new Color32(128, g, 128, a);
                }
            }
            tex.SetPixels32(pixels);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            tex.Apply(true, false);
            return tex;
        }
    }
}
