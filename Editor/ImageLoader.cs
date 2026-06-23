using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using S1API.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace Inkubator.Editor
{
    /// <summary>
    /// Loads tattoo source images into Unity Texture2D. PNG/JPG go through Unity's fast ImageConversion path;
    /// WebP and GIF are decoded with SixLabors.ImageSharp (pure-managed, loads in MelonLoader's runtime) and the
    /// rows are flipped (ImageSharp is top-left origin, Unity textures are bottom-left). One decoder covers all
    /// supported formats so the editor, baker and exporter accept the same set.
    /// </summary>
    public static class ImageLoader
    {
        private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".webp" };

        public static bool IsSupported(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(Exts, ext) >= 0;
        }

        public static bool IsAnimated(string path) =>
            !string.IsNullOrEmpty(path) && Path.GetExtension(path).ToLowerInvariant() == ".gif";

        /// <summary>All supported images in the Import folder, sorted.</summary>
        public static string[] ListImportImages()
        {
            try
            {
                if (!Directory.Exists(Paths.Import)) return Array.Empty<string>();
                var list = new List<string>();
                foreach (string f in Directory.GetFiles(Paths.Import))
                    if (IsSupported(f)) list.Add(f);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Load any supported image as a Texture2D (GIF/WebP via ImageSharp; first frame for GIF).</summary>
        public static Texture2D LoadTexture(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                {
                    Texture2D t = TextureUtils.LoadTextureFromFile(path);
                    if (t != null) return t;
                    // fall through to ImageSharp if Unity failed
                }
                return FromImageSharp(path);
            }
            catch (Exception e) { Core.Log?.Warning("[image] load '" + path + "': " + e.Message); return null; }
        }

        /// <summary>Decode every frame of an animated image (GIF), plus per-frame delays in milliseconds.</summary>
        public static bool LoadFrames(string path, out List<Texture2D> frames, out List<int> delaysMs)
        {
            frames = new List<Texture2D>(); delaysMs = new List<int>();
            try
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(path);
                int w = image.Width, h = image.Height;
                for (int fi = 0; fi < image.Frames.Count; fi++)
                {
                    using Image<Rgba32> frame = image.Frames.CloneFrame(fi);
                    frames.Add(ToTexture(frame, w, h));
                    int delay = 100;
                    try { delay = Math.Max(20, image.Frames[fi].Metadata.GetGifMetadata().FrameDelay * 10); } catch { }
                    delaysMs.Add(delay);
                }
                return frames.Count > 0;
            }
            catch (Exception e) { Core.Log?.Warning("[image] frames '" + path + "': " + e.Message); return false; }
        }

        private static Texture2D FromImageSharp(string path)
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(path);
            return ToTexture(image, image.Width, image.Height);
        }

        private static Texture2D ToTexture(Image<Rgba32> image, int w, int h)
        {
            var px = new Color32[w * h];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    int dy = (h - 1 - y) * w; // flip vertically for Unity
                    for (int x = 0; x < w; x++)
                    {
                        Rgba32 p = row[x];
                        px[dy + x] = new Color32(p.R, p.G, p.B, p.A);
                    }
                }
            });
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(new Il2CppStructArray<Color32>(px));
            tex.Apply(false);
            return tex;
        }
    }
}
