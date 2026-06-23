#if DEBUG
using System;
using System.IO;
using Il2CppScheduleOne.AvatarFramework;
using S1API.Rendering;
using S1API.UI;
using UnityEngine;
using S1Creator = Il2CppScheduleOne.AvatarFramework.Customization.CharacterCreator;

namespace Inkubator.Dev
{
    /// <summary>
    /// DEBUG spike: can the game CharacterCreator be used as the editor host from the menu (its rig has built-in
    /// rotation + a framed camera + a customizable, lightly-clothed character)? Opens it head-less (no creator
    /// UI), reports rig availability, applies a magenta test tattoo to ITS rig, and rotates it. Triggered by a
    /// UserData/Inkubator/.creatortest flag file (automation cannot press keys). Screenshot to judge the result.
    /// </summary>
    internal static class CreatorSpike
    {
        internal static string FlagFile => Path.Combine(Editor.Paths.Root, ".creatortest");

        internal static void Run()
        {
            try
            {
                Core.Log?.Msg("[creator] === CharacterCreator host spike ===");

                bool instExists = false, rigExists = false, open = false;
                try { instExists = S1Creator.Instance != null; } catch (Exception e) { Core.Log?.Warning("[creator] Instance threw: " + e.Message); }
                Core.Log?.Msg("[creator] CharacterCreator.Instance present (before open): " + instExists);

                // Open head-less (no creator UI). Pass null so S1API picks defaults.
                CharacterCreatorManager.Open(null, false);
                try { open = CharacterCreatorManager.IsOpen; } catch { }
                Core.Log?.Msg("[creator] IsOpen after Open(showUI:false): " + open);

                S1Creator cc = null;
                try { cc = S1Creator.Instance; } catch { }
                if (cc == null) { Core.Log?.Warning("[creator] Instance null after open - creator not available in this scene."); return; }

                Avatar rig = null;
                try { rig = cc.Rig; } catch (Exception e) { Core.Log?.Warning("[creator] Rig threw: " + e.Message); }
                rigExists = rig != null;
                Core.Log?.Msg("[creator] Rig present: " + rigExists + ", RigContainer: " + (cc.RigContainer != null) + ", CameraPosition: " + (cc.CameraPosition != null));
                if (!rigExists) return;

                // Apply a magenta left-arm tattoo to the creator rig (does R1 work on this rig too?).
                Texture2D tex = MakeSolid(512, new Color(1f, 0f, 1f, 1f));
                string source = "Avatar/Layers/Tattoos/leftarm/LeftArm_Web";
                string target = "Avatar/Layers/Tattoos/custom/leftarm/inkubator_creator_spike";
                bool reg = AvatarLayerFactory.CreateAndRegisterAvatarLayer(source, target, "Creator Spike", tex);
                Core.Log?.Msg("[creator] layer registered: " + reg);

                AvatarSettings cur = rig.CurrentSettings;
                Core.Log?.Msg("[creator] Rig.CurrentSettings null: " + (cur == null));
                if (cur != null && reg)
                {
                    if (cur.BodyLayerSettings == null)
                        cur.BodyLayerSettings = new Il2CppSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                    var ls = new AvatarSettings.LayerSetting(); ls.layerPath = target; ls.layerTint = Color.white;
                    cur.BodyLayerSettings.Add(ls);
                    rig.LoadAvatarSettings(cur);
                    Core.Log?.Msg("[creator] tattoo applied to creator rig (" + cur.BodyLayerSettings.Count + " body layers).");
                }

                try { cc.SliderChanged(0.15f); Core.Log?.Msg("[creator] SliderChanged(0.15) ok (rotation)."); }
                catch (Exception e) { Core.Log?.Warning("[creator] SliderChanged threw: " + e.Message); }

                Core.Log?.Msg("[creator] === done (screenshot to judge framing/clothing/rotation) ===");
            }
            catch (Exception e) { Core.Log?.Error("[creator] FAILED: " + e); }
            finally { try { if (File.Exists(FlagFile)) File.Delete(FlagFile); } catch { } }
        }

        private static Texture2D MakeSolid(int size, Color c)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt; GL.Clear(true, true, c);
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0); tex.Apply(false);
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset; return tex;
            }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }
    }
}
#endif
