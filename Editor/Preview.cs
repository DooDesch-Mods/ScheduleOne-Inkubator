using System;
using System.Collections.Generic;
using Il2CppScheduleOne.AvatarFramework;
using S1API.Rendering;
using UnityEngine;
using S1MenuRig = Il2CppScheduleOne.UI.MainMenu.MainMenuRig;

namespace Inkubator.Editor
{
    /// <summary>
    /// Applies baked tattoo textures to the menu avatar live, using the same proven path Inkorporated uses:
    /// register the baked texture as a cloned AvatarLayer at a stable per-placement session Resources path, add
    /// that path to the avatar's CurrentSettings (face layers for Face, body layers otherwise) and reload. Re-baking
    /// overwrites the same path (RuntimeResourceRegistry assigns by path), so preview refreshes without leaking.
    /// </summary>
    public static class Preview
    {
        private static Avatar _avatar;
        private static readonly HashSet<string> _applied = new HashSet<string>();
        // Previously baked texture per placement path, destroyed on re-bake so live preview does not leak textures.
        private static readonly Dictionary<string, Texture2D> _lastTex = new Dictionary<string, Texture2D>();

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

        /// <summary>Bake one tattoo entry and show it on the avatar. Returns false on failure.</summary>
        public static bool ApplyEntry(Project project, TattooEntry entry)
        {
            try
            {
                if (!EnsureAvatar()) { Core.Log?.Warning("[preview] no menu avatar"); return false; }
                if (entry == null) return false;

                Placement placement = entry.PlacementEnum;
                Texture2D baked = Baker.Bake(project, entry);
                if (baked == null) return false;

                string path = Placements.SessionTargetPath(placement);
                string source = Placements.SourceLayer(placement);
                bool ok = AvatarLayerFactory.CreateAndRegisterAvatarLayer(source, path, "Inkubator " + entry.Id, baked);
                if (!ok) { UnityEngine.Object.Destroy(baked); Core.Log?.Warning("[preview] register failed for " + path); return false; }

                // Free the previous bake for this placement (the new layer replaced it in the registry).
                if (_lastTex.TryGetValue(path, out var old) && old != null && old != baked) UnityEngine.Object.Destroy(old);
                _lastTex[path] = baked;

                AvatarSettings cur = _avatar.CurrentSettings;
                if (cur == null) { Core.Log?.Warning("[preview] avatar CurrentSettings null"); return false; }

                AddPathOnce(cur, path, placement == Placement.Face);
                _applied.Add(path);
                _avatar.LoadAvatarSettings(cur);
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[preview] apply: " + e.Message); return false; }
        }

        /// <summary>
        /// Show the given decals (the composite of all visible tattoos of a placement) on the avatar, or remove the
        /// placement's layer when the list is empty. One session layer per placement.
        /// </summary>
        public static bool ApplyPlacement(Project project, Placement placement, List<Decal> decals)
        {
            try
            {
                if (!EnsureAvatar()) return false;
                AvatarSettings cur = _avatar.CurrentSettings;
                if (cur == null) return false;

                string path = Placements.SessionTargetPath(placement);
                bool face = placement == Placement.Face;

                if (decals == null || decals.Count == 0)
                {
                    RemovePaths(cur.BodyLayerSettings, new HashSet<string> { path });
                    RemovePaths(cur.FaceLayerSettings, new HashSet<string> { path });
                    _applied.Remove(path);
                    if (_lastTex.TryGetValue(path, out var old) && old != null) UnityEngine.Object.Destroy(old);
                    _lastTex.Remove(path);
                    _avatar.LoadAvatarSettings(cur);
                    return true;
                }

                Texture2D baked = Baker.Bake(project, decals);
                if (baked == null) return false;
                string source = Placements.SourceLayer(placement);
                bool ok = AvatarLayerFactory.CreateAndRegisterAvatarLayer(source, path, "Inkubator " + Placements.Token(placement), baked);
                if (!ok) { UnityEngine.Object.Destroy(baked); return false; }

                if (_lastTex.TryGetValue(path, out var prev) && prev != null && prev != baked) UnityEngine.Object.Destroy(prev);
                _lastTex[path] = baked;
                AddPathOnce(cur, path, face);
                _applied.Add(path);
                _avatar.LoadAvatarSettings(cur);
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[preview] applyplacement: " + e.Message); return false; }
        }

        /// <summary>
        /// Hard reset: strip EVERY Inkubator layer (any path containing "inkubator") from the avatar so leftover/
        /// orphaned preview layers from earlier sessions or test runs disappear. The editor calls this before
        /// re-applying the current project, so the character shows exactly the project's tattoos and nothing else.
        /// </summary>
        public static void ResetSessionLayers()
        {
            try
            {
                if (!EnsureAvatar()) return;
                var cur = _avatar.CurrentSettings;
                if (cur == null) return;
                RemoveByContains(cur.BodyLayerSettings, "inkubator");
                RemoveByContains(cur.FaceLayerSettings, "inkubator");
                _applied.Clear();
                DestroyTextures();
                _avatar.LoadAvatarSettings(cur);
            }
            catch (Exception e) { Core.Log?.Warning("[preview] reset: " + e.Message); }
        }

        private static void RemoveByContains(Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting> list, string substr)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var it = list[i];
                if (it != null && it.layerPath != null && it.layerPath.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0)
                    list.RemoveAt(i);
            }
        }

        /// <summary>Remove all Inkubator session layers from the avatar and reload (clean it up).</summary>
        public static void Clear()
        {
            try
            {
                if (_avatar == null || _avatar.CurrentSettings == null) { _applied.Clear(); return; }
                var cur = _avatar.CurrentSettings;
                RemovePaths(cur.BodyLayerSettings, _applied);
                RemovePaths(cur.FaceLayerSettings, _applied);
                _avatar.LoadAvatarSettings(cur);
            }
            catch (Exception e) { Core.Log?.Warning("[preview] clear: " + e.Message); }
            finally { _applied.Clear(); DestroyTextures(); }
        }

        /// <summary>Return the menu character to normal (clothes back, tattoos off, un-rotated, un-zoomed) on editor close.</summary>
        public static void ExitEditor()
        {
            try { SetStripUnderwear(false); } catch { }
            try { SetStripClothing(false); } catch { }
            Clear();
            RestoreRig();
        }

        /// <summary>Drop the cached avatar (call when leaving the menu scene).</summary>
        public static void Forget()
        {
            RestoreRig(); _avatar = null; _applied.Clear(); DestroyTextures();
            _outerStripped = false; _savedOuter.Clear();
            _underwearStripped = false; _savedUnderwear.Clear();
        }

        // Two independent clothing toggles: the outer garments (T-Shirt / Jeans) and the underwear, each with its
        // own saved-layer list so they can be shown/hidden separately while placing tattoos.
        private static bool _outerStripped;
        private static readonly List<(string path, Color tint)> _savedOuter = new List<(string, Color)>();
        private static bool _underwearStripped;
        private static readonly List<(string path, Color tint)> _savedUnderwear = new List<(string, Color)>();

        public static bool IsClothingStripped => _outerStripped;
        public static bool IsUnderwearStripped => _underwearStripped;

        /// <summary>
        /// Show or hide the character's outer clothes (shirt / pants) so body tattoos under them are visible.
        /// The Nipples censor and the underwear are kept (the underwear has its own toggle). Returns the new state.
        /// </summary>
        public static bool SetStripClothing(bool strip) => StripMatching(strip, ref _outerStripped, _savedOuter, IsOuterClothing, "clothes");

        /// <summary>Show or hide just the underwear (independent of the outer clothes). Returns the new state.</summary>
        public static bool SetStripUnderwear(bool strip) => StripMatching(strip, ref _underwearStripped, _savedUnderwear, IsUnderwear, "underwear");

        // Strip (or restore) every body layer matching a predicate, remembering exactly the removed layers (path+tint)
        // so the restore puts them back without nuking the whole list (which would also wipe live tattoo layers).
        private static bool StripMatching(bool strip, ref bool state, List<(string path, Color tint)> saved, Func<string, bool> match, string tag)
        {
            try
            {
                if (!EnsureAvatar()) return state;
                var cur = _avatar.CurrentSettings;
                if (cur == null || cur.BodyLayerSettings == null) return state;
                var list = cur.BodyLayerSettings;

                if (strip && !state)
                {
                    saved.Clear();
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var it = list[i]; if (it == null) continue;
                        if (match(it.layerPath)) { Core.Log?.Msg("[" + tag + "] STRIP " + it.layerPath); saved.Add((it.layerPath, it.layerTint)); list.RemoveAt(i); }
                    }
                    state = true;
                    _avatar.LoadAvatarSettings(cur);
                }
                else if (!strip && state)
                {
                    foreach (var s in saved)
                    {
                        bool present = false;
                        for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].layerPath == s.path) { present = true; break; }
                        if (!present) { var ls = new AvatarSettings.LayerSetting(); ls.layerPath = s.path; ls.layerTint = s.tint; list.Add(ls); }
                    }
                    saved.Clear();
                    state = false;
                    _avatar.LoadAvatarSettings(cur);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[preview] strip " + tag + ": " + e.Message); }
            return state;
        }

        // Outer clothing = Top/Bottom garments, but NOT the Nipples censor and NOT the underwear (those are kept
        // or have their own toggle). Tattoos are never clothing.
        private static bool IsOuterClothing(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (p.IndexOf("Tattoo", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("inkubator", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (p.IndexOf("Nipple", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (p.IndexOf("Underwear", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return p.IndexOf("/Top/", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("/Bottom/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUnderwear(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (p.IndexOf("Tattoo", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("inkubator", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return p.IndexOf("Underwear", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Transform _pivot;
        private static Transform _origParent;
        private static int _origSibling;
        private static Camera _cam;
        private static Vector3 _origCamPos;
        private static bool _camCaptured;

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

        /// <summary>Dolly the menu camera toward / away from the character (delta &gt; 0 = zoom in).</summary>
        public static void ZoomCamera(float delta)
        {
            if (!EnsureAvatar()) return;
            try
            {
                if (_cam == null) _cam = Camera.main;
                if (_cam == null) return;
                if (!_camCaptured) { _origCamPos = _cam.transform.position; _camCaptured = true; }
                Vector3 target = _avatar.gameObject.transform.position + Vector3.up * 1.0f;
                Vector3 to = target - _cam.transform.position;
                float dist = to.magnitude;
                float step = delta * 0.15f * Mathf.Max(0.5f, dist);
                if (dist - step > 0.6f) _cam.transform.position += to.normalized * step;
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
            }
            catch { }
            _pivot = null; _origParent = null;
            try { if (_cam != null && _camCaptured) _cam.transform.position = _origCamPos; } catch { }
            _cam = null; _camCaptured = false;
        }

        /// <summary>Log the avatar's body / face / accessory layer paths once, so clothing layers can be identified.</summary>
        public static void LogLayers()
        {
            try
            {
                if (!EnsureAvatar()) { Core.Log?.Msg("[layers] no avatar"); return; }
                var cur = _avatar.CurrentSettings;
                if (cur == null) { Core.Log?.Msg("[layers] CurrentSettings null"); return; }
                DumpList("body", cur.BodyLayerSettings);
                DumpList("face", cur.FaceLayerSettings);
                if (cur.AccessorySettings != null)
                {
                    for (int i = 0; i < cur.AccessorySettings.Count; i++)
                    {
                        var a = cur.AccessorySettings[i];
                        Core.Log?.Msg("[layers] accessory #" + i + ": " + (a != null ? a.path : "null"));
                    }
                }
            }
            catch (Exception e) { Core.Log?.Warning("[layers] " + e.Message); }
        }

        private static void DumpList(string tag, Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting> list)
        {
            if (list == null) { Core.Log?.Msg("[layers] " + tag + ": null"); return; }
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                Core.Log?.Msg("[layers] " + tag + " #" + i + ": " + (it != null ? it.layerPath : "null"));
            }
        }

        private static void DestroyTextures()
        {
            foreach (var kv in _lastTex) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _lastTex.Clear();
        }

        private static void AddPathOnce(AvatarSettings cur, string path, bool face)
        {
            var list = face ? cur.FaceLayerSettings : cur.BodyLayerSettings;
            if (list == null)
            {
                list = new Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (face) cur.FaceLayerSettings = list; else cur.BodyLayerSettings = list;
            }
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].layerPath == path) return; // already present

            var ls = new AvatarSettings.LayerSetting();
            ls.layerPath = path;
            ls.layerTint = Color.white;
            list.Add(ls);
        }

        private static void RemovePaths(Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting> list, HashSet<string> paths)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var it = list[i];
                if (it != null && paths.Contains(it.layerPath)) list.RemoveAt(i);
            }
        }
    }
}
