using System;
using System.Collections.Generic;
using PublicTransit.Core;
using PublicTransit.UI;
using UnityEngine;
using Verse;

namespace PublicTransit.Buildings
{
    /// <summary>
    /// A map-registered, player-nameable stop. It does not move pawns or vehicles.
    /// The current map owns the stop registry and route definitions.
    /// </summary>
    public sealed class Building_TransitStop : Building
    {
        private string customName;

        public string StopName => string.IsNullOrWhiteSpace(customName)
            ? "PT_DefaultStopName".Translate() + " (" + Position.x + ", " + Position.z + ")"
            : customName;

        public void Rename(string newName)
        {
            if (!string.IsNullOrWhiteSpace(newName))
                customName = newName.Trim();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "PT_customName");
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<TransitMapComponent>()?.RegisterStop(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Cache the map: base.DeSpawn clears the reference.
            Map oldMap = Map;
            oldMap?.GetComponent<TransitMapComponent>()?.UnregisterStop(this);
            base.DeSpawn(mode);
        }

        public override string GetInspectString()
        {
            string baseText = base.GetInspectString();
            string stopText = "PT_StopInspectName".Translate(StopName);
            return string.IsNullOrEmpty(baseText) ? stopText : baseText + "\n" + stopText;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
                yield return gizmo;

            if (!Spawned)
                yield break;

            // Temporary icon; the final transit UI will have dedicated sprites.
            Texture2D icon = ContentFinder<Texture2D>.Get("Things/Building/TransitStop");
            yield return new Command_Action
            {
                defaultLabel = "PT_CreateRoute".Translate(),
                defaultDesc = "PT_CreateRouteDesc".Translate(),
                icon = icon,
                action = () =>
                {
                    Map currentMap = Map;
                    if (currentMap == null) return;
                    TransitMapComponent network = currentMap.GetComponent<TransitMapComponent>();
                    if (network == null) return;
                    string suggestedName = "PT_DefaultRouteName".Translate(network.Routes.Count + 1);
                    Find.WindowStack.Add(new Dialog_TransitName(
                        "PT_CreateRoute".Translate(), suggestedName,
                        name =>
                        {
                            if (!Spawned || Map != currentMap) return;
                            network.CreateRoute(name, this);
                            Find.WindowStack.Add(new Dialog_TransitNetwork(currentMap));
                        }));
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "PT_RenameStop".Translate(),
                defaultDesc = "PT_RenameStopDesc".Translate(),
                icon = icon,
                action = () => Find.WindowStack.Add(new Dialog_TransitName(
                    "PT_RenameStop".Translate(), StopName, Rename))
            };

            yield return new Command_Action
            {
                defaultLabel = "PT_OpenNetwork".Translate(),
                defaultDesc = "PT_OpenNetworkDesc".Translate(),
                icon = icon,
                action = () =>
                {
                    if (Map != null)
                        Find.WindowStack.Add(new Dialog_TransitNetwork(Map));
                }
            };
        }
    }
}
