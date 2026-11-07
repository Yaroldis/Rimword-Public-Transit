using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using PublicTransit.Routes;
using RimWorld;
using Verse;

namespace PublicTransit.Core
{
    /// <summary>
    /// Automatic movement of one ROAD vehicle per route. VF remains responsible for
    /// actual pathfinding and movement. No pawn jobs or passenger logic are changed.
    /// </summary>
    public sealed class TransitVehicleController
    {
        private const int StopWaitTicks = 240;
        private const int TravelTimeoutTicks = 1800;
        private const int PathStartGraceTicks = 150;
        private const int MaxDockingRetries = 2;
        private readonly Map map;
        private readonly Dictionary<TransitRoute, TravelWatch> watches =
            new Dictionary<TransitRoute, TravelWatch>();
        private readonly Dictionary<TransitRoute, string> lastIgnitionWarning =
            new Dictionary<TransitRoute, string>();

        private sealed class TravelWatch
        {
            public IntVec3 lastCell;
            public int lastProgressTick;
            public int commandTick;
            public int retries;
            public readonly HashSet<IntVec3> triedDockingCells = new HashSet<IntVec3>();
        }

        public TransitVehicleController(Map map) { this.map = map; }

        public bool TryEnable(TransitRoute route, out string error)
        {
            error = null;
            if (route == null || route.AssignedVehicle == null)
                error = "PT_DriveNoVehicle".Translate().ToString();
            else if (route.Stops.Count < 2)
                error = "PT_VehicleStatusNeedStops".Translate().ToString();
            else if (!VehicleAdapter.CanAssign(route.AssignedVehicle, map))
                error = "PT_VehicleInvalid".Translate().ToString();
            else if (VehicleAdapter.IsTrain(route.AssignedVehicle))
                error = "PT_DriveTrainPending".Translate().ToString();
            else if (!AllStopsValid(route))
                error = "PT_DriveMissingStop".Translate().ToString();
            else if (!VehicleAdapter.SupportsDriving(route.AssignedVehicle, out string vfError))
                error = "PT_DriveApiUnsupported".Translate(vfError).ToString();
            if (error != null) return false;

            route.SetServiceEnabled(true);
            route.SetRunState(TransitRunState.WaitingForDriver);
            watches.Remove(route);
            lastIgnitionWarning.Remove(route);
            // The nearest stop is the starting area, not necessarily the first destination.
            int closest = 0;
            float minimumDistance = float.MaxValue;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                float distance = route.AssignedVehicle.Position.DistanceToSquared(route.Stops[i].Position);
                if (distance < minimumDistance) { closest = i; minimumDistance = distance; }
            }
            route.SetNextStopIndex(minimumDistance <= 25
                ? (closest + 1) % route.Stops.Count : closest);
            route.SetDestinationCell(IntVec3.Invalid);
            return true;
        }

        /// <summary>
        /// Stops movement; deliberately leaves VF ignition alone so the player
        /// retains control over the engine outside of Public Transit service.
        /// </summary>
        public void Stop(TransitRoute route)
        {
            if (route == null) return;
            if (route.ServiceEnabled && route.AssignedVehicle != null)
            {
                if (!VehicleAdapter.TryStop(route.AssignedVehicle, out string err))
                    Log.Warning("[Public Transit] Unable to stop route '" + route.DisplayName + "': " + err);
            }
            route.SetServiceEnabled(false);
            route.SetDestinationCell(IntVec3.Invalid);
            watches.Remove(route);
            lastIgnitionWarning.Remove(route);
        }

        public void TickRoute(TransitRoute route, int tick)
        {
            if (route == null || !route.ServiceEnabled) return;
            Thing vehicle = route.AssignedVehicle;
            if (vehicle == null || vehicle.Destroyed || !vehicle.Spawned
                || vehicle.Map != map || vehicle.Faction != Faction.OfPlayer
                || route.Stops.Count < 2 || VehicleAdapter.IsTrain(vehicle))
            {
                Block(route, "invalid route or vehicle");
                return;
            }
            if (!AllStopsValid(route))
            {
                Block(route, "missing stop");
                return;
            }

            if (!VehicleAdapter.HasActiveDriver(vehicle))
            {
                if (route.RunState == TransitRunState.Moving ||
                    route.RunState == TransitRunState.Recovering)
                {
                    if (!VehicleAdapter.TryStop(vehicle, out string error))
                    {
                        Block(route, error);
                        return;
                    }
                }
                route.SetRunState(TransitRunState.WaitingForDriver);
                watches.Remove(route);
                lastIgnitionWarning.Remove(route);
                return;
            }

            // VehiclePawn.CanMoveFinal is NOT sufficient by itself to detect an
            // un-drafted / unignited engine. Ask VF's ignition controller before
            // attempting another path. Never start a vehicle without a driver.
            if (!VehicleAdapter.TryEnsureIgnition(vehicle, out string ignitionReason))
            {
                if (route.RunState == TransitRunState.Moving ||
                    route.RunState == TransitRunState.Recovering)
                    VehicleAdapter.TryStop(vehicle, out string unused);
                route.SetRunState(TransitRunState.WaitingForEngine);
                watches.Remove(route);
                // Keep the line enabled: the user can refuel, repair or start
                // the engine manually without recreating the route.
                if (!lastIgnitionWarning.TryGetValue(route, out string previous)
                    || previous != ignitionReason)
                {
                    lastIgnitionWarning[route] = ignitionReason;
                    Log.Warning("[Public Transit] Line '" + route.DisplayName
                        + "' waiting for engine: " + ignitionReason);
                }
                return;
            }
            lastIgnitionWarning.Remove(route);

            // After a save/load, avoid issuing a second path while VF is moving.
            if (route.RunState == TransitRunState.Recovering)
            {
                if (VehicleAdapter.TryIsMoving(vehicle, out bool stillMoving) && stillMoving
                    && route.DestinationCell.IsValid)
                {
                    route.SetRunState(TransitRunState.Moving);
                    var watch = new TravelWatch {
                        lastCell = vehicle.Position, lastProgressTick = tick, commandTick = tick };
                    watch.triedDockingCells.Add(route.DestinationCell);
                    watches[route] = watch;
                    return;
                }
                route.SetRunState(TransitRunState.WaitingForDriver);
            }

            if (route.RunState == TransitRunState.Moving)
            {
                CheckProgress(route, vehicle, tick);
                return;
            }
            if (route.RunState == TransitRunState.Dwelling && tick < route.DwellUntilTick)
                return;
            if (route.RunState == TransitRunState.Blocked) return;

            StartNextLeg(route, vehicle, tick);
        }

        private void StartNextLeg(TransitRoute route, Thing vehicle, int tick)
        {
            if (route.NextStopIndex < 0 || route.NextStopIndex >= route.Stops.Count)
                route.SetNextStopIndex(0);
            Building_TransitStop stop = route.Stops[route.NextStopIndex];
            if (IsBesideStop(vehicle, stop))
            {
                Arrive(route, tick);
                return;
            }
            if (!TryGetDockingCell(stop, vehicle, null, out IntVec3 destination))
            {
                Block(route, "no full-size parking space next to '" + stop.StopName + "'");
                return;
            }

            var watch = new TravelWatch {
                lastCell = vehicle.Position, lastProgressTick = tick, commandTick = tick };
            watch.triedDockingCells.Add(destination);
            if (!IssueLeg(route, vehicle, tick, destination, watch))
                Block(route, "VF rejected initial path to '" + stop.StopName + "'");
        }

        private bool IssueLeg(TransitRoute route, Thing vehicle, int tick, IntVec3 destination,
            TravelWatch watch)
        {
            if (!VehicleAdapter.TryStartPath(vehicle, destination, out string error))
            {
                Log.Warning("[Public Transit] " + error);
                return false;
            }
            watch.lastCell = vehicle.Position;
            watch.lastProgressTick = tick;
            watch.commandTick = tick;
            route.SetDestinationCell(destination);
            route.SetRunState(TransitRunState.Moving);
            watches[route] = watch;
            return true;
        }

        private void CheckProgress(TransitRoute route, Thing vehicle, int tick)
        {
            if (!route.DestinationCell.IsValid)
            {
                Block(route, "missing saved docking cell");
                return;
            }
            if (!VehicleAdapter.TryIsMoving(vehicle, out bool moving))
            {
                Block(route, "cannot read VehiclePathFollower.Moving");
                return;
            }
            Building_TransitStop stop = route.Stops[route.NextStopIndex];
            if (!moving && IsBesideStop(vehicle, stop))
            {
                Arrive(route, tick);
                return;
            }
            if (!watches.TryGetValue(route, out TravelWatch watch))
            {
                watch = new TravelWatch { lastCell = vehicle.Position,
                    lastProgressTick = tick, commandTick = tick };
                watch.triedDockingCells.Add(route.DestinationCell);
                watches[route] = watch;
            }
            if (!moving)
            {
                if (tick - watch.commandTick < PathStartGraceTicks)
                    return; // Give VF time to initialize or complete the current path.
                if (watch.retries < MaxDockingRetries &&
                    TryGetDockingCell(stop, vehicle, watch.triedDockingCells,
                        out IntVec3 alternate))
                {
                    watch.retries++;
                    watch.triedDockingCells.Add(alternate);
                    if (IssueLeg(route, vehicle, tick, alternate, watch))
                        return;
                }
                Block(route, "VF path ended; stop='" + stop.StopName
                    + "', vehicle-stop gap=" + DistanceFromBodyToStop(vehicle, stop)
                    + ", target=" + route.DestinationCell
                    + ", attempts=" + (watch.retries + 1));
                return;
            }
            if (watch.lastCell != vehicle.Position)
            {
                watch.lastCell = vehicle.Position;
                watch.lastProgressTick = tick;
            }
            else if (tick - watch.lastProgressTick > TravelTimeoutTicks)
            {
                Block(route, "vehicle has not changed cells for " + TravelTimeoutTicks
                    + " ticks near '" + stop.StopName + "'");
            }
        }

        private void Arrive(TransitRoute route, int tick)
        {
            route.SetRunState(TransitRunState.Dwelling);
            route.SetDwellUntilTick(tick + StopWaitTicks);
            route.SetDestinationCell(IntVec3.Invalid);
            route.SetNextStopIndex((route.NextStopIndex + 1) % route.Stops.Count);
            watches.Remove(route);
        }

        private void Block(TransitRoute route, string reason)
        {
            Stop(route);
            route.SetRunState(TransitRunState.Blocked);
            Log.Warning("[Public Transit] Line '" + route.DisplayName
                + "' stopped: " + reason);
            Messages.Message("PT_DriveBlockedMessage".Translate(route.DisplayName),
                MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>
        /// A station occupies its own tile. To dock, the vehicle's entire
        /// footprint must fit on passable cells and end next to that station.
        /// VF is still the final authority on reachability and movement.
        /// </summary>
        private bool TryGetDockingCell(Building_TransitStop stop, Thing vehicle,
            HashSet<IntVec3> excluded, out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            int radius = Math.Max(5, Math.Max(vehicle.def.size.x, vehicle.def.size.z) + 3);
            float best = float.MaxValue;
            IntVec3 station = stop.Position;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                IntVec3 root = new IntVec3(station.x + dx, 0, station.z + dz);
                if (!root.InBounds(map) || (excluded != null && excluded.Contains(root))) continue;
                CellRect footprint = GenAdj.OccupiedRect(root, vehicle.Rotation, vehicle.def.size);
                if (!footprint.InBounds(map)) continue;
                int gap = FootprintGap(footprint, station);
                // Prefer true side-adjacency. Allow one extra cell only if
                // no accessible adjacent placement is available.
                if (gap < 1 || gap > 2) continue;
                if (!FootprintCanPark(footprint, vehicle)) continue;
                float score = gap * 10000f
                    + root.DistanceToSquared(vehicle.Position)
                    + (dx * dx + dz * dz) * 0.02f;
                if (score < best) { best = score; destination = root; }
            }
            return destination.IsValid;
        }

        private bool FootprintCanPark(CellRect rect, Thing vehicle)
        {
            foreach (IntVec3 cell in rect.Cells)
            {
                if (!cell.InBounds(map) || !cell.Standable(map)) return false;
                foreach (Thing occupant in cell.GetThingList(map))
                    if (occupant != vehicle && VehicleAdapter.IsVehicle(occupant))
                        return false;
            }
            return true;
        }

        private static int FootprintGap(CellRect occupied, IntVec3 station)
        {
            int dx = station.x < occupied.minX ? occupied.minX - station.x
                : station.x > occupied.maxX ? station.x - occupied.maxX : 0;
            int dz = station.z < occupied.minZ ? occupied.minZ - station.z
                : station.z > occupied.maxZ ? station.z - occupied.maxZ : 0;
            return dx + dz;
        }

        private static int DistanceFromBodyToStop(Thing vehicle, Building_TransitStop stop)
        {
            return FootprintGap(vehicle.OccupiedRect(), stop.Position);
        }

        private static bool IsBesideStop(Thing vehicle, Building_TransitStop stop)
        {
            return DistanceFromBodyToStop(vehicle, stop) == 1;
        }

        private bool AllStopsValid(TransitRoute route)
        {
            foreach (Building_TransitStop stop in route.Stops)
                if (stop == null || stop.Destroyed || !stop.Spawned || stop.Map != map)
                    return false;
            return true;
        }
    }
}
