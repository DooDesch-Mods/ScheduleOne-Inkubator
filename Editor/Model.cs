using System.Collections.Generic;
using Newtonsoft.Json;

namespace Inkubator.Editor
{
    /// <summary>
    /// One imported PNG placed on a body part. Transform is stored in normalized UV space so it is
    /// resolution-independent: (U,V) is the decal centre (0..1), Scale is the decal width as a fraction of the
    /// canvas width, RotationDeg is clockwise degrees, Opacity 0..1.
    /// </summary>
    public sealed class Decal
    {
        [JsonProperty("source")] public string Source { get; set; } = "";   // relative to the project's sources/ dir
        [JsonProperty("u")] public float U { get; set; } = 0.5f;
        [JsonProperty("v")] public float V { get; set; } = 0.5f;
        [JsonProperty("scale")] public float Scale { get; set; } = 0.35f;
        [JsonProperty("rotationDeg")] public float RotationDeg { get; set; } = 0f;
        [JsonProperty("opacity")] public float Opacity { get; set; } = 1f;
        [JsonProperty("flipX")] public bool FlipX { get; set; } = false;
        [JsonProperty("flipY")] public bool FlipY { get; set; } = false;
        [JsonProperty("tint")] public string Tint { get; set; } = "#FFFFFFFF";
        [JsonProperty("order")] public int Order { get; set; } = 0;
    }

    /// <summary>
    /// One shippable Inkorporated tattoo: one placement texture built from one or more flattened decals. This
    /// is the unit that becomes a single entry in the exported pack manifest.
    /// </summary>
    public sealed class TattooEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("placement")] public string Placement { get; set; } = "chest";
        [JsonProperty("price")] public float Price { get; set; } = 250f;   // default shop price; a json with an explicit 0 still loads as 0 (Free)
        [JsonProperty("visible")] public bool Visible { get; set; } = true;  // shown in the editor's live preview
        [JsonProperty("decals")] public List<Decal> Decals { get; set; } = new List<Decal>();
        [JsonProperty("bakedFile")] public string BakedFile { get; set; } = "";

        [JsonIgnore]
        public Placement PlacementEnum
        {
            get { Placements.TryParse(Placement, out var p); return p; }
            set { Placement = Placements.Token(value); }
        }
    }

    /// <summary>
    /// A tattoo mod in progress. Persisted as project.json; the editable source of truth. Exports are derived
    /// from this (flattened baked PNGs + minimal manifests).
    /// </summary>
    public sealed class Project
    {
        [JsonProperty("formatVersion")] public int FormatVersion { get; set; } = 1;
        [JsonProperty("name")] public string Name { get; set; } = "My Tattoo Pack";
        [JsonProperty("author")] public string Author { get; set; } = "";
        [JsonProperty("modVersion")] public string ModVersion { get; set; } = "1.0.0";
        [JsonProperty("description")] public string Description { get; set; } = "";
        // Release metadata edited on the review screen and woven into the exported manifests / README / LICENSE.
        [JsonProperty("websiteUrl")] public string WebsiteUrl { get; set; } = "";          // Thunderstore website_url + README link
        [JsonProperty("license")] public string License { get; set; } = "All rights reserved"; // token -> LICENSE body
        [JsonProperty("iconSource")] public string IconSource { get; set; } = "";           // project-relative PNG for icon.png (empty = placeholder)
        [JsonProperty("tattoos")] public List<TattooEntry> Tattoos { get; set; } = new List<TattooEntry>();

        [JsonIgnore] public string FolderName { get; set; } // runtime-only: the on-disk folder this loaded from
    }
}
