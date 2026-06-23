#if DEBUG
using System;
using Il2CppScheduleOne.AvatarFramework;
using S1API.Rendering;
using S1API.UI;
using SideHustle;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using S1Av = Il2CppScheduleOne.AvatarFramework;
using S1MenuRig = Il2CppScheduleOne.UI.MainMenu.MainMenuRig;

namespace Inkubator.Dev
{
    /// <summary>
    /// DEBUG-only feasibility spike for risk R1: can a custom AvatarLayer (registered through the same S1API
    /// AvatarLayerFactory + RuntimeResourceRegistry path Inkorporated uses) be applied to and rendered on the
    /// MAIN-MENU avatar rig, without loading any save? We put a bright magenta tattoo on the menu avatar's bare
    /// left forearm (a body layer, visible since the menu avatar wears short sleeves) by appending it to the
    /// avatar's CurrentSettings and re-loading. If the arm turns magenta, R1 passes and the menu-overlay editor
    /// design is viable. Excluded from Release (csproj Compile Remove Dev/**).
    /// </summary>
    internal static class R1Spike
    {
        private const string SourceLayer = "Avatar/Layers/Tattoos/leftarm/LeftArm_Web";
        private const string TargetLayer = "Avatar/Layers/Tattoos/custom/leftarm/inkubator_spike";

        private static GameObject _overlay;
        private static S1Av.Avatar _avatar;

        /// <summary>Run the spike and show a small overlay; Back returns to the hub.</summary>
        internal static void Launch(LaunchContext ctx)
        {
            (bool ok, string msg) = Apply();
            Core.Log?.Msg(ok ? "[R1] PASS - " + msg : "[R1] FAIL - " + msg);
            ShowOverlay((ok ? "R1 PASS: " : "R1 FAIL: ") + msg, () => ctx.ReturnToHub());
        }

        /// <summary>Apply a magenta custom tattoo layer to the menu avatar's left arm. Returns success + detail.</summary>
        internal static (bool ok, string msg) Apply()
        {
            try
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<S1MenuRig> rigs =
                    UnityEngine.Object.FindObjectsOfType<S1MenuRig>(true);
                if (rigs == null || rigs.Length == 0) return (false, "no MainMenuRig in scene");

                S1Av.Avatar avatar = rigs[0].Avatar;
                if (avatar == null) return (false, "MainMenuRig has no Avatar");
                _avatar = avatar;

                Texture2D tex = MakeSolid(512, new Color(1f, 0f, 1f, 1f));
                if (tex == null) return (false, "texture readback failed");

                bool reg = AvatarLayerFactory.CreateAndRegisterAvatarLayer(SourceLayer, TargetLayer, "Inkubator Spike", tex);
                if (!reg) return (false, "CreateAndRegisterAvatarLayer failed (source " + SourceLayer + ")");

                AvatarSettings cur = avatar.CurrentSettings;
                if (cur == null) return (false, "avatar.CurrentSettings is null");

                if (cur.BodyLayerSettings == null)
                    cur.BodyLayerSettings = new Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();

                var ls = new AvatarSettings.LayerSetting();
                ls.layerPath = TargetLayer;
                ls.layerTint = Color.white;
                cur.BodyLayerSettings.Add(ls);

                avatar.LoadAvatarSettings(cur);
                return (true, "magenta left-arm tattoo applied via custom layer (" + cur.BodyLayerSettings.Count + " body layers)");
            }
            catch (Exception e)
            {
                return (false, e.Message);
            }
        }

        /// <summary>Remove the spike tattoo (so the menu avatar is clean again) and destroy the overlay.</summary>
        internal static void Close()
        {
            try
            {
                if (_avatar != null && _avatar.CurrentSettings != null && _avatar.CurrentSettings.BodyLayerSettings != null)
                {
                    var list = _avatar.CurrentSettings.BodyLayerSettings;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var item = list[i];
                        if (item != null && item.layerPath == TargetLayer) list.RemoveAt(i);
                    }
                    _avatar.LoadAvatarSettings(_avatar.CurrentSettings);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[R1] restore failed: " + e.Message); }
            finally
            {
                _avatar = null;
                if (_overlay != null) { UnityEngine.Object.Destroy(_overlay); _overlay = null; }
            }
        }

        private static void ShowOverlay(string status, Action onBack)
        {
            if (_overlay != null) UnityEngine.Object.Destroy(_overlay);
            _overlay = new GameObject("Inkubator_SpikeOverlay");
            UnityEngine.Object.DontDestroyOnLoad(_overlay);
            var canvas = _overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30050;
            _overlay.AddComponent<GraphicRaycaster>();

            // Top band only, so the 3D avatar stays visible behind it.
            var panel = UIFactory.Panel("Panel", _overlay.transform, new Color(0.06f, 0.06f, 0.09f, 0.95f),
                new Vector2(0.15f, 0.85f), new Vector2(0.85f, 0.99f));
            UIFactory.Text("T", "Inkubator - R1 feasibility spike\n" + status, panel.transform, 18,
                TextAnchor.MiddleCenter, FontStyle.Bold);

            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("Back", "Back to hub", _overlay.transform,
                new Color(0.30f, 0.30f, 0.34f, 1f), 200f, 50f);
            var rt = backGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 60f);
            backBtn.onClick.AddListener((UnityAction)(() => onBack?.Invoke()));
        }

        // GPU-side solid fill -> readable Texture2D (avoids managed-array marshalling; same readback as the
        // Inkorporated TemplateDumper, which is proven to work on IL2CPP).
        private static Texture2D MakeSolid(int size, Color c)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, c);
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply(false);
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return tex;
            }
            catch (Exception e) { Core.Log?.Warning("[R1] MakeSolid: " + e.Message); return null; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
#endif
