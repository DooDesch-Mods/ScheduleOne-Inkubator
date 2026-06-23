using System.Collections.Generic;
using System.Reflection;
using S1API.Rendering;
using UnityEngine;

namespace Inkubator.Editor
{
    /// <summary>
    /// Loads real (free-to-use) icon PNGs bundled as embedded resources (Inkubator.Assets.Icons.&lt;name&gt;.png) for
    /// tool cursors and UI buttons. Returns null when an icon is missing, so callers fall back to the default
    /// cursor / no icon. Icons are Material Symbols (Apache-2.0).
    /// </summary>
    public static class ToolIcons
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        /// <summary>A downscaled copy of an icon (e.g. for a small cursor). Cached per (name,size).</summary>
        public static Texture2D GetSized(string name, int size)
        {
            string key = name + "@" + size;
            if (_cache.TryGetValue(key, out var c)) return c;
            Texture2D src = Get(name);
            Texture2D outp = src != null ? Downscale(src, size) : null;
            _cache[key] = outp;
            return outp;
        }

        private static Texture2D Downscale(Texture2D src, int size)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                t.Apply(false);
                t.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return t;
            }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }

        public static Texture2D Get(string name)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            Texture2D tex = null;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string res = "Inkubator.Assets.Icons." + name + ".png";
                using var s = asm.GetManifestResourceStream(res);
                if (s != null)
                {
                    byte[] b = new byte[s.Length];
                    s.Read(b, 0, b.Length);
                    tex = TextureUtils.LoadTextureFromBytes(b);
                    if (tex != null) { tex.filterMode = FilterMode.Bilinear; tex.hideFlags = HideFlags.DontUnloadUnusedAsset; }
                }
                Core.Log?.Msg("[icon] '" + name + "' -> " + (tex != null ? (tex.width + "x" + tex.height) : "MISSING (" + res + ")"));
            }
            catch (System.Exception e) { Core.Log?.Warning("[icon] '" + name + "': " + e.Message); tex = null; }
            _cache[name] = tex;
            return tex;
        }
    }
}
