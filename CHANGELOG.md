## 0.4.2-dev — Path recovery hardening (2026-11-07)

- Fix a premature route-stop condition: `VehiclePathFollower.Moving == false` is no longer treated as proof of a blocked road. Wait for sustained inactivity before reissuing a path.
- A first recovery retries the same destination; only the second retry selects another footprint-safe docking cell.
- Stalled movement (VF still reports Moving but the vehicle position does not change for 1800 ticks) also uses bounded recovery.
- Emit a movement snapshot when recovering or blocking a route, including current position, destination, vehicle-stop gap, and best-effort VF state fields.
- Driver presence, ignition logic, damage cutoffs, and train restrictions remain unchanged.
- This is an experimental fix for observed 0.4.1 behavior. GitHub Actions and real RimWorld/VF 1.6.2144 tests are still required.

## 0.4.1-dev — Docking and ignition hotfix (2026-11-07)

- Road vehicles now select a parking anchor using their entire rotated footprint, preferring cells immediately adjacent to a stop rather than the nearest pawn-walkable cell.
- Arrival uses the distance between the vehicle's occupied rectangle and the stop, not the vehicle origin versus a target root.
- When VF ends a path before docking, the dispatcher makes at most two attempts with different docking cells, then stops with detailed diagnostics.
- An occupied driver seat is still mandatory. The adapter now attempts to enable ignition using VF's ignition controller, and waits without falsely reporting a blocked road if VF cannot start the engine.
- The engine is **not forcibly switched off** when the line stops; this stays under player/VF control.
- This version is experimental. VF 1.6.2144 API behavior and in-game movement require testing via CI and RimWorld.

## 0.4.0-dev — experimental driver-required road vehicle movement

- Opt-in Start/Stop button for each route; boarding a driver is still manual via VF.
- `TransitVehicleController`: per-line state machine (waiting for driver / moving / dwelling / blocked).
- Adapter attempts to drive via VF's `vehiclePather.StartPath`, with compatible signature discovery and fail-closed feature detection; requires a supported stop method.
- A vehicle is never dispatched without an occupied driver role; loss of the driver attempts to stop the vehicle immediately.
- Halt routes on edits, vehicle removal, damage threshold, or lost stop; pause instead of issuing infinite move orders.
- Persist service status and destination through RimWorld saves; handle restoration conservatively.
- Rail locomotives/railcars remain assignable where permitted by 0.3.1, but no automatic train driving yet.
- Experimental: intended for verification with VF 1.6.2144 and Vanilla Vehicles Expanded. Actual in-game driving is **not verified**.

# Changelog

## 0.3.1-dev

- Do not offer Trains of the Rim railcars as route-leading vehicles; locomotives remain eligible via conservative train classification.
- Use VF component health (not Pawn/Thing generic HP): if any component has lost at least the configured percent, automatically free the route without destroying the vehicle.
- Add a user-configurable damage threshold (default: 50%) under Public Transit mod settings.
- Prevent manual assignment of heavily damaged vehicles. Emit a one-time warning when VF component health cannot be inspected; fail without detaching a vehicle.
- Continue to retain off-map assignments; update RU/EN messages and test plan.

## 0.3.0-dev

- Vehicle adapter detects player-owned on-map `Vehicles.VehiclePawn` instances (including derived classes from vehicle content mods) without bundling VF assemblies.
- Assign, change or unassign one vehicle per transit line from the network editor; enforce uniqueness across routes.
- Save a Thing reference to the chosen vehicle using Scribe; preserve old 0.2.0 save keys.
- Clean up references to destroyed vehicles periodically, retain off-map assignments, repair duplicate references after loading.
- Show assignment status, support English and Russian, add a 0.3.0 in-game regression plan.
- Does not automate driving, railway traffic, passenger roles, boarding or capacity checks.

## 0.2.0-dev

- Registrations of spawned transit stops in map-scoped in-memory registry.
- Custom persistent names for stops.
- Basic route management window: create, rename, delete, append/remove/reorder stops.
- Remove de-spawned stops from route references; repair bad references on post-load.
- Keyed English and Russian UI translations.
- Flattened CI artifact to avoid nested ZIP files.
- Still no automatic vehicles, AI, or schedules.

## 0.1.0-dev

- Basic loadable mod, stop definition, routes data classes, CI for RimWorld 1.6.
