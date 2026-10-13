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
    /// Maps placements to the game's built-in source layers (copied for layer type and layering order), the
    /// session object id used for live preview, and the manifest token Inkorporated expects. Mirrors
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
        /// entry is the tattoo a preview object is copied from (so it inherits the right layering order), and their
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

        /// <summary>The built-in layer whose avatar object a preview tattoo of this placement is copied from.</summary>
        public static string SourceLayer(Placement p) => StockLayers(p)[0];

        private static string[] Build(string folder, params string[] names)
        {
            var outp = new string[names.Length];
            for (int i = 0; i < names.Length; i++) outp[i] = "Avatar/Layers/Tattoos/" + folder + "/" + folder + "_" + names[i];
            return outp;
        }

        /// <summary>Prefix of every avatar object id the live preview registers with the game.</summary>
        public const string SessionIdPrefix = "inkubator_session_";

        /// <summary>
        /// Stable per-placement avatar object id used for LIVE PREVIEW. Re-baking repaints the same object, so the
        /// game's object library holds at most one entry per placement.
        /// </summary>
        public static string SessionObjectId(Placement p) => SessionIdPrefix + Token(p);
    }
}
