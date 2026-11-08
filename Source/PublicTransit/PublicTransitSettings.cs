using UnityEngine;
using Verse;

namespace PublicTransit
{
    /// <summary>Persistent player preference, not part of the colony save.</summary>
    public sealed class PublicTransitSettings : ModSettings
    {
        // Damage fraction at which ANY VF component causes auto-unassignment.
        // 0.5 means losing 50% or more of one component's maximum health.
        public float DamageUnassignThreshold = 0.5f;
        // Explicit opt-in: VF boarding reflection is experimental in 0.5.0.
        public bool EnableExperimentalDriverBoarding = false;
        // Driver starts a break when food, rest or recreation reaches this level.
        public float DriverBreakNeedThreshold = 0.20f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref DamageUnassignThreshold, "damageUnassignThreshold", 0.5f);
            Scribe_Values.Look(ref EnableExperimentalDriverBoarding,
                "enableExperimentalDriverBoarding", false);
            Scribe_Values.Look(ref DriverBreakNeedThreshold, "driverBreakNeedThreshold", 0.20f);
            DamageUnassignThreshold = Mathf.Clamp(DamageUnassignThreshold, 0.10f, 1.0f);
            DriverBreakNeedThreshold = Mathf.Clamp(DriverBreakNeedThreshold, 0.05f, 0.40f);
        }
    }
}
