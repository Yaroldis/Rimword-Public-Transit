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
        private const int PathInactiveRetryTicks = 360;
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
            public int inactiveSinceTick = -1;
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

            // With no shift assigned, preserve the old manual-VF boarding mode.
            // With a specific driver assigned, require THAT pawn in the driver seat.
            bool driverReady = route.AssignedDriver == null
                ? VehicleAdapter.HasActiveDriver(vehicle)
                : VehicleAdapter.IsPawnDriving(vehicle, route.AssignedDriver);
            if (!driverReady)
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
            if (IsDockedAtStop(vehicle, stop))
            {
                Arrive(route, tick);
                return;
            }
            if (!TryGetDockingCell(stop, vehicle, null, out IntVec3 destination))
            {
                Block(route, "no full-size parking space on the marked side of '" + stop.StopName + "'");
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
            watch.inactiveSinceTick = -1;
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
            if (IsDockedAtStop(vehicle, stop))
            {
                // The footprint being beside the stop is authoritative for
                // docking. VF can briefly be updating its internal path state.
                if (moving) VehicleAdapter.TryStop(vehicle, out string unused);
                Arrive(route, tick);
                return;
            }
            if (!watches.TryGetValue(route, out TravelWatch watch))
            {
                watch = new TravelWatch {
                    lastCell = vehicle.Position, lastProgressTick = tick,
                    commandTick = tick };
                watch.triedDockingCells.Add(route.DestinationCell);
                watches[route] = watch;
            }

            // Track real movement independently from the VF Moving flag.
            // Moving == false does not establish that a road is blocked:
            // the VF pathfinder can be calculating, interrupted, or temporarily
            // between path segments. Do not immediately issue another order.
            if (watch.lastCell != vehicle.Position)
            {
                watch.lastCell = vehicle.Position;
                watch.lastProgressTick = tick;
            }

            if (!moving)
            {
                if (watch.inactiveSinceTick < 0)
                    watch.inactiveSinceTick = tick;
                if (tick - watch.commandTick < PathStartGraceTicks
                    || tick - watch.inactiveSinceTick < PathInactiveRetryTicks)
                    return;

                TryRecoverLeg(route, vehicle, stop, tick, watch,
                    "VF Moving=false for " + (tick - watch.inactiveSinceTick) + " ticks");
                return;
            }

            watch.inactiveSinceTick = -1;
            if (tick - watch.lastProgressTick > TravelTimeoutTicks)
            {
                // VF still says it is moving, but the root cell has not
                // changed for a long time. Treat this as a stalled leg and
                // attempt bounded recovery instead of immediately blocking.
                TryRecoverLeg(route, vehicle, stop, tick, watch,
                    "position unchanged for " + (tick - watch.lastProgressTick) + " ticks");
            }
        }

        private void TryRecoverLeg(TransitRoute route, Thing vehicle,
            Building_TransitStop stop, int tick, TravelWatch watch, string reason)
        {
            if (watch.retries >= MaxDockingRetries)
            {
                Block(route, "VF travel did not reach stop='" + stop.StopName
                    + "'; vehicle-stop gap=" + DistanceFromBodyToStop(vehicle, stop)
                    + ", position=" + vehicle.Position
                    + ", target=" + route.DestinationCell
                    + ", attempts=" + (watch.retries + 1)
                    + ", last condition=" + reason
                    + "; VF state: " + VehicleAdapter.DescribeMovement(vehicle));
                return;
            }

            // First recover to the SAME docking cell. VF may have temporarily
            // stopped or discarded the current path. Choosing a new target
            // on every retry can itself cause unstable path requests.
            IntVec3 destination = route.DestinationCell;
            if (watch.retries > 0)
            {
                // Only the second retry tries a different valid parking cell.
                if (!TryGetDockingCell(stop, vehicle, watch.triedDockingCells,
                    out destination))
                {
                    Block(route, "no alternative docking cell on the marked side of '" + stop.StopName
                        + "' after VF stopped at " + vehicle.Position
                        + "; VF state: " + VehicleAdapter.DescribeMovement(vehicle));
                    return;
                }
                watch.triedDockingCells.Add(destination);
            }
            watch.retries++;
            Log.Warning("[Public Transit] Line '" + route.DisplayName
                + "' retry " + watch.retries + "/" + MaxDockingRetries
                + ": " + reason + "; pos=" + vehicle.Position
                + "; stop='" + stop.StopName + "' gap="
                + DistanceFromBodyToStop(vehicle, stop)
                + "; destination=" + destination + "; VF state: "
                + VehicleAdapter.DescribeMovement(vehicle));

            if (!VehicleAdapter.TryStop(vehicle, out string stopError))
            {
                Block(route, "VF could not halt stalled path: " + stopError);
                return;
            }
            if (!IssueLeg(route, vehicle, tick, destination, watch))
                Block(route, "VF rejected recovery route to '" + stop.StopName
                    + "' (" + reason + ")");
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
        /// Docking side is fixed by the stop's rotation (South by default).
        /// The vehicle footprint must contain the adjacent boarding marker cell,
        /// without overlapping the stop. Do not search the other three sides:
        /// a blocked boarding side is a genuine route-layout problem.
        /// </summary>
        private bool TryGetDockingCell(Building_TransitStop stop, Thing vehicle,
            HashSet<IntVec3> excluded, out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            IntVec3 marker = stop.DockingSideCell;
            if (!marker.InBounds(map) || !marker.Standable(map)) return false;

            int radius = Math.Max(5, Math.Max(vehicle.def.size.x, vehicle.def.size.z) + 3);
            int bestAlignment = int.MaxValue;
            // Deterministic tie-breaking: unlike the former algorithm, this
            // does not depend on which direction the vehicle approached from.
            IntVec3 station = stop.Position;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                IntVec3 root = new IntVec3(station.x + dx, 0, station.z + dz);
                if (!root.InBounds(map) || (excluded != null && excluded.Contains(root)))
                    continue;
                CellRect footprint = GenAdj.OccupiedRect(root, vehicle.Rotation, vehicle.def.size);
                if (!footprint.InBounds(map) || !FootprintOnDockingSide(footprint, stop))
                    continue;
                if (!FootprintCanPark(footprint, vehicle)) continue;

                // All eligible footprints contain the same marker cell. Prefer
                // the one whose center line matches the marker most closely.
                int alignment = (stop.Rotation == Rot4.South || stop.Rotation == Rot4.North)
                    ? Math.Abs(footprint.minX + footprint.maxX - 2 * station.x)
                    : Math.Abs(footprint.minZ + footprint.maxZ - 2 * station.z);
                if (alignment < bestAlignment)
                {
                    bestAlignment = alignment;
                    destination = root;
                }
            }
            return destination.IsValid;
        }

        private static bool FootprintOnDockingSide(CellRect footprint, Building_TransitStop stop)
        {
            IntVec3 station = stop.Position;
            IntVec3 marker = stop.DockingSideCell;
            if (!footprint.Contains(marker)) return false;
            if (stop.Rotation == Rot4.South)
                return footprint.maxZ == station.z - 1;
            if (stop.Rotation == Rot4.North)
                return footprint.minZ == station.z + 1;
            if (stop.Rotation == Rot4.East)
                return footprint.minX == station.x + 1;
            return footprint.maxX == station.x - 1; // West
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

        private static bool IsDockedAtStop(Thing vehicle, Building_TransitStop stop)
        {
            return FootprintOnDockingSide(vehicle.OccupiedRect(), stop);
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
