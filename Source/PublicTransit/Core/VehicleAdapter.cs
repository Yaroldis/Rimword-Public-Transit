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

        public static bool TryStartPath(Thing vehicle, IntVec3 destination, out string error)
        {
            if (!SupportsDriving(vehicle, out error)) return false;
            try
            {
                object pather = ReadMember(vehicle, "vehiclePather", "VehiclePather");
                MethodInfo method = FindStartPath(pather.GetType());
                ParameterInfo[] pars = method.GetParameters();
                object[] args = new object[pars.Length];
                args[0] = pars[0].ParameterType == typeof(IntVec3)
                    ? (object)destination : new LocalTargetInfo(destination);
                args[1] = Verse.AI.PathEndMode.OnCell;
                for (int i = 2; i < args.Length; i++)
                    args[i] = pars[i].DefaultValue;
                object outcome = method.Invoke(pather, args);
                if (outcome is bool && !(bool)outcome)
                {
                    error = "VehiclePathFollower rejected the path";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "VF path request failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        public static bool TryStop(Thing vehicle, out string error)
        {
            error = null;
            if (!IsVehicle(vehicle) || vehicle.Destroyed || !vehicle.Spawned) return true;
            if (!SupportsDriving(vehicle, out error)) return false;
            try
            {
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
