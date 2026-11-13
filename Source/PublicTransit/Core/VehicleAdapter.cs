using System;
using System.Collections;
using System.Reflection;
using RimWorld;
using Verse;

namespace PublicTransit.Core
{
    /// <summary>
    /// Narrow bridge to Vehicle Framework 1.6: no hard reference to Vehicles.dll.
    /// All VF-specific introspection stays here, not in route/gameplay classes.
    /// </summary>
    public static class VehicleAdapter
    {
        private const string VehiclePawnFullName = "Vehicles.VehiclePawn";
        private const BindingFlags InstanceMembers = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly System.Collections.Generic.HashSet<Type> MissingHealthApiLogged =
            new System.Collections.Generic.HashSet<Type>();

        public static bool IsVehicle(Thing thing)
        {
            if (thing == null) return false;
            for (Type type = thing.GetType(); type != null; type = type.BaseType)
                if (type.FullName == VehiclePawnFullName) return true;
            return false;
        }

        /// <summary>
        /// Only independent powered units may be assigned to a route.
        /// Trains of the Rim implements *railcars* as VehiclePawn too: on the
        /// colony map they can even move on their own to ease positioning.
        /// Therefore CanMove alone would incorrectly include every railcar.
        /// This conservative mod-specific rule checks the train asset/def
        /// classification, not movement or fuel consumption.
        /// </summary>
        public static bool IsRouteLeader(Thing thing)
        {
            if (!IsVehicle(thing) || thing.def == null) return false;

            string modName = thing.def.modContentPack == null
                ? string.Empty : thing.def.modContentPack.Name ?? string.Empty;
            string packageId = thing.def.modContentPack == null
                ? string.Empty : thing.def.modContentPack.PackageId ?? string.Empty;
            string path = thing.def.graphicData == null
                ? string.Empty : thing.def.graphicData.texPath ?? string.Empty;
            string label = thing.def.label ?? string.Empty;
            string defName = thing.def.defName ?? string.Empty;

            bool trainMod = modName.IndexOf("Trains of the Rim", StringComparison.OrdinalIgnoreCase) >= 0
                || packageId.IndexOf("trainsoftherim", StringComparison.OrdinalIgnoreCase) >= 0
                || packageId.IndexOf("trains.of.the.rim", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("/Rail/", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!trainMod) return true;

            // Explicit rolling stock always loses, even if it can currently
            // be ordered to move around the map on its own.
            if (ContainsAny(path, "/Railcars/", "/Railcar/", "/RollingStock/", "/Wagons/")
                || ContainsAny(label, "wagon", "railcar", "rail car", "boxcar", "caboose",
                    "flatbed", "gondola", "hopper", "tanker", "passenger car", "вагон", "цистерна")
                || ContainsAny(defName, "Railcar", "Boxcar", "Caboose", "Flatbed",
                    "Gondola", "Hopper", "Tanker", "PassengerCar"))
                return false;

            // A conservative allow-list for locomotives. Unknown Trains of
            // the Rim vehicles are excluded until we classify them reliably.
            return ContainsAny(path, "/Locomotives/", "/Locomotive/")
                || ContainsAny(defName, "Locomotive", "TankEngine", "SteamEngine", "DieselEngine")
                || ContainsAny(label, "locomotive", "steam engine", "diesel engine", "паровоз", "тепловоз");
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (string token in tokens)
                if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool CanAssign(Thing thing, Map map)
        {
            return map != null && thing != null && !thing.Destroyed && thing.Spawned
                && thing.Map == map && thing.Faction == Faction.OfPlayer
                && IsRouteLeader(thing);
        }

        /// <summary>
        /// Reads VF component health. Other mods and VF versions may change
        /// implementation details; on an unknown API fail *without* removing
        /// the assignment and warn once instead of guessing from Thing.HitPoints.
        /// The method is called only for already-assigned vehicles every 250 ticks.
        /// </summary>
        public static bool TryGetWorstComponentHealth(Thing vehicle, out float fraction,
            out string componentLabel)
        {
            fraction = 1f;
            componentLabel = string.Empty;
            if (!IsVehicle(vehicle)) return false;

            try
            {
                object handler = ReadMember(vehicle, "statHandler", "StatHandler");
                object components = ReadMember(handler, "components", "Components");
                var enumerable = components as IEnumerable;
                if (enumerable == null) return HealthApiUnavailable(vehicle);

                bool any = false;
                foreach (object component in enumerable)
                {
                    if (component == null) continue;
                    float componentFraction;
                    if (!TryGetComponentFraction(component, out componentFraction))
                        return HealthApiUnavailable(vehicle);
                    any = true;
                    if (componentFraction < fraction)
                    {
                        fraction = componentFraction;
                        object props = ReadMember(component, "props", "Props");
                        object name = ReadMember(props, "label", "Label", "key", "Key");
                        componentLabel = name == null ? string.Empty : name.ToString();
                    }
                }
                if (!any) return HealthApiUnavailable(vehicle);
                return true;
            }
            catch (Exception ex)
            {
                WarnMissingHealthApi(vehicle, ex.GetType().Name);
                return false;
            }
        }

        private static bool TryGetComponentFraction(object component, out float fraction)
        {
            fraction = 1f;
            object pct = ReadMember(component, "HealthPercent", "healthPercent");
            if (TryFloat(pct, out fraction))
            {
                fraction = Math.Max(0f, Math.Min(1f, fraction));
                return true;
            }
            float health;
            float maximum;
            if (!TryFloat(ReadMember(component, "Health", "health"), out health)
                || !TryFloat(ReadMember(component, "MaxHealth", "maxHealth"), out maximum)
                || maximum <= 0f)
                return false;
            fraction = Math.Max(0f, Math.Min(1f, health / maximum));
            return true;
        }

        private static bool TryFloat(object value, out float result)
        {
            result = 0f;
            if (value == null) return false;
            try
            {
                result = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
                return !float.IsNaN(result) && !float.IsInfinity(result);
            }
            catch (InvalidCastException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }

        private static object ReadMember(object instance, params string[] names)
        {
            if (instance == null) return null;
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                foreach (string name in names)
                {
                    PropertyInfo property = type.GetProperty(name, InstanceMembers | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(instance, null);
                    FieldInfo field = type.GetField(name, InstanceMembers | BindingFlags.DeclaredOnly);
                    if (field != null) return field.GetValue(instance);
                }
            }
            return null;
        }

        private static bool HealthApiUnavailable(Thing vehicle)
        {
            WarnMissingHealthApi(vehicle, "unknown component layout");
            return false;
        }

        private static void WarnMissingHealthApi(Thing vehicle, string reason)
        {
            Type type = vehicle.GetType();
            if (MissingHealthApiLogged.Add(type))
                Log.Warning("[Public Transit] Cannot read Vehicle Framework component health for "
                    + type.FullName + " (" + reason + "). Automatic damage unassignment "
                    + "is disabled for this vehicle rather than using an unsafe guess.");
        }

        /// <summary>
        /// 0.4.0 supports VF road vehicles only. Trains remain assignable, but
        /// coupled locomotives/railcars need their own pathing adapter later.
        /// </summary>
        public static bool IsTrain(Thing thing)
        {
            if (!IsVehicle(thing) || thing.def == null) return false;
            string name = thing.def.modContentPack?.Name ?? string.Empty;
            string id = thing.def.modContentPack?.PackageId ?? string.Empty;
            return name.IndexOf("Trains of the Rim", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("trainsoftherim", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("trains.of.the.rim", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Only a living, conscious player pawn in a driver/locomotion role
        /// satisfies the 0.4 driver condition. No "remote/autopilot" fallback.
        /// The handler layout comes from VF's vehicle save format.
        /// </summary>
        public static bool HasActiveDriver(Thing vehicle)
        {
            if (!IsVehicle(vehicle)) return false;
            try
            {
                var handlers = ReadMember(vehicle, "handlers", "Handlers") as IEnumerable;
                if (handlers == null) return false; // fail closed
                foreach (object handler in handlers)
                {
                    if (handler == null || !IsDrivingRole(ReadMember(handler, "role", "Role")))
                        continue;
                    var pawns = ReadMember(handler, "thingOwner", "ThingOwner") as IEnumerable;
                    if (pawns == null) continue;
                    foreach (object occupant in pawns)
                    {
                        var pawn = occupant as Pawn;
                        if (pawn != null && !pawn.Dead && !pawn.Downed
                            && pawn.Faction == Faction.OfPlayer)
                            return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[Public Transit] Driver inspection failed: " + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 0.5: when a line has a specifically assigned driver, the mere presence
        /// of SOME driver in the vehicle must not count as fulfilling that shift.
        /// </summary>
        public static bool IsPawnDriving(Thing vehicle, Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || !IsVehicle(vehicle)) return false;
            try
            {
                var handlers = ReadMember(vehicle, "handlers", "Handlers") as IEnumerable;
                if (handlers == null) return false;
                foreach (object handler in handlers)
                {
                    if (handler == null || !IsDrivingRole(ReadMember(handler, "role", "Role")))
                        continue;
                    var aboard = ReadMember(handler, "thingOwner", "ThingOwner") as IEnumerable;
                    if (aboard == null) continue;
                    foreach (object occupant in aboard)
                        if (ReferenceEquals(occupant, pawn)) return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Public Transit] Assigned driver read failed: " + ex.GetType().Name);
            }
            return false;
        }

        /// <summary>
        /// Experimental 0.5 boarding integration. The JobDriver first walks to
        /// the vehicle. Only then use VF's own TryAddPawn(Pawn, VehicleRoleHandler)
        /// if the installed build exposes it; fail closed when it does not.
        /// No teleportation, manual ThingOwner manipulation or despawn hacks.
        /// </summary>
        public static bool TryBoardDriver(Pawn pawn, Thing vehicle, out string error)
        {
            error = null;
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted
                || !CanAssign(vehicle, pawn.Map) || IsTrain(vehicle))
            {
                error = "driver or vehicle unavailable";
                return false;
            }
            if (IsPawnDriving(vehicle, pawn)) return true;
            if (HasActiveDriver(vehicle))
            {
                error = "driver seat already occupied";
                return false;
            }
            try
            {
                var handlers = ReadMember(vehicle, "handlers", "Handlers") as IEnumerable;
                if (handlers == null)
                {
                    error = "VF vehicle handlers unavailable";
                    return false;
                }
                foreach (object handler in handlers)
                {
                    if (handler == null || !IsDrivingRole(ReadMember(handler, "role", "Role")))
                        continue;
                    object owner = ReadMember(handler, "thingOwner", "ThingOwner");
                    var occupants = owner as IEnumerable;
                    if (occupants == null) continue;
                    bool occupied = false;
                    foreach (object _ in occupants) { occupied = true; break; }
                    if (occupied) continue;

                    MethodInfo boarding = null;
                    foreach (MethodInfo method in vehicle.GetType().GetMethods(InstanceMembers))
                    {
                        if (method.Name != "TryAddPawn") continue;
                        ParameterInfo[] args = method.GetParameters();
                        if (args.Length == 2 && args[0].ParameterType.IsAssignableFrom(typeof(Pawn))
                            && args[1].ParameterType.IsInstanceOfType(handler))
                        {
                            boarding = method;
                            break;
                        }
                    }
                    if (boarding == null)
                    {
                        error = "VF TryAddPawn(Pawn, handler) API unavailable";
                        return false;
                    }
                    object result = boarding.Invoke(vehicle, new[] { (object)pawn, handler });
                    if (result is bool && !(bool)result)
                    {
                        error = "VF declined driver boarding";
                        return false;
                    }
                    if (!IsPawnDriving(vehicle, pawn))
                    {
                        error = "VF accepted boarding but driver role is not confirmed";
                        return false;
                    }
                    return true;
                }
                error = "no free VF driver role found";
                return false;
            }
            catch (Exception ex)
            {
                error = "VF boarding exception: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// End an assigned driver's shift using VF's own disembark operation.
        /// The Pawn is not removed from a ThingOwner or manually spawned here:
        /// doing so could corrupt crew assignments and saved games. VF versions
        /// with an unknown required parameter signature are rejected safely.
        /// </summary>
        public static bool TryDisembarkDriver(Thing vehicle, Pawn driver, out string error)
        {
            error = null;
            if (!IsVehicle(vehicle) || driver == null || vehicle.Destroyed
                || !vehicle.Spawned)
            {
                error = "vehicle or driver unavailable";
                return false;
            }
            if (!IsPawnDriving(vehicle, driver)) return true;

            try
            {
                MethodInfo disembark = null;
                string available = string.Empty;
                foreach (MethodInfo method in vehicle.GetType().GetMethods(InstanceMembers))
                {
                    if (method.Name != "DisembarkPawn") continue;
                    available += (available.Length == 0 ? string.Empty : "; ") + method;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 1 || parameters[0].ParameterType != typeof(Pawn))
                        continue;
                    bool supported = true;
                    for (int i = 1; i < parameters.Length; i++)
                        if (!parameters[i].IsOptional) supported = false;
                    if (supported && (disembark == null
                        || parameters.Length < disembark.GetParameters().Length))
                        disembark = method;
                }
                if (disembark == null)
                {
                    error = "VF DisembarkPawn overload unsupported; signatures="
                        + (available.Length == 0 ? "not found" : available)
                        + ". Use VF's manual disembark button.";
                    return false;
                }
                ParameterInfo[] argsInfo = disembark.GetParameters();
                object[] args = new object[argsInfo.Length];
                args[0] = driver;
                for (int i = 1; i < args.Length; i++)
                    args[i] = Type.Missing; // use the VF method's optional default
                object result = disembark.Invoke(vehicle, args);
                if (result is bool && !(bool)result)
                {
                    error = "VF refused to disembark driver";
                    return false;
                }
                if (IsPawnDriving(vehicle, driver))
                {
                    error = "VF disembark returned, but driver still occupies the seat";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "VF disembark exception: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static bool IsDrivingRole(object role)
        {
            if (role == null) return false;
            string value = role.ToString() ?? string.Empty;
            object key = ReadMember(role, "key", "Key", "label", "Label", "defName", "DefName");
            if (key != null) value += " " + key;
            // Conservatively recognize actual driving roles, not passenger roles.
            return ContainsAny(value, "driver", "pilot", "helmsman", "locomotive driver",
                "train engineer", "водитель", "машинист");
        }

        /// <summary>
        /// Verify the VF path follower supports both moving and halting before
        /// enabling service. Unsupported builds fail closed with a readable reason.
        /// </summary>
        public static bool SupportsDriving(Thing vehicle, out string error)
        {
            error = null;
            if (!IsVehicle(vehicle)) { error = "Not a VF vehicle"; return false; }
            try
            {
                object pather = ReadMember(vehicle, "vehiclePather", "VehiclePather");
                if (pather == null) { error = "VehiclePathFollower missing"; return false; }
                if (FindStartPath(pather.GetType()) == null)
                {
                    error = "VehiclePathFollower.StartPath not supported";
                    return false;
                }
                if (FindStop(pather.GetType()) == null)
                {
                    error = "VehiclePathFollower.StopDead not supported";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Cannot inspect VF movement API: " + ex.GetType().Name;
                return false;
            }
        }

        private static MethodInfo FindStartPath(Type type)
        {
            foreach (MethodInfo method in type.GetMethods(InstanceMembers))
            {
                if (method.Name != "StartPath") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || parameters.Length > 4) continue;
                if (parameters[0].ParameterType != typeof(IntVec3)
                    && parameters[0].ParameterType != typeof(LocalTargetInfo)) continue;
                if (parameters[1].ParameterType != typeof(Verse.AI.PathEndMode)) continue;
                bool optional = true;
                for (int i = 2; i < parameters.Length; i++)
                    if (!parameters[i].IsOptional) optional = false;
                if (optional) return method;
            }
            return null;
        }

        private static MethodInfo FindStop(Type type)
        {
            MethodInfo method = type.GetMethod("StopDead", InstanceMembers,
                null, Type.EmptyTypes, null);
            return method ?? type.GetMethod("Stop", InstanceMembers,
                null, Type.EmptyTypes, null);
        }

        /// <summary>
        /// Hand travel to RimWorld's job tracker rather than starting an
        /// orphaned VF path. The framework patches the standard Goto job for
        /// VehiclePawn; a bare VehiclePathFollower.StartPath can be cancelled
        /// later when its job tracker changes the current job.
        /// This method is deliberately limited to road vehicles in 0.4.3.
        /// </summary>
        public static bool TryStartPath(Thing vehicle, IntVec3 destination, out string error)
        {
            if (!SupportsDriving(vehicle, out error)) return false;
            var pawn = vehicle as Pawn;
            if (pawn == null)
            {
                error = "VF vehicle is not a Pawn";
                return false;
            }

            try
            {
                // Follow the same job pipeline as a player-ordered Goto.
                // Never call the VF pather directly from Public Transit.
                Verse.AI.Job job = Verse.JobMaker.MakeJob(
                    JobDefOf.Goto, new LocalTargetInfo(destination));
                job.playerForced = true;
                if (pawn.jobs == null)
                {
                    error = "VF vehicle has no job tracker";
                    return false;
                }
                // Diagnostic 0.5.4e: observe the actual JobCondition if VF
                // completes or replaces this Goto immediately. No job changes.
                TransitGotoDiagnostics.Begin(pawn, destination);
                Log.Message("[Public Transit][ReachDiag] from=" + vehicle.Position
                    + ", target=" + destination + ", "
                    + DescribeVehicleReachability(vehicle, destination));
                string beforeJob = DescribeCurrentJob(pawn);
                bool accepted = pawn.jobs.TryTakeOrderedJob(job);
                string afterJob = DescribeCurrentJob(pawn);
                Log.Message("[Public Transit][GotoDiag] requested=" + destination
                    + ", from=" + vehicle.Position
                    + ", accepted=" + accepted
                    + ", before={" + beforeJob + "}, immediatelyAfter={" + afterJob + "}");
                if (!accepted)
                {
                    error = "VF rejected the Goto order to " + destination
                        + "; current job=" + afterJob;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "VF Goto job request failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// Observational VF reachability probe for 0.5.4e only. It does not
        /// reject or alter a Goto based on its result. The VF region precheck
        /// and full pathfinder are separate systems; disagreement is useful
        /// diagnostic data but not proof that the destination is blocked.
        /// </summary>
        private static string DescribeVehicleReachability(Thing vehicle, IntVec3 destination)
        {
            try
            {
                Type utility = vehicle.GetType().Assembly.GetType(
                    "Vehicles.VehicleReachabilityUtility", false);
                if (utility == null) return "reachability=unavailable (VF utility not found)";

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static;
                foreach (MethodInfo method in utility.GetMethods(flags))
                {
                    if (method.Name != "CanReachVehicle") continue;
                    ParameterInfo[] p = method.GetParameters();
                    if (p.Length != 5 || !p[0].ParameterType.IsInstanceOfType(vehicle)
                        || p[1].ParameterType != typeof(LocalTargetInfo)
                        || p[2].ParameterType != typeof(Verse.AI.PathEndMode)
                        || p[3].ParameterType != typeof(Danger)
                        || p[4].ParameterType != typeof(TraverseMode)) continue;
                    object result = method.Invoke(null, new object[] {
                        vehicle, new LocalTargetInfo(destination),
                        Verse.AI.PathEndMode.OnCell, Danger.Deadly, TraverseMode.ByPawn });
                    return "reachability=" + (result is bool ? result.ToString() : "unknown")
                        + " (mode=OnCell/Danger.Deadly/ByPawn; diagnostic only)";
                }
                return "reachability=unavailable (VF signature differs)";
            }
            catch (Exception ex)
            {
                return "reachability=unavailable (" + (ex.InnerException ?? ex).GetType().Name
                    + ": " + (ex.InnerException ?? ex).Message + ")";
            }
        }

        /// <summary>
        /// The ordered-job API returning true does not mean the requested
        /// Goto has started. Keep the job check separate from the VF pather's
        /// Moving flag to distinguish a queued, rejected or interrupted job.
        /// </summary>
        public static bool TryInspectGoto(Thing vehicle, IntVec3 destination,
            out bool isExpectedGoto, out string details)
        {
            isExpectedGoto = false;
            var pawn = vehicle as Pawn;
            if (pawn == null || pawn.jobs == null)
            {
                details = "VF pawn job tracker unavailable";
                return false;
            }
            var current = pawn.CurJob;
            isExpectedGoto = current != null && current.def == JobDefOf.Goto
                && current.targetA.Cell == destination;
            details = DescribeCurrentJob(pawn);
            return true;
        }

        private static string DescribeCurrentJob(Pawn pawn)
        {
            if (pawn == null || pawn.CurJob == null) return "none";
            var job = pawn.CurJob;
            return (job.def == null ? "unknown" : job.def.defName)
                + ", targetA=" + job.targetA.Cell
                + ", playerForced=" + job.playerForced;
        }

        public static bool TryStop(Thing vehicle, out string error)
        {
            error = null;
            if (!IsVehicle(vehicle) || vehicle.Destroyed || !vehicle.Spawned) return true;
            if (!SupportsDriving(vehicle, out error)) return false;
            try
            {
                // Cancel our movement job as well as the pather. Otherwise
                // JobDriver_Goto can reissue a path on the next tick.
                var pawn = vehicle as Pawn;
                if (pawn != null && pawn.CurJob != null
                    && pawn.CurJob.def == JobDefOf.Goto)
                    pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);

                object pather = ReadMember(vehicle, "vehiclePather", "VehiclePather");
                FindStop(pather.GetType()).Invoke(pather, null);
                return true;
            }
            catch (Exception ex)
            {
                error = "VF stop failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        public static bool TryIsMoving(Thing vehicle, out bool moving)
        {
            moving = false;
            if (!IsVehicle(vehicle)) return false;
            try
            {
                object pather = ReadMember(vehicle, "vehiclePather", "VehiclePather");
                object value = ReadMember(pather, "Moving", "moving", "MovingNow", "movingNow");
                if (!(value is bool)) return false;
                moving = (bool)value;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[Public Transit] VF moving state read failed: " + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// Snapshot of VF movement state for troubleshooting, read ONLY when a
        /// leg needs recovery. Unknown VF members are reported as "?" rather
        /// than assumed to mean path failure. No state is modified here.
        /// </summary>
        public static string DescribeMovement(Thing vehicle)
        {
            if (!IsVehicle(vehicle)) return "not a VF vehicle";
            try
            {
                object pather = ReadMember(vehicle, "vehiclePather", "VehiclePather");
                if (pather == null) return "pather unavailable";
                object moving = ReadMember(pather, "Moving", "moving");
                object waiting = ReadMember(pather, "CalculatingPath", "calculatingPath",
                    "WaitingForPath", "waitingForPath", "Pathing", "pathing");
                object destination = ReadMember(pather, "Destination", "destination",
                    "dest", "curDest", "CurDest");
                object path = ReadMember(pather, "curPath", "CurPath", "currentPath");
                object failed = ReadMember(pather, "PatherFailed", "patherFailed",
                    "Failed", "failed");
                Pawn pawn = vehicle as Pawn;
                string jobName = pawn == null || pawn.CurJob == null || pawn.CurJob.def == null
                    ? "none" : pawn.CurJob.def.defName;
                return "job=" + jobName
                    + ", moving=" + (moving == null ? "?" : moving.ToString())
                    + ", calculating=" + (waiting == null ? "?" : waiting.ToString())
                    + ", destination=" + (destination == null ? "?" : destination.ToString())
                    + ", currentPath=" + (path == null ? "unknown/none" : "present")
                    + ", failed=" + (failed == null ? "?" : failed.ToString())
                    + ", canMove=" + CanMoveNow(vehicle);
            }
            catch (Exception ex)
            {
                return "VF movement inspection failed: " + ex.GetType().Name;
            }
        }

        /// <summary>
        /// VF controls ignition separately from its path follower. A path order
        /// must not be issued while the engine is switched off. Only uses
        /// explicit ignition-on / draft setters on VF's ignition controller;
        /// never starts a vehicle without its occupied driver seat.
        /// Returns false with a diagnostic if the specific VF build exposes
        /// no supported ignition setter. Manual ignition is still possible.
        /// </summary>
        public static bool TryEnsureIgnition(Thing vehicle, out string reason)
        {
            reason = null;
            if (!IsVehicle(vehicle)) { reason = "not a VF vehicle"; return false; }
            if (!HasActiveDriver(vehicle)) { reason = "no active driver"; return false; }
            try
            {
                object controller = ReadMember(vehicle, "ignition", "Ignition");
                if (controller == null)
                {
                    if (CanMoveNow(vehicle)) return true;
                    reason = "VF ignition controller unavailable";
                    return false;
                }

                // The draft state represents engine ignition for VF vehicles.
                // Other forks may expose an equivalent explicit property.
                object current = ReadMember(controller, "Drafted", "Ignited",
                    "IgnitionOn", "EngineOn", "IsIgnited");
                if (current is bool && !(bool)current)
                {
                    if (!TryWriteBoolMember(controller, true, "Drafted", "Ignited",
                        "IgnitionOn", "EngineOn"))
                    {
                        if (!TryInvokeIgnitionOn(controller))
                        {
                            reason = "VF ignition is off, but this version has no supported on-setter";
                            return false;
                        }
                    }
                }
                else if (!(current is bool))
                {
                    // Never blindly toggle an unknown controller state.
                    if (!CanMoveNow(vehicle))
                    {
                        reason = "VF ignition state unavailable; start the engine manually";
                        return false;
                    }
                }

                if (CanMoveNow(vehicle)) return true;
                reason = "ignition requested but VF reports unable to drive (fuel or components)";
                return false;
            }
            catch (Exception ex)
            {
                reason = "VF ignition failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// Explicit engine-off request for a parked Public Transit vehicle.
        /// Unlike a generic toggle, this operation is idempotent and never
        /// accidentally starts the engine. VF owns the actual ignition state.
        /// </summary>
        public static bool TrySwitchIgnitionOff(Thing vehicle, out string reason)
        {
            reason = null;
            if (!IsVehicle(vehicle) || vehicle.Destroyed || !vehicle.Spawned)
            {
                reason = "vehicle unavailable";
                return false;
            }
            try
            {
                object controller = ReadMember(vehicle, "ignition", "Ignition");
                if (controller == null)
                {
                    reason = "VF ignition controller unavailable";
                    return false;
                }
                object current = ReadMember(controller, "Drafted", "Ignited",
                    "IgnitionOn", "EngineOn", "IsIgnited");
                if (current is bool && !(bool)current) return true;
                if (!TryWriteBoolMember(controller, false, "Drafted", "Ignited",
                    "IgnitionOn", "EngineOn") && !TryInvokeIgnitionOff(controller))
                {
                    reason = "VF has no supported ignition-off setter";
                    return false;
                }
                object after = ReadMember(controller, "Drafted", "Ignited",
                    "IgnitionOn", "EngineOn", "IsIgnited");
                if (after is bool && (bool)after)
                {
                    reason = "VF still reports ignition on after shutdown request";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "VF ignition-off failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static bool TryInvokeIgnitionOff(object controller)
        {
            foreach (string name in new[] { "SetIgnition", "SetDrafted" })
            {
                MethodInfo method = controller.GetType().GetMethod(name, InstanceMembers,
                    null, new[] { typeof(bool) }, null);
                if (method == null) continue;
                method.Invoke(controller, new object[] { false });
                return true;
            }
            foreach (string name in new[] { "StopEngine", "TurnOff", "ShutDownEngine" })
            {
                MethodInfo method = controller.GetType().GetMethod(name, InstanceMembers,
                    null, Type.EmptyTypes, null);
                if (method == null) continue;
                method.Invoke(controller, null);
                return true;
            }
            return false;
        }

        private static bool TryWriteBoolMember(object instance, bool value, params string[] names)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                foreach (string name in names)
                {
                    PropertyInfo property = type.GetProperty(name, InstanceMembers | BindingFlags.DeclaredOnly);
                    if (property != null && property.PropertyType == typeof(bool)
                        && property.GetIndexParameters().Length == 0 && property.GetSetMethod(true) != null)
                    {
                        property.SetValue(instance, value, null);
                        return true;
                    }
                    // A public field is considered only if it is an explicit
                    // ignition state flag, never a generic VF private field.
                    FieldInfo field = type.GetField(name, InstanceMembers | BindingFlags.DeclaredOnly);
                    if (field != null && field.FieldType == typeof(bool)
                        && field.IsPublic && !field.IsInitOnly)
                    {
                        field.SetValue(instance, value);
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryInvokeIgnitionOn(object controller)
        {
            foreach (string name in new[] { "SetIgnition", "SetDrafted", "StartEngine", "Ignite" })
            {
                MethodInfo withFlag = controller.GetType().GetMethod(name, InstanceMembers,
                    null, new[] { typeof(bool) }, null);
                if (withFlag != null)
                {
                    withFlag.Invoke(controller, new object[] { true });
                    return true;
                }
                MethodInfo noArgs = controller.GetType().GetMethod(name, InstanceMembers,
                    null, Type.EmptyTypes, null);
                if (noArgs != null)
                {
                    noArgs.Invoke(controller, null);
                    return true;
                }
            }
            return false;
        }

        public static bool CanMoveNow(Thing vehicle)
        {
            try
            {
                object value = ReadMember(vehicle, "CanMoveFinal", "CanMove");
                // If a VF build lacks this property, do not pretend it can move.
                return value is bool && (bool)value;
            }
            catch (Exception ex)
            {
                Log.Warning("[Public Transit] VF CanMoveFinal read failed: " + ex.GetType().Name);
                return false;
            }
        }

    }
}
