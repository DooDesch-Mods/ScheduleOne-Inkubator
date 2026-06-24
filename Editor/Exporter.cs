using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace Inkubator.Editor
{
    /// <summary>Result of an export: the folder produced plus any warnings the user should act on.</summary>
    public sealed class ExportResult
    {
        public bool Ok;
        public string ExportFolder;
        public string PackFolder;
        public readonly List<string> Warnings = new List<string>();
        public int TattoosWritten;
    }

    /// <summary>
    /// Turns a project into a complete, ready-to-release mod. In-game DLL compilation is not possible, so the
    /// output is the Inkorporated DATA-PACK approach (which is sufficient - Inkorporated discovers and renders
    /// packs from UserData/Inkorporated/Packs). The export tree is a Thunderstore/Nexus-ready package:
    ///
    ///   Exports/&lt;name&gt;/
    ///     manifest.json                         (Thunderstore manifest, depends on Inkorporated)
    ///     icon.png                              (256x256)
    ///     README.md  CHANGELOG.md  LICENSE
    ///     Inkorporated/Packs/&lt;name&gt;/
    ///       manifest.json                       (Inkorporated pack manifest)
    ///       &lt;id&gt;.png ...                         (baked placement textures)
    /// </summary>
    public static class Exporter
    {
        // Thunderstore namespace + Inkorporated package id used for the dependency string. The Inkorporated
        // version is read from its MelonInfo at runtime when available.
        private const string ThunderstoreNamespace = "DooDesch";
        private const string InkorporatedPackage = "Inkorporated";
        private const string InkorporatedFallbackVersion = "1.1.0";

        public static ExportResult Export(Project project)
        {
            var result = new ExportResult();
            try
            {
                if (project == null || project.Tattoos == null || project.Tattoos.Count == 0)
                {
                    result.Warnings.Add("Project has no tattoos to export.");
                    return result;
                }

                string folder = Paths.Sanitize(project.Name);
                string exportDir = Paths.ExportDir(folder);
                string packDir = Path.Combine(exportDir, "Inkorporated", "Packs", folder);

                // Fresh export each time.
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
                Directory.CreateDirectory(packDir);

                // 1) Bake each tattoo into the pack as <id>.png + collect pack-manifest entries.
                var manifestTattoos = new List<PackManifestEntry>();
                var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TattooEntry entry in project.Tattoos)
                {
                    if (entry.Decals == null || entry.Decals.Count == 0) continue;   // empties are not shop entries (never claim an id or ship a blank PNG)
                    string id = ShopId(project, entry);
                    if (!usedIds.Add(id)) { result.Warnings.Add("Duplicate tattoo id '" + id + "' skipped."); continue; }

                    if (!Placements.TryParse(entry.Placement, out Placement placement))
                    {
                        result.Warnings.Add("Tattoo '" + id + "' has invalid placement '" + entry.Placement + "', defaulting to chest.");
                        placement = Placement.Chest;
                    }

                    string file = id + ".png";
                    if (!Baker.BakeToFile(project, entry, Path.Combine(packDir, file)))
                    {
                        result.Warnings.Add("Failed to bake tattoo '" + id + "'.");
                        continue;
                    }

                    manifestTattoos.Add(new PackManifestEntry
                    {
                        id = id,
                        name = string.IsNullOrWhiteSpace(entry.Name) ? id : entry.Name,
                        placement = Placements.Token(placement),
                        file = file,
                        price = entry.Price < 0 ? 0 : entry.Price
                    });
                    result.TattoosWritten++;
                }

                if (manifestTattoos.Count == 0)
                {
                    result.Warnings.Add("No tattoos baked; export aborted.");
                    return result;
                }

                // 2) Inkorporated pack manifest.
                var packManifest = new PackManifest
                {
                    name = project.Name,
                    author = string.IsNullOrWhiteSpace(project.Author) ? "Unknown" : project.Author,
                    tattoos = manifestTattoos
                };
                File.WriteAllText(Path.Combine(packDir, "manifest.json"),
                    JsonConvert.SerializeObject(packManifest, Formatting.Indented));

                // 3) Thunderstore manifest (declares the Inkorporated dependency).
                string tsName = ToThunderstoreName(project.Name);
                string version = NormalizeVersion(project.ModVersion);
                var tsManifest = new ThunderstoreManifest
                {
                    name = tsName,
                    version_number = version,
                    website_url = (project.WebsiteUrl ?? "").Trim(),
                    description = Truncate(string.IsNullOrWhiteSpace(project.Description)
                        ? "Custom tattoos for Schedule I (requires Inkorporated)." : project.Description, 250),
                    dependencies = new List<string> { InkorporatedDependency() }
                };
                File.WriteAllText(Path.Combine(exportDir, "manifest.json"),
                    JsonConvert.SerializeObject(tsManifest, Formatting.Indented));

                // 4) README / CHANGELOG / LICENSE / icon.
                File.WriteAllText(Path.Combine(exportDir, "README.md"), BuildReadme(project, folder));
                File.WriteAllText(Path.Combine(exportDir, "CHANGELOG.md"),
                    "# Changelog\n\n## " + version + "\n\n- Initial release.\n");
                File.WriteAllText(Path.Combine(exportDir, "LICENSE"), BuildLicense(project));

                WriteIcon(project, Path.Combine(exportDir, "icon.png"), result);

                result.ExportFolder = exportDir;
                result.PackFolder = packDir;
                result.Ok = true;
                Core.Log?.Msg($"[export] '{project.Name}' -> {exportDir} ({result.TattoosWritten} tattoo(s))");
                return result;
            }
            catch (Exception e)
            {
                result.Warnings.Add("Export error: " + e.Message);
                Core.Log?.Warning("[export] " + e);
                return result;
            }
        }

        public enum Severity { Error, Warning, Info }

        /// <summary>
        /// Pre-flight checks for the review screen. Errors block export (mirroring the cases Export aborts on);
        /// warnings/infos surface the things Export used to skip silently (missing author, duplicate ids, over-long
        /// description, schemeless URL, placeholder icon) so the user can fix them before publishing.
        /// </summary>
        public static List<(Severity, string)> Validate(Project p)
        {
            var issues = new List<(Severity, string)>();
            if (p == null) { issues.Add((Severity.Error, "No project loaded.")); return issues; }

            if (string.IsNullOrWhiteSpace(p.Name))
                issues.Add((Severity.Error, "Pack name is empty."));

            int withDecals = 0;
            if (p.Tattoos != null) foreach (var t in p.Tattoos) if (t.Decals != null && t.Decals.Count > 0) withDecals++;
            if (withDecals == 0)
                issues.Add((Severity.Error, "No tattoo has an image yet - add at least one image to a tattoo."));

            if (string.IsNullOrWhiteSpace(p.Author))
                issues.Add((Severity.Warning, "No author set - the pack and LICENSE will say 'Unknown'."));

            // Duplicate derived ids would be silently skipped by Export (only the first ships).
            if (p.Tattoos != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in p.Tattoos)
                {
                    if (t.Decals == null || t.Decals.Count == 0) continue;
                    string id = ShopId(p, t);
                    if (!seen.Add(id))
                        issues.Add((Severity.Warning, "Duplicate shop id '" + id + "' - two tattoos share the same name; rename one."));
                }
            }

            if (!string.IsNullOrWhiteSpace(p.Description) && p.Description.Trim().Length > 250)
                issues.Add((Severity.Warning, "Description is " + p.Description.Trim().Length + " chars - Thunderstore will cut it to 250."));

            // Free (price 0) is valid, but surface it so a forgotten price is not shipped silently.
            if (p.Tattoos != null)
            {
                int free = 0;
                foreach (var t in p.Tattoos) if (t.Decals != null && t.Decals.Count > 0 && t.Price <= 0f) free++;
                if (free > 0)
                    issues.Add((Severity.Info, free + (free == 1 ? " tattoo is" : " tattoos are") + " free (price 0)."));
            }

            if (!string.IsNullOrWhiteSpace(p.WebsiteUrl))
            {
                string u = p.WebsiteUrl.Trim();
                if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    issues.Add((Severity.Warning, "Website URL should start with https://"));
            }

            if (string.IsNullOrWhiteSpace(p.IconSource))
                issues.Add((Severity.Info, "No icon picked - a placeholder icon.png will be generated."));

            return issues;
        }

        private static string InkorporatedDependency()
        {
            string version = InkorporatedFallbackVersion;
            try
            {
                foreach (var mod in MelonMod.RegisteredMelons)
                {
                    var info = mod.Info;
                    if (info != null && string.Equals(info.Name, "Inkorporated", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(info.Version))
                    {
                        version = NormalizeVersion(info.Version);
                        break;
                    }
                }
            }
            catch { /* use fallback */ }
            return $"{ThunderstoreNamespace}-{InkorporatedPackage}-{version}";
        }

        private static string BuildReadme(Project project, string folder)
        {
            string intro = string.IsNullOrWhiteSpace(project.Description)
                ? "Custom tattoos for **Schedule I**, made with the Inkubator editor."
                : project.Description.Trim();

            var meta = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(project.Author)) meta.AppendLine("- Author: " + project.Author.Trim());
            meta.AppendLine("- License: " + LicenseLabel(project.License));
            if (!string.IsNullOrWhiteSpace(project.WebsiteUrl)) meta.AppendLine("- Website / source: " + project.WebsiteUrl.Trim());

            return
$@"# {project.Name}

{intro}

{meta}
## Requirements

- [Inkorporated](https://thunderstore.io/c/schedule-i/) (this pack depends on it)

## Install

1. Install Inkorporated.
2. Copy the `Inkorporated/Packs/{folder}` folder from this package into your game's
   `UserData/Inkorporated/Packs/` folder.
3. Launch the game - the tattoos appear in the in-game tattoo shop.

## Tattoos

{TattooList(project)}

---

Made with Inkubator. Add your own support / contact info here before publishing.
";
        }

        private static string TattooList(Project project)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in project.Tattoos)
            {
                string price = t.Price > 0 ? " - $" + t.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
                sb.AppendLine($"- {t.Name} ({t.Placement}){price}");
            }
            return sb.ToString();
        }

        // Write icon.png from the user-picked IconSource, auto-resized to the 256x256 Thunderstore wants. Falls back to
        // a generated dark placeholder when no icon is chosen or the chosen file is missing/unreadable.
        private static void WriteIcon(Project project, string path, ExportResult result)
        {
            string srcAbs = ProjectStore.ResolveRelative(project, project.IconSource);
            if (!string.IsNullOrEmpty(srcAbs) && File.Exists(srcAbs))
            {
                Texture2D chosen = null, scaled = null;
                try
                {
                    chosen = ImageLoader.LoadTexture(srcAbs);
                    if (chosen != null)
                    {
                        Texture2D out256 = chosen;
                        if (chosen.width != 256 || chosen.height != 256) { scaled = ScaleIcon(chosen, 256); if (scaled != null) out256 = scaled; }
                        byte[] png = ImageConversion.EncodeToPNG(out256);
                        if (png != null && png.Length > 0) { File.WriteAllBytes(path, png); return; }
                    }
                    result.Warnings.Add("Could not read the chosen icon; a placeholder icon.png was written instead.");
                }
                catch (Exception e) { Core.Log?.Warning("[export] icon source: " + e.Message); result.Warnings.Add("Could not read the chosen icon; a placeholder icon.png was written instead."); }
                finally { if (scaled != null) UnityEngine.Object.Destroy(scaled); if (chosen != null) UnityEngine.Object.Destroy(chosen); }
            }

            // Placeholder fallback.
            Texture2D icon = null;
            try
            {
                icon = SolidIcon(256, new Color(0.10f, 0.12f, 0.14f, 1f));
                byte[] png = ImageConversion.EncodeToPNG(icon);
                if (png == null || png.Length == 0) { result.Warnings.Add("Could not generate icon.png - add your own 256x256 icon before uploading."); return; }
                File.WriteAllBytes(path, png);
                result.Warnings.Add("A placeholder icon.png was generated - pick or add your own 256x256 art before uploading.");
            }
            catch (Exception e) { Core.Log?.Warning("[export] icon: " + e.Message); result.Warnings.Add("Could not generate icon.png - add your own 256x256 icon before uploading."); }
            finally { if (icon != null) UnityEngine.Object.Destroy(icon); }
        }

        // --- license ---

        public static string LicenseLabel(string token)
        {
            switch ((token ?? "").Trim())
            {
                case "MIT": return "MIT";
                case "CC-BY-4.0": return "CC BY 4.0";
                case "CC0-1.0": return "CC0 1.0 (public domain)";
                default: return "All rights reserved";
            }
        }

        // The license token cycle offered on the review screen.
        public static readonly string[] LicenseTokens = { "All rights reserved", "MIT", "CC-BY-4.0", "CC0-1.0" };

        private static string BuildLicense(Project project)
        {
            string holder = string.IsNullOrWhiteSpace(project.Author)
                ? (string.IsNullOrWhiteSpace(project.Name) ? "the author" : project.Name.Trim())
                : project.Author.Trim();
            int year = DateTime.Now.Year;
            switch ((project.License ?? "").Trim())
            {
                case "MIT":
                    return
$@"MIT License

Copyright (c) {year} {holder}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
";
                case "CC-BY-4.0":
                    return
$@"Copyright (c) {year} {holder}

This work is licensed under the Creative Commons Attribution 4.0 International
License (CC BY 4.0). You are free to share and adapt the material for any
purpose, even commercially, as long as you give appropriate credit.

Full license text: https://creativecommons.org/licenses/by/4.0/legalcode
";
                case "CC0-1.0":
                    return
$@"Copyright (c) {year} {holder}

To the extent possible under law, {holder} has waived all copyright and related
or neighboring rights to this work (Creative Commons CC0 1.0 Universal, public
domain dedication).

Full text: https://creativecommons.org/publicdomain/zero/1.0/legalcode
";
                default:
                    return "All rights reserved by " + holder +
                        ".\nReplace this with your preferred license (e.g. MIT) before publishing.\n";
            }
        }

        private static Texture2D SolidIcon(int size, Color c)
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
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Bilinear-scale a texture into a new size x size Texture2D (GPU blit). Used to fit the icon to Thunderstore's 256.
        private static Texture2D ScaleIcon(Texture2D src, int size)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                rt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply(false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // --- helpers ---

        public static string ToThunderstoreName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name ?? "")
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            string outp = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(outp) ? "MyTattooPack" : outp;
        }

        // Stable shop id / PNG filename for a tattoo: packname_version_tattooname (sanitized), so tattoos from
        // different packs or versions never collide in the shop's id space, and the id auto-follows the names.
        public static string ShopId(Project project, TattooEntry t)
        {
            string pack = Slug(project?.Name);
            string ver = NormalizeVersion(project?.ModVersion).Replace('.', '_');   // semantic version (Thunderstore requires it)
            string name = Slug(t?.Name);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(pack)) parts.Add(pack);
            parts.Add(ver);
            if (!string.IsNullOrEmpty(name)) parts.Add(name);
            string id = string.Join("_", parts);
            return string.IsNullOrWhiteSpace(id) ? "tattoo" : id;
        }

        private static string Slug(string s) => Paths.Sanitize(s ?? "").Replace(" ", "_").ToLowerInvariant();

        // Thunderstore requires a semantic X.Y.Z version; coerce common inputs ("1.2" -> "1.2.0", "v1" -> "1.0.0").
        public static string NormalizeVersion(string v)
        {
            string s = (v ?? "").Trim();
            if (s.StartsWith("v") || s.StartsWith("V")) s = s.Substring(1);
            if (string.IsNullOrWhiteSpace(s)) return "1.0.0";
            string[] parts = s.Split('.');
            int[] nums = { 1, 0, 0 };
            for (int i = 0; i < 3 && i < parts.Length; i++)
                int.TryParse(parts[i], out nums[i]);
            if (nums[0] < 1) nums[0] = 1;   // a valid first release is 1.0.0, never 0.0.0 (also rescues junk/'0' input)
            return $"{nums[0]}.{nums[1]}.{nums[2]}";
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        // --- manifest POCOs ---

        private sealed class PackManifest
        {
            public string name;
            public string author;
            public List<PackManifestEntry> tattoos;
        }

        private sealed class PackManifestEntry
        {
            public string id;
            public string name;
            public string placement;
            public string file;
            public float price;
        }

        private sealed class ThunderstoreManifest
        {
            public string name;
            public string version_number;
            public string website_url;
            public string description;
            public List<string> dependencies;
        }
    }
}
