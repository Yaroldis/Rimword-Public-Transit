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
        private const int LostGotoGraceTicks = 150;
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
            public bool observedGoto;
            public bool loggedFirstJobCheck;
            public int missingGotoSinceTick = -1;
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

            route.EndDriverBreak();
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
        /// Ends the active shift. Stopping the vehicle is followed by VF
        /// disembark and a separate explicit ignition-off request.
        /// </summary>
        public void Stop(TransitRoute route)
        {
            if (route == null) return;
            bool wasRunning = route.ServiceEnabled;
            Thing vehicle = route.AssignedVehicle;
            Pawn driver = route.AssignedDriver;
            bool stopped = true;
            if (wasRunning && vehicle != null)
            {
                stopped = VehicleAdapter.TryStop(vehicle, out string err);
                if (!stopped)
                    Log.Warning("[Public Transit] Unable to stop route '" + route.DisplayName + "': " + err);
            }
            // A line stop ends the selected driver's shift, not their permanent
            // assignment to the route. Cancel an unfinished boarding order, too.
            if (wasRunning && driver != null && driver.Spawned && driver.jobs != null
                && driver.CurJob != null && driver.CurJob.def != null
                && driver.CurJob.def.defName == "PT_BoardTransitDriver")
                driver.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);

            if (wasRunning && stopped && vehicle != null)
                EndShiftAtStop(route, vehicle, driver);

            route.EndDriverBreak();
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

            // A break keeps the service enabled, but prevents dispatch while
            // the driver is on foot eating, sleeping or recreating.
            if (route.DriverOnBreak)
            {
                route.SetRunState(TransitRunState.DriverBreak);
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
                    route.RunState == TransitRunState.PendingGoto ||
                    route.RunState == TransitRunState.Recovering)
                {
                    if (!VehicleAdapter.TryStop(vehicle, out string error))
                    {
                        Block(route, error);
                        return;
                    }
                }
                // The driver can also leave manually while a line is running.
                // Once the vehicle has stopped, don't keep consuming fuel.
                if (!VehicleAdapter.TrySwitchIgnitionOff(vehicle, out string shutdownReason)
                    && route.RunState != TransitRunState.WaitingForDriver)
                    Log.Warning("[Public Transit] Driver left but ignition could not be disabled: "
                        + shutdownReason);
                // If VF or the player made the driver leave during a planned
                // break trip, treat it as an early break rather than locking
                // the route forever in break-pending with no driver aboard.
                if (route.DriverBreakPending && route.AssignedDriver != null)
                {
                    route.BeginDriverBreak();
                    route.SetRunState(TransitRunState.DriverBreak);
                }
                else
                    route.SetRunState(TransitRunState.WaitingForDriver);
                watches.Remove(route);
                lastIgnitionWarning.Remove(route);
                return;
            }

            if (!route.DriverBreakPending && route.AssignedDriver != null
                && DriverNeedsPlanner.NeedsBreak(route.AssignedDriver,
                    PublicTransitMod.DriverBreakNeedThreshold))
            {
                if (!RequestNeedsBreak(route, vehicle, tick)) return;
            }

            // VehiclePawn.CanMoveFinal is NOT sufficient by itself to detect an
            // un-drafted / unignited engine. Ask VF's ignition controller before
            // attempting another path. Never start a vehicle without a driver.
            if (!VehicleAdapter.TryEnsureIgnition(vehicle, out string ignitionReason))
            {
                if (route.RunState == TransitRunState.Moving ||
                    route.RunState == TransitRunState.PendingGoto ||
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
                // A saved Goto may still be queued or awaiting VF pathing.
                // Reconnect to it rather than issuing a competing order.
                if (route.DestinationCell.IsValid
                    && VehicleAdapter.TryInspectGoto(vehicle, route.DestinationCell,
                        out bool pendingGoto, out string unusedJobState) && pendingGoto)
                {
                    route.SetRunState(TransitRunState.PendingGoto);
                    watches[route] = new TravelWatch {
                        lastCell = vehicle.Position, lastProgressTick = tick, commandTick = tick };
                }
                else
                    route.SetRunState(TransitRunState.WaitingForDriver);
            }

            if (route.RunState == TransitRunState.Moving ||
                route.RunState == TransitRunState.PendingGoto)
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
                Arrive(route, vehicle, tick);
                return;
            }
            if (!TryGetDockingCell(stop, vehicle, null, out IntVec3 destination))
            {
                Block(route, "no full-size parking space on the marked side of '" + stop.StopName + "'");
                return;
            }

            // Diagnostic only: compare the chosen parking anchor with the
            // actual rotation after a fully automatic arrival. Earlier versions
            // calculate the destination footprint using the vehicle's CURRENT
            // rotation, which can differ from the rotation at arrival.
            Log.Message("[Public Transit][DockDiag] depart: stop='" + stop.StopName
                + "', vehiclePos=" + vehicle.Position + ", vehicleRot=" + vehicle.Rotation
                + ", currentBody=" + vehicle.OccupiedRect()
                + ", stopPos=" + stop.Position + ", stopRot=" + stop.Rotation
                + ", marker=" + stop.DockingSideCell
                + ", chosenAnchor=" + destination
                + ", assumedArrivalBody="
                + GenAdj.OccupiedRect(destination, vehicle.Rotation, vehicle.def.size)
                + ", nextIndex=" + route.NextStopIndex);

            if (IsCloseEnoughToDock(vehicle, stop, destination)
                && VehicleAdapter.TryIsMoving(vehicle, out bool alreadyMoving) && !alreadyMoving)
            {
                Arrive(route, vehicle, tick);
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
            // Ordered Goto accepted does not prove that VF started driving.
            route.SetRunState(TransitRunState.PendingGoto);
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
            // VF's Goto can finish with the vehicle root one cell short of
            // its requested anchor. Accept that as arrival ONLY once the path
            // is idle, on the chosen side, and close to the intended target.
            // This prevents repeated Goto orders that can trap a large vehicle
            // near a stop. Exact side docking is accepted while moving too.
            if (IsDockedAtStop(vehicle, stop)
                || (!moving && IsCloseEnoughToDock(vehicle, stop, route.DestinationCell)))
            {
                if (!VehicleAdapter.TryStop(vehicle, out string stopError))
                {
                    Block(route, "unable to complete docking: " + stopError);
                    return;
                }
                Arrive(route, vehicle, tick);
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

            // Check the actual RimWorld job as well as VehiclePathFollower.
            // VF may abandon a Goto and switch to IdleVehicle/Wait while the
            // vehicle is still fully capable of moving (canMove=True).
            if (!VehicleAdapter.TryInspectGoto(vehicle, route.DestinationCell,
                out bool expectedGoto, out string jobDetails))
            {
                TryRecoverLeg(route, vehicle, stop, tick, watch,
                    "unable to inspect VF Goto job: " + jobDetails);
                return;
            }

            // Diagnostic 0.5.4a: first observation after the ordered Goto.
            // Issuing the order can succeed even if it ends before we tick.
            if (!watch.loggedFirstJobCheck)
            {
                watch.loggedFirstJobCheck = true;
                Log.Message("[Public Transit][GotoDiag] first check after "
                    + (tick - watch.commandTick) + " ticks; target=" + route.DestinationCell
                    + ", expectedGoto=" + expectedGoto + ", moving=" + moving
                    + ", pos=" + vehicle.Position + ", current={" + jobDetails + "}");
            }

            bool changedCell = watch.lastCell != vehicle.Position;
            if (changedCell)
            {
                watch.lastCell = vehicle.Position;
                watch.lastProgressTick = tick;
            }

            // A leg is really under way only once Goto targets our cell AND
            // VF reports movement (or we have observed a changed root cell).
            if (expectedGoto && (moving || changedCell))
            {
                watch.observedGoto = true;
                if (route.RunState == TransitRunState.PendingGoto)
                    route.SetRunState(TransitRunState.Moving);
            }

            if (!expectedGoto)
            {
                if (watch.missingGotoSinceTick < 0)
                    watch.missingGotoSinceTick = tick;
                int missingTicks = tick - watch.missingGotoSinceTick;
                if (tick - watch.commandTick >= PathStartGraceTicks
                    && missingTicks >= LostGotoGraceTicks)
                {
                    TryRecoverLeg(route, vehicle, stop, tick, watch,
                        (watch.observedGoto ? "Goto was replaced during travel" : "Goto never became active")
                        + "; current " + jobDetails + "; missing=" + missingTicks + " ticks");
                }
                return;
            }
            watch.missingGotoSinceTick = -1;

            if (route.RunState == TransitRunState.PendingGoto)
            {
                // A queued/starting Goto gets a bounded window to start moving.
                // This is not a road-block diagnosis.
                if (tick - watch.commandTick >= PathInactiveRetryTicks)
                    TryRecoverLeg(route, vehicle, stop, tick, watch,
                        "Goto accepted but never started moving; current " + jobDetails);
                return;
            }

            // Track real movement independently from the VF Moving flag.
            // Moving == false does not establish that a road is blocked:
            // the VF pathfinder can be calculating, interrupted, or temporarily
            // between path segments. Do not immediately issue another order.
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
            if (IsCloseEnoughToDock(vehicle, stop, route.DestinationCell))
            {
                if (!VehicleAdapter.TryStop(vehicle, out string nearStopError))
                {
                    Block(route, "cannot complete near-stop recovery: " + nearStopError);
                    return;
                }
                Arrive(route, vehicle, tick);
                return;
            }

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

        private bool RequestNeedsBreak(TransitRoute route, Thing vehicle, int tick)
        {
            int index = DriverNeedsPlanner.FindBreakStop(route, map, route.AssignedDriver,
                vehicle.Position,
                stop => IsDockedAtStop(vehicle, stop)
                    || TryGetDockingCell(stop, vehicle, null, out IntVec3 unused),
                out bool foodNearby);
            if (index < 0)
            {
                Block(route, "no dockable stop for exhausted driver");
                return false;
            }

            route.RequestDriverBreak(index);
            if (!foodNearby)
                Messages.Message("PT_DriverBreakNoFood".Translate(route.AssignedDriver.LabelShortCap),
                    MessageTypeDefOf.NegativeEvent, false);
            else
                Messages.Message("PT_DriverBreakRequested".Translate(route.AssignedDriver.LabelShortCap,
                    route.Stops[index].StopName), MessageTypeDefOf.NeutralEvent, false);

            // Already at the chosen stop: do not restart ignition just to
            // notice on the next tick that a break can start immediately.
            if (IsDockedAtStop(vehicle, route.Stops[index])
                || (route.NextStopIndex == index
                    && IsCloseEnoughToDock(vehicle, route.Stops[index], route.DestinationCell)))
            {
                route.SetNextStopIndex(index);
                Arrive(route, vehicle, tick);
                return false; // Arrive handled the break (or failed closed).
            }

            // If VF is already travelling to this stop, keep its successful
            // Goto job intact. Otherwise replace it with a new leg to the break stop.
            if (route.NextStopIndex != index)
            {
                if (!VehicleAdapter.TryStop(vehicle, out string error))
                {
                    Block(route, "could not divert vehicle for driver break: " + error);
                    return false;
                }
                route.SetNextStopIndex(index);
                route.SetDestinationCell(IntVec3.Invalid);
                route.SetRunState(TransitRunState.WaitingForDriver);
                watches.Remove(route);
            }
            return true;
        }

        private void Arrive(TransitRoute route, Thing vehicle, int tick)
        {
            // Diagnostic only: the actual footprint/rotation at the stop can
            // differ from the footprint assumed when the Goto was requested.
            if (route.NextStopIndex >= 0 && route.NextStopIndex < route.Stops.Count)
            {
                Building_TransitStop reached = route.Stops[route.NextStopIndex];
                Log.Message("[Public Transit][DockDiag] arrived: stop='" + reached.StopName
                    + "', vehiclePos=" + vehicle.Position + ", vehicleRot=" + vehicle.Rotation
                    + ", actualBody=" + vehicle.OccupiedRect()
                    + ", commandedAnchor=" + route.DestinationCell
                    + ", stopPos=" + reached.Position + ", stopRot=" + reached.Rotation
                    + ", marker=" + reached.DockingSideCell);
            }
            if (route.DriverBreakPending
                && route.NextStopIndex == route.DriverBreakStopIndex)
            {
                if (!VehicleAdapter.TryStop(vehicle, out string error))
                {
                    Block(route, "cannot stop for driver break: " + error);
                    return;
                }
                if (!EndShiftAtStop(route, vehicle, route.AssignedDriver))
                {
                    Block(route, "cannot finish driver break: VF exit or ignition failed");
                    return;
                }
                route.BeginDriverBreak();
                route.SetRunState(TransitRunState.DriverBreak);
                route.SetDestinationCell(IntVec3.Invalid);
                route.SetNextStopIndex((route.NextStopIndex + 1) % route.Stops.Count);
                watches.Remove(route);
                return;
            }
            route.SetRunState(TransitRunState.Dwelling);
            route.SetDwellUntilTick(tick + StopWaitTicks);
            route.SetDestinationCell(IntVec3.Invalid);
            route.SetNextStopIndex((route.NextStopIndex + 1) % route.Stops.Count);
            watches.Remove(route);
        }

        private static bool EndShiftAtStop(TransitRoute route, Thing vehicle, Pawn driver)
        {
            // VF alone performs seat exit; we never spawn or remove its pawn.
            bool disembarked = true;
            if (driver != null && VehicleAdapter.IsPawnDriving(vehicle, driver))
            {
                disembarked = VehicleAdapter.TryDisembarkDriver(vehicle, driver, out string error);
                if (!disembarked)
                    Log.Warning("[Public Transit] Driver exit failed on '"
                        + route.DisplayName + "': " + error);
            }
            bool ignitionOff = VehicleAdapter.TrySwitchIgnitionOff(vehicle, out string ignitionError);
            if (!ignitionOff)
                Log.Warning("[Public Transit] Engine shutdown failed on '"
                    + route.DisplayName + "': " + ignitionError);
            return disembarked && ignitionOff;
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

        /// <summary>
        /// VF may finish a Goto within one root cell of the destination.
        /// The vehicle must still be close to the stop; normally it also
        /// must be on the marked side. A legacy vehicle that was already on
        /// top of a passable stop is allowed to finish this one arrival and
        /// leave instead of retrying a path through the stop forever.
        /// New stops are impassable, preventing that overlap in normal play.
        /// </summary>
        private static bool IsCloseEnoughToDock(Thing vehicle,
            Building_TransitStop stop, IntVec3 requestedAnchor)
        {
            if (!requestedAnchor.IsValid) return false;
            IntVec3 actual = vehicle.Position;
            if (Math.Abs(actual.x - requestedAnchor.x) > 1
                || Math.Abs(actual.z - requestedAnchor.z) > 1)
                return false;
            CellRect body = vehicle.OccupiedRect();
            IntVec3 post = stop.Position;
            if (FootprintGap(body, post) > 2) return false;
            if (body.Contains(post)) return true; // legacy overlap: allow departure

            // First prefer the intended docking side and allow a one-cell
            // lateral offset, as in previous versions.
            bool onExpectedSide;
            if (stop.Rotation == Rot4.South)
                onExpectedSide = body.maxZ < post.z && post.x >= body.minX - 1
                    && post.x <= body.maxX + 1;
            else if (stop.Rotation == Rot4.North)
                onExpectedSide = body.minZ > post.z && post.x >= body.minX - 1
                    && post.x <= body.maxX + 1;
            else if (stop.Rotation == Rot4.East)
                onExpectedSide = body.minX > post.x && post.z >= body.minZ - 1
                    && post.z <= body.maxZ + 1;
            else
                onExpectedSide = body.maxX < post.x && post.z >= body.minZ - 1
                    && post.z <= body.maxZ + 1;
            if (onExpectedSide) return true;

            // VF may rotate the vehicle on the final path step. Our parking
            // anchor was chosen using the rotation at departure; a changed
            // final footprint can straddle the stop's X/Z line even though
            // VF reached the EXACT ordered destination. Do not issue another
            // Goto to the vehicle's current cell: treat it as a near-dock
            // arrival only if the body is within one tile of the boarding
            // marker and is not overlapping the stop itself.
            if (actual == requestedAnchor
                && FootprintGap(body, stop.DockingSideCell) <= 1)
            {
                Log.Message("[Public Transit][DockDiag] accepted anchor arrival with "
                    + "rotated body: anchor=" + requestedAnchor + ", rot=" + vehicle.Rotation
                    + ", body=" + body + ", marker=" + stop.DockingSideCell);
                return true;
            }
            return false;
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
