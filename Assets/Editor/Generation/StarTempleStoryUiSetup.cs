using System;
using System.Collections.Generic;
using System.IO;
using SparkCode.Gameplay;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SparkCode.Generation
{
    /// <summary>
    /// Builds the world-space story UI and the large aimable lore points around the Spark.
    /// The UI deliberately avoids EventSystem so both mouse buttons can activate every button.
    /// </summary>
    public static class StarTempleStoryUiSetup
    {
        const string Generated = "Assets/Generated/BlenderStarTempleURP";
        const string DefaultLorePng = Generated + "/Lore_Default.png";
        const string UnicodeFontPath = Generated + "/Fonts/NotoSansSC-VF.ttf";
        static Font projectFont;
        static readonly Color Panel = new Color(0.018f, 0.028f, 0.045f, 0.94f);
        static readonly Color PanelRaised = new Color(0.045f, 0.064f, 0.092f, 0.98f);
        static readonly Color Gold = new Color(0.94f, 0.68f, 0.25f, 1f);
        static readonly Color Cream = new Color(0.97f, 0.93f, 0.82f, 1f);
        static readonly Color Muted = new Color(0.67f, 0.68f, 0.64f, 1f);
        static readonly Color ButtonNormal = new Color(0.055f, 0.075f, 0.105f, 0.98f);
        static readonly Color ButtonHover = new Color(0.28f, 0.20f, 0.075f, 1f);

        [MenuItem("SparkCode/打开星火亮点持久化档案目录", false, 56)]
        public static void OpenLoreFolder()
        {
            StarTempleLoreStore.LoadManifest();
            EditorUtility.RevealInFinder(StarTempleLoreStore.DataDirectory);
        }

        public static StarTempleWorldUiController Build(GameObject player, Transform starPivot, Camera cam)
        {
            var old = GameObject.Find("StarTemple_WorldUI");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            var orbMaterial = CreateEmissiveMaterial("Lore_Orb.mat",
                new Color(1f, 0.94f, 0.78f), new Color(9f, 7.4f, 4.2f));
            var obeliskMaterial = CreateEmissiveMaterial("Exit_Obelisk.mat",
                new Color(0.96f, 0.98f, 1f), new Color(7f, 7.5f, 8f));
            var defaultLoreImage = LoadOrCreateDefaultLoreImage();
            var haloMaterial = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Starfire_Halo_Realtime.mat");

            var root = new GameObject("StarTemple_WorldUI");
            var controller = root.AddComponent<StarTempleWorldUiController>();
            controller.player = player.GetComponent<StarTemplePlayerController>();
            controller.playerCamera = cam;
            controller.starPivot = starPivot;
            controller.uiRoot = root.transform;
            controller.uiFont = BuiltinFont();

            CreateLorePoints(starPivot, cam, orbMaterial, haloMaterial, defaultLoreImage);

            var bridgeAnchor = new GameObject("Bridge_Dialogue_Anchor").transform;
            bridgeAnchor.SetParent(root.transform, false);
            bridgeAnchor.position = new Vector3(-1.6f, 1.55f, 6.0f);
            controller.bridgeAnchor = bridgeAnchor;

            var bridgeCanvas = CreateCanvas("Bridge_Epic_Dialogue", root.transform,
                new Vector2(920f, 560f), 0.0028f, bridgeAnchor.position + Vector3.up * 0.68f, 20);
            bridgeCanvas.GetComponent<Canvas>().worldCamera = cam;
            BuildBridgeDialogue(bridgeCanvas, controller);
            controller.bridgeCanvas = bridgeCanvas;
            controller.bridgePanelRect = bridgeCanvas.GetComponent<RectTransform>();

            var exitAnchor = new GameObject("Exit_Prompt_Anchor").transform;
            exitAnchor.SetParent(root.transform, false);
            exitAnchor.position = new Vector3(-1.6f, 0.15f, 13.9f);
            controller.exitAnchor = exitAnchor;

            CreateExitObelisk(root.transform, obeliskMaterial);
            var exitCanvas = CreateScreenCanvas("Exit_Confirmation_Modal", root.transform, cam, 500);
            controller.exitCanvas = exitCanvas;
            BuildExitModal(exitCanvas, controller);

            // Fixed menu orientation: its readable face points horizontally toward
            // the white obelisk, so the sign stays upright instead of pitching down.
            Vector3 bridgeForward = bridgeCanvas.transform.position - exitAnchor.position;
            bridgeForward.y = 0f;
            bridgeCanvas.transform.rotation = Quaternion.LookRotation(bridgeForward.normalized, Vector3.up);

            var loreCanvas = CreateCanvas("Lore_Card", root.transform,
                new Vector2(620f, 360f), 0.0032f, new Vector3(0f, 7f, 3f), 22);
            loreCanvas.GetComponent<Canvas>().worldCamera = cam;
            BuildLoreCard(loreCanvas, controller, defaultLoreImage);
            controller.loreCanvas = loreCanvas;
            controller.lorePanelRect = loreCanvas.GetComponent<RectTransform>();

            SetLayerRecursively(root, 5);

            // The bridge panel is visible in the saved scene so it can be reviewed in the editor.
            loreCanvas.SetActive(false);
            exitCanvas.SetActive(false);
            bridgeCanvas.SetActive(true);
            AssetDatabase.SaveAssets();
            Debug.Log("[StarTempleStoryUi] World UI built. Lore manifest: " + StarTempleLoreStore.ManifestPath);
            return controller;
        }

        static void CreateLorePoints(Transform starPivot, Camera cam, Material orbMaterial,
            Material haloMaterial, Texture2D defaultImage)
        {
            var group = new GameObject("Star_LorePoints").transform;
            group.SetParent(starPivot, false);
            group.localPosition = Vector3.zero;

            string[] ids = { "spark_memory_01", "spark_memory_02", "spark_memory_03", "spark_memory_04", "spark_memory_05" };
            string[] titles = { "星火之核", "第一束光", "远航者的誓言", "时间的回声", "未竟的史诗" };
            string[] bodies =
            {
                "四芒星的火种在穹顶下缓慢呼吸。每一道金线都记着一位远航者曾经许下的愿望。",
                "传说第一束光不是从天空落下，而是从一颗不肯熄灭的心里升起。",
                "当环形水池映出完整的星光，远航者便会在桥的尽头留下誓言，然后继续向东。",
                "穹顶的孔洞把四季引进殿内。星火听见每一阵风，也记得每一场离别。",
                "史诗尚未写完。新的篇章正等待踏入圣殿的人亲手落笔。"
            };
            Vector3[] worldPositions =
            {
                new Vector3(-1.86f, 6.78f, 0.78f),
                new Vector3(1.90f, 6.48f, 0.88f),
                new Vector3(-1.80f, 4.82f, 0.74f),
                new Vector3(1.86f, 4.72f, 0.62f),
                new Vector3(0.28f, 8.02f, 0.70f)
            };

            for (int i = 0; i < ids.Length; i++)
            {
                var orb = new GameObject("LorePoint_" + (i + 1).ToString("00") + "_" + titles[i]);
                orb.transform.SetParent(group, true);
                orb.transform.position = worldPositions[i];

                var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                core.name = "Glow Core";
                core.transform.SetParent(orb.transform, false);
                core.transform.localScale = Vector3.one * 0.30f;
                var coreCollider = core.GetComponent<Collider>();
                if (coreCollider != null) UnityEngine.Object.DestroyImmediate(coreCollider);
                var coreRenderer = core.GetComponent<MeshRenderer>();
                coreRenderer.sharedMaterial = orbMaterial;
                coreRenderer.shadowCastingMode = ShadowCastingMode.Off;
                coreRenderer.receiveShadows = false;

                var halo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                halo.name = "Halo";
                halo.transform.SetParent(orb.transform, false);
                halo.transform.localScale = Vector3.one * 1.35f;
                var haloCollider = halo.GetComponent<Collider>();
                if (haloCollider != null) UnityEngine.Object.DestroyImmediate(haloCollider);
                var haloRenderer = halo.GetComponent<MeshRenderer>();
                if (haloMaterial != null) haloRenderer.sharedMaterial = haloMaterial;
                haloRenderer.shadowCastingMode = ShadowCastingMode.Off;
                haloRenderer.receiveShadows = false;
                var billboard = halo.AddComponent<Billboard>();
                billboard.cameraTransform = cam.transform;

                var collider = orb.AddComponent<SphereCollider>();
                collider.isTrigger = true;
                collider.radius = 0.62f;

                var lore = orb.AddComponent<StarTempleLorePoint>();
                lore.pointId = ids[i];
                lore.defaultTitle = titles[i];
                lore.defaultBody = bodies[i];
                lore.defaultImage = defaultImage;
                lore.EditorInitializePersistence();
            }
        }

        static void BuildBridgeDialogue(GameObject canvasGo, StarTempleWorldUiController controller)
        {
            AddPanelBackground(canvasGo, Panel);
            controller.bridgeTitle = CreateText(canvasGo.transform, "Title", "星火史诗", 40,
                Cream, TextAnchor.MiddleCenter, new Vector2(700f, 70f), new Vector2(0f, 220f), FontStyle.Bold);

            var back = CreateButton(canvasGo.transform, "Back_Button", "返回上一级", StarTempleUiAction.Back,
                new Vector2(230f, 48f), Vector2.zero);
            AnchorTopLeft((RectTransform)back.transform, new Vector2(22f, -20f), new Vector2(230f, 48f));
            SetButtonFontSize(back, 25);
            controller.buttons.Add(back);

            var level1 = NewUIObject("Level_01", canvasGo.transform, new Vector2(820f, 390f), new Vector2(0f, -34f));
            var enter = CreateButton(level1.transform, "Enter_Epic_Button", "进入史诗...", StarTempleUiAction.EnterEpic,
                new Vector2(310f, 120f), new Vector2(-190f, 30f));
            SetButtonFontSize(enter, 34);
            controller.buttons.Add(enter);
            var settings = CreateButton(level1.transform, "Settings_Button", "设置", StarTempleUiAction.Settings,
                new Vector2(310f, 120f), new Vector2(190f, 30f));
            SetButtonFontSize(settings, 34);
            controller.buttons.Add(settings);
            CreateText(level1.transform, "Input Hint", "左键或右键选择", 20, Muted,
                TextAnchor.MiddleCenter, new Vector2(620f, 34f), new Vector2(0f, -154f));

            var epic = NewUIObject("Level_02_Epic", canvasGo.transform, new Vector2(850f, 400f), new Vector2(0f, -34f));
            CreateColumn(epic.transform, "Create_Epic_Column", "创建新史诗", new Vector2(-205f, -5f));
            CreateColumn(epic.transform, "Load_Epic_Column", "加载已有史诗", new Vector2(205f, -5f));

            var settingsPage = NewUIObject("Level_02_Settings", canvasGo.transform, new Vector2(850f, 400f), new Vector2(0f, -34f));
            var settingsFrame = AddImage(NewUIObject("Settings_Empty_Frame", settingsPage.transform,
                new Vector2(760f, 360f), new Vector2(0f, 0f)), PanelRaised);
            AddOutline(NewUIObject("Settings_Frame_Outline", settingsPage.transform,
                new Vector2(764f, 364f), new Vector2(0f, 0f)), new Color(0.27f, 0.22f, 0.14f, 0.9f), 2f);
            settingsFrame.transform.SetAsLastSibling();

            controller.bridgeRootPage = level1;
            controller.epicPage = epic;
            controller.settingsPage = settingsPage;
            level1.SetActive(true);
            epic.SetActive(false);
            settingsPage.SetActive(false);
        }

        static void CreateColumn(Transform parent, string name, string title, Vector2 pos)
        {
            var frame = NewUIObject(name, parent, new Vector2(380f, 360f), pos);
            var bg = AddImage(frame, new Color(0.035f, 0.050f, 0.072f, 0.98f));
            AddOutline(frame, new Color(0.50f, 0.37f, 0.17f, 0.92f), 2f);
            CreateText(frame.transform, "Column_Title", title, 29, Cream, TextAnchor.MiddleCenter,
                new Vector2(344f, 58f), new Vector2(0f, 136f), FontStyle.Bold);
            var rule = NewUIObject("Title_Rule", frame.transform, new Vector2(310f, 2f), new Vector2(0f, 100f));
            AddImage(rule, new Color(0.72f, 0.52f, 0.23f, 0.8f));
            bg.raycastTarget = false;
        }

        static void BuildExitModal(GameObject canvasGo, StarTempleWorldUiController controller)
        {
            var dim = NewUIObject("Exit_Dim", canvasGo.transform, Vector2.zero, Vector2.zero);
            var dimRect = (RectTransform)dim.transform;
            dimRect.anchorMin = Vector2.zero;
            dimRect.anchorMax = Vector2.one;
            dimRect.offsetMin = Vector2.zero;
            dimRect.offsetMax = Vector2.zero;
            AddImage(dim, new Color(0.004f, 0.007f, 0.013f, 0.78f));

            var panel = NewUIObject("Exit_Panel", canvasGo.transform, new Vector2(680f, 380f), Vector2.zero);
            AddPanelBackground(panel, new Color(0.014f, 0.022f, 0.038f, 0.98f));
            controller.exitPanelRect = (RectTransform)panel.transform;

            var spine = NewUIObject("Gold_Spine", panel.transform, new Vector2(5f, 344f), new Vector2(-336f, 0f));
            AddImage(spine, Gold);

            CreateText(panel.transform, "Exit_Title", "退出程序？", 46, Cream,
                TextAnchor.MiddleCenter, new Vector2(560f, 90f), new Vector2(0f, 100f), FontStyle.Bold);
            var yes = CreateButton(panel.transform, "Exit_Yes", "是", StarTempleUiAction.ExitYes,
                new Vector2(190f, 74f), new Vector2(-112f, -48f));
            SetButtonFontSize(yes, 32);
            controller.buttons.Add(yes);
            var no = CreateButton(panel.transform, "Exit_No", "否", StarTempleUiAction.ExitNo,
                new Vector2(190f, 74f), new Vector2(112f, -48f));
            SetButtonFontSize(no, 32);
            controller.buttons.Add(no);
            CreateText(panel.transform, "Exit_Hint", "左键或右键选择", 19, Muted,
                TextAnchor.MiddleCenter, new Vector2(500f, 30f), new Vector2(0f, -146f));
        }

        static GameObject CreateScreenCanvas(string name, Transform parent, Camera cam, int sortingOrder)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 0.45f;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            scaler.referencePixelsPerUnit = 100f;
            return go;
        }

        static void BuildLoreCard(GameObject canvasGo, StarTempleWorldUiController controller, Texture2D defaultImage)
        {
            AddPanelBackground(canvasGo, new Color(0.014f, 0.022f, 0.038f, 0.95f));
            var accent = NewUIObject("Gold_Spine", canvasGo.transform, new Vector2(5f, 330f), new Vector2(-303f, 0f));
            AddImage(accent, Gold);
            controller.loreTitle = CreateText(canvasGo.transform, "Lore_Title", "星火记忆", 31, Cream,
                TextAnchor.UpperLeft, new Vector2(360f, 52f), new Vector2(-118f, 126f), FontStyle.Bold);

            controller.loreBody = CreateText(canvasGo.transform, "Lore_Body", "正史尚未书写。", 21,
                new Color(0.86f, 0.85f, 0.78f, 1f), TextAnchor.UpperLeft,
                new Vector2(352f, 230f), new Vector2(-122f, -10f));
            controller.loreBody.horizontalOverflow = HorizontalWrapMode.Wrap;
            controller.loreBody.verticalOverflow = VerticalWrapMode.Truncate;

            var imageGo = new GameObject("Lore_Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            imageGo.transform.SetParent(canvasGo.transform, false);
            var imageRect = imageGo.GetComponent<RectTransform>();
            imageRect.sizeDelta = new Vector2(150f, 150f);
            imageRect.anchoredPosition = new Vector2(214f, 62f);
            var raw = imageGo.GetComponent<RawImage>();
            raw.texture = defaultImage;
            raw.color = Color.white;
            raw.raycastTarget = false;
            controller.loreImage = raw;

            CreateText(canvasGo.transform, "Lore_Kicker", "SPARK MEMORY", 15, Gold,
                TextAnchor.UpperRight, new Vector2(310f, 28f), new Vector2(146f, 154f));

            var hint = NewUIObject("Lore_Bottom_Rule", canvasGo.transform, new Vector2(548f, 1.5f), new Vector2(0f, -154f));
            AddImage(hint, new Color(0.55f, 0.42f, 0.20f, 0.7f));
        }

        static GameObject CreateExitObelisk(Transform parent, Material material)
        {
            var obelisk = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obelisk.name = "Temple_Exit_Obelisk";
            obelisk.transform.SetParent(parent, false);
            obelisk.transform.position = new Vector3(-1.6f, 1.375f, 13.9f);
            obelisk.transform.localScale = new Vector3(0.78f, 2.45f, 0.18f);
            var renderer = obelisk.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return obelisk;
        }

        static GameObject CreateCanvas(string name, Transform parent, Vector2 size, float scale,
            Vector3 position, int sortingOrder)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.pivot = new Vector2(0.5f, 0.5f);
            go.transform.localScale = Vector3.one * scale;
            go.transform.position = position;
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = sortingOrder;
            return go;
        }

        static void AddPanelBackground(GameObject canvasGo, Color color)
        {
            var image = canvasGo.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var left = NewUIObject("Border_Left", canvasGo.transform, new Vector2(4f, 0f), Vector2.zero);
            var lr = (RectTransform)left.transform;
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(0f, 1f);
            lr.pivot = new Vector2(0f, 0.5f);
            lr.sizeDelta = new Vector2(4f, 0f);
            lr.anchoredPosition = Vector2.zero;
            AddImage(left, Gold);
            var top = NewUIObject("Border_Top", canvasGo.transform, new Vector2(0f, 3f), Vector2.zero);
            var tr = (RectTransform)top.transform;
            tr.anchorMin = new Vector2(0f, 1f);
            tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(0.5f, 1f);
            tr.sizeDelta = new Vector2(0f, 3f);
            tr.anchoredPosition = Vector2.zero;
            AddImage(top, new Color(0.86f, 0.63f, 0.26f, 0.85f));
        }

        static void AddOutline(GameObject target, Color color, float width)
        {
            var rect = target.GetComponent<RectTransform>();
            var parent = target.transform;
            var top = NewUIObject("Outline_Top", parent, new Vector2(rect.sizeDelta.x, width), Vector2.zero);
            Stretch((RectTransform)top.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 1f));
            AddImage(top, color);
            var bottom = NewUIObject("Outline_Bottom", parent, new Vector2(rect.sizeDelta.x, width), Vector2.zero);
            Stretch((RectTransform)bottom.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0.5f, 0f));
            AddImage(bottom, color);
        }

        static StarTempleWorldUiButton CreateButton(Transform parent, string name, string label,
            StarTempleUiAction action, Vector2 size, Vector2 position)
        {
            var go = NewUIObject(name, parent, size, position);
            var background = go.AddComponent<Image>();
            background.color = ButtonNormal;
            background.raycastTarget = false;
            AddOutline(go, new Color(0.58f, 0.43f, 0.19f, 0.92f), 2f);

            var text = CreateText(go.transform, "Label", label, 30, Cream, TextAnchor.MiddleCenter,
                new Vector2(size.x - 20f, size.y - 12f), Vector2.zero);
            var button = go.AddComponent<StarTempleWorldUiButton>();
            button.action = action;
            button.background = background;
            button.label = text;
            button.normalColor = ButtonNormal;
            button.hoverColor = ButtonHover;
            return button;
        }

        static void SetButtonFontSize(StarTempleWorldUiButton button, int size)
        {
            if (button.label != null) button.label.fontSize = size;
        }

        static Text CreateText(Transform parent, string name, string content, int fontSize, Color color,
            TextAnchor alignment, Vector2 size, Vector2 position, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            var text = go.GetComponent<Text>();
            text.text = content;
            text.font = BuiltinFont();
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static GameObject NewUIObject(string name, Transform parent, Vector2 size, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            return go;
        }

        static Image AddImage(GameObject go, Color color)
        {
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static void AnchorTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
        }

        static Font BuiltinFont()
        {
            if (projectFont == null)
                projectFont = AssetDatabase.LoadAssetAtPath<Font>(UnicodeFontPath);
            if (projectFont == null)
            {
                Debug.LogWarning("[StarTempleStoryUi] Missing " + UnicodeFontPath + ", falling back to LegacyRuntime.ttf");
                projectFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            return projectFont;
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursively(child.gameObject, layer);
        }

        static Material CreateEmissiveMaterial(string filename, Color baseColor, Color emission)
        {
            string path = Generated + "/" + filename;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(filename) };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            material.SetColor("_EmissionColor", emission);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.35f);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static Texture2D LoadOrCreateDefaultLoreImage()
        {
            if (!File.Exists(DefaultLorePng))
            {
                Directory.CreateDirectory(Generated);
                const int size = 256;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float nx = (x - size * 0.5f) / (size * 0.5f);
                        float ny = (y - size * 0.5f) / (size * 0.5f);
                        float r = Mathf.Sqrt(nx * nx + ny * ny);
                        float star = Mathf.Pow(Mathf.Abs(nx), 0.56f) + Mathf.Pow(Mathf.Abs(ny), 0.56f);
                        float glow = Mathf.Exp(-r * r * 7f);
                        float ring = Mathf.Exp(-Mathf.Pow((r - 0.83f) / 0.045f, 2f));
                        float alpha = Mathf.Clamp01(glow * 0.55f + ring * 0.75f);
                        Color color = new Color(1f, 0.78f, 0.38f, alpha);
                        if (star < 0.72f)
                        {
                            float core = Mathf.Clamp01(1f - star / 0.72f);
                            color = Color.Lerp(new Color(1f, 0.58f, 0.12f, 1f),
                                new Color(1f, 0.98f, 0.86f, 1f), core * core);
                            color.a = Mathf.Clamp01(0.4f + core);
                        }
                        else if (star < 0.86f)
                        {
                            color = Color.Lerp(color, new Color(1f, 0.91f, 0.68f, 0.7f), 0.55f);
                        }
                        pixels[y * size + x] = color;
                    }
                }
                texture.SetPixels(pixels);
                texture.Apply();
                File.WriteAllBytes(DefaultLorePng, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.Refresh();
            }

            var importer = AssetImporter.GetAtPath(DefaultLorePng) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = 512;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultLorePng);
        }
    }
}
