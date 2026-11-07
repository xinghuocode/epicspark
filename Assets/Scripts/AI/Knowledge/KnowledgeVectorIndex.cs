using System;
using System.Collections.Generic;
using System.IO;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// Unity 内的扁平 float32 向量索引。千级文本块使用精确余弦检索，
    /// 不需要原生 ANN 插件；索引保存在 Application.persistentDataPath。
    /// </summary>
    internal sealed class KnowledgeVectorIndex
    {
        private const string Magic = "ESKB";
        private const int Version = 2;

        public string Fingerprint { get; private set; }
        public string EmbedKind { get; private set; }
        public string EmbedModel { get; private set; }
        public int Dim { get; private set; }
        public List<float[]> Vectors { get; private set; }

        public KnowledgeVectorIndex(
            string fingerprint,
            string embedKind,
            string embedModel,
            int dim,
            List<float[]> vectors)
        {
            Fingerprint = fingerprint ?? string.Empty;
            EmbedKind = embedKind ?? string.Empty;
            EmbedModel = embedModel ?? string.Empty;
            Dim = dim;
            Vectors = vectors ?? new List<float[]>();
        }

        public bool IsCompatible(string fingerprint, string embedKind, string embedModel, int dim)
        {
            // 远端模型首次响应前 dim=0，此时只校验语料指纹和模型身份；维度由索引记录。
            return (dim <= 0 || Dim == dim)
                && string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(EmbedKind, embedKind, StringComparison.Ordinal)
                && string.Equals(EmbedModel, embedModel, StringComparison.Ordinal);
        }

        public List<KnowledgeScoredPosition> Search(float[] queryVector, int limit)
        {
            if (queryVector == null || queryVector.Length != Dim)
                throw new InvalidOperationException("查询向量维度与索引不一致");
            queryVector = KnowledgeText.NormalizeVector((float[])queryVector.Clone());
            var results = new List<KnowledgeScoredPosition>(Vectors.Count);
            for (int row = 0; row < Vectors.Count; row++)
            {
                float[] vector = Vectors[row];
                float score = 0f;
                for (int column = 0; column < Dim; column++)
                    score += vector[column] * queryVector[column];
                results.Add(new KnowledgeScoredPosition(row, score));
            }
            results.Sort((left, right) => right.score.CompareTo(left.score));
            int count = Math.Max(1, Math.Min(limit, results.Count));
            if (results.Count > count) results.RemoveRange(count, results.Count - count);
            return results;
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(Fingerprint);
                writer.Write(EmbedKind);
                writer.Write(EmbedModel);
                writer.Write(Dim);
                writer.Write(Vectors.Count);
                foreach (float[] vector in Vectors)
                {
                    if (vector.Length != Dim) throw new InvalidDataException("向量维度不一致");
                    for (int i = 0; i < Dim; i++) writer.Write(vector[i]);
                }
                writer.Flush();
            }
            File.Copy(temporary, path, true);
            File.Delete(temporary);
        }

        public static KnowledgeVectorIndex Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadString() != Magic) return null;
                    int version = reader.ReadInt32();
                    if (version != Version) return null;
                    string fingerprint = reader.ReadString();
                    string kind = reader.ReadString();
                    string model = reader.ReadString();
                    int dim = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    if (dim <= 0 || count < 0) return null;
                    long expectedBytes = stream.Position + (long)dim * count * sizeof(float);
                    if (expectedBytes != stream.Length) return null;

                    var vectors = new List<float[]>(count);
                    for (int row = 0; row < count; row++)
                    {
                        var vector = new float[dim];
                        for (int column = 0; column < dim; column++) vector[column] = reader.ReadSingle();
                        vectors.Add(vector);
                    }
                    return new KnowledgeVectorIndex(fingerprint, kind, model, dim, vectors);
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException || exception is EndOfStreamException)
            {
                return null;
            }
        }
    }
}
