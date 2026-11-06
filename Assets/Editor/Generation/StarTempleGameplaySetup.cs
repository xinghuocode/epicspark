using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SparkCode.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace SparkCode.Generation
{
    /// <summary>
    /// Creates a gameplay copy of the URP temple, adds the first-person controller,
    /// a player camera and a test interaction target on the star.
    /// </summary>
    public static class StarTempleGameplaySetup
    {
        public const string SourceScene = "Assets/Scenes/BlenderStarTemple_URP.unity";
        public const string GameplayScene = "Assets/Scenes/BlenderStarTemple_Gameplay.unity";
        const string Generated = "Assets/Generated/BlenderStarTempleURP";
        public const string PlayerName = "StarTemple_Player";
        static readonly Vector3 SpawnPosition = new Vector3(0f, 0.25f, 10.2f);

        [MenuItem("SparkCode/制作第一人称键盘移动系统", false, 54)]
        public static void BuildAndCapture()
        {
            Build();
            Capture();
        }

        public static void Build()
        {
            try
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                if (!File.Exists(SourceScene))
                    throw new InvalidOperationException("Source scene missing: " + SourceScene);

                int quality = ShaderPreviewTest.FindQuality("High Fidelity");
                if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);
                EnsureFolder(Generated);

                var source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(GameplayScene) != null)
                    AssetDatabase.DeleteAsset(GameplayScene);
                if (!EditorSceneManager.SaveScene(source, GameplayScene))
                    throw new InvalidOperationException("Could not create gameplay scene copy");
                AssetDatabase.Refresh();
                var scene = EditorSceneManager.OpenScene(GameplayScene, OpenSceneMode.Single);

                ConfigureShowcaseScene();
                var material = CreateTraceMaterial();
                var player = CreatePlayer(material);
                ConfigureStarInteraction(player);
                var starPivot = GameObject.Find("Spark Pivot");
                if (starPivot == null) throw new InvalidOperationException("Spark Pivot missing");
                StarTempleStoryUiSetup.Build(player, starPivot.transform,
                    player.GetComponentInChildren<Camera>(true));
                Physics.SyncTransforms();

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, GameplayScene))
                    throw new InvalidOperationException("Could not save " + GameplayScene);
                AddSceneToBuildSettings();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Validate();
                Debug.Log("[StarTempleGameplay] BUILD COMPLETE: " + GameplayScene);
            }
            catch (Exception e)
            {
                Debug.LogError("[StarTempleGameplay] BUILD FAILED: " + e);
                throw;
            }
        }

        static void ConfigureShowcaseScene()
        {
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            {
                cam.enabled = false;
                cam.tag = "Untagged";
                var listener = cam.GetComponent<AudioListener>();
                if (listener != null) listener.enabled = false;
            }

            var showcase = UnityEngine.Object.FindObjectOfType<TempleShowcaseController>();
            if (showcase != null)
            {
                // Keep the star spin/bob alive, but disable the 1-4 showcase camera keys.
                showcase.cameras = new Camera[0];
                showcase.starSpinSpeed = 2.0f;
            }
        }

        static Material CreateTraceMaterial()
        {
            string path = Generated + "/Attack_Trace.mat";
            AssetDatabase.DeleteAsset(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            var mat = new Material(shader) { name = "Attack_Trace" };
            mat.SetColor("_BaseColor", new Color(0.5f, 0.22f, 0.03f, 1f));
            mat.SetColor("_EmissionColor", new Color(4.5f, 2.3f, 0.42f, 1f));
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.65f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static GameObject CreatePlayer(Material traceMaterial)
        {
            var existing = GameObject.Find(PlayerName);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            var player = new GameObject(PlayerName);
            player.tag = "Player";
            player.layer = 2; // Built-in Ignore Raycast layer, excluded from player raycasts.
            player.transform.position = SpawnPosition;
            player.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.32f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.slopeLimit = 48f;
            cc.stepOffset = 0.38f;
            cc.skinWidth = 0.04f;
            cc.minMoveDistance = 0f;

            var camGo = new GameObject("Player Camera");
            camGo.layer = 2;
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.62f, 0f);
            camGo.transform.localRotation = Quaternion.Euler(-8f, 0f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 72f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 220f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.tag = "MainCamera";
            cam.enabled = true;
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            data.renderShadows = true;
            data.volumeLayerMask = ~0;
            data.volumeTrigger = camGo.transform;
            var listener = camGo.AddComponent<AudioListener>();
            listener.enabled = true;

            var traceGo = new GameObject("Attack Trace");
            traceGo.layer = 2;
            traceGo.transform.SetParent(player.transform, false);
            var line = traceGo.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.025f;
            line.endWidth = 0.012f;
            line.numCapVertices = 2;
            line.sharedMaterial = traceMaterial;
            line.startColor = new Color(1f, 0.55f, 0.12f, 1f);
            line.endColor = new Color(1f, 0.32f, 0.04f, 0.4f);
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;

            var controller = player.AddComponent<StarTemplePlayerController>();
            controller.playerCamera = cam;
            controller.attackTrace = line;
            controller.standingHeight = 1.8f;
            controller.crouchHeight = 1.05f;
            controller.walkSpeed = 3.8f;
            controller.sprintSpeed = 7f;
            controller.crouchSpeed = 2f;
            controller.jumpHeight = 1.15f;
            controller.mouseSensitivity = 2.2f;

            var haloBillboards = UnityEngine.Object.FindObjectsOfType<Billboard>(true);
            foreach (var billboard in haloBillboards)
                billboard.cameraTransform = cam.transform;

            return player;
        }

        static void ConfigureStarInteraction(GameObject player)
        {
            var pivot = GameObject.Find("Spark Pivot");
            if (pivot == null)
            {
                Debug.LogWarning("[StarTempleGameplay] Spark Pivot not found; interaction demo skipped.");
                return;
            }

            var collider = pivot.GetComponent<SphereCollider>();
            if (collider == null) collider = pivot.AddComponent<SphereCollider>();
            collider.center = Vector3.zero;
            collider.radius = 2.45f;
            collider.isTrigger = true;
            pivot.layer = 0;

            var interactable = pivot.GetComponent<StarTempleSparkInteractable>();
            if (interactable == null) interactable = pivot.AddComponent<StarTempleSparkInteractable>();
            interactable.hideFlags = HideFlags.None;

            Debug.Log("[StarTempleGameplay] Star interaction target ready. Player=" + player.name);
        }

        [MenuItem("SparkCode/验证第一人称键盘移动系统", false, 55)]
        public static void Validate()
        {
            if (!File.Exists(GameplayScene))
                throw new InvalidOperationException("Gameplay scene missing: " + GameplayScene);
            var scene = EditorSceneManager.OpenScene(GameplayScene, OpenSceneMode.Single);
            var report = new GameplayValidationReport { scene = GameplayScene };
            report.pipeline = GraphicsSettings.currentRenderPipeline != null
                ? GraphicsSettings.currentRenderPipeline.name : "Built-in";
            report.urpActive = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;

            var player = GameObject.Find(PlayerName);
            var controller = player != null ? player.GetComponent<StarTemplePlayerController>() : null;
            var cam = player != null ? player.GetComponentInChildren<Camera>(true) : null;
            report.playerFound = player != null;
            report.characterController = player != null && player.GetComponent<CharacterController>() != null;
            report.controllerFound = controller != null;
            report.playerCameraActive = cam != null && cam.enabled && cam.tag == "MainCamera";
            report.activeCameras = UnityEngine.Object.FindObjectsOfType<Camera>().Count(c => c.enabled);
            report.enabledAudioListeners = UnityEngine.Object.FindObjectsOfType<AudioListener>().Count(l => l.enabled);
            var showcase = UnityEngine.Object.FindObjectOfType<TempleShowcaseController>();
            report.showcaseCameraCount = showcase != null && showcase.cameras != null ? showcase.cameras.Length : -1;
            var pivot = GameObject.Find("Spark Pivot");
            report.starCollider = pivot != null && pivot.GetComponent<SphereCollider>() != null;
            report.starInteractable = pivot != null && pivot.GetComponent<StarTempleSparkInteractable>() != null;
            report.traceMaterial = controller != null && controller.attackTrace != null && controller.attackTrace.sharedMaterial != null;

            var storyUi = UnityEngine.Object.FindObjectOfType<StarTempleWorldUiController>();
            report.storyUiFound = storyUi != null;
            report.storyUiButtons = storyUi != null ? storyUi.buttons.Count : 0;
            report.lorePointCount = UnityEngine.Object.FindObjectsOfType<StarTempleLorePoint>().Length;
            report.exitObelisk = GameObject.Find("Temple_Exit_Obelisk") != null;
            report.loreManifestPath = StarTempleLoreStore.ManifestPath;
            report.loreManifestExists = File.Exists(report.loreManifestPath);
            if (report.loreManifestExists)
            {
                var manifest = StarTempleLoreStore.LoadManifest();
                report.loreEntryCount = manifest != null && manifest.entries != null ? manifest.entries.Count : 0;
            }

            var defaults = StarTemplePlayerController.CreateDefaultBindings();
            report.defaultBindingCount = defaults.Count;
            report.defaultBindingSignature = StarTemplePlayerController.DefaultBindingSignature();
            var uniqueKeys = new HashSet<KeyCode>(defaults.Values.Select(k => k));
            report.uniqueDefaultKeys = uniqueKeys.Count;
            report.bindingsUnique = uniqueKeys.Count == StarTemplePlayerController.AllActions.Length;

            report.passed = report.urpActive && report.playerFound && report.characterController && report.controllerFound &&
                            report.playerCameraActive && report.activeCameras == 1 && report.enabledAudioListeners == 1 &&
                            report.showcaseCameraCount == 0 && report.starCollider && report.starInteractable &&
                            report.traceMaterial && report.defaultBindingCount == 10 && report.bindingsUnique &&
                            report.storyUiFound && report.storyUiButtons >= 5 && report.lorePointCount >= 5 &&
                            report.exitObelisk && report.loreManifestExists && report.loreEntryCount >= 5;
            report.sceneRenderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>().Length;

            Directory.CreateDirectory("Docs");
            File.WriteAllText("Docs/BlenderStarTemple_GameplayValidation.json", JsonUtility.ToJson(report, true));
            Debug.Log("[StarTempleGameplay] VALIDATION " + JsonUtility.ToJson(report));
            if (!report.passed) throw new InvalidOperationException("Gameplay validation failed");
        }

        public static void Capture()
        {
            EditorSceneManager.OpenScene(GameplayScene, OpenSceneMode.Single);
            int quality = ShaderPreviewTest.FindQuality("High Fidelity");
            if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);
            var cam = Camera.main;
            if (cam == null) throw new InvalidOperationException("Player camera not found");
            Directory.CreateDirectory("Screenshots");
            var ui = UnityEngine.Object.FindObjectOfType<StarTempleWorldUiController>();
            if (ui != null) ui.ApplyCjkFont();

            var originalPosition = cam.transform.position;
            var originalRotation = cam.transform.rotation;

            if (ui != null) ui.EditorPreview(0, false, false);
            RenderScreenshot(cam, "Screenshots/BlenderStarTemple_Gameplay.png");

            if (ui != null)
            {
                ui.EditorPreview(1, false, false);
                RenderScreenshot(cam, "Screenshots/BlenderStarTemple_StoryDialogue.png");

                var lore = UnityEngine.Object.FindObjectsOfType<StarTempleLorePoint>()
                    .OrderBy(p => p.name).FirstOrDefault();
                if (lore != null)
                {
                    Vector3 camPosition = lore.transform.position + new Vector3(0f, -1.15f, 4.2f);
                    Vector3 lookAt = lore.transform.position + Vector3.up * 0.45f;
                    cam.transform.SetPositionAndRotation(camPosition,
                        Quaternion.LookRotation(lookAt - camPosition, Vector3.up));
                    ui.EditorPreview(-1, true, false);
                    RenderScreenshot(cam, "Screenshots/BlenderStarTemple_StoryLore.png");
                }

                if (ui.starPivot != null && ui.loreCanvas != null)
                {
                    Vector3 focusBase = ui.starPivot.position;
                    Vector3 focusCamera = new Vector3(0f, 1.9f, 9.2f);
                    Vector3 focusLookAt = new Vector3(0f, 3.0f, 0f);
                    cam.transform.SetPositionAndRotation(focusCamera,
                        Quaternion.LookRotation(focusLookAt - focusCamera, Vector3.up));
                    ui.starPivot.position = focusBase + Vector3.up * ui.loreFocusLift;
                    ui.EditorPreviewLoreFocus();
                    RenderScreenshot(cam, "Screenshots/BlenderStarTemple_LoreFocus.png");
                    ui.starPivot.position = focusBase;
                    ui.EditorPreview(-1, false, false);
                }

                if (ui.starPivot != null)
                {
                    Vector3 starBase = ui.starPivot.position;
                    Vector3 lowered = starBase + Vector3.down * ui.starDropHeight;
                    Vector3 starCamera = new Vector3(0f, 2.05f, 5.7f);
                    Vector3 starLookAt = lowered + Vector3.up * 0.15f;
                    cam.transform.SetPositionAndRotation(starCamera,
                        Quaternion.LookRotation(starLookAt - starCamera, Vector3.up));
                    ui.EditorPreview(-1, false, false);
                    ui.starPivot.position = lowered;
                    RenderScreenshot(cam, "Screenshots/BlenderStarTemple_StarLowered.png");
                    ui.starPivot.position = starBase;
                }

                Vector3 obeliskBase = new Vector3(-1.6f, 0.15f, 13.9f);
                Vector3 exitCamPosition = obeliskBase + new Vector3(0f, 1.55f, -1.25f);
                Vector3 exitLookAt = obeliskBase + Vector3.up * 2.15f;
                cam.transform.SetPositionAndRotation(exitCamPosition,
                    Quaternion.LookRotation(exitLookAt - exitCamPosition, Vector3.up));
                ui.EditorPreview(-1, false, true);
                RenderScreenshot(cam, "Screenshots/BlenderStarTemple_ExitPrompt.png");
            }

            cam.transform.SetPositionAndRotation(originalPosition, originalRotation);
            AssetDatabase.Refresh();
            Debug.Log("[StarTempleGameplay] screenshots written to Screenshots/ (scene, dialogue, lore, focused lore, lowered star, exit prompt)");
        }

        static void RenderScreenshot(Camera cam, string path)
        {
            const int width = 1600, height = 1200;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            var previous = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = previous;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(texture);
            Debug.Log("[StarTempleGameplay] screenshot " + path);
        }

        static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == GameplayScene);
            scenes.Add(new EditorBuildSettingsScene(GameplayScene, false));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        [Serializable]
        public class GameplayValidationReport
        {
            public string scene;
            public string pipeline;
            public string defaultBindingSignature;
            public bool urpActive;
            public bool playerFound;
            public bool characterController;
            public bool controllerFound;
            public bool playerCameraActive;
            public bool starCollider;
            public bool starInteractable;
            public bool traceMaterial;
            public bool bindingsUnique;
            public bool storyUiFound;
            public bool exitObelisk;
            public bool loreManifestExists;
            public bool passed;
            public int activeCameras;
            public int enabledAudioListeners;
            public int showcaseCameraCount;
            public int defaultBindingCount;
            public int uniqueDefaultKeys;
            public int sceneRenderers;
            public int storyUiButtons;
            public int lorePointCount;
            public int loreEntryCount;
            public string loreManifestPath;
        }
    }
}
