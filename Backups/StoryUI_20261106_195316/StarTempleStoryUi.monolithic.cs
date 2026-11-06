using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace SparkCode.Gameplay
{
    [Serializable]
    public class StarTempleLoreEntry
    {
        public string id;
        public string title;
        public string body;
        public string imageFile;
    }

    [Serializable]
    public class StarTempleLoreManifest
    {
        public int version = 1;
        public string note = "Edit this file and add images to the images folder to customize the floating lore cards.";
        public List<StarTempleLoreEntry> entries = new List<StarTempleLoreEntry>();
    }

    /// <summary>
    /// File-backed lore store. Content lives in Application.persistentDataPath so it
    /// survives scene and application restarts. Images are copied into the same folder.
    /// </summary>
    public static class StarTempleLoreStore
    {
        static readonly object Gate = new object();
        static string cachedDirectory;

        public static string DataDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(cachedDirectory))
                    cachedDirectory = Path.Combine(Application.persistentDataPath, "StarTempleLore");
                return cachedDirectory;
            }
        }

        public static string ManifestPath { get { return Path.Combine(DataDirectory, "lore.json"); } }
        public static string ImagesDirectory { get { return Path.Combine(DataDirectory, "images"); } }

        public static StarTempleLoreManifest LoadManifest()
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ImagesDirectory);
                var manifest = ReadManifest();
                if (manifest == null)
                {
                    manifest = new StarTempleLoreManifest();
                    SaveManifest(manifest);
                }
                return manifest;
            }
        }

        static StarTempleLoreManifest ReadManifest()
        {
            if (!File.Exists(ManifestPath)) return null;
            try
            {
                string json = File.ReadAllText(ManifestPath);
                var data = JsonUtility.FromJson<StarTempleLoreManifest>(json);
                if (data == null) return null;
                if (data.entries == null) data.entries = new List<StarTempleLoreEntry>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarTempleLore] Could not read " + ManifestPath + ": " + e.Message);
                return null;
            }
        }

        public static void SaveManifest(StarTempleLoreManifest manifest)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ImagesDirectory);
                if (manifest.entries == null) manifest.entries = new List<StarTempleLoreEntry>();
                string json = JsonUtility.ToJson(manifest, true);
                string temp = ManifestPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
                File.Move(temp, ManifestPath);
            }
        }

        public static StarTempleLoreEntry EnsureEntry(string id, string defaultTitle, string defaultBody)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                var entry = manifest.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new StarTempleLoreEntry
                    {
                        id = id,
                        title = string.IsNullOrEmpty(defaultTitle) ? id : defaultTitle,
                        body = defaultBody ?? string.Empty,
                        imageFile = string.Empty
                    };
                    manifest.entries.Add(entry);
                    SaveManifest(manifest);
                }
                return entry;
            }
        }

        public static bool TryGetEntry(string id, out StarTempleLoreEntry entry)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                entry = manifest.entries.Find(e => e.id == id);
                return entry != null;
            }
        }

        public static void SetContent(string id, string title, string body)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                var entry = manifest.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new StarTempleLoreEntry { id = id };
                    manifest.entries.Add(entry);
                }
                entry.title = title ?? string.Empty;
                entry.body = body ?? string.Empty;
                SaveManifest(manifest);
            }
        }

        public static void SetImageFile(string id, string imageFile)
        {
            lock (Gate)
            {
                var manifest = LoadManifest();
                var entry = manifest.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new StarTempleLoreEntry { id = id };
                    manifest.entries.Add(entry);
                }
                entry.imageFile = imageFile ?? string.Empty;
                SaveManifest(manifest);
            }
        }

        public static string ImportImage(string id, string sourceFile)
        {
            if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile)) return null;
            string extension = Path.GetExtension(sourceFile);
            if (string.IsNullOrEmpty(extension)) extension = ".png";
            string safeId = Sanitize(id);
            string filename = safeId + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + extension.ToLowerInvariant();
            Directory.CreateDirectory(ImagesDirectory);
            string target = Path.Combine(ImagesDirectory, filename);
            File.Copy(sourceFile, target, true);
            SetImageFile(id, filename);
            return target;
        }

        public static string SavePng(string id, byte[] pngData)
        {
            if (pngData == null || pngData.Length == 0) return null;
            Directory.CreateDirectory(ImagesDirectory);
            string filename = Sanitize(id) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".png";
            string target = Path.Combine(ImagesDirectory, filename);
            File.WriteAllBytes(target, pngData);
            SetImageFile(id, filename);
            return target;
        }

        public static Texture2D LoadImage(StarTempleLoreEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.imageFile)) return null;
            string path = Path.IsPathRooted(entry.imageFile)
                ? entry.imageFile : Path.Combine(ImagesDirectory, entry.imageFile);
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "LoreImage_" + entry.id;
                texture.wrapMode = TextureWrapMode.Clamp;
                return texture;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarTempleLore] Could not load image " + path + ": " + e.Message);
                return null;
            }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "lore";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }
    }

    /// <summary>Marks one large floating sparkle as a lore card target.</summary>
    public class StarTempleLorePoint : MonoBehaviour
    {
        public string pointId = "lore_01";
        public string defaultTitle = "星火记忆";
        [TextArea(3, 9)] public string defaultBody = "这段记忆尚未书写。";
        public Texture2D defaultImage;

        public string Title { get; private set; }
        public string Body { get; private set; }
        public Texture2D CurrentImage { get; private set; }
        public string PersistentImagePath { get; private set; }

        Texture2D loadedImage;
        StarTempleLoreEntry entry;

        void Awake()
        {
            ReloadFromFile();
        }

        void OnDestroy()
        {
            if (loadedImage != null) Destroy(loadedImage);
        }

        public void SetContent(string title, string body)
        {
            StarTempleLoreStore.SetContent(pointId, title, body);
            ReloadFromFile();
        }

        public void SetImage(Texture2D image)
        {
            if (image == null)
            {
                StarTempleLoreStore.SetImageFile(pointId, string.Empty);
            }
            else
            {
                StarTempleLoreStore.SavePng(pointId, image.EncodeToPNG());
            }
            ReloadFromFile();
        }

        public bool SetImageFromFile(string sourcePath)
        {
            string target = StarTempleLoreStore.ImportImage(pointId, sourcePath);
            if (string.IsNullOrEmpty(target)) return false;
            ReloadFromFile();
            return true;
        }

        public void SaveToFile()
        {
            StarTempleLoreStore.SetContent(pointId, Title, Body);
        }

        public void ReloadFromFile()
        {
            entry = StarTempleLoreStore.EnsureEntry(pointId, defaultTitle, defaultBody);
            Title = string.IsNullOrEmpty(entry.title) ? defaultTitle : entry.title;
            Body = string.IsNullOrEmpty(entry.body) ? defaultBody : entry.body;

            var next = StarTempleLoreStore.LoadImage(entry);
            if (loadedImage != null && loadedImage != next)
            {
                if (Application.isPlaying) Destroy(loadedImage);
                else DestroyImmediate(loadedImage);
                loadedImage = null;
            }
            if (next != null) loadedImage = next;
            CurrentImage = next != null ? next : defaultImage;
            PersistentImagePath = next != null
                ? Path.Combine(StarTempleLoreStore.ImagesDirectory, entry.imageFile) : string.Empty;
        }

        /// <summary>Used by the editor scene builder to create the initial persistent file.</summary>
        public void EditorInitializePersistence()
        {
            StarTempleLoreStore.EnsureEntry(pointId, defaultTitle, defaultBody);
            ReloadFromFile();
        }

        public StarTempleLoreEntry GetEntry()
        {
            return entry;
        }
    }

    public enum StarTempleUiAction
    {
        Back,
        EnterEpic,
        Settings,
        ExitYes,
        ExitNo
    }

    /// <summary>A UGUI button without EventSystem dependencies; the UI controller tests its rect.</summary>
    public class StarTempleWorldUiButton : MonoBehaviour
    {
        public StarTempleUiAction action;
        public Image background;
        public Text label;
        public Color normalColor = new Color(0.08f, 0.11f, 0.15f, 0.96f);
        public Color hoverColor = new Color(0.30f, 0.22f, 0.09f, 1f);

        public void SetHovered(bool hovered)
        {
            if (background != null)
                background.color = hovered ? hoverColor : normalColor;
        }
    }

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
        public float exitOpenDistance = 2.8f;
        public float exitCloseDistance = 4.2f;
        public float loreAimDistance = 25f;

        public List<StarTempleWorldUiButton> buttons = new List<StarTempleWorldUiButton>();

        int bridgePage;
        bool bridgeInside;
        bool bridgeDismissed;
        bool exitInside;
        bool exitDismissed;
        StarTempleLorePoint activeLore;
        StarTempleWorldUiButton hoveredButton;
        Font cjkFont;

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
            loreCanvas.SetActive(false);
            exitCanvas.SetActive(false);
            bridgeCanvas.SetActive(false);
            SetBridgePage(0);

            if (starPivot != null)
            {
                foreach (var point in starPivot.GetComponentsInChildren<StarTempleLorePoint>(true))
                    point.ReloadFromFile();
            }
        }

        void OnDisable()
        {
            PointerOverUiThisFrame = false;
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

            UpdateBridgeProximity();
            UpdateExitProximity();
            FaceBridge();
            FaceExit();

            Vector2 screenPoint = GetPointerScreenPoint();
            bool overBridge = bridgeCanvas.activeSelf && Contains(bridgePanelRect, screenPoint);
            bool overExit = exitCanvas.activeSelf && Contains(exitPanelRect, screenPoint);
            PointerOverUiThisFrame = overBridge || overExit;

            var hover = FindButton(screenPoint);
            SetHoveredButton(hover);
            bool clicked = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
            if (clicked && hover != null) HandleAction(hover.action);

            UpdateLoreGaze(screenPoint, overBridge || overExit);
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
            return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, playerCamera);
        }

        StarTempleWorldUiButton FindButton(Vector2 screenPoint)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                if (button == null || !button.isActiveAndEnabled) continue;
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
                        bridgeCanvas.SetActive(false);
                    }
                    break;
                case StarTempleUiAction.EnterEpic:
                    SetBridgePage(1);
                    break;
                case StarTempleUiAction.Settings:
                    SetBridgePage(2);
                    break;
                case StarTempleUiAction.ExitYes:
                    exitCanvas.SetActive(false);
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                    break;
                case StarTempleUiAction.ExitNo:
                    exitDismissed = true;
                    exitCanvas.SetActive(false);
                    break;
            }
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
                    bridgeCanvas.SetActive(true);
                }
            }
            else if (bridgeInside && distance > bridgeCloseDistance)
            {
                bridgeInside = false;
                bridgeDismissed = false;
                bridgeCanvas.SetActive(false);
            }
        }

        void UpdateExitProximity()
        {
            if (exitAnchor == null) return;
            float distance = FlatDistance(player.transform.position, exitAnchor.position);
            if (!exitInside && distance <= exitOpenDistance)
            {
                exitInside = true;
                if (!exitDismissed) exitCanvas.SetActive(true);
            }
            else if (exitInside && distance > exitCloseDistance)
            {
                exitInside = false;
                exitDismissed = false;
                exitCanvas.SetActive(false);
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        void FaceBridge()
        {
            if (bridgeCanvas != null && bridgeCanvas.activeSelf)
                FaceTowardCamera(bridgeCanvas.transform);
        }

        void FaceExit()
        {
            if (exitCanvas != null && exitCanvas.activeSelf && exitAnchor != null)
            {
                exitCanvas.transform.position = exitAnchor.position + Vector3.up * 3.1f;
                FaceTowardCamera(exitCanvas.transform);
            }
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

            if (activeLore != found)
            {
                activeLore = found;
                loreTitle.text = found.Title;
                loreBody.text = found.Body;
                loreImage.texture = found.CurrentImage;
                loreImage.enabled = found.CurrentImage != null;
            }
            loreCanvas.SetActive(true);
            var pointPosition = found.transform.position;
            var offset = playerCamera.transform.right * 1.25f + playerCamera.transform.up * 0.55f;
            loreCanvas.transform.position = pointPosition + offset;
            FaceTowardCamera(loreCanvas.transform);
        }

        void HideLore()
        {
            activeLore = null;
            if (loreCanvas != null) loreCanvas.SetActive(false);
        }

        public void ApplyCjkFont()
        {
            if (cjkFont == null)
            {
                cjkFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" }, 24);
            }
            if (cjkFont == null) return;
            foreach (var text in GetComponentsInChildren<Text>(true))
                text.font = cjkFont;
        }

        /// <summary>Editor-only visual preview used by the batch screenshot tool.</summary>
        public void EditorPreview(int page, bool showLore, bool showExit)
        {
            ApplyCjkFont();
            bridgeCanvas.SetActive(page >= 0);
            SetBridgePage(Mathf.Max(0, page));
            if (page >= 0) FaceBridge();
            loreCanvas.SetActive(showLore);
            if (showLore)
            {
                var points = starPivot != null
                    ? starPivot.GetComponentsInChildren<StarTempleLorePoint>(true) : new StarTempleLorePoint[0];
                if (points.Length > 0)
                {
                    activeLore = points[0];
                    loreTitle.text = activeLore.Title;
                    loreBody.text = activeLore.Body;
                    loreImage.texture = activeLore.CurrentImage;
                    loreCanvas.transform.position = activeLore.transform.position +
                        (playerCamera != null ? playerCamera.transform.right * 1.25f : Vector3.right * 1.25f) +
                        Vector3.up * 0.55f;
                    FaceTowardCamera(loreCanvas.transform);
                }
            }
            exitCanvas.SetActive(showExit);
            if (showExit) FaceExit();
        }
    }
}
