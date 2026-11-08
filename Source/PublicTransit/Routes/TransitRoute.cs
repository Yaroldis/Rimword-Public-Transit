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
        // Save a reference to the actual VF vehicle, never the vehicle itself.
        // The vehicle belongs to RimWorld / Vehicle Framework, not this route.
        private Thing assignedVehicle;
        // 0.5.0: player-selected driver. A reference, not a copy of the pawn.
        private Pawn assignedDriver;

        // 0.4.0: service is opt-in; route logic persists with the colony save.
        // A separate controller issues vehicle commands via the VF adapter.
        private bool serviceEnabled;
        private TransitRunState runState = TransitRunState.Stopped;
        private int nextStopIndex;
        private int dwellUntilTick;
        private IntVec3 destinationCell = IntVec3.Invalid;
        // 0.5.2: urgent driver break is distinct from stopping the line.
        private bool driverBreakPending;
        private bool driverOnBreak;
        private int driverBreakStopIndex = -1;

        public TransitRoute() { } // Scribe requires a parameterless constructor.

        public TransitRoute(string name)
        {
            id = Guid.NewGuid().ToString("N");
            Rename(name);
        }

        public string Id => id;
        public string DisplayName => displayName;
        public IReadOnlyList<Building_TransitStop> Stops => stops;
        public Thing AssignedVehicle => assignedVehicle;
        public Pawn AssignedDriver => assignedDriver;
        public bool ServiceEnabled => serviceEnabled;
        public TransitRunState RunState => runState;
        public int NextStopIndex => nextStopIndex;
        public int DwellUntilTick => dwellUntilTick;
        public IntVec3 DestinationCell => destinationCell;
        public bool DriverBreakPending => driverBreakPending;
        public bool DriverOnBreak => driverOnBreak;
        public int DriverBreakStopIndex => driverBreakStopIndex;

        internal void SetServiceEnabled(bool enabled)
        {
            serviceEnabled = enabled;
            if (!enabled) SetRunState(TransitRunState.Stopped);
        }

        internal void SetRunState(TransitRunState state) => runState = state;
        internal void SetNextStopIndex(int index) => nextStopIndex = index;
        internal void SetDwellUntilTick(int tick) => dwellUntilTick = tick;
        internal void SetDestinationCell(IntVec3 cell) => destinationCell = cell;
        internal void RequestDriverBreak(int stopIndex)
        {
            driverBreakPending = true;
            driverOnBreak = false;
            driverBreakStopIndex = stopIndex;
        }
        internal void BeginDriverBreak()
        {
            driverBreakPending = false;
            driverOnBreak = true;
            driverBreakStopIndex = -1;
        }
        internal void EndDriverBreak()
        {
            driverBreakPending = false;
            driverOnBreak = false;
            driverBreakStopIndex = -1;
        }

        // Only TransitMapComponent changes assignments: it validates ownership,
        // map and exclusivity across all lines.
        internal void SetAssignedVehicle(Thing vehicle) => assignedVehicle = vehicle;
        internal void SetAssignedDriver(Pawn pawn) => assignedDriver = pawn;

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
            Scribe_References.Look(ref assignedVehicle, "assignedVehicle");
            Scribe_References.Look(ref assignedDriver, "PT_assignedDriver");
            Scribe_Values.Look(ref serviceEnabled, "PT_serviceEnabled", false);
            Scribe_Values.Look(ref runState, "PT_runState", TransitRunState.Stopped);
            Scribe_Values.Look(ref nextStopIndex, "PT_nextStopIndex", 0);
            Scribe_Values.Look(ref dwellUntilTick, "PT_dwellUntilTick", 0);
            Scribe_Values.Look(ref destinationCell, "PT_destinationCell", IntVec3.Invalid);
            Scribe_Values.Look(ref driverBreakPending, "PT_driverBreakPending", false);
            Scribe_Values.Look(ref driverOnBreak, "PT_driverOnBreak", false);
            Scribe_Values.Look(ref driverBreakStopIndex, "PT_driverBreakStopIndex", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (stops == null) stops = new List<Building_TransitStop>();
                if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(displayName)) displayName = "Route";
                if (nextStopIndex < 0 || nextStopIndex >= stops.Count) nextStopIndex = 0;
                if (driverBreakPending && (driverBreakStopIndex < 0
                    || driverBreakStopIndex >= stops.Count)) EndDriverBreak();
                // Never restart an old movement command while Scribe is resolving the map.
                // The map controller will resume or reissue it after the first tick.
                if (serviceEnabled && runState == TransitRunState.Moving)
                    runState = TransitRunState.Recovering;
            }
        }
    }
}
