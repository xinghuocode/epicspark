using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SparkCode.Gameplay
{
    /// <summary>
    /// World-space story UI: aimable star lore cards, the bridge dialogue, and the
    /// wall obelisk exit confirmation. Both left and right mouse buttons activate buttons.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class StarTempleWorldUiController : MonoBehaviour
    {
        public static bool PointerOverUiThisFrame { get; private set; }

        [Header("Scene references")]
        public StarTemplePlayerController player;
        public Camera playerCamera;
        public Transform starPivot;
        public Transform bridgeAnchor;
        public Transform exitAnchor;
        public Transform uiRoot;
        public Font uiFont;

        [Header("Lore card")]
        public GameObject loreCanvas;
        public RectTransform lorePanelRect;
        public Text loreTitle;
        public Text loreBody;
        public RawImage loreImage;

        [Header("Bridge epic dialogue")]
        public GameObject bridgeCanvas;
        public RectTransform bridgePanelRect;
        public GameObject bridgeRootPage;
        public GameObject epicPage;
        public GameObject settingsPage;
        public Text bridgeTitle;

        [Header("Exit confirmation")]
        public GameObject exitCanvas;
        public RectTransform exitPanelRect;

        [Header("Input distances")]
        public float bridgeOpenDistance = 6.5f;
        public float bridgeCloseDistance = 8.0f;
        public float exitOpenDistance = 0.9f;
        public float exitCloseDistance = 1.6f;
        public float loreAimDistance = 25f;

        [Header("Star proximity")]
        public float starLowerDistance = 8.0f;
        public float starRaiseDistance = 10.0f;
        public float starDropHeight = 1.25f;
        public float starDropSpeed = 1.2f;

        [Header("Lore focus")]
        public float loreFocusLift = 1.6f;
        public float loreFocusTransition = 0.55f;

        public List<StarTempleWorldUiButton> buttons = new List<StarTempleWorldUiButton>();

        int bridgePage;
        bool bridgeInside;
        bool bridgeDismissed;
        bool exitInside;
        bool exitDismissed;
        bool starNear;
        bool starBaseCaptured;
        float starDropOffset;
        Vector3 starBasePosition;
        StarTempleLorePoint activeLore;
        StarTempleLorePoint focusedLore;
        StarTempleWorldUiButton hoveredButton;
        SparkCode.TempleShowcaseController showcase;
        bool loreFocused;
        float loreFocusAmount;

        void Awake()
        {
            if (player == null) player = FindObjectOfType<StarTemplePlayerController>();
            if (playerCamera == null && player != null) playerCamera = player.playerCamera;
            if (starPivot == null)
            {
                var pivot = GameObject.Find("Spark Pivot");
                if (pivot != null) starPivot = pivot.transform;
            }
            if (buttons == null) buttons = new List<StarTempleWorldUiButton>();
            buttons.RemoveAll(b => b == null);
            if (buttons.Count == 0) buttons.AddRange(GetComponentsInChildren<StarTempleWorldUiButton>(true));

            ApplyCjkFont();
            if (loreCanvas != null) loreCanvas.SetActive(false);
            if (exitCanvas != null) exitCanvas.SetActive(false);
            if (bridgeCanvas != null) bridgeCanvas.SetActive(false);
            SetBridgePage(0);
            if (starPivot != null)
            {
                starBasePosition = starPivot.position;
                starBaseCaptured = true;
                showcase = starPivot.GetComponentInParent<SparkCode.TempleShowcaseController>();
            }
            FaceBridgeFixed();

            if (starPivot != null)
            {
                foreach (var point in starPivot.GetComponentsInChildren<StarTempleLorePoint>(true))
                    point.ReloadFromFile();
            }
        }

        void OnDisable()
        {
            PointerOverUiThisFrame = false;
            loreFocused = false;
            loreFocusAmount = 0f;
            focusedLore = null;
            if (player != null) player.SetModalFrozen(false);
            if (showcase != null) showcase.SetVerticalOffset(0f);
        }

        void Update()
        {
            PointerOverUiThisFrame = false;
            if (player == null || playerCamera == null || player.IsKeybindingMenuOpen)
            {
                SetHoveredButton(null);
                if (loreCanvas != null) loreCanvas.SetActive(false);
                return;
            }

            UpdateLoreFocusAmount(Time.deltaTime);
            UpdateStarProximity();
            UpdateBridgeProximity();
            UpdateExitProximity();
            FaceBridgeFixed();

            Vector2 screenPoint = GetPointerScreenPoint();

            // The exit confirmation is a centered modal. While it is open only its
            // own two buttons receive input, and the player controller is frozen.
            if (exitCanvas != null && exitCanvas.activeSelf)
            {
                PointerOverUiThisFrame = true;
                var modalHover = FindButton(screenPoint, true);
                SetHoveredButton(modalHover);
                bool modalClicked = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
                if (modalClicked && modalHover != null) HandleAction(modalHover.action);
                HideLore();
                return;
            }

            bool overBridge = bridgeCanvas != null && bridgeCanvas.activeSelf && Contains(bridgePanelRect, screenPoint);

            // While a lore card is lifted to the island, it owns the interaction
            // until the player right-clicks it to send it back.
            if (loreFocused || loreFocusAmount > 0f)
            {
                UpdateFocusedLore(screenPoint, overBridge);
                return;
            }

            PointerOverUiThisFrame = overBridge;

            var hover = FindButton(screenPoint);
            SetHoveredButton(hover);
            bool clicked = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
            if (clicked && hover != null) HandleAction(hover.action);

            UpdateLoreGaze(screenPoint, overBridge);
        }

        Vector2 GetPointerScreenPoint()
        {
            if (player != null && player.CursorVisible)
                return new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        bool Contains(RectTransform rect, Vector2 screenPoint)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy) return false;
            var canvas = rect.GetComponentInParent<Canvas>();
            Camera eventCamera = null;
            if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                eventCamera = canvas != null && canvas.worldCamera != null ? canvas.worldCamera : playerCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, eventCamera);
        }

        StarTempleWorldUiButton FindButton(Vector2 screenPoint, bool exitOnly = false)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                if (button == null || !button.isActiveAndEnabled) continue;
                if (exitOnly && button.action != StarTempleUiAction.ExitYes &&
                    button.action != StarTempleUiAction.ExitNo) continue;
                var rect = button.transform as RectTransform;
                if (Contains(rect, screenPoint)) return button;
            }
            return null;
        }

        void SetHoveredButton(StarTempleWorldUiButton button)
        {
            if (hoveredButton != null && hoveredButton != button) hoveredButton.SetHovered(false);
            hoveredButton = button;
            if (hoveredButton != null) hoveredButton.SetHovered(true);
        }

        void HandleAction(StarTempleUiAction action)
        {
            switch (action)
            {
                case StarTempleUiAction.Back:
                    if (bridgePage > 0)
                    {
                        SetBridgePage(0);
                    }
                    else
                    {
                        bridgeDismissed = true;
                        if (bridgeCanvas != null) bridgeCanvas.SetActive(false);
                    }
                    break;
                case StarTempleUiAction.EnterEpic:
                    SetBridgePage(1);
                    break;
                case StarTempleUiAction.Settings:
                    SetBridgePage(2);
                    break;
                case StarTempleUiAction.ExitYes:
                    CloseExitModal();
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                    break;
                case StarTempleUiAction.ExitNo:
                    exitDismissed = true;
                    CloseExitModal();
                    break;
            }
        }

        void OpenExitModal()
        {
            if (exitCanvas != null) exitCanvas.SetActive(true);
            if (player != null) player.SetModalFrozen(true);
            Canvas.ForceUpdateCanvases();
        }

        void CloseExitModal()
        {
            if (exitCanvas != null) exitCanvas.SetActive(false);
            if (player != null) player.SetModalFrozen(false);
        }

        void SetBridgePage(int page)
        {
            bridgePage = Mathf.Clamp(page, 0, 2);
            if (bridgeRootPage != null) bridgeRootPage.SetActive(bridgePage == 0);
            if (epicPage != null) epicPage.SetActive(bridgePage == 1);
            if (settingsPage != null) settingsPage.SetActive(bridgePage == 2);
            if (bridgeTitle != null)
                bridgeTitle.text = bridgePage == 0 ? "星火史诗" : (bridgePage == 1 ? "进入史诗..." : "设置");
        }

        void UpdateBridgeProximity()
        {
            if (bridgeAnchor == null) return;
            float distance = FlatDistance(player.transform.position, bridgeAnchor.position);
            if (!bridgeInside && distance <= bridgeOpenDistance)
            {
                bridgeInside = true;
                if (!bridgeDismissed)
                {
                    SetBridgePage(0);
                    if (bridgeCanvas != null) bridgeCanvas.SetActive(true);
                }
            }
            else if (bridgeInside && distance > bridgeCloseDistance)
            {
                bridgeInside = false;
                bridgeDismissed = false;
                if (bridgeCanvas != null) bridgeCanvas.SetActive(false);
            }
        }

        void UpdateExitProximity()
        {
            if (exitAnchor == null) return;
            float distance = FlatDistance(player.transform.position, exitAnchor.position);
            if (!exitInside && distance <= exitOpenDistance)
            {
                exitInside = true;
                if (!exitDismissed) OpenExitModal();
            }
            else if (exitInside && distance > exitCloseDistance)
            {
                exitInside = false;
                exitDismissed = false;
                if (exitCanvas != null && exitCanvas.activeSelf) CloseExitModal();
            }
        }

        void UpdateStarProximity()
        {
            UpdateStarProximity(Time.deltaTime);
        }

        void UpdateStarProximity(float deltaTime)
        {
            if (starPivot == null) return;
            if (!starBaseCaptured)
            {
                starBasePosition = starPivot.position;
                starBaseCaptured = true;
            }

            float distance = FlatDistance(player.transform.position, starBasePosition);
            if (!starNear && distance <= starLowerDistance) starNear = true;
            else if (starNear && distance >= starRaiseDistance) starNear = false;

            float target = starNear ? -starDropHeight : 0f;
            starDropOffset = Mathf.MoveTowards(starDropOffset, target, starDropSpeed * deltaTime);

            // The imported temple also drives the same pivot for its idle bob.
            // Feed our combined offset into that controller instead of writing the
            // pivot directly, otherwise the bob would overwrite it every frame.
            float totalOffset = starDropOffset + loreFocusAmount * loreFocusLift;
            if (showcase != null && showcase.isActiveAndEnabled)
                showcase.SetVerticalOffset(totalOffset);
            else
                starPivot.position = starBasePosition + Vector3.up * totalOffset;
        }

        void UpdateLoreFocusAmount(float deltaTime)
        {
            float target = loreFocused ? 1f : 0f;
            float step = deltaTime / Mathf.Max(0.01f, loreFocusTransition);
            loreFocusAmount = Mathf.MoveTowards(loreFocusAmount, target, step);
        }

        public void BeginLoreFocus(StarTempleLorePoint point)
        {
            if (point == null) return;
            focusedLore = point;
            loreFocused = true;
            BindLore(point);
            if (loreCanvas != null) loreCanvas.SetActive(true);
        }

        public void EndLoreFocus()
        {
            loreFocused = false;
        }

        void UpdateFocusedLore(Vector2 screenPoint, bool overBridge)
        {
            if (loreCanvas == null || playerCamera == null || focusedLore == null)
            {
                HideLore();
                return;
            }

            if (!loreCanvas.activeSelf) loreCanvas.SetActive(true);
            if (activeLore != focusedLore) BindLore(focusedLore);

            var cameraTransform = playerCamera.transform;
            Vector3 nearPoint = focusedLore.transform.position +
                cameraTransform.right * 1.25f + cameraTransform.up * 0.55f;
            Vector3 islandPosition = starPivot != null
                ? new Vector3(starPivot.position.x, cameraTransform.position.y, starPivot.position.z)
                : new Vector3(nearPoint.x, cameraTransform.position.y, nearPoint.z);
            float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(loreFocusAmount));
            loreCanvas.transform.position = Vector3.Lerp(nearPoint, islandPosition, blend);
            FaceTowardCamera(loreCanvas.transform);

            bool overCard = Contains(lorePanelRect, screenPoint);
            PointerOverUiThisFrame = overBridge || overCard;

            if (loreFocused && overCard && Input.GetMouseButtonDown(1))
            {
                EndLoreFocus();
                return;
            }

            if (!loreFocused && loreFocusAmount <= 0.001f)
            {
                focusedLore = null;
                HideLore();
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // The bridge menu is a fixed sign facing the white exit obelisk, not a
        // billboard that follows the player.
        void FaceBridgeFixed()
        {
            if (bridgeCanvas == null || exitAnchor == null) return;
            Vector3 fromTarget = bridgeCanvas.transform.position - exitAnchor.position;
            fromTarget.y = 0f;
            if (fromTarget.sqrMagnitude > 0.001f)
                bridgeCanvas.transform.rotation = Quaternion.LookRotation(fromTarget.normalized, Vector3.up);
        }

        void FaceTowardCamera(Transform panel)
        {
            if (playerCamera == null) return;
            Vector3 fromCamera = panel.position - playerCamera.transform.position;
            fromCamera.y = 0f;
            if (fromCamera.sqrMagnitude > 0.001f)
                panel.rotation = Quaternion.LookRotation(fromCamera.normalized, Vector3.up);
        }

        void UpdateLoreGaze(Vector2 screenPoint, bool pointerOverModalPanel)
        {
            if (pointerOverModalPanel)
            {
                HideLore();
                return;
            }

            if (loreCanvas == null || playerCamera == null) return;
            Ray ray = playerCamera.ScreenPointToRay(screenPoint);
            RaycastHit[] hits = Physics.RaycastAll(ray, loreAimDistance, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            StarTempleLorePoint found = null;
            for (int i = 0; i < hits.Length; i++)
            {
                var point = hits[i].collider.GetComponentInParent<StarTempleLorePoint>();
                if (point != null)
                {
                    found = point;
                    break;
                }
                if (!hits[i].collider.isTrigger) break;
            }

            if (found == null)
            {
                HideLore();
                return;
            }

            if (activeLore != found) BindLore(found);
            loreCanvas.SetActive(true);
            var pointPosition = found.transform.position;
            var offset = playerCamera.transform.right * 1.25f + playerCamera.transform.up * 0.55f;
            loreCanvas.transform.position = pointPosition + offset;
            FaceTowardCamera(loreCanvas.transform);

            if (Input.GetMouseButtonDown(1))
            {
                BeginLoreFocus(found);
                // Consume this click so the player does not also interact/attack.
                PointerOverUiThisFrame = true;
            }
        }

        void BindLore(StarTempleLorePoint point)
        {
            if (point == null || loreTitle == null || loreBody == null || loreImage == null) return;
            activeLore = point;
            loreTitle.text = point.Title;
            loreBody.text = point.Body;
            loreImage.texture = point.CurrentImage;
            loreImage.enabled = point.CurrentImage != null;
        }

        void HideLore()
        {
            activeLore = null;
            if (loreCanvas != null) loreCanvas.SetActive(false);
        }

        public void ApplyCjkFont()
        {
            if (uiFont == null) return;
            foreach (var text in GetComponentsInChildren<Text>(true))
                text.font = uiFont;
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>Editor-only preview of the lifted, island-centered lore card.</summary>
        public void EditorPreviewLoreFocus()
        {
            if (loreCanvas == null || playerCamera == null || starPivot == null) return;
            ApplyCjkFont();
            if (bridgeCanvas != null) bridgeCanvas.SetActive(false);
            if (exitCanvas != null) exitCanvas.SetActive(false);
            var points = starPivot.GetComponentsInChildren<StarTempleLorePoint>(true);
            if (points == null || points.Length == 0) return;
            points[0].ReloadFromFile();
            BeginLoreFocus(points[0]);
            loreFocusAmount = 1f;
            UpdateFocusedLore(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), false);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>Editor-only visual preview used by the batch screenshot tool.</summary>
        public void EditorPreview(int page, bool showLore, bool showExit)
        {
            ApplyCjkFont();
            Canvas.ForceUpdateCanvases();
            if (bridgeCanvas != null) bridgeCanvas.SetActive(page >= 0);
            SetBridgePage(Mathf.Max(0, page));
            FaceBridgeFixed();
            if (loreCanvas != null) loreCanvas.SetActive(showLore);
            if (showLore)
            {
                var points = starPivot != null
                    ? starPivot.GetComponentsInChildren<StarTempleLorePoint>(true) : new StarTempleLorePoint[0];
                if (points.Length > 0)
                {
                    activeLore = points[0];
                    activeLore.ReloadFromFile();
                    loreTitle.text = activeLore.Title;
                    loreBody.text = activeLore.Body;
                    loreImage.texture = activeLore.CurrentImage;
                    loreImage.enabled = activeLore.CurrentImage != null;
                    loreCanvas.transform.position = activeLore.transform.position +
                        (playerCamera != null ? playerCamera.transform.right * 1.25f : Vector3.right * 1.25f) +
                        Vector3.up * 0.55f;
                    FaceTowardCamera(loreCanvas.transform);
                }
            }
            if (exitCanvas != null) exitCanvas.SetActive(showExit);
            Canvas.ForceUpdateCanvases();
        }
    }
}
