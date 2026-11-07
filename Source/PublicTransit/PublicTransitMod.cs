using UnityEngine;
using Verse;

namespace PublicTransit
{
    public sealed class PublicTransitMod : Mod
    {
        private static PublicTransitSettings settings;

        public static float DamageUnassignThreshold => settings == null
            ? 0.5f : settings.DamageUnassignThreshold;

        public PublicTransitMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<PublicTransitSettings>();
            Log.Message("[Public Transit] Bootstrap loaded for RimWorld 1.6.");
        }

        public override string SettingsCategory() => "Public Transit";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("PT_DamageThresholdSetting".Translate(
                (int)(DamageUnassignThreshold * 100f)).ToString());
            settings.DamageUnassignThreshold = listing.Slider(
                settings.DamageUnassignThreshold, 0.10f, 1.0f);
            listing.Label("PT_DamageThresholdDescription".Translate().ToString());
            listing.End();
        }
    }
}
