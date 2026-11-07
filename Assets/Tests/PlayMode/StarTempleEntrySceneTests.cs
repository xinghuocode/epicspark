using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace EpicSpark.MovementTest.Tests
{
    public sealed class StarTempleEntrySceneTests
    {
        private const string EntryScenePath = "Assets/Scenes/BlenderStarTemple_Gameplay.unity";

#if UNITY_EDITOR
        [Test]
        public void GameplayScene_IsTheOnlyConfiguredEntry()
        {
            var scenes = UnityEditor.EditorBuildSettings.scenes;
            Assert.That(scenes.Length, Is.EqualTo(1), "发布构建列表只能保留玩法入口场景");
            Assert.That(scenes[0].path, Is.EqualTo(EntryScenePath));
            Assert.That(scenes[0].enabled, Is.True, "玩法入口场景必须启用且位于索引 0");

            var playModeStartScene = UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene;
            Assert.That(playModeStartScene, Is.Not.Null, "编辑器 Play Mode 也必须指定入口场景");
            Assert.That(UnityEditor.AssetDatabase.GetAssetPath(playModeStartScene), Is.EqualTo(EntryScenePath));
        }
#endif

        [UnityTest]
        public IEnumerator ConfiguredEntryScene_LoadsPlayableTemple()
        {
#if UNITY_EDITOR
            var scenes = UnityEditor.EditorBuildSettings.scenes;
            Assert.That(scenes.Length, Is.GreaterThan(0));
            Assert.That(scenes[0].path, Is.EqualTo(EntryScenePath));
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                scenes[0].path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            AsyncOperation load = SceneManager.LoadSceneAsync(0, LoadSceneMode.Single);
#endif
            Assert.That(load, Is.Not.Null, "入口场景必须能够加载");
            yield return load;
            yield return null;

            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(EntryScenePath));
            var player = GameObject.Find("StarTemple_Player");
            Assert.That(player, Is.Not.Null, "入口场景应直接生成玩法角色");
            Assert.That(player.GetComponent<CharacterController>(), Is.Not.Null);
            Assert.That(player.GetComponent("StarTemplePlayerController"), Is.Not.Null);
            Assert.That(Camera.main, Is.Not.Null, "入口场景应有可用的主相机");
            Assert.That(GameObject.Find("Spark Pivot"), Is.Not.Null, "入口场景应包含星火交互对象");
            Assert.That(GameObject.Find("Temple_Exit_Obelisk"), Is.Not.Null, "入口场景应包含退出交互对象");
        }
    }
}
