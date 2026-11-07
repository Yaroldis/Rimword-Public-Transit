using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using Verse;

namespace PublicTransit.Routes
{
    /// <summary>
    /// Persistent definition of one public transit line on a single map.
    /// It contains a sequence of stops, NOT Vehicle Framework runtime state.
    /// We will add schedules and assigned vehicles only after verifying VF APIs.
    /// </summary>
    public sealed class TransitRoute : IExposable
    {
        private string id;
        private string displayName;
        private List<Building_TransitStop> stops = new List<Building_TransitStop>();

        // The parameterless constructor is required by RimWorld's Scribe loader.
        public TransitRoute() { }

        public TransitRoute(string name)
        {
            id = Guid.NewGuid().ToString("N");
            displayName = name;
        }

        public string Id => id;
        public string DisplayName => displayName;
        public IReadOnlyList<Building_TransitStop> Stops => stops;

        public bool AddStop(Building_TransitStop stop)
        {
            if (stop == null || stops.Contains(stop))
                return false;

            stops.Add(stop);
            return true;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref displayName, "displayName");
            // Stops are spawned Thing instances, so save references rather than duplicates.
            Scribe_Collections.Look(ref stops, "stops", LookMode.Reference);

            if (stops == null)
                stops = new List<Building_TransitStop>();
        }
    }
}
