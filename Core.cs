using MelonLoader;
using SideHustle;
using UnityEngine;

[assembly: MelonInfo(typeof(Inkubator.Core), "Inkubator", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Inkubator")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("SideHustle")]

namespace Inkubator
{
    /// <summary>
    /// MelonLoader entry point for Inkubator. Registers itself as a singleplayer, menu-space gamemode with
    /// Side Hustle. Launching it (from the main-menu hub) opens the tattoo editor overlay on top of the live
    /// menu rig - no save is loaded. The editor lets the player import PNGs, place them on the character, preview
    /// live and export a complete, ready-to-release Inkorporated tattoo mod.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            try
            {
                API.Register(new GamemodeDescriptor
                {
                    Id = "doodesch.inkubator",
                    DisplayName = "Inkubator",
                    Description = "Design and export custom tattoo mods.",
                    Author = "DooDesch",
                    Support = GamemodeSupport.Singleplayer,
                    Surface = GamemodeSurface.MenuSpace,
                    OnLaunchSingleplayer = OnLaunch,
                    OnExitToHub = OnExit
                });
                Log.Msg("Inkubator 1.0.0 registered with Side Hustle.");
            }
            catch (System.Exception e)
            {
                Log.Warning("Side Hustle not available, Inkubator cannot register: " + e.Message);
            }
        }

        private static void OnLaunch(LaunchContext ctx) => Editor.EditorUI.Open(ctx);

        private static void OnExit(LaunchContext ctx)
        {
            Editor.EditorUI.Close();
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                Editor.EditorUI.Close();
                Editor.Preview.Forget();
#if DEBUG
                _inMenu = false;
#endif
            }
        }

        public override void OnUpdate()
        {
            // Editor drag handling (runs whenever the overlay is open).
            if (Editor.EditorUI.IsOpen) Editor.EditorUI.Tick();

#if DEBUG
            DebugUpdate();
#endif
        }

#if DEBUG
        private bool _inMenu;
        private float _menuTime;
        private bool _selfTestDone;

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu") { _inMenu = true; _menuTime = 0f; _selfTestDone = false; }
        }

        // A ".selftest" flag runs the headless pipeline test; an ".editortest"/".reviewtest" flag auto-opens the
        // editor a few seconds after the menu loads (so the layout can be screenshotted without clicking).
        private void DebugUpdate()
        {
            if (_inMenu)
            {
                _menuTime += Time.deltaTime;
                if (!_selfTestDone && _menuTime > 3f)
                {
                    _selfTestDone = true;
                    if (System.IO.File.Exists(Dev.SelfTest.FlagFile)) Dev.SelfTest.Run();
                    else if (System.IO.File.Exists(ReviewTestFlag) && !Editor.EditorUI.IsOpen)
                    {
                        string pn = "";
                        try { pn = System.IO.File.ReadAllText(ReviewTestFlag).Trim(); } catch { }
                        Dev.TestAssets.EnsureTestImages();
                        Editor.EditorUI.DebugOpenReview(pn);
                    }
                    else if (System.IO.File.Exists(EditorTestFlag) && !Editor.EditorUI.IsOpen)
                    {
                        string pn = "";
                        try { pn = System.IO.File.ReadAllText(EditorTestFlag).Trim(); } catch { }
                        Dev.TestAssets.EnsureTestImages();
                        Editor.EditorUI.DebugOpen(pn);
                    }
                }
            }
        }

        private static string EditorTestFlag => System.IO.Path.Combine(Editor.Paths.Root, ".editortest");
        private static string ReviewTestFlag => System.IO.Path.Combine(Editor.Paths.Root, ".reviewtest");
#endif
    }
}
