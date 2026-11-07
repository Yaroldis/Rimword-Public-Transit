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
