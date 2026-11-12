using System.IO;
using UnityEngine;

namespace SparkCode.Gameplay
{
    /// <summary>Marks one large floating sparkle as a lore card target.</summary>
    public class StarTempleLorePoint : MonoBehaviour
    {
        public string pointId = "lore_01";
        public string defaultTitle = "星火记忆";
        [TextArea(3, 9)] public string defaultBody = "这段记忆尚未书写。";
        public Texture2D defaultImage;

        public string Title { get; private set; }
        public string Body { get; private set; }
        public Texture2D CurrentImage { get; private set; }
        public string PersistentImagePath { get; private set; }

        Texture2D loadedImage;
        StarTempleLoreEntry entry;

        void Awake()
        {
            ReloadFromFile();
        }

        void OnDestroy()
        {
            if (loadedImage != null) Destroy(loadedImage);
        }

        public void SetContent(string title, string body)
        {
            StarTempleLoreStore.SetContent(pointId, title, body);
            ReloadFromFile();
        }

        public void SetImage(Texture2D image)
        {
            if (image == null)
            {
                StarTempleLoreStore.SetImageFile(pointId, string.Empty);
            }
            else
            {
                StarTempleLoreStore.SavePng(pointId, image.EncodeToPNG());
            }
            ReloadFromFile();
        }

        public bool SetImageFromFile(string sourcePath)
        {
            string target = StarTempleLoreStore.ImportImage(pointId, sourcePath);
            if (string.IsNullOrEmpty(target)) return false;
            ReloadFromFile();
            return true;
        }

        public void SaveToFile()
        {
            StarTempleLoreStore.SetContent(pointId, Title, Body);
        }

        public void ReloadFromFile()
        {
            entry = StarTempleLoreStore.EnsureEntry(pointId, defaultTitle, defaultBody);
            Title = string.IsNullOrEmpty(entry.title) ? defaultTitle : entry.title;
            Body = string.IsNullOrEmpty(entry.body) ? defaultBody : entry.body;

            var next = StarTempleLoreStore.LoadImage(entry);
            if (loadedImage != null && loadedImage != next)
            {
                if (Application.isPlaying) Destroy(loadedImage);
                else DestroyImmediate(loadedImage);
                loadedImage = null;
            }
            if (next != null) loadedImage = next;
            CurrentImage = next != null ? next : defaultImage;
            PersistentImagePath = next != null
                ? Path.Combine(StarTempleLoreStore.ImagesDirectory, entry.imageFile) : string.Empty;
        }

        /// <summary>Used by the editor scene builder to create the initial persistent file.</summary>
        public void EditorInitializePersistence()
        {
            StarTempleLoreStore.EnsureEntry(pointId, defaultTitle, defaultBody);
            ReloadFromFile();
        }

        public StarTempleLoreEntry GetEntry()
        {
            return entry;
        }
    }
}
