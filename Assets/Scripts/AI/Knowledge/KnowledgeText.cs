using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// 中文检索文本工具。中文采用字符二元组，避免引入词典和原生插件；
    /// 本地向量采用 SHA-256 特征哈希，保证 Unity 各平台、各次启动结果一致。
    /// </summary>
    internal static class KnowledgeText
    {
        private static readonly Regex CjkRunRegex = new Regex(
            @"[㐀-䶿一-鿿豈-﫿]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex AsciiWordRegex = new Regex(
            @"[a-z0-9][a-z0-9_'+.\-]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex WhitespaceRegex = new Regex(
            @"\s+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex FillerRegex = new Regex(
            "[的了吗呢吧啊呀是在把被和与及之]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        public static string StripFillers(string text)
        {
            string normalized = Normalize(text);
            string stripped = FillerRegex.Replace(normalized, string.Empty).Trim();
            return string.IsNullOrEmpty(stripped) ? normalized : stripped;
        }

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

        public static HashSet<string> TokenSet(string text)
        {
            return new HashSet<string>(Tokenize(text), StringComparer.Ordinal);
        }

        public static float Coverage(HashSet<string> queryTokens, HashSet<string> documentTokens)
        {
            if (queryTokens == null || queryTokens.Count == 0) return 0f;
            int overlap = 0;
            foreach (string token in queryTokens)
                if (documentTokens.Contains(token)) overlap++;
            return overlap / (float)queryTokens.Count;
        }

        public static float Jaccard(HashSet<string> left, HashSet<string> right)
        {
            if (left.Count == 0 || right.Count == 0) return 0f;
            int overlap = 0;
            foreach (string token in left)
                if (right.Contains(token)) overlap++;
            int union = left.Count + right.Count - overlap;
            return union == 0 ? 0f : overlap / (float)union;
        }

        public static float Similarity(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;
            int max = Math.Max(left.Length, right.Length);
            if (max == 0) return 1f;
            return 1f - Levenshtein(left, right) / (float)max;
        }

        private static int Levenshtein(string left, string right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++) previous[j] = j;
            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= right.Length; j++)
                {
                    int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[right.Length];
        }

        public static string FormatYear(int year)
        {
            return year < 0 ? "前" + (-year) + "年" : year + "年";
        }

        public static float[] NormalizeVector(float[] vector)
        {
            double squared = 0;
            for (int i = 0; i < vector.Length; i++) squared += vector[i] * vector[i];
            if (squared <= 0) return vector;
            float inverse = (float)(1.0 / Math.Sqrt(squared));
            for (int i = 0; i < vector.Length; i++) vector[i] *= inverse;
            return vector;
        }

        /// <summary>确定性特征哈希向量：单字 0.3、二元 1.0、三元 0.6、英文词 1.0。</summary>
        public static float[] HashingEmbed(string text, int dim)
        {
            if (dim <= 0) throw new ArgumentOutOfRangeException(nameof(dim));
            var features = new Dictionary<string, float>(StringComparer.Ordinal);
            string normalized = Normalize(text);

            foreach (Match match in CjkRunRegex.Matches(normalized))
            {
                string run = match.Value;
                for (int i = 0; i < run.Length; i++)
                {
                    AddFeature(features, run[i].ToString(), 0.3f);
                    if (i + 1 < run.Length) AddFeature(features, run.Substring(i, 2), 1f);
                    if (i + 2 < run.Length) AddFeature(features, run.Substring(i, 3), 0.6f);
                }
            }
            foreach (Match match in AsciiWordRegex.Matches(normalized))
                AddFeature(features, match.Value, 1f);

            var vector = new float[dim];
            using (var sha = SHA256.Create())
            {
                foreach (KeyValuePair<string, float> feature in features)
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(feature.Key));
                    uint bucket = (uint)(hash[0] | (hash[1] << 8) | (hash[2] << 16) | (hash[3] << 24));
                    int index = (int)(bucket % (uint)dim);
                    float sign = (hash[4] & 1) == 1 ? 1f : -1f;
                    vector[index] += sign * (1f + (float)Math.Log(feature.Value));
                }
            }
            return NormalizeVector(vector);
        }

        private static void AddFeature(Dictionary<string, float> features, string feature, float weight)
        {
            features.TryGetValue(feature, out float current);
            features[feature] = current + weight;
        }
    }

    /// <summary>内存 BM25（Okapi），无第三方依赖。</summary>
    internal sealed class KnowledgeBm25Index
    {
        private sealed class Posting
        {
            public int document;
            public int termFrequency;
        }

        private readonly float k1;
        private readonly float b;
        private readonly Dictionary<string, List<Posting>> postings = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> idf = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<string> documentIds = new List<string>();
        private readonly List<int> documentLengths = new List<int>();
        private float averageLength;
        private bool finalized;

        public KnowledgeBm25Index(float k1 = 1.2f, float b = 0.75f)
        {
            this.k1 = k1;
            this.b = b;
        }

        public int Count => documentIds.Count;

        public void Add(string documentId, string text)
        {
            int document = documentIds.Count;
            documentIds.Add(documentId);
            List<string> tokens = KnowledgeText.Tokenize(text);
            documentLengths.Add(tokens.Count);
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string token in tokens)
            {
                frequencies.TryGetValue(token, out int frequency);
                frequencies[token] = frequency + 1;
            }
            foreach (KeyValuePair<string, int> pair in frequencies)
            {
                if (!postings.TryGetValue(pair.Key, out List<Posting> list))
                {
                    list = new List<Posting>();
                    postings[pair.Key] = list;
                }
                list.Add(new Posting { document = document, termFrequency = pair.Value });
            }
            finalized = false;
        }

        public void FinalizeIndex()
        {
            idf.Clear();
            int count = documentIds.Count;
            if (count == 0)
            {
                averageLength = 0f;
                finalized = true;
                return;
            }
            int total = 0;
            foreach (int length in documentLengths) total += length;
            averageLength = total / (float)count;
            foreach (KeyValuePair<string, List<Posting>> pair in postings)
            {
                int df = pair.Value.Count;
                idf[pair.Key] = (float)Math.Log(1.0 + (count - df + 0.5) / (df + 0.5));
            }
            finalized = true;
        }

        public List<KnowledgeScoredId> Search(string query, int limit)
        {
            if (!finalized) FinalizeIndex();
            var results = new List<KnowledgeScoredId>();
            if (documentIds.Count == 0) return results;

            var queryTerms = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string token in KnowledgeText.Tokenize(query))
            {
                queryTerms.TryGetValue(token, out int frequency);
                queryTerms[token] = frequency + 1;
            }
            if (queryTerms.Count == 0) return results;

            var scores = new float[documentIds.Count];
            float average = averageLength <= 0 ? 1f : averageLength;
            foreach (KeyValuePair<string, int> term in queryTerms)
            {
                if (!postings.TryGetValue(term.Key, out List<Posting> list)) continue;
                float termIdf = idf.TryGetValue(term.Key, out float value) ? value : 0f;
                foreach (Posting posting in list)
                {
                    float tf = posting.termFrequency;
                    float length = Math.Max(1, documentLengths[posting.document]);
                    float denominator = tf + k1 * (1f - b + b * length / average);
                    scores[posting.document] += term.Value * termIdf * (tf * (k1 + 1f)) / Math.Max(0.0001f, denominator);
                }
            }
            for (int i = 0; i < scores.Length; i++)
                if (scores[i] > 0) results.Add(new KnowledgeScoredId(documentIds[i], scores[i]));
            results.Sort((left, right) => right.score.CompareTo(left.score));
            if (results.Count > limit) results.RemoveRange(limit, results.Count - limit);
            return results;
        }
    }
}
