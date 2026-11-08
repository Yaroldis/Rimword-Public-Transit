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

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref DamageUnassignThreshold, "damageUnassignThreshold", 0.5f);
            Scribe_Values.Look(ref EnableExperimentalDriverBoarding,
                "enableExperimentalDriverBoarding", false);
            DamageUnassignThreshold = Mathf.Clamp(DamageUnassignThreshold, 0.10f, 1.0f);
        }
    }
}
