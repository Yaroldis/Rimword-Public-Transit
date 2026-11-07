using Verse;

namespace PublicTransit
{
    /// <summary>
    /// Entry point. RimWorld constructs Mod subclasses when loading assemblies.
    /// No Harmony patching or experimental Vehicle Framework APIs are used yet.
    /// </summary>
    public sealed class PublicTransitMod : Mod
    {
        public PublicTransitMod(ModContentPack content) : base(content)
        {
            Log.Message("[Public Transit] Bootstrap loaded for RimWorld 1.6.");
        }
    }
}
