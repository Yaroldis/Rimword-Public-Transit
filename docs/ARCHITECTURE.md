# Architecture notes (RimWorld 1.6)

## Goal

Fixed-route public transit. Colonists eventually decide whether to walk or ride, wait at stops, board, transfer, leave at the right stop, and continue their original job.

## Implemented in 0.2.0-dev

- `Building_TransitStop`: map registration on spawn and deregistration before despawn, customizable persistent stop name, gizmos.
- `TransitMapComponent`: registered stops derived from live buildings (not saved), persistent routes saved as `LookMode.Deep`. Stops inside each route are saved as `LookMode.Reference`.
- `TransitRoute`: ordered stop list, add/remove/swap and custom name; `id` and serialization keys retained for older saves.
- `Dialog_TransitNetwork`: line editor UI; `Dialog_TransitName`, `Dialog_TransitConfirm`: small modal windows.
- `Languages/*/Keyed/PublicTransit.xml`: localized UI strings.

### Load order invariant

Do **not** prune route references in `MapComponent.FinalizeInit` before Scribe resolves cross-references. The only automatic repair of routes after loading is in `ExposeData` during `LoadSaveMode.PostLoadInit`. The stop registry may be reconstructed in `FinalizeInit` because it contains no serialized references.

### Lifecycle invariant

`Building_TransitStop.DeSpawn` notifies `TransitMapComponent` while `Map` is still available. The unregister logic removes that stop from all route lists. It does not destroy empty lines, avoiding accidental user data loss.

## Added in 0.3.0-dev: vehicle assignments

- `Core/VehicleAdapter`: readonly recognition of VF vehicles (`Vehicles.VehiclePawn`, including subclasses) via the runtime type inheritance chain. No VF DLL is copied or referenced at compile time. `CanAssign` requires a live, spawned, player-owned vehicle on this map.
- `TransitRoute.AssignedVehicle`: optional `Thing` reference, stored with `Scribe_References.Look` under new key `assignedVehicle`; existing save keys and data remain unchanged.
- `TransitMapComponent`: owns vehicle assignment changes; enforces one vehicle per line (and one line per vehicle), handles destroyed vehicle cleanup every 250 ticks, retains temporarily off-map references. Duplicates are repaired only in `PostLoadInit`.
- `Dialog_TransitNetwork`: assignment controls and availability/status information. A train/vehicle mod is not required for the static editor, but a VF content mod is required to see vehicles.
- No driving, pathfinding, passenger slots, or rail coupling integration yet. A vehicle is **assignable**, not necessarily a valid passenger shuttle; that check comes in a future stage.

### Compatibility

No Harmony patches are applied. The mod metadata still depends on VF and Harmony, but this stage does not access their APIs. The read-only vehicle adapter is isolated from routing and UI; future VF-specific movement commands must go there too.

## Vehicle Framework research checkpoint

Smash Phil discussed `LordJob_TravelWithVehicle` on 15 October 2026 as an experimental basis for several pawns with distinct jobs traveling toward a common destination. Its stability and applicability to multi-stop fixed routes in stable VF for RimWorld 1.6 remain to be tested. Keep it out of this stage.

## Not implemented

Schedules, passengers, commuting decisions, passenger eligibility, vehicle-aware docking planner, automatic driver jobs, drawing a route map overlay, cross-map network, or synchronization with group travel systems.

## Testing

See [docs/TEST_PLAN.ru.md](TEST_PLAN.ru.md).


## Experimental 0.4.0: vehicle movement (road vehicles only)

- `Routes/TransitRoute`: saves opt-in service switch, target index, state, dwell tick and destination. Old save keys remain unchanged.
- `Core/TransitVehicleController`: one map-local controller; state machine issues a new VF travel order only on state transition (every 30 ticks, not each tick). It does not assign any pawn Jobs.
- `Core/VehicleAdapter`: VF-only reflection functions for role occupancy, path-follower StartPath / StopDead, Moving, and CanMoveFinal. If an essential VF API is missing, service start fails. Never fake a driver or teleport a vehicle.
- `Core/TransitMapComponent`: is the only class allowed to enable/stop service. Route modification or invalid vehicle calls Stop first. Damage auto-unassignment still runs every 250 ticks.
- `UI/Dialog_TransitNetwork`: Start/Stop per route and status messages. Driver must board manually via standard VF controls.
- Trains of the Rim leaders may be assigned, but cannot be started in 0.4.0 (rail coupling/rotation not handled).
- Docking currently uses a simple adjacent-cell heuristic. Full vehicle footprint/road routing and stop platform placement are future improvements.

Safety rules: no driver = no movement; driver loss = stop command; inability to halt = no start; invalid stop or missing vehicle = stop; stalled path = block until manually restarted. If VF internals differ on a user's setup, treat this as experimental and report the exception/log, not as production-ready transit.
