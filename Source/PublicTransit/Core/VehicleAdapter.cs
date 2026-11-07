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
    }
}
