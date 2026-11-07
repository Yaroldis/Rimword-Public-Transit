# Architecture notes (RimWorld 1.6)

## Goal

A fixed-route public transit system. Colonists independently decide whether to walk or ride, wait at stops, board, transfer, leave the vehicle at their stop, and continue their existing RimWorld job.

## Current status

This repository is **only the bootstrap**. It implements:

- a loadable mod assembly (after compilation),
- a placeable transit-stop marker (`PT_TransitStop`),
- a per-map persisted route collection (`TransitMapComponent`),
- a route object with persistent references to station buildings (`TransitRoute`).

It does **not** automate vehicle movement, create a route UI, board passengers, or modify pawn jobs.

## Future boundaries

1. **Core:** routes, stops, service schedules and route search; no dependency on concrete vehicle types.
2. **Vehicle adapter:** calls to *verified* Vehicle Framework APIs for drivers, movement, capacity, boarding and alighting.
3. **Passenger AI:** decides whether to walk, wait or ride, without disrupting original pawn jobs or reservations.
4. **UI:** map overlays and a route/vehicle management window.

## Vehicle Framework research checkpoint

Smash Phil discussed `LordJob_TravelWithVehicle` on 15 October 2026 as an experimental basis for several pawns with distinct jobs traveling toward a common destination. We have **not** yet established availability, stability or multi-stop behavior for RimWorld 1.6's stable VF build. Do not add a compile-time VF reference or patch its private fields until the precise version and API are inspected.

## Invariants to protect

- Stop references must not be deep-serialized: they are world Things, saved by reference.
- Routes are map-scoped: never reuse a route object across unrelated maps.
- The vehicle's movement and the passenger's job are different state machines.
- Cancelled jobs, destroyed vehicles, absent stops and changed priorities must not trap pawns in permanent waiting.
- No expensive global path searches on every game tick.

## Next milestone

Compile locally and in CI, enable dependencies, start a development colony, place two stops, save and reload without errors. After that, implement route creation UI and inspect VF API.
