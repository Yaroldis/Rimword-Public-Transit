using System.Collections.Generic;
using PublicTransit.Core;
using Verse;
using Verse.AI;

namespace PublicTransit.Jobs
{
    /// <summary>
    /// First experimental driver Job. Walk to the player-assigned vehicle, then
    /// request a VF driver seat via its public/reflected boarding method.
    /// Driving itself stays on the vehicle's normal Goto job (0.4.3).
    /// </summary>
    public sealed class JobDriver_BoardTransitDriver : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // Reservation is enforced by our one-driver-per-line roster. VF may
            // use separate reservations for its own role handlers; don't fight them.
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil board = ToilMaker.MakeToil("PT_BoardTransitDriver");
            board.defaultCompleteMode = ToilCompleteMode.Instant;
            board.initAction = () =>
            {
                Thing vehicle = job.targetA.Thing;
                if (!StillAssignedToThisLine(vehicle))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (!VehicleAdapter.TryBoardDriver(pawn, vehicle, out string reason))
                {
                    // Keep the vehicle untouched if the installed VF release
                    // exposes an incompatible API. Players can board manually.
                    Log.Warning("[Public Transit] Could not board assigned driver "
                        + pawn.LabelShortCap + ": " + reason);
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            yield return board;
        }

        private bool StillAssignedToThisLine(Thing vehicle)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || vehicle == null)
                return false;
            var network = pawn.Map.GetComponent<TransitMapComponent>();
            if (network == null) return false;
            foreach (var route in network.Routes)
                if (route != null && route.ServiceEnabled
                    && route.AssignedVehicle == vehicle && route.AssignedDriver == pawn)
                    return true;
            return false;
        }
    }
}
