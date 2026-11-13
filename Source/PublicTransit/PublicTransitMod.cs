using UnityEngine;
using Verse;

namespace PublicTransit
{
    public sealed class PublicTransitMod : Mod
    {
        private static PublicTransitSettings settings;

        public static bool ExperimentalDriverBoardingEnabled =>
            settings != null && settings.EnableExperimentalDriverBoarding;

        public static float DamageUnassignThreshold => settings == null
            ? 0.5f : settings.DamageUnassignThreshold;
        public static float DriverBreakNeedThreshold => settings == null
            ? 0.20f : settings.DriverBreakNeedThreshold;

        public PublicTransitMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<PublicTransitSettings>();
            Core.TransitGotoDiagnostics.Initialize();
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
            listing.GapLine();
            listing.CheckboxLabeled("PT_ExperimentalDriverBoardingSetting".Translate().ToString(),
                ref settings.EnableExperimentalDriverBoarding);
            listing.Label("PT_ExperimentalDriverBoardingDescription".Translate().ToString());
            listing.GapLine();
            listing.Label("PT_DriverBreakThresholdSetting".Translate(
                (int)(DriverBreakNeedThreshold * 100f)).ToString());
            settings.DriverBreakNeedThreshold = listing.Slider(
                settings.DriverBreakNeedThreshold, 0.05f, 0.40f);
            listing.Label("PT_DriverBreakThresholdDescription".Translate().ToString());
            listing.End();
        }
    }
}
