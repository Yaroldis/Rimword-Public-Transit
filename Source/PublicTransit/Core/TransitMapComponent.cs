using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using PublicTransit.Routes;
using RimWorld;
using Verse.AI;
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
        // Do not spam VF boarding attempts when a mod build lacks the required API.
        private readonly Dictionary<Pawn, int> nextBoardAttemptTick = new Dictionary<Pawn, int>();
        private TransitVehicleController driving;
        private TransitVehicleController Driving => driving ??
            (driving = new TransitVehicleController(map));

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
            {
                if (route == null || !route.ContainsStop(stop)) continue;
                Driving.Stop(route); // A service must not pursue a demolished stop.
                route.RemoveStop(stop);
            }
        }

        /// <summary>
        /// Stop running services before the orientation and docking marker move.
        /// Route definitions and vehicle assignments remain intact.
        /// </summary>
        public void StopRoutesUsingStop(Building_TransitStop stop)
        {
            if (stop == null || routes == null) return;
            foreach (TransitRoute route in routes)
                if (route != null && route.ContainsStop(stop) && route.ServiceEnabled)
                    Driving.Stop(route);
        }

        public TransitRoute CreateRoute(string displayName, Building_TransitStop firstStop = null)
        {
            if (routes == null) routes = new List<TransitRoute>();
            var route = new TransitRoute(displayName);
            if (IsValidStop(firstStop)) route.AddStop(firstStop);
            routes.Add(route);
            return route;
        }

        public bool RemoveRoute(TransitRoute route)
        {
            if (route == null || routes == null || !routes.Contains(route)) return false;
            Driving.Stop(route);
            return routes.Remove(route);
        }

        public bool TryStartService(TransitRoute route, out string error)
        {
            error = null;
            if (route == null || routes == null || !routes.Contains(route))
            {
                error = "PT_VehicleRouteMissing".Translate().ToString();
                return false;
            }
            if (route.AssignedVehicle != null && IsVehicleOverDamageThreshold(route.AssignedVehicle))
            {
                error = "PT_VehicleTooDamagedToAssign".Translate().ToString();
                return false;
            }
            return Driving.TryEnable(route, out error);
        }

        public void StopService(TransitRoute route)
        {
            if (route != null && routes != null && routes.Contains(route))
                Driving.Stop(route);
        }

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
                if (VehicleAdapter.CanAssign(thing, map)
                    && !IsVehicleOverDamageThreshold(thing)
                    && !AssignedToAnotherRoute(thing, targetRoute))
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


        /// <summary>
        /// Opt-in driver roster: never recruit an arbitrary colonist automatically.
        /// A pawn can be assigned to at most one line, regardless of vehicle type.
        /// </summary>
        public List<Pawn> GetAssignableDrivers(TransitRoute targetRoute)
        {
            var result = new List<Pawn>();
            if (targetRoute == null || routes == null || !routes.Contains(targetRoute)
                || map?.mapPawns == null) return result;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (CanBeTransitDriver(pawn) && !DriverAssignedElsewhere(pawn, targetRoute))
                    result.Add(pawn);
            }
            result.Sort((a, b) => string.Compare(a.LabelShortCap.ToString(),
                b.LabelShortCap.ToString(), StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        private bool DriverAssignedElsewhere(Pawn pawn, TransitRoute except)
        {
            foreach (TransitRoute route in routes)
                if (route != null && route != except && route.AssignedDriver == pawn)
                    return true;
            return false;
        }

        public bool TryAssignDriver(TransitRoute route, Pawn pawn, out string error)
        {
            error = null;
            if (route == null || routes == null || !routes.Contains(route))
                error = "PT_VehicleRouteMissing".Translate().ToString();
            else if (!CanBeTransitDriver(pawn))
                error = "PT_DriverUnavailable".Translate().ToString();
            else if (DriverAssignedElsewhere(pawn, route))
                error = "PT_DriverAlreadyAssigned".Translate().ToString();
            if (error != null) return false;
            Driving.Stop(route); // Driver change may not happen during travel.
            route.SetAssignedDriver(pawn);
            return true;
        }

        public void UnassignDriver(TransitRoute route)
        {
            if (route == null || routes == null || !routes.Contains(route)) return;
            Driving.Stop(route);
            route.SetAssignedDriver(null);
            // Do not forcibly eject an existing crew member; let VF handle disembarking.
        }

        private bool CanBeTransitDriver(Pawn pawn)
        {
            return pawn != null && !pawn.Dead && !pawn.Downed && pawn.Spawned
                && pawn.Map == map && pawn.Faction == Faction.OfPlayer
                && pawn.RaceProps.Humanlike && !pawn.IsPrisoner;
        }

        /// <summary>
        /// An assigned, available and idle colonist receives a normal RimWorld Job.
        /// Never interrupt combat, medical work or other ongoing player tasks.
        /// The boarding JobDriver walks up to the vehicle; VF owns the actual seat.
        /// </summary>
        private void DispatchDriverJobs()
        {
            if (routes == null || !PublicTransitMod.ExperimentalDriverBoardingEnabled) return;
            JobDef jobDef = DefDatabase<JobDef>.GetNamedSilentFail("PT_BoardTransitDriver");
            if (jobDef == null) return;
            foreach (TransitRoute route in routes)
            {
                if (route == null || !route.ServiceEnabled || route.AssignedVehicle == null)
                    continue;
                Pawn pawn = route.AssignedDriver;
                if (!CanBeTransitDriver(pawn) || pawn.Drafted || pawn.InMentalState)
                    continue;
                Thing vehicle = route.AssignedVehicle;
                if (!VehicleAdapter.CanAssign(vehicle, map) || VehicleAdapter.IsTrain(vehicle)
                    || IsVehicleOverDamageThreshold(vehicle)
                    || VehicleAdapter.HasActiveDriver(vehicle)) continue;
                // Do not overwrite an existing task, including a manually issued VF boarding task.
                Job current = pawn.CurJob;
                if (current != null && (current.def == null ||
                    (current.def != JobDefOf.Wait
                        && current.def.defName != "Wait_Wander"))) continue;
                if (pawn.jobs == null) continue;
                int now = Find.TickManager.TicksGame;
                if (nextBoardAttemptTick.TryGetValue(pawn, out int due) && now < due)
                    continue;
                // One attempt at most every 2,500 ticks (especially useful if VF
                // exposes a different boarding API and this Job must fail closed).
                nextBoardAttemptTick[pawn] = now + 2500;
                Job boarding = JobMaker.MakeJob(jobDef, new LocalTargetInfo(vehicle));
                boarding.playerForced = true;
                pawn.jobs.TryTakeOrderedJob(boarding);
            }
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
            if (IsVehicleOverDamageThreshold(vehicle))
            {
                error = "PT_VehicleTooDamagedToAssign".Translate();
                return false;
            }
            if (AssignedToAnotherRoute(vehicle, route))
            {
                error = "PT_VehicleAlreadyAssigned".Translate();
                return false;
            }

            Driving.Stop(route); // Changing vehicles while moving must halt the old one.
            route.SetAssignedVehicle(vehicle);
            return true;
        }

        public void UnassignVehicle(TransitRoute route)
        {
            if (route != null && routes != null && routes.Contains(route))
            {
                Driving.Stop(route);
                route.SetAssignedVehicle(null);
            }
        }

        /// <summary>
        /// Off-map vehicles retain their assignment (including caravans).
        /// On-map assignments are automatically cleared when a VF component
        /// loses at least the user-selected fraction of its maximum HP.
        /// The pawn/vehicle is NOT destroyed; repair is still possible.
        /// </summary>
        private void RepairVehicleAssignments(bool checkActiveVehicles)
        {
            if (routes == null) return;
            var seen = new HashSet<Thing>();
            foreach (TransitRoute route in routes)
            {
                if (route == null || route.AssignedVehicle == null) continue;
                Thing vehicle = route.AssignedVehicle;
                if ((checkActiveVehicles && vehicle.Destroyed) || !VehicleAdapter.IsVehicle(vehicle)
                    || !seen.Add(vehicle))
                {
                    if (checkActiveVehicles) Driving.Stop(route);
                    route.SetAssignedVehicle(null);
                    continue;
                }
                // Avoid inspecting transient Thing state while Scribe resolves references.
                if (!checkActiveVehicles || !vehicle.Spawned || vehicle.Map != map)
                    continue;

                if (!VehicleAdapter.IsRouteLeader(vehicle) || vehicle.Faction != Faction.OfPlayer)
                {
                    if (checkActiveVehicles) Driving.Stop(route);
                    route.SetAssignedVehicle(null);
                    continue;
                }

                float fraction;
                string component;
                if (VehicleAdapter.TryGetWorstComponentHealth(vehicle, out fraction, out component)
                    && 1f - fraction >= PublicTransitMod.DamageUnassignThreshold)
                {
                    Driving.Stop(route);
                    route.SetAssignedVehicle(null);
                    int percent = (int)Math.Round((1f - fraction) * 100f);
                    Messages.Message("PT_VehicleAutoUnassignedDamage".Translate(
                        vehicle.LabelCap, route.DisplayName, percent),
                        MessageTypeDefOf.RejectInput, false);
                }
            }
        }

        private static bool IsVehicleOverDamageThreshold(Thing vehicle)
        {
            float fraction;
            string ignored;
            return VehicleAdapter.TryGetWorstComponentHealth(vehicle, out fraction, out ignored)
                && 1f - fraction >= PublicTransitMod.DamageUnassignThreshold;
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            // Only once per 250 game ticks, never for every pawn every tick.
            int tick = Find.TickManager.TicksGame;
            if (tick % 250 == 0)
            {
                RepairVehicleAssignments(true);
                foreach (TransitRoute route in routes)
                    if (route?.AssignedDriver != null && route.AssignedDriver.Dead)
                    {
                        Driving.Stop(route);
                        route.SetAssignedDriver(null);
                    }
                DispatchDriverJobs();
            }
            if (tick % 30 == 0 && routes != null)
                foreach (TransitRoute route in routes)
                    if (route != null && route.ServiceEnabled)
                        Driving.TickRoute(route, tick);
        }

        public string GetVehicleStatus(TransitRoute route)
        {
            Thing vehicle = route?.AssignedVehicle;
            if (vehicle == null) return "PT_VehicleStatusNone".Translate();
            if (vehicle.Destroyed) return "PT_VehicleStatusDestroyed".Translate();
            if (!VehicleAdapter.IsVehicle(vehicle)) return "PT_VehicleStatusInvalid".Translate();
            if (!VehicleAdapter.IsRouteLeader(vehicle)) return "PT_VehicleStatusNotLeader".Translate();
            if (!vehicle.Spawned || vehicle.Map != map) return "PT_VehicleStatusOffMap".Translate();
            if (vehicle.Faction != Faction.OfPlayer) return "PT_VehicleStatusNotOwned".Translate();
            if (IsVehicleOverDamageThreshold(vehicle)) return "PT_VehicleStatusDamaged".Translate();
            if (route.Stops.Count < 2) return "PT_VehicleStatusNeedStops".Translate();
            if (VehicleAdapter.IsTrain(vehicle)) return "PT_DriveTrainPending".Translate();
            switch (route.RunState)
            {
                case TransitRunState.WaitingForDriver: return "PT_DriveWaitingDriver".Translate();
                case TransitRunState.WaitingForEngine: return "PT_DriveWaitingEngine".Translate();
                case TransitRunState.Moving:
                    string stopName = route.NextStopIndex >= 0 && route.NextStopIndex < route.Stops.Count
                        ? route.Stops[route.NextStopIndex]?.StopName ?? "?" : "?";
                    return "PT_DriveMovingTo".Translate(stopName);
                case TransitRunState.Dwelling: return "PT_DriveDwelling".Translate();
                case TransitRunState.Recovering: return "PT_DriveRecovering".Translate();
                case TransitRunState.Blocked: return "PT_DriveBlocked".Translate();
                default: return "PT_DriveStopped".Translate();
            }
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
