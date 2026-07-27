using System;
using System.Collections.Generic;
using Il2CppScheduleOne.AvatarFramework;
using UnityEngine;

namespace Inkubator.Editor
{
    /// <summary>
    /// Where each body part actually lives inside the avatar's shared UV atlas.
    ///
    /// The game gives every body mesh the same layer texture, so "chest", "left arm" and "right arm" are not
    /// separate canvases - they are three small islands in one 1024-square atlas, and picking a placement does
    /// nothing to move an image between them. Measured on the shipped assets: chest sits at u 0.28-0.41, the left
    /// arm at u 0.55-0.60, the right arm at u 0.09-0.15, all of them up at v 0.84-0.97. A decal left at the
    /// canvas centre (0.5, 0.5) lands in none of them.
    ///
    /// The regions are derived at runtime from the alpha of the stock tattoos of each part, so they need no
    /// shipped asset and follow the game if it ever moves its UVs. Face is deliberately excluded: its stock
    /// tattoos span almost the whole face atlas, so there is nothing meaningful to frame.
    /// </summary>
    internal static class UvRegions
    {
        // Alpha below this counts as empty when tracing a stock tattoo's extent.
        private const byte AlphaThreshold = 16;
        // Sampling resolution for the readback. The islands are several percent of the atlas wide, so 256 is ample.
        private const int SampleSize = 256;
        // Breathing room around the traced ink so a decal can sit slightly outside the stock art.
        private const float Padding = 0.08f;

        private static readonly Dictionary<Placement, Rect> _regions = new Dictionary<Placement, Rect>();
        private static readonly Dictionary<Placement, Texture> _backdrops = new Dictionary<Placement, Texture>();
        private static readonly Rect Full = new Rect(0f, 0f, 1f, 1f);

        /// <summary>
        /// The part's island in atlas coordinates, or the full atlas when it cannot be measured (Face, or a game
        /// build whose layers do not resolve). Cached for the session.
        /// </summary>
        internal static Rect Region(Placement p)
        {
            if (_regions.TryGetValue(p, out Rect cached)) return cached;
            Rect r = p == Placement.Face ? Full : Measure(p);
            _regions[p] = r;
            if (r != Full)
                Core.Log?.Msg("[uv] " + Placements.Token(p) + " region u=" + r.xMin.ToString("F3") + ".." + r.xMax.ToString("F3") +
                              " v=" + r.yMin.ToString("F3") + ".." + r.yMax.ToString("F3"));
            return r;
        }

        /// <summary>
        /// The stock tattoo shown faintly behind the canvas so the inked area is visible. It is the same layer a
        /// custom tattoo is cloned from, so what the user sees is exactly the reference their art replaces.
        /// </summary>
        internal static Texture Backdrop(Placement p)
        {
            if (_backdrops.TryGetValue(p, out Texture cached)) return cached;
            Texture t = null;
            foreach (string path in Placements.StockLayers(p))
            {
                AvatarLayer lay = Load(path);
                if (lay != null && lay.Texture != null) { t = lay.Texture; break; }
            }
            _backdrops[p] = t;
            return t;
        }

        /// <summary>A decal size in atlas units, given its size relative to the framed canvas.</summary>
        internal static float ToAtlasSize(Rect view, float canvasSize) => canvasSize * (view.width <= 0f ? 1f : view.width);

        /// <summary>Inverse of <see cref="ToAtlasSize"/>: an atlas size as a fraction of the framed canvas.</summary>
        internal static float ToCanvasSize(Rect view, float atlasSize) => view.width <= 0f ? atlasSize : atlasSize / view.width;

        /// <summary>Maps a 0..1 point inside a view rect to full-atlas UV, which is what projects store.</summary>
        internal static Vector2 ToAtlas(Rect view, Vector2 local01) =>
            new Vector2(view.xMin + local01.x * view.width, view.yMin + local01.y * view.height);

        /// <summary>Inverse of <see cref="ToAtlas"/>: full-atlas UV back to 0..1 inside the view rect.</summary>
        internal static Vector2 ToLocal(Rect view, Vector2 atlas) =>
            new Vector2(
                view.width <= 0f ? 0.5f : (atlas.x - view.xMin) / view.width,
                view.height <= 0f ? 0.5f : (atlas.y - view.yMin) / view.height);

        // Union of the alpha bounding boxes of every stock tattoo of the part.
        private static Rect Measure(Placement p)
        {
            float minU = 1f, minV = 1f, maxU = 0f, maxV = 0f;
            bool any = false;
            foreach (string path in Placements.StockLayers(p))
            {
                AvatarLayer lay = Load(path);
                if (lay == null || lay.Texture == null) continue;
                if (!AlphaBounds(lay.Texture, out float a, out float b, out float c, out float d)) continue;
                minU = Mathf.Min(minU, a); minV = Mathf.Min(minV, b);
                maxU = Mathf.Max(maxU, c); maxV = Mathf.Max(maxV, d);
                any = true;
            }
            if (!any)
            {
                Core.Log?.Warning("[uv] no stock layer measurable for " + Placements.Token(p) + ", using the full atlas");
                return Full;
            }

            float pad = Mathf.Max(maxU - minU, maxV - minV) * Padding;
            return Square(Rect.MinMaxRect(minU - pad, minV - pad, maxU + pad, maxV + pad));
        }

        /// <summary>
        /// Grow a rect to a square around its centre, clamped into the atlas. The editor canvas is square, so a
        /// square source rect keeps decal size, aspect and rotation undistorted between canvas and bake.
        /// </summary>
        internal static Rect Square(Rect r)
        {
            float side = Mathf.Min(1f, Mathf.Max(r.width, r.height));
            float cx = Mathf.Clamp(r.center.x, side * 0.5f, 1f - side * 0.5f);
            float cy = Mathf.Clamp(r.center.y, side * 0.5f, 1f - side * 0.5f);
            return new Rect(cx - side * 0.5f, cy - side * 0.5f, side, side);
        }

        // Stock layer textures are not CPU-readable, so trace them through a RenderTexture.
        private static bool AlphaBounds(Texture2D src, out float minU, out float minV, out float maxU, out float maxV)
        {
            minU = minV = 1f; maxU = maxV = 0f;
            RenderTexture rt = RenderTexture.GetTemporary(SampleSize, SampleSize, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, SampleSize, SampleSize), 0, 0);
                copy.Apply(false);

                var px = copy.GetPixels32();
                bool any = false;
                for (int y = 0; y < SampleSize; y++)
                    for (int x = 0; x < SampleSize; x++)
                    {
                        if (px[y * SampleSize + x].a < AlphaThreshold) continue;
                        float u = x / (float)(SampleSize - 1), v = y / (float)(SampleSize - 1);
                        if (u < minU) minU = u;
                        if (v < minV) minV = v;
                        if (u > maxU) maxU = u;
                        if (v > maxV) maxV = v;
                        any = true;
                    }
                return any;
            }
            catch (Exception e) { Core.Log?.Warning("[uv] trace failed: " + e.Message); return false; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }

        private static AvatarLayer Load(string path)
        {
            try
            {
                UnityEngine.Object o = Resources.Load(path);
                return o != null ? o.TryCast<AvatarLayer>() : null;
            }
            catch { return null; }
        }
    }
}
