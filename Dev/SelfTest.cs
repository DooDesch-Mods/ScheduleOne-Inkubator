#if DEBUG
using System;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Inkubator.Editor;
using UnityEngine;

namespace Inkubator.Dev
{
    /// <summary>
    /// DEBUG-only end-to-end pipeline test that needs no UI input (so it can be driven via a flag file while the
    /// game is automated). Creates a project with a generated test decal, bakes it, previews it live on the menu
    /// avatar, and exports the complete mod - logging every artifact path so the result can be verified from
    /// screenshots + the produced files. Triggered when UserData/Inkubator/.selftest exists.
    /// </summary>
    internal static class SelfTest
    {
        internal static string FlagFile => Path.Combine(Paths.Root, ".selftest");

        internal static void Run()
        {
            try
            {
                Core.Log?.Msg("[selftest] === Inkubator pipeline self-test ===");
                Paths.EnsureBaseDirs();

                // 1) Generate a recognizable test decal (green disc on transparent) in the Import folder.
                string decalPath = Path.Combine(Paths.Import, "selftest_disc.png");
                WriteDisc(decalPath, 256, new Color32(40, 230, 90, 255));
                Core.Log?.Msg("[selftest] decal: " + decalPath);

                // 2) New project + import the decal as a source.
                Project project = ProjectStore.Create("Inkubator Selftest", "DooDesch");
                string rel = ProjectStore.ImportSource(project, decalPath);
                Core.Log?.Msg("[selftest] imported source: " + rel);

                // 3) Add a tattoo entry placing the decal on the left arm.
                project.Tattoos.Add(new TattooEntry
                {
                    Id = "testdisc",
                    Name = "Test Disc",
                    Placement = "leftarm",
                    Price = 0f,
                    Decals = { new Decal { Source = rel, U = 0.5f, V = 0.55f, Scale = 0.5f, Opacity = 1f } }
                });
                ProjectStore.Save(project);
                Core.Log?.Msg("[selftest] project saved: " + Paths.ProjectFile(project.FolderName));

                // 4) Live preview on the menu avatar.
                bool previewed = Preview.ApplyEntry(project, project.Tattoos[0]);
                Core.Log?.Msg("[selftest] preview applied: " + previewed);

                // 5) Export the complete mod.
                ExportResult ex = Exporter.Export(project);
                Core.Log?.Msg("[selftest] export ok=" + ex.Ok + " tattoos=" + ex.TattoosWritten + " -> " + ex.ExportFolder);
                foreach (string w in ex.Warnings) Core.Log?.Msg("[selftest]   warn: " + w);
                if (ex.Ok)
                {
                    Core.Log?.Msg("[selftest]   pack: " + ex.PackFolder);
                    foreach (string f in Directory.GetFiles(ex.ExportFolder, "*", SearchOption.AllDirectories))
                        Core.Log?.Msg("[selftest]   file: " + f.Substring(ex.ExportFolder.Length + 1));
                }

                Core.Log?.Msg("[selftest] === done ===");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[selftest] FAILED: " + e);
            }
            finally
            {
                try { if (File.Exists(FlagFile)) File.Delete(FlagFile); } catch { }
            }
        }

        // Writes a filled disc (opaque) on a transparent background to a PNG.
        private static void WriteDisc(string path, int size, Color32 color)
        {
            var px = new Color32[size * size];
            float cx = size * 0.5f, cy = size * 0.5f, rad = size * 0.40f;
            Color32 clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    px[y * size + x] = (dx * dx + dy * dy <= rad * rad) ? color : clear;
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(new Il2CppStructArray<Color32>(px));
                tex.Apply(false);
                byte[] png = ImageConversion.EncodeToPNG(tex);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }
    }
}
#endif
