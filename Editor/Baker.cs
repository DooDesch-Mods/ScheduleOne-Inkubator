using System;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using S1API.Rendering;
using UnityEngine;

namespace Inkubator.Editor
{
    /// <summary>
    /// Flattens a tattoo's decals into one full-UV RGBA32 texture for its placement. Each decal is composited by
    /// inverse-transform sampling (position/scale/rotation/opacity/flip/tint) over its rotated bounding box, so
    /// the result is the exact texture both the live preview and the exported pack consume. Bake is on-demand
    /// (button / export), never per frame. CPU compositing keeps it simple and IL2CPP-safe; only the relevant
    /// bounding boxes are touched so cost stays low.
    /// </summary>
    public static class Baker
    {
        public const int CanvasSize = 1024;

        /// <summary>Bake a tattoo entry to an in-memory Texture2D (transparent where no decal covers).</summary>
        public static Texture2D Bake(Project project, TattooEntry entry, int size = CanvasSize)
            => Bake(project, entry?.Decals, size);

        /// <summary>Bake an arbitrary list of decals into one full-UV texture (used to composite several tattoos).</summary>
        public static Texture2D Bake(Project project, System.Collections.Generic.IList<Decal> decalList, int size = CanvasSize)
        {
            int w = size, h = size;
            var canvas = new Color32[w * h]; // transparent (0,0,0,0)

            var decals = new System.Collections.Generic.List<Decal>(decalList ?? new System.Collections.Generic.List<Decal>());
            decals.Sort((a, b) => a.Order.CompareTo(b.Order));

            foreach (Decal d in decals)
            {
                string abs = ProjectStore.ResolveSource(project, d);
                if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) { Core.Log?.Warning("[bake] missing source: " + d.Source); continue; }
                if (!TryReadPixels(abs, out Color32[] dpx, out int dw, out int dh)) { Core.Log?.Warning("[bake] unreadable: " + d.Source); continue; }
                Composite(canvas, w, h, dpx, dw, dh, d);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(new Il2CppStructArray<Color32>(canvas));
            tex.Apply(false);
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return tex;
        }

        /// <summary>Bake and write a PNG to <paramref name="pngPath"/>. Returns true on success.</summary>
        public static bool BakeToFile(Project project, TattooEntry entry, string pngPath, int size = CanvasSize)
        {
            Texture2D tex = null;
            try
            {
                tex = Bake(project, entry, size);
                byte[] png = ImageConversion.EncodeToPNG(tex);
                if (png == null || png.Length == 0) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
                File.WriteAllBytes(pngPath, png);
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[bake] write failed: " + e.Message); return false; }
            finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
        }

        // --- compositing ---

        private static void Composite(Color32[] canvas, int w, int h, Color32[] dpx, int dw, int dh, Decal d)
        {
            // Decal display size in canvas pixels (preserve the decal's own aspect ratio).
            float dispW = Mathf.Max(1f, d.Scale * w);
            float dispH = dispW * (dh / (float)dw);
            float cx = d.U * w;
            float cy = d.V * h;

            float rad = d.RotationDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

            // Axis-aligned bounding box of the rotated display rect, clamped to the canvas.
            float hw = dispW * 0.5f, hh = dispH * 0.5f;
            float ext = Mathf.Abs(hw * cos) + Mathf.Abs(hh * sin);
            float eyt = Mathf.Abs(hw * sin) + Mathf.Abs(hh * cos);
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - ext));
            int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(cx + ext));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - eyt));
            int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(cy + eyt));

            Color32 tint = ParseHex(d.Tint);
            float opacity = Mathf.Clamp01(d.Opacity);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    // canvas px -> decal-local (un-rotate), then to decal UV.
                    float px = x - cx, py = y - cy;
                    float rx = px * cos + py * sin;
                    float ry = -px * sin + py * cos;
                    float su = rx / dispW + 0.5f;
                    float sv = ry / dispH + 0.5f;
                    if (su < 0f || su > 1f || sv < 0f || sv > 1f) continue;
                    if (d.FlipX) su = 1f - su;
                    if (d.FlipY) sv = 1f - sv;

                    int sx = Mathf.Clamp((int)(su * dw), 0, dw - 1);
                    int sy = Mathf.Clamp((int)(sv * dh), 0, dh - 1);
                    Color32 s = dpx[sy * dw + sx];

                    float sa = (s.a / 255f) * opacity;
                    if (sa <= 0f) continue;

                    int ci = y * w + x;
                    Color32 dst = canvas[ci];
                    float da = dst.a / 255f;
                    float outA = sa + da * (1f - sa);
                    if (outA <= 0f) { canvas[ci] = new Color32(0, 0, 0, 0); continue; }

                    float srcR = s.r * (tint.r / 255f);
                    float srcG = s.g * (tint.g / 255f);
                    float srcB = s.b * (tint.b / 255f);

                    byte r = (byte)Mathf.Clamp((srcR * sa + dst.r * da * (1f - sa)) / outA, 0f, 255f);
                    byte g = (byte)Mathf.Clamp((srcG * sa + dst.g * da * (1f - sa)) / outA, 0f, 255f);
                    byte b = (byte)Mathf.Clamp((srcB * sa + dst.b * da * (1f - sa)) / outA, 0f, 255f);
                    canvas[ci] = new Color32(r, g, b, (byte)Mathf.Clamp(outA * 255f, 0f, 255f));
                }
            }
        }

        // --- pixel IO ---

        /// <summary>Load a PNG and return its pixels as a managed Color32[] plus dimensions (via a GPU readback so
        /// non-readable textures still work).</summary>
        private static bool TryReadPixels(string absPath, out Color32[] pixels, out int w, out int h)
        {
            pixels = null; w = 0; h = 0;
            Texture2D src = null, readable = null;
            RenderTexture rt = null, prev = RenderTexture.active;
            try
            {
                src = ImageLoader.LoadTexture(absPath);
                if (src == null) return false;
                w = src.width; h = src.height;

                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply(false);

                Il2CppStructArray<Color32> il = readable.GetPixels32();
                int n = il.Length;
                pixels = new Color32[n];
                for (int i = 0; i < n; i++) pixels[i] = il[i];
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[bake] read '" + absPath + "': " + e.Message); return false; }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.Destroy(readable);
                if (src != null) UnityEngine.Object.Destroy(src);
            }
        }

        private static Color32 ParseHex(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return new Color32(255, 255, 255, 255);
                hex = hex.TrimStart('#');
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                byte a = hex.Length >= 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
                return new Color32(r, g, b, a);
            }
            catch { return new Color32(255, 255, 255, 255); }
        }
    }
}
