using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using PublicTransit.Routes;
using RimWorld;
using Verse;

namespace PublicTransit.Core
{
    /// <summary>
    /// Owns the transport network on a single colony map. The stop registry is
    /// reconstructed from spawned buildings; only routes are serialized.
    /// </summary>
    public sealed class TransitMapComponent : MapComponent
    {
        private List<TransitRoute> routes = new List<TransitRoute>();
        private readonly HashSet<Building_TransitStop> registeredStops = new HashSet<Building_TransitStop>();

        public TransitMapComponent(Map map) : base(map) { }

        public IReadOnlyList<TransitRoute> Routes => routes;

        public List<Building_TransitStop> GetRegisteredStops()
        {
            var result = new List<Building_TransitStop>();
            foreach (Building_TransitStop stop in registeredStops)
                if (IsValidStop(stop)) result.Add(stop);
            result.Sort((a, b) => string.Compare(a.StopName, b.StopName, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        public void RegisterStop(Building_TransitStop stop)
        {
            if (IsValidStop(stop))
                registeredStops.Add(stop);
        }

        public void UnregisterStop(Building_TransitStop stop)
        {
            registeredStops.Remove(stop);
            if (routes == null) return;
            foreach (TransitRoute route in routes)
                route?.RemoveStop(stop);
        }

        public TransitRoute CreateRoute(string displayName, Building_TransitStop firstStop = null)
        {
            if (routes == null) routes = new List<TransitRoute>();
            var route = new TransitRoute(displayName);
            if (IsValidStop(firstStop)) route.AddStop(firstStop);
            routes.Add(route);
            return route;
        }

        public bool RemoveRoute(TransitRoute route) => route != null && routes != null && routes.Remove(route);

        /// <summary>
        /// Find eligible vehicles from any mod based on Vehicle Framework.
        /// This scan happens when the player opens the assignment menu.
        /// </summary>
        public List<Thing> GetAssignableVehicles(TransitRoute targetRoute)
        {
            var result = new List<Thing>();
            if (map?.listerThings == null || routes == null || !routes.Contains(targetRoute))
                return result;

            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (VehicleAdapter.CanAssign(thing, map) && !AssignedToAnotherRoute(thing, targetRoute))
                    result.Add(thing);
            }
            result.Sort((a, b) => string.Compare(a.LabelCap, b.LabelCap,
                StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        private bool AssignedToAnotherRoute(Thing vehicle, TransitRoute exceptRoute)
        {
            if (vehicle == null || routes == null) return false;
            foreach (TransitRoute route in routes)
            {
                if (route != null && route != exceptRoute && route.AssignedVehicle == vehicle)
                    return true;
            }
            return false;
        }

        /// <summary>Assign a valid VF vehicle to exactly one line on this map.</summary>
        public bool TryAssignVehicle(TransitRoute route, Thing vehicle, out string error)
        {
            error = null;
            if (routes == null || route == null || !routes.Contains(route))
            {
                error = "PT_VehicleRouteMissing".Translate();
                return false;
            }
            if (!VehicleAdapter.CanAssign(vehicle, map))
            {
                error = "PT_VehicleInvalid".Translate();
                return false;
            }
            if (AssignedToAnotherRoute(vehicle, route))
            {
                error = "PT_VehicleAlreadyAssigned".Translate();
                return false;
            }

            route.SetAssignedVehicle(vehicle);
            return true;
        }

        public void UnassignVehicle(TransitRoute route)
        {
            if (route != null && routes != null && routes.Contains(route))
                route.SetAssignedVehicle(null);
        }

        /// <summary>
        /// An off-map vehicle remains assigned (e.g. while in a caravan).
        /// Destroyed or duplicate assignments are discarded; route stops survive.
        /// </summary>
        private void RepairVehicleAssignments(bool removeDestroyed)
        {
            if (routes == null) return;
            var seen = new HashSet<Thing>();
            foreach (TransitRoute route in routes)
            {
                if (route == null || route.AssignedVehicle == null) continue;
                Thing vehicle = route.AssignedVehicle;
                if ((removeDestroyed && vehicle.Destroyed) || !VehicleAdapter.IsVehicle(vehicle) || !seen.Add(vehicle))
                    route.SetAssignedVehicle(null);
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            // Not a transport AI: only clean up destroyed vehicle references.
            // This lightweight check runs once per 250 game ticks.
            if (Find.TickManager.TicksGame % 250 == 0)
                RepairVehicleAssignments(true);
        }

        public string GetVehicleStatus(TransitRoute route)
        {
            Thing vehicle = route?.AssignedVehicle;
            if (vehicle == null) return "PT_VehicleStatusNone".Translate();
            if (vehicle.Destroyed) return "PT_VehicleStatusDestroyed".Translate();
            if (!VehicleAdapter.IsVehicle(vehicle)) return "PT_VehicleStatusInvalid".Translate();
            if (!vehicle.Spawned || vehicle.Map != map) return "PT_VehicleStatusOffMap".Translate();
            if (vehicle.Faction != Faction.OfPlayer) return "PT_VehicleStatusNotOwned".Translate();
            if (route.Stops.Count < 2) return "PT_VehicleStatusNeedStops".Translate();
            return "PT_VehicleStatusReady".Translate();
        }

        public bool IsValidStop(Building_TransitStop stop) =>
            stop != null && !stop.Destroyed && stop.Spawned && stop.Map == map;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RebuildStopRegistry();
            // Do not inspect routes here: referenced Things may not yet be resolved
            // while loading a save. RepairRoutes runs in Scribe's PostLoadInit pass.
        }

        private void RebuildStopRegistry()
        {
            registeredStops.Clear();
            if (map == null || map.listerThings == null) return;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                var stop = thing as Building_TransitStop;
                if (IsValidStop(stop)) registeredStops.Add(stop);
            }
        }

        private void RepairRoutes()
        {
            if (routes == null) routes = new List<TransitRoute>();
            routes.RemoveAll(route => route == null);
            // During Scribe PostLoadInit some Thing lifecycle fields may still be
            // transient. Discard only missing or destroyed references here; actual
            // despawns are handled by Building_TransitStop.DeSpawn at runtime.
            foreach (TransitRoute route in routes)
                route.RemoveInvalidStops(stop => stop != null && !stop.Destroyed);
            // Only deduplicate here: VF Things may still be in load lifecycle.
            // Destroyed references are removed on normal map ticks.
            RepairVehicleAssignments(false);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref routes, "PT_routes", LookMode.Deep);
            // References to spawned Things are resolved during the PostLoadInit pass.
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                RepairRoutes();
        }
    }
}
