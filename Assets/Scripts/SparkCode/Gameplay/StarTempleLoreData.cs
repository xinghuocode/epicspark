using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SparkCode.Gameplay
{
    [Serializable]
    public class StarTempleLoreEntry
    {
        public string id;
        public string title;
        public string body;
        public string imageFile;
    }

    [Serializable]
    public class StarTempleLoreManifest
    {
        public int version = 1;
        public string note = "Edit this file and add images to the images folder to customize the floating lore cards.";
        public List<StarTempleLoreEntry> entries = new List<StarTempleLoreEntry>();
    }

    /// <summary>
    /// File-backed lore store. Content lives in Application.persistentDataPath so it
    /// survives scene and application restarts. Images are copied into the same folder.
    /// </summary>
    public static class StarTempleLoreStore
    {
        static readonly object Gate = new object();
        static string cachedDirectory;

        public static string DataDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(cachedDirectory))
                    cachedDirectory = Path.Combine(Application.persistentDataPath, "StarTempleLore");
                return cachedDirectory;
            }
        }

        public static string ManifestPath { get { return Path.Combine(DataDirectory, "lore.json"); } }
        public static string ImagesDirectory { get { return Path.Combine(DataDirectory, "images"); } }

        public static StarTempleLoreManifest LoadManifest()
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ImagesDirectory);
                var manifest = ReadManifest();
                if (manifest == null)
                {
                    manifest = new StarTempleLoreManifest();
                    SaveManifest(manifest);
                }
                return manifest;
            }
        }

        static StarTempleLoreManifest ReadManifest()
        {
            if (!File.Exists(ManifestPath)) return null;
            try
            {
                string json = File.ReadAllText(ManifestPath);
                var data = JsonUtility.FromJson<StarTempleLoreManifest>(json);
                if (data == null) return null;
                if (data.entries == null) data.entries = new List<StarTempleLoreEntry>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarTempleLore] Could not read " + ManifestPath + ": " + e.Message);
                return null;
            }
        }

        public static void SaveManifest(StarTempleLoreManifest manifest)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ImagesDirectory);
                if (manifest.entries == null) manifest.entries = new List<StarTempleLoreEntry>();
                string json = JsonUtility.ToJson(manifest, true);
                string temp = ManifestPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
                File.Move(temp, ManifestPath);
            }
        }

        public static StarTempleLoreEntry EnsureEntry(string id, string defaultTitle, string defaultBody)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                var entry = manifest.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new StarTempleLoreEntry
                    {
                        id = id,
                        title = string.IsNullOrEmpty(defaultTitle) ? id : defaultTitle,
                        body = defaultBody ?? string.Empty,
                        imageFile = string.Empty
                    };
                    manifest.entries.Add(entry);
                    SaveManifest(manifest);
                }
                return entry;
            }
        }

        public static bool TryGetEntry(string id, out StarTempleLoreEntry entry)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                entry = manifest.entries.Find(e => e.id == id);
                return entry != null;
            }
        }

        public static void SetContent(string id, string title, string body)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                var entry = manifest.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new StarTempleLoreEntry { id = id };
                    manifest.entries.Add(entry);
                }
                entry.title = title ?? string.Empty;
                entry.body = body ?? string.Empty;
                SaveManifest(manifest);
            }
        }

        public static void SetImageFile(string id, string imageFile)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                var entry = manifest.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new StarTempleLoreEntry { id = id };
                    manifest.entries.Add(entry);
                }
                entry.imageFile = imageFile ?? string.Empty;
                SaveManifest(manifest);
            }
        }

        public static string ImportImage(string id, string sourceFile)
        {
            if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile)) return null;
            string extension = Path.GetExtension(sourceFile);
            if (string.IsNullOrEmpty(extension)) extension = ".png";
            string safeId = Sanitize(id);
            string filename = safeId + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + extension.ToLowerInvariant();
            Directory.CreateDirectory(ImagesDirectory);
            string target = Path.Combine(ImagesDirectory, filename);
            File.Copy(sourceFile, target, true);
            SetImageFile(id, filename);
            return target;
        }

        public static string SavePng(string id, byte[] pngData)
        {
            if (pngData == null || pngData.Length == 0) return null;
            Directory.CreateDirectory(ImagesDirectory);
            string filename = Sanitize(id) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".png";
            string target = Path.Combine(ImagesDirectory, filename);
            File.WriteAllBytes(target, pngData);
            SetImageFile(id, filename);
            return target;
        }

        public static Texture2D LoadImage(StarTempleLoreEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.imageFile)) return null;
            string path = Path.IsPathRooted(entry.imageFile)
                ? entry.imageFile : Path.Combine(ImagesDirectory, entry.imageFile);
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "LoreImage_" + entry.id;
                texture.wrapMode = TextureWrapMode.Clamp;
                return texture;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarTempleLore] Could not load image " + path + ": " + e.Message);
                return null;
            }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "lore";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }
    }
}
