using System.Collections.Generic;
using SparkCode.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SparkCode.Generation
{
    public struct MenuInfo
    {
        public GameObject Root;
        public Canvas Canvas;
        public StartMenuController Controller;
    }

    /// <summary>
    /// Builds the baked-sprite start menu on a Screen Space - Camera canvas.
    /// Every piece of art (Chinese text included) comes from Assets/Art/UI/*.png,
    /// baked by Tools/generate_ui_art.py. Layout is authored at 1920x1080 and
    /// keeps the central spark / dome clear: brand top-left, menu bottom-left,
    /// panels slide in from the right.
    /// </summary>
    public static class StartMenuBuilder
    {
        const string ArtRoot = "Assets/Art/UI/";
        const int UILayer = 5;
        static readonly Vector2 Ref = new Vector2(1920f, 1080f);

        // layout constants (canvas units at 1920x1080)
        const float ButtonW = 380f;
        const float ButtonH = 72f;
        const float ButtonGap = 15f;
        const float MenuHeight = ButtonH * 4f + ButtonGap * 3f; // 333

        public static MenuInfo Build(TempleBuildKit kit, Camera camera, OrbitCamera orbit)
        {
            var art = new Art(kit);

            var root = new GameObject("SparkCodeStartMenu", typeof(RectTransform));
            root.layer = UILayer;
            if (kit != null && kit.Root != null) root.transform.SetParent(kit.Root, false);
            var rootRt = (RectTransform)root.transform;
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = Vector2.zero;
            rootRt.anchoredPosition = Vector2.zero;

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 2f;
            canvas.sortingOrder = 100;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Ref;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            scaler.referencePixelsPerUnit = 100f;

            root.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            var controller = root.AddComponent<StartMenuController>();
            controller.orbit = orbit;

            // ---------------------------------------------------------- shades
            var shadeLeft = NewRect("ShadeLeft", rootRt, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(820f, 0f));
            AddImage(shadeLeft, art.ShadeLeft, Color.white, false, false);

            var shadeBottom = NewRect("ShadeBottom", rootRt, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 300f));
            AddImage(shadeBottom, art.ShadeBottom, Color.white, false, false);

            var shadeTop = NewRect("ShadeTop", rootRt, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 250f));
            AddImage(shadeTop, art.ShadeTop, Color.white, false, false);

            // screen corners (decorative thin gold L)
            var corners = NewRect("Corners", rootRt, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            AddCorner(corners, art.Corner, new Vector2(0f, 1f), new Vector2(86f, -86f), 0f);
            AddCorner(corners, art.Corner, new Vector2(1f, 1f), new Vector2(-86f, -86f), -90f);
            AddCorner(corners, art.Corner, new Vector2(1f, 0f), new Vector2(-86f, 86f), 180f);
            AddCorner(corners, art.Corner, new Vector2(0f, 0f), new Vector2(86f, 86f), 90f);

            // ---------------------------------------------------------- menu content
            var menuContent = NewRect("MenuContent", rootRt, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var menuGroup = menuContent.gameObject.AddComponent<CanvasGroup>();
            menuGroup.alpha = 1f;
            menuGroup.blocksRaycasts = true;
            menuGroup.interactable = true;

            // brand (top-left)
            var brand = NewRect("Brand", menuContent, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(108f, -44f), new Vector2(320f, 180f));
            var brandGlowRt = NewRect("BrandGlow", brand, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(16f, -16f), new Vector2(112f, 112f));
            AddImage(brandGlowRt, art.Glow, new Color(1f, 0.84f, 0.52f, 0.34f), false, false);
            var brandStarRt = NewRect("BrandStar", brand, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(30f, 30f));
            AddImage(brandStarRt, art.Fourpoint, new Color(1f, 0.93f, 0.72f, 1f), false, true);
            var brandWords = Text(brand, "BrandWords", art.Brand, new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(44f, -2f), 0.94f, Color.white);
            float brandWordsH = SpriteSize(art.Brand).y * 0.94f;
            var brandRuleRt = NewRect("BrandRule", brand, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(44f, -brandWordsH - 12f), new Vector2(196f, 2f));
            AddImage(brandRuleRt, art.Line, new Color(0.93f, 0.80f, 0.55f, 0.75f), false, false);

            // left column: menu + title block
            var column = NewRect("LeftColumn", menuContent, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(108f, 84f), new Vector2(680f, 720f));
            var menu = NewRect("Menu", column, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), Vector2.zero, new Vector2(ButtonW, MenuHeight));

            var buttons = new MenuButton[4];
            var actions = new[] { MenuAction.Start, MenuAction.Settings, MenuAction.About, MenuAction.Quit };
            var labels = new[] { art.BtnStart, art.BtnSettings, art.BtnAbout, art.BtnQuit };
            var names = new[] { "Btn_Start", "Btn_Settings", "Btn_About", "Btn_Quit" };
            for (int i = 0; i < 4; i++)
            {
                float y = MenuHeight - ButtonH - i * (ButtonH + ButtonGap);
                buttons[i] = CreateButton(menu, names[i], labels[i], actions[i], i, art,
                    new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, y),
                    new Vector2(ButtonW, ButtonH), false);
                buttons[i].controller = controller;
            }

            float subY = MenuHeight + 16f;
            var subtitle = Text(column, "Subtitle", art.Subtitle, new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(2f, subY), 0.95f, new Color(1f, 1f, 1f, 0.95f));
            float subH = SpriteSize(art.Subtitle).y * 0.95f;

            float enY = subY + subH + 10f;
            var titleEn = Text(column, "TitleEn", art.TitleEn, new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(4f, enY), 0.90f, Color.white);
            float enH = SpriteSize(art.TitleEn).y * 0.90f;

            float titleY = enY + enH + 4f;
            var titleCn = Text(column, "TitleCn", art.TitleCn, new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, titleY), 0.94f, Color.white);
            float titleH = SpriteSize(art.TitleCn).y * 0.94f;

            var underlineRt = NewRect("TitleRule", column, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(2f, MenuHeight + 3f), new Vector2(150f, 10f));
            AddImage(underlineRt, art.Underline, new Color(1f, 1f, 1f, 0.5f), false, false);

            var sparkleA = NewRect("SparkleA", column, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0.5f, 0.5f), new Vector2(SpriteSize(art.TitleCn).x * 0.94f + 16f,
                    titleY + titleH * 0.72f), new Vector2(18f, 18f));
            AddImage(sparkleA, art.Fourpoint, new Color(1f, 0.92f, 0.70f, 0.55f), false, true);
            var sparkleB = NewRect("SparkleB", column, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0.5f, 0.5f), new Vector2(SpriteSize(art.TitleCn).x * 0.94f + 40f,
                    titleY + titleH * 0.46f), new Vector2(10f, 10f));
            AddImage(sparkleB, art.Fourpoint, new Color(1f, 0.92f, 0.70f, 0.32f), false, true);

            // bottom bar
            Text(menuContent, "Version", art.Version, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(108f, 36f), 1f, Color.white);
            Text(menuContent, "HintClick", art.HintClick, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 54f), 0.95f, Color.white);
            Text(menuContent, "HintKeys", art.HintKeys, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 26f), 0.90f, Color.white);
            Text(menuContent, "Copyright", art.Copyright, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-108f, 36f), 1f, new Color(1f, 1f, 1f, 0.85f));

            // ---------------------------------------------------------- caption
            var captionRt = NewRect("Caption", rootRt, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var captionGroup = captionRt.gameObject.AddComponent<CanvasGroup>();
            captionGroup.alpha = 0f;
            captionGroup.blocksRaycasts = false;
            captionGroup.interactable = false;
            var captionGlowRt = NewRect("CaptionGlow", captionRt, new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 244f),
                new Vector2(820f, 320f));
            AddImage(captionGlowRt, art.Glow, new Color(1f, 0.82f, 0.48f, 0.20f), false, false);
            Text(captionRt, "CaptionTitle", art.CaptionTitle, new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 168f), 1f, Color.white);
            Text(captionRt, "CaptionHint", art.CaptionHint, new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 112f), 1f, new Color(1f, 1f, 1f, 0.86f));

            // ---------------------------------------------------------- blocker + panels
            var blockerRt = NewRect("PanelBlocker", rootRt, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            AddImage(blockerRt, null, new Color(0.015f, 0.02f, 0.028f, 0.34f), true, false);
            blockerRt.gameObject.SetActive(false);

            MenuRow[] rows;
            var settings = BuildSettingsPanel(rootRt, art, controller, out rows);
            var about = BuildAboutPanel(rootRt, art, controller);

            controller.settingsPanel = new StartMenuController.MenuPanel
            {
                group = settings.Value,
                rect = settings.Key,
                shownPos = new Vector2(-140f, 0f),
                hiddenPos = new Vector2(-30f, 0f)
            };
            controller.aboutPanel = new StartMenuController.MenuPanel
            {
                group = about.Value,
                rect = about.Key,
                shownPos = new Vector2(-140f, 0f),
                hiddenPos = new Vector2(-30f, 0f)
            };
            controller.settingsRows = rows;
            controller.mainButtons = buttons;
            controller.menuGroup = menuGroup;
            controller.menuRoot = menuContent;
            controller.captionGroup = captionGroup;
            controller.captionRoot = captionRt;

            // flash overlay on top of everything
            var flashRt = NewRect("Flash", rootRt, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var flash = AddImage(flashRt, art.Glow, new Color(1f, 0.86f, 0.60f, 0f), false, false);
            controller.flash = flash;
            controller.panelBlocker = blockerRt.gameObject;

            // initial settings display (static, readable in edit mode)
            int q = Mathf.Clamp(PlayerPrefs.GetInt("SparkCode.Quality", 2), 0, 2);
            int v = Mathf.Clamp(PlayerPrefs.GetInt("SparkCode.Volume", 2), 0, 4);
            int f = Mathf.Clamp(PlayerPrefs.GetInt("SparkCode.Fullscreen", 0), 0, 1);
            controller.qualityIndex = q;
            controller.volumeIndex = v;
            controller.fullscreenIndex = f;
            controller.RefreshRows();
            controller.PrepareCapture("main");

            Canvas.ForceUpdateCanvases();

            return new MenuInfo { Root = root, Canvas = canvas, Controller = controller };
        }

        // ------------------------------------------------------------------ panels
        static KeyValuePair<RectTransform, CanvasGroup> BuildSettingsPanel(Transform parent, Art art,
            StartMenuController controller, out MenuRow[] settingsRows)
        {
            var panelRt = NewRect("SettingsPanel", parent, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-30f, 0f), new Vector2(600f, 560f));
            AddSliced(panelRt, art.PanelBg, new Color(1f, 1f, 1f, 0.98f), true);
            var group = panelRt.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            Text(panelRt, "PanelTitle", art.PanelSettings, new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(38f, -26f), 0.95f, Color.white);
            var divider = NewRect("Divider", panelRt, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(38f, -116f), new Vector2(500f, 12f));
            AddImage(divider, art.Divider, new Color(1f, 1f, 1f, 0.9f), false, false);

            var rows = new MenuRow[3];
            var rowLabels = new[] { art.LabelQuality, art.LabelVolume, art.LabelFullscreen };
            var rowKinds = new[] { RowKind.Quality, RowKind.Volume, RowKind.Fullscreen };
            var rowSprites = new[]
            {
                new[] { art.ValLow, art.ValMid, art.ValHigh },
                new[] { art.Vol0, art.Vol25, art.Vol50, art.Vol75, art.Vol100 },
                new[] { art.ValOn, art.ValOff }
            };
            var rowNames = new[] { "Row_Quality", "Row_Volume", "Row_Fullscreen" };
            for (int i = 0; i < 3; i++)
            {
                rows[i] = CreateRow(panelRt, rowNames[i], rowLabels[i], rowKinds[i],
                    rowSprites[i], new Vector2(40f, -142f - i * 74f), art);
                rows[i].controller = controller;
            }
            settingsRows = rows;

            var back = CreateButton(panelRt, "Btn_SettingsBack", art.BtnBack, MenuAction.Back, -1,
                art, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 30f),
                new Vector2(300f, 60f), true);
            back.controller = controller;
            back.alwaysInteractable = true;

            return new KeyValuePair<RectTransform, CanvasGroup>(panelRt, group);
        }

        static KeyValuePair<RectTransform, CanvasGroup> BuildAboutPanel(Transform parent, Art art,
            StartMenuController controller)
        {
            var panelRt = NewRect("AboutPanel", parent, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-30f, 0f), new Vector2(600f, 560f));
            AddSliced(panelRt, art.PanelBg, new Color(1f, 1f, 1f, 0.98f), true);
            var group = panelRt.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            Text(panelRt, "PanelTitle", art.PanelAbout, new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(38f, -26f), 0.95f, Color.white);
            var divider = NewRect("Divider", panelRt, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(38f, -116f), new Vector2(500f, 12f));
            AddImage(divider, art.Divider, new Color(1f, 1f, 1f, 0.9f), false, false);

            Text(panelRt, "AboutName", art.AboutName, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(40f, -136f), 0.95f, Color.white);
            Text(panelRt, "AboutNameEn", art.AboutNameEn, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(40f, -202f), 0.95f, Color.white);
            Text(panelRt, "AboutBody", art.AboutBody, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(40f, -258f), 0.95f, new Color(1f, 1f, 1f, 0.92f));
            Text(panelRt, "AboutTeam", art.AboutTeam, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(40f, -388f), 1f, Color.white);
            Text(panelRt, "AboutEngine", art.AboutEngine, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(40f, -424f), 1f, Color.white);

            var sparkle = NewRect("PanelSparkle", panelRt, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(-52f, -40f), new Vector2(16f, 16f));
            AddImage(sparkle, art.Fourpoint, new Color(1f, 0.92f, 0.70f, 0.5f), false, true);

            var back = CreateButton(panelRt, "Btn_AboutBack", art.BtnBack, MenuAction.Back, -1,
                art, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 30f),
                new Vector2(300f, 60f), true);
            back.controller = controller;
            back.alwaysInteractable = true;

            return new KeyValuePair<RectTransform, CanvasGroup>(panelRt, group);
        }

        // ------------------------------------------------------------------ widgets
        static MenuRow CreateRow(Transform parent, string name, Sprite labelSprite, RowKind kind,
            Sprite[] values, Vector2 pos, Art art)
        {
            var rt = NewRect(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), pos, new Vector2(520f, 64f));
            var hover = AddImage(rt, art.RowHover, new Color(1f, 1f, 1f, 0f), true, false);

            var labelRt = NewRect("Label", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(18f, 0f), Vector2.zero);
            var label = AddImage(labelRt, labelSprite, new Color(1f, 1f, 1f, 0.94f), false, true);
            labelRt.sizeDelta = SpriteSize(labelSprite) * 0.92f;

            var valueRt = NewRect("Value", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-130f, 0f), Vector2.zero);
            var value = AddImage(valueRt, values != null && values.Length > 0 ? values[0] : null,
                Color.white, false, true);
            if (values != null && values.Length > 0)
                valueRt.sizeDelta = SpriteSize(values[0]) * 0.95f;

            var chevL = NewRect("ChevronLeft", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-210f, 0f), new Vector2(14f, 21f));
            var ciL = AddImage(chevL, art.Chevron, new Color(1f, 1f, 1f, 0.45f), false, true);
            ciL.transform.localScale = new Vector3(-1f, 1f, 1f);

            var chevR = NewRect("ChevronRight", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-50f, 0f), new Vector2(14f, 21f));
            AddImage(chevR, art.Chevron, new Color(1f, 1f, 1f, 0.45f), false, true);

            var row = rt.gameObject.AddComponent<MenuRow>();
            row.kind = kind;
            row.hoverBg = hover;
            row.label = label;
            row.value = value;
            row.chevronLeft = ciL;
            row.chevronRight = chevR.GetComponent<Image>();
            row.valueSprites = values;
            return row;
        }

        static MenuButton CreateButton(Transform parent, string name, Sprite labelSprite,
            MenuAction action, int index, Art art, Vector2 anchor, Vector2 pivot, Vector2 pos,
            Vector2 size, bool centered)
        {
            var rt = NewRect(name, parent, anchor, anchor, pivot, pos, size);
            var frame = AddSliced(rt, art.FrameNormal, new Color(1f, 1f, 1f, 0.85f), true);

            var hoverRt = NewRect("Hover", rt, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            var hover = AddSliced(hoverRt, art.FrameHover, new Color(1f, 1f, 1f, 0f), false);

            var haloRt = NewRect("Halo", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(centered ? 0f : 30f, 0f),
                new Vector2(size.x + 80f, 104f));
            var halo = AddImage(haloRt, art.HaloBar, new Color(1f, 0.84f, 0.50f, 0.07f), false, false);

            Image marker;
            Image label;
            Image dot;
            if (centered)
            {
                var labelRt = NewRect("Label", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(0f, 1f), Vector2.zero);
                label = AddImage(labelRt, labelSprite, Color.white, false, true);
                labelRt.sizeDelta = SpriteSize(labelSprite) * 0.82f;

                var dotRt = NewRect("Spark", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(-105f, 0f), new Vector2(40f, 40f));
                dot = AddImage(dotRt, art.Glow, new Color(1f, 0.86f, 0.58f, 0.22f), false, false);

                var markerRt = NewRect("Marker", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(105f, 0f), new Vector2(12f, 12f));
                marker = AddImage(markerRt, art.Fourpoint, new Color(1f, 0.92f, 0.72f, 0.45f), false, true);
            }
            else
            {
                var markerRt = NewRect("Marker", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f), new Vector2(24f, 1f), new Vector2(14f, 14f));
                marker = AddImage(markerRt, art.Fourpoint, new Color(1f, 0.92f, 0.72f, 0.45f), false, true);

                var labelRt = NewRect("Label", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f), new Vector2(52f, 1f), Vector2.zero);
                label = AddImage(labelRt, labelSprite, Color.white, false, true);
                labelRt.sizeDelta = SpriteSize(labelSprite) * 0.90f;

                var dotRt = NewRect("Spark", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(1f, 0.5f), new Vector2(-56f, 0f), new Vector2(46f, 46f));
                dot = AddImage(dotRt, art.Glow, new Color(1f, 0.86f, 0.58f, 0.22f), false, false);

                var chevRt = NewRect("Chevron", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(1f, 0.5f), new Vector2(-18f, 0f), new Vector2(10f, 15f));
                AddImage(chevRt, art.Chevron, new Color(1f, 1f, 1f, 0.18f), false, true);
            }

            var comp = rt.gameObject.AddComponent<MenuButton>();
            comp.action = action;
            comp.index = index;
            comp.visual = rt;
            comp.frame = frame;
            comp.frameHover = hover;
            comp.halo = halo;
            comp.label = label;
            comp.marker = marker;
            comp.glowDot = dot;
            return comp;
        }

        static void AddCorner(Transform parent, Sprite sprite, Vector2 anchor, Vector2 pos, float rot)
        {
            var rt = NewRect("Corner", parent, anchor, anchor, new Vector2(0.5f, 0.5f), pos,
                new Vector2(96f, 96f));
            AddImage(rt, sprite, new Color(1f, 1f, 1f, 0.42f), false, true);
            rt.localRotation = Quaternion.Euler(0f, 0f, rot);
        }

        // ------------------------------------------------------------------ helpers
        static void EnsureEventSystem()
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(null, false);
        }

        static RectTransform NewRect(string name, Transform parent, Vector2 anchorMin,
            Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = UILayer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        static Image AddImage(RectTransform rt, Sprite sprite, Color color, bool raycast,
            bool preserveAspect)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = raycast;
            img.preserveAspect = preserveAspect;
            return img;
        }

        static Image AddSliced(RectTransform rt, Sprite sprite, Color color, bool raycast)
        {
            var img = AddImage(rt, sprite, color, raycast, false);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
            img.fillCenter = true;
            return img;
        }

        static Image Text(Transform parent, string name, Sprite sprite, Vector2 anchor,
            Vector2 pivot, Vector2 pos, float scale, Color color)
        {
            var rt = NewRect(name, parent, anchor, anchor, pivot, pos, Vector2.zero);
            var img = AddImage(rt, sprite, color, false, true);
            if (sprite != null) rt.sizeDelta = SpriteSize(sprite) * scale;
            return img;
        }

        static Vector2 SpriteSize(Sprite sprite)
        {
            if (sprite == null) return Vector2.zero;
            return new Vector2(sprite.rect.width, sprite.rect.height) * 0.5f;
        }

        // ------------------------------------------------------------------ art
        class Art
        {
            public Sprite Brand, TitleCn, TitleEn, Subtitle;
            public Sprite BtnStart, BtnSettings, BtnAbout, BtnQuit, BtnBack;
            public Sprite HintClick, HintKeys, Version, Copyright;
            public Sprite CaptionTitle, CaptionHint;
            public Sprite PanelSettings, PanelAbout;
            public Sprite LabelQuality, LabelVolume, LabelFullscreen;
            public Sprite ValLow, ValMid, ValHigh, ValOn, ValOff;
            public Sprite Vol0, Vol25, Vol50, Vol75, Vol100;
            public Sprite AboutName, AboutNameEn, AboutBody, AboutTeam, AboutEngine, AboutVersion;
            public Sprite Line, Corner, FrameNormal, FrameHover, PanelBg, RowHover;
            public Sprite ShadeLeft, ShadeBottom, ShadeTop;
            public Sprite Fourpoint, Glow, GlowSoft, Mote, Divider, Underline, Chevron, HaloBar;

            public Art(TempleBuildKit kit)
            {
                Brand = Load(kit, "ui_brand_words");
                TitleCn = Load(kit, "ui_title_cn");
                TitleEn = Load(kit, "ui_title_en");
                Subtitle = Load(kit, "ui_subtitle");
                BtnStart = Load(kit, "ui_btn_start");
                BtnSettings = Load(kit, "ui_btn_settings");
                BtnAbout = Load(kit, "ui_btn_about");
                BtnQuit = Load(kit, "ui_btn_quit");
                BtnBack = Load(kit, "ui_btn_back");
                HintClick = Load(kit, "ui_hint_click");
                HintKeys = Load(kit, "ui_hint_keys");
                Version = Load(kit, "ui_version");
                Copyright = Load(kit, "ui_copyright");
                CaptionTitle = Load(kit, "ui_caption_title");
                CaptionHint = Load(kit, "ui_caption_hint");
                PanelSettings = Load(kit, "ui_panel_settings");
                PanelAbout = Load(kit, "ui_panel_about");
                LabelQuality = Load(kit, "ui_label_quality");
                LabelVolume = Load(kit, "ui_label_volume");
                LabelFullscreen = Load(kit, "ui_label_fullscreen");
                ValLow = Load(kit, "ui_val_low");
                ValMid = Load(kit, "ui_val_mid");
                ValHigh = Load(kit, "ui_val_high");
                ValOn = Load(kit, "ui_val_on");
                ValOff = Load(kit, "ui_val_off");
                Vol0 = Load(kit, "ui_val_vol0");
                Vol25 = Load(kit, "ui_val_vol25");
                Vol50 = Load(kit, "ui_val_vol50");
                Vol75 = Load(kit, "ui_val_vol75");
                Vol100 = Load(kit, "ui_val_vol100");
                AboutName = Load(kit, "ui_about_name");
                AboutNameEn = Load(kit, "ui_about_name_en");
                AboutBody = Load(kit, "ui_about_body");
                AboutTeam = Load(kit, "ui_about_team");
                AboutEngine = Load(kit, "ui_about_engine");
                AboutVersion = Load(kit, "ui_about_version");
                Line = Load(kit, "ui_line");
                Corner = Load(kit, "ui_corner");
                FrameNormal = LoadSliced(kit, "ui_frame_normal", new Vector4(20f, 20f, 20f, 20f));
                FrameHover = LoadSliced(kit, "ui_frame_hover", new Vector4(20f, 20f, 20f, 20f));
                PanelBg = LoadSliced(kit, "ui_panel_bg", new Vector4(72f, 72f, 72f, 72f));
                RowHover = Load(kit, "ui_row_hover");
                ShadeLeft = Load(kit, "ui_shade_left");
                ShadeBottom = Load(kit, "ui_shade_bottom");
                ShadeTop = Load(kit, "ui_shade_top");
                Fourpoint = Load(kit, "ui_fourpoint");
                Glow = Load(kit, "ui_glow");
                GlowSoft = Load(kit, "ui_glow_soft");
                Mote = Load(kit, "ui_mote");
                Divider = Load(kit, "ui_divider");
                Underline = Load(kit, "ui_underline");
                Chevron = Load(kit, "ui_chevron");
                HaloBar = Load(kit, "ui_halo_bar");
            }

            static Sprite Load(TempleBuildKit kit, string name)
            {
                if (kit == null) return null;
                return kit.LoadSprite(ArtRoot + name + ".png");
            }

            static Sprite LoadSliced(TempleBuildKit kit, string name, Vector4 border)
            {
                if (kit == null) return null;
                string path = ArtRoot + name + ".png";
                kit.LoadSprite(path);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    importer.spriteBorder = border;
                    importer.SaveAndReimport();
                }
                return AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
        }
    }
}
