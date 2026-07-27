using System;

namespace Inkubator.Editor
{
    /// <summary>Body region a tattoo applies to. Matches Inkorporated's vocabulary (chest|leftarm|rightarm|face).</summary>
    public enum Placement
    {
        Chest,
        LeftArm,
        RightArm,
        Face
    }

    /// <summary>
    /// Maps placements to the game's built-in source layers (cloned for material/Order/UV), the custom session
    /// Resources path used for live preview, and the manifest token Inkorporated expects. Mirrors
    /// Inkorporated.Registration.TattooRegistry so exported packs match exactly.
    /// </summary>
    public static class Placements
    {
        public static readonly Placement[] All = { Placement.Chest, Placement.LeftArm, Placement.RightArm, Placement.Face };

        /// <summary>Manifest/folder token: chest|leftarm|rightarm|face.</summary>
        public static string Token(Placement p) => p switch
        {
            Placement.Chest => "chest",
            Placement.LeftArm => "leftarm",
            Placement.RightArm => "rightarm",
            Placement.Face => "face",
            _ => "chest"
        };

        public static bool TryParse(string s, out Placement p)
        {
            switch ((s ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "chest": p = Placement.Chest; return true;
                case "leftarm": p = Placement.LeftArm; return true;
                case "rightarm": p = Placement.RightArm; return true;
                case "face": p = Placement.Face; return true;
                default: p = Placement.Chest; return false;
            }
        }

        /// <summary>A built-in layer of each placement to clone (inherits CombinedMaterial / Order / UV expectations).
        /// Folder casing matches the shipped resource index exactly.</summary>
        public static string SourceLayer(Placement p) => p switch
        {
            Placement.Chest => "Avatar/Layers/Tattoos/Chest/Chest_Bird",
            Placement.LeftArm => "Avatar/Layers/Tattoos/LeftArm/LeftArm_Web",
            Placement.RightArm => "Avatar/Layers/Tattoos/RightArm/RightArm_Web",
            Placement.Face => "Avatar/Layers/Tattoos/Face/Face_Teardrop",
            _ => "Avatar/Layers/Tattoos/Chest/Chest_Bird"
        };

        /// <summary>
        /// Every stock tattoo of a placement. Their combined alpha marks where that body part sits in the shared
        /// body atlas, which is what the editor uses to frame its canvas (see <see cref="UvRegions"/>).
        /// </summary>
        public static string[] StockLayers(Placement p) => p switch
        {
            Placement.Chest => Build("Chest", "Bird", "DeadFace", "Egg", "LBC", "Sword"),
            Placement.LeftArm => Build("LeftArm", "Alien", "Heart", "Peace", "Web", "Weed"),
            Placement.RightArm => Build("RightArm", "Alien", "Heart", "Peace", "Web", "Weed"),
            Placement.Face => Build("Face", "ForeheadCross", "Sword", "Teardrop", "Tribal"),
            _ => new string[0]
        };

        private static string[] Build(string folder, params string[] names)
        {
            var outp = new string[names.Length];
            for (int i = 0; i < names.Length; i++) outp[i] = "Avatar/Layers/Tattoos/" + folder + "/" + folder + "_" + names[i];
            return outp;
        }

        /// <summary>
        /// Stable per-placement custom Resources path used for LIVE PREVIEW. Re-baking overwrites the same path
        /// (RuntimeResourceRegistry has no unregister), so preview textures do not leak. Face uses a capital
        /// "/Face/" segment so the avatar routes it to the face mesh; body placements must not contain it.
        /// </summary>
        public static string SessionTargetPath(Placement p)
        {
            string seg = p == Placement.Face ? "Face" : Token(p);
            return "Avatar/Layers/Tattoos/custom/inkubator_session/" + seg;
        }

        /// <summary>Embedded UV-template resource name bundled with Inkubator (authoring canvas reference).</summary>
        public static string TemplateResource(Placement p) => "Inkubator.Assets.Templates." + Token(p) + ".png";
    }
}
