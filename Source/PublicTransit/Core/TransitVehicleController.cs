using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using PublicTransit.Routes;
using RimWorld;
using Verse;

namespace PublicTransit.Core
{
    /// <summary>
    /// Controls a single automatically driven road vehicle per route.
    /// This class never boards a pawn or overrides their JobDriver.
    /// Tick checks are cheap (every 30 ticks) and vehicle commands run only on transitions.
    /// </summary>
    public sealed class TransitVehicleController
    {
        private const int StopWaitTicks = 240;
        private const int TravelTimeoutTicks = 1800;
        private readonly Map map;
        private readonly Dictionary<TransitRoute, TravelWatch> watches =
            new Dictionary<TransitRoute, TravelWatch>();

        private sealed class TravelWatch
        {
            public IntVec3 lastCell;
            public int lastProgressTick;
            public int commandTick;
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
            // Initial destination: nearest stop unless already parked next to it.
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

        /// <summary>Stop moving immediately; never delete the vehicle or its driver.</summary>
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
            foreach (Building_TransitStop stop in route.Stops)
            {
                if (stop == null || stop.Destroyed || !stop.Spawned || stop.Map != map)
                {
                    Block(route, "missing stop");
                    return;
                }
            }

            if (!VehicleAdapter.HasActiveDriver(vehicle))
            {
                // A driver exiting while underway must halt the vehicle.
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
                return;
            }
            if (!VehicleAdapter.CanMoveNow(vehicle))
            {
                Block(route, "VF reports vehicle cannot move (fuel, ignition or crew)");
                return;
            }

            // Save/load: do not issue a second path while VF is still driving.
            if (route.RunState == TransitRunState.Recovering)
            {
                bool moving;
                if (VehicleAdapter.TryIsMoving(vehicle, out moving) && moving
                    && route.DestinationCell.IsValid)
                {
                    route.SetRunState(TransitRunState.Moving);
                    watches[route] = new TravelWatch {
                        lastCell = vehicle.Position, lastProgressTick = tick, commandTick = tick };
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
            IntVec3 destination;
            if (!TryGetDockingCell(stop, vehicle, out destination))
            {
                Block(route, "no free docking cell near " + stop.StopName);
                return;
            }
            if (vehicle.Position == destination)
            {
                Arrive(route, tick);
                return;
            }
            if (!VehicleAdapter.TryStartPath(vehicle, destination, out string error))
            {
                Block(route, error);
                return;
            }
            route.SetDestinationCell(destination);
            route.SetRunState(TransitRunState.Moving);
            watches[route] = new TravelWatch {
                lastCell = vehicle.Position, lastProgressTick = tick, commandTick = tick };
        }

        private void CheckProgress(TransitRoute route, Thing vehicle, int tick)
        {
            if (!route.DestinationCell.IsValid)
            {
                Block(route, "missing saved docking cell");
                return;
            }
            bool moving;
            if (!VehicleAdapter.TryIsMoving(vehicle, out moving))
            {
                Block(route, "cannot read VehiclePathFollower.Moving");
                return;
            }
            // A vehicle may be stopped 1 cell short due to VF's footprint logic.
            if (!moving && vehicle.Position.DistanceToSquared(route.DestinationCell) <= 2)
            {
                Arrive(route, tick);
                return;
            }
            if (!moving)
            {
                if (watches.TryGetValue(route, out TravelWatch pending)
                    && tick - pending.commandTick <= 120)
                    return; // VF may still be initializing its path.
                Block(route, "path ended before reaching the stop");
                return;
            }
            if (!watches.TryGetValue(route, out TravelWatch watch))
            {
                watch = new TravelWatch { lastCell = vehicle.Position, lastProgressTick = tick };
                watches[route] = watch;
            }
            if (watch.lastCell != vehicle.Position)
            {
                watch.lastCell = vehicle.Position;
                watch.lastProgressTick = tick;
            }
            else if (tick - watch.lastProgressTick > TravelTimeoutTicks)
            {
                Block(route, "no progress on route for " + TravelTimeoutTicks + " ticks");
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
        /// A building's tile is not necessarily drivable. Pick a free nearby cell
        /// for a first test; VF will validate the actual route and footprint.
        /// This is NOT a full vehicle-aware docking planner yet.
        /// </summary>
        private bool TryGetDockingCell(Building_TransitStop stop, Thing vehicle,
            out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            float best = float.MaxValue;
            IntVec3 at = stop.Position;
            for (int dx = -4; dx <= 4; dx++)
            for (int dz = -4; dz <= 4; dz++)
            {
                int radiusSquared = dx * dx + dz * dz;
                if (radiusSquared < 4 || radiusSquared > 20) continue;
                IntVec3 cell = new IntVec3(at.x + dx, 0, at.z + dz);
                if (!cell.InBounds(map) || !cell.Standable(map)) continue;
                if (cell == at) continue;
                float distance = cell.DistanceToSquared(vehicle.Position);
                if (distance < best) { best = distance; destination = cell; }
            }
            return destination.IsValid;
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
