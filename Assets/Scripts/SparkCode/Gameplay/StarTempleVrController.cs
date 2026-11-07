using System;
using System.Collections;
using Google.XR.Cardboard;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;

namespace SparkCode.Gameplay
{
    /// <summary>
    /// Cardboard XR viewer integration. Normal play uses the existing center camera;
    /// Cardboard owns stereo projection, lens distortion and head tracking in VR mode.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class StarTempleVrController : MonoBehaviour
    {
        public static bool PointerOverUiThisFrame { get; private set; }
        public static bool IsCardboardActive { get; private set; }

        private const string DefaultViewerUri = "https://g.co/cardboard";
        private const float CalibrationDuration = 1.2f;
        private static readonly Rect ToggleRect = new Rect(1610f, 24f, 286f, 52f);
        private static readonly Rect DialogRect = new Rect(560f, 220f, 800f, 620f);

        enum UiState
        {
            Hidden,
            Initializing,
            ProfileRequired,
            AwaitFlatConfirmation,
            Calibrating,
            Error
        }

        public bool IsVrMode { get; private set; }
        public Camera CenterCamera { get; private set; }

        StarTemplePlayerController player;
        StarTempleMobileGamepad mobileInput;
        TrackedPoseDriver trackedPose;
        Transform trackingReference;
        UiState uiState;
        string errorMessage = string.Empty;
        float calibrationProgress;
        bool loaderReady;
        bool waitingForProfileScan;

        Transform normalParent;
        Vector3 normalLocalPosition;
        Quaternion normalLocalRotation;
        float normalFieldOfView;
        float normalAspect;
        Rect normalRect;

        UniversalAdditionalCameraData cameraData;
        UniversalRenderPipelineAsset urpAsset;
        bool performanceCaptured;
        bool normalPostProcessing;
        float normalRenderScale;
        int normalVsyncCount;
        int normalTargetFrameRate;

        GUIStyle titleStyle;
        GUIStyle bodyStyle;
        GUIStyle buttonStyle;
        Font uiFont;

        void Start()
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                enabled = false;
                return;
            }

            player = GetComponent<StarTemplePlayerController>();
            mobileInput = StarTempleMobileGamepad.Instance;
            if (player != null) CenterCamera = player.playerCamera;
            if (CenterCamera == null)
            {
                Debug.LogError("[StarTempleVR] Center player camera is missing");
                enabled = false;
                return;
            }

            cameraData = CenterCamera.GetComponent<UniversalAdditionalCameraData>();
            urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            trackedPose = CenterCamera.GetComponent<TrackedPoseDriver>();
            if (trackedPose == null) trackedPose = CenterCamera.gameObject.AddComponent<TrackedPoseDriver>();
            trackedPose.trackingType = TrackedPoseDriver.TrackingType.RotationOnly;
            trackedPose.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            trackedPose.enabled = false;

            var reference = new GameObject("Cardboard Tracking Reference");
            trackingReference = reference.transform;
            trackingReference.SetParent(CenterCamera.transform.parent, false);
            trackingReference.localPosition = Vector3.zero;
            trackingReference.localRotation = Quaternion.identity;

            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            var worldUi = FindObjectOfType<StarTempleWorldUiController>();
            if (worldUi != null) uiFont = worldUi.uiFont;
        }

        void Update()
        {
            PointerOverUiThisFrame = uiState != UiState.Hidden || PointerOver(ToggleRect);
            if (!loaderReady) return;

            if (Api.HasNewDeviceParams()) Api.ReloadDeviceParams();
            Api.UpdateScreenParams();

            if (IsVrMode && Api.IsCloseButtonPressed) ExitVrMode();
            if (Api.IsGearButtonPressed) StartCoroutine(ScanViewerProfile());

            if (waitingForProfileScan && Api.HasNewDeviceParams())
            {
                waitingForProfileScan = false;
                Api.ReloadDeviceParams();
                uiState = UiState.AwaitFlatConfirmation;
            }
        }

        public void SetVrMode(bool enabled)
        {
            if (enabled) StartCoroutine(EnterVrMode());
            else ExitVrMode();
        }

        public void BeginCalibrationPrompt()
        {
            if (IsVrMode)
            {
                ExitVrMode();
                return;
            }
            StartCoroutine(PrepareVrPrompt());
        }

        IEnumerator PrepareVrPrompt()
        {
            if (uiState == UiState.Initializing || IsVrMode) yield break;
            uiState = UiState.Initializing;
            yield return InitializeCardboardLoader();
            if (!loaderReady)
            {
                Fail("Cardboard 初始化失败。请检查 XR Plug-in Management 和图形 API 配置。");
                yield break;
            }
            uiState = Api.HasDeviceParams() ? UiState.AwaitFlatConfirmation : UiState.ProfileRequired;
        }

        IEnumerator InitializeCardboardLoader()
        {
            if (loaderReady) yield break;
            var general = XRGeneralSettings.Instance;
            if (general == null || general.Manager == null)
            {
                loaderReady = false;
                yield break;
            }
            if (general.Manager.activeLoader == null)
                yield return general.Manager.InitializeLoader();
            loaderReady = general.Manager.activeLoader != null;
        }

        IEnumerator EnterVrMode()
        {
            if (IsVrMode || uiState == UiState.Calibrating) yield break;
            uiState = UiState.Calibrating;
            calibrationProgress = 0f;
            yield return InitializeCardboardLoader();
            if (!loaderReady)
            {
                Fail("Cardboard 初始化失败。请检查 XR Plug-in Management 和图形 API 配置。");
                yield break;
            }
            if (!Api.HasDeviceParams())
            {
                uiState = UiState.ProfileRequired;
                yield break;
            }

            CaptureNormalCamera();
            CenterCamera.transform.SetParent(trackingReference, true);
            trackingReference.localPosition = Vector3.zero;
            trackingReference.localRotation = Quaternion.Euler(90f, 0f, 0f);
            trackedPose.enabled = true;

            IsVrMode = true;
            IsCardboardActive = true;
            ApplyVrPerformanceSettings();
            XRGeneralSettings.Instance.Manager.StartSubsystems();

            float started = Time.unscaledTime;
            while (Time.unscaledTime - started < CalibrationDuration)
            {
                calibrationProgress = Mathf.Clamp01((Time.unscaledTime - started) / CalibrationDuration);
                Api.UpdateScreenParams();
                yield return null;
            }
            Api.Recenter();
            uiState = UiState.Hidden;
        }

        public void ExitVrMode()
        {
            if (!IsVrMode && !loaderReady) return;
            IsVrMode = false;
            IsCardboardActive = false;
            waitingForProfileScan = false;
            if (trackedPose != null) trackedPose.enabled = false;

            if (loaderReady && XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null)
            {
                var manager = XRGeneralSettings.Instance.Manager;
                if (manager.isInitializationComplete) manager.StopSubsystems();
                if (manager.activeLoader != null) manager.DeinitializeLoader();
            }
            loaderReady = false;
            RestoreNormalCamera();
            RestorePerformanceSettings();
            uiState = UiState.Hidden;
        }

        void CaptureNormalCamera()
        {
            normalParent = CenterCamera.transform.parent;
            normalLocalPosition = CenterCamera.transform.localPosition;
            normalLocalRotation = CenterCamera.transform.localRotation;
            normalFieldOfView = CenterCamera.fieldOfView;
            normalAspect = CenterCamera.aspect;
            normalRect = CenterCamera.rect;
        }

        void RestoreNormalCamera()
        {
            if (CenterCamera == null) return;
            CenterCamera.enabled = true;
            if (normalParent != null) CenterCamera.transform.SetParent(normalParent, true);
            CenterCamera.transform.localPosition = normalLocalPosition;
            CenterCamera.transform.localRotation = normalLocalRotation;
            CenterCamera.fieldOfView = normalFieldOfView;
            CenterCamera.aspect = normalAspect;
            CenterCamera.rect = normalRect;
            if (trackingReference != null) trackingReference.localRotation = Quaternion.identity;
        }

        void ApplyVrPerformanceSettings()
        {
            if (performanceCaptured) return;
            performanceCaptured = true;
            if (cameraData != null)
            {
                normalPostProcessing = cameraData.renderPostProcessing;
                cameraData.renderPostProcessing = false;
            }
            if (urpAsset != null)
            {
                normalRenderScale = urpAsset.renderScale;
                urpAsset.renderScale = 0.8f;
            }
            normalVsyncCount = QualitySettings.vSyncCount;
            normalTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }

        void RestorePerformanceSettings()
        {
            if (!performanceCaptured) return;
            if (cameraData != null) cameraData.renderPostProcessing = normalPostProcessing;
            if (urpAsset != null) urpAsset.renderScale = normalRenderScale;
            QualitySettings.vSyncCount = normalVsyncCount;
            Application.targetFrameRate = normalTargetFrameRate;
            performanceCaptured = false;
        }

        IEnumerator ScanViewerProfile()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string cameraPermission = "android.permission.CAMERA";
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
            {
                UnityEngine.Android.Permission.RequestUserPermission(cameraPermission);
                float started = Time.unscaledTime;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission) &&
                       Time.unscaledTime - started < 15f)
                    yield return null;
            }
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
            {
                Fail("需要相机权限才能扫描 VR 盒子的 QR 码。");
                yield break;
            }
#endif
            if (!loaderReady) yield return InitializeCardboardLoader();
            if (!loaderReady)
            {
                Fail("Cardboard 尚未初始化，无法扫描 QR 码。");
                yield break;
            }
            waitingForProfileScan = true;
            uiState = UiState.ProfileRequired;
            Api.ScanDeviceParams();
        }

        void UseDefaultViewerProfile()
        {
            if (!loaderReady) return;
            Api.SaveDeviceParams(DefaultViewerUri);
            Api.ReloadDeviceParams();
            uiState = UiState.AwaitFlatConfirmation;
        }

        void CancelVrPrompt()
        {
            waitingForProfileScan = false;
            if (!IsVrMode && loaderReady && XRGeneralSettings.Instance != null &&
                XRGeneralSettings.Instance.Manager != null)
            {
                var manager = XRGeneralSettings.Instance.Manager;
                if (manager.activeLoader != null) manager.DeinitializeLoader();
            }
            loaderReady = false;
            uiState = UiState.Hidden;
        }

        void Fail(string message)
        {
            Debug.LogError("[StarTempleVR] " + message);
            errorMessage = message;
            uiState = UiState.Error;
        }

        void OnDisable()
        {
            PointerOverUiThisFrame = false;
            ExitVrMode();
        }

        void OnDestroy()
        {
            if (trackingReference != null) Destroy(trackingReference.gameObject);
        }

        void OnGUI()
        {
            if (Application.platform != RuntimePlatform.Android || CenterCamera == null) return;
            if (player != null && player.IsModalFrozen && uiState == UiState.Hidden) return;
            EnsureStyles();

            Matrix4x4 previous = GUI.matrix;
            GetGuiTransform(out float scale, out Vector2 offset);
            GUI.matrix = Matrix4x4.TRS(new Vector3(offset.x, offset.y, 0f), Quaternion.identity,
                new Vector3(scale, scale, 1f));
            GUI.depth = -1200;

            if (uiState == UiState.Hidden)
            {
                if (GUI.Button(ToggleRect, IsVrMode ? "退出 VR" : "VR 模式", buttonStyle))
                    BeginCalibrationPrompt();
            }
            else
            {
                GUI.Box(DialogRect, GUIContent.none);
                DrawDialog();
            }
            GUI.matrix = previous;
        }

        void DrawDialog()
        {
            GUI.Label(new Rect(590f, 250f, 740f, 58f), "Cardboard VR 配置", titleStyle);
            switch (uiState)
            {
                case UiState.Initializing:
                    GUI.Label(new Rect(610f, 350f, 700f, 150f), "正在初始化 Cardboard……", bodyStyle);
                    break;
                case UiState.ProfileRequired:
                    GUI.Label(new Rect(610f, 315f, 700f, 180f),
                        waitingForProfileScan
                            ? "请扫描 VR 盒子上的 QR 码，完成后返回本应用。"
                            : "需要 VR 盒子的 Viewer Profile 来校准镜片、视口和畸变。\n请扫码，或先使用通用 Cardboard 参数。", bodyStyle);
                    if (GUI.Button(new Rect(680f, 510f, 560f, 64f), "扫描盒子 QR 码", buttonStyle))
                        StartCoroutine(ScanViewerProfile());
                    if (GUI.Button(new Rect(680f, 590f, 560f, 64f), "使用通用 Cardboard 参数", buttonStyle))
                        UseDefaultViewerProfile();
                    if (GUI.Button(new Rect(835f, 710f, 250f, 62f), "取消", buttonStyle))
                        CancelVrPrompt();
                    break;
                case UiState.AwaitFlatConfirmation:
                    GUI.Label(new Rect(610f, 320f, 700f, 210f),
                        "请将手机平放在水平桌面上，屏幕朝上并保持静止。\n确认后将启动头部追踪，以平放姿态作为正下方参考视角。\n校准期间请不要移动手机。", bodyStyle);
                    if (GUI.Button(new Rect(690f, 570f, 540f, 72f), "已平放，开始校准", buttonStyle))
                        StartCoroutine(EnterVrMode());
                    if (GUI.Button(new Rect(835f, 700f, 250f, 62f), "取消", buttonStyle))
                        CancelVrPrompt();
                    break;
                case UiState.Calibrating:
                    GUI.Label(new Rect(610f, 340f, 700f, 150f),
                        "正在启动 Cardboard 并校准，请保持手机平放且静止……", bodyStyle);
                    GUI.Box(new Rect(680f, 540f, 560f, 34f), GUIContent.none);
                    GUI.DrawTexture(new Rect(684f, 544f, 552f * calibrationProgress, 26f), Texture2D.whiteTexture);
                    GUI.Label(new Rect(610f, 610f, 700f, 62f),
                        Mathf.RoundToInt(calibrationProgress * 100f) + "%", bodyStyle);
                    break;
                case UiState.Error:
                    GUI.Label(new Rect(610f, 325f, 700f, 220f), errorMessage, bodyStyle);
                    if (GUI.Button(new Rect(835f, 650f, 250f, 62f), "关闭", buttonStyle))
                        CancelVrPrompt();
                    break;
            }
        }

        void EnsureStyles()
        {
            if (titleStyle != null) return;
            if (uiFont == null)
                uiFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" }, 26);
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 32,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                font = uiFont,
                normal = { textColor = new Color(1f, 0.82f, 0.45f, 1f) }
            };
            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 25,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                font = uiFont,
                normal = { textColor = Color.white }
            };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 25, font = uiFont };
        }

        static void GetGuiTransform(out float scale, out Vector2 offset)
        {
            scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            offset = new Vector2((Screen.width - 1920f * scale) * 0.5f,
                (Screen.height - 1080f * scale) * 0.5f);
        }

        static bool PointerOver(Rect rect)
        {
            GetGuiTransform(out float scale, out Vector2 offset);
            Vector2 point = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            point = (point - offset) / Mathf.Max(0.0001f, scale);
            return rect.Contains(point);
        }
    }
}
