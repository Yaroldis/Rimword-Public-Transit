using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace PublicTransit.Core
{
    /// <summary>
    /// TEMPORARY 0.5.4e diagnostics. These Harmony prefixes observe jobs but
    /// never change a JobCondition, return false, or replace a job.
    /// Only a vehicle for which Public Transit recently requested Goto is traced.
    /// </summary>
    internal static class TransitGotoDiagnostics
    {
        private const int TraceDurationTicks = 6000;
        private const int MaxEventsPerRequest = 14;
        private static readonly Dictionary<Pawn, TraceWindow> windows =
            new Dictionary<Pawn, TraceWindow>();
        private static readonly FieldInfo trackerPawnField =
            typeof(Pawn_JobTracker).GetField("pawn",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static bool initialized;

        private sealed class TraceWindow
        {
            public IntVec3 destination;
            public int expires;
            public int events;
        }

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                MethodInfo end = FindTrackerMethod("EndCurrentJob", typeof(JobCondition));
                MethodInfo start = FindTrackerMethod("StartJob", typeof(Job), typeof(JobCondition));
                if (end == null || start == null || trackerPawnField == null)
                {
                    Log.Warning("[Public Transit][JobTrace] RimWorld job methods differ from the "
                        + "expected 1.6 signatures. Detailed job diagnostics disabled.");
                    return;
                }

                var harmony = new Harmony("rimtransit.publictransit.goto-diagnostics");
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
                harmony.Patch(end, prefix: new HarmonyMethod(
                    typeof(TransitGotoDiagnostics).GetMethod(nameof(EndCurrentJobPrefix), flags)));
                harmony.Patch(start, prefix: new HarmonyMethod(
                    typeof(TransitGotoDiagnostics).GetMethod(nameof(StartJobPrefix), flags)));
                Log.Message("[Public Transit][JobTrace] Read-only job diagnostics active "
                    + "for assigned VF vehicles (EndCurrentJob / StartJob).");
            }
            catch (Exception ex)
            {
                // A diagnostic feature must never prevent the mod from loading.
                Log.Warning("[Public Transit][JobTrace] Failed to install diagnostic hooks: "
                    + (ex.InnerException ?? ex).Message);
            }
        }

        private static MethodInfo FindTrackerMethod(string name, params Type[] firstParameters)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance;
            foreach (MethodInfo method in typeof(Pawn_JobTracker).GetMethods(flags))
            {
                if (method.Name != name) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < firstParameters.Length) continue;
                bool matches = true;
                for (int i = 0; i < firstParameters.Length; i++)
                    if (parameters[i].ParameterType != firstParameters[i]) matches = false;
                if (matches) return method;
            }
            return null;
        }

        public static void Begin(Pawn vehicle, IntVec3 destination)
        {
            if (vehicle == null || vehicle.jobs == null) return;
            int tick = Find.TickManager.TicksGame;
            // Avoid retaining stale destroyed pawns after long-running saves.
            var expired = new List<Pawn>();
            foreach (var item in windows)
                if (item.Key == null || item.Key.Destroyed || item.Value.expires < tick)
                    expired.Add(item.Key);
            foreach (Pawn pawn in expired) windows.Remove(pawn);

            windows[vehicle] = new TraceWindow
            {
                destination = destination,
                expires = tick + TraceDurationTicks,
                events = 0
            };
            Log.Message("[Public Transit][JobTrace] Watching vehicle=" + vehicle.LabelShort
                + ", tick=" + tick + ", requestedTarget=" + destination);
        }

        // Harmony positional parameter binding (__0, __1) avoids depending
        // on parameter names in the game's compiled assemblies.
        private static void EndCurrentJobPrefix(Pawn_JobTracker __instance,
            JobCondition __0)
        {
            if (!TryGetWindow(__instance, out TraceWindow window)) return;
            Job job = __instance.curJob;
            Write(window, "EndCurrentJob condition=" + __0 + ", current=" + JobName(job)
                + ", target=" + (job == null ? "none" : job.targetA.Cell.ToString()));
        }

        private static void StartJobPrefix(Pawn_JobTracker __instance,
            Job __0, JobCondition __1)
        {
            if (!TryGetWindow(__instance, out TraceWindow window)) return;
            Job prior = __instance.curJob;
            Write(window, "StartJob requested=" + JobName(__0)
                + ", target=" + (__0 == null ? "none" : __0.targetA.Cell.ToString())
                + ", lastJobEndCondition=" + __1 + ", previous=" + JobName(prior));
        }

        private static bool TryGetWindow(Pawn_JobTracker tracker, out TraceWindow window)
        {
            window = null;
            if (trackerPawnField == null || windows.Count == 0) return false;
            Pawn pawn = trackerPawnField.GetValue(tracker) as Pawn;
            if (pawn == null || !windows.TryGetValue(pawn, out window)) return false;
            if (Find.TickManager == null || Find.TickManager.TicksGame > window.expires)
            {
                windows.Remove(pawn);
                window = null;
                return false;
            }
            return window.events < MaxEventsPerRequest;
        }

        private static void Write(TraceWindow window, string details)
        {
            window.events++;
            Log.Message("[Public Transit][JobTrace] tick=" + Find.TickManager.TicksGame
                + ", requestedTarget=" + window.destination + ", " + details);
        }

        private static string JobName(Job job)
        {
            return job == null || job.def == null ? "none" : job.def.defName;
        }
    }
}
