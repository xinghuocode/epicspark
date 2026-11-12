// 神殿开始场景 → 历史事件输入 → EpicSpark 主场景的端到端 Play Mode 测试（仅编辑器）。
// 用 batchmode 运行：
//   Unity.exe -batchmode -projectPath <proj> -executeMethod EpicSpark.EditorTools.TempleStartPlayTest.Run -logFile <log>
// 测试会临时把离线开关打开（避免测试时走网络），结束时恢复现场并以退出码 0/1 结束进程。
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EpicSpark.EditorTools
{
    public static class TempleStartPlayTest
    {
        private const string ScenePath = "Assets/Scenes/StartScene.unity";
        private const string TestEvent = "攻占巴士底狱";

        private static int _phase;
        private static float _t0;
        private static float _runStart;
        private static bool _offlineWasSet;
        private static int _offlinePrev;

        [MenuItem("EpicSpark/运行神殿开始场景端到端测试")]
        public static void Run()
        {
            PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.GameBootstrap.BootEventKey);
            PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.GameBootstrap.BootResumeKey);

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
            Debug.Log("[神殿测试] 进入 Play Mode（离线强制开启）");
        }

        private static void Tick()
        {
            try
            {
                if (!EditorApplication.isPlaying) return;
                float now = Time.realtimeSinceStartup;
                if (_runStart <= 0f) _runStart = now;

                var bridge = UnityEngine.Object.FindObjectOfType<EpicSpark.Bootstrap.StartSceneBridge>();
                var menu = UnityEngine.Object.FindObjectOfType<SparkCode.StartMenuController>();

                if (_phase == 0)
                {
                    if (bridge == null) return;
                    if (now - _runStart < 1.0f) return;
                    Debug.Log("[神殿测试] 桥接已挂载，触发「开始游戏」");
                    if (menu != null) menu.StartGame();
                    _phase = 1;
                    _t0 = now;
                    return;
                }

                if (_phase == 1)
                {
                    if (now - _t0 < 2.0f) return;
                    GameObject canvasGo = GameObject.Find("TempleStartCanvas");
                    bool exists = canvasGo != null;
                    bool active = exists && canvasGo.activeSelf;
                    Debug.Log("[神殿测试] 输入面板 存在=" + exists + " 激活=" + active);
                    if (exists) CaptureOverlay(canvasGo);
                    if (bridge != null) bridge.SubmitEvent(TestEvent);
                    _phase = 2;
                    _t0 = now;
                    return;
                }

                if (_phase == 2)
                {
                    Scene scene = SceneManager.GetActiveScene();
                    bool loaded = scene.name == "EpicSpark";
                    bool consumed = !PlayerPrefs.HasKey(EpicSpark.Bootstrap.GameBootstrap.BootEventKey);
                    if (loaded && consumed)
                    {
                        Debug.Log("[神殿测试] 已进入主场景，启动参数已消费");
                        _phase = 3;
                        _t0 = now;
                        return;
                    }
                    if (now - _t0 > 30f)
                    {
                        Debug.LogError("[神殿测试] 超时：场景=" + scene.name + " bootKeyLeft="
                                       + PlayerPrefs.GetString(EpicSpark.Bootstrap.GameBootstrap.BootEventKey, ""));
                        Finish(1);
                    }
                    return;
                }

                if (_phase == 3 && now - _t0 > 3f)
                {
                    Debug.Log("[神殿测试] 通过：神殿开始场景 → 主场景自动开局");
                    Finish(0);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[神殿测试] 异常: " + e);
                Finish(1);
            }
        }

        private static void CaptureOverlay(GameObject canvasGo)
        {
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            Camera cam = Camera.main;
            if (cam == null) cam = UnityEngine.Object.FindObjectOfType<Camera>();
            if (canvas == null || cam == null)
            {
                Debug.LogWarning("[神殿测试] 无法截图：缺少 Canvas 或相机");
                return;
            }

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            Canvas.ForceUpdateCanvases();

            const int w = 1920;
            const int h = 1080;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes("Logs/temple-overlay.png", tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = prev;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(tex);
            Debug.Log("[神殿测试] 已保存 Logs/temple-overlay.png");
        }

        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            Debug.Log("[神殿测试] 结束，退出码=" + code + "，离线开关恢复=" + _offlineWasSet);
            if (_offlineWasSet)
            {
                if (_offlinePrev == 1) PlayerPrefs.SetInt(EpicSpark.Bootstrap.ApiConfig.PrefOffline, 1);
                else PlayerPrefs.DeleteKey(EpicSpark.Bootstrap.ApiConfig.PrefOffline);
                PlayerPrefs.Save();
            }
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
            EditorApplication.isPlaying = false;
            EditorApplication.Exit(code);
        }
    }
}
