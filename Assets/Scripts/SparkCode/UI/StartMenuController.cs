using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using SparkCode.UI;

namespace SparkCode
{
    public enum MenuAction { Start, Settings, About, Quit, Back }
    public enum RowKind { Quality, Volume, Fullscreen }

    /// <summary>
    /// Drives the baked-sprite start menu: hover / click / keyboard navigation,
    /// settings &amp; about panels, the start sequence and ESC return.
    /// Every visual state is also directly settable through PrepareCapture so
    /// the editor capture script can render any state without entering play mode.
    /// </summary>
    public class StartMenuController : MonoBehaviour
    {
        [System.Serializable]
        public class MenuPanel
        {
            public CanvasGroup group;
            public RectTransform rect;
            public Vector2 shownPos;
            public Vector2 hiddenPos;
        }

        const string KeyQuality = "SparkCode.Quality";
        const string KeyVolume = "SparkCode.Volume";
        const string KeyFullscreen = "SparkCode.Fullscreen";

        [Header("Scene")]
        public OrbitCamera orbit;

        [Header("Menu")]
        public CanvasGroup menuGroup;
        public RectTransform menuRoot;
        public MenuButton[] mainButtons;

        [Header("Caption")]
        public CanvasGroup captionGroup;
        public RectTransform captionRoot;

        [Header("Effects")]
        public Image flash;
        public GameObject panelBlocker;

        [Header("Panels")]
        public MenuPanel settingsPanel;
        public MenuPanel aboutPanel;

        [Header("Settings values (0..n)")]
        public MenuRow[] settingsRows;
        public int qualityIndex = 2;
        public int volumeIndex = 2;
        public int fullscreenIndex = 0;

        public static readonly string[] QualityNames = { "Performant", "Balanced", "High Fidelity" };
        public static readonly float[] VolumeValues = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        enum State { Main, Settings, About, Started }

        State _state = State.Main;
        bool _interactable = true;
        int _selection;
        int _panelRow;
        Coroutine _routine;
        bool _instantMode;

        public bool CanInteract { get { return _interactable && _state == State.Main; } }
        public bool IsStarted { get { return _state == State.Started; } }
        public bool IsPanelOpen { get { return _state == State.Settings || _state == State.About; } }

        // ------------------------------------------------------------------ lifecycle
        void Start()
        {
            LoadSettings();
            ApplyQuality();
            ApplyVolume();
            ApplyFullscreen();
            RefreshRows();
            if (orbit != null) orbit.SetPaused(false);
        }

        void Update()
        {
            bool esc = Input.GetKeyDown(KeyCode.Escape);
            if (esc)
            {
                if (_state == State.Settings || _state == State.About || _state == State.Started)
                {
                    BackToMain();
                    return;
                }
            }

            if (_state == State.Main && _interactable)
            {
                if (Input.GetKeyDown(KeyCode.DownArrow)) MoveSelection(1);
                else if (Input.GetKeyDown(KeyCode.UpArrow)) MoveSelection(-1);
                else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) ActivateSelection();
            }
            else if (IsPanelOpen)
            {
                if (Input.GetKeyDown(KeyCode.DownArrow)) MovePanelRow(1);
                else if (Input.GetKeyDown(KeyCode.UpArrow)) MovePanelRow(-1);
                else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    MenuRow row = CurrentRow();
                    if (row != null) CycleRow(row, 1);
                }
            }
        }

        // ------------------------------------------------------------------ public API
        public bool ButtonInteractable(MenuButton button)
        {
            if (button == null) return false;
            if (button.alwaysInteractable)
                return _state == State.Settings || _state == State.About || _state == State.Main;
            return _state == State.Main && _interactable;
        }

        public void OnMenuAction(MenuAction action)
        {
            switch (action)
            {
                case MenuAction.Start: StartGame(); break;
                case MenuAction.Settings: ShowSettings(); break;
                case MenuAction.About: ShowAbout(); break;
                case MenuAction.Quit: QuitGame(); break;
                case MenuAction.Back: BackToMain(); break;
            }
        }

        public void StartGame()
        {
            if (_state == State.Started) return;
            _state = State.Started;
            _interactable = false;
            ClearForcedHover();
            ClosePanelsImmediate();
            if (panelBlocker != null) panelBlocker.SetActive(false);
            if (orbit != null) orbit.FocusOnSpark();
            TryFlare();

            if (_routine != null) StopCoroutine(_routine);
            if (isActiveAndEnabled) _routine = StartCoroutine(StartRoutine());
            else SetStartedImmediate();
        }

        public void ShowSettings()
        {
            if (_state == State.Started) return;
            _state = State.Settings;
            _interactable = false;
            _panelRow = 0;
            ClearForcedHover();
            DimMenu(true);
            SetCaption(false);
            if (flash != null) flash.color = new Color(1f, 1f, 1f, 0f);
            ClosePanel(aboutPanel);
            OpenPanel(settingsPanel);
            RefreshRows();
            HighlightPanelRow(0);
        }

        public void ShowAbout()
        {
            if (_state == State.Started) return;
            _state = State.About;
            _interactable = false;
            _panelRow = 0;
            ClearForcedHover();
            DimMenu(true);
            SetCaption(false);
            if (flash != null) flash.color = new Color(1f, 1f, 1f, 0f);
            ClosePanel(settingsPanel);
            OpenPanel(aboutPanel);
            HighlightPanelRow(0);
        }

        public void BackToMain()
        {
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
            _state = State.Main;
            _interactable = true;
            if (orbit != null) orbit.ResetView();
            if (flash != null) flash.color = new Color(1f, 1f, 1f, 0f);
            if (panelBlocker != null) panelBlocker.SetActive(false);
            ClosePanelsImmediate();
            SetCaption(false);
            SetMenuVisible(1f, true);
            RefreshSelectionVisuals();
        }

        public void QuitGame()
        {
#if UNITY_EDITOR
            if (Application.isPlaying) UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Force the menu into a displayable state (works in edit mode, no coroutines).</summary>
        public void PrepareCapture(string state, int hoverIndex = -1)
        {
            StopAllCoroutines();
            _routine = null;
            _instantMode = true;
            string s = string.IsNullOrEmpty(state) ? "main" : state.ToLowerInvariant();

            if (flash != null) flash.color = new Color(1f, 1f, 1f, 0f);
            if (panelBlocker != null) panelBlocker.SetActive(false);
            ClosePanelsImmediate();
            SetCaption(false);
            ClearForcedHover();

            switch (s)
            {
                case "settings":
                    _state = State.Settings;
                    _interactable = false;
                    DimMenu(true);
                    OpenPanelImmediate(settingsPanel);
                    RefreshRows();
                    if (settingsPanel != null && settingsPanel.group != null)
                        settingsPanel.group.alpha = 1f;
                    break;
                case "about":
                    _state = State.About;
                    _interactable = false;
                    DimMenu(true);
                    OpenPanelImmediate(aboutPanel);
                    break;
                case "started":
                    _state = State.Started;
                    _interactable = false;
                    SetMenuVisible(0f, false);
                    SetCaption(true);
                    break;
                default:
                    _state = State.Main;
                    _interactable = true;
                    SetMenuVisible(1f, true);
                    _selection = 0;
                    if (hoverIndex >= 0 && mainButtons != null && hoverIndex < mainButtons.Length)
                    {
                        _selection = hoverIndex;
                        for (int i = 0; i < mainButtons.Length; i++)
                            if (mainButtons[i] != null) mainButtons[i].SetHoverForced(i == hoverIndex);
                    }
                    break;
            }
            _instantMode = false;
            Canvas.ForceUpdateCanvases();
        }

        // ------------------------------------------------------------------ keyboard
        void MoveSelection(int dir)
        {
            if (mainButtons == null || mainButtons.Length == 0) return;
            _selection = (_selection + dir + mainButtons.Length) % mainButtons.Length;
            RefreshSelectionVisuals();
        }

        void ActivateSelection()
        {
            if (mainButtons == null || mainButtons.Length == 0) return;
            _selection = Mathf.Clamp(_selection, 0, mainButtons.Length - 1);
            MenuButton b = mainButtons[_selection];
            if (b != null) b.Pulse();
            OnMenuAction(b != null ? b.action : MenuAction.Start);
        }

        public void NotifyPointerHover(int index, MenuButton button)
        {
            if (mainButtons == null) return;
            if (index >= 0 && index < mainButtons.Length) _selection = index;
        }

        void RefreshSelectionVisuals()
        {
            if (mainButtons == null) return;
            for (int i = 0; i < mainButtons.Length; i++)
                if (mainButtons[i] != null) mainButtons[i].SetHoverForced(i == _selection);
        }

        void ClearForcedHover()
        {
            if (mainButtons == null) return;
            for (int i = 0; i < mainButtons.Length; i++)
                if (mainButtons[i] != null) mainButtons[i].ClearForced();
        }

        // ------------------------------------------------------------------ settings
        public void LoadSettings()
        {
            qualityIndex = Mathf.Clamp(PlayerPrefs.GetInt(KeyQuality, FindCurrentQuality()), 0, 2);
            volumeIndex = Mathf.Clamp(PlayerPrefs.GetInt(KeyVolume, 2), 0, VolumeValues.Length - 1);
            fullscreenIndex = Mathf.Clamp(PlayerPrefs.GetInt(KeyFullscreen, Screen.fullScreen ? 0 : 1), 0, 1);
        }

        static int FindCurrentQuality()
        {
            string current = QualitySettings.names != null && QualitySettings.names.Length > 0
                ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "";
            for (int i = 0; i < QualityNames.Length; i++)
                if (QualityNames[i] == current) return i;
            return Mathf.Clamp(QualitySettings.GetQualityLevel(), 0, 2);
        }

        public void ApplyQuality()
        {
            int target = -1;
            string wanted = QualityNames[Mathf.Clamp(qualityIndex, 0, QualityNames.Length - 1)];
            if (QualitySettings.names != null)
            {
                for (int i = 0; i < QualitySettings.names.Length; i++)
                {
                    if (QualitySettings.names[i] == wanted) { target = i; break; }
                }
            }
            if (target < 0)
                target = Mathf.Clamp(qualityIndex, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
            QualitySettings.SetQualityLevel(target, true);
        }

        public void ApplyVolume()
        {
            AudioListener.volume = VolumeValues[Mathf.Clamp(volumeIndex, 0, VolumeValues.Length - 1)];
        }

        public void ApplyFullscreen()
        {
            Screen.fullScreen = fullscreenIndex == 0;
        }

        public void CycleRow(MenuRow row, int dir)
        {
            if (row == null) return;
            switch (row.kind)
            {
                case RowKind.Quality:
                    qualityIndex = Wrap(qualityIndex + dir, QualityNames.Length);
                    ApplyQuality();
                    PlayerPrefs.SetInt(KeyQuality, qualityIndex);
                    break;
                case RowKind.Volume:
                    volumeIndex = Wrap(volumeIndex + dir, VolumeValues.Length);
                    ApplyVolume();
                    PlayerPrefs.SetInt(KeyVolume, volumeIndex);
                    break;
                case RowKind.Fullscreen:
                    fullscreenIndex = 1 - Mathf.Clamp(fullscreenIndex, 0, 1);
                    ApplyFullscreen();
                    PlayerPrefs.SetInt(KeyFullscreen, fullscreenIndex);
                    break;
            }
            PlayerPrefs.Save();
            RefreshRows();
        }

        static int Wrap(int v, int n)
        {
            if (n <= 1) return 0;
            return ((v % n) + n) % n;
        }

        public void RefreshRows()
        {
            if (settingsRows == null) return;
            for (int i = 0; i < settingsRows.Length; i++)
            {
                MenuRow row = settingsRows[i];
                if (row == null) continue;
                switch (row.kind)
                {
                    case RowKind.Quality: row.SetValue(Mathf.Clamp(qualityIndex, 0, 2)); break;
                    case RowKind.Volume: row.SetValue(Mathf.Clamp(volumeIndex, 0, 4)); break;
                    case RowKind.Fullscreen: row.SetValue(Mathf.Clamp(fullscreenIndex, 0, 1)); break;
                }
            }
        }

        MenuRow CurrentRow()
        {
            if (settingsRows == null || settingsRows.Length == 0) return null;
            _panelRow = Mathf.Clamp(_panelRow, 0, settingsRows.Length - 1);
            return settingsRows[_panelRow];
        }

        void MovePanelRow(int dir)
        {
            if (settingsRows == null || settingsRows.Length == 0) return;
            _panelRow = (_panelRow + dir + settingsRows.Length) % settingsRows.Length;
            HighlightPanelRow(_panelRow);
        }

        void HighlightPanelRow(int index)
        {
            if (settingsRows == null) return;
            for (int i = 0; i < settingsRows.Length; i++)
                if (settingsRows[i] != null) settingsRows[i].SetHighlight(i == index && IsPanelOpen);
        }

        // ------------------------------------------------------------------ visuals
        void DimMenu(bool dim)
        {
            SetMenuVisible(dim ? 0.30f : 1f, !dim);
        }

        void SetMenuVisible(float alpha, bool interactive)
        {
            if (menuGroup != null)
            {
                menuGroup.alpha = alpha;
                menuGroup.blocksRaycasts = interactive;
                menuGroup.interactable = interactive;
            }
        }

        void SetCaption(bool visible)
        {
            if (captionGroup != null)
            {
                captionGroup.alpha = visible ? 1f : 0f;
                captionGroup.blocksRaycasts = false;
                captionGroup.interactable = false;
            }
        }

        void OpenPanel(MenuPanel panel)
        {
            if (panel == null) return;
            if (panelBlocker != null) panelBlocker.SetActive(true);
            if (panel.group != null)
            {
                panel.group.blocksRaycasts = true;
                panel.group.interactable = true;
                panel.group.alpha = 1f;
            }
            if (panel.rect != null) panel.rect.anchoredPosition = panel.shownPos;
            if (Application.isPlaying && !_instantMode && isActiveAndEnabled)
                StartCoroutine(AnimatePanelIn(panel));
        }

        IEnumerator AnimatePanelIn(MenuPanel panel)
        {
            if (panel == null || panel.group == null) yield break;
            float t = 0f;
            const float dur = 0.22f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / dur);
                float e = k * k * (3f - 2f * k);
                if (panel.group != null) panel.group.alpha = e;
                if (panel.rect != null) panel.rect.anchoredPosition = Vector2.Lerp(panel.hiddenPos, panel.shownPos, e);
                yield return null;
            }
            if (panel.group != null) panel.group.alpha = 1f;
            if (panel.rect != null) panel.rect.anchoredPosition = panel.shownPos;
        }

        void OpenPanelImmediate(MenuPanel panel)
        {
            OpenPanel(panel);
        }

        void ClosePanel(MenuPanel panel)
        {
            if (panel == null) return;
            if (panel.group != null)
            {
                panel.group.alpha = 0f;
                panel.group.blocksRaycasts = false;
                panel.group.interactable = false;
            }
            if (panel.rect != null) panel.rect.anchoredPosition = panel.hiddenPos;
        }

        void ClosePanelsImmediate()
        {
            ClosePanel(settingsPanel);
            ClosePanel(aboutPanel);
            if (panelBlocker != null) panelBlocker.SetActive(false);
        }

        void SetStartedImmediate()
        {
            SetMenuVisible(0f, false);
            SetCaption(true);
        }

        IEnumerator StartRoutine()
        {
            SetCaption(false);
            if (panelBlocker != null) panelBlocker.SetActive(false);

            float t = 0f;
            const float fade = 0.9f;
            float startAlpha = menuGroup != null ? menuGroup.alpha : 1f;
            while (t < fade)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / fade);
                if (menuGroup != null) menuGroup.alpha = Mathf.Lerp(startAlpha, 0f, k);
                if (flash != null)
                {
                    float a = Mathf.Sin(k * Mathf.PI) * 0.5f;
                    flash.color = new Color(1f, 0.84f, 0.55f, a);
                }
                yield return null;
            }
            if (menuGroup != null)
            {
                menuGroup.alpha = 0f;
                menuGroup.blocksRaycasts = false;
                menuGroup.interactable = false;
            }
            if (flash != null) flash.color = new Color(1f, 1f, 1f, 0f);

            yield return new WaitForSeconds(2.2f);

            if (captionGroup != null)
            {
                float c = 0f;
                while (c < 0.7f)
                {
                    c += Time.deltaTime;
                    captionGroup.alpha = Mathf.Clamp01(c / 0.7f);
                    yield return null;
                }
                captionGroup.alpha = 1f;
            }
            _routine = null;
        }

        void TryFlare()
        {
            // SparkVisual lives in SparkCode.Spark (owned by another module); reflection
            // keeps this file compiling even before that module lands.
            var behaviours = Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                var mb = behaviours[i];
                if (mb == null) continue;
                System.Type t = mb.GetType();
                if (t.Name != "SparkVisual") continue;
                MethodInfo m = t.GetMethod("Flare", BindingFlags.Public | BindingFlags.Instance,
                    null, System.Type.EmptyTypes, null);
                if (m != null)
                {
                    try { m.Invoke(mb, null); } catch (System.Exception) { }
                }
            }
        }
    }
}
