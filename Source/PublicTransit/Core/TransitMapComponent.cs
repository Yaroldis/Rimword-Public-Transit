using System.Collections.Generic;
using PublicTransit.Routes;
using Verse;

namespace PublicTransit.Core
{
    /// <summary>
    /// One instance per RimWorld Map. Saves a list of routes with the settlement.
    /// Route UI, drivers, dispatching and pawn AI will be separate systems.
    /// </summary>
    public sealed class TransitMapComponent : MapComponent
    {
        private List<TransitRoute> routes = new List<TransitRoute>();

        public TransitMapComponent(Map map) : base(map) { }

        public IReadOnlyList<TransitRoute> Routes => routes;

        public TransitRoute CreateRoute(string displayName)
        {
            var route = new TransitRoute(displayName);
            routes.Add(route);
            return route;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref routes, "PT_routes", LookMode.Deep);

            if (routes == null)
                routes = new List<TransitRoute>();
        }
    }
}
