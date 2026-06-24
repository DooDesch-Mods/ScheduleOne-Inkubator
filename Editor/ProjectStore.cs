using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Inkubator.Editor
{
    /// <summary>
    /// Loads/saves <see cref="Project"/> as project.json under UserData/Inkubator/Projects/&lt;name&gt;, and copies
    /// imported PNGs into the project's self-contained sources/ folder.
    /// </summary>
    public static class ProjectStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>Names of all existing projects (folders containing a project.json).</summary>
        public static List<string> List()
        {
            var result = new List<string>();
            try
            {
                Paths.EnsureBaseDirs();
                foreach (string dir in Directory.GetDirectories(Paths.Projects))
                {
                    if (File.Exists(Path.Combine(dir, "project.json")))
                        result.Add(Path.GetFileName(dir));
                }
            }
            catch (Exception e) { Core.Log?.Warning("[project] list failed: " + e.Message); }
            return result;
        }

        public static bool Exists(string name) => File.Exists(Paths.ProjectFile(name));

        /// <summary>The display title (pack name) stored in a project's json, falling back to the folder name.</summary>
        public static string DisplayName(string folder)
        {
            try
            {
                string file = Paths.ProjectFile(folder);
                if (File.Exists(file))
                {
                    var p = JsonConvert.DeserializeObject<Project>(File.ReadAllText(file));
                    if (p != null && !string.IsNullOrWhiteSpace(p.Name)) return p.Name;
                }
            }
            catch { }
            return folder;
        }

        /// <summary>Number of tattoos in a project (read from its json), or 0.</summary>
        public static int TattooCount(string folder)
        {
            try
            {
                string file = Paths.ProjectFile(folder);
                if (File.Exists(file))
                {
                    var p = JsonConvert.DeserializeObject<Project>(File.ReadAllText(file));
                    if (p?.Tattoos != null) return p.Tattoos.Count;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>A sensible starting description for a new pack (the user can replace it on the review screen).</summary>
        public const string DefaultDescription = "Custom tattoos for Schedule I, made with the Inkubator editor.";

        /// <summary>Create a new empty project on disk and return it.</summary>
        public static Project Create(string name, string author = "")
        {
            string folder = Paths.Sanitize(name);
            Directory.CreateDirectory(Paths.SourcesDir(folder));
            Directory.CreateDirectory(Paths.BakedDir(folder));
            var project = new Project { Name = name, Author = author, FolderName = folder, Description = DefaultDescription };
            Save(project);
            return project;
        }

        public static Project Load(string name)
        {
            try
            {
                string file = Paths.ProjectFile(name);
                if (!File.Exists(file)) return null;
                var project = JsonConvert.DeserializeObject<Project>(File.ReadAllText(file));
                if (project == null) return null;
                project.FolderName = Paths.Sanitize(name);
                project.Tattoos ??= new List<TattooEntry>();
                return project;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[project] load '" + name + "' failed: " + e.Message);
                return null;
            }
        }

        public static bool Save(Project project)
        {
            if (project == null) return false;
            try
            {
                string folder = string.IsNullOrEmpty(project.FolderName) ? Paths.Sanitize(project.Name) : project.FolderName;
                project.FolderName = folder;
                Directory.CreateDirectory(Paths.SourcesDir(folder));
                Directory.CreateDirectory(Paths.BakedDir(folder));
                File.WriteAllText(Paths.ProjectFile(folder), JsonConvert.SerializeObject(project, JsonSettings));
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[project] save failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Permanently delete a project's folder (the pack, its sources and bakes). Returns true on success.</summary>
        public static bool Delete(string folder)
        {
            try
            {
                string dir = Paths.ProjectDir(folder);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[project] delete '" + folder + "' failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Copy a PNG from anywhere on disk into the project's sources/ folder. Returns the source-relative
        /// path to store on a <see cref="Decal"/> (e.g. "sources/skull.png"), or null on failure.
        /// </summary>
        public static string ImportSource(Project project, string absolutePngPath)
        {
            try
            {
                if (project == null || string.IsNullOrEmpty(absolutePngPath) || !File.Exists(absolutePngPath)) return null;
                string folder = project.FolderName;
                string fileName = Path.GetFileName(absolutePngPath);
                string dest = Path.Combine(Paths.SourcesDir(folder), fileName);
                // Avoid clobbering a different image with the same name.
                int n = 1;
                while (File.Exists(dest) && !SameFile(absolutePngPath, dest))
                {
                    string stem = Path.GetFileNameWithoutExtension(fileName);
                    dest = Path.Combine(Paths.SourcesDir(folder), $"{stem}_{n}.png");
                    n++;
                }
                Directory.CreateDirectory(Paths.SourcesDir(folder));
                File.Copy(absolutePngPath, dest, true);
                return "sources/" + Path.GetFileName(dest);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[project] import source failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Absolute path of a decal's source PNG within a project.</summary>
        public static string ResolveSource(Project project, Decal decal)
        {
            if (project == null || decal == null || string.IsNullOrEmpty(decal.Source)) return null;
            return ResolveRelative(project, decal.Source);
        }

        /// <summary>Absolute path of any project-relative path (e.g. an "sources/icon.png" IconSource), or null.</summary>
        public static string ResolveRelative(Project project, string relative)
        {
            if (project == null || string.IsNullOrEmpty(relative)) return null;
            string rel = relative.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(Paths.ProjectDir(project.FolderName), rel);
        }

        private static bool SameFile(string a, string b)
        {
            try { return new FileInfo(a).Length == new FileInfo(b).Length; }
            catch { return false; }
        }
    }
}
