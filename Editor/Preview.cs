using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Core.Avatar.Properties;
using UnityEngine;
using AvatarAppearance = Il2CppScheduleOne.Avatar.AvatarAppearance;
using AvatarObjectLibrary = Il2CppScheduleOne.Avatar.AvatarObjectLibrary;
using CoreLayer = Il2CppScheduleOne.Core.Avatar.AvatarLayer;
using CoreObject = Il2CppScheduleOne.Core.Avatar.AvatarObject;
using LegacyLayer = Il2CppScheduleOne.AvatarFramework.AvatarLayer;
using MenuAvatar = Il2CppScheduleOne.AvatarFramework.Avatar;
using S1MenuRig = Il2CppScheduleOne.UI.MainMenu.MainMenuRig;
using SerializedObject = Il2CppScheduleOne.Core.Avatar.SerializedAvatarObject;

namespace Inkubator.Editor
{
    /// <summary>
    /// Applies baked tattoo textures to the menu avatar live. The game paints an avatar from the avatar objects
    /// applied to its <see cref="AvatarAppearance"/>, and it only instantiates objects it finds in
    /// <see cref="AvatarObjectLibrary"/>. So each placement gets one session object: a copy of a stock tattoo of
    /// that placement (which brings the right layer type and layering order), registered in the library under an
    /// Inkubator id and carrying the baked texture. Re-baking swaps the texture on the applied copy and repaints.
    /// </summary>
    public static class Preview
    {
        /// <summary>Layer slots the body material has. The game paints the lowest-ordered layers and drops the rest.</summary>
        public const int BodySlots = 8;

        private static MenuAvatar _avatar;
        // Library templates the session tattoos are instantiated from, parked under an inactive holder.
        private static readonly Dictionary<Placement, CoreObject> _templates = new Dictionary<Placement, CoreObject>();
        private static GameObject _templateHolder;
        // Baked texture currently shown per placement, destroyed on re-bake so live preview does not leak textures.
        private static readonly Dictionary<Placement, Texture2D> _lastTex = new Dictionary<Placement, Texture2D>();
        // Ids of the character's own tattoos that are switched off because the project's layers need their slots.
        private static readonly List<string> _hiddenStock = new List<string>();
        // Layering order the game gives body tattoos, read from a stock one. Null until it has been resolved.
        private static int? _tattooOrder;

        /// <summary>How many of the character's own tattoos the last apply had to hide to stay inside the budget.</summary>
        public static int TrimmedStockCount => _hiddenStock.Count;

        /// <summary>Locate (and cache) the main-menu avatar. Returns false if not in the menu scene.</summary>
        public static bool EnsureAvatar()
        {
            if (_avatar != null) return true;
            try
            {
                var rigs = UnityEngine.Object.FindObjectsOfType<S1MenuRig>(true);
                if (rigs == null || rigs.Length == 0) return false;
                _avatar = rigs[0].Avatar;
                return _avatar != null;
            }
            catch (Exception e) { Core.Log?.Warning("[preview] find avatar: " + e.Message); return false; }
        }

        private static AvatarAppearance Appearance()
        {
            if (!EnsureAvatar()) { Core.Log?.Warning("[preview] no menu avatar"); return null; }
            AvatarAppearance ap = _avatar.Appearance;
            if (ap == null) Core.Log?.Warning("[preview] the menu avatar has no appearance component");
            return ap;
        }

        /// <summary>Bake one tattoo entry and show it on the avatar. Returns false on failure.</summary>
        public static bool ApplyEntry(Project project, TattooEntry entry)
        {
            try
            {
                if (entry == null) return false;
                Texture2D baked = Baker.Bake(project, entry);
                if (baked == null) return false;
                if (Show(entry.PlacementEnum, baked)) return true;
                UnityEngine.Object.Destroy(baked);
                return false;
            }
            catch (Exception e) { Core.Log?.Warning("[preview] apply: " + e.Message); return false; }
        }

        /// <summary>
        /// Show the given decals (the composite of all visible tattoos of a placement) on the avatar, or remove the
        /// placement's session object when the list is empty. One session object per placement.
        /// </summary>
        public static bool ApplyPlacement(Project project, Placement placement, List<Decal> decals)
        {
            try
            {
                if (decals == null || decals.Count == 0)
                {
                    AvatarAppearance ap = Appearance();
                    if (ap == null) return false;
                    CoreObject live = FindApplied(ap, Placements.SessionObjectId(placement));
                    if (live != null) Remove(ap, live);
                    DestroyTexture(placement);
                    Repaint(ap);
                    return true;
                }

                Texture2D baked = Baker.Bake(project, decals);
                if (baked == null) { Core.Log?.Warning("[preview] bake failed for " + Placements.Token(placement)); return false; }
                if (Show(placement, baked)) return true;
                UnityEngine.Object.Destroy(baked);
                return false;
            }
            catch (Exception e) { Core.Log?.Warning("[preview] applyplacement: " + e.Message); return false; }
        }

        // Puts a baked texture on the placement's session object, applying that object first when it is not on the
        // avatar yet. Takes ownership of the texture on success.
        private static bool Show(Placement placement, Texture2D baked)
        {
            AvatarAppearance ap = Appearance();
            if (ap == null) return false;
            CoreObject template = EnsureTemplate(placement);
            if (template == null) return false;
            SetTexture(template, baked);

            string id = Placements.SessionObjectId(placement);
            CoreObject live = FindApplied(ap, id);
            if (live != null)
            {
                // The applied object owns copies of the template's layers, so it needs the new texture itself.
                SetTexture(live, baked);
            }
            else
            {
                live = ap.ApplyNakedObject(new SerializedObject(id, new Il2CppReferenceArray<SerializedColorProperty>(0)), false);
                if (live == null) { Core.Log?.Warning("[preview] the game did not apply " + id); return false; }
            }

            if (_lastTex.TryGetValue(placement, out Texture2D old) && old != null && old != baked) UnityEngine.Object.Destroy(old);
            _lastTex[placement] = baked;
            Repaint(ap);
            return true;
        }

        // The library entry a placement's session object is instantiated from: a copy of the stock tattoo the
        // placement is modelled on, with layer assets of its own so a re-bake never repaints the stock art.
        private static CoreObject EnsureTemplate(Placement placement)
        {
            if (_templates.TryGetValue(placement, out CoreObject cached) && cached != null) return cached;

            CoreObject stock = StockObject(Placements.SourceLayer(placement));
            if (stock == null) return null;

            if (_templateHolder == null)
            {
                _templateHolder = new GameObject("Inkubator_PreviewTemplates");
                _templateHolder.SetActive(false);
            }

            string id = Placements.SessionObjectId(placement);
            CoreObject template = UnityEngine.Object.Instantiate(stock, _templateHolder.transform);
            template.gameObject.name = id;
            template._id = id;
            template._name = "Inkubator " + Placements.Token(placement);

            var shared = template._layers;
            var own = new Il2CppSystem.Collections.Generic.List<CoreLayer>();
            if (shared != null)
                for (int i = 0; i < shared.Count; i++)
                {
                    if (shared[i] == null) continue;
                    CoreLayer layer = UnityEngine.Object.Instantiate(shared[i]);
                    layer._normal = null;   // the stock tattoo's normal map does not fit a different image
                    own.Add(layer);
                }
            if (own.Count == 0)
            {
                Core.Log?.Warning("[preview] stock tattoo " + stock.Id + " has no layer to paint on");
                UnityEngine.Object.Destroy(template.gameObject);
                return null;
            }
            template._layers = own;

            AvatarObjectLibrary._avatarObjects[id] = template;
            _templates[placement] = template;
            Core.Log?.Msg("[preview] registered " + id + " (from " + stock.Id + ")");
            return template;
        }

        // The avatar object the game uses for one of its built-in layers.
        private static CoreObject StockObject(string layerPath)
        {
            UnityEngine.Object asset = Resources.Load(layerPath);
            LegacyLayer layer = asset != null ? asset.TryCast<LegacyLayer>() : null;
            if (layer == null) { Core.Log?.Warning("[preview] stock layer not found: " + layerPath); return null; }
            CoreObject stock = layer.AvatarObjectEquivalent;
            if (stock == null) Core.Log?.Warning("[preview] stock layer has no avatar object: " + layerPath);
            return stock;
        }

        private static void SetTexture(CoreObject obj, Texture2D tex)
        {
            var layers = obj._layers;
            if (layers == null) return;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i] != null) layers[i]._texture = tex;
        }

        private static CoreObject FindApplied(AvatarAppearance ap, string id)
        {
            var applied = ap._appliedObjects;
            if (applied == null) return null;
            for (int i = 0; i < applied.Count; i++)
            {
                CoreObject o = applied[i];
                if (o != null && o.Id == id) return o;
            }
            return null;
        }

        private static bool IsSessionObject(CoreObject o) =>
            o != null && o.Id != null && o.Id.StartsWith(Placements.SessionIdPrefix, StringComparison.Ordinal);

        // Session tattoos go on as naked objects. The fallback covers one the game no longer lists there.
        private static void Remove(AvatarAppearance ap, CoreObject o)
        {
            var naked = ap._nakedObjects;
            if (naked != null)
                for (int i = 0; i < naked.Count; i++)
                    if (naked[i] != null && naked[i].Pointer == o.Pointer) { ap.RemoveNakedObject(o, false); return; }
            ap.RemoveAvatarObject(o, false);
        }

        private static int RemoveSessionObjects(AvatarAppearance ap)
        {
            var applied = ap._appliedObjects;
            if (applied == null) return 0;
            int removed = 0;
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                CoreObject o = applied[i];
                if (!IsSessionObject(o)) continue;
                Remove(ap, o);
                removed++;
            }
            return removed;
        }

        // The single place that repaints the avatar. The body material has eight layer slots and the game fills
        // them in layering order, so with more layers than slots the top garments silently go missing. To keep the
        // clothes, the character's own tattoos give up their slots first: every one hidden earlier is shown again
        // (so the trim heals as soon as room frees up) and then as many as needed are hidden.
        private static void Repaint(AvatarAppearance ap)
        {
            foreach (string id in _hiddenStock)
            {
                CoreObject o = FindApplied(ap, id);
                if (o != null) o.SetEnabled(true);
            }
            _hiddenStock.Clear();

            var applied = ap._appliedObjects;
            int used = 0;
            if (applied != null)
                for (int i = 0; i < applied.Count; i++) used += BodyLayers(applied[i]);

            if (used > BodySlots && applied != null && TattooOrder(out int order))
            {
                for (int i = 0; i < applied.Count && used > BodySlots; i++)
                {
                    CoreObject o = applied[i];
                    if (IsSessionObject(o) || !PaintsAt(o, order)) continue;
                    used -= BodyLayers(o);
                    o.SetEnabled(false);
                    _hiddenStock.Add(o.Id);
                    Core.Log?.Msg("[budget] hid stock tattoo " + o.Id + " (only " + BodySlots + " body layers render)");
                }
            }
            ap.RepaintLayers();
        }

        // How many body slots an applied object takes: its enabled full-body layers.
        private static int BodyLayers(CoreObject o)
        {
            var layers = o != null ? o._layers : null;
            if (layers == null) return 0;
            int n = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                CoreLayer l = layers[i];
                if (l != null && l.LayerEnabled && l.Type == CoreLayer.EType.FullBody) n++;
            }
            return n;
        }

        private static bool PaintsAt(CoreObject o, int order)
        {
            var layers = o != null ? o._layers : null;
            if (layers == null) return false;
            for (int i = 0; i < layers.Count; i++)
            {
                CoreLayer l = layers[i];
                if (l != null && l.LayerEnabled && l.Type == CoreLayer.EType.FullBody && l.LayeringOrder == order) return true;
            }
            return false;
        }

        // Body tattoos all share one layering order, which is what tells them apart from skin details below and
        // garments above. It is read from a stock chest tattoo rather than assumed.
        private static bool TattooOrder(out int order)
        {
            if (_tattooOrder == null)
            {
                CoreObject stock = StockObject(Placements.SourceLayer(Placement.Chest));
                var layers = stock != null ? stock._layers : null;
                if (layers != null && layers.Count > 0 && layers[0] != null) _tattooOrder = layers[0].LayeringOrder;
                else Core.Log?.Warning("[budget] tattoo layering order unknown, stock tattoos keep their slots");
            }
            order = _tattooOrder ?? 0;
            return _tattooOrder != null;
        }

        /// <summary>
        /// Hard reset: take EVERY Inkubator session object off the avatar so leftovers from an earlier project
        /// disappear. The editor calls this before re-applying the current project, so the character shows exactly
        /// the project's tattoos and nothing else.
        /// </summary>
        public static void ResetSessionLayers()
        {
            try
            {
                AvatarAppearance ap = Appearance();
                if (ap == null) return;
                RemoveSessionObjects(ap);
                DestroyTextures();
                Repaint(ap);
            }
            catch (Exception e) { Core.Log?.Warning("[preview] reset: " + e.Message); }
        }

        /// <summary>Take all Inkubator session objects off the avatar and out of the game's object library.</summary>
        public static void Clear()
        {
            try
            {
                if (_avatar != null && _avatar.Appearance != null)
                {
                    AvatarAppearance ap = _avatar.Appearance;
                    int removed = RemoveSessionObjects(ap);
                    Repaint(ap);
                    Core.Log?.Msg("[preview] cleared " + removed + " session tattoo(s)");
                }
            }
            catch (Exception e) { Core.Log?.Warning("[preview] clear: " + e.Message); }
            finally { DropTemplates(); DestroyTextures(); }
        }

        /// <summary>Return the menu character to normal (clothes back, tattoos off, un-rotated, un-zoomed) on editor close.</summary>
        public static void ExitEditor()
        {
            SetStripUnderwear(false);
            SetStripClothing(false);
            Clear();
            RestoreRig();
        }

        /// <summary>Drop the cached avatar (call when leaving the menu scene).</summary>
        public static void Forget()
        {
            RestoreRig(); _avatar = null; DropTemplates(); DestroyTextures(); UvRegions.Forget();
            _outerStripped = false; _hiddenOuter.Clear();
            _underwearStripped = false; _hiddenUnderwear.Clear();
            _hiddenStock.Clear();
            _outerIds = null;
        }

        // Unregisters the session templates. The scene may already have destroyed the objects, so the library
        // entries go by id and the holder only when it is still alive.
        private static void DropTemplates()
        {
            try
            {
                var library = AvatarObjectLibrary._avatarObjects;
                if (library != null)
                    foreach (Placement p in Placements.All) library.Remove(Placements.SessionObjectId(p));
            }
            catch (Exception e) { Core.Log?.Warning("[preview] unregister templates: " + e.Message); }
            _templates.Clear();
            if (_templateHolder != null) UnityEngine.Object.Destroy(_templateHolder);
            _templateHolder = null;
        }

        // Two independent clothing toggles: the outer garments (shirt / trousers) and the underwear. Each switches
        // the matching avatar objects off rather than removing them, and remembers their ids to switch them back on.
        private static bool _outerStripped;
        private static readonly List<string> _hiddenOuter = new List<string>();
        private static bool _underwearStripped;
        private static readonly List<string> _hiddenUnderwear = new List<string>();
        // Ids of the avatar objects behind the game's Top and Bottom layers. Null until first needed.
        private static HashSet<string> _outerIds;

        public static bool IsClothingStripped => _outerStripped;
        public static bool IsUnderwearStripped => _underwearStripped;

        /// <summary>
        /// Show or hide the character's outer clothes (shirt / pants) so body tattoos under them are visible.
        /// The Nipples censor and the underwear are kept (the underwear has its own toggle). Returns the new state.
        /// </summary>
        public static bool SetStripClothing(bool strip) => StripMatching(strip, ref _outerStripped, _hiddenOuter, IsOuterClothing, "clothes");

        /// <summary>Show or hide just the underwear (independent of the outer clothes). Returns the new state.</summary>
        public static bool SetStripUnderwear(bool strip) => StripMatching(strip, ref _underwearStripped, _hiddenUnderwear, IsUnderwear, "underwear");

        private static bool StripMatching(bool strip, ref bool state, List<string> hidden, Func<CoreObject, bool> match, string tag)
        {
            try
            {
                if (strip == state) return state;
                if (!EnsureAvatar() || _avatar.Appearance == null) return state;
                AvatarAppearance ap = _avatar.Appearance;
                var applied = ap._appliedObjects;
                if (applied == null) return state;

                if (strip)
                {
                    hidden.Clear();
                    for (int i = 0; i < applied.Count; i++)
                    {
                        CoreObject o = applied[i];
                        if (o == null || !match(o)) continue;
                        Core.Log?.Msg("[" + tag + "] STRIP " + o.Id);
                        o.SetEnabled(false);
                        hidden.Add(o.Id);
                    }
                }
                else
                {
                    foreach (string id in hidden)
                    {
                        CoreObject o = FindApplied(ap, id);
                        if (o == null) { Core.Log?.Msg("[" + tag + "] " + id + " is no longer on the avatar"); continue; }
                        o.SetEnabled(true);
                    }
                    hidden.Clear();
                }
                state = strip;
                Repaint(ap);
            }
            catch (Exception e) { Core.Log?.Warning("[preview] strip " + tag + ": " + e.Message); }
            return state;
        }

        // Outer clothing = the garments the game keeps under its Top and Bottom layers, but NOT the Nipples censor
        // and NOT the underwear (those are kept or have their own toggle). Tattoos are never clothing.
        private static bool IsOuterClothing(CoreObject o)
        {
            string id = o.Id;
            if (string.IsNullOrEmpty(id) || IsSessionObject(o)) return false;
            if (id.IndexOf("nipple", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (id.IndexOf("underwear", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return OuterIds().Contains(id);
        }

        private static bool IsUnderwear(CoreObject o)
        {
            string id = o.Id;
            if (string.IsNullOrEmpty(id) || IsSessionObject(o)) return false;
            return id.IndexOf("underwear", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HashSet<string> OuterIds()
        {
            if (_outerIds != null) return _outerIds;
            _outerIds = new HashSet<string>();
            foreach (string folder in new[] { "Avatar/Layers/Top", "Avatar/Layers/Bottom" })
            {
                var layers = Resources.LoadAll<LegacyLayer>(folder);
                int found = 0;
                if (layers != null)
                    for (int i = 0; i < layers.Length; i++)
                    {
                        CoreObject o = layers[i] != null ? layers[i].AvatarObjectEquivalent : null;
                        if (o == null || string.IsNullOrEmpty(o.Id)) continue;
                        if (_outerIds.Add(o.Id)) found++;
                    }
                if (found == 0) Core.Log?.Warning("[clothes] no garment found under " + folder);
            }
            return _outerIds;
        }

        private static Transform _pivot;
        private static Transform _origParent;
        private static int _origSibling;
        private static Camera _menuCam;
        private static Vector3 _origCamPos;
        private static bool _camCaptured;
        private static float _baseDist;
        private static float _focusHeight = 1.0f;   // dolly aim height above the avatar root: chest by default, higher for Face
        private static int _zoomPercent = 100;

        /// <summary>Camera zoom relative to the editor's starting distance (100 = default), for the canvas readout.</summary>
        public static int ZoomPercent => _zoomPercent;

        // The menu avatar's idle animation resets its LOCAL rotation every frame, so rotating the avatar directly
        // snaps back. We insert a pivot parent (whose rotation the animation never touches) and rotate that.
        private static void EnsurePivot()
        {
            if (_pivot != null || _avatar == null) return;
            var at = _avatar.gameObject != null ? _avatar.gameObject.transform : null;
            if (at == null) return;
            _origParent = at.parent;
            _origSibling = at.GetSiblingIndex();
            var pgo = new GameObject("Inkubator_RotPivot");
            _pivot = pgo.transform;
            _pivot.SetParent(_origParent, false);
            _pivot.position = at.position;
            _pivot.rotation = at.rotation;
            at.SetParent(_pivot, true);
        }

        /// <summary>Spin the preview character around its up axis (degrees), persistently (via a pivot parent).</summary>
        public static void RotateAvatar(float deg)
        {
            if (!EnsureAvatar()) return;
            try { EnsurePivot(); if (_pivot != null) _pivot.Rotate(0f, deg, 0f, Space.World); }
            catch (Exception e) { Core.Log?.Warning("[preview] rotate: " + e.Message); }
        }

        /// <summary>The single menu-scene camera. Camera.main is null here (the menu camera is Untagged), so find it
        /// directly and cache it. Verified: moving this camera dollies the rig and the move persists (menu won't reset it).</summary>
        private static Camera FindMenuCamera()
        {
            if (_menuCam != null) return _menuCam;
            if (Camera.main != null) { _menuCam = Camera.main; return _menuCam; }
            try
            {
                var cams = UnityEngine.Object.FindObjectsOfType<Camera>(false);
                if (cams != null)
                    foreach (var c in cams)
                        if (c != null && c.enabled && c.gameObject.activeInHierarchy) { _menuCam = c; break; }
            }
            catch (Exception e) { Core.Log?.Warning("[preview] find camera: " + e.Message); }
            return _menuCam;
        }

        private static void EnsureCamCaptured(Camera cam)
        {
            if (!_camCaptured && cam != null) { _origCamPos = cam.transform.position; _camCaptured = true; }
        }

        /// <summary>Aim the preview higher (Face) or at the chest (everything else), so the framing follows the selected
        /// body part. Translates the camera by the focus delta so the current zoom/shot is kept, just re-centred.</summary>
        public static void SetFocusPlacement(Placement placement)
        {
            float h = placement == Placement.Face ? 1.55f : 1.0f;
            try
            {
                if (!EnsureAvatar()) { _focusHeight = h; return; }
                var cam = FindMenuCamera();
                if (cam != null)
                {
                    EnsureCamCaptured(cam);                                   // capture the rest position before any shift
                    cam.transform.position += Vector3.up * (h - _focusHeight);
                }
                _focusHeight = h;
            }
            catch (Exception e) { Core.Log?.Warning("[preview] focus: " + e.Message); }
        }

        /// <summary>Dolly the menu camera toward / away from the character along its view line (delta &gt; 0 = zoom in).
        /// 100% = the distance when the editor opened; clamped so it can't pass through or fly far off the rig.</summary>
        public static void ZoomCamera(float delta)
        {
            if (!EnsureAvatar()) return;
            try
            {
                var cam = FindMenuCamera();
                if (cam == null || _avatar.gameObject == null) return;
                EnsureCamCaptured(cam);

                Vector3 target = _avatar.gameObject.transform.position + Vector3.up * _focusHeight;
                Vector3 to = target - cam.transform.position;     // camera -> character
                float dist = to.magnitude;
                if (dist < 0.001f) return;
                if (_baseDist <= 0f) _baseDist = dist;

                float newDist = Mathf.Clamp(dist - delta * 0.12f * dist, _baseDist * 0.35f, _baseDist * 1.6f);
                cam.transform.position = target - to.normalized * newDist;   // stay on the same view line, change distance only
                _zoomPercent = Mathf.RoundToInt(_baseDist / Mathf.Max(0.01f, newDist) * 100f);
            }
            catch (Exception e) { Core.Log?.Warning("[preview] zoom: " + e.Message); }
        }

        private static void RestoreRig()
        {
            try
            {
                if (_pivot != null)
                {
                    var at = _avatar != null && _avatar.gameObject != null ? _avatar.gameObject.transform : null;
                    if (at != null && _origParent != null) { at.SetParent(_origParent, true); at.SetSiblingIndex(_origSibling); }
                    UnityEngine.Object.Destroy(_pivot.gameObject);
                }
                if (_menuCam != null && _camCaptured) _menuCam.transform.position = _origCamPos;   // put the shared menu camera back
            }
            catch (Exception e) { Core.Log?.Warning("[preview] restore rig: " + e.Message); }
            _pivot = null; _origParent = null;
            _menuCam = null; _camCaptured = false; _baseDist = 0f; _focusHeight = 1.0f; _zoomPercent = 100;
        }

        /// <summary>Log the avatar objects on the menu character once, so clothing and tattoo ids can be identified.</summary>
        public static void LogLayers()
        {
            try
            {
                AvatarAppearance ap = Appearance();
                if (ap == null) return;
                DumpList("naked", ap._nakedObjects);
                DumpList("worn", ap._wornObjects);
            }
            catch (Exception e) { Core.Log?.Warning("[layers] " + e.Message); }
        }

        private static void DumpList(string tag, Il2CppSystem.Collections.Generic.List<CoreObject> list)
        {
            if (list == null) { Core.Log?.Msg("[layers] " + tag + ": null"); return; }
            for (int i = 0; i < list.Count; i++)
            {
                CoreObject o = list[i];
                if (o == null) { Core.Log?.Msg("[layers] " + tag + " #" + i + ": null"); continue; }
                string line = "[layers] " + tag + " #" + i + ": " + o.Id;
                var layers = o._layers;
                if (layers != null)
                    for (int j = 0; j < layers.Count; j++)
                        if (layers[j] != null) line += " (" + layers[j].Type + " order " + layers[j].LayeringOrder + ")";
                Core.Log?.Msg(line);
            }
        }

        private static void DestroyTexture(Placement placement)
        {
            if (_lastTex.TryGetValue(placement, out Texture2D old) && old != null) UnityEngine.Object.Destroy(old);
            _lastTex.Remove(placement);
        }

        private static void DestroyTextures()
        {
            foreach (var kv in _lastTex) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _lastTex.Clear();
        }
    }
}
