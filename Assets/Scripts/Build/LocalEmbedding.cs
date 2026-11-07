// 本地确定性词嵌入：中英混排 → 特征哈希 → 1024 维 L2 归一化向量。
// 与 Assets/Scripts/AI/Knowledge/KnowledgeText.cs 的 SHA-256 特征哈希思路一致，但本文件独立实现，
// 避免灰盒/搭建/资产生成模块反向依赖 AI 知识库（也便于这两层在没有远端 Embedding 时离线可用）。
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EpicSpark.Build
{
    /// <summary>
    /// 无第三方依赖的本地文本向量与相似度工具。
    /// 中文按字符一元/二元/三元组切分，英文按单词切分，特征经 SHA-256 哈希落到固定维度，
    /// 因此同一段文本在任何平台、任何一次启动都得到完全相同的向量（可复现）。
    /// </summary>
    public static class LocalEmbedding
    {
        /// <summary>模型库向量维度（Qwen3-Embedding-8B）。</summary>
        public const int DefaultDim = 1024;

        private static readonly Regex CjkRunRegex = new Regex(
            @"[㐀-䶿一-鿿豈-﫿]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex AsciiWordRegex = new Regex(
            @"[a-z0-9][a-z0-9_'+.\-]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex WhitespaceRegex = new Regex(
            @"\s+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ─────────────────────────── 文本规范化 ───────────────────────────

        /// <summary>全角→半角、去零宽字符、小写、折叠空白。</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string normalized = text.Normalize(NormalizationForm.FormKC)
                .Replace("\u200b", string.Empty)
                .Replace("\u200c", string.Empty)
                .Replace("\u200d", string.Empty)
                .Replace("\ufeff", string.Empty)
                .ToLowerInvariant();
            return WhitespaceRegex.Replace(normalized, " ").Trim();
        }

        // ─────────────────────────── 分词 ───────────────────────────

        /// <summary>检索用词元：中文二元组 + 英文单词（长度不足 2 的中文串保留单字）。</summary>
        public static List<string> Tokenize(string text)
        {
            string normalized = Normalize(text);
            var tokens = new List<string>();
            if (normalized.Length == 0) return tokens;

            foreach (Match match in CjkRunRegex.Matches(normalized))
            {
                string run = match.Value;
                if (run.Length == 1)
                {
                    tokens.Add(run);
                    continue;
                }
                for (int i = 0; i < run.Length - 1; i++)
                    tokens.Add(run.Substring(i, 2));
            }
            foreach (Match match in AsciiWordRegex.Matches(normalized))
                tokens.Add(match.Value);
            return tokens;
        }

        /// <summary>去重词元集合，用于关键词重合度粗排（资产生成兜底选型）。</summary>
        public static HashSet<string> TokenSet(string text)
        {
            return new HashSet<string>(Tokenize(text), StringComparer.Ordinal);
        }

        /// <summary>
        /// 关键词重合度，0..1。对短查询很敏感：用于在「离线克隆最接近资产」时排序，
        /// 不参与向量检索（向量检索只比较同一 Embedding 空间里的向量）。
        /// </summary>
        public static float OverlapScore(string left, string right)
        {
            HashSet<string> a = TokenSet(left);
            HashSet<string> b = TokenSet(right);
            if (a.Count == 0 || b.Count == 0) return 0f;
            int overlap = 0;
            foreach (string token in a)
                if (b.Contains(token)) overlap++;
            if (overlap == 0) return 0f;
            float coverageA = overlap / (float)a.Count;
            float coverageB = overlap / (float)b.Count;
            return Clamp01(coverageA * 0.6f + coverageB * 0.4f);
        }

        // ─────────────────────────── 向量 ───────────────────────────

        /// <summary>
        /// 确定性特征哈希向量。权重：中文单字 0.25、二元 1.0、三元 0.5、英文单词 1.0。
        /// 返回长度为 dim 的 L2 归一化向量；空文本返回零向量（调用方应视为不可用）。
        /// </summary>
        public static float[] Embed(string text, int dim)
        {
            if (dim <= 0) dim = DefaultDim;
            var features = new Dictionary<string, float>(StringComparer.Ordinal);
            string normalized = Normalize(text);

            foreach (Match match in CjkRunRegex.Matches(normalized))
            {
                string run = match.Value;
                for (int i = 0; i < run.Length; i++)
                {
                    AddFeature(features, run[i].ToString(), 0.25f);
                    if (i + 1 < run.Length) AddFeature(features, run.Substring(i, 2), 1f);
                    if (i + 2 < run.Length) AddFeature(features, run.Substring(i, 3), 0.5f);
                }
            }
            foreach (Match match in AsciiWordRegex.Matches(normalized))
                AddFeature(features, match.Value, 1f);

            var vector = new float[dim];
            if (features.Count == 0) return vector;

            using (var sha = SHA256.Create())
            {
                foreach (KeyValuePair<string, float> feature in features)
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(feature.Key));
                    uint bucket = (uint)(hash[0] | (hash[1] << 8) | (hash[2] << 16) | (hash[3] << 24));
                    int index = (int)(bucket % (uint)dim);
                    float sign = (hash[4] & 1) == 1 ? 1f : -1f;
                    vector[index] += sign * feature.Value;
                }
            }
            return NormalizeVector(vector);
        }

        /// <summary>用默认维度（1024）计算本地向量。</summary>
        public static float[] Embed(string text)
        {
            return Embed(text, DefaultDim);
        }

        /// <summary>就地 L2 归一化并返回同一数组；零向量原样返回。</summary>
        public static float[] NormalizeVector(float[] vector)
        {
            if (vector == null || vector.Length == 0) return vector;
            double squared = 0;
            for (int i = 0; i < vector.Length; i++) squared += vector[i] * vector[i];
            if (squared <= 1e-12) return vector;
            float inverse = (float)(1.0 / Math.Sqrt(squared));
            for (int i = 0; i < vector.Length; i++) vector[i] *= inverse;
            return vector;
        }

        /// <summary>余弦相似度，结果已收敛到 [-1,1]；任一向量为零向量时返回 0。</summary>
        public static float Cosine(float[] left, float[] right)
        {
            if (left == null || right == null) return 0f;
            int count = Math.Min(left.Length, right.Length);
            if (count == 0) return 0f;
            double dot = 0, leftNorm = 0, rightNorm = 0;
            for (int i = 0; i < count; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }
            if (leftNorm <= 1e-12 || rightNorm <= 1e-12) return 0f;
            double cosine = dot / Math.Sqrt(leftNorm * rightNorm);
            return (float)Math.Max(-1.0, Math.Min(1.0, cosine));
        }

        /// <summary>向量是否可用：非空、维度匹配、且不是零向量。</summary>
        public static bool IsUsableVector(float[] vector, int dim)
        {
            if (vector == null || vector.Length != dim) return false;
            for (int i = 0; i < vector.Length; i++)
                if (Math.Abs(vector[i]) > 1e-12f) return true;
            return false;
        }

        /// <summary>把任意维度向量裁剪/补零到目标维度并归一化（远端模型维度变化时的兜底）。</summary>
        public static float[] FitDimension(float[] source, int dim)
        {
            if (dim <= 0) dim = DefaultDim;
            var result = new float[dim];
            if (source != null)
            {
                int count = Math.Min(source.Length, dim);
                for (int i = 0; i < count; i++) result[i] = source[i];
            }
            return NormalizeVector(result);
        }

        /// <summary>取向量前若干分量做一个稳定的短指纹，仅用于日志排查。</summary>
        public static string Fingerprint(float[] vector)
        {
            if (vector == null) return "null";
            double sum = 0;
            for (int i = 0; i < vector.Length; i++) sum += Math.Abs(vector[i]);
            return vector.Length + "d/" + sum.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void AddFeature(Dictionary<string, float> features, string feature, float weight)
        {
            if (string.IsNullOrEmpty(feature)) return;
            float current;
            features.TryGetValue(feature, out current);
            features[feature] = current + weight;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }
}
