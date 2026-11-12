// 星火神殿开始场景（BlenderStarTemple_Gameplay 移植）→ 历史事件输入 → EpicSpark 主场景
// 的端到端 Play Mode 测试（仅编辑器）。用 batchmode 运行：
//   Unity.exe -batchmode -projectPath <proj> -executeMethod EpicSpark.EditorTools.StarTempleStartPlayTest.Run -logFile <log>
// 测试会临时把离线开关打开（避免测试时走网络），结束时恢复现场并以退出码 0/1 结束进程。
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EpicSpark.EditorTools
{
    public static class StarTempleStartPlayTest
    {
        private const string ScenePath = "Assets/Scenes/StarTempleStart.unity";
        private const string TestEvent = "攻占巴士底狱";

        private static int _phase;
        private static float _t0;
        private static float _runStart;
        private static bool _offlineWasSet;
        private static int _offlinePrev;
        private static bool _toleranceWasSet;
        private static float _tolerancePrev;

        [MenuItem("EpicSpark/运行星火神殿开始场景端到端测试")]
        public static void Run()
        {
            PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.GameBootstrap.BootEventKey);
            PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.GameBootstrap.BootResumeKey);
            _toleranceWasSet = PlayerPrefs.HasKey(EpicSpark.Bootstrap.GameBootstrap.BootToleranceKey);
            _tolerancePrev = PlayerPrefs.GetFloat(EpicSpark.Bootstrap.GameBootstrap.BootToleranceKey, 0.5f);

            _offlinePrev = PlayerPrefs.GetInt(EpicSpark.Bootstrap.ApiConfig.PrefOffline, 0);
            PlayerPrefs.SetInt(EpicSpark.Bootstrap.ApiConfig.PrefOffline, 1);
            PlayerPrefs.Save();
            _offlineWasSet = true;

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            _phase = 0;
            _runStart = 0f;
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
            Debug.Log("[星火神殿测试] 进入 Play Mode（离线强制开启）");
        }

        private static void Tick()
        {
            try
            {
                if (!EditorApplication.isPlaying) return;
                float now = Time.realtimeSinceStartup;
                if (_runStart <= 0f) _runStart = now;

                var bridge = UnityEngine.Object.FindObjectOfType<EpicSpark.Bootstrap.StarTempleStartBridge>();
                var worldUi = UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>();
                var player = UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTemplePlayerController>();

                if (_phase == 0)
                {
                    if (bridge == null || worldUi == null) return;
                    if (now - _runStart < 1.5f) return;
                    Debug.Log("[星火神殿测试] 桥接已挂载，世界菜单激活="
                              + (worldUi.bridgeCanvas != null && worldUi.bridgeCanvas.activeSelf));
                    TriggerAction(worldUi, SparkCode.Gameplay.StarTempleUiAction.Settings);
                    _phase = 1;
                    _t0 = now;
                    return;
                }

                if (_phase == 1)
                {
                    if (now - _t0 < 0.6f) return;
                    bool settingsActive = worldUi != null && worldUi.settingsPage != null
                                          && worldUi.settingsPage.activeSelf;
                    Debug.Log("[星火神殿测试] 设置页激活=" + settingsActive);
                    CaptureWorld("Logs/startemple-settings.png");
                    TriggerAction(worldUi, SparkCode.Gameplay.StarTempleUiAction.OpenApi);
                    _phase = 2;
                    _t0 = now;
                    return;
                }

                if (_phase == 2)
                {
                    if (now - _t0 < 0.6f) return;
                    var api = UnityEngine.Object.FindObjectOfType<EpicSpark.Bootstrap.ApiSettingsScreen>();
                    bool visible = api != null && api.IsVisible;
                    bool frozen = player != null && player.IsModalFrozen;
                    Debug.Log("[星火神殿测试] API 设置面板 存在=" + (api != null) + " 显示=" + visible
                              + " 玩家冻结=" + frozen);
                    if (api != null) api.Hide();
                    _phase = 3;
                    _t0 = now;
                    return;
                }

                if (_phase == 3)
                {
                    if (now - _t0 < 0.5f) return;
                    bool frozen = player != null && player.IsModalFrozen;
                    bool suspended = worldUi != null && worldUi.SuspendInteraction;
                    Debug.Log("[星火神殿测试] 关闭 API 设置后 玩家冻结=" + frozen + " 世界UI挂起=" + suspended);
                    if (frozen || suspended)
                    {
                        Debug.LogError("[星火神殿测试] 关闭 API 设置后没有恢复玩家/世界 UI");
                        Finish(1);
                        return;
                    }
                    TriggerAction(worldUi, SparkCode.Gameplay.StarTempleUiAction.EnterEpic);
                    _phase = 4;
                    _t0 = now;
                    return;
                }

                if (_phase == 4)
                {
                    if (now - _t0 < 0.5f) return;
                    bool epicActive = worldUi != null && worldUi.epicPage != null && worldUi.epicPage.activeSelf;
                    Debug.Log("[星火神殿测试] 进入史诗页激活=" + epicActive);
                    CaptureWorld("Logs/startemple-epic.png");
                    TriggerAction(worldUi, SparkCode.Gameplay.StarTempleUiAction.CreateEpic);
                    _phase = 5;
                    _t0 = now;
                    return;
                }

                if (_phase == 5)
                {
                    if (now - _t0 < 0.8f) return;
                    bool open = bridge != null && bridge.IsCreatePageOpen;
                    bool frozen = player != null && player.IsModalFrozen;
                    bool suspended = worldUi != null && worldUi.SuspendInteraction;
                    Debug.Log("[星火神殿测试] 新建史诗页 打开=" + open + " 玩家冻结=" + frozen
                              + " 世界UI挂起=" + suspended);
                    if (open) CaptureWorld("Logs/startemple-create.png");
                    if (bridge != null)
                    {
                        bridge.SetToleranceForTest(0.8f);
                        Debug.Log("[星火神殿测试] 设置容忍度=0.8（写入 PlayerPrefs）");
                    }
                    if (bridge != null) bridge.SubmitEvent(TestEvent);
                    _phase = 6;
                    _t0 = now;
                    return;
                }

                if (_phase == 6)
                {
                    Scene scene = SceneManager.GetActiveScene();
                    bool loaded = scene.name == "EpicSpark";
                    bool consumed = !PlayerPrefs.HasKey(EpicSpark.Bootstrap.GameBootstrap.BootEventKey);
                    if (loaded && consumed)
                    {
                        Debug.Log("[星火神殿测试] 已进入主场景，启动参数已消费");
                        _phase = 7;
                        _t0 = now;
                        return;
                    }
                    if (now - _t0 > 30f)
                    {
                        Debug.LogError("[星火神殿测试] 超时：场景=" + scene.name + " bootKeyLeft="
                                       + PlayerPrefs.GetString(EpicSpark.Bootstrap.GameBootstrap.BootEventKey, ""));
                        Finish(1);
                    }
                    return;
                }

                if (_phase == 7 && now - _t0 > 3f)
                {
                    var bootstrap = UnityEngine.Object.FindObjectOfType<EpicSpark.Bootstrap.GameBootstrap>();
                    float tolerance = bootstrap != null ? bootstrap.initialTolerance : -1f;
                    float saved = PlayerPrefs.GetFloat(EpicSpark.Bootstrap.GameBootstrap.BootToleranceKey, -1f);
                    Debug.Log("[星火神殿测试] 主场景 initialTolerance=" + tolerance + " PlayerPrefs=" + saved);
                    if (bootstrap != null && Mathf.Abs(tolerance - 0.8f) > 0.01f)
                    {
                        Debug.LogError("[星火神殿测试] 容忍度没有带到主场景");
                        Finish(1);
                        return;
                    }
                    Debug.Log("[星火神殿测试] 通过：星火神殿开始场景 → 主场景自动开局");
                    Finish(0);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[星火神殿测试] 异常: " + e);
                Finish(1);
            }
        }

        private static void TriggerAction(SparkCode.Gameplay.StarTempleWorldUiController worldUi,
            SparkCode.Gameplay.StarTempleUiAction action)
        {
            // 直接走真实输入路径：模拟点击世界菜单按钮。
            MethodInfo handle = typeof(SparkCode.Gameplay.StarTempleWorldUiController)
                .GetMethod("HandleAction", BindingFlags.Instance | BindingFlags.NonPublic);
            if (handle == null)
            {
                Debug.LogError("[星火神殿测试] 找不到 HandleAction");
                Finish(1);
                return;
            }
            handle.Invoke(worldUi, new object[] { action });
        }

        private static void CaptureWorld(string path)
        {
            var player = UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTemplePlayerController>();
            Camera cam = player != null && player.playerCamera != null
                ? player.playerCamera
                : UnityEngine.Object.FindObjectOfType<Camera>();
            if (cam == null)
            {
                Debug.LogWarning("[星火神殿测试] 无法截图：缺少玩家相机");
                return;
            }

            const int w = 1600;
            const int h = 1200;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = prev;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(tex);
            Debug.Log("[星火神殿测试] 已保存 " + path);
        }

        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            Debug.Log("[星火神殿测试] 结束，退出码=" + code + "，离线开关恢复=" + _offlineWasSet);
            if (_offlineWasSet)
            {
                if (_offlinePrev == 1) PlayerPrefs.SetInt(EpicSpark.Bootstrap.ApiConfig.PrefOffline, 1);
                else PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.ApiConfig.PrefOffline);
            }
            if (_toleranceWasSet)
                PlayerPrefs.SetFloat(EpicSpark.Bootstrap.GameBootstrap.BootToleranceKey, _tolerancePrev);
            else
                PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.GameBootstrap.BootToleranceKey);
            PlayerPrefs.Save();
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
            EditorApplication.isPlaying = false;
            EditorApplication.Exit(code);
        }
    }
}
