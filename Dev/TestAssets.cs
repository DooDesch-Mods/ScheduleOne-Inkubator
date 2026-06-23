#if DEBUG
using System;
using System.IO;
using Inkubator.Editor;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Inkubator.Dev
{
    /// <summary>
    /// DEBUG-only: writes a WebP + an animated GIF test image into the Import folder using ImageSharp, so the
    /// editor's WebP/GIF support can be verified (encode here, decode in the editor) without hunting for sample
    /// files. Runs once if the files are missing.
    /// </summary>
    internal static class TestAssets
    {
        internal static void EnsureTestImages()
        {
            try
            {
                Paths.EnsureBaseDirs();
                string webp = Path.Combine(Paths.Import, "test_disc.webp");
                string gif = Path.Combine(Paths.Import, "test_anim.gif");

                if (!File.Exists(webp))
                {
                    using var img = MakeDisc(256, new Rgba32(40, 200, 230, 255));
                    img.SaveAsWebp(webp);
                    Core.Log?.Msg("[testassets] wrote " + webp);
                }
                if (!File.Exists(gif))
                {
                    var colors = new[]
                    {
                        new Rgba32(230, 70, 70, 255), new Rgba32(70, 230, 70, 255),
                        new Rgba32(70, 120, 230, 255), new Rgba32(230, 210, 60, 255)
                    };
                    using var anim = MakeDisc(256, colors[0]);
                    SetGifDelay(anim, 0);
                    for (int i = 1; i < colors.Length; i++)
                    {
                        using var f = MakeDisc(256, colors[i]);
                        anim.Frames.AddFrame(f.Frames.RootFrame);
                        SetGifDelay(anim, i);
                    }
                    anim.SaveAsGif(gif);
                    Core.Log?.Msg("[testassets] wrote " + gif + " (" + colors.Length + " frames)");
                }
            }
            catch (Exception e) { Core.Log?.Warning("[testassets] " + e.Message); }
        }

        private static Image<Rgba32> MakeDisc(int size, Rgba32 color)
        {
            var img = new Image<Rgba32>(size, size);
            float cx = size * 0.5f, cy = size * 0.5f, rad = size * 0.40f;
            var clear = new Rgba32(0, 0, 0, 0);
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < size; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - cx, dy = y - cy;
                        row[x] = (dx * dx + dy * dy <= rad * rad) ? color : clear;
                    }
                }
            });
            return img;
        }

        private static void SetGifDelay(Image<Rgba32> img, int frameIndex)
        {
            try { img.Frames[frameIndex].Metadata.GetGifMetadata().FrameDelay = 20; } catch { }
        }
    }
}
#endif
