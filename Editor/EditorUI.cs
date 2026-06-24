using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using DooDesch.UI;
using S1API.Rendering;
using S1API.UI;
using SideHustle;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Inkubator.Editor
{
    /// <summary>
    /// The in-game tattoo editor overlay. Opened from the Side Hustle hub, it runs in the menu scene ON TOP of
    /// the live character rig (no save loaded). The overlay background is transparent and the panels sit at the
    /// edges, so the 3D character stays visible in the middle and the tattoo updates there live as you edit.
    /// Flow: choose / create a project -> per body part, import PNGs and place them on a UV canvas (drag to move,
    /// buttons to scale/rotate/fade) -> see it live on the character -> export a complete, ready-to-release
    /// Inkorporated tattoo mod. Positions are stored in normalized UV space so the canvas and the baked texture
    /// stay in sync. Drag is handled by polling input in <see cref="Tick"/> (no injected MonoBehaviour).
    /// </summary>
    public static class EditorUI
    {
        private static LaunchContext _ctx;
        private static GameObject _canvasGO;
        private static GameObject _screen;
        private static Project _project;
        private static Placement _tab = Placement.Chest;
        private static string _status = "";
        private static Text _statusText;

        // UV canvas
        private static RectTransform _uvArea;
        private static float _uvSize;
        private static readonly Dictionary<Decal, GameObject> _decalSprites = new Dictionary<Decal, GameObject>();
        private static Decal _selected;          // selected decal within the selected tattoo
        private static TattooEntry _selectedTattoo;  // the tattoo being edited (its decals show on the canvas)
        private static GameObject _selRing;

        // Undo/redo: coarse whole-project JSON snapshots.
        private static readonly Stack<string> _undo = new Stack<string>();
        private static readonly Stack<string> _redo = new Stack<string>();

        // Review & Export screen: the most recent export result (drives the green/red result card), null = none yet.
        private static ExportResult _lastExport;

        // transform tool (game-editor style: W=move, E=rotate, R=scale)
        private enum Tool { Move, Rotate, Scale }
        private static Tool _tool = Tool.Move;
        private static Button[] _toolSegButtons;   // Move/Rotate/Scale switcher docked on the canvas

        // Right panel: contextual decal Inspector OR the import library, toggled by a segmented control.
        private enum RightTab { Inspector, Import }
        private static RightTab _rightTab = RightTab.Import;
        private static GameObject _rightPanel, _rightContent;
        private static Text _zoomLabel;

        // drag + live-preview debounce
        private static bool _dragging;
        private static int _dragMode;            // 0 none, 1 decal, 2 rotate-character
        private static Vector2 _dragOffset;
        private static Vector2 _dragStartMouse, _lastMouse;
        private static float _startRot, _startScale;
        private static bool _dragSnapped;        // whether the current drag gesture already pushed an undo snapshot
        private static bool _previewDirty;
        private static float _lastEdit;
        private static bool _layersLogged;

        // inspector controls: sliders + their live value readouts
        private static Text _sizeVal, _rotVal, _opVal;
        private static UnityEngine.UI.Slider _scaleSlider, _rotSlider, _opSlider;
        private static bool _suppressSlider;   // ignore slider callbacks while we set values programmatically
        private static float _lastSliderSnap;  // throttle undo snapshots during a slider drag

        // One consistent content inset (gutter) for every docked panel - left rail AND right panel use this.
        private const float Gutter = 18f;

        // Colors now come from the shared DooDesch.UI design tokens (violet/indigo "Dark Editor" theme).
        private static readonly Color Clear = Theme.Clear;
        private static readonly Color Panel = Theme.BgPanel;
        private static readonly Color CanvasBg = Theme.WithAlpha(Theme.BgDeep, 0.95f);
        private static readonly Color Accent = Theme.Accent;
        private static readonly Color Btn = Theme.Button;
        private static readonly Color BtnSel = new Color(0.24f, 0.255f, 0.42f, 1f);   // violet-tinted "selected" fill

        public static bool IsOpen => _canvasGO != null;

        // --- open / close ---

        public static void Open(LaunchContext ctx)
        {
            _ctx = ctx;
            if (_canvasGO == null) BuildCanvas();
            Preview.EnsureAvatar();
            ShowProjectSelect();
        }

#if DEBUG
        public static void DebugOpen(string projectName)
        {
            _ctx = null;
            if (_canvasGO == null) BuildCanvas();
            Preview.EnsureAvatar();
            if (!string.IsNullOrEmpty(projectName))
            {
                _project = ProjectStore.Load(projectName);
                if (_project != null) { DebugSelectFirstDecal(); ShowEditor(); Preview.SetStripClothing(true); PreviewAll(); return; }
            }
            ShowProjectSelect();
        }

        // Test aid: pre-select the first decal so a screenshot can show the Inspector (MCP can't click the canvas).
        private static void DebugSelectFirstDecal()
        {
            if (_project?.Tattoos == null) return;
            foreach (var t in _project.Tattoos)
                if (t.Decals != null && t.Decals.Count > 0)
                {
                    _tab = t.PlacementEnum; _selectedTattoo = t; _selected = t.Decals[0]; _rightTab = RightTab.Inspector;
                    return;
                }
        }

        // Open the editor on a project, then jump straight to the Review & Export screen (for screenshot tests).
        public static void DebugOpenReview(string projectName)
        {
            DebugOpen(projectName);
            if (_project != null) { _lastExport = null; ShowReviewExport(); }
        }

#endif

        public static void Close()
        {
            try { Preview.ExitEditor(); } catch { }
            try { Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto); } catch { }
            try { Toast.Clear(); } catch { }
            ClearScrollers();
            _decalSprites.Clear();
            _selected = null; _selRing = null; _dragging = false; _previewDirty = false;
            if (_canvasGO != null) { UnityEngine.Object.Destroy(_canvasGO); _canvasGO = null; }
            _screen = null; _project = null; _uvArea = null; _statusText = null;
            _sizeVal = _rotVal = _opVal = null;
        }

        private static void BuildCanvas()
        {
            _canvasGO = new GameObject("Inkubator_EditorCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
            var canvas = _canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            _canvasGO.AddComponent<GraphicRaycaster>();
            Toast.Init(_canvasGO.transform);   // transient feedback host, survives screen rebuilds
        }

        // --- screen: project select (dim background so the title reads) ---

        private static void ShowProjectSelect()
        {
            ClearScreen();
            _screen = UIFactory.Panel("ProjectSelect", _canvasGO.transform, Theme.BgBase, fullAnchor: true);

            // Centered "picker" card (consistent with the Side Hustle hub's centered window).
            float cardH = Mathf.Min(760f, Screen.height * 0.84f);
            var card = UIFactory.Panel("Card", _screen.transform, Theme.BgPanel);
            var cimg = card.GetComponent<Image>(); if (cimg != null) { cimg.sprite = Theme.RoundedSprite(); cimg.type = Image.Type.Sliced; }
            var crt = card.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0.5f, 0.5f); crt.anchorMax = new Vector2(0.5f, 0.5f); crt.pivot = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(640f, cardH); crt.anchoredPosition = Vector2.zero;
            var cardOutline = card.AddComponent<Outline>(); cardOutline.effectColor = Theme.HairlineStrong; cardOutline.effectDistance = new Vector2(1, -1);

            // Title + subtitle: full width so they never wrap (the old screen wrapped into vertical stacks).
            var title = UIFactory.Text("Title", "Inkubator", card.transform, Theme.H1, TextAnchor.UpperCenter, FontStyle.Bold);
            title.color = Theme.TextPrimary; title.raycastTarget = false;
            var trt = title.rectTransform; trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1); trt.pivot = new Vector2(0.5f, 1);
            trt.offsetMin = new Vector2(Gutter, -70); trt.offsetMax = new Vector2(-Gutter, -26);

            var sub = UIFactory.Text("Sub", "Design and export complete tattoo modpacks.", card.transform, 14, TextAnchor.UpperCenter);
            sub.color = Theme.TextMuted; sub.raycastTarget = false;
            var subrt = sub.rectTransform; subrt.anchorMin = new Vector2(0, 1); subrt.anchorMax = new Vector2(1, 1); subrt.pivot = new Vector2(0.5f, 1);
            subrt.offsetMin = new Vector2(Gutter, -98); subrt.offsetMax = new Vector2(-Gutter, -74);

            // New pack - the one primary CTA, centered.
            var (newGO, newBtn, _) = UIFactory.ButtonWithLabel("New", "New pack", card.transform, Accent, 210, 50);
            var nrt = newGO.GetComponent<RectTransform>(); nrt.anchorMin = new Vector2(0.5f, 1); nrt.anchorMax = new Vector2(0.5f, 1); nrt.pivot = new Vector2(0.5f, 1);
            nrt.anchoredPosition = new Vector2(0, -118); nrt.sizeDelta = new Vector2(210, 50);
            SetLeadingIcon(newGO, "add");
            newBtn.onClick.AddListener((UnityAction)(() => CreateProjectFlow()));

            // Divider + "Your packs (N)" header.
            var div = Components.Divider(card.transform);
            var dvrt = div.GetComponent<RectTransform>();
            dvrt.anchorMin = new Vector2(0, 1); dvrt.anchorMax = new Vector2(1, 1); dvrt.pivot = new Vector2(0.5f, 1);
            dvrt.offsetMin = new Vector2(Gutter, -181); dvrt.offsetMax = new Vector2(-Gutter, -180);

            List<string> projects = ProjectStore.List();
            TopLabel(card.transform, "YP", "Your packs (" + projects.Count + ")", -192, 22, 15, FontStyle.Bold);
            // small "open packs folder" affordance (manual access to the project folders)
            var openF = IconButton(card.transform, ToolIcons.Get("folder"), Theme.Button);
            var ofrt = openF.GetComponent<RectTransform>();
            ofrt.anchorMin = new Vector2(1, 1); ofrt.anchorMax = new Vector2(1, 1); ofrt.pivot = new Vector2(1, 1);
            ofrt.sizeDelta = new Vector2(28, 28); ofrt.anchoredPosition = new Vector2(-Gutter, -188);
            openF.onClick.AddListener((UnityAction)(() => OpenFolder(Paths.Projects)));

            // Scrollable pack list (fills the middle of the card).
            var scrollGO = new GameObject("PackScroll"); scrollGO.transform.SetParent(card.transform, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1);
            srt.offsetMin = new Vector2(Gutter, 76); srt.offsetMax = new Vector2(-Gutter, -218);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false; TuneScroll(scroll);

            var vp = new GameObject("Viewport"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vprt;

            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var ccrt = content.AddComponent<RectTransform>();
            ccrt.anchorMin = new Vector2(0, 1); ccrt.anchorMax = new Vector2(1, 1); ccrt.pivot = new Vector2(0.5f, 1); ccrt.offsetMin = Vector2.zero; ccrt.offsetMax = Vector2.zero;
            scroll.content = ccrt;

            const float rowH = 50f, gap = 8f, pad = 2f;
            if (projects.Count == 0)
            {
                ccrt.sizeDelta = new Vector2(0, 84);
                var empty = UIFactory.Panel("Empty", content.transform, Theme.BgElevated);
                var eimg = empty.GetComponent<Image>(); if (eimg != null) { eimg.sprite = Theme.RoundedSprite(); eimg.type = Image.Type.Sliced; eimg.raycastTarget = false; }
                var ert = empty.GetComponent<RectTransform>(); ert.anchorMin = new Vector2(0, 1); ert.anchorMax = new Vector2(1, 1); ert.pivot = new Vector2(0.5f, 1);
                ert.offsetMin = new Vector2(0, -80); ert.offsetMax = new Vector2(0, -4);
                var et = UIFactory.Text("EmptyT", "No packs yet.\nCreate your first one above.", empty.transform, 13, TextAnchor.MiddleCenter);
                et.color = Theme.TextMuted; et.raycastTarget = false;
                var etrt = et.rectTransform; etrt.anchorMin = Vector2.zero; etrt.anchorMax = Vector2.one; etrt.offsetMin = new Vector2(12, 6); etrt.offsetMax = new Vector2(-12, -6);
            }
            else
            {
                ccrt.sizeDelta = new Vector2(0, pad * 2f + projects.Count * rowH + Mathf.Max(0, projects.Count - 1) * gap);
                for (int i = 0; i < projects.Count; i++)
                    BuildPackRow(content.transform, projects[i], i, rowH, gap, pad);
            }

            // Back to hub - bottom of the card.
            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("Back", "Back to hub", card.transform, Btn, 220, 44);
            var brt = backGO.GetComponent<RectTransform>(); brt.anchorMin = new Vector2(0.5f, 0); brt.anchorMax = new Vector2(0.5f, 0); brt.pivot = new Vector2(0.5f, 0);
            brt.anchoredPosition = new Vector2(0, 18); brt.sizeDelta = new Vector2(220, 44);
            SetLeadingIcon(backGO, "back");
            backBtn.onClick.AddListener((UnityAction)(() => { Close(); _ctx?.ReturnToHub(); }));

            Interactions.PolishButtons(_screen.transform);   // rounded corners + hover/press/disabled states
        }

        // One pack row: a rounded card with the pack name (left), tattoo count + a trash button (right).
        // Click the row opens the pack; the trash button confirms then deletes the pack folder + refreshes.
        private static void BuildPackRow(Transform content, string folder, int index, float rowH, float gap, float pad)
        {
            string f = folder;
            int tcount = ProjectStore.TattooCount(f);
            string display = ProjectStore.DisplayName(f);
            var row = new GameObject("pack_" + f); row.transform.SetParent(content, false);
            var rrt = row.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(0.5f, 1);
            rrt.sizeDelta = new Vector2(0, rowH); rrt.anchoredPosition = new Vector2(0, -(pad + index * (rowH + gap)));
            var rbg = row.AddComponent<Image>(); rbg.color = Theme.BgElevated;
            var rowBtn = row.AddComponent<Button>(); rowBtn.targetGraphic = rbg;
            rowBtn.onClick.AddListener((UnityAction)(() => OpenProject(f)));

            var name = UIFactory.Text("nm", display, row.transform, 15, TextAnchor.MiddleLeft, FontStyle.Bold);
            name.color = Theme.TextPrimary; name.raycastTarget = false; name.horizontalOverflow = HorizontalWrapMode.Overflow;
            var nmrt = name.rectTransform; nmrt.anchorMin = new Vector2(0, 0); nmrt.anchorMax = new Vector2(1, 1); nmrt.offsetMin = new Vector2(16, 0); nmrt.offsetMax = new Vector2(-160, 0);

            var cnt = UIFactory.Text("ct", tcount + (tcount == 1 ? " tattoo" : " tattoos"), row.transform, 13, TextAnchor.MiddleRight);
            cnt.color = Theme.TextMuted; cnt.raycastTarget = false;
            var ctrt = cnt.rectTransform; ctrt.anchorMin = new Vector2(1, 0.5f); ctrt.anchorMax = new Vector2(1, 0.5f); ctrt.pivot = new Vector2(1, 0.5f);
            ctrt.sizeDelta = new Vector2(100, rowH); ctrt.anchoredPosition = new Vector2(-52, 0);

            var delBtn = IconButton(row.transform, ToolIcons.Get("trash"), Color.Lerp(Theme.Button, Theme.Danger, 0.55f));
            var drt = delBtn.GetComponent<RectTransform>();
            drt.anchorMin = new Vector2(1, 0.5f); drt.anchorMax = new Vector2(1, 0.5f); drt.pivot = new Vector2(1, 0.5f);
            drt.sizeDelta = new Vector2(30, 30); drt.anchoredPosition = new Vector2(-12, 0);
            delBtn.onClick.AddListener((UnityAction)(() => Components.ConfirmDialog(_canvasGO.transform,
                "Delete pack?", "Delete '" + display + "' and its " + tcount + " tattoo(s)? This permanently removes the pack folder and cannot be undone.",
                "Delete pack", () => { ProjectStore.Delete(f); ShowProjectSelect(); })));
        }

        private static void CreateProjectFlow()
        {
            string baseName = "New Tattoo Pack"; string name = baseName; int i = 2;
            while (ProjectStore.Exists(name)) { name = baseName + " " + i; i++; }
            _project = ProjectStore.Create(name, "");
            _selectedTattoo = null; _selected = null; _undo.Clear(); _redo.Clear();
            ShowEditor();
        }

        private static void OpenProject(string folder)
        {
            _project = ProjectStore.Load(folder);
            if (_project == null) { SetStatus("Failed to open '" + folder + "'"); return; }
            _selectedTattoo = null; _selected = null; _undo.Clear(); _redo.Clear();
            ShowEditor();
        }

        // --- screen: editor (transparent background -> character visible) ---

        private static void ShowEditor()
        {
          try
          {
            ClearScreen();
            Preview.SetFocusPlacement(_tab);   // aim the camera at the face when the Face tab is active, chest otherwise
            _screen = UIFactory.Panel("Editor", _canvasGO.transform, Clear, fullAnchor: true);
            // The root must not block clicks where it is transparent; only the panels do.
            var rootImg = _screen.GetComponent<Image>(); if (rootImg != null) rootImg.raycastTarget = false;

            _uvSize = Mathf.Min(Screen.width * 0.30f, Screen.height * 0.80f);

            // Top bar - three clusters: Identity | History | Output (clothes/underwear moved to the stage controls).
            var top = UIFactory.Panel("Top", _screen.transform, Panel);
            Place(top, 0f, 0.92f, 1f, 1f);
            var tcl = top.AddComponent<HorizontalLayoutGroup>();
            tcl.padding = new RectOffset(20, 20, 8, 8); tcl.spacing = 10; tcl.childAlignment = TextAnchor.MiddleLeft;
            tcl.childForceExpandWidth = false; tcl.childForceExpandHeight = true;

            // Identity: wordmark + editable pack name + switch.
            var inkT = UIFactory.Text("Ink", "Inkubator", top.transform, 18, TextAnchor.MiddleLeft, FontStyle.Bold);
            inkT.color = Theme.TextPrimary; AddLE(inkT.gameObject, 0, 92);
            var packInput = MakeInput(top.transform, _project.Name, (UnityAction<string>)(s =>
            {
                _project.Name = string.IsNullOrWhiteSpace(s) ? _project.Name : s.Trim();
                ProjectStore.Save(_project);
            }));
            AddLE(packInput.gameObject, 34, 200);
            MakeBarButton(top.transform, "Switch pack", Btn, () => { ProjectStore.Save(_project); ShowProjectSelect(); }, 132, "switchpack");
            BarDivider(top.transform);
            // History.
            MakeBarButton(top.transform, "Save", Btn, () => { ProjectStore.Save(_project); SetStatus("Saved.", Severity.Success); }, 92, "save");
            var undoBtn = MakeBarButton(top.transform, "Undo", Btn, Undo, 46, "undo", true);
            var redoBtn = MakeBarButton(top.transform, "Redo", Btn, Redo, 46, "redo", true);
            undoBtn.interactable = _undo.Count > 0;   // visible disabled state when there is nothing to undo/redo
            redoBtn.interactable = _redo.Count > 0;
            BarSpacer(top.transform);
            // Output: Export is the one primary action.
            MakeBarButton(top.transform, "Export", Accent, () => { ProjectStore.Save(_project); _lastExport = null; ShowReviewExport(); }, 124, "export");
            MakeBarButton(top.transform, "Back to hub", Btn, () => { ProjectStore.Save(_project); Close(); _ctx?.ReturnToHub(); }, 132, "back");

            // Left rail - navigation only: body-part tabs (top) + the tattoo list (rest).
            var left = UIFactory.Panel("Left", _screen.transform, Panel);
            Place(left, 0f, 0f, 0.20f, 0.92f);
            const float tabsBandH = 212f;   // fixed height for the body-part tabs; the list fills the rest right below
            BuildTabs(SubRegionPx(left, "TabsR", tabsBandH, true).transform);
            BuildTattooList(SubRegionPx(left, "ListR", tabsBandH, false).transform);

            // Center stage (0.20 - 0.46): the 3D character shows here; turn/zoom + clothes/underwear controls float below it.
            BuildStageControls();

            // UV canvas (center-right): framed editing surface with the Move/Rotate/Scale tool switcher.
            BuildUvCanvas();

            // Right panel: contextual decal Inspector OR the import library (segmented toggle).
            BuildRightPanel();

            _statusText = null;   // transient feedback lives in the Toast stack now

            RebuildDecalSprites();
            PreviewAll();
            if (!_layersLogged) { _layersLogged = true; Preview.LogLayers(); }

            Interactions.PolishButtons(_screen.transform);   // rounded corners + hover/press/disabled states
          }
          catch (Exception e) { Core.Log?.Error("[editor] ShowEditor failed: " + e); }
        }

        // A thin vertical separator + a flexible spacer for the top-bar clusters.
        private static void BarDivider(Transform parent)
        {
            var d = UIFactory.Panel("bardiv", parent, Theme.Hairline);
            var im = d.GetComponent<Image>(); if (im != null) im.raycastTarget = false;
            var le = d.AddComponent<LayoutElement>(); le.minWidth = 1; le.preferredWidth = 1; le.flexibleWidth = 0; le.minHeight = 22;
        }

        private static void BarSpacer(Transform parent)
        {
            var s = UIFactory.Panel("barspace", parent, Clear);
            var im = s.GetComponent<Image>(); if (im != null) im.raycastTarget = false;
            var le = s.AddComponent<LayoutElement>(); le.flexibleWidth = 1; le.minWidth = 10;
        }

        // Center-stage controls (under the rig): turn / zoom + clothes / underwear toggles. Two sibling rows (no nested
        // layout groups - those collapse on this canvas).
        private static void BuildStageControls()
        {
            var rowA = UIFactory.Panel("StageA", _screen.transform, Clear);
            Place(rowA, 0.20f, 0.085f, 0.46f, 0.135f);
            var aImg = rowA.GetComponent<Image>(); if (aImg != null) aImg.raycastTarget = false;
            var ah = rowA.AddComponent<HorizontalLayoutGroup>();
            ah.spacing = 6; ah.childAlignment = TextAnchor.MiddleCenter; ah.childForceExpandWidth = false; ah.childForceExpandHeight = false;
            ah.padding = new RectOffset(8, 8, 4, 4);
            StageBtn(rowA.transform, "turn left", () => Preview.RotateAvatar(-25f), 46, "turn_l");
            StageBtn(rowA.transform, "turn right", () => Preview.RotateAvatar(25f), 46, "turn_r");
            StageBtn(rowA.transform, "zoom out", () => { Preview.ZoomCamera(-1f); UpdateZoomLabel(); }, 46, "zoom_out");
            StageBtn(rowA.transform, "zoom in", () => { Preview.ZoomCamera(1f); UpdateZoomLabel(); }, 46, "zoom_in");
            _zoomLabel = UIFactory.Text("zoom", Preview.ZoomPercent + "%", rowA.transform, 13, TextAnchor.MiddleCenter, FontStyle.Bold);
            _zoomLabel.color = Theme.TextMuted; AddLE(_zoomLabel.gameObject, 30, 54);

            var rowB = UIFactory.Panel("StageB", _screen.transform, Clear);
            Place(rowB, 0.20f, 0.028f, 0.46f, 0.078f);
            var bImg = rowB.GetComponent<Image>(); if (bImg != null) bImg.raycastTarget = false;
            var bh = rowB.AddComponent<HorizontalLayoutGroup>();
            bh.spacing = 8; bh.childAlignment = TextAnchor.MiddleCenter; bh.childForceExpandWidth = false; bh.childForceExpandHeight = false;
            bh.padding = new RectOffset(8, 8, 4, 4);
            ClothesChip(rowB.transform);
            UnderwearChip(rowB.transform);
        }

        private static void StageBtn(Transform parent, string label, Action onClick, float w, string icon = null)
        {
            var (go, btn, _) = UIFactory.ButtonWithLabel("st_" + label, label, parent, Btn, w, 34);
            AddLE(go, 34, w);
            btn.onClick.AddListener((UnityAction)(() => onClick()));
            if (icon != null) SetLeadingIcon(go, icon, true);
        }

        // A toggle chip that lights up (accent) when active; recolors itself in place on click (no rebuild).
        private static void ClothesChip(Transform parent)
        {
            bool on = Preview.IsClothingStripped;
            var (go, btn, txt) = UIFactory.ButtonWithLabel("clchip", on ? "Show clothes" : "Hide clothes", parent, on ? Accent : Btn, 130, 34);
            AddLE(go, 34, 130);
            btn.onClick.AddListener((UnityAction)(() =>
            {
                bool s = Preview.SetStripClothing(!Preview.IsClothingStripped);
                txt.text = s ? "Show clothes" : "Hide clothes";
                var im = btn.targetGraphic as Image; if (im != null) { im.color = s ? Accent : Btn; Interactions.ApplyStates(btn); }
                PreviewAll();
                SetStatus(s ? "Clothes hidden - body tattoos now visible." : "Clothes shown.");
            }));
        }

        private static void UnderwearChip(Transform parent)
        {
            bool on = Preview.IsUnderwearStripped;
            var (go, btn, txt) = UIFactory.ButtonWithLabel("uwchip", on ? "Show underwear" : "Hide underwear", parent, on ? Accent : Btn, 140, 34);
            AddLE(go, 34, 140);
            btn.onClick.AddListener((UnityAction)(() =>
            {
                bool s = Preview.SetStripUnderwear(!Preview.IsUnderwearStripped);
                txt.text = s ? "Show underwear" : "Hide underwear";
                var im = btn.targetGraphic as Image; if (im != null) { im.color = s ? Accent : Btn; Interactions.ApplyStates(btn); }
                PreviewAll();
                SetStatus(s ? "Underwear hidden." : "Underwear shown.");
            }));
        }

        private static void UpdateZoomLabel() { if (_zoomLabel != null) _zoomLabel.text = Preview.ZoomPercent + "%"; }

        private static void UpdateToolSeg() { if (_toolSegButtons != null) Components.SetSegmentedActive(_toolSegButtons, (int)_tool); }

        // Right panel host (built once per ShowEditor); its content is swapped by RefreshRightPanel.
        private static void BuildRightPanel()
        {
            _rightPanel = UIFactory.Panel("Right", _screen.transform, Panel);
            Place(_rightPanel, 0.78f, 0f, 1f, 0.92f);
            RefreshRightPanel(false);   // the screen-wide PolishButtons sweep in ShowEditor will style it once
        }

        // Swap the right panel between the decal Inspector and the import library. Called standalone on selection
        // change (polish = true) so it styles its own new buttons; called with polish = false during a full rebuild.
        private static void RefreshRightPanel(bool polish = true)
        {
            if (_rightPanel == null) return;
            UIFactory.ClearChildren(_rightPanel.transform);
            // The inspector controls (if any) were just destroyed - drop the refs so the Import view never holds dangling ones.
            _sizeVal = _rotVal = _opVal = null; _scaleSlider = _rotSlider = _opSlider = null;

            int active = _rightTab == RightTab.Inspector ? 0 : 1;
            var seg = Components.Segmented(_rightPanel.transform, new[] { "Inspector", "Import" }, active,
                i => { _rightTab = i == 0 ? RightTab.Inspector : RightTab.Import; RefreshRightPanel(); }, out _, false, 6f);
            var segRT = seg.GetComponent<RectTransform>();
            segRT.anchorMin = new Vector2(0, 1); segRT.anchorMax = new Vector2(1, 1); segRT.pivot = new Vector2(0.5f, 1);
            segRT.sizeDelta = new Vector2(-2 * Gutter, 32); segRT.anchoredPosition = new Vector2(0, -14);   // gutter inset each side

            _rightContent = UIFactory.Panel("RightContent", _rightPanel.transform, Clear);
            var cimg = _rightContent.GetComponent<Image>(); if (cimg != null) cimg.raycastTarget = false;
            var crt = _rightContent.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(1, 1);
            crt.offsetMin = new Vector2(0, 0); crt.offsetMax = new Vector2(0, -60);

            if (_rightTab == RightTab.Inspector) BuildInspector(_rightContent.transform);
            else BuildImportList(_rightContent.transform);

            if (polish) Interactions.PolishButtons(_rightPanel.transform);
        }

        private static void BuildTabs(Transform parent)
        {
            var hdr = TopLabel(parent, "TabsH", "Body part", -14, 22, 16, FontStyle.Bold);

            float y = -44;
            foreach (Placement p in Placements.All)
            {
                Placement pp = p; bool active = p == _tab;
                int count = TattoosFor(p).Count;
                var (go, btn, _) = UIFactory.ButtonWithLabel("tab_" + p, Label(p) + (count > 0 ? "  (" + count + ")" : ""), parent, active ? BtnSel : Btn, 0, 36);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
                rt.offsetMin = new Vector2(Gutter, y - 36); rt.offsetMax = new Vector2(-Gutter, y);   // offsets only (no sizeDelta)
                btn.onClick.AddListener((UnityAction)(() => { _tab = pp; _selected = null; _selectedTattoo = null; ShowEditor(); }));
                y -= 40;
            }
        }

        private static void BuildTattooList(Transform parent)
        {
            TopLabel(parent, "TLH", "Tattoos - " + Label(_tab), -14, 22, 15, FontStyle.Bold);

            var (addGO, addBtn, _) = UIFactory.ButtonWithLabel("AddTat", "Add tattoo", parent, Accent, 0, 32);
            var art = addGO.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0, 1); art.anchorMax = new Vector2(1, 1); art.pivot = new Vector2(0.5f, 1);
            art.offsetMin = new Vector2(Gutter, -80); art.offsetMax = new Vector2(-Gutter, -44);   // offsets only (sizeDelta.x would wipe the inset)
            SetLeadingIcon(addGO, "add");
            addBtn.onClick.AddListener((UnityAction)(() =>
            {
                Snapshot();
                _selectedTattoo = CreateTattoo(_tab, null); _selected = null;
                ProjectStore.Save(_project); ShowEditor();
            }));

            // Scroll view + vertical stack of rows, built manually with explicit anchored positions.
            // Nested LayoutGroups (ScrollableVerticalList + per-row HorizontalLayoutGroup) collapsed the row
            // children to the world origin here; the import grid uses this same manual pattern and renders fine.
            var scrollGO = new GameObject("TattooScroll"); scrollGO.transform.SetParent(parent, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1);
            srt.offsetMin = new Vector2(Gutter, 2); srt.offsetMax = new Vector2(-Gutter, -92);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false; TuneScroll(scroll);

            var vp = new GameObject("Viewport"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vprt;

            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var crt = content.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            scroll.content = crt;

            var tattoos = TattoosFor(_tab);
            if (tattoos.Count == 0)
            {
                crt.sizeDelta = new Vector2(0, 60);
                var none = UIFactory.Text("None", "No tattoos on this body part yet.\nClick '+ Add tattoo' or an import below.", content.transform, 12, TextAnchor.UpperCenter);
                none.color = new Color(0.6f, 0.6f, 0.65f);
                var nrt = none.rectTransform; nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1);
                nrt.pivot = new Vector2(0.5f, 1); nrt.anchoredPosition = new Vector2(0, -8); nrt.sizeDelta = new Vector2(-4, 48);
                return;
            }

            const float rowH = 44f, gap = 6f, pad = 4f;
            crt.sizeDelta = new Vector2(0, pad * 2f + tattoos.Count * rowH + Mathf.Max(0, tattoos.Count - 1) * gap);
            for (int i = 0; i < tattoos.Count; i++)
                BuildTattooRow(content.transform, tattoos[i], i, rowH, gap, pad);
        }

        private static void BuildTattooRow(Transform content, TattooEntry t, int index, float rowH, float gap, float pad)
        {
            TattooEntry tat = t;
            bool sel = t == _selectedTattoo;

            var row = new GameObject("row_" + t.Id); row.transform.SetParent(content, false);
            var rrt = row.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(0.5f, 1);
            rrt.sizeDelta = new Vector2(0, rowH);   // fill the gutter-inset scroll width so the row card aligns to the gutter
            rrt.anchoredPosition = new Vector2(0, -(pad + index * (rowH + gap)));
            var rbg = row.AddComponent<Image>(); rbg.color = sel ? BtnSel : Theme.BgElevated;
            var rowBtn = row.AddComponent<Button>(); rowBtn.targetGraphic = rbg;
            rowBtn.onClick.AddListener((UnityAction)(() => { _selectedTattoo = tat; _selected = null; ShowEditor(); }));

            // eye toggle (left) - show/hide this tattoo in the preview
            var eyeBtn = IconButton(row.transform, ToolIcons.Get(tat.Visible ? "eye" : "eye_off"), tat.Visible ? Color.Lerp(Theme.Button, Theme.Success, 0.6f) : Theme.Button);
            var ert = eyeBtn.GetComponent<RectTransform>();
            ert.anchorMin = new Vector2(0, 0.5f); ert.anchorMax = new Vector2(0, 0.5f); ert.pivot = new Vector2(0, 0.5f);
            ert.sizeDelta = new Vector2(30, 30); ert.anchoredPosition = new Vector2(8, 0);
            eyeBtn.onClick.AddListener((UnityAction)(() =>
            {
                tat.Visible = !tat.Visible; ProjectStore.Save(_project);
                RefreshPlacementPreview(tat.PlacementEnum); ShowEditor();
            }));

            // delete (right) - trash icon opens a confirm dialog (replaces the old hidden two-step "Sure?").
            var delBtn = IconButton(row.transform, ToolIcons.Get("trash"), Color.Lerp(Theme.Button, Theme.Danger, 0.55f));
            var drt = delBtn.GetComponent<RectTransform>();
            drt.anchorMin = new Vector2(1, 0.5f); drt.anchorMax = new Vector2(1, 0.5f); drt.pivot = new Vector2(1, 0.5f);
            drt.sizeDelta = new Vector2(30, 30); drt.anchoredPosition = new Vector2(-8, 0);
            delBtn.onClick.AddListener((UnityAction)(() => Components.ConfirmDialog(_canvasGO.transform,
                "Delete tattoo?", "Delete '" + tat.Name + "' and its " + tat.Decals.Count + " image(s)? You can still undo with Ctrl+Z.",
                "Delete tattoo", () => RemoveTattoo(tat))));
            float rightInset = 8 + 30 + 8;   // gap + trash + gap

            // image count (how many images compose this tattoo) - sits just left of the delete control
            int n = tat.Decals.Count;
            var cnt = UIFactory.Text("cnt", n + (n == 1 ? " img" : " imgs"), row.transform, 11, TextAnchor.MiddleRight);
            cnt.color = Theme.TextMuted; cnt.raycastTarget = false;
            var crt2 = cnt.rectTransform;
            crt2.anchorMin = new Vector2(1, 0.5f); crt2.anchorMax = new Vector2(1, 0.5f); crt2.pivot = new Vector2(1, 0.5f);
            crt2.sizeDelta = new Vector2(54, 24); crt2.anchoredPosition = new Vector2(-rightInset, 0);
            float cntLeft = rightInset + 54 + 8;

            // name field (fills the middle between eye and count) - this becomes the shop name.
            // NOTE: do NOT set anchoredPosition here - on a stretched rect it re-centers and overwrites the
            // asymmetric offsets, which made the field overlap (and hide) the image count.
            var input = MakeInput(row.transform, tat.Name, (UnityAction<string>)(s =>
            {
                tat.Name = string.IsNullOrWhiteSpace(s) ? tat.Name : s.Trim();
                ProjectStore.Save(_project);
            }));
            var irt = input.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0); irt.anchorMax = new Vector2(1, 1);
            irt.offsetMin = new Vector2(46, 7); irt.offsetMax = new Vector2(-cntLeft, -7);
        }

        private static Button IconButton(Transform parent, Texture2D icon, Color bg)
        {
            var go = new GameObject("iconbtn"); go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>(); img.color = bg;
            var btn = go.AddComponent<Button>(); btn.targetGraphic = img;
            if (icon != null)
            {
                var ic = new GameObject("ic"); ic.transform.SetParent(go.transform, false);
                var irt = ic.AddComponent<RectTransform>(); irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = new Vector2(3, 3); irt.offsetMax = new Vector2(-3, -3);
                var iimg = ic.AddComponent<Image>(); iimg.sprite = Sprite.Create(icon, new Rect(0, 0, icon.width, icon.height), new Vector2(0.5f, 0.5f), 100f);
                iimg.raycastTarget = false; iimg.preserveAspect = true;
            }
            return btn;
        }

        private static InputField MakeInput(Transform parent, string value, UnityAction<string> onEnd)
        {
            var go = new GameObject("input"); go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>(); img.color = Theme.SurfaceInput;   // darker than panels -> the field reads as an input
            img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced;
            go.AddComponent<RectMask2D>();   // clip the text to the field so long names don't overflow onto the icons
            var input = go.AddComponent<InputField>();

            var tgo = new GameObject("Text"); tgo.transform.SetParent(go.transform, false);
            var trt = tgo.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(8, 2); trt.offsetMax = new Vector2(-6, -2);
            var txt = tgo.AddComponent<Text>(); txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); txt.fontSize = 14; txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft; txt.supportRichText = false; txt.horizontalOverflow = HorizontalWrapMode.Overflow;

            input.textComponent = txt;
            input.lineType = InputField.LineType.SingleLine;
            input.text = value ?? "";
            // AddListener(null) registers a null delegate that survives AddListener but NREs at invoke time
            // (DeactivateInputField -> onEndEdit.Invoke -> AllowInvoke(null)) on every deselect, which also aborts
            // the field's highlight-clear and leaves stuck text selections. Only attach a real callback.
            if (onEnd != null) { try { input.onEndEdit.AddListener(onEnd); } catch { } }
            return input;
        }

        // The decal Inspector (right panel) - roomy sliders + reset + flip toggles, replacing the old cramped -/+ pairs.
        private static void BuildInspector(Transform parent)
        {
            _sizeVal = _rotVal = _opVal = null;
            _scaleSlider = _rotSlider = _opSlider = null;

            if (_selected == null)
            {
                var t = UIFactory.Text("insEmpty", "No decal selected.\n\nPick an image from the Import tab, or click a decal on the canvas to edit it.", parent, 13, TextAnchor.UpperCenter);
                t.color = Theme.TextMuted; t.raycastTarget = false;
                var trt = t.rectTransform; trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1); trt.pivot = new Vector2(0.5f, 1);
                trt.offsetMin = new Vector2(Gutter, -160); trt.offsetMax = new Vector2(-Gutter, -28);
                return;
            }

            TopLabel(parent, "InsH", "Decal", -14, 24, 16, FontStyle.Bold);
            float y = -56f;
            _scaleSlider = SliderRow(parent, "Size", 0.004f, 2f, Mathf.Clamp(_selected.Scale, 0.004f, 2f),
                v => AdjustLive(d => d.Scale = Mathf.Clamp(v, 0.004f, 2f)), out _sizeVal, () => AdjustReset(d => d.Scale = 0.35f), ref y);
            _rotSlider = SliderRow(parent, "Rotate", 0f, 360f, Mathf.Repeat(_selected.RotationDeg, 360f),
                v => AdjustLive(d => d.RotationDeg = v), out _rotVal, () => AdjustReset(d => d.RotationDeg = 0f), ref y);
            _opSlider = SliderRow(parent, "Opacity", 0f, 1f, Mathf.Clamp01(_selected.Opacity),
                v => AdjustLive(d => d.Opacity = Mathf.Clamp01(v)), out _opVal, () => AdjustReset(d => d.Opacity = 1f), ref y);
            UpdateControlValues();

            var flipLab = UIFactory.Text("FlipL", "Flip", parent, 13, TextAnchor.MiddleLeft);
            flipLab.color = Theme.TextPrimary; flipLab.raycastTarget = false;
            var flrt = flipLab.rectTransform; flrt.anchorMin = new Vector2(0, 1); flrt.anchorMax = new Vector2(0, 1); flrt.pivot = new Vector2(0, 1);
            flrt.anchoredPosition = new Vector2(Gutter, y); flrt.sizeDelta = new Vector2(80, 20);
            y -= 24f;
            var (fhGO, fhBtn, _) = UIFactory.ButtonWithLabel("FlipH", "Flip H", parent, _selected.FlipX ? Accent : Btn, 0, 32);
            var fhrt = fhGO.GetComponent<RectTransform>(); fhrt.anchorMin = new Vector2(0, 1); fhrt.anchorMax = new Vector2(0.5f, 1); fhrt.pivot = new Vector2(0.5f, 1);
            fhrt.offsetMin = new Vector2(Gutter, y - 32); fhrt.offsetMax = new Vector2(-6, y);   // offsets define inset + height (no sizeDelta - it would wipe the inset)
            SetLeadingIcon(fhGO, "flip_h");
            fhBtn.onClick.AddListener((UnityAction)(() => AdjustReset(d => d.FlipX = !d.FlipX)));
            var (fvGO, fvBtn, _2) = UIFactory.ButtonWithLabel("FlipV", "Flip V", parent, _selected.FlipY ? Accent : Btn, 0, 32);
            var fvrt = fvGO.GetComponent<RectTransform>(); fvrt.anchorMin = new Vector2(0.5f, 1); fvrt.anchorMax = new Vector2(1, 1); fvrt.pivot = new Vector2(0.5f, 1);
            fvrt.offsetMin = new Vector2(6, y - 32); fvrt.offsetMax = new Vector2(-Gutter, y);
            SetLeadingIcon(fvGO, "flip_v");
            fvBtn.onClick.AddListener((UnityAction)(() => AdjustReset(d => d.FlipY = !d.FlipY)));
            y -= 48f;

            var (delGO, delBtn, _3) = UIFactory.ButtonWithLabel("Del", "Delete decal", parent, Theme.Danger, 0, 38);
            var drt = delGO.GetComponent<RectTransform>();
            drt.anchorMin = new Vector2(0, 1); drt.anchorMax = new Vector2(1, 1); drt.pivot = new Vector2(0.5f, 1);
            drt.offsetMin = new Vector2(Gutter, y - 38); drt.offsetMax = new Vector2(-Gutter, y);
            delBtn.onClick.AddListener((UnityAction)DeleteSelected);
        }

        // One slider row: label + live value readout + a small Reset + the slider beneath. Returns the slider.
        private static UnityEngine.UI.Slider SliderRow(Transform parent, string label, float min, float max, float value, Action<float> onChange, out Text valueText, Action onReset, ref float y)
        {
            var lab = UIFactory.Text("L_" + label, label, parent, 13, TextAnchor.MiddleLeft);
            lab.color = Theme.TextPrimary; lab.raycastTarget = false;
            var lrt = lab.rectTransform; lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(0, 1); lrt.pivot = new Vector2(0, 1);
            lrt.anchoredPosition = new Vector2(Gutter, y); lrt.sizeDelta = new Vector2(80, 20);

            valueText = UIFactory.Text("V_" + label, "", parent, 13, TextAnchor.MiddleRight, FontStyle.Bold);
            valueText.color = Theme.TextPrimary; valueText.raycastTarget = false;
            var vrt = valueText.rectTransform; vrt.anchorMin = new Vector2(1, 1); vrt.anchorMax = new Vector2(1, 1); vrt.pivot = new Vector2(1, 1);
            vrt.anchoredPosition = new Vector2(-(Gutter + 60), y); vrt.sizeDelta = new Vector2(110, 20);

            var (rGO, rBtn, rTxt) = UIFactory.ButtonWithLabel("R_" + label, "Reset", parent, Btn, 52, 20);
            if (rTxt != null) rTxt.fontSize = 11;
            var rrt = rGO.GetComponent<RectTransform>(); rrt.anchorMin = new Vector2(1, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(1, 1);
            rrt.anchoredPosition = new Vector2(-Gutter, y); rrt.sizeDelta = new Vector2(52, 20);
            if (onReset != null) rBtn.onClick.AddListener((UnityAction)(() => onReset()));

            var slider = Components.Slider(parent, min, max, value, onChange);
            var srt = slider.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 1); srt.anchorMax = new Vector2(1, 1); srt.pivot = new Vector2(0.5f, 1);
            // offsets define BOTH the side inset and the height; setting sizeDelta.x here would reset the
            // stretched width back to full-panel and wipe the inset (that was the "no padding" bug).
            srt.offsetMin = new Vector2(Gutter, y - 42); srt.offsetMax = new Vector2(-Gutter, y - 24);

            y -= 56f;
            return slider;
        }

        private static void UpdateControlValues()
        {
            if (_selected == null) return;
            _suppressSlider = true;   // setting slider.value programmatically must not re-fire the edit callbacks
            if (_sizeVal != null) _sizeVal.text = _selected.Scale.ToString("0.000");
            if (_scaleSlider != null) _scaleSlider.value = Mathf.Clamp(_selected.Scale, 0.004f, 2f);
            if (_rotVal != null) _rotVal.text = Mathf.RoundToInt(Mathf.Repeat(_selected.RotationDeg, 360f)) + " deg";
            if (_rotSlider != null) _rotSlider.value = Mathf.Repeat(_selected.RotationDeg, 360f);
            if (_opVal != null) _opVal.text = Mathf.RoundToInt(_selected.Opacity * 100f) + " %";
            if (_opSlider != null) _opSlider.value = Mathf.Clamp01(_selected.Opacity);
            _suppressSlider = false;
        }

        // Continuous edit from a slider drag: throttle undo snapshots, update the sprite + preview live.
        private static void AdjustLive(Action<Decal> mutate)
        {
            if (_suppressSlider || _selected == null) return;
            if (Time.time - _lastSliderSnap > 0.5f) { Snapshot(); _lastSliderSnap = Time.time; }
            mutate(_selected);
            UpdateSpriteTransform(_selected);
            UpdateControlValues();
            MarkDirty();
        }

        // A discrete edit (reset / flip): snapshot once, then rebuild the inspector so its controls reflect the new value.
        private static void AdjustReset(Action<Decal> mutate)
        {
            if (_selected == null) return;
            Snapshot();
            mutate(_selected);
            UpdateSpriteTransform(_selected);
            RefreshRightPanel();
            MarkDirty();
        }

        // --- UV canvas ---

        private static void BuildUvCanvas()
        {
            var holder = UIFactory.Panel("UVHolder", _screen.transform, Clear);
            Place(holder, 0.46f, 0.05f, 0.78f, 0.92f);
            var hImg = holder.GetComponent<Image>(); if (hImg != null) hImg.raycastTarget = false;

            var area = UIFactory.Panel("UV", holder.transform, CanvasBg);
            var areaImg = area.GetComponent<Image>(); if (areaImg != null) { areaImg.sprite = Theme.RoundedSprite(); areaImg.type = Image.Type.Sliced; }
            var frame = area.AddComponent<Outline>(); frame.effectColor = Theme.HairlineStrong; frame.effectDistance = new Vector2(1, -1);   // subtle canvas frame
            _uvArea = area.GetComponent<RectTransform>();
            _uvArea.anchorMin = new Vector2(0.5f, 0.5f); _uvArea.anchorMax = new Vector2(0.5f, 0.5f); _uvArea.pivot = new Vector2(0.5f, 0.5f);
            _uvArea.sizeDelta = new Vector2(_uvSize, _uvSize); _uvArea.anchoredPosition = Vector2.zero;

            // Move / Rotate / Scale tool switcher, docked above the canvas (replaces the old "Tool: ..." text line).
            var toolIcons = new[] { ToolIcons.Get("tool_move"), ToolIcons.Get("tool_rotate"), ToolIcons.Get("tool_scale") };
            var toolSeg = Components.Segmented(holder.transform, new[] { "Move", "Rotate", "Scale" }, (int)_tool, i => SetTool((Tool)i), out _toolSegButtons, false, 4f, toolIcons);
            var tsRT = toolSeg.GetComponent<RectTransform>();
            tsRT.anchorMin = new Vector2(0.5f, 1f); tsRT.anchorMax = new Vector2(0.5f, 1f); tsRT.pivot = new Vector2(0.5f, 1f);
            tsRT.sizeDelta = new Vector2(168, 32); tsRT.anchoredPosition = new Vector2(0, -6);

            Sprite tpl = LoadTemplate(_tab);
            if (tpl != null)
            {
                var bg = new GameObject("Template"); bg.transform.SetParent(_uvArea, false);
                var brt = bg.AddComponent<RectTransform>(); brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                var bi = bg.AddComponent<Image>(); bi.sprite = tpl; bi.color = new Color(1, 1, 1, 0.35f); bi.preserveAspect = true; bi.raycastTarget = false;
            }
            string capText = _selectedTattoo != null ? ("Editing: " + _selectedTattoo.Name) : (Label(_tab) + " - select or add a tattoo");
            var cap = UIFactory.Text("Cap", capText + "  (faint = inked region)", _uvArea, 13, TextAnchor.UpperCenter);
            cap.color = new Color(1, 1, 1, 0.45f);
            cap.rectTransform.anchorMin = new Vector2(0, 1); cap.rectTransform.anchorMax = new Vector2(1, 1); cap.rectTransform.pivot = new Vector2(0.5f, 1);
            cap.rectTransform.anchoredPosition = new Vector2(0, 18); cap.rectTransform.sizeDelta = new Vector2(0, 24);
        }

        private static void RebuildDecalSprites()
        {
            _decalSprites.Clear(); _selRing = null;
            if (_uvArea == null || _selectedTattoo == null) return;
            foreach (Decal d in _selectedTattoo.Decals) CreateSprite(d);
            UpdateSelectionRing();
        }

        private static void CreateSprite(Decal d)
        {
            string abs = ProjectStore.ResolveSource(_project, d);
            Sprite spr = LoadSprite(abs);
            var go = new GameObject("decal"); go.transform.SetParent(_uvArea, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>(); if (spr != null) img.sprite = spr; img.preserveAspect = true;
            _decalSprites[d] = go; UpdateSpriteTransform(d);
        }

        private static void UpdateSpriteTransform(Decal d)
        {
            if (!_decalSprites.TryGetValue(d, out var go) || go == null) return;
            var rt = go.GetComponent<RectTransform>(); var img = go.GetComponent<Image>();
            float aspect = 1f;
            if (img != null && img.sprite != null && img.sprite.texture != null && img.sprite.texture.width > 0)
                aspect = img.sprite.texture.height / (float)img.sprite.texture.width;
            float w = d.Scale * _uvSize;
            rt.sizeDelta = new Vector2(w, w * aspect);
            rt.anchoredPosition = new Vector2((d.U - 0.5f) * _uvSize, (d.V - 0.5f) * _uvSize);
            rt.localEulerAngles = new Vector3(0, 0, -d.RotationDeg);
            Vector3 sc = Vector3.one; if (d.FlipX) sc.x = -1; if (d.FlipY) sc.y = -1; rt.localScale = sc;
            if (img != null) { Color c = img.color; c.a = Mathf.Clamp01(d.Opacity); img.color = c; }
        }

        private static void UpdateSelectionRing()
        {
            if (_selRing != null) { UnityEngine.Object.Destroy(_selRing); _selRing = null; }
            if (_selected == null || !_decalSprites.TryGetValue(_selected, out var go) || go == null) return;
            _selRing = new GameObject("SelRing"); _selRing.transform.SetParent(go.transform, false);
            var rt = _selRing.AddComponent<RectTransform>(); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(-3, -3); rt.offsetMax = new Vector2(3, 3);
            // A HOLLOW frame (transparent centre) so it outlines the decal instead of covering it.
            var img = _selRing.AddComponent<Image>(); img.sprite = Theme.FrameSprite(); img.type = Image.Type.Sliced; img.color = Theme.AccentBorder; img.raycastTarget = false;
        }

        // --- import list ---

        private static void BuildImportList(Transform parent)
        {
            TopLabel(parent, "IH", "Import PNGs", -14, 22, 16, FontStyle.Bold);
            var sub = UIFactory.Text("IHsub", "Put PNGs in the Import folder, then click one to place it.", parent, 11, TextAnchor.UpperLeft);
            sub.color = Theme.TextMuted; sub.raycastTarget = false;
            sub.rectTransform.anchorMin = new Vector2(0, 1); sub.rectTransform.anchorMax = new Vector2(1, 1); sub.rectTransform.pivot = new Vector2(0.5f, 1);
            sub.rectTransform.offsetMin = new Vector2(Gutter, -78); sub.rectTransform.offsetMax = new Vector2(-Gutter, -40);

            var (openGO, openBtn, _) = UIFactory.ButtonWithLabel("OpenImp", "Open import folder", parent, Accent, 0, 40);
            var ort = openGO.GetComponent<RectTransform>(); ort.anchorMin = new Vector2(0, 1); ort.anchorMax = new Vector2(1, 1); ort.pivot = new Vector2(0.5f, 1);
            ort.offsetMin = new Vector2(Gutter, -124); ort.offsetMax = new Vector2(-Gutter, -84);   // offsets only (sizeDelta.x would wipe the inset)
            SetLeadingIcon(openGO, "folder");
            openBtn.onClick.AddListener((UnityAction)(() => OpenFolder(Paths.Import)));

            var (refreshGO, refreshBtn, _) = UIFactory.ButtonWithLabel("Refresh", "Refresh list", parent, Btn, 0, 34);
            var rrt = refreshGO.GetComponent<RectTransform>(); rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(0.5f, 1);
            rrt.offsetMin = new Vector2(Gutter, -164); rrt.offsetMax = new Vector2(-Gutter, -130);   // offsets only (sizeDelta.x would wipe the inset)
            SetLeadingIcon(refreshGO, "refresh");
            refreshBtn.onClick.AddListener((UnityAction)(() => ShowEditor()));

            // Scroll view + grid of square thumbnail tiles (built manually so there is exactly one layout group).
            var scrollGO = new GameObject("ImportsScroll"); scrollGO.transform.SetParent(parent, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(Gutter, 12); srt.offsetMax = new Vector2(-Gutter, -178);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false; TuneScroll(scroll);

            var vp = new GameObject("Viewport"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vprt;

            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var crt = content.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;   // content == viewport width exactly
            scroll.content = crt;

            // Responsive tiles: each tile is anchored to a fraction of the panel width, so the row always fills the
            // full width and adapts to any panel/resolution (no fixed px, no clipping). Square-ish, fixed height.
            const int cols = 2; const float gap = 8f, pad = 6f, cellH = 150f;
            string[] files = ImageLoader.ListImportImages();
            if (files.Length == 0)
            {
                var none = UIFactory.Text("None", "(no images)", content.transform, 13, TextAnchor.UpperCenter);
                none.color = new Color(0.6f, 0.6f, 0.65f);
                none.rectTransform.anchorMin = new Vector2(0, 1); none.rectTransform.anchorMax = new Vector2(1, 1);
                none.rectTransform.pivot = new Vector2(0.5f, 1); none.rectTransform.anchoredPosition = new Vector2(0, -10); none.rectTransform.sizeDelta = new Vector2(0, 24);
            }
            int rows = (files.Length + cols - 1) / cols;
            crt.sizeDelta = new Vector2(0, pad * 2f + rows * cellH + Mathf.Max(0, rows - 1) * gap);
            for (int i = 0; i < files.Length; i++)
                BuildImportTile(content.transform, files[i], i % cols, cols, i / cols, cellH, gap, pad);
        }

        private static void BuildImportTile(Transform parent, string path, int col, int cols, int row, float cellH, float gap, float pad)
        {
          try
          {
            string p = path;
            var cell = new GameObject("tile"); cell.transform.SetParent(parent, false);
            var crt = cell.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(col / (float)cols, 1f);
            crt.anchorMax = new Vector2((col + 1) / (float)cols, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = new Vector2(-gap, cellH);                       // width = column fraction - gap (responsive)
            crt.anchoredPosition = new Vector2(0f, -(pad + row * (cellH + gap)));
            var bg = cell.AddComponent<Image>(); bg.color = Theme.BgElevated;
            var btn = cell.AddComponent<Button>(); btn.targetGraphic = bg;
            btn.onClick.AddListener((UnityAction)(() => AddDecalFromImport(p)));

            var thumb = new GameObject("thumb"); thumb.transform.SetParent(cell.transform, false);
            var trt = thumb.AddComponent<RectTransform>();
            trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1); trt.offsetMin = new Vector2(4, 20); trt.offsetMax = new Vector2(-4, -4);
            var timg = thumb.AddComponent<Image>(); timg.preserveAspect = true; timg.raycastTarget = false;
            Sprite spr = LoadSprite(p); if (spr != null) timg.sprite = spr; else timg.color = new Color(1, 1, 1, 0.1f);

            string nm = Path.GetFileNameWithoutExtension(p);
            if (nm.Length > 14) nm = nm.Substring(0, 12) + "..";
            var lbl = UIFactory.Text("n", nm, cell.transform, 10, TextAnchor.LowerCenter);
            lbl.raycastTarget = false; lbl.color = new Color(0.82f, 0.84f, 0.88f);
            lbl.horizontalOverflow = HorizontalWrapMode.Overflow; lbl.verticalOverflow = VerticalWrapMode.Truncate;
            var lrt = lbl.rectTransform; lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0); lrt.pivot = new Vector2(0.5f, 0); lrt.anchoredPosition = new Vector2(0, 2); lrt.sizeDelta = new Vector2(-2, 16);

            // Format badge for non-PNG sources.
            string ext = Path.GetExtension(p).ToLowerInvariant().TrimStart('.');
            if (ext == "webp" || ext == "gif" || ext == "jpg" || ext == "jpeg")
            {
                var badge = UIFactory.Text("badge", ext.ToUpperInvariant(), cell.transform, 9, TextAnchor.UpperRight, FontStyle.Bold);
                badge.raycastTarget = false; badge.color = ext == "gif" ? new Color(1f, 0.7f, 0.3f) : new Color(0.5f, 0.8f, 1f);
                var brt = badge.rectTransform; brt.anchorMin = new Vector2(1, 1); brt.anchorMax = new Vector2(1, 1); brt.pivot = new Vector2(1, 1); brt.anchoredPosition = new Vector2(-4, -4); brt.sizeDelta = new Vector2(44, 14);
            }
          }
          catch (Exception e) { Core.Log?.Warning("[editor] tile '" + path + "': " + e.Message); }
        }

        private static void AddDecalFromImport(string absPath)
        {
            Snapshot();
            // No tattoo selected yet -> start a new one named after the imported file (a good default shop name).
            bool createdNew = _selectedTattoo == null;
            if (createdNew) _selectedTattoo = CreateTattoo(_tab, Path.GetFileNameWithoutExtension(absPath));

            string rel = ProjectStore.ImportSource(_project, absPath);
            if (rel == null) { SetStatus("Import failed."); return; }
            var d = new Decal { Source = rel, U = 0.5f, V = 0.5f, Scale = 0.35f, Order = _selectedTattoo.Decals.Count };
            _selectedTattoo.Decals.Add(d);
            _selected = d;
            _rightTab = RightTab.Inspector;     // the new decal is selected -> show its inspector
            ProjectStore.Save(_project);
            ShowEditor();                       // refresh the tattoo list + canvas (keeps _selectedTattoo / _selected)
            MarkDirty();
            SetStatus("Added '" + Path.GetFileName(absPath) + "' to tattoo '" + _selectedTattoo.Name + "'. Drag it on the canvas.");
        }

        private static void DeleteSelected()
        {
            if (_selected == null || _selectedTattoo == null) return;
            Snapshot();
            _selectedTattoo.Decals.Remove(_selected);
            if (_decalSprites.TryGetValue(_selected, out var go) && go != null) UnityEngine.Object.Destroy(go);
            _decalSprites.Remove(_selected); _selected = null;

            // An empty tattoo is not a shop entry -> drop it from the list when its last decal is removed.
            bool removedTattoo = false;
            if (_selectedTattoo.Decals.Count == 0)
            {
                _project.Tattoos.Remove(_selectedTattoo);
                _selectedTattoo = null;
                removedTattoo = true;
            }

            ProjectStore.Save(_project);
            RefreshPlacementPreview(_tab);
            ShowEditor();   // rebuild the tattoo list + canvas so the change shows immediately
            SetStatus(removedTattoo ? "Tattoo removed (last decal deleted)." : "Decal deleted.");
        }

        private static void DeleteSelectedTattoo() { if (_selectedTattoo != null) RemoveTattoo(_selectedTattoo); }

        private static void RemoveTattoo(TattooEntry tat)
        {
            if (tat == null) return;
            Snapshot();
            _project.Tattoos.Remove(tat);
            if (_selectedTattoo == tat) { _selectedTattoo = null; _selected = null; }
            ProjectStore.Save(_project);
            RefreshPlacementPreview(tat.PlacementEnum);
            ShowEditor();
            SetStatus("Tattoo '" + tat.Name + "' deleted.  (Ctrl+Z to undo)");
        }

        private static bool IsTypingInField()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return false;
            var go = es.currentSelectedGameObject;
            return go != null && go.GetComponent<InputField>() != null;
        }

        // --- undo / redo (coarse whole-project JSON snapshots) ---

        private static float _lastWheelSnap;

        private static void Snapshot()
        {
            try
            {
                if (_project == null) return;
                PushCapped(_undo, Newtonsoft.Json.JsonConvert.SerializeObject(_project));
                _redo.Clear();
            }
            catch { }
        }

        private static void PushCapped(Stack<string> s, string v)
        {
            s.Push(v);
            if (s.Count > 100)
            {
                var arr = s.ToArray();   // newest first
                s.Clear();
                for (int i = 99; i >= 0; i--) s.Push(arr[i]);
            }
        }

        private static void Undo()
        {
            if (_undo.Count == 0) { SetStatus("Nothing to undo."); return; }
            _redo.Push(Newtonsoft.Json.JsonConvert.SerializeObject(_project));
            RestoreFrom(_undo.Pop(), "Undo.");
        }

        private static void Redo()
        {
            if (_redo.Count == 0) { SetStatus("Nothing to redo."); return; }
            _undo.Push(Newtonsoft.Json.JsonConvert.SerializeObject(_project));
            RestoreFrom(_redo.Pop(), "Redo.");
        }

        private static void RestoreFrom(string json, string status)
        {
            try
            {
                string folder = _project?.FolderName;
                var p = Newtonsoft.Json.JsonConvert.DeserializeObject<Project>(json);
                if (p == null) return;
                p.FolderName = folder;
                p.Tattoos ??= new List<TattooEntry>();
                string selId = _selectedTattoo?.Id;
                _project = p;
                _selected = null;
                _selectedTattoo = null;
                if (selId != null) foreach (var t in _project.Tattoos) if (t.Id == selId) { _selectedTattoo = t; break; }
                ProjectStore.Save(_project);
                ShowEditor();
                PreviewAll();
                SetStatus(status);
            }
            catch (Exception e) { Core.Log?.Warning("[undo] restore: " + e.Message); }
        }

        // --- preview / export ---

        private static void PreviewAll()
        {
            if (_project == null) return;
            Preview.ResetSessionLayers();   // wipe any leftover/orphaned layers so the preview == this project exactly
            foreach (Placement p in Placements.All) RefreshPlacementPreview(p);
        }

        // Composite all VISIBLE tattoos of a placement into its single preview layer (or clear it if none).
        private static void RefreshPlacementPreview(Placement p)
        {
            if (_project == null) return;
            var decals = new List<Decal>();
            foreach (TattooEntry t in TattoosFor(p))
                if (t.Visible) decals.AddRange(t.Decals);
            Preview.ApplyPlacement(_project, p, decals);
        }

        private static void PreviewCurrent() => RefreshPlacementPreview(_tab);

        private static void ExportCurrent()
        {
            ProjectStore.Save(_project);
            ExportResult r = Exporter.Export(_project);
            if (r.Ok) SetStatus("Exported '" + _project.Name + "' (" + r.TattoosWritten + " tattoo(s)). Use 'Open export folder'.");
            else SetStatus("Export failed: " + (r.Warnings.Count > 0 ? r.Warnings[0] : "unknown"));
        }

        // --- screen: review & export (the pre-export step: edit all metadata + see a full overview + export with feedback) ---

        private static readonly Color ReviewBg = Theme.BgBase;
        private static readonly Color Hint = Theme.TextMuted;

        private static void ShowReviewExport()
        {
          try
          {
            ClearScreen();
            // Regression guard: with no UV canvas, Tick() fully no-ops (no avatar spin / wheel / Delete / Ctrl+Z here).
            _uvArea = null; _decalSprites.Clear(); _selected = null; _dropdownPopup = null; CloseIconModal();

            _screen = UIFactory.Panel("ReviewExport", _canvasGO.transform, ReviewBg, fullAnchor: true);

            var title = UIFactory.Text("Title", "Pack details & export  -  " + _project.Name, _screen.transform, 26, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(title.gameObject, 0f, 0.91f, 1f, 1f);

            // Left: pack metadata form. Right-top: tattoo overview table. Right-bottom: pre-flight + export + result.
            var left = UIFactory.Panel("PackForm", _screen.transform, Panel);
            Place(left, 0.03f, 0.115f, 0.40f, 0.90f);
            BuildPackForm(left.transform);

            var rtop = UIFactory.Panel("TattooOverview", _screen.transform, Panel);
            Place(rtop, 0.42f, 0.53f, 0.98f, 0.90f);
            BuildReviewTable(rtop.transform);

            var rbot = UIFactory.Panel("ExportPanel", _screen.transform, Panel);
            Place(rbot, 0.42f, 0.115f, 0.98f, 0.50f);
            BuildExportPanel(rbot.transform);

            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("BackEd", "Back to editor", _screen.transform, Btn, 220, 46);
            Place(backGO, 0.03f, 0.03f, 0.17f, 0.085f);
            backBtn.onClick.AddListener((UnityAction)(() => ShowEditor()));

            var (hubGO, hubBtn, _) = UIFactory.ButtonWithLabel("BackHub2", "Back to hub", _screen.transform, Btn, 200, 46);
            Place(hubGO, 0.185f, 0.03f, 0.31f, 0.085f);
            hubBtn.onClick.AddListener((UnityAction)(() => { ProjectStore.Save(_project); Close(); _ctx?.ReturnToHub(); }));

            _statusText = null;   // feedback shows as toasts now

            Interactions.PolishButtons(_screen.transform);   // rounded corners + hover/press/disabled states
          }
          catch (Exception e) { Core.Log?.Error("[review] ShowReviewExport failed: " + e); }
        }

        private static void BuildPackForm(Transform parent)
        {
            TopLabel(parent, "PFH", "Pack details", -14, 22, 16, FontStyle.Bold);
            float y = -52f;
            // Consistent rhythm: label -> input -> (hint) -> next field.
            const float lblStep = 22f;        // label (18) + 4 gap
            const float inpStep = 48f;        // input (30) + 18 field gap
            const float inpHintStep = 34f;    // input (30) + 4 gap before its hint
            const float hintStep = 34f;       // hint (16) + 18 field gap

            // Pack name + hint
            ReviewLabel(parent, y, "Pack name (the mod name)"); y -= lblStep;
            Text nameHint = null;
            ReviewInput(parent, y, _project.Name, -1, (UnityAction<string>)(s =>
            {
                if (!string.IsNullOrWhiteSpace(s)) { _project.Name = s.Trim(); ProjectStore.Save(_project); }
                if (nameHint != null) nameHint.text = NameHintText();
                RefreshShopIds();
            })); y -= inpHintStep;
            nameHint = ReviewHint(parent, y, NameHintText()); y -= hintStep;

            // Author
            ReviewLabel(parent, y, "Author"); y -= lblStep;
            ReviewInput(parent, y, _project.Author, -1, (UnityAction<string>)(s => { _project.Author = (s ?? "").Trim(); ProjectStore.Save(_project); })); y -= inpStep;

            // Version (semantic X.Y.Z) + inline normalized hint
            ReviewLabel(parent, y, "Version (e.g. 1.0.0)"); y -= lblStep;
            Text verHint = null;
            ReviewInput(parent, y, _project.ModVersion, 140, (UnityAction<string>)(s =>
            {
                _project.ModVersion = (s ?? "").Trim(); ProjectStore.Save(_project);
                if (verHint != null) verHint.text = VersionHintText();
                RefreshShopIds();
            }));
            verHint = ReviewHintAt(parent, y - 7, Gutter + 152f, 240f, VersionHintText()); y -= inpStep;

            // Website
            ReviewLabel(parent, y, "Website / source URL (optional)"); y -= lblStep;
            ReviewInput(parent, y, _project.WebsiteUrl, -1, (UnityAction<string>)(s => { _project.WebsiteUrl = (s ?? "").Trim(); ProjectStore.Save(_project); })); y -= inpStep;

            // Short description + live counter (on the label line, right-aligned)
            ReviewLabel(parent, y, "Short description");
            var counter = ReviewHintAt(parent, y, -1f, 80f, DescCount()); y -= lblStep;
            var descInput = ReviewInput(parent, y, _project.Description, -1, (UnityAction<string>)(s =>
            {
                _project.Description = s ?? ""; ProjectStore.Save(_project);
                if (counter != null) { counter.text = DescCount(); counter.color = _project.Description.Trim().Length > 250 ? Theme.DangerText : Hint; }
            }));
            try { descInput.onValueChanged.AddListener((UnityAction<string>)(s =>
            {
                if (counter != null) { int n = (s ?? "").Trim().Length; counter.text = n + " / 250"; counter.color = n > 250 ? Theme.DangerText : Hint; }
            })); } catch { }
            y -= inpStep;

            // License (input-styled dropdown with a chevron)
            ReviewLabel(parent, y, "License"); y -= lblStep;
            var licLabels = new string[Exporter.LicenseTokens.Length];
            for (int i = 0; i < licLabels.Length; i++) licLabels[i] = Exporter.LicenseLabel(Exporter.LicenseTokens[i]);
            Text licTxt = null;
            var (licGO, licLabel) = DropdownButton(parent, Exporter.LicenseLabel(_project.License), (rt) =>
            {
                int cur = Array.IndexOf(Exporter.LicenseTokens, _project.License); if (cur < 0) cur = 0;
                OpenDropdown(rt, licLabels, cur, (i) =>
                {
                    _project.License = Exporter.LicenseTokens[i];
                    if (licTxt != null) licTxt.text = Exporter.LicenseLabel(_project.License);
                    ProjectStore.Save(_project);
                });
            });
            licTxt = licLabel;
            var lrt = licGO.GetComponent<RectTransform>(); lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(1, 1); lrt.pivot = new Vector2(0.5f, 1);
            lrt.offsetMin = new Vector2(Gutter, y - 30); lrt.offsetMax = new Vector2(-Gutter, y); y -= inpStep;

            // Icon row: a clickable preview (the main affordance) + a secondary trigger + an immediate size check.
            ReviewLabel(parent, y, "Icon (Thunderstore cover, 256x256)"); y -= 26f;
            bool hasIcon = !string.IsNullOrEmpty(_project.IconSource);
            Sprite iconSpr = hasIcon ? LoadSprite(ProjectStore.ResolveRelative(_project, _project.IconSource)) : null;

            const float prev = 72f;
            var iconGO = new GameObject("iconprev"); iconGO.transform.SetParent(parent, false);
            var iprt = iconGO.AddComponent<RectTransform>(); iprt.anchorMin = new Vector2(0, 1); iprt.anchorMax = new Vector2(0, 1); iprt.pivot = new Vector2(0, 1);
            iprt.anchoredPosition = new Vector2(Gutter, y); iprt.sizeDelta = new Vector2(prev, prev);
            var ibg = iconGO.AddComponent<Image>(); ibg.color = Theme.SurfaceInput; ibg.sprite = Theme.RoundedSprite(); ibg.type = Image.Type.Sliced;
            var ibtn = iconGO.AddComponent<Button>(); ibtn.targetGraphic = ibg;
            ibtn.onClick.AddListener((UnityAction)(() => ChooseIconDialog()));
            var thumb = new GameObject("thumb"); thumb.transform.SetParent(iconGO.transform, false);
            var thrt = thumb.AddComponent<RectTransform>(); thrt.anchorMin = Vector2.zero; thrt.anchorMax = Vector2.one;
            var thimg = thumb.AddComponent<Image>(); thimg.preserveAspect = true; thimg.raycastTarget = false;
            if (iconSpr != null) { thimg.sprite = iconSpr; thrt.offsetMin = new Vector2(6, 6); thrt.offsetMax = new Vector2(-6, -6); }
            else
            {
                Texture2D ph = ToolIcons.Get("image");
                if (ph != null) thimg.sprite = Sprite.Create(ph, new Rect(0, 0, ph.width, ph.height), new Vector2(0.5f, 0.5f), 100f);
                thimg.color = Theme.WithAlpha(Theme.TextMuted, 0.55f);
                thrt.offsetMin = new Vector2(22, 22); thrt.offsetMax = new Vector2(-22, -22);   // smaller -> reads as a placeholder glyph
            }

            float ix = Gutter + prev + 14f;
            var (pickGO, pickBtn, _) = UIFactory.ButtonWithLabel("PickIcon", hasIcon ? "Replace..." : "Choose...", parent, Btn, 150, 30);
            var pkrt = pickGO.GetComponent<RectTransform>(); pkrt.anchorMin = new Vector2(0, 1); pkrt.anchorMax = new Vector2(0, 1); pkrt.pivot = new Vector2(0, 1); pkrt.anchoredPosition = new Vector2(ix, y); pkrt.sizeDelta = new Vector2(150, 30);
            pickBtn.onClick.AddListener((UnityAction)(() => ChooseIconDialog()));

            if (hasIcon)
            {
                var (rmGO, rmBtn, _) = UIFactory.ButtonWithLabel("RemoveIcon", "Remove", parent, Btn, 110, 30);
                var rmrt = rmGO.GetComponent<RectTransform>(); rmrt.anchorMin = new Vector2(0, 1); rmrt.anchorMax = new Vector2(0, 1); rmrt.pivot = new Vector2(0, 1); rmrt.anchoredPosition = new Vector2(ix + 158f, y); rmrt.sizeDelta = new Vector2(110, 30);
                rmBtn.onClick.AddListener((UnityAction)(() => { _project.IconSource = ""; ProjectStore.Save(_project); _lastExport = null; ShowReviewExport(); }));

                int iw = iconSpr != null && iconSpr.texture != null ? iconSpr.texture.width : 0;
                int ih = iconSpr != null && iconSpr.texture != null ? iconSpr.texture.height : 0;
                bool sizeOk = iw == 256 && ih == 256;
                string chip = iconSpr == null ? "Could not read this image" : (sizeOk ? "256x256" : iw + "x" + ih + " - resized to 256x256 on export");
                var sz = UIFactory.Text("iconsize", chip, parent, 11, TextAnchor.UpperLeft);
                sz.color = iconSpr == null ? Theme.DangerText : (sizeOk ? Theme.SuccessText : Hint); sz.raycastTarget = false;
                var szrt = sz.rectTransform; szrt.anchorMin = new Vector2(0, 1); szrt.anchorMax = new Vector2(1, 1); szrt.pivot = new Vector2(0.5f, 1);
                szrt.offsetMin = new Vector2(ix, y - 54); szrt.offsetMax = new Vector2(-Gutter, y - 38);
            }
            else
            {
                var iconHint = UIFactory.Text("iconhint", "PNG, ideally 256x256. Optional - a placeholder is generated otherwise.", parent, 11, TextAnchor.UpperLeft);
                iconHint.color = Hint; iconHint.raycastTarget = false;
                var ihrt = iconHint.rectTransform; ihrt.anchorMin = new Vector2(0, 1); ihrt.anchorMax = new Vector2(1, 1); ihrt.pivot = new Vector2(0.5f, 1);
                ihrt.offsetMin = new Vector2(ix, y - 54); ihrt.offsetMax = new Vector2(-Gutter, y - 38);
            }
        }

        private static string NameHintText() => "Exports as: " + Paths.Sanitize(_project.Name) + "   (Thunderstore: " + Exporter.ToThunderstoreName(_project.Name) + ")";
        // Only surface the normalized version when coercion actually changes the input (e.g. "1.0" -> "1.0.0"); an
        // already-valid "1.0.0" needs no echo. The jargon about where it's used was noise.
        private static string VersionHintText()
        {
            string norm = Exporter.NormalizeVersion(_project.ModVersion);
            return string.Equals((_project.ModVersion ?? "").Trim(), norm, StringComparison.Ordinal) ? "" : "becomes " + norm;
        }
        private static string DescCount() => _project.Description.Trim().Length + " / 250";

        // Form helpers - all aligned to the shared Gutter (offsets only). y is the TOP of the element.
        private static void ReviewLabel(Transform parent, float y, string text)
        {
            var l = UIFactory.Text("lbl", text, parent, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            l.color = Theme.TextPrimary; l.raycastTarget = false;
            var rt = l.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(Gutter, y - 18); rt.offsetMax = new Vector2(-Gutter, y);
        }

        private static InputField ReviewInput(Transform parent, float y, string value, float width, UnityAction<string> onEnd)
        {
            var inp = MakeInput(parent, value, onEnd);
            var rt = inp.GetComponent<RectTransform>();
            if (width <= 0)   // full width at the gutter
            {
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
                rt.offsetMin = new Vector2(Gutter, y - 30); rt.offsetMax = new Vector2(-Gutter, y);
            }
            else              // fixed width, left edge at the gutter
            {
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(Gutter, y); rt.sizeDelta = new Vector2(width, 30);
            }
            return inp;
        }

        private static Text ReviewHint(Transform parent, float y, string text)
        {
            var t = UIFactory.Text("hint", text, parent, 11, TextAnchor.UpperLeft);
            t.color = Hint; t.raycastTarget = false;
            var rt = t.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(Gutter, y - 16); rt.offsetMax = new Vector2(-Gutter, y);
            return t;
        }

        // A hint at a custom position: x < 0 = right-aligned at the gutter; x >= 0 = left edge at x (width w).
        private static Text ReviewHintAt(Transform parent, float y, float x, float w, string text)
        {
            var t = UIFactory.Text("hint", text, parent, 11, x < 0 ? TextAnchor.UpperRight : TextAnchor.UpperLeft);
            t.color = Hint; t.raycastTarget = false;
            var rt = t.rectTransform;
            if (x < 0)
            {
                rt.anchorMin = new Vector2(1, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1);
                rt.anchoredPosition = new Vector2(-Gutter, y); rt.sizeDelta = new Vector2(w, 16);
            }
            else
            {
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, 16);
            }
            return t;
        }

        // --- tattoo overview table ---

        private static void BuildReviewTable(Transform parent)
        {
            _reviewIdCells.Clear();
            var sorted = AllTattoosInOrder();
            TopLabel(parent, "TOH", "Tattoos (" + sorted.Count + ") - edit name, body part, price, shop id", -8, 22, 14, FontStyle.Bold);

            // pinned caption row (same gutter + column fractions as the data rows, so columns line up)
            var cap = UIFactory.Panel("cap", parent, Clear); var caprt = cap.GetComponent<RectTransform>();
            caprt.anchorMin = new Vector2(0, 1); caprt.anchorMax = new Vector2(1, 1); caprt.pivot = new Vector2(0.5f, 1); caprt.anchoredPosition = new Vector2(0, -38); caprt.sizeDelta = new Vector2(-2 * Gutter, 18);
            var capImg = cap.GetComponent<Image>(); if (capImg != null) capImg.raycastTarget = false;
            CaptionCell(cap.transform, 0.045f, 0.40f, "Name (shown in the shop)");
            CaptionCell(cap.transform, 0.405f, 0.56f, "Body part");
            CaptionCell(cap.transform, 0.565f, 0.66f, "Price");
            CaptionCell(cap.transform, 0.665f, 0.88f, "Shop id");
            CaptionCell(cap.transform, 0.885f, 1.0f, "Imgs");

            // manual scroll (nested LayoutGroups collapse here; mirror the import grid)
            var scrollGO = new GameObject("TblScroll"); scrollGO.transform.SetParent(parent, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(Gutter, 6); srt.offsetMax = new Vector2(-Gutter, -60);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false; TuneScroll(scroll);
            var vp = new GameObject("Viewport"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = vprt;
            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var crt = content.AddComponent<RectTransform>(); crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            scroll.content = crt;

            if (sorted.Count == 0)
            {
                crt.sizeDelta = new Vector2(0, 60);
                var none = UIFactory.Text("None", "No tattoos yet - go back and add some.", content.transform, 13, TextAnchor.UpperCenter);
                none.color = Hint; var nrt = none.rectTransform; nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1); nrt.pivot = new Vector2(0.5f, 1); nrt.anchoredPosition = new Vector2(0, -10); nrt.sizeDelta = new Vector2(-8, 40);
                return;
            }

            const float rowH = 38f, gap = 6f, pad = 4f;
            crt.sizeDelta = new Vector2(0, pad * 2f + sorted.Count * rowH + Mathf.Max(0, sorted.Count - 1) * gap);
            for (int i = 0; i < sorted.Count; i++) BuildReviewRow(content.transform, sorted[i], i, rowH, gap, pad);
        }

        private static void BuildReviewRow(Transform content, TattooEntry t, int index, float rowH, float gap, float pad)
        {
            TattooEntry tat = t;
            int imgs = tat.Decals.Count;
            var row = new GameObject("rrow_" + t.Id); row.transform.SetParent(content, false);
            var rrt = row.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(0.5f, 1);
            rrt.sizeDelta = new Vector2(0, rowH); rrt.anchoredPosition = new Vector2(0, -(pad + index * (rowH + gap)));   // fill the gutter-inset content so columns align with the caption
            var rbg = row.AddComponent<Image>(); rbg.color = imgs == 0 ? Theme.WarningSubtle : Theme.BgElevated;
            rbg.raycastTarget = false;

            // eye glyph (read-only)
            var eye = new GameObject("eye"); eye.transform.SetParent(row.transform, false);
            var eyeImg = eye.AddComponent<Image>(); eyeImg.sprite = LoadIconSprite(tat.Visible ? "eye" : "eye_off"); eyeImg.preserveAspect = true; eyeImg.raycastTarget = false;
            if (eyeImg.sprite == null) { eyeImg.color = tat.Visible ? new Color(0.4f, 0.7f, 0.5f) : new Color(0.4f, 0.4f, 0.45f); }
            SetCell(eye, 0.0f, 0.045f, 8f);

            // name (editable) - changing it auto-updates the shop id (id = packname_version_tattooname)
            var nameIn = MakeInput(row.transform, tat.Name, (UnityAction<string>)(s =>
            {
                if (!string.IsNullOrWhiteSpace(s)) { tat.Name = s.Trim(); ProjectStore.Save(_project); RefreshShopIds(); }
            }));
            SetCell(nameIn.gameObject, 0.05f, 0.40f);

            // placement (input-styled dropdown with a chevron; the row never moves when this changes)
            Text plTxt = null;
            var (plGO, plLabel) = DropdownButton(row.transform, Label(tat.PlacementEnum), (rt) => OpenPlacementDropdown(tat, rt, plTxt));
            plTxt = plLabel;
            SetCell(plGO, 0.405f, 0.56f);

            // price (editable, German-comma safe)
            var priceIn = MakeInput(row.transform, tat.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), null);
            SetCell(priceIn.gameObject, 0.565f, 0.66f);
            priceIn.onEndEdit.AddListener((UnityAction<string>)(s =>
            {
                if (TryParsePrice(s, out float f)) { tat.Price = f; ProjectStore.Save(_project); priceIn.text = f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
                else { priceIn.text = tat.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); SetStatus("Price must be a number (e.g. 250).", Severity.Warning); }
            }));

            // shop id (read-only; computed as packname_version_tattooname, refreshed live)
            var idCell = UIFactory.Panel("idcell", row.transform, Clear);
            var idImg = idCell.GetComponent<Image>(); if (idImg != null) idImg.raycastTarget = false;
            idCell.AddComponent<RectMask2D>();
            SetCell(idCell, 0.665f, 0.88f, 5f);
            var idTxt = UIFactory.Text("id", Exporter.ShopId(_project, tat), idCell.transform, 11, TextAnchor.MiddleLeft);
            idTxt.color = new Color(0.6f, 0.66f, 0.74f); idTxt.raycastTarget = false;
            idTxt.horizontalOverflow = HorizontalWrapMode.Overflow; idTxt.verticalOverflow = VerticalWrapMode.Truncate;
            var itrt = idTxt.rectTransform; itrt.anchorMin = Vector2.zero; itrt.anchorMax = Vector2.one; itrt.offsetMin = new Vector2(4, 0); itrt.offsetMax = new Vector2(-4, 0);
            _reviewIdCells.Add((tat, idTxt));

            // images count (read-only)
            var cnt = UIFactory.Text("imgs", imgs + (imgs == 1 ? " img" : " imgs"), row.transform, 11, TextAnchor.MiddleCenter);
            cnt.color = imgs == 0 ? new Color(0.95f, 0.7f, 0.4f) : Hint; cnt.raycastTarget = false;
            SetCell(cnt.gameObject, 0.885f, 1.0f);
        }

        private static void CaptionCell(Transform parent, float a, float b, string text)
        {
            var t = UIFactory.Text("c_" + text, text, parent, 11, TextAnchor.MiddleLeft, FontStyle.Bold);
            t.color = new Color(0.6f, 0.64f, 0.7f); t.raycastTarget = false;
            SetCell(t.gameObject, a, b, 0f);
        }

        // Stretch a child across the [a,b] horizontal fraction of its row, full height minus insets.
        private static void SetCell(GameObject go, float a, float b, float vInset = 5f, float hInset = 3f)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(a, 0f); rt.anchorMax = new Vector2(b, 1f);
            rt.offsetMin = new Vector2(hInset, vInset); rt.offsetMax = new Vector2(-hInset, -vInset);
        }

        // The review table lists tattoos in their stable PROJECT order, so a row never moves when its body part
        // changes (the body part is just an editable column).
        private static List<TattooEntry> AllTattoosInOrder() => new List<TattooEntry>(_project.Tattoos);

        // The shop-id cells in the review table, refreshed live when a name / pack name / version changes.
        private static readonly List<(TattooEntry t, Text cell)> _reviewIdCells = new List<(TattooEntry, Text)>();
        private static GameObject _dropdownPopup;   // open dropdown overlay (review screen), if any

        private static void CloseDropdown()
        {
            if (_dropdownPopup != null) { UnityEngine.Object.Destroy(_dropdownPopup); _dropdownPopup = null; }
        }

        // An input-styled dropdown trigger: value text (left) + a chevron-down icon (right). Returns the label Text
        // so callers can update the shown value. Click opens the popup anchored below it.
        private static (GameObject go, Text label) DropdownButton(Transform parent, string value, Action<RectTransform> onOpen)
        {
            var (go, btn, txt) = UIFactory.ButtonWithLabel("dd_trigger", value, parent, Theme.SurfaceInput, 0, 30);
            if (txt != null)
            {
                txt.alignment = TextAnchor.MiddleLeft; txt.fontStyle = FontStyle.Normal;
                var lrt = txt.rectTransform; lrt.offsetMin = new Vector2(12, lrt.offsetMin.y); lrt.offsetMax = new Vector2(-30, lrt.offsetMax.y);
            }
            Texture2D chev = ToolIcons.Get("chevron_down");
            if (chev != null)
            {
                var ic = new GameObject("chev"); ic.transform.SetParent(go.transform, false);
                var irt = ic.AddComponent<RectTransform>(); irt.anchorMin = new Vector2(1, 0.5f); irt.anchorMax = new Vector2(1, 0.5f); irt.pivot = new Vector2(1, 0.5f);
                irt.sizeDelta = new Vector2(16, 16); irt.anchoredPosition = new Vector2(-9, 0);
                var iimg = ic.AddComponent<Image>(); iimg.sprite = Sprite.Create(chev, new Rect(0, 0, chev.width, chev.height), new Vector2(0.5f, 0.5f), 100f);
                iimg.preserveAspect = true; iimg.raycastTarget = false;
            }
            var rt = go.GetComponent<RectTransform>();
            btn.onClick.AddListener((UnityAction)(() => onOpen(rt)));
            return (go, txt);
        }

        private static void OpenDropdown(RectTransform anchorRT, string[] optionLabels, int currentIdx, Action<int> onSelect)
        {
            CloseDropdown();
            if (_screen == null || anchorRT == null || optionLabels == null || optionLabels.Length == 0) return;

            // full-screen catcher closes the popup on an outside click
            var catcher = UIFactory.Panel("ddCatch", _screen.transform, new Color(0, 0, 0, 0.01f), fullAnchor: true);
            var cbtn = catcher.AddComponent<Button>(); cbtn.targetGraphic = catcher.GetComponent<Image>();
            cbtn.onClick.AddListener((UnityAction)(() => CloseDropdown()));
            _dropdownPopup = catcher;

            int n = optionLabels.Length;
            const float itemH = 30f, padV = 5f;
            // Anchor the popup to the TRIGGER (below it, same width), not to the mouse. On a ScreenSpaceOverlay
            // canvas the rect's world corners == screen pixels, and they are valid here (the UI is long rendered).
            // Trigger's on-screen rect from marshal-safe scalar getters. RectTransform.GetWorldCorners(Vector3[]) does
            // NOT write back through Il2CppInterop (the managed array stays all-zero -> popup dropped into the bottom-left
            // corner). position/rect/pivot are by-value struct getters and marshal correctly on this overlay canvas.
            float trigW = anchorRT.rect.width * anchorRT.lossyScale.x;
            float trigH = anchorRT.rect.height * anchorRT.lossyScale.y;
            Vector3 tp = anchorRT.position;                      // pivot position in screen px (ScreenSpaceOverlay)
            float leftX = tp.x - anchorRT.pivot.x * trigW;       // trigger's left edge, screen px
            float bottomY = tp.y - anchorRT.pivot.y * trigH;     // trigger's bottom edge, screen px
            float w = Mathf.Max(150f, trigW);
            float h = n * itemH + padV * 2f;

            var panel = UIFactory.Panel("ddPanel", catcher.transform, Theme.BgElevated);
            var pimg = panel.GetComponent<Image>(); if (pimg != null) { pimg.sprite = Theme.RoundedSprite(); pimg.type = Image.Type.Sliced; }
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.zero; prt.pivot = new Vector2(0, 1);
            prt.sizeDelta = new Vector2(w, h);
            float x = Mathf.Clamp(leftX, 0f, Mathf.Max(0f, Screen.width - w));
            float topY = bottomY - 4f;                              // popup top just below the trigger's bottom edge
            if (topY - h < 0f) topY = bottomY + trigH + h + 4f;     // flip above the trigger if it runs off the bottom
            prt.anchoredPosition = new Vector2(x, topY);
            var ol = panel.AddComponent<Outline>(); ol.effectColor = Theme.HairlineStrong; ol.effectDistance = new Vector2(1, -1);

            for (int i = 0; i < n; i++)
            {
                int idx = i; bool cur = i == currentIdx;
                var (oGO, oBtn, otxt) = UIFactory.ButtonWithLabel("dd_" + i, optionLabels[i], panel.transform, cur ? BtnSel : Theme.Button, 0, itemH - 4);
                if (otxt != null) { otxt.alignment = TextAnchor.MiddleLeft; otxt.fontStyle = FontStyle.Normal; }
                var ort = oGO.GetComponent<RectTransform>(); ort.anchorMin = new Vector2(0, 1); ort.anchorMax = new Vector2(1, 1); ort.pivot = new Vector2(0.5f, 1);
                ort.offsetMin = new Vector2(5, 0); ort.offsetMax = new Vector2(-5, 0); ort.sizeDelta = new Vector2(0, itemH - 4); ort.anchoredPosition = new Vector2(0, -(padV + i * itemH));
                if (otxt != null) { var lrt = otxt.rectTransform; lrt.offsetMin = new Vector2(8, lrt.offsetMin.y); }
                oBtn.onClick.AddListener((UnityAction)(() => { onSelect(idx); CloseDropdown(); }));
            }
            Interactions.PolishButtons(panel.transform);   // rounded + hover/press states on the option rows
        }

        // Body-part dropdown for a tattoo row. Changing it updates only this row's label + the preview; never reorders.
        private static void OpenPlacementDropdown(TattooEntry tat, RectTransform anchorRT, Text cellLabel)
        {
            var all = Placements.All;
            var labels = new string[all.Length];
            for (int i = 0; i < all.Length; i++) labels[i] = Label(all[i]);
            int cur = Array.IndexOf(all, tat.PlacementEnum); if (cur < 0) cur = 0;
            OpenDropdown(anchorRT, labels, cur, (i) =>
            {
                Placement old = tat.PlacementEnum; Placement p = all[i];
                if (p != old)
                {
                    tat.PlacementEnum = p; ProjectStore.Save(_project);
                    RefreshPlacementPreview(old); RefreshPlacementPreview(p);
                    if (cellLabel != null) cellLabel.text = Label(p);
                }
            });
        }

        private static void RefreshShopIds()
        {
            for (int i = 0; i < _reviewIdCells.Count; i++)
            {
                var (t, cell) = _reviewIdCells[i];
                if (cell != null) cell.text = Exporter.ShopId(_project, t);
            }
        }

        private static bool TryParsePrice(string s, out float f)
        {
            f = 0f;
            if (string.IsNullOrWhiteSpace(s)) return true;
            s = s.Trim().Replace(',', '.');
            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) { if (f < 0) f = 0; return true; }
            f = 0f; return false;
        }

        private static Sprite LoadIconSprite(string name)
        {
            try { var tex = ToolIcons.Get(name); return tex == null ? null : Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f); }
            catch { return null; }
        }

        // --- pre-flight + export + result panel ---

        private static void BuildExportPanel(Transform parent)
        {
            TopLabel(parent, "EPH", "Pre-flight & export", -14, 22, 16, FontStyle.Bold);

            var issues = Exporter.Validate(_project);
            bool hasError = false;
            float y = -50f;
            foreach (var it in issues)
            {
                if (it.Item1 == Exporter.Severity.Error) hasError = true;
                Severity sev = it.Item1 == Exporter.Severity.Error ? Severity.Danger
                             : it.Item1 == Exporter.Severity.Warning ? Severity.Warning : Severity.Info;
                y = Components.Banner(parent, sev, it.Item2, y, 30f, 8f, Gutter);
            }
            if (issues.Count == 0) y = Components.Banner(parent, Severity.Success, "Everything looks good - ready to export.", y, 30f, 8f, Gutter);

            y -= 4f;
            var (expGO, expBtn, expTxt) = UIFactory.ButtonWithLabel("DoExport", hasError ? "Fix errors to export" : "Export mod", parent,
                hasError ? new Color(0.18f, 0.19f, 0.23f, 1f) : Accent, 0, 48);
            var ert = expGO.GetComponent<RectTransform>(); ert.anchorMin = new Vector2(0, 1); ert.anchorMax = new Vector2(1, 1); ert.pivot = new Vector2(0.5f, 1);
            ert.offsetMin = new Vector2(Gutter, y - 48); ert.offsetMax = new Vector2(-Gutter, y);   // offsets only (no sizeDelta.x wipe)
            if (!hasError) expBtn.onClick.AddListener((UnityAction)(() => ExportFlow()));
            else expBtn.onClick.AddListener((UnityAction)(() => SetStatus("Resolve the red items above first.")));
            y -= 60f;

            // result card (only after an export)
            if (_lastExport != null) BuildResultCard(parent, y);
        }

        private static void BuildResultCard(Transform parent, float yTop)
        {
            ExportResult r = _lastExport;
            var card = UIFactory.Panel("Result", parent, r.Ok ? Theme.SuccessSubtle : Theme.DangerSubtle);
            var crt = card.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = new Vector2(Gutter, 8); crt.offsetMax = new Vector2(-Gutter, yTop);
            var outline = card.AddComponent<Outline>(); outline.effectColor = r.Ok ? Theme.Success : Theme.Danger; outline.effectDistance = new Vector2(2, 2);

            var head = UIFactory.Text("rh", r.Ok ? ("Exported " + r.TattoosWritten + " tattoo(s)") : "Export failed", card.transform, 16, TextAnchor.UpperLeft, FontStyle.Bold);
            head.color = r.Ok ? Theme.SuccessText : Theme.DangerText;
            var hrt = head.rectTransform; hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1); hrt.pivot = new Vector2(0.5f, 1); hrt.anchoredPosition = new Vector2(12, -8); hrt.sizeDelta = new Vector2(-24, 22);

            var body = new System.Text.StringBuilder();
            if (r.Ok) body.AppendLine("Folder: " + r.ExportFolder);
            foreach (var w in r.Warnings) body.AppendLine("- " + w);
            string bodyStr = body.ToString();

            // Scrollable body so a long export path + many warnings stay reachable (not clipped).
            var scrollGO = new GameObject("rbody"); scrollGO.transform.SetParent(card.transform, false);
            var srt = scrollGO.AddComponent<RectTransform>(); srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(12, 44); srt.offsetMax = new Vector2(-12, -34);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false; TuneScroll(scroll);
            var vp = new GameObject("vp"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false; scroll.viewport = vprt;
            var bContent = new GameObject("ct"); bContent.transform.SetParent(vp.transform, false);
            var bcrt = bContent.AddComponent<RectTransform>(); bcrt.anchorMin = new Vector2(0, 1); bcrt.anchorMax = new Vector2(1, 1); bcrt.pivot = new Vector2(0.5f, 1); bcrt.offsetMin = Vector2.zero; bcrt.offsetMax = Vector2.zero;
            scroll.content = bcrt;
            int lineEst = bodyStr.Split('\n').Length + 3;   // generous estimate (extra space is harmless in a scroll)
            bcrt.sizeDelta = new Vector2(0, lineEst * 18f);
            var info = UIFactory.Text("ri", bodyStr, bContent.transform, 11, TextAnchor.UpperLeft);
            info.color = new Color(0.85f, 0.88f, 0.9f); info.raycastTarget = false; info.horizontalOverflow = HorizontalWrapMode.Wrap; info.verticalOverflow = VerticalWrapMode.Overflow;
            var irt = info.rectTransform; irt.anchorMin = new Vector2(0, 1); irt.anchorMax = new Vector2(1, 1); irt.pivot = new Vector2(0.5f, 1); irt.sizeDelta = new Vector2(0, lineEst * 18f); irt.anchoredPosition = Vector2.zero;

            if (r.Ok)
            {
                var (ofGO, ofBtn, _) = UIFactory.ButtonWithLabel("OpenExp", "Open export folder", card.transform, Accent, 200, 34);
                var ofrt = ofGO.GetComponent<RectTransform>(); ofrt.anchorMin = new Vector2(0, 0); ofrt.anchorMax = new Vector2(0, 0); ofrt.pivot = new Vector2(0, 0); ofrt.anchoredPosition = new Vector2(12, 8); ofrt.sizeDelta = new Vector2(200, 34);
                ofBtn.onClick.AddListener((UnityAction)(() => OpenFolder(r.ExportFolder)));
                var (pfGO, pfBtn, _) = UIFactory.ButtonWithLabel("OpenPack", "Open pack folder", card.transform, Btn, 190, 34);
                var pfrt = pfGO.GetComponent<RectTransform>(); pfrt.anchorMin = new Vector2(0, 0); pfrt.anchorMax = new Vector2(0, 0); pfrt.pivot = new Vector2(0, 0); pfrt.anchoredPosition = new Vector2(222, 8); pfrt.sizeDelta = new Vector2(190, 34);
                pfBtn.onClick.AddListener((UnityAction)(() => OpenFolder(r.PackFolder)));
            }
        }

        private static void ExportFlow()
        {
            ProjectStore.Save(_project);
            var issues = Exporter.Validate(_project);
            foreach (var it in issues) if (it.Item1 == Exporter.Severity.Error) { _lastExport = null; SetStatus("Fix the errors before exporting."); ShowReviewExport(); return; }
            _lastExport = Exporter.Export(_project);
            SetStatus(_lastExport.Ok ? ("Exported '" + _project.Name + "' (" + _lastExport.TattoosWritten + " tattoo(s)).") : "Export failed.", _lastExport.Ok ? Severity.Success : Severity.Danger);
            ShowReviewExport();
        }

        // --- icon picker: a modal over the (dimmed) review form, sourced from the imported PNGs / import folder ---

        private static GameObject _iconModal;
        private static void CloseIconModal() { if (_iconModal != null) { UnityEngine.Object.Destroy(_iconModal); _iconModal = null; } }

        // Modal (scrim + outside-click catcher + centered card), like Components.ConfirmDialog: the form stays behind so
        // closing needs no rebuild. Self-sufficient: Open-folder + Refresh + an actionable empty state.
        private static void ChooseIconDialog()
        {
            CloseIconModal();
            if (_canvasGO == null) return;
            var scrim = UIFactory.Panel("IconModal", _canvasGO.transform, new Color(0f, 0f, 0f, 0.6f), fullAnchor: true);
            scrim.transform.SetAsLastSibling();
            _iconModal = scrim;
            var catcher = UIFactory.Panel("catcher", scrim.transform, new Color(0f, 0f, 0f, 0.01f), fullAnchor: true);
            var cbtn = catcher.AddComponent<Button>(); cbtn.targetGraphic = catcher.GetComponent<Image>();
            cbtn.onClick.AddListener((UnityAction)(() => CloseIconModal()));

            var card = UIFactory.Panel("card", scrim.transform, Theme.BgElevated);
            var cimg = card.GetComponent<Image>(); if (cimg != null) { cimg.sprite = Theme.RoundedSprite(); cimg.type = Image.Type.Sliced; }
            var crt = card.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0.18f, 0.12f); crt.anchorMax = new Vector2(0.82f, 0.88f); crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            var ol = card.AddComponent<Outline>(); ol.effectColor = Theme.HairlineStrong; ol.effectDistance = new Vector2(1, -1);

            // header: title + close X
            var title = UIFactory.Text("Title", "Choose icon", card.transform, Theme.H3, TextAnchor.UpperLeft, FontStyle.Bold);
            title.color = Theme.TextPrimary; title.raycastTarget = false;
            var ttrt = title.rectTransform; ttrt.anchorMin = new Vector2(0, 1); ttrt.anchorMax = new Vector2(1, 1); ttrt.pivot = new Vector2(0.5f, 1);
            ttrt.offsetMin = new Vector2(Gutter, -46); ttrt.offsetMax = new Vector2(-Gutter, -14);
            var xBtn = IconButton(card.transform, ToolIcons.Get("close"), Btn);
            var xrt = xBtn.GetComponent<RectTransform>(); xrt.anchorMin = new Vector2(1, 1); xrt.anchorMax = new Vector2(1, 1); xrt.pivot = new Vector2(1, 1); xrt.anchoredPosition = new Vector2(-12, -10); xrt.sizeDelta = new Vector2(30, 30);
            xBtn.onClick.AddListener((UnityAction)(() => CloseIconModal()));

            // footer: open import folder + refresh (also serve the empty state)
            var (ofGO, ofBtn, _) = UIFactory.ButtonWithLabel("OpenImp", "Open import folder", card.transform, Btn, 210, 36);
            var ofrt = ofGO.GetComponent<RectTransform>(); ofrt.anchorMin = new Vector2(0, 0); ofrt.anchorMax = new Vector2(0, 0); ofrt.pivot = new Vector2(0, 0); ofrt.anchoredPosition = new Vector2(Gutter, 14); ofrt.sizeDelta = new Vector2(210, 36);
            SetLeadingIcon(ofGO, "folder", false);
            ofBtn.onClick.AddListener((UnityAction)(() => OpenFolder(Paths.Import)));
            var (rfGO, rfBtn, _) = UIFactory.ButtonWithLabel("Refresh", "Refresh", card.transform, Btn, 130, 36);
            var rfrt = rfGO.GetComponent<RectTransform>(); rfrt.anchorMin = new Vector2(0, 0); rfrt.anchorMax = new Vector2(0, 0); rfrt.pivot = new Vector2(0, 0); rfrt.anchoredPosition = new Vector2(Gutter + 222f, 14); rfrt.sizeDelta = new Vector2(130, 36);
            SetLeadingIcon(rfGO, "refresh", false);
            rfBtn.onClick.AddListener((UnityAction)(() => ChooseIconDialog()));   // reopen = re-scan the folder

            // scroll grid between header and footer
            var scrollGO = new GameObject("IconScroll"); scrollGO.transform.SetParent(card.transform, false);
            var srt = scrollGO.AddComponent<RectTransform>(); srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(Gutter, 60); srt.offsetMax = new Vector2(-Gutter, -52);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false; TuneScroll(scroll);
            var vp = new GameObject("Viewport"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false; scroll.viewport = vprt;
            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var ccrt = content.AddComponent<RectTransform>(); ccrt.anchorMin = new Vector2(0, 1); ccrt.anchorMax = new Vector2(1, 1); ccrt.pivot = new Vector2(0.5f, 1); ccrt.offsetMin = Vector2.zero; ccrt.offsetMax = Vector2.zero;
            scroll.content = ccrt;

            string[] files = ImageLoader.ListImportImages();
            if (files.Length == 0)
            {
                var none = UIFactory.Text("None", "No images yet. Click 'Open import folder', drop your PNG there, then 'Refresh'.", content.transform, 14, TextAnchor.UpperCenter);
                none.color = Hint; none.raycastTarget = false;
                var nrt = none.rectTransform; nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1); nrt.pivot = new Vector2(0.5f, 1); nrt.anchoredPosition = new Vector2(0, -16); nrt.sizeDelta = new Vector2(-8, 48);
            }
            else
            {
                const int cols = 4; const float gap = 10f, pad = 8f, cellH = 150f;
                int rows = (files.Length + cols - 1) / cols;
                ccrt.sizeDelta = new Vector2(0, pad * 2f + rows * (cellH + gap));
                string curName = string.IsNullOrEmpty(_project.IconSource) ? null : Path.GetFileName(_project.IconSource);
                for (int i = 0; i < files.Length; i++)
                {
                    string p = files[i]; int col = i % cols, row = i / cols;
                    bool isCur = curName != null && string.Equals(Path.GetFileName(p), curName, StringComparison.OrdinalIgnoreCase);
                    var cell = new GameObject("tile"); cell.transform.SetParent(content.transform, false);
                    var cellrt = cell.AddComponent<RectTransform>(); cellrt.anchorMin = new Vector2(col / (float)cols, 1f); cellrt.anchorMax = new Vector2((col + 1) / (float)cols, 1f); cellrt.pivot = new Vector2(0.5f, 1f);
                    cellrt.sizeDelta = new Vector2(-gap, cellH); cellrt.anchoredPosition = new Vector2(0, -(pad + row * (cellH + gap)));
                    var bg = cell.AddComponent<Image>(); bg.color = isCur ? BtnSel : Theme.BgElevated;
                    var btn = cell.AddComponent<Button>(); btn.targetGraphic = bg;
                    btn.onClick.AddListener((UnityAction)(() => PickIcon(p)));
                    var thumb = new GameObject("thumb"); thumb.transform.SetParent(cell.transform, false);
                    var trt = thumb.AddComponent<RectTransform>(); trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1); trt.offsetMin = new Vector2(4, 22); trt.offsetMax = new Vector2(-4, -4);
                    var timg = thumb.AddComponent<Image>(); timg.preserveAspect = true; timg.raycastTarget = false; var spr = LoadSprite(p); if (spr != null) timg.sprite = spr; else timg.color = new Color(1, 1, 1, 0.1f);
                    string nm = Path.GetFileNameWithoutExtension(p); if (nm.Length > 16) nm = nm.Substring(0, 14) + "..";
                    var lbl = UIFactory.Text("n", nm, cell.transform, 10, TextAnchor.LowerCenter); lbl.raycastTarget = false; lbl.color = new Color(0.82f, 0.84f, 0.88f);
                    var lrt = lbl.rectTransform; lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0); lrt.pivot = new Vector2(0.5f, 0); lrt.anchoredPosition = new Vector2(0, 2); lrt.sizeDelta = new Vector2(-2, 16);
                    if (isCur)   // accent frame marks the current selection
                    {
                        var sel = new GameObject("sel"); sel.transform.SetParent(cell.transform, false);
                        var frt = sel.AddComponent<RectTransform>(); frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
                        var fim = sel.AddComponent<Image>(); fim.sprite = Theme.FrameSprite(); fim.type = Image.Type.Sliced; fim.color = Theme.AccentBorder; fim.raycastTarget = false;
                    }
                }
            }

            Interactions.PolishButtons(scrim.transform);   // rounded corners + hover/press on the tiles and footer
        }

        private static void PickIcon(string absPath)
        {
            string rel = ProjectStore.ImportSource(_project, absPath);
            if (rel != null) { _project.IconSource = rel; ProjectStore.Save(_project); SetStatus("Icon set from " + Path.GetFileName(absPath) + "."); }
            else SetStatus("Could not use that image as the icon.");
            _lastExport = null;   // a metadata edit invalidates the previous export's result card
            CloseIconModal();
            ShowReviewExport();
        }

        private static void MarkDirty() { _previewDirty = true; _lastEdit = Time.time; }

        // --- per-frame: drag + debounced live preview (called from Core.OnUpdate while open) ---

        public static void Tick()
        {
            if (_canvasGO == null) return;
            Toast.Tick();                       // runs on every screen (review / icon picker too), not just the editor
            TickScroll();                       // smooth list scrolling on every screen
            if (_uvArea == null) return;

            // Tool hotkeys (Unity-style: W move, E rotate, R scale) when a decal is selected.
            if (_selected != null)
            {
                if (Input.GetKeyDown(KeyCode.W)) SetTool(Tool.Move);
                else if (Input.GetKeyDown(KeyCode.E)) SetTool(Tool.Rotate);
                else if (Input.GetKeyDown(KeyCode.R)) SetTool(Tool.Scale);
            }

            // Delete key: remove the selected decal, or the selected tattoo if no decal is selected
            // (ignored while typing a name in an InputField).
            if (Input.GetKeyDown(KeyCode.Delete) && !IsTypingInField())
            {
                if (_selected != null) DeleteSelected();
                else if (_selectedTattoo != null) DeleteSelectedTattoo();
            }

            // Undo / redo (Ctrl+Z / Ctrl+Y), unless typing a name.
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && !IsTypingInField())
            {
                if (Input.GetKeyDown(KeyCode.Z)) Undo();
                else if (Input.GetKeyDown(KeyCode.Y)) Redo();
            }
            // Rotate character with A/D regardless of selection.
            if (Input.GetKey(KeyCode.A)) Preview.RotateAvatar(-90f * Time.deltaTime);
            if (Input.GetKey(KeyCode.D)) Preview.RotateAvatar(90f * Time.deltaTime);

            // Context-sensitive wheel: over the canvas = scale the selected decal; over the character = zoom the
            // camera; over a scrollable list / elsewhere = leave it to Unity's ScrollRect.
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                Vector2 wp = Input.mousePosition;
                if (OverCanvas(wp))
                {
                    if (_selected != null)
                    {
                        if (Time.time - _lastWheelSnap > 0.5f) { Snapshot(); _lastWheelSnap = Time.time; }
                        _selected.Scale = Mathf.Clamp(_selected.Scale * (1f + wheel * 0.06f), 0.004f, 2f);
                        UpdateSpriteTransform(_selected); UpdateControlValues(); MarkDirty();
                    }
                }
                else if (InCharacterRegion(wp))
                {
                    Preview.ZoomCamera(wheel); UpdateZoomLabel();
                }
                // else: over a panel/list -> ScrollRect handles it; do nothing.
            }

            if (Input.GetMouseButtonDown(0))
            {
                Vector2 mp = Input.mousePosition;
                Decal hit = HitTest(mp);
                if (hit != null)
                {
                    _selected = hit; _dragging = true; _dragMode = 1; _dragSnapped = false;
                    _lastSliderSnap = _lastWheelSnap = float.NegativeInfinity;   // first edit on a newly selected decal always snapshots
                    _dragStartMouse = mp; _startRot = hit.RotationDeg; _startScale = hit.Scale;
                    var rt = _decalSprites[hit].GetComponent<RectTransform>();
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(_uvArea, mp, null, out Vector2 local);
                    _dragOffset = rt.anchoredPosition - local;
                    UpdateSelectionRing(); _rightTab = RightTab.Inspector; RefreshRightPanel();
                }
                else if (OverCanvas(mp))
                {
                    // Clicked an empty spot on the canvas -> deselect, fall back to the import library.
                    if (_selected != null) { _selected = null; UpdateSelectionRing(); _rightTab = RightTab.Import; RefreshRightPanel(); }
                }
                else if (InCharacterRegion(mp))
                {
                    if (_selected != null) { _selected = null; UpdateSelectionRing(); _rightTab = RightTab.Import; RefreshRightPanel(); }
                    _dragging = true; _dragMode = 2; _lastMouse = mp;
                }
            }
            else if (_dragging && Input.GetMouseButton(0))
            {
                if (_dragMode == 1 && _selected != null && _decalSprites.TryGetValue(_selected, out var go) && go != null)
                {
                    if (!_dragSnapped) { Snapshot(); _dragSnapped = true; }
                    Vector2 mp = Input.mousePosition;
                    if (_tool == Tool.Move)
                    {
                        RectTransformUtility.ScreenPointToLocalPointInRectangle(_uvArea, mp, null, out Vector2 local);
                        Vector2 pos = local + _dragOffset;
                        pos.x = Mathf.Clamp(pos.x, -_uvSize * 0.5f, _uvSize * 0.5f);
                        pos.y = Mathf.Clamp(pos.y, -_uvSize * 0.5f, _uvSize * 0.5f);
                        _selected.U = pos.x / _uvSize + 0.5f; _selected.V = pos.y / _uvSize + 0.5f;
                    }
                    else if (_tool == Tool.Rotate)
                    {
                        _selected.RotationDeg = _startRot + (mp.x - _dragStartMouse.x) * 0.4f;
                    }
                    else // Scale
                    {
                        _selected.Scale = Mathf.Clamp(_startScale * (1f + (mp.x - _dragStartMouse.x) * 0.004f), 0.004f, 2f);
                    }
                    UpdateSpriteTransform(_selected); UpdateControlValues(); MarkDirty();
                }
                else if (_dragMode == 2)
                {
                    Vector2 mp = Input.mousePosition;
                    Preview.RotateAvatar((mp.x - _lastMouse.x) * 0.4f);
                    _lastMouse = mp;
                }
            }
            else if (_dragging && Input.GetMouseButtonUp(0))
            {
                _dragging = false; _dragMode = 0;
                if (_project != null) ProjectStore.Save(_project);
                MarkDirty();
            }

            // Debounced live preview: ~0.18s after the last edit (and not mid-drag) re-bake + apply.
            if (_previewDirty && !_dragging && Time.time - _lastEdit > 0.18f)
            {
                _previewDirty = false;
                PreviewCurrent();
            }
        }

        private static void SetTool(Tool t)
        {
            _tool = t;
            UpdateToolSeg();
            try
            {
                // Move uses the default OS cursor; Rotate/Scale use real (free) icons at ~28px (small cursor).
                Texture2D cur = t == Tool.Move ? null : ToolIcons.GetSized(t == Tool.Rotate ? "rotate" : "scale", 28);
                if (cur != null) Cursor.SetCursor(cur, new Vector2(cur.width * 0.5f, cur.height * 0.5f), CursorMode.ForceSoftware);
                else Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
            catch { }
        }

        // The open centre strip where the 3D character stands (between the left rail and the UV canvas).
        private static bool InCharacterRegion(Vector2 screenPos)
        {
            float x = screenPos.x;
            return x > Screen.width * 0.20f && x < Screen.width * 0.46f;
        }

        // Smooth wheel scrolling. Unity's ScrollRect wheel handling is an instant per-notch jump: small steps feel
        // slow, big steps feel jumpy. We disable the native wheel and drive an eased glide ourselves from the
        // per-frame TickScroll (runs on every screen), so lists scroll fast AND fluidly.
        private sealed class SmoothScroller { public ScrollRect sr; public float target; public bool animating; }
        private static readonly List<SmoothScroller> _scrollers = new List<SmoothScroller>();
        private const float ScrollStepPx = 72f;   // target advance per wheel notch
        private const float ScrollEase = 16f;      // glide rate (higher = snappier)

        private static void TuneScroll(ScrollRect s)
        {
            if (s == null) return;
            s.scrollSensitivity = 0f;   // native wheel off; TickScroll drives a smooth glide instead
            s.movementType = ScrollRect.MovementType.Clamped;
            _scrollers.Add(new SmoothScroller { sr = s });
        }

        private static void ClearScrollers() => _scrollers.Clear();

        private static void TickScroll()
        {
            if (_scrollers.Count == 0) return;
            float wheel = Input.mouseScrollDelta.y;
            Vector2 mouse = Input.mousePosition;
            float dt = Time.unscaledDeltaTime; if (dt <= 0f) dt = 0.016f;
            bool wheeled = Mathf.Abs(wheel) > 0.01f;
            for (int i = _scrollers.Count - 1; i >= 0; i--)
            {
                var e = _scrollers[i];
                if (e.sr == null || e.sr.content == null || e.sr.viewport == null) { _scrollers.RemoveAt(i); continue; }
                float maxY = Mathf.Max(0f, e.sr.content.rect.height - e.sr.viewport.rect.height);
                if (wheeled && RectTransformUtility.RectangleContainsScreenPoint(e.sr.viewport, mouse, null))
                {
                    if (!e.animating) e.target = e.sr.content.anchoredPosition.y;   // start from where it is (drag-safe)
                    e.target = Mathf.Clamp(e.target - wheel * ScrollStepPx, 0f, maxY);
                    e.animating = true;
                }
                if (e.animating)
                {
                    var pos = e.sr.content.anchoredPosition;
                    float ny = Mathf.Lerp(pos.y, Mathf.Clamp(e.target, 0f, maxY), 1f - Mathf.Exp(-ScrollEase * dt));
                    if (Mathf.Abs(ny - e.target) < 0.5f) { ny = e.target; e.animating = false; }
                    pos.y = ny; e.sr.content.anchoredPosition = pos;
                }
            }
        }

        private static GameObject SubRegion(GameObject parent, string name, float yMin, float yMax)
        {
            var p = UIFactory.Panel(name, parent.transform, Clear);
            var rt = p.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, yMin); rt.anchorMax = new Vector2(1, yMax);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = p.GetComponent<Image>(); if (img != null) img.raycastTarget = false;
            return p;
        }

        // A full-width sub-region sized in PIXELS: top = a fixed-height band anchored to the top; otherwise it fills
        // the rest below that band. Used so the body-part tabs take exactly their height and the list starts right under.
        private static GameObject SubRegionPx(GameObject parent, string name, float bandH, bool top)
        {
            var p = UIFactory.Panel(name, parent.transform, Clear);
            var rt = p.GetComponent<RectTransform>();
            if (top) { rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.offsetMin = new Vector2(0, -bandH); rt.offsetMax = new Vector2(0, 0); }
            else { rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1); rt.offsetMin = new Vector2(0, 0); rt.offsetMax = new Vector2(0, -bandH); }
            var img = p.GetComponent<Image>(); if (img != null) img.raycastTarget = false;
            return p;
        }

        private static bool OverCanvas(Vector2 screenPos) =>
            _uvArea != null && RectTransformUtility.RectangleContainsScreenPoint(_uvArea, screenPos, null);

        private static Decal HitTest(Vector2 screenPos)
        {
            Decal found = null;
            foreach (var kv in _decalSprites)
            {
                var rt = kv.Value != null ? kv.Value.GetComponent<RectTransform>() : null;
                if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null)) found = kv.Key;
            }
            return found;
        }

        // --- helpers ---

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
                SetStatus("Opened: " + path);
            }
            catch
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); SetStatus("Opened: " + path); }
                catch (Exception e) { SetStatus("Could not open folder (" + e.Message + "): " + path); }
            }
        }

        // All tattoos that belong to a body part (a body part can hold several named tattoos = several shop entries).
        private static List<TattooEntry> TattoosFor(Placement p)
        {
            string tok = Placements.Token(p);
            var list = new List<TattooEntry>();
            foreach (var t in _project.Tattoos)
                if (string.Equals(t.Placement, tok, StringComparison.OrdinalIgnoreCase)) list.Add(t);
            return list;
        }

        private const float DefaultTattooPrice = 250f;   // sensible default shop price for new tattoos (Inkorporated uses 250 in its example)

        private static TattooEntry CreateTattoo(Placement p, string name)
        {
            string display = string.IsNullOrWhiteSpace(name) ? (Label(p) + " " + (TattoosFor(p).Count + 1)) : name;
            var e = new TattooEntry { Id = UniqueId(display), Name = display, Placement = Placements.Token(p), Visible = true, Price = DefaultTattooPrice };
            _project.Tattoos.Add(e);
            return e;
        }

        // A pack-unique id (used for the exported PNG filename + shop id).
        private static string UniqueId(string baseName)
        {
            string root = Paths.Sanitize(string.IsNullOrWhiteSpace(baseName) ? "tattoo" : baseName).Replace(" ", "_").ToLowerInvariant();
            if (string.IsNullOrEmpty(root)) root = "tattoo";
            string id = root; int n = 2;
            while (HasId(id)) { id = root + "_" + n; n++; }
            return id;
        }

        private static bool HasId(string id)
        {
            foreach (var t in _project.Tattoos) if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Decals shown on the canvas = the selected tattoo's decals (or none).
        private static List<Decal> CurrentDecals() => _selectedTattoo != null ? _selectedTattoo.Decals : new List<Decal>();

        private static string Label(Placement p) => p switch
        {
            Placement.Chest => "Chest", Placement.LeftArm => "Left arm", Placement.RightArm => "Right arm", Placement.Face => "Face", _ => p.ToString()
        };

        private static Sprite LoadTemplate(Placement p)
        {
            try
            {
                string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "Inkorporated", "Templates", Placements.Token(p));
                if (!Directory.Exists(dir)) return null;
                foreach (string f in Directory.GetFiles(dir, "*.png"))
                    if (!f.EndsWith("_normal.png", StringComparison.OrdinalIgnoreCase)) return LoadSprite(f);
                return null;
            }
            catch { return null; }
        }

        private static Sprite LoadSprite(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                Texture2D t = ImageLoader.LoadTexture(path);
                if (t == null) return null;
                return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch { return null; }
        }

        // Section header: left-aligned at the gutter (consistent across every panel). Offsets only (no sizeDelta).
        private static Text TopLabel(Transform parent, string name, string text, float y, float h, int size, FontStyle style)
        {
            var t = UIFactory.Text(name, text, parent, size, TextAnchor.MiddleLeft, style);
            var rt = t.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(Gutter, y - h); rt.offsetMax = new Vector2(-Gutter, y);
            return t;
        }

        private static void SetStatus(string s) => SetStatus(s, Severity.Info);

        private static void SetStatus(string s, Severity sev)
        {
            _status = s;
            if (_statusText != null) _statusText.text = s;
            if (!string.IsNullOrEmpty(s)) Toast.Show(s, sev);
            Core.Log?.Msg("[editor] " + s);
        }
        private static void ClearScreen() { ClearScrollers(); if (_screen != null) { UnityEngine.Object.Destroy(_screen); _screen = null; } }

        private static Button MakeBarButton(Transform parent, string label, Color color, Action onClick, float width = 150f, string icon = null, bool iconOnly = false)
        {
            var (go, btn, _) = UIFactory.ButtonWithLabel("bar_" + label, label, parent, color, width, 40);
            AddLE(go, 0, width + 8f);
            btn.onClick.AddListener((UnityAction)(() => onClick()));
            if (icon != null) SetLeadingIcon(go, icon, iconOnly);
            return btn;
        }

        // Put an icon on a ButtonWithLabel: icon-only (centered, label hidden) or a leading icon + left-aligned label.
        private static void SetLeadingIcon(GameObject btnGO, string iconName, bool iconOnly = false)
        {
            Texture2D tex = ToolIcons.Get(iconName);
            if (tex == null) return;
            var labelT = btnGO.transform.Find("Label");
            if (iconOnly)
            {
                if (labelT != null) labelT.gameObject.SetActive(false);
                Components.AddCenterIcon(btnGO.transform, tex, 20f);
                return;
            }
            var ic = new GameObject("icon"); ic.transform.SetParent(btnGO.transform, false);
            var irt = ic.AddComponent<RectTransform>(); irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(0, 0.5f); irt.pivot = new Vector2(0, 0.5f);
            irt.sizeDelta = new Vector2(18, 18); irt.anchoredPosition = new Vector2(12, 0);
            var iimg = ic.AddComponent<Image>(); iimg.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            iimg.preserveAspect = true; iimg.raycastTarget = false;
            var lblText = labelT != null ? labelT.GetComponent<Text>() : null;
            if (lblText != null) { lblText.alignment = TextAnchor.MiddleLeft; var lrt = lblText.rectTransform; lrt.offsetMin = new Vector2(38, lrt.offsetMin.y); }
        }

        private static void AddLE(GameObject go, float h, float w = 0)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            if (h > 0) { le.minHeight = h; le.preferredHeight = h; }
            if (w > 0) { le.minWidth = w; le.preferredWidth = w; }
            le.flexibleWidth = w > 0 ? 0 : 1;
        }

        private static RectTransform Place(GameObject go, float xMin, float yMin, float xMax, float yMax)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(xMin, yMin); rt.anchorMax = new Vector2(xMax, yMax);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static void PlaceCenter(GameObject go, float x, float yFromTop)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f); rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
        }

        private static void PlaceBottom(GameObject go, float x, float yFromBottom)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f); rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(x, yFromBottom);
        }
    }
}
