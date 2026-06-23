using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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
        private static string _armedDeleteId;    // tattoo id whose delete button is armed (two-step confirm)
        private static GameObject _selRing;

        // Undo/redo: coarse whole-project JSON snapshots.
        private static readonly Stack<string> _undo = new Stack<string>();
        private static readonly Stack<string> _redo = new Stack<string>();

        // Review & Export screen: the most recent export result (drives the green/red result card), null = none yet.
        private static ExportResult _lastExport;

        // transform tool (game-editor style: W=move, E=rotate, R=scale)
        private enum Tool { Move, Rotate, Scale }
        private static Tool _tool = Tool.Move;
        private static Text _toolText;

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

        // control value labels
        private static Text _sizeVal, _rotVal, _opVal, _flipVal;

        private static readonly Color Clear = new Color(0, 0, 0, 0);
        private static readonly Color Panel = new Color(0.11f, 0.12f, 0.15f, 0.96f);
        private static readonly Color CanvasBg = new Color(0.04f, 0.04f, 0.06f, 0.92f);
        private static readonly Color Accent = new Color(0.20f, 0.55f, 0.45f, 1f);
        private static readonly Color Btn = new Color(0.22f, 0.23f, 0.28f, 1f);
        private static readonly Color BtnSel = new Color(0.25f, 0.50f, 0.42f, 1f);

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
                if (_project != null) { ShowEditor(); Preview.SetStripClothing(true); PreviewAll(); return; }
            }
            ShowProjectSelect();
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
            _decalSprites.Clear();
            _selected = null; _selRing = null; _dragging = false; _previewDirty = false;
            if (_canvasGO != null) { UnityEngine.Object.Destroy(_canvasGO); _canvasGO = null; }
            _screen = null; _project = null; _uvArea = null; _statusText = null;
            _sizeVal = _rotVal = _opVal = _flipVal = null;
        }

        private static void BuildCanvas()
        {
            _canvasGO = new GameObject("Inkubator_EditorCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
            var canvas = _canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            _canvasGO.AddComponent<GraphicRaycaster>();
        }

        // --- screen: project select (dim background so the title reads) ---

        private static void ShowProjectSelect()
        {
            ClearScreen();
            _screen = UIFactory.Panel("ProjectSelect", _canvasGO.transform, new Color(0.06f, 0.07f, 0.09f, 0.97f), fullAnchor: true);

            UIFactory.Text("Title", "Inkubator - Tattoo Modpacks", _screen.transform, 32, TextAnchor.UpperCenter, FontStyle.Bold)
                .rectTransform.anchoredPosition = new Vector2(0, -40);
            var sub = UIFactory.Text("Sub", "Each pack is a complete, exportable tattoo mod (many tattoos, each from one or more images).", _screen.transform, 15, TextAnchor.UpperCenter);
            sub.color = new Color(0.7f, 0.72f, 0.78f);
            sub.rectTransform.anchoredPosition = new Vector2(0, -82); sub.rectTransform.sizeDelta = new Vector2(700, 24);

            var (newGO, newBtn, _) = UIFactory.ButtonWithLabel("New", "+ New pack", _screen.transform, Accent, 360, 56);
            PlaceCenter(newGO, 0, 120);
            newBtn.onClick.AddListener((UnityAction)(() => CreateProjectFlow()));

            var hdr = UIFactory.Text("Existing", "Open a pack", _screen.transform, 18, TextAnchor.UpperCenter, FontStyle.Bold);
            hdr.rectTransform.anchoredPosition = new Vector2(0, 40);

            var listContent = UIFactory.ScrollableVerticalList("Projects", _screen.transform, out var scroll);
            var lrt = scroll.gameObject.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0.5f, 0f); lrt.anchorMax = new Vector2(0.5f, 0f); lrt.pivot = new Vector2(0.5f, 0f);
            lrt.sizeDelta = new Vector2(520, 340); lrt.anchoredPosition = new Vector2(0, 110);

            List<string> projects = ProjectStore.List();
            if (projects.Count == 0)
            {
                var none = UIFactory.Text("None", "(no packs yet - create one above)", listContent, 15, TextAnchor.MiddleCenter);
                none.color = new Color(0.6f, 0.6f, 0.65f); AddLE(none.gameObject, 40);
            }
            foreach (string folder in projects)
            {
                string f = folder;
                int tcount = ProjectStore.TattooCount(f);
                var (rowGO, rowBtn, _) = UIFactory.ButtonWithLabel("p_" + f, ProjectStore.DisplayName(f) + "   (" + tcount + " tattoo" + (tcount == 1 ? "" : "s") + ")", listContent, Btn, 500, 46);
                AddLE(rowGO, 46);
                rowBtn.onClick.AddListener((UnityAction)(() => OpenProject(f)));
            }

            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("Back", "Back to hub", _screen.transform, Btn, 200, 48);
            PlaceBottom(backGO, 0, 30);
            backBtn.onClick.AddListener((UnityAction)(() => { Close(); _ctx?.ReturnToHub(); }));
        }

        private static void CreateProjectFlow()
        {
            string baseName = "New Tattoo Pack"; string name = baseName; int i = 2;
            while (ProjectStore.Exists(name)) { name = baseName + " " + i; i++; }
            _project = ProjectStore.Create(name, "");
            _selectedTattoo = null; _selected = null; _armedDeleteId = null; _undo.Clear(); _redo.Clear();
            ShowEditor();
        }

        private static void OpenProject(string folder)
        {
            _project = ProjectStore.Load(folder);
            if (_project == null) { SetStatus("Failed to open '" + folder + "'"); return; }
            _selectedTattoo = null; _selected = null; _armedDeleteId = null; _undo.Clear(); _redo.Clear();
            ShowEditor();
        }

        // --- screen: editor (transparent background -> character visible) ---

        private static void ShowEditor()
        {
          try
          {
            ClearScreen();
            _screen = UIFactory.Panel("Editor", _canvasGO.transform, Clear, fullAnchor: true);
            // The root must not block clicks where it is transparent; only the panels do.
            var rootImg = _screen.GetComponent<Image>(); if (rootImg != null) rootImg.raycastTarget = false;

            _uvSize = Mathf.Min(Screen.width * 0.34f, Screen.height * 0.80f);

            // Top bar
            var top = UIFactory.Panel("Top", _screen.transform, Panel);
            Place(top, 0f, 0.92f, 1f, 1f);
            var tcl = top.AddComponent<HorizontalLayoutGroup>();
            tcl.padding = new RectOffset(24, 24, 8, 8); tcl.spacing = 12; tcl.childAlignment = TextAnchor.MiddleLeft;
            tcl.childForceExpandWidth = false; tcl.childForceExpandHeight = true;
            // Pack title: "Inkubator" + an editable name field. A project IS a tattoo modpack (N tattoos, each of
            // M images); the name here is the pack title used for the exported mod.
            var inkT = UIFactory.Text("Ink", "Inkubator", top.transform, 18, TextAnchor.MiddleLeft, FontStyle.Bold);
            AddLE(inkT.gameObject, 0, 96);
            var packInput = MakeInput(top.transform, _project.Name, (UnityAction<string>)(s =>
            {
                _project.Name = string.IsNullOrWhiteSpace(s) ? _project.Name : s.Trim();
                ProjectStore.Save(_project);
            }));
            AddLE(packInput.gameObject, 36, 210);

            MakeBarButton(top.transform, "Switch pack", Accent, () => { ProjectStore.Save(_project); ShowProjectSelect(); }, 120);
            MakeBarButton(top.transform, "Save", Btn, () => { ProjectStore.Save(_project); SetStatus("Saved."); }, 80);
            MakeBarButton(top.transform, "Undo", Btn, Undo, 80);
            MakeBarButton(top.transform, "Redo", Btn, Redo, 80);
            var (clGO, clBtn, clTxt) = UIFactory.ButtonWithLabel("bar_clothes", Preview.IsClothingStripped ? "Show clothes" : "Hide clothes", top.transform, Btn, 120, 40);
            AddLE(clGO, 0, 128);
            clBtn.onClick.AddListener((UnityAction)(() =>
            {
                bool s = Preview.SetStripClothing(!Preview.IsClothingStripped);
                clTxt.text = s ? "Show clothes" : "Hide clothes";
                PreviewAll();
                SetStatus(s ? "Clothes hidden - body tattoos now visible." : "Clothes shown.");
            }));
            var (uwGO, uwBtn, uwTxt) = UIFactory.ButtonWithLabel("bar_underwear", Preview.IsUnderwearStripped ? "Show underwear" : "Hide underwear", top.transform, Btn, 130, 40);
            AddLE(uwGO, 0, 138);
            uwBtn.onClick.AddListener((UnityAction)(() =>
            {
                bool s = Preview.SetStripUnderwear(!Preview.IsUnderwearStripped);
                uwTxt.text = s ? "Show underwear" : "Hide underwear";
                PreviewAll();
                SetStatus(s ? "Underwear hidden." : "Underwear shown.");
            }));
            // The export folder is reachable from the review screen's result card, so it is not duplicated here (keeps the bar narrow enough for ~1366px displays).
            MakeBarButton(top.transform, "Export", new Color(0.45f, 0.35f, 0.15f), () => { ProjectStore.Save(_project); _lastExport = null; ShowReviewExport(); }, 110);
            MakeBarButton(top.transform, "Back to hub", Btn, () => { ProjectStore.Save(_project); Close(); _ctx?.ReturnToHub(); }, 110);

            // Left: body-part tabs (top) + tattoo list for this body part (middle) + selected-decal controls (bottom)
            var left = UIFactory.Panel("Left", _screen.transform, Panel);
            Place(left, 0f, 0f, 0.17f, 0.92f);
            BuildTabs(SubRegion(left, "TabsR", 0.745f, 1.0f).transform);
            BuildTattooList(SubRegion(left, "ListR", 0.405f, 0.74f).transform);
            BuildDecalControls(SubRegion(left, "CtrlR", 0.0f, 0.40f).transform);

            // Center gap (0.16 - 0.46) left transparent -> the 3D character shows here, with live tattoo.

            // UV canvas on the right-center
            BuildUvCanvas();

            // Right: import list + open-folder
            var right = UIFactory.Panel("Right", _screen.transform, Panel);
            Place(right, 0.80f, 0f, 1f, 0.92f);
            BuildImportList(right.transform);

            // Rotate-character buttons (also: drag on the character, or hold A / D).
            var rotRow = UIFactory.Panel("RotRow", _screen.transform, Clear);
            Place(rotRow, 0.16f, 0.045f, 0.45f, 0.105f);
            var (rlGO, rlBtn, _) = UIFactory.ButtonWithLabel("RotL", "< turn", rotRow.transform, Btn, 90, 38);
            var rlrt = rlGO.GetComponent<RectTransform>(); rlrt.anchorMin = new Vector2(0.5f, 0.5f); rlrt.anchorMax = new Vector2(0.5f, 0.5f); rlrt.pivot = new Vector2(1, 0.5f); rlrt.anchoredPosition = new Vector2(-8, 0); rlrt.sizeDelta = new Vector2(90, 38);
            rlBtn.onClick.AddListener((UnityAction)(() => Preview.RotateAvatar(-25f)));
            var (rrGO, rrBtn, _) = UIFactory.ButtonWithLabel("RotR", "turn >", rotRow.transform, Btn, 90, 38);
            var rrrt = rrGO.GetComponent<RectTransform>(); rrrt.anchorMin = new Vector2(0.5f, 0.5f); rrrt.anchorMax = new Vector2(0.5f, 0.5f); rrrt.pivot = new Vector2(0, 0.5f); rrrt.anchoredPosition = new Vector2(8, 0); rrrt.sizeDelta = new Vector2(90, 38);
            rrBtn.onClick.AddListener((UnityAction)(() => Preview.RotateAvatar(25f)));

            _toolText = UIFactory.Text("Tool", "", _screen.transform, 13, TextAnchor.LowerCenter, FontStyle.Bold);
            _toolText.color = new Color(0.55f, 0.85f, 0.75f);
            Place(_toolText.gameObject, 0.16f, 0.105f, 0.80f, 0.145f);
            SetTool(_tool);

            _statusText = UIFactory.Text("Status", _status, _screen.transform, 14, TextAnchor.LowerCenter);
            _statusText.color = new Color(0.75f, 0.8f, 0.85f);
            Place(_statusText.gameObject, 0.16f, 0f, 0.80f, 0.045f);
            var stBg = _statusText.gameObject.AddComponent<Outline>(); stBg.effectColor = new Color(0, 0, 0, 0.8f);

            RebuildDecalSprites();
            PreviewAll();
            if (!_layersLogged) { _layersLogged = true; Preview.LogLayers(); }
            SetStatus("Import a PNG (right), drag it on the canvas, tweak it (left). Drag the character or A/D to turn it.");
          }
          catch (Exception e) { Core.Log?.Error("[editor] ShowEditor failed: " + e); }
        }

        private static void BuildTabs(Transform parent)
        {
            var hdr = TopLabel(parent, "TabsH", "Body part", -12, 28, 16, FontStyle.Bold);

            float y = -42;
            foreach (Placement p in Placements.All)
            {
                Placement pp = p; bool active = p == _tab;
                int count = TattoosFor(p).Count;
                var (go, btn, _) = UIFactory.ButtonWithLabel("tab_" + p, Label(p) + (count > 0 ? "  (" + count + ")" : ""), parent, active ? BtnSel : Btn, 0, 40);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
                rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, 40); rt.anchoredPosition = new Vector2(0, y);
                btn.onClick.AddListener((UnityAction)(() => { _tab = pp; _selected = null; _selectedTattoo = null; ShowEditor(); }));
                y -= 46;
            }
        }

        private static void BuildTattooList(Transform parent)
        {
            TopLabel(parent, "TLH", "Tattoos - " + Label(_tab), -6, 20, 13, FontStyle.Bold);

            var (addGO, addBtn, _) = UIFactory.ButtonWithLabel("AddTat", "+ Add tattoo", parent, Accent, 0, 32);
            var art = addGO.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0, 1); art.anchorMax = new Vector2(1, 1); art.pivot = new Vector2(0.5f, 1);
            art.offsetMin = new Vector2(6, 0); art.offsetMax = new Vector2(-6, 0); art.sizeDelta = new Vector2(0, 32); art.anchoredPosition = new Vector2(0, -28);
            addBtn.onClick.AddListener((UnityAction)(() =>
            {
                Snapshot();
                _armedDeleteId = null; _selectedTattoo = CreateTattoo(_tab, null); _selected = null;
                ProjectStore.Save(_project); ShowEditor();
            }));

            // Scroll view + vertical stack of rows, built manually with explicit anchored positions.
            // Nested LayoutGroups (ScrollableVerticalList + per-row HorizontalLayoutGroup) collapsed the row
            // children to the world origin here; the import grid uses this same manual pattern and renders fine.
            var scrollGO = new GameObject("TattooScroll"); scrollGO.transform.SetParent(parent, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1);
            srt.offsetMin = new Vector2(4, 2); srt.offsetMax = new Vector2(-4, -66);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false;

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
            bool armed = _armedDeleteId == tat.Id;

            var row = new GameObject("row_" + t.Id); row.transform.SetParent(content, false);
            var rrt = row.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(0.5f, 1);
            rrt.sizeDelta = new Vector2(-2 * pad, rowH);
            rrt.anchoredPosition = new Vector2(0, -(pad + index * (rowH + gap)));
            var rbg = row.AddComponent<Image>(); rbg.color = sel ? BtnSel : new Color(0.16f, 0.17f, 0.20f, 1f);
            var rowBtn = row.AddComponent<Button>(); rowBtn.targetGraphic = rbg;
            rowBtn.onClick.AddListener((UnityAction)(() => { _armedDeleteId = null; _selectedTattoo = tat; _selected = null; ShowEditor(); }));

            // eye toggle (left) - show/hide this tattoo in the preview
            var eyeBtn = IconButton(row.transform, ToolIcons.Get(tat.Visible ? "eye" : "eye_off"), tat.Visible ? new Color(0.18f, 0.46f, 0.34f, 1f) : new Color(0.30f, 0.30f, 0.34f, 1f));
            var ert = eyeBtn.GetComponent<RectTransform>();
            ert.anchorMin = new Vector2(0, 0.5f); ert.anchorMax = new Vector2(0, 0.5f); ert.pivot = new Vector2(0, 0.5f);
            ert.sizeDelta = new Vector2(30, 30); ert.anchoredPosition = new Vector2(6, 0);
            eyeBtn.onClick.AddListener((UnityAction)(() =>
            {
                _armedDeleteId = null; tat.Visible = !tat.Visible; ProjectStore.Save(_project);
                RefreshPlacementPreview(tat.PlacementEnum); ShowEditor();
            }));

            // delete (right) - trash icon; first click arms (turns into a red "Sure?"), second click confirms.
            float rightInset;
            if (armed)
            {
                var (dGO, dBtn, _) = UIFactory.ButtonWithLabel("delC_" + tat.Id, "Sure?", row.transform, new Color(0.85f, 0.20f, 0.20f, 1f), 0, 0);
                var drt = dGO.GetComponent<RectTransform>();
                drt.anchorMin = new Vector2(1, 0.5f); drt.anchorMax = new Vector2(1, 0.5f); drt.pivot = new Vector2(1, 0.5f);
                drt.sizeDelta = new Vector2(72, 30); drt.anchoredPosition = new Vector2(-6, 0);
                dBtn.onClick.AddListener((UnityAction)(() => { Core.Log?.Msg("[row] DELETE confirm '" + tat.Name + "'"); RemoveTattoo(tat); }));
                rightInset = 6 + 72 + 6;
            }
            else
            {
                var delBtn = IconButton(row.transform, ToolIcons.Get("trash"), new Color(0.62f, 0.22f, 0.22f, 1f));
                var drt = delBtn.GetComponent<RectTransform>();
                drt.anchorMin = new Vector2(1, 0.5f); drt.anchorMax = new Vector2(1, 0.5f); drt.pivot = new Vector2(1, 0.5f);
                drt.sizeDelta = new Vector2(32, 30); drt.anchoredPosition = new Vector2(-6, 0);
                delBtn.onClick.AddListener((UnityAction)(() => { _armedDeleteId = tat.Id; ShowEditor(); SetStatus("Click the red 'Sure?' to delete '" + tat.Name + "', or click elsewhere to cancel."); }));
                rightInset = 6 + 32 + 6;
            }

            // image count (how many images compose this tattoo) - sits just left of the delete control
            int n = tat.Decals.Count;
            var cnt = UIFactory.Text("cnt", n + (n == 1 ? " img" : " imgs"), row.transform, 11, TextAnchor.MiddleRight);
            cnt.color = new Color(0.62f, 0.66f, 0.72f); cnt.raycastTarget = false;
            var crt2 = cnt.rectTransform;
            crt2.anchorMin = new Vector2(1, 0.5f); crt2.anchorMax = new Vector2(1, 0.5f); crt2.pivot = new Vector2(1, 0.5f);
            crt2.sizeDelta = new Vector2(46, 24); crt2.anchoredPosition = new Vector2(-rightInset, 0);
            float cntLeft = rightInset + 46 + 4;

            // name field (fills the middle between eye and count) - this becomes the shop name
            var input = MakeInput(row.transform, tat.Name, (UnityAction<string>)(s =>
            {
                tat.Name = string.IsNullOrWhiteSpace(s) ? tat.Name : s.Trim();
                ProjectStore.Save(_project);
            }));
            var irt = input.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0); irt.anchorMax = new Vector2(1, 1);
            irt.offsetMin = new Vector2(42, 6); irt.offsetMax = new Vector2(-cntLeft, -6);
            irt.anchoredPosition = Vector2.zero;
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
            var img = go.AddComponent<Image>(); img.color = new Color(0.09f, 0.09f, 0.12f, 1f);
            go.AddComponent<RectMask2D>();   // clip the text to the field so long names don't overflow onto the icons
            var input = go.AddComponent<InputField>();

            var tgo = new GameObject("Text"); tgo.transform.SetParent(go.transform, false);
            var trt = tgo.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(8, 2); trt.offsetMax = new Vector2(-6, -2);
            var txt = tgo.AddComponent<Text>(); txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); txt.fontSize = 14; txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft; txt.supportRichText = false; txt.horizontalOverflow = HorizontalWrapMode.Overflow;

            input.textComponent = txt;
            input.lineType = InputField.LineType.SingleLine;
            input.text = value ?? "";
            try { input.onEndEdit.AddListener(onEnd); } catch { }
            return input;
        }

        private static GameObject _controlsBox;

        private static void BuildDecalControls(Transform parent)
        {
            _controlsBox = UIFactory.Panel("Controls", parent, Clear);
            var rt = _controlsBox.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(8, 6); rt.offsetMax = new Vector2(-8, -2);
            var img = _controlsBox.GetComponent<Image>(); if (img != null) img.raycastTarget = false;
            RefreshControls();
        }

        private static void RefreshControls()
        {
            if (_controlsBox == null) return;
            UIFactory.ClearChildren(_controlsBox.transform);
            _sizeVal = _rotVal = _opVal = _flipVal = null;

            TopLabel(_controlsBox.transform, "CH", _selected == null ? "No decal selected" : "Selected decal", -4, 24, 15, FontStyle.Bold);
            if (_selected == null) return;

            float y = -34;
            y = ControlRow("Size", out _sizeVal, "-", "+", () => Adjust(d => d.Scale = Mathf.Clamp(d.Scale - 0.01f, 0.004f, 2f)), () => Adjust(d => d.Scale = Mathf.Clamp(d.Scale + 0.01f, 0.004f, 2f)), y);
            y = ControlRow("Rotate", out _rotVal, "<", ">", () => Adjust(d => d.RotationDeg -= 15f), () => Adjust(d => d.RotationDeg += 15f), y);
            y = ControlRow("Opacity", out _opVal, "-", "+", () => Adjust(d => d.Opacity = Mathf.Clamp01(d.Opacity - 0.1f)), () => Adjust(d => d.Opacity = Mathf.Clamp01(d.Opacity + 0.1f)), y);
            y = ControlRow("Flip", out _flipVal, "X", "Y", () => Adjust(d => d.FlipX = !d.FlipX), () => Adjust(d => d.FlipY = !d.FlipY), y);
            UpdateControlValues();

            var (delGO, delBtn, _) = UIFactory.ButtonWithLabel("Del", "Delete decal", _controlsBox.transform, new Color(0.5f, 0.22f, 0.22f), 0, 40);
            var drt = delGO.GetComponent<RectTransform>();
            drt.anchorMin = new Vector2(0, 1); drt.anchorMax = new Vector2(1, 1); drt.pivot = new Vector2(0.5f, 1);
            drt.offsetMin = new Vector2(6, 0); drt.offsetMax = new Vector2(-6, 0); drt.sizeDelta = new Vector2(0, 40);
            drt.anchoredPosition = new Vector2(0, y - 6);
            delBtn.onClick.AddListener((UnityAction)DeleteSelected);
        }

        private static float ControlRow(string label, out Text valueText, string a, string b, Action onA, Action onB, float y)
        {
            var lab = UIFactory.Text("L_" + label, label, _controlsBox.transform, 13, TextAnchor.MiddleLeft);
            var lrt = lab.rectTransform; lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(0, 1); lrt.pivot = new Vector2(0, 1);
            lrt.anchoredPosition = new Vector2(8, y); lrt.sizeDelta = new Vector2(80, 30);

            MakeMini(_controlsBox.transform, "a_" + label, a, 92, y, 34, onA);
            MakeMini(_controlsBox.transform, "b_" + label, b, 130, y, 34, onB);

            valueText = UIFactory.Text("V_" + label, "", _controlsBox.transform, 13, TextAnchor.MiddleRight, FontStyle.Bold);
            var vrt = valueText.rectTransform; vrt.anchorMin = new Vector2(1, 1); vrt.anchorMax = new Vector2(1, 1); vrt.pivot = new Vector2(1, 1);
            vrt.anchoredPosition = new Vector2(-6, y); vrt.sizeDelta = new Vector2(78, 30);
            valueText.color = new Color(0.8f, 0.85f, 0.9f);
            return y - 38;
        }

        private static void UpdateControlValues()
        {
            if (_selected == null) return;
            if (_sizeVal != null) _sizeVal.text = _selected.Scale.ToString("0.000");
            if (_rotVal != null) _rotVal.text = Mathf.RoundToInt(Mathf.Repeat(_selected.RotationDeg, 360f)) + " °";
            if (_opVal != null) _opVal.text = Mathf.RoundToInt(_selected.Opacity * 100f) + " %";
            if (_flipVal != null) _flipVal.text = (_selected.FlipX ? "X" : "-") + " " + (_selected.FlipY ? "Y" : "-");
        }

        private static void MakeMini(Transform parent, string name, string text, float x, float y, float w, Action onClick)
        {
            var (go, btn, _) = UIFactory.ButtonWithLabel(name, text, parent, Btn, w, 30);
            var rt = go.GetComponent<RectTransform>(); rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, 30);
            btn.onClick.AddListener((UnityAction)(() => onClick()));
        }

        private static void Adjust(Action<Decal> mutate)
        {
            if (_selected == null) return;
            Snapshot();
            mutate(_selected);
            UpdateSpriteTransform(_selected);
            UpdateControlValues();
            MarkDirty();
        }

        // --- UV canvas ---

        private static void BuildUvCanvas()
        {
            var holder = UIFactory.Panel("UVHolder", _screen.transform, Clear);
            Place(holder, 0.46f, 0.05f, 0.80f, 0.92f);
            var hImg = holder.GetComponent<Image>(); if (hImg != null) hImg.raycastTarget = false;

            var area = UIFactory.Panel("UV", holder.transform, CanvasBg);
            _uvArea = area.GetComponent<RectTransform>();
            _uvArea.anchorMin = new Vector2(0.5f, 0.5f); _uvArea.anchorMax = new Vector2(0.5f, 0.5f); _uvArea.pivot = new Vector2(0.5f, 0.5f);
            _uvArea.sizeDelta = new Vector2(_uvSize, _uvSize); _uvArea.anchoredPosition = Vector2.zero;

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
            var img = _selRing.AddComponent<Image>(); img.color = new Color(0.3f, 0.85f, 0.7f, 0.5f); img.raycastTarget = false;
            _selRing.transform.SetAsFirstSibling();
        }

        // --- import list ---

        private static void BuildImportList(Transform parent)
        {
            TopLabel(parent, "IH", "Import PNGs", -12, 26, 16, FontStyle.Bold);
            var sub = UIFactory.Text("IHsub", "Put PNGs in the Import folder, then click one to place it.", parent, 11, TextAnchor.UpperCenter);
            sub.color = new Color(0.62f, 0.65f, 0.7f);
            sub.rectTransform.anchorMin = new Vector2(0, 1); sub.rectTransform.anchorMax = new Vector2(1, 1); sub.rectTransform.pivot = new Vector2(0.5f, 1);
            sub.rectTransform.anchoredPosition = new Vector2(0, -40); sub.rectTransform.sizeDelta = new Vector2(-16, 40);

            var (openGO, openBtn, _) = UIFactory.ButtonWithLabel("OpenImp", "Open import folder", parent, Accent, 0, 40);
            var ort = openGO.GetComponent<RectTransform>(); ort.anchorMin = new Vector2(0, 1); ort.anchorMax = new Vector2(1, 1); ort.pivot = new Vector2(0.5f, 1);
            ort.offsetMin = new Vector2(10, 0); ort.offsetMax = new Vector2(-10, 0); ort.sizeDelta = new Vector2(0, 40); ort.anchoredPosition = new Vector2(0, -84);
            openBtn.onClick.AddListener((UnityAction)(() => OpenFolder(Paths.Import)));

            var (refreshGO, refreshBtn, _) = UIFactory.ButtonWithLabel("Refresh", "Refresh list", parent, Btn, 0, 34);
            var rrt = refreshGO.GetComponent<RectTransform>(); rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(1, 1); rrt.pivot = new Vector2(0.5f, 1);
            rrt.offsetMin = new Vector2(10, 0); rrt.offsetMax = new Vector2(-10, 0); rrt.sizeDelta = new Vector2(0, 34); rrt.anchoredPosition = new Vector2(0, -130);
            refreshBtn.onClick.AddListener((UnityAction)(() => ShowEditor()));

            // Scroll view + grid of square thumbnail tiles (built manually so there is exactly one layout group).
            var scrollGO = new GameObject("ImportsScroll"); scrollGO.transform.SetParent(parent, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(8, 12); srt.offsetMax = new Vector2(-8, -172);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false;

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
            var bg = cell.AddComponent<Image>(); bg.color = new Color(0.16f, 0.17f, 0.20f, 1f);
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
            _armedDeleteId = null;
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
                _selected = null; _armedDeleteId = null;
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

        private static readonly Color ReviewBg = new Color(0.06f, 0.07f, 0.09f, 1f);
        private static readonly Color Hint = new Color(0.55f, 0.58f, 0.64f);

        private static void ShowReviewExport()
        {
          try
          {
            ClearScreen();
            // Regression guard: with no UV canvas, Tick() fully no-ops (no avatar spin / wheel / Delete / Ctrl+Z here).
            _uvArea = null; _decalSprites.Clear(); _selected = null; _dropdownPopup = null;

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

            _statusText = UIFactory.Text("Status", _status, _screen.transform, 14, TextAnchor.LowerCenter);
            _statusText.color = new Color(0.75f, 0.8f, 0.85f);
            Place(_statusText.gameObject, 0.33f, 0.0f, 0.98f, 0.05f);
          }
          catch (Exception e) { Core.Log?.Error("[review] ShowReviewExport failed: " + e); }
        }

        private static void BuildPackForm(Transform parent)
        {
            TopLabel(parent, "PFH", "Pack details", -8, 24, 16, FontStyle.Bold);
            float y = -44f;

            // Pack name
            ReviewLabel(parent, y, "Pack name (the mod / shop name)"); y -= 20;
            var nameHint = (Text)null;
            ReviewInput(parent, y, _project.Name, -28, (UnityAction<string>)(s =>
            {
                if (!string.IsNullOrWhiteSpace(s)) { _project.Name = s.Trim(); ProjectStore.Save(_project); }
                if (nameHint != null) nameHint.text = NameHintText();
                RefreshShopIds();
            })); y -= 34;
            nameHint = ReviewHint(parent, y, NameHintText()); y -= 30;

            // Author
            ReviewLabel(parent, y, "Author"); y -= 20;
            ReviewInput(parent, y, _project.Author, -28, (UnityAction<string>)(s => { _project.Author = (s ?? "").Trim(); ProjectStore.Save(_project); })); y -= 38;

            // Version (semantic X.Y.Z - Thunderstore requires it)
            ReviewLabel(parent, y, "Version (e.g. 1.0.0)"); y -= 20;
            var verHint = (Text)null;
            var verInput = ReviewInput(parent, y, _project.ModVersion, 140, (UnityAction<string>)(s =>
            {
                _project.ModVersion = (s ?? "").Trim(); ProjectStore.Save(_project);
                if (verHint != null) verHint.text = VersionHintText();
                RefreshShopIds();
            }));
            verHint = ReviewHintAt(parent, y - 2, 160, 280, VersionHintText()); y -= 38;

            // Website
            ReviewLabel(parent, y, "Website / source URL (optional)"); y -= 20;
            ReviewInput(parent, y, _project.WebsiteUrl, -28, (UnityAction<string>)(s => { _project.WebsiteUrl = (s ?? "").Trim(); ProjectStore.Save(_project); })); y -= 38;

            // Description + live counter
            ReviewLabel(parent, y, "Short description"); y -= 20;
            var counter = ReviewHintAt(parent, y, -90, 80, DescCount());
            var descInput = ReviewInput(parent, y - 22, _project.Description, -28, (UnityAction<string>)(s =>
            {
                _project.Description = s ?? ""; ProjectStore.Save(_project);
                if (counter != null) { counter.text = DescCount(); counter.color = _project.Description.Trim().Length > 250 ? new Color(0.9f, 0.4f, 0.4f) : Hint; }
            }));
            try { descInput.onValueChanged.AddListener((UnityAction<string>)(s =>
            {
                if (counter != null) { int n = (s ?? "").Trim().Length; counter.text = n + " / 250"; counter.color = n > 250 ? new Color(0.9f, 0.4f, 0.4f) : Hint; }
            })); } catch { }
            y -= 56;

            // License dropdown
            ReviewLabel(parent, y, "License"); y -= 20;
            var licLabels = new string[Exporter.LicenseTokens.Length];
            for (int i = 0; i < licLabels.Length; i++) licLabels[i] = Exporter.LicenseLabel(Exporter.LicenseTokens[i]);
            var (licGO, licBtn, licTxt) = UIFactory.ButtonWithLabel("lic", Exporter.LicenseLabel(_project.License) + "   v", parent, Btn, 0, 30);
            var lrt = licGO.GetComponent<RectTransform>(); lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(1, 1); lrt.pivot = new Vector2(0, 1);
            lrt.anchoredPosition = new Vector2(14, y); lrt.offsetMin = new Vector2(14, lrt.offsetMin.y); lrt.sizeDelta = new Vector2(-28, 30);
            var licRT = licGO.GetComponent<RectTransform>();
            licBtn.onClick.AddListener((UnityAction)(() =>
            {
                int cur = Array.IndexOf(Exporter.LicenseTokens, _project.License); if (cur < 0) cur = 0;
                OpenDropdown(licRT, licLabels, cur, (i) =>
                {
                    _project.License = Exporter.LicenseTokens[i];
                    licTxt.text = Exporter.LicenseLabel(_project.License) + "   v";
                    ProjectStore.Save(_project);
                });
            }));
            y -= 44;

            // Icon row: preview + pick + clear
            ReviewLabel(parent, y, "Icon (for Thunderstore, ideally 256x256)"); y -= 22;
            var iconGO = new GameObject("iconprev"); iconGO.transform.SetParent(parent, false);
            var iprt = iconGO.AddComponent<RectTransform>(); iprt.anchorMin = new Vector2(0, 1); iprt.anchorMax = new Vector2(0, 1); iprt.pivot = new Vector2(0, 1);
            iprt.anchoredPosition = new Vector2(14, y); iprt.sizeDelta = new Vector2(64, 64);
            var iimg = iconGO.AddComponent<Image>(); iimg.preserveAspect = true;
            Sprite iconSpr = string.IsNullOrEmpty(_project.IconSource) ? null : LoadSprite(ProjectStore.ResolveRelative(_project, _project.IconSource));
            if (iconSpr != null) iimg.sprite = iconSpr; else iimg.color = new Color(0.10f, 0.12f, 0.14f, 1f);
            var (pickGO, pickBtn, _) = UIFactory.ButtonWithLabel("PickIcon", "Pick icon...", parent, Accent, 140, 30);
            var pkrt = pickGO.GetComponent<RectTransform>(); pkrt.anchorMin = new Vector2(0, 1); pkrt.anchorMax = new Vector2(0, 1); pkrt.pivot = new Vector2(0, 1); pkrt.anchoredPosition = new Vector2(88, y); pkrt.sizeDelta = new Vector2(140, 30);
            pickBtn.onClick.AddListener((UnityAction)(() => ShowIconPicker()));
            var (clrGO, clrBtn, _) = UIFactory.ButtonWithLabel("ClearIcon", "Clear", parent, Btn, 80, 30);
            var clrt = clrGO.GetComponent<RectTransform>(); clrt.anchorMin = new Vector2(0, 1); clrt.anchorMax = new Vector2(0, 1); clrt.pivot = new Vector2(0, 1); clrt.anchoredPosition = new Vector2(88, y - 34); clrt.sizeDelta = new Vector2(140, 28);
            clrBtn.onClick.AddListener((UnityAction)(() => { _project.IconSource = ""; ProjectStore.Save(_project); _lastExport = null; ShowReviewExport(); }));
        }

        private static string NameHintText() => "Exports as: " + Paths.Sanitize(_project.Name) + "   (Thunderstore: " + Exporter.ToThunderstoreName(_project.Name) + ")";
        private static string VersionHintText() => "-> " + Exporter.NormalizeVersion(_project.ModVersion) + "   (used in the shop id + Thunderstore)";
        private static string DescCount() => _project.Description.Trim().Length + " / 250";

        private static void ReviewLabel(Transform parent, float y, string text)
        {
            var l = UIFactory.Text("lbl", text, parent, 12, TextAnchor.UpperLeft, FontStyle.Bold);
            l.color = new Color(0.74f, 0.78f, 0.84f);
            var rt = l.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(14, y); rt.sizeDelta = new Vector2(-28, 18);
        }

        private static InputField ReviewInput(Transform parent, float y, string value, float width, UnityAction<string> onEnd)
        {
            var inp = MakeInput(parent, value, onEnd);
            var rt = inp.GetComponent<RectTransform>();
            if (width <= 0) { rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1); rt.anchoredPosition = new Vector2(14, y); rt.sizeDelta = new Vector2(width, 30); }
            else { rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1); rt.anchoredPosition = new Vector2(14, y); rt.sizeDelta = new Vector2(width, 30); }
            return inp;
        }

        private static Text ReviewHint(Transform parent, float y, string text)
        {
            var t = UIFactory.Text("hint", text, parent, 11, TextAnchor.UpperLeft);
            t.color = Hint; t.raycastTarget = false;
            var rt = t.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(14, y); rt.sizeDelta = new Vector2(-28, 16);
            return t;
        }

        private static Text ReviewHintAt(Transform parent, float y, float x, float w, string text)
        {
            var t = UIFactory.Text("hint", text, parent, 11, TextAnchor.UpperLeft);
            t.color = Hint; t.raycastTarget = false;
            var rt = t.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x < 0 ? 14 : x, y); rt.sizeDelta = new Vector2(w, 16);
            if (x < 0) { rt.anchorMax = new Vector2(1, 1); rt.anchoredPosition = new Vector2(0, y); rt.pivot = new Vector2(1, 1); rt.sizeDelta = new Vector2(-14, 16); }
            return t;
        }

        // --- tattoo overview table ---

        private static void BuildReviewTable(Transform parent)
        {
            _reviewIdCells.Clear();
            var sorted = AllTattoosInOrder();
            TopLabel(parent, "TOH", "Tattoos (" + sorted.Count + ") - edit name, body part, price, shop id", -8, 22, 14, FontStyle.Bold);

            // pinned caption row (same column fractions as the data rows)
            var cap = UIFactory.Panel("cap", parent, Clear); var caprt = cap.GetComponent<RectTransform>();
            caprt.anchorMin = new Vector2(0, 1); caprt.anchorMax = new Vector2(1, 1); caprt.pivot = new Vector2(0.5f, 1); caprt.anchoredPosition = new Vector2(0, -34); caprt.sizeDelta = new Vector2(-12, 18);
            var capImg = cap.GetComponent<Image>(); if (capImg != null) capImg.raycastTarget = false;
            CaptionCell(cap.transform, 0.045f, 0.40f, "Name (shown in the shop)");
            CaptionCell(cap.transform, 0.405f, 0.56f, "Body part");
            CaptionCell(cap.transform, 0.565f, 0.66f, "Price");
            CaptionCell(cap.transform, 0.665f, 0.88f, "Shop id");
            CaptionCell(cap.transform, 0.885f, 1.0f, "Imgs");

            // manual scroll (nested LayoutGroups collapse here; mirror the import grid)
            var scrollGO = new GameObject("TblScroll"); scrollGO.transform.SetParent(parent, false);
            var srt = scrollGO.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(6, 6); srt.offsetMax = new Vector2(-6, -56);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false;
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
            rrt.sizeDelta = new Vector2(-2 * pad, rowH); rrt.anchoredPosition = new Vector2(0, -(pad + index * (rowH + gap)));
            var rbg = row.AddComponent<Image>(); rbg.color = imgs == 0 ? new Color(0.26f, 0.20f, 0.14f, 1f) : new Color(0.16f, 0.17f, 0.20f, 1f);
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

            // placement (click-to-open dropdown; the row never moves when this changes)
            var (plGO, plBtn, plTxt) = UIFactory.ButtonWithLabel("pl", Label(tat.PlacementEnum) + "  v", row.transform, new Color(0.22f, 0.27f, 0.30f, 1f), 0, 28);
            SetCell(plGO, 0.405f, 0.56f);
            var plRT = plGO.GetComponent<RectTransform>();
            plBtn.onClick.AddListener((UnityAction)(() => OpenPlacementDropdown(tat, plRT, plTxt)));

            // price (editable, German-comma safe)
            var priceIn = MakeInput(row.transform, tat.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), null);
            SetCell(priceIn.gameObject, 0.565f, 0.66f);
            priceIn.onEndEdit.AddListener((UnityAction<string>)(s =>
            {
                if (TryParsePrice(s, out float f)) { tat.Price = f; ProjectStore.Save(_project); priceIn.text = f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
                else { priceIn.text = tat.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); SetStatus("Price must be a number (e.g. 250)."); }
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

        // A reusable lightweight click-to-pick dropdown. Built as a custom popup positioned at the cursor
        // (GetWorldCorners is unreliable on this CanvasScaler-less overlay canvas; the mouse is in screen px = canvas px).
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
            const float itemH = 30f;
            float w = Mathf.Max(150f, anchorRT.rect.width);
            float h = n * itemH + 8f;
            var panel = UIFactory.Panel("ddPanel", catcher.transform, new Color(0.14f, 0.15f, 0.18f, 1f));
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.zero; prt.pivot = new Vector2(0, 1);
            prt.sizeDelta = new Vector2(w, h);
            Vector3 mp = Input.mousePosition;
            float x = Mathf.Clamp(mp.x, 0f, Mathf.Max(0f, Screen.width - w));
            float topY = mp.y; if (topY - h < 0f) topY = h;   // flip up if it would run off the bottom
            prt.anchoredPosition = new Vector2(x, topY);
            var ol = panel.AddComponent<Outline>(); ol.effectColor = new Color(0, 0, 0, 0.7f); ol.effectDistance = new Vector2(2, 2);

            for (int i = 0; i < n; i++)
            {
                int idx = i; bool cur = i == currentIdx;
                var (oGO, oBtn, _) = UIFactory.ButtonWithLabel("dd_" + i, optionLabels[i], panel.transform, cur ? BtnSel : new Color(0.20f, 0.22f, 0.26f, 1f), 0, itemH - 2);
                var ort = oGO.GetComponent<RectTransform>(); ort.anchorMin = new Vector2(0, 1); ort.anchorMax = new Vector2(1, 1); ort.pivot = new Vector2(0.5f, 1);
                ort.offsetMin = new Vector2(4, 0); ort.offsetMax = new Vector2(-4, 0); ort.sizeDelta = new Vector2(0, itemH - 2); ort.anchoredPosition = new Vector2(0, -(4 + i * itemH));
                oBtn.onClick.AddListener((UnityAction)(() => { onSelect(idx); CloseDropdown(); }));
            }
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
                    if (cellLabel != null) cellLabel.text = Label(p) + "  v";
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
            TopLabel(parent, "EPH", "Pre-flight & export", -8, 22, 16, FontStyle.Bold);

            var issues = Exporter.Validate(_project);
            bool hasError = false;
            float y = -40f;
            foreach (var it in issues)
            {
                if (it.Item1 == Exporter.Severity.Error) hasError = true;
                Color c = it.Item1 == Exporter.Severity.Error ? new Color(0.95f, 0.45f, 0.45f) : it.Item1 == Exporter.Severity.Warning ? new Color(0.95f, 0.75f, 0.4f) : Hint;
                string pfx = it.Item1 == Exporter.Severity.Error ? "[x] " : it.Item1 == Exporter.Severity.Warning ? "[!] " : "[i] ";
                var chip = UIFactory.Text("chip", pfx + it.Item2, parent, 12, TextAnchor.UpperLeft);
                chip.color = c; chip.raycastTarget = false; chip.horizontalOverflow = HorizontalWrapMode.Wrap; chip.verticalOverflow = VerticalWrapMode.Truncate;
                var rt = chip.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
                rt.anchoredPosition = new Vector2(0, y); rt.sizeDelta = new Vector2(-24, 30);
                y -= 26f;
            }
            if (issues.Count == 0) { var ok = ReviewHint(parent, y, "Everything looks good."); y -= 26f; }

            y -= 6f;
            var (expGO, expBtn, expTxt) = UIFactory.ButtonWithLabel("DoExport", hasError ? "Fix errors to export" : "Export mod", parent,
                hasError ? new Color(0.30f, 0.28f, 0.22f) : new Color(0.55f, 0.42f, 0.16f), 0, 48);
            var ert = expGO.GetComponent<RectTransform>(); ert.anchorMin = new Vector2(0, 1); ert.anchorMax = new Vector2(1, 1); ert.pivot = new Vector2(0.5f, 1);
            ert.anchoredPosition = new Vector2(0, y); ert.offsetMin = new Vector2(16, ert.offsetMin.y); ert.sizeDelta = new Vector2(-32, 48);
            if (!hasError) expBtn.onClick.AddListener((UnityAction)(() => ExportFlow()));
            else expBtn.onClick.AddListener((UnityAction)(() => SetStatus("Resolve the red items above first.")));
            y -= 60f;

            // result card (only after an export)
            if (_lastExport != null) BuildResultCard(parent, y);
        }

        private static void BuildResultCard(Transform parent, float yTop)
        {
            ExportResult r = _lastExport;
            var card = UIFactory.Panel("Result", parent, r.Ok ? new Color(0.10f, 0.20f, 0.13f, 1f) : new Color(0.24f, 0.10f, 0.10f, 1f));
            var crt = card.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = new Vector2(16, 8); crt.offsetMax = new Vector2(-16, yTop);
            var outline = card.AddComponent<Outline>(); outline.effectColor = r.Ok ? new Color(0.3f, 0.7f, 0.4f, 1f) : new Color(0.8f, 0.3f, 0.3f, 1f); outline.effectDistance = new Vector2(2, 2);

            var head = UIFactory.Text("rh", r.Ok ? ("Exported " + r.TattoosWritten + " tattoo(s)") : "Export failed", card.transform, 16, TextAnchor.UpperLeft, FontStyle.Bold);
            head.color = r.Ok ? new Color(0.6f, 0.95f, 0.7f) : new Color(0.98f, 0.6f, 0.6f);
            var hrt = head.rectTransform; hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1); hrt.pivot = new Vector2(0.5f, 1); hrt.anchoredPosition = new Vector2(12, -8); hrt.sizeDelta = new Vector2(-24, 22);

            var body = new System.Text.StringBuilder();
            if (r.Ok) body.AppendLine("Folder: " + r.ExportFolder);
            foreach (var w in r.Warnings) body.AppendLine("- " + w);
            string bodyStr = body.ToString();

            // Scrollable body so a long export path + many warnings stay reachable (not clipped).
            var scrollGO = new GameObject("rbody"); scrollGO.transform.SetParent(card.transform, false);
            var srt = scrollGO.AddComponent<RectTransform>(); srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(12, 44); srt.offsetMax = new Vector2(-12, -34);
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false;
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
            SetStatus(_lastExport.Ok ? ("Exported '" + _project.Name + "' (" + _lastExport.TattoosWritten + " tattoo(s)).") : "Export failed.");
            ShowReviewExport();
        }

        // --- icon picker (reuses the imported source PNGs / import folder) ---

        private static void ShowIconPicker()
        {
            ClearScreen();
            _screen = UIFactory.Panel("IconPicker", _canvasGO.transform, ReviewBg, fullAnchor: true);
            UIFactory.Text("Title", "Pick an icon (from your imported PNGs)", _screen.transform, 24, TextAnchor.UpperCenter, FontStyle.Bold)
                .rectTransform.anchoredPosition = new Vector2(0, -30);

            var scrollGO = new GameObject("IconScroll"); scrollGO.transform.SetParent(_screen.transform, false);
            var srt = scrollGO.AddComponent<RectTransform>(); srt.anchorMin = new Vector2(0.1f, 0.12f); srt.anchorMax = new Vector2(0.9f, 0.86f); srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
            var scroll = scrollGO.AddComponent<ScrollRect>(); scroll.horizontal = false;
            var vp = new GameObject("Viewport"); vp.transform.SetParent(scrollGO.transform, false);
            var vprt = vp.AddComponent<RectTransform>(); vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = new Color(0, 0, 0, 0.04f); vp.AddComponent<Mask>().showMaskGraphic = false; scroll.viewport = vprt;
            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var crt = content.AddComponent<RectTransform>(); crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            scroll.content = crt;

            string[] files = ImageLoader.ListImportImages();
            const int cols = 4; const float gap = 10f, pad = 8f, cellH = 150f;
            int rows = (files.Length + cols - 1) / cols;
            crt.sizeDelta = new Vector2(0, pad * 2f + Mathf.Max(1, rows) * (cellH + gap));
            if (files.Length == 0)
            {
                var none = UIFactory.Text("None", "No PNGs in the import folder. Use 'Open import folder' in the editor first.", content.transform, 14, TextAnchor.UpperCenter);
                none.color = Hint; none.rectTransform.anchorMin = new Vector2(0, 1); none.rectTransform.anchorMax = new Vector2(1, 1); none.rectTransform.pivot = new Vector2(0.5f, 1); none.rectTransform.anchoredPosition = new Vector2(0, -10); none.rectTransform.sizeDelta = new Vector2(-8, 40);
            }
            for (int i = 0; i < files.Length; i++)
            {
                string p = files[i]; int col = i % cols, row = i / cols;
                var cell = new GameObject("tile"); cell.transform.SetParent(content.transform, false);
                var cellrt = cell.AddComponent<RectTransform>(); cellrt.anchorMin = new Vector2(col / (float)cols, 1f); cellrt.anchorMax = new Vector2((col + 1) / (float)cols, 1f); cellrt.pivot = new Vector2(0.5f, 1f);
                cellrt.sizeDelta = new Vector2(-gap, cellH); cellrt.anchoredPosition = new Vector2(0, -(pad + row * (cellH + gap)));
                var bg = cell.AddComponent<Image>(); bg.color = new Color(0.16f, 0.17f, 0.20f, 1f);
                var btn = cell.AddComponent<Button>(); btn.targetGraphic = bg;
                btn.onClick.AddListener((UnityAction)(() => PickIcon(p)));
                var thumb = new GameObject("thumb"); thumb.transform.SetParent(cell.transform, false);
                var trt = thumb.AddComponent<RectTransform>(); trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1); trt.offsetMin = new Vector2(4, 22); trt.offsetMax = new Vector2(-4, -4);
                var timg = thumb.AddComponent<Image>(); timg.preserveAspect = true; timg.raycastTarget = false; var spr = LoadSprite(p); if (spr != null) timg.sprite = spr; else timg.color = new Color(1, 1, 1, 0.1f);
                string nm = Path.GetFileNameWithoutExtension(p); if (nm.Length > 16) nm = nm.Substring(0, 14) + "..";
                var lbl = UIFactory.Text("n", nm, cell.transform, 10, TextAnchor.LowerCenter); lbl.raycastTarget = false; lbl.color = new Color(0.82f, 0.84f, 0.88f);
                var lrt = lbl.rectTransform; lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0); lrt.pivot = new Vector2(0.5f, 0); lrt.anchoredPosition = new Vector2(0, 2); lrt.sizeDelta = new Vector2(-2, 16);
            }

            var (cancelGO, cancelBtn, _) = UIFactory.ButtonWithLabel("Cancel", "Cancel", _screen.transform, Btn, 200, 46);
            PlaceBottom(cancelGO, 0, 28);
            cancelBtn.onClick.AddListener((UnityAction)(() => ShowReviewExport()));
        }

        private static void PickIcon(string absPath)
        {
            string rel = ProjectStore.ImportSource(_project, absPath);
            if (rel != null) { _project.IconSource = rel; ProjectStore.Save(_project); SetStatus("Icon set from " + Path.GetFileName(absPath) + "."); }
            else SetStatus("Could not use that image as the icon.");
            _lastExport = null;   // a metadata edit invalidates the previous export's result card
            ShowReviewExport();
        }

        private static void MarkDirty() { _previewDirty = true; _lastEdit = Time.time; }

        // --- per-frame: drag + debounced live preview (called from Core.OnUpdate while open) ---

        public static void Tick()
        {
            if (_canvasGO == null || _uvArea == null) return;

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
                    Preview.ZoomCamera(wheel);
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
                    _dragStartMouse = mp; _startRot = hit.RotationDeg; _startScale = hit.Scale;
                    var rt = _decalSprites[hit].GetComponent<RectTransform>();
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(_uvArea, mp, null, out Vector2 local);
                    _dragOffset = rt.anchoredPosition - local;
                    UpdateSelectionRing(); RefreshControls();
                }
                else if (OverCanvas(mp))
                {
                    // Clicked an empty spot on the canvas -> deselect.
                    if (_selected != null) { _selected = null; UpdateSelectionRing(); RefreshControls(); }
                }
                else if (InCharacterRegion(mp))
                {
                    if (_selected != null) { _selected = null; UpdateSelectionRing(); RefreshControls(); }
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
            if (_toolText != null) _toolText.text = "Tool: " + t + "   (W move / E rotate / R scale; wheel over canvas = scale, over character = zoom)";
            try
            {
                // Move uses the default OS cursor; Rotate/Scale use real (free) icons at ~28px (small cursor).
                Texture2D cur = t == Tool.Move ? null : ToolIcons.GetSized(t == Tool.Rotate ? "rotate" : "scale", 28);
                if (cur != null) Cursor.SetCursor(cur, new Vector2(cur.width * 0.5f, cur.height * 0.5f), CursorMode.ForceSoftware);
                else Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
            catch { }
        }

        // The open centre-left strip where the 3D character stands (between the left panel and the UV canvas).
        private static bool InCharacterRegion(Vector2 screenPos)
        {
            float x = screenPos.x;
            return x > Screen.width * 0.17f && x < Screen.width * 0.45f;
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

        private static Text TopLabel(Transform parent, string name, string text, float y, float h, int size, FontStyle style)
        {
            var t = UIFactory.Text(name, text, parent, size, TextAnchor.UpperCenter, style);
            var rt = t.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y); rt.sizeDelta = new Vector2(0, h);
            return t;
        }

        private static void SetStatus(string s) { _status = s; if (_statusText != null) _statusText.text = s; Core.Log?.Msg("[editor] " + s); }
        private static void ClearScreen() { if (_screen != null) { UnityEngine.Object.Destroy(_screen); _screen = null; } }

        private static void MakeBarButton(Transform parent, string label, Color color, Action onClick, float width = 150f)
        {
            var (go, btn, _) = UIFactory.ButtonWithLabel("bar_" + label, label, parent, color, width, 40);
            AddLE(go, 0, width + 8f);
            btn.onClick.AddListener((UnityAction)(() => onClick()));
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
