// 运行时小工具：当前场景标识、玩家引用缓存、灰盒平面坐标 → 世界坐标换算。
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Player;

namespace EpicSpark.Tools
{
    /// <summary>运行时上下文辅助（可选接线：Lead 的 GameDirector 可写入 SceneId / SceneName）。</summary>
    public static class RuntimeContext
    {
        /// <summary>当前场景 id；为空时回退到 Services.Save.currentSceneId。</summary>
        public static string SceneId = "";

        /// <summary>当前场景显示名；为空时回退到 SceneId。</summary>
        public static string SceneName = "";

        private static EpicSparkPlayerController _player;

        /// <summary>玩家控制器缓存（场景切换后调用 ResetCache）。</summary>
        public static EpicSparkPlayerController Player
        {
            get
            {
                if (_player == null)
                    _player = UnityEngine.Object.FindObjectOfType<EpicSparkPlayerController>();
                return _player;
            }
        }

        public static void SetScene(string sceneId, string sceneName)
        {
            SceneId = sceneId ?? "";
            SceneName = sceneName ?? "";
        }

        public static string ResolveSceneId()
        {
            if (!string.IsNullOrEmpty(SceneId)) return SceneId;
            GameSaveData save = Services.Save;
            if (save != null && !string.IsNullOrEmpty(save.currentSceneId)) return save.currentSceneId;
            return "scene";
        }

        public static string ResolveSceneName()
        {
            if (!string.IsNullOrEmpty(SceneName)) return SceneName;
            if (!string.IsNullOrEmpty(SceneId)) return SceneId;
            GameSaveData save = Services.Save;
            if (save != null && !string.IsNullOrEmpty(save.currentSceneId)) return save.currentSceneId;
            return "未命名场景";
        }

        /// <summary>灰盒平面坐标 (x, y) → 世界坐标 (x, y, z)：约定 y 轴落入世界 z。</summary>
        public static Vector3 PlanToWorld(Vector2 plan, float worldY)
        {
            return new Vector3(plan.x, worldY, plan.y);
        }

        public static void ResetCache()
        {
            _player = null;
        }
    }
}
