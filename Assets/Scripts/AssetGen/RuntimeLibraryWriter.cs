// 运行时模型库写入器。
// 资产生成 Agent 生成/克隆出的新条目写进 Application.persistentDataPath/EpicSpark/runtime_library.json，
// ModelCatalog 下次 LoadAsync 会通过 ModelCatalog.RuntimeLibraryPath 读到；同时它也支持热注册进内存。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using EpicSpark.Build;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.AssetGen
{
    /// <summary>runtime_library.json 的读写（JsonUtility 实现，无第三方依赖）。</summary>
    public sealed class RuntimeLibraryWriter
    {
        /// <summary>运行时库最多保留多少条；超出后丢弃最老的 gen_ 条目，避免无限膨胀。</summary>
        public const int MaxEntries = 400;

        private readonly string baseDirectory;

        public RuntimeLibraryWriter()
            : this(null)
        {
        }

        /// <summary>baseDirectory 为空时用 Application.persistentDataPath/EpicSpark。</summary>
        public RuntimeLibraryWriter(string baseDirectory)
        {
            this.baseDirectory = string.IsNullOrEmpty(baseDirectory)
                ? Path.Combine(Application.persistentDataPath, "EpicSpark")
                : baseDirectory;
        }

        public string DirectoryPath
        {
            get { return baseDirectory; }
        }

        /// <summary>运行时模型库 JSON 路径（与 ModelCatalog.RuntimeLibraryPath 指向同一文件）。</summary>
        public string LibraryPath
        {
            get { return Path.Combine(baseDirectory, ModelCatalog.RuntimeLibraryFileName); }
        }

        /// <summary>生成的 glb / png 落盘目录。</summary>
        public string GeneratedDirectory
        {
            get { return Path.Combine(baseDirectory, "generated"); }
        }

        public int Count
        {
            get
            {
                ModelLibrary library = Load();
                return library != null && library.entries != null ? library.entries.Count : 0;
            }
        }

        // ─────────────────────────── 读写 ───────────────────────────

        /// <summary>读取运行时库；文件不存在/损坏时返回空库（不抛异常）。</summary>
        public ModelLibrary Load()
        {
            try
            {
                if (!File.Exists(LibraryPath)) return EmptyLibrary();
                string json = File.ReadAllText(LibraryPath, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return EmptyLibrary();
                ModelLibrary library = JsonUtility.FromJson<ModelLibrary>(json);
                if (library == null) return EmptyLibrary();
                if (library.entries == null) library.entries = new List<ModelEntry>();
                return library;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 读取运行时模型库失败: " + e.Message);
                return EmptyLibrary();
            }
        }

        /// <summary>追加/替换一条条目并立刻落盘。返回是否成功。</summary>
        public bool Append(ModelEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.id)) return false;
            try
            {
                ModelLibrary library = Load();
                if (library.entries == null) library.entries = new List<ModelEntry>();

                int existing = -1;
                for (int i = 0; i < library.entries.Count; i++)
                {
                    if (library.entries[i] != null && library.entries[i].id == entry.id)
                    {
                        existing = i;
                        break;
                    }
                }
                if (existing >= 0) library.entries[existing] = entry;
                else library.entries.Add(entry);

                Prune(library);
                UpdateMeta(library);
                EnsureDirectories();
                File.WriteAllText(LibraryPath, JsonUtility.ToJson(library, true), Encoding.UTF8);
                Debug.Log("[资产生成] 运行时模型库已写入 " + library.entries.Count + " 条: " + LibraryPath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 写入运行时模型库失败（不影响本次使用）: " + e.Message);
                return false;
            }
        }

        /// <summary>写盘 + 热注册进 ModelCatalog（若传入的是本项目实现）。</summary>
        public void Register(ModelCatalog catalog, ModelEntry entry)
        {
            Append(entry);
            if (catalog != null) catalog.RegisterRuntimeEntry(entry);
        }

        /// <summary>把新条目的 glb 文件名规范成 generated/&lt;id&gt;.glb 的绝对路径。</summary>
        public string NewAssetPath(string id)
        {
            string safeId = string.IsNullOrEmpty(id) ? "gen_asset" : id;
            return Path.Combine(GeneratedDirectory, safeId + ".glb");
        }

        private static ModelLibrary EmptyLibrary()
        {
            var library = new ModelLibrary { entries = new List<ModelEntry>() };
            library.meta = new ModelLibraryMeta
            {
                dim = ModelCatalog.VectorDim,
                embed_model = "Qwen3-Embedding-8B",
                count = 0,
                normalized = true,
                updated_at = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture),
            };
            return library;
        }

        private static void Prune(ModelLibrary library)
        {
            if (library.entries == null || library.entries.Count <= MaxEntries) return;
            // 从最老的 gen_ 条目开始丢，保留非 gen_（外部导入）条目。
            int overflow = library.entries.Count - MaxEntries;
            for (int i = 0; i < library.entries.Count && overflow > 0; i++)
            {
                ModelEntry item = library.entries[i];
                if (item == null || string.IsNullOrEmpty(item.id)) continue;
                if (item.id.StartsWith("gen_", StringComparison.Ordinal))
                {
                    library.entries.RemoveAt(i);
                    i--;
                    overflow--;
                }
            }
            while (library.entries.Count > MaxEntries) library.entries.RemoveAt(0);
        }

        private static void UpdateMeta(ModelLibrary library)
        {
            if (library.meta == null) library.meta = new ModelLibraryMeta();
            library.meta.dim = ModelCatalog.VectorDim;
            library.meta.embed_model = "Qwen3-Embedding-8B";
            library.meta.count = library.entries != null ? library.entries.Count : 0;
            library.meta.normalized = true;
            library.meta.updated_at = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture);
        }

        private void EnsureDirectories()
        {
            try
            {
                if (!Directory.Exists(baseDirectory)) Directory.CreateDirectory(baseDirectory);
                if (!Directory.Exists(GeneratedDirectory)) Directory.CreateDirectory(GeneratedDirectory);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 创建运行时目录失败: " + e.Message);
            }
        }
    }
}
