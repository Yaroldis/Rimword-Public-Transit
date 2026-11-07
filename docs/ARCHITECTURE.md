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

Schedules, passengers, commuting decisions, vehicle movement, passenger eligibility, dispatch, drawing a route map overlay, cross-map network, or synchronization with group travel systems.

## Testing

See [docs/TEST_PLAN.ru.md](TEST_PLAN.ru.md).
