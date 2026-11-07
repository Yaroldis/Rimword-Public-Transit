# Changelog

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
