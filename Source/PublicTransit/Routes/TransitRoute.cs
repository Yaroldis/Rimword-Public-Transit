using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using Verse;

namespace PublicTransit.Routes
{
    /// <summary>
    /// One fixed-order route. Existing save key names are unchanged since 0.1.0.
    /// </summary>
    public sealed class TransitRoute : IExposable
    {
        private string id;
        private string displayName;
        private List<Building_TransitStop> stops = new List<Building_TransitStop>();

        public TransitRoute() { } // Scribe requires a parameterless constructor.

        public TransitRoute(string name)
        {
            id = Guid.NewGuid().ToString("N");
            Rename(name);
        }

        public string Id => id;
        public string DisplayName => displayName;
        public IReadOnlyList<Building_TransitStop> Stops => stops;

        public void Rename(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                displayName = name.Trim();
        }

        public bool ContainsStop(Building_TransitStop stop) => stop != null && stops.Contains(stop);

        public bool AddStop(Building_TransitStop stop)
        {
            if (stop == null || stop.Destroyed || !stop.Spawned || stops.Contains(stop))
                return false;
            stops.Add(stop);
            return true;
        }

        public bool RemoveStop(Building_TransitStop stop) => stop != null && stops.Remove(stop);

        public bool MoveStop(int index, int direction)
        {
            int target = index + direction;
            if (index < 0 || index >= stops.Count || target < 0 || target >= stops.Count)
                return false;
            Building_TransitStop temp = stops[index];
            stops[index] = stops[target];
            stops[target] = temp;
            return true;
        }

        public void RemoveInvalidStops(Func<Building_TransitStop, bool> isValid)
        {
            if (stops == null) stops = new List<Building_TransitStop>();
            var seen = new HashSet<Building_TransitStop>();
            stops.RemoveAll(stop => !isValid(stop) || !seen.Add(stop));
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref displayName, "displayName");
            Scribe_Collections.Look(ref stops, "stops", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (stops == null) stops = new List<Building_TransitStop>();
                if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(displayName)) displayName = "Route";
            }
        }
    }
}
