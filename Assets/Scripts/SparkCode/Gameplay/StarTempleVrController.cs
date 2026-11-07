using System;
using System.Collections;
using Google.XR.Cardboard;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
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
        /// <summary>VR 校准模式激活中（画面叠加测试图与 16 宫格高亮面板）。</summary>
        public static bool CalibrationPatternActive { get; private set; }

        /// <summary>
        /// 准星偏移（归一化）：x 以单眼半屏宽为单位，y 以整屏高为单位。
        /// 由玩家在 VR 校准模式中按 X 键进入调整模式后用摇杆实测对准高亮块得到，
        /// 持久化到 PlayerPrefs；绘制准星时叠加，使准星与真实视口中心射线一致。
        /// </summary>
        public static Vector2 GazeOffsetNorm => gazeOffsetNorm;
        static Vector2 gazeOffsetNorm;

        /// <summary>准星调整模式激活中：玩家移动输入被屏蔽，防止摇杆同时移动角色破坏对位。</summary>
        public static bool ReticleAdjustModeActive { get; private set; }

        // VR 校准模式的 16 宫格面板（世界锚定 uGUI 画布，退出时销毁）。
        // 渲染与悬停高亮完全复用开始面板（bridge UI）的机制：世界空间 Canvas +
        // Image + RectangleContainsScreenPoint(屏幕中心, 相机)，VR 下已被验证可行。
        const int GridCellsPerRow = 8;
        const float GridCellSize = 0.2f;
        const float GridCellGap = 0.018f;
        const float ReticleAdjustSpeed = 0.2f;
        const float ReticleAdjustRange = 0.25f;
        GameObject calibrationPanel;
        RectTransform[] calibrationCells;
        RectTransform calibrationHighlightRect;
        GameObject calibrationHighlight;
        bool reticleAdjustMode;

        // VR SHINECON 官方 Viewer Profile（Shinecon 官网 QR 码解析所得）。
        // Cardboard SDK 的 SaveDeviceParams 仅支持 HTTPS URI，故使用 https 形式。
        // 注意：G04 与 G05 Pro 的光学参数字节完全一致（厂商复用默认值），仅型号名不同。
        static readonly (string name, string uri)[] SupportedViewers =
        {
            ("VR SHINECON G05 Pro",
             "https://google.com/cardboard/cfg?p=CghTaGluZWNvbhIDRzA1HexROD0ltvN9PSoQAABIQgAASEIAAEhCAABIQlgANbbz_Tw6CPCnBj_jpRu-UABgAA"),
            ("VR SHINECON G04",
             "https://google.com/cardboard/cfg?p=CghTaGluZWNvbhIDRzA0HexROD0ltvN9PSoQAABIQgAASEIAAEhCAABIQlgANbbz_Tw6CPCnBj_jpRu-UABgAA"),
        };
        private static readonly Rect ToggleRect = new Rect(1610f, 24f, 286f, 52f);
        private static readonly Rect CalibrateRect = new Rect(1610f, 88f, 286f, 52f);
        private static readonly Rect DialogRect = new Rect(560f, 220f, 800f, 620f);

        enum UiState
        {
            Hidden,
            Initializing,
            ProfileRequired,
            AwaitHeadConfirm,
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
        bool loaderReady;
        bool pendingCalibrationPattern;
        // 戴盒确认（AwaitHeadConfirm）的静止自动校准状态
        float headConfirmStartTime = -1f;
        float stillnessAccum;

        Transform normalParent;
        Vector3 normalLocalPosition;
        Quaternion normalLocalRotation;
        float normalFieldOfView;
        float normalAspect;
        Rect normalRect;

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

            trackedPose = CenterCamera.GetComponent<TrackedPoseDriver>();
            if (trackedPose == null) trackedPose = CenterCamera.gameObject.AddComponent<TrackedPoseDriver>();
            // 运行时 AddComponent 不会触发编辑器专属的 Reset()，默认 Action 没有任何
            // Binding，TrackedPoseDriver 将永远收不到 XRHMD 数据（表现为陀螺仪无效）。
            // 因此这里显式指定与官方 HelloCardboard 样例等价的 XRHMD 绑定。
            trackedPose.positionInput = new InputActionProperty(
                new InputAction("Position", InputActionType.Value, binding: "<XRHMD>/centerEyePosition"));
            trackedPose.rotationInput = new InputActionProperty(
                new InputAction("Rotation", InputActionType.Value, binding: "<XRHMD>/centerEyeRotation"));
            // 与官方样例一致，不显式绑定 trackingState：未绑定时按"始终有效"处理，
            // 避免设备启动初期短暂报告 None 导致姿态被忽略。
            trackedPose.trackingType = TrackedPoseDriver.TrackingType.RotationOnly;
            trackedPose.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            trackedPose.enabled = false;

            var reference = new GameObject("Cardboard Tracking Reference");
            trackingReference = reference.transform;
            trackingReference.SetParent(CenterCamera.transform.parent, false);
            trackingReference.localPosition = Vector3.zero;
            trackingReference.localRotation = Quaternion.identity;

            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            gazeOffsetNorm = new Vector2(PlayerPrefs.GetFloat("vrGazeOffsetX", 0f),
                                         PlayerPrefs.GetFloat("vrGazeOffsetY", 0f));
            var worldUi = FindObjectOfType<StarTempleWorldUiController>();
            if (worldUi != null) uiFont = worldUi.uiFont;
        }

        void Update()
        {
            // VR 模式下不自绘按钮（见 OnGUI），原生控件区域不参与 UI 指针判定。
            PointerOverUiThisFrame = uiState != UiState.Hidden || (!IsVrMode && PointerOver(ToggleRect));
            if (!loaderReady) return;

            if (Api.HasNewDeviceParams()) Api.ReloadDeviceParams();
            Api.UpdateScreenParams();

            if (IsVrMode && Api.IsCloseButtonPressed) ExitVrMode();

            if (IsVrMode && CalibrationPatternActive)
            {
                UpdateCalibrationGrid();
                UpdateReticleAdjust();
                // B 键：结束校准，直接进入普通 VR 模式
                if (mobileInput != null && mobileInput.CancelPressed)
                    ExitCalibrationPattern();
            }

            if (!IsVrMode && uiState == UiState.AwaitHeadConfirm)
                UpdateHeadConfirm();
        }

        /// <summary>
        /// 生成校准面板：4×4 共 16 格，位于进入校准模式时视线正前方正中，面向玩家。
        /// 使用与开始面板（bridge UI）相同的世界空间 Canvas + Image 体系，
        /// 渲染与坐标换算在 VR 下已被验证可行。
        /// </summary>
        void BuildCalibrationPanel()
        {
            DestroyCalibrationPanel();
            if (CenterCamera == null) return;
            Transform cam = CenterCamera.transform;
            calibrationPanel = new GameObject("VRCalibrationGrid", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.Image));
            Vector3 forwardFlat = cam.forward;
            forwardFlat.y = 0f;
            if (forwardFlat.sqrMagnitude < 0.001f) forwardFlat = Vector3.forward;
            forwardFlat.Normalize();
            // 整板约 2.3 m 见方，距离取 2.6 m 保证完整落入视野
            calibrationPanel.transform.position =
                cam.position + forwardFlat * 2.6f + Vector3.down * 0.1f;
            calibrationPanel.transform.rotation =
                Quaternion.LookRotation(calibrationPanel.transform.position - cam.position, Vector3.up);

            var canvas = calibrationPanel.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = CenterCamera;
            // 画布背景块：半透明深底，衬托宫格
            var bg = calibrationPanel.GetComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0f, 0f, 0f, 0.35f);
            float step = GridCellSize + GridCellGap;
            float panelExtent = (GridCellsPerRow - 1) * 0.5f * step + GridCellSize * 0.5f;
            var canvasRt = (RectTransform)calibrationPanel.transform;
            canvasRt.sizeDelta = new Vector2(panelExtent * 2f + GridCellGap * 2f,
                                             panelExtent * 2f + GridCellGap * 2f);

            calibrationCells = new RectTransform[GridCellsPerRow * GridCellsPerRow];
            for (int row = 0; row < GridCellsPerRow; row++)
            {
                for (int col = 0; col < GridCellsPerRow; col++)
                {
                    var cellGo = new GameObject($"Cell_{row}_{col}",
                        typeof(RectTransform), typeof(UnityEngine.UI.Image));
                    cellGo.transform.SetParent(calibrationPanel.transform, false);
                    var crt = (RectTransform)cellGo.transform;
                    crt.sizeDelta = new Vector2(GridCellSize, GridCellSize);
                    crt.anchoredPosition =
                        new Vector2((col - (GridCellsPerRow - 1) * 0.5f) * step,
                                    (row - (GridCellsPerRow - 1) * 0.5f) * step);
                    cellGo.GetComponent<UnityEngine.UI.Image>().color = new Color(1f, 1f, 1f, 0.5f);
                    calibrationCells[row * GridCellsPerRow + col] = crt;
                }
            }

            var hiGo = new GameObject("Highlight", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            hiGo.transform.SetParent(calibrationPanel.transform, false);
            calibrationHighlightRect = (RectTransform)hiGo.transform;
            calibrationHighlightRect.sizeDelta = new Vector2(GridCellSize, GridCellSize);
            hiGo.GetComponent<UnityEngine.UI.Image>().color = new Color(1f, 0.85f, 0.2f, 0.95f);
            calibrationHighlight = hiGo;
            hiGo.SetActive(false);
        }

        void DestroyCalibrationPanel()
        {
            if (calibrationPanel != null) Destroy(calibrationPanel);
            calibrationPanel = null;
            calibrationCells = null;
            calibrationHighlightRect = null;
            calibrationHighlight = null;
        }

        /// <summary>
        /// 高亮交互逻辑与开始面板（bridge UI）完全同构：指针取屏幕中心，
        /// 用 RectTransformUtility.RectangleContainsScreenPoint 逐个判定格子，
        /// 命中项激活黄色高亮块（等效于 SetHoveredButton 的换色反馈）。
        /// </summary>
        void UpdateCalibrationGrid()
        {
            if (calibrationPanel == null || CenterCamera == null || calibrationHighlight == null) return;
            Vector2 pointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            RectTransform hit = null;
            for (int i = 0; i < calibrationCells.Length; i++)
            {
                var cell = calibrationCells[i];
                if (cell == null) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(cell, pointer, CenterCamera))
                {
                    hit = cell;
                    break;
                }
            }
            if (hit != null)
            {
                calibrationHighlightRect.anchoredPosition = hit.anchoredPosition;
                calibrationHighlight.SetActive(true);
            }
            else
            {
                calibrationHighlight.SetActive(false);
            }
        }

        /// <summary>结束校准模式并进入普通 VR 模式；若有未保存的准星偏移则先保存。</summary>
        void ExitCalibrationPattern()
        {
            if (reticleAdjustMode)
            {
                PlayerPrefs.SetFloat("vrGazeOffsetX", gazeOffsetNorm.x);
                PlayerPrefs.SetFloat("vrGazeOffsetY", gazeOffsetNorm.y);
            }
            reticleAdjustMode = false;
            ReticleAdjustModeActive = false;
            CalibrationPatternActive = false;
            DestroyCalibrationPanel();
        }

        /// <summary>
        /// X 键切换准星调整模式：进入后推左摇杆移动准星（屏幕偏移），
        /// 对准黄色高亮块后再按 X 保存到 PlayerPrefs 并退出调整模式。
        /// </summary>
        void UpdateReticleAdjust()
        {
            var pad = mobileInput;
            if (pad == null) return;
            if (pad.AttackPressed)
            {
                reticleAdjustMode = !reticleAdjustMode;
                ReticleAdjustModeActive = reticleAdjustMode;
                if (reticleAdjustMode)
                {
                    if (player != null) player.Notice("准星调整：推左摇杆对准黄色高亮块，再按 X 保存");
                }
                else
                {
                    PlayerPrefs.SetFloat("vrGazeOffsetX", gazeOffsetNorm.x);
                    PlayerPrefs.SetFloat("vrGazeOffsetY", gazeOffsetNorm.y);
                    if (player != null) player.Notice("准星偏移已保存");
                }
                return;
            }
            if (reticleAdjustMode)
            {
                gazeOffsetNorm += pad.Move * (ReticleAdjustSpeed * Time.unscaledDeltaTime);
                gazeOffsetNorm.x = Mathf.Clamp(gazeOffsetNorm.x, -ReticleAdjustRange, ReticleAdjustRange);
                gazeOffsetNorm.y = Mathf.Clamp(gazeOffsetNorm.y, -ReticleAdjustRange, ReticleAdjustRange);
            }
        }

        // 戴盒确认阶段：玩家戴着盒子不可能按屏幕按钮，因此以"头部位姿静止"
        // 作为确认条件——戴好盒子的动作天然以静止收尾，静止约 0.6 秒即自动
        // Recenter 进入 VR。手柄 A 键 / 屏幕按钮作为立即开始的快捷方式保留。
        const float HeadConfirmGraceSeconds = 6f;
        const float HeadConfirmStillnessSeconds = 0.6f;
        const float HeadConfirmStillnessThresholdRad = 3f * Mathf.Deg2Rad; // 陀螺角速度阈值

        void UpdateHeadConfirm()
        {
            if (mobileInput != null && mobileInput.ConfirmPressed)
            {
                StartCoroutine(EnterVrMode());
                return;
            }
            if (headConfirmStartTime < 0f) headConfirmStartTime = Time.unscaledTime;
            // 宽限期：戴盒动作本身在动，期间不做静止判定
            if (Time.unscaledTime - headConfirmStartTime < HeadConfirmGraceSeconds) return;
            if (!SystemInfo.supportsGyroscope)
            {
                // 无陀螺仪设备退化为宽限结束后固定再等待 2 秒直接开始
                if (Time.unscaledTime - headConfirmStartTime >= HeadConfirmGraceSeconds + 2f)
                    StartCoroutine(EnterVrMode());
                return;
            }
            if (!Input.gyro.enabled) Input.gyro.enabled = true;
            Vector3 rate = Input.gyro.rotationRateUnbiased;
            bool still = rate.sqrMagnitude < HeadConfirmStillnessThresholdRad * HeadConfirmStillnessThresholdRad;
            stillnessAccum = still ? stillnessAccum + Time.unscaledDeltaTime : 0f;
            if (stillnessAccum >= HeadConfirmStillnessSeconds)
                StartCoroutine(EnterVrMode());
        }

        public void SetVrMode(bool enabled)
        {
            if (enabled) StartCoroutine(EnterVrMode());
            else ExitVrMode();
        }

        public void BeginCalibrationPrompt(bool calibrationPattern = false)
        {
            if (IsVrMode)
            {
                ExitVrMode();
                return;
            }
            pendingCalibrationPattern = calibrationPattern;
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
            uiState = Api.HasDeviceParams() ? UiState.AwaitHeadConfirm : UiState.ProfileRequired;
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
            if (IsVrMode) yield break;
            // 立即隐藏对话框并复位静止检测，防止 Update 重复触发本协程
            uiState = UiState.Hidden;
            headConfirmStartTime = -1f;
            stillnessAccum = 0f;
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
            // 校准姿态 = 使用姿态（戴盒正视后按 A 键 Recenter），
            // 追踪器参考系即 Unity 参考系，无需任何补偿旋转。
            trackingReference.localRotation = Quaternion.identity;
            trackedPose.enabled = true;

            IsVrMode = true;
            IsCardboardActive = true;
            XRGeneralSettings.Instance.Manager.StartSubsystems();

            // 等一帧让追踪器产生首帧姿态，再以用户当前正视方向为参考做 Recenter。
            yield return null;
            Api.Recenter();
            uiState = UiState.Hidden;
            if (pendingCalibrationPattern)
            {
                pendingCalibrationPattern = false;
                CalibrationPatternActive = true;
                BuildCalibrationPanel();
            }
        }

        public void ExitVrMode()
        {
            if (!IsVrMode && !loaderReady) return;
            IsVrMode = false;
            IsCardboardActive = false;
            CalibrationPatternActive = false;
            pendingCalibrationPattern = false;
            reticleAdjustMode = false;
            ReticleAdjustModeActive = false;
            DestroyCalibrationPanel();
            if (trackedPose != null) trackedPose.enabled = false;

            if (loaderReady && XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null)
            {
                var manager = XRGeneralSettings.Instance.Manager;
                if (manager.isInitializationComplete) manager.StopSubsystems();
                if (manager.activeLoader != null) manager.DeinitializeLoader();
            }
            loaderReady = false;
            RestoreNormalCamera();
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

        void SelectViewerProfile(int index)
        {
            if (!loaderReady || index < 0 || index >= SupportedViewers.Length) return;
            Api.SaveDeviceParams(SupportedViewers[index].uri);
            Api.ReloadDeviceParams();
            uiState = UiState.AwaitHeadConfirm;
        }

        void CancelVrPrompt()
        {
            headConfirmStartTime = -1f;
            stillnessAccum = 0f;
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
            DestroyCalibrationPanel();
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
                // VR 模式下不绘制自定义按钮：Cardboard 原生控件（左上角 X 关闭键、
                // 右上角齿轮键）会消费其区域的触摸事件，自绘按钮与其重叠导致点不动。
                // 退出 VR 由原生 X 键经 Api.IsCloseButtonPressed 处理（见 Update）。
                if (!IsVrMode)
                {
                    if (GUI.Button(ToggleRect, "VR 模式", buttonStyle))
                        BeginCalibrationPrompt();
                    if (GUI.Button(CalibrateRect, "VR 校准", buttonStyle))
                        BeginCalibrationPrompt(calibrationPattern: true);
                }
            }
            else
            {
                GUI.Box(DialogRect, GUIContent.none);
                DrawDialog();
            }
            GUI.matrix = previous;

            if (IsVrMode)
            {
                // 校准模式下准星同样绘制（调整模式需要看到准星去对高亮块）
                DrawGazeReticle();
                if (CalibrationPatternActive) DrawCalibrationPattern();
            }
        }

        /// <summary>
        /// VR 校准测试图：每眼一张，用于戴着盒子调节镜片/瞳距/姿态。
        /// 第 1 页 对中与对齐；第 2 页 网格与畸变；第 3 页 文字清晰度；第 4 页 会聚。
        /// 全部用原始屏幕坐标绘制（左右眼视口各占半屏）。
        /// </summary>
        void DrawCalibrationPattern()
        {
            Color previousColor = GUI.color;
            GUI.depth = -1200;
            float w = Screen.width;
            float h = Screen.height;
            float half = w * 0.5f;
            float centerY = h * 0.5f;
            float[] eyeCentersX = { w * 0.25f, w * 0.75f };

            foreach (float cx in eyeCentersX)
            {
                float left = cx - half * 0.5f;
                // 文字清晰度页（黑底）：调镜片焦距至文字最清晰
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(left + 10f, centerY - 130f, half - 20f, 260f), Texture2D.whiteTexture);
                GUI.color = new Color(1f, 1f, 1f, 0.95f);
                DrawSizedLabel(new Rect(left + 20f, centerY - 120f, half - 40f, 60f),
                    "星火神殿 VR", titleStyle);
                DrawSizedLabel(new Rect(left + 20f, centerY - 50f, half - 40f, 60f),
                    "AaBbCc 0123456789", bodyStyle);
                DrawSizedLabel(new Rect(left + 20f, centerY + 20f, half - 40f, 70f),
                    "调节镜片至文字最清晰", bodyStyle);
            }

            // 底部操作提示（整屏居中）
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(w * 0.5f - 330f, h - 64f, 660f, 44f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            string hint = reticleAdjustMode
                ? "推左摇杆移动准星对准黄色高亮块  ·  再按 X 保存"
                : "按 X 调整准星  ·  按 B 进入 VR  ·  左上角 X 退出校准";
            DrawSizedLabel(new Rect(w * 0.5f - 330f, h - 60f, 660f, 40f), hint, bodyStyle);
            GUI.color = previousColor;
        }

        /// <summary>在原始屏幕坐标下用居中对齐重绘标签（GUI.matrix 已还原）。</summary>
        void DrawSizedLabel(Rect rect, string text, GUIStyle template)
        {
            GUIStyle style = new GUIStyle(template) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(rect, text, style);
        }

        /// <summary>
        /// VR 目视准星：Screen Space Overlay 画布不参与 XR 立体渲染，
        /// 因此用 IMGUI 在左右眼视口中心各画一个十字线。
        /// 注意必须用原始屏幕坐标（在恢复 GUI.matrix 之后绘制），不能用设计空间。
        /// </summary>
        void DrawGazeReticle()
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            GUI.depth = -1200;
            float w = Screen.width;
            float h = Screen.height;
            float half = w * 0.5f;
            // 叠加校准得到的偏移，使准星与真实视口中心射线一致
            float centerY = h * 0.5f + gazeOffsetNorm.y * h;
            const float ArmHalf = 16f;
            const float ArmThickness = 3f;
            const float Gap = 5f;
            const float DotSize = 5f;
            float[] eyeCentersX = { w * 0.25f + gazeOffsetNorm.x * half,
                                    w * 0.75f + gazeOffsetNorm.x * half };
            foreach (float cx in eyeCentersX)
            {
                // 中心点
                GUI.DrawTexture(new Rect(cx - DotSize * 0.5f, centerY - DotSize * 0.5f, DotSize, DotSize),
                    Texture2D.whiteTexture);
                // 十字四臂（带中心空隙，避免遮挡注视点）
                GUI.DrawTexture(new Rect(cx - ArmHalf, centerY - ArmThickness * 0.5f,
                        ArmHalf - Gap, ArmThickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + Gap, centerY - ArmThickness * 0.5f,
                        ArmHalf - Gap, ArmThickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - ArmThickness * 0.5f, centerY - ArmHalf,
                        ArmThickness, ArmHalf - Gap), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - ArmThickness * 0.5f, centerY + Gap,
                        ArmThickness, ArmHalf - Gap), Texture2D.whiteTexture);
            }
            GUI.color = previousColor;
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
                    GUI.Label(new Rect(610f, 300f, 700f, 150f),
                        "请选择 VR 盒子型号。\n将载入对应官方光学参数（镜片畸变、瞳距与视口）后继续。", bodyStyle);
                    float buttonY = 470f;
                    for (int i = 0; i < SupportedViewers.Length; i++)
                    {
                        int index = i;
                        if (GUI.Button(new Rect(680f, buttonY, 560f, 64f),
                                "选用 " + SupportedViewers[i].name, buttonStyle))
                            SelectViewerProfile(index);
                        buttonY += 84f;
                    }
                    if (GUI.Button(new Rect(835f, 700f, 250f, 62f), "取消", buttonStyle))
                        CancelVrPrompt();
                    break;
                case UiState.AwaitHeadConfirm:
                    GUI.Label(new Rect(610f, 320f, 700f, 210f),
                        "请将手机放入 VR 盒子并戴好，平视前方后保持静止。\n头部静止约半秒后会自动开始校准，\n也可以按手柄 A 键（或点击下面按钮）立即开始。", bodyStyle);
                    if (GUI.Button(new Rect(690f, 570f, 540f, 72f), "立即开始（A）", buttonStyle))
                        StartCoroutine(EnterVrMode());
                    if (GUI.Button(new Rect(835f, 700f, 250f, 62f), "取消", buttonStyle))
                        CancelVrPrompt();
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
