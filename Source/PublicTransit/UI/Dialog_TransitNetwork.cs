using System;
using System.Collections.Generic;
using PublicTransit.Buildings;
using PublicTransit.Core;
using PublicTransit.Routes;
using RimWorld;
using UnityEngine;
using Verse;

namespace PublicTransit.UI
{
    /// <summary>
    /// A first, intentionally simple editor for a network of ordered stops.
    /// 0.5.0 adds an opt-in driver roster and experimental boarding job.
    /// </summary>
    public sealed class Dialog_TransitNetwork : Window
    {
        private readonly Map map;
        private Vector2 scrollPosition;

        public override Vector2 InitialSize => new Vector2(820f, 610f);

        public Dialog_TransitNetwork(Map map)
        {
            this.map = map;
            doCloseX = true;
            draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            TransitMapComponent network = map?.GetComponent<TransitMapComponent>();
            if (network == null)
            {
                Widgets.Label(inRect, "PT_NoMap".Translate());
                return;
            }

            Widgets.Label(new Rect(0f, 0f, inRect.width - 170f, 34f),
                "PT_NetworkSummary".Translate(network.Routes.Count, network.GetRegisteredStops().Count));
            if (Widgets.ButtonText(new Rect(inRect.width - 162f, 0f, 162f, 34f), "PT_CreateRoute".Translate()))
                NewRoute(network);

            Widgets.Label(new Rect(0f, 42f, inRect.width, 24f), "PT_NetworkHint".Translate());
            Rect scrollRect = new Rect(0f, 74f, inRect.width, inRect.height - 82f);
            float viewWidth = scrollRect.width - 20f;
            float height = 10f;
            foreach (TransitRoute route in network.Routes)
                height += 210f + Math.Max(1, route.Stops.Count) * 28f;
            height = Math.Max(scrollRect.height, height);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, new Rect(0f, 0f, viewWidth, height));
            float y = 6f;
            if (network.Routes.Count == 0)
                Widgets.Label(new Rect(8f, y, viewWidth - 16f, 42f), "PT_EmptyNetwork".Translate());

            // Snapshot: delete actions can mutate the collection while Unity renders UI.
            foreach (TransitRoute route in new List<TransitRoute>(network.Routes))
                DrawRoute(network, route, viewWidth, ref y);
            Widgets.EndScrollView();
        }

        private void NewRoute(TransitMapComponent network)
        {
            List<Building_TransitStop> available = network.GetRegisteredStops();
            if (available.Count == 0)
            {
                Messages.Message("PT_NoStops".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            string proposed = "PT_DefaultRouteName".Translate(network.Routes.Count + 1);
            Find.WindowStack.Add(new Dialog_TransitName("PT_CreateRoute".Translate(), proposed,
                name =>
                {
                    // The station is selected separately, not implicitly by an arbitrary sort order.
                    TransitRoute route = network.CreateRoute(name);
                    ChooseStop(network, route);
                }));
        }

        private void DrawRoute(TransitMapComponent network, TransitRoute route, float width, ref float y)
        {
            Widgets.Label(new Rect(8f, y + 5f, width - 300f, 30f),
                route.DisplayName + "  (" + route.Stops.Count + ")");
            if (Widgets.ButtonText(new Rect(width - 289f, y, 88f, 30f), "PT_Rename".Translate()))
            {
                Find.WindowStack.Add(new Dialog_TransitName("PT_RenameRoute".Translate(),
                    route.DisplayName, route.Rename));
            }
            if (Widgets.ButtonText(new Rect(width - 195f, y, 105f, 30f), "PT_AddStop".Translate()))
                ChooseStop(network, route);
            if (Widgets.ButtonText(new Rect(width - 84f, y, 80f, 30f), "PT_Delete".Translate()))
            {
                Find.WindowStack.Add(new Dialog_TransitConfirm(
                    "PT_DeleteRouteQuestion".Translate(route.DisplayName),
                    () => network.RemoveRoute(route)));
            }
            y += 38f;
            DrawVehicleAssignment(network, route, width, ref y);
            DrawDriverAssignment(network, route, width, ref y);

            if (route.Stops.Count == 0)
            {
                Widgets.Label(new Rect(28f, y, width - 30f, 26f), "PT_RouteNoStops".Translate());
                y += 28f;
            }
            else
            {
                // Iterate by index; handlers execute after controls are drawn, so a mutation
                // simply refreshes the list next frame instead of enumerator invalidation.
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    int stopIndex = i;
                    Building_TransitStop stop = route.Stops[i];
                    Widgets.Label(new Rect(28f, y, width - 182f, 26f),
                        (i + 1) + ". " + (stop == null ? "?" : stop.StopName));
                    if (Widgets.ButtonText(new Rect(width - 148f, y, 38f, 25f), "↑"))
                    {
                        network.StopService(route);
                        route.MoveStop(stopIndex, -1);
                    }
                    if (Widgets.ButtonText(new Rect(width - 106f, y, 38f, 25f), "↓"))
                    {
                        network.StopService(route);
                        route.MoveStop(stopIndex, 1);
                    }
                    if (Widgets.ButtonText(new Rect(width - 64f, y, 58f, 25f), "PT_Remove".Translate()))
                    {
                        network.StopService(route);
                        route.RemoveStop(stop);
                    }
                    y += 28f;
                }
            }
            y += 16f;
        }

        private void DrawVehicleAssignment(TransitMapComponent network, TransitRoute route, float width, ref float y)
        {
            Thing vehicle = route.AssignedVehicle;
            string vehicleName = vehicle == null ? "PT_NoVehicle".Translate().ToString() : vehicle.LabelCap;
            Widgets.Label(new Rect(28f, y + 2f, width - 330f, 26f),
                "PT_VehicleOnRoute".Translate(vehicleName));

            string actionLabel = vehicle == null ? "PT_AssignVehicle".Translate() : "PT_ChangeVehicle".Translate();
            if (Widgets.ButtonText(new Rect(width - 297f, y, 152f, 26f), actionLabel))
                ChooseVehicle(network, route);

            if (vehicle != null && Widgets.ButtonText(new Rect(width - 139f, y, 133f, 26f),
                "PT_UnassignVehicle".Translate()))
                network.UnassignVehicle(route);

            y += 30f;
            Widgets.Label(new Rect(28f, y + 1f, width - 34f, 23f),
                "PT_VehicleState".Translate(network.GetVehicleStatus(route)));
            y += 30f;

            string serviceAction = route.ServiceEnabled
                ? "PT_DriveStop".Translate().ToString()
                : "PT_DriveStart".Translate().ToString();
            if (Widgets.ButtonText(new Rect(28f, y, 172f, 27f), serviceAction))
            {
                if (route.ServiceEnabled) network.StopService(route);
                else if (!network.TryStartService(route, out string error))
                    Messages.Message(error, MessageTypeDefOf.RejectInput, false);
            }
            Widgets.Label(new Rect(214f, y + 2f, width - 224f, 26f),
                "PT_DriveManualDriverHint".Translate());
            y += 32f;
        }

        private static void DrawDriverAssignment(TransitMapComponent network, TransitRoute route,
            float width, ref float y)
        {
            Pawn driver = route.AssignedDriver;
            string name = driver == null ? "PT_NoDriverAssigned".Translate().ToString()
                : driver.LabelShortCap.ToString();
            Widgets.Label(new Rect(28f, y + 2f, width - 330f, 26f),
                "PT_DriverOnRoute".Translate(name));
            string change = driver == null ? "PT_AssignDriver".Translate().ToString()
                : "PT_ChangeDriver".Translate().ToString();
            if (Widgets.ButtonText(new Rect(width - 297f, y, 152f, 26f), change))
                ChooseDriver(network, route);
            if (driver != null && Widgets.ButtonText(new Rect(width - 139f, y, 133f, 26f),
                "PT_UnassignDriver".Translate()))
                network.UnassignDriver(route);
            y += 31f;
            Widgets.Label(new Rect(28f, y, width - 34f, 26f),
                driver == null ? "PT_DriverManualMode".Translate()
                    : PublicTransitMod.ExperimentalDriverBoardingEnabled
                        ? "PT_DriverJobHint".Translate()
                        : "PT_DriverJobDisabledHint".Translate());
            y += 31f;
        }

        private static void ChooseDriver(TransitMapComponent network, TransitRoute route)
        {
            var options = new List<FloatMenuOption>();
            foreach (Pawn pawn in network.GetAssignableDrivers(route))
            {
                Pawn candidate = pawn;
                options.Add(new FloatMenuOption(candidate.LabelShortCap, () =>
                {
                    if (!network.TryAssignDriver(route, candidate, out string error))
                        Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                }));
            }
            if (options.Count == 0)
            {
                Messages.Message("PT_NoAssignableDrivers".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void ChooseVehicle(TransitMapComponent network, TransitRoute route)
        {
            var options = new List<FloatMenuOption>();
            foreach (Thing vehicle in network.GetAssignableVehicles(route))
            {
                if (vehicle == route.AssignedVehicle) continue;
                Thing candidate = vehicle;
                string name = "PT_VehicleOption".Translate(candidate.LabelCap,
                    candidate.Position.x, candidate.Position.z);
                options.Add(new FloatMenuOption(name, () =>
                {
                    if (!network.TryAssignVehicle(route, candidate, out string error))
                        Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                }));
            }
            if (options.Count == 0)
            {
                Messages.Message("PT_NoAssignableVehicles".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void ChooseStop(TransitMapComponent network, TransitRoute route)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (Building_TransitStop stop in network.GetRegisteredStops())
            {
                if (route.ContainsStop(stop)) continue;
                Building_TransitStop candidate = stop;
                options.Add(new FloatMenuOption(candidate.StopName, () =>
                {
                    if (network.IsValidStop(candidate))
                    {
                        network.StopService(route);
                        route.AddStop(candidate);
                    }
                }));
            }
            if (options.Count == 0)
            {
                Messages.Message("PT_NoAvailableStops".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
