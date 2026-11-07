// 存档：承载当前剧情进度、玩家状态、行为日志与已生成场景（供场景复用）。
using System;
using System.IO;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Bootstrap
{
    public static class SaveService
    {
        public static string SaveDirectory
        {
            get { return Path.Combine(Application.persistentDataPath, "EpicSpark", "saves"); }
        }

        public static string SavePath
        {
            get { return Path.Combine(SaveDirectory, "session.json"); }
        }

        public static bool HasSave
        {
            get { return File.Exists(SavePath); }
        }

        public static bool Save(GameSaveData data)
        {
            if (data == null) return false;
            try
            {
                Directory.CreateDirectory(SaveDirectory);
                string json = JsonUtility.ToJson(data, true);
                string temp = SavePath + ".tmp";
                File.WriteAllText(temp, json, System.Text.Encoding.UTF8);
                if (File.Exists(SavePath)) File.Delete(SavePath);
                File.Move(temp, SavePath);
                Debug.Log("[存档] 已写入 " + SavePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[存档] 写入失败: " + e.Message);
                return false;
            }
        }

        public static GameSaveData Load()
        {
            try
            {
                if (!File.Exists(SavePath)) return null;
                string json = File.ReadAllText(SavePath, System.Text.Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return null;
                GameSaveData data = JsonUtility.FromJson<GameSaveData>(json);
                Debug.Log("[存档] 已读取 " + (data != null ? data.userEvent : "null"));
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[存档] 读取失败: " + e.Message);
                return null;
            }
        }

        public static void Delete()
        {
            try { if (File.Exists(SavePath)) File.Delete(SavePath); }
            catch (Exception e) { Debug.LogWarning("[存档] 删除失败: " + e.Message); }
        }

        /// <summary>打开存档所在目录（编辑器与桌面端有用）。</summary>
        public static void OpenSaveFolder()
        {
            try
            {
                Directory.CreateDirectory(SaveDirectory);
                Application.OpenURL("file:///" + SaveDirectory.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[存档] 无法打开目录: " + e.Message);
            }
        }
    }
}
