using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PublicTransit.Buildings
{
    /// <summary>
    /// Displays the boarding side when the player is planning a stop,
    /// before the blueprint has been built. RimWorld supplies the proposed
    /// orientation via rot when Q/E is pressed.
    /// </summary>
    public sealed class PlaceWorker_TransitStopDocking : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center,
            Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map currentMap = Find.CurrentMap;
            if (currentMap == null) return;
            IntVec3 marker = Building_TransitStop.DockingSideCellFor(center, rot);
            if (marker.InBounds(currentMap))
                GenDraw.DrawFieldEdges(new List<IntVec3> { marker }, Color.cyan);
        }
    }
}
