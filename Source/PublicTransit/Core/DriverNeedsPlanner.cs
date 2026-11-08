using System;
using PublicTransit.Buildings;
using PublicTransit.Routes;
using RimWorld;
using Verse;
using Verse.AI;

namespace PublicTransit.Core
{
    /// <summary>
    /// Simple, map-scoped break planner. The driver keeps the normal RimWorld
    /// food/rest/recreation AI after VF disembarks them. Nothing is teleported
    /// or automatically eaten by this mod.
    /// </summary>
    public static class DriverNeedsPlanner
    {
        // Distance from the stop marker (in map cells). Food must actually be
        // reachable on foot from the stop, not merely visible through a wall.
        private const int FoodSearchRadius = 14;
        public const float ReturnToShiftThreshold = 0.60f;

        public static bool NeedsBreak(Pawn pawn, float threshold)
        {
            if (pawn == null || pawn.Dead || pawn.needs == null) return false;
            return IsLow(pawn.needs.food, threshold)
                || IsLow(pawn.needs.rest, threshold)
                || IsLow(pawn.needs.joy, threshold);
        }

        public static bool ReadyToReturn(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || pawn.needs == null)
                return false;
            return IsRecovered(pawn.needs.food) && IsRecovered(pawn.needs.rest)
                && IsRecovered(pawn.needs.joy);
        }

        private static bool IsLow(Need need, float threshold) =>
            need != null && need.CurLevelPercentage <= threshold;

        private static bool IsRecovered(Need need) =>
            need == null || need.CurLevelPercentage >= ReturnToShiftThreshold;

        /// <summary>
        /// Prefer the nearest dockable stop ON THIS LINE with food close by.
        /// If no such stop exists, choose the nearest dockable stop anyway so
        /// a starving/exhausted driver is never kept onboard indefinitely.
        /// </summary>
        public static int FindBreakStop(TransitRoute route, Map map, Pawn driver,
            IntVec3 fromCell, Func<Building_TransitStop, bool> canDock,
            out bool hasNearbyFood)
        {
            hasNearbyFood = false;
            if (route == null || map == null || driver == null) return -1;

            int bestFoodIndex = -1;
            int bestFallbackIndex = -1;
            float bestFoodDistance = float.MaxValue;
            float bestFallbackDistance = float.MaxValue;

            for (int i = 0; i < route.Stops.Count; i++)
            {
                Building_TransitStop stop = route.Stops[i];
                if (stop == null || stop.Destroyed || !stop.Spawned || stop.Map != map
                    || !canDock(stop)) continue;

                float distance = fromCell.DistanceToSquared(stop.DockingSideCell);
                if (distance < bestFallbackDistance)
                {
                    bestFallbackIndex = i;
                    bestFallbackDistance = distance;
                }
                bool foodAvailable = false;
                if (distance < bestFoodDistance)
                {
                    try { foodAvailable = HasAccessibleFood(stop, map, driver); }
                    catch (Exception ex)
                    {
                        Log.Warning("[Public Transit] Food access check at '" + stop.StopName
                            + "' failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                if (!foodAvailable) continue;
                bestFoodIndex = i;
                bestFoodDistance = distance;
            }

            hasNearbyFood = bestFoodIndex >= 0;
            return hasNearbyFood ? bestFoodIndex : bestFallbackIndex;
        }

        private static bool HasAccessibleFood(Building_TransitStop stop, Map map, Pawn driver)
        {
            IntVec3 marker = stop.DockingSideCell;
            if (!marker.InBounds(map) || !marker.Standable(map)) return false;
            var foods = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
            if (foods == null) return false;

            int maxDistSq = FoodSearchRadius * FoodSearchRadius;
            foreach (Thing food in foods)
            {
                if (food == null || !food.Spawned || food.Destroyed || food.Map != map
                    || food.stackCount <= 0 || food.def == null
                    || !food.def.IsNutritionGivingIngestible || food.def.IsDrug
                    || !food.IngestibleNow || food.IsNotFresh() || food.IsForbidden(driver)
                    || marker.DistanceToSquared(food.Position) > maxDistSq)
                    continue;
                // A real food source for THIS pawn, not just any edible item.
                if (!driver.WillEat(food, driver)) continue;
                if (map.reachability.CanReach(marker, food, PathEndMode.Touch,
                    TraverseParms.For(TraverseMode.PassDoors)))
                    return true;
            }
            return false;
        }
    }
}
