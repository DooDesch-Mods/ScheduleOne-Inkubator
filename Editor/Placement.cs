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

        /// <summary>
        /// Every stock tattoo of a placement, casing exactly as in the shipped resource index. Two jobs: the first
        /// entry is the layer a custom tattoo is cloned from (so it inherits the right sort Order), and their
        /// combined alpha marks where the body part sits in the shared body atlas, which is how the editor frames
        /// its canvas (see <see cref="UvRegions"/>).
        /// </summary>
        public static string[] StockLayers(Placement p) => p switch
        {
            Placement.LeftArm => Build("LeftArm", "Web", "Alien", "Heart", "Peace", "Weed"),
            Placement.RightArm => Build("RightArm", "Web", "Alien", "Heart", "Peace", "Weed"),
            Placement.Face => Build("Face", "Teardrop", "ForeheadCross", "Sword", "Tribal"),
            _ => Build("Chest", "Bird", "DeadFace", "Egg", "LBC", "Sword")
        };

        /// <summary>The built-in layer a custom tattoo of this placement is cloned from.</summary>
        public static string SourceLayer(Placement p) => StockLayers(p)[0];

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
    }
}
