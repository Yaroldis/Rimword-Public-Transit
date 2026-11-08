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
        // This flag migrates pre-0.5 saves, whose 1x1 signs had no rotation.
        private bool dockingDirectionInitialized = true;

        public string StopName => string.IsNullOrWhiteSpace(customName)
            ? "PT_DefaultStopName".Translate() + " (" + Position.x + ", " + Position.z + ")"
            : customName;

        /// <summary>
        /// Exactly one tile adjacent to the building is the boarding/docking
        /// marker. Rotation is RimWorld's native Rot4 and is saved by Thing.
        /// New stops are placed facing south in the ThingDef.
        /// </summary>
        public IntVec3 DockingSideCell
        {
            get
            {
                if (Rotation == Rot4.North) return Position + new IntVec3(0, 0, 1);
                if (Rotation == Rot4.East) return Position + new IntVec3(1, 0, 0);
                if (Rotation == Rot4.West) return Position + new IntVec3(-1, 0, 0);
                return Position + new IntVec3(0, 0, -1); // South
            }
        }

        private string DockingDirectionName
        {
            get
            {
                if (Rotation == Rot4.North) return "PT_DockingNorth".Translate().ToString();
                if (Rotation == Rot4.East) return "PT_DockingEast".Translate().ToString();
                if (Rotation == Rot4.West) return "PT_DockingWest".Translate().ToString();
                return "PT_DockingSouth".Translate().ToString();
            }
        }

        private void RotateDockingSide()
        {
            if (!Spawned || Map == null) return;
            Map currentMap = Map;
            // A live route must not keep driving towards an old parking cell.
            currentMap.GetComponent<TransitMapComponent>()?.StopRoutesUsingStop(this);
            Rotation = Rotation.Rotated(RotationDirection.Clockwise);
            currentMap.mapDrawer.MapMeshDirty(Position, MapMeshFlag.Things);
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (Spawned && DockingSideCell.InBounds(Map))
                GenDraw.DrawFieldEdges(new List<IntVec3> { DockingSideCell }, Color.cyan);
        }

        public void Rename(string newName)
        {
            if (!string.IsNullOrWhiteSpace(newName))
                customName = newName.Trim();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "PT_customName");
            Scribe_Values.Look(ref dockingDirectionInitialized,
                "PT_dockingDirectionInitialized", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && !dockingDirectionInitialized)
            {
                Rotation = Rot4.South;
                dockingDirectionInitialized = true;
            }
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
            string dockingText = "PT_DockingSide".Translate(DockingDirectionName,
                DockingSideCell.x, DockingSideCell.z);
            string combined = stopText + "\n" + dockingText;
            return string.IsNullOrEmpty(baseText) ? combined : baseText + "\n" + combined;
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
                defaultLabel = "PT_RotateDockingSide".Translate(),
                defaultDesc = "PT_RotateDockingSideDesc".Translate(),
                icon = icon,
                action = RotateDockingSide
            };

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
