using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using PublicTransit.Routes;
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
