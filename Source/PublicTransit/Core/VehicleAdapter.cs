using System;
using RimWorld;
using Verse;

namespace PublicTransit.Core
{
    /// <summary>
    /// Minimal, read-only boundary to Vehicle Framework 1.6.
    /// VF's public vehicle base class is Vehicles.VehiclePawn. Comparing the
    /// runtime inheritance chain avoids a compile-time VF DLL dependency.
    /// We will extend this adapter with explicit VF API calls in a later stage.
    /// </summary>
    public static class VehicleAdapter
    {
        private const string VehiclePawnFullName = "Vehicles.VehiclePawn";

        public static bool IsVehicle(Thing thing)
        {
            if (thing == null) return false;

            // A content mod may derive its own class from VehiclePawn.
            for (Type type = thing.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == VehiclePawnFullName)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Only player-owned vehicles currently spawned on this map can be
        /// newly assigned. A vehicle that later leaves the map is kept in its
        /// route as an unavailable reference rather than silently forgotten.
        /// </summary>
        public static bool CanAssign(Thing thing, Map map)
        {
            return map != null && thing != null && !thing.Destroyed && thing.Spawned
                && thing.Map == map && thing.Faction == Faction.OfPlayer
                && IsVehicle(thing);
        }
    }
}
