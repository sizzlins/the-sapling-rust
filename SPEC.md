# Spec: sapling-rust v1 — ecosystem sim

## ASSUMPTIONS I'M MAKING
1. Engine is Bevy 0.16.x (standard Rust engine; raw wgpu rejected as slower to build).
2. Primary target is Windows 10/11 64-bit exe, dev on your current PC.
3. All meshes procedural low-poly originals (cones/icosahedrons); zero files from `the_sapling_Data`.
4. Sim uses fixed timestep + seeded RNG so parity tests are deterministic.
5. "Same mechanic" = same *observable behavior within tolerance*, not identical code/frames.
6. Project root assumed `D:\put gitrepos here\sapling-rust` → correct me if wrong.
→ Correct me now or I proceed with these.

## Objective
Original Rust/Bevy 3D low-poly ecosystem sim inspired by The Sapling, proving Rust beats the Unity build's performance. v1 only: plants (grow/spread/mutate/die) + herbivores (seek/eat/move/reproduce/die). No editors, no predators/pollinators/seasons, no ripped assets/code (REA output = behavior notes only).

This spec answers "are we cheaping out?" with a mechanic checklist (§Success Criteria): every mechanic has an observable + tolerance + automated check. If a feature is missing, its check fails.

## Tech Stack
- Rust 1.78+ (verified 1.98.1), Cargo, Bevy 0.16.x, serde not needed v1
- No new deps beyond Bevy (ponytail ladder rung 5: already-installed/standard only)

## Commands
```
Build:     cargo build --release
Run:       cargo run --release
Test:      cargo test
Lint:      cargo clippy -- -D warnings
Perf run:  cargo run --release -- --entities 3000 --seconds 60
```

## Project Structure
```
SPEC.md          → this file (source of truth)
src/main.rs      → Bevy app, camera, spawning, perf HUD
src/sim.rs       → plants + herbivores tick (pure logic, no Bevy types where possible)
tests/parity.rs  → mechanic parity checks (seeded, ±tolerance)
```
Fewest files wins; no `render.rs`/`ecs/` abstractions until v2 forces it.

## Code Style
```rust
// ponytail: O(n²) seek is deliberate v1 ceiling; upgrade to spatial hash when >5000 entities.
pub fn tick_herbivores(plants: &mut [Plant], herbs: &mut [Herbivore], rng: &mut impl Rng) {
    for h in herbs.iter_mut() { /* seek nearest, eat, reproduce-or-die */ }
}
```
- `snake_case` fns, `PascalCase` types, `cargo fmt` clean, one `ponytail:` comment per deliberate shortcut with ceiling + upgrade path.

## Testing Strategy
- Framework: `cargo test` only, no harness. Deterministic seeds (e.g. `seed=42`).
- `tests/parity.rs` holds ONE check per mechanic row below; each fails if logic is stubbed/cheaped-out.
- Perf is a manual `cargo run --release` measurement (FPS log), not a CI assert, because frame time is hardware-specific.

## Boundaries
- Always: `cargo test` green before claiming done; validate CLI args (`--entities`, `--seconds`); keep sim deterministic under seed.
- Ask first: adding any dependency beyond Bevy; changing fixed timestep; adding editors/predators (v2 scope).
- Never: paste game code/assets or commit decompiled output; commit secrets; delete a failing parity test to get green; claim "same output" as frame-identical.

## Success Criteria (mechanic parity — anti-cheap-out list)
Baseline = v1 self-baseline (you can't play/log the original, so no manual numbers required). Defaults below lock on first green run; later runs must stay within tolerance = regression proof, not original-match. REA notes optional later to tune feel, never pasted code.

| # | Mechanic | Observable | Tolerance | Verify |
|---|---|---|---|---|
| 1 | Plant growth | mean plant scale +20–40% over 120 sim-sec from seed | within ±15% of baseline trend | `plant_growth_trend` |
| 2 | Plant spread | new sprouts within radius R of parent per 60 sim-sec | R ±15%, count ±20% | `plant_spread_radius` |
| 3 | Plant mutation | child size/growth-rate drift vs parent | drift >0 and <10% per gen | `plant_mutation_drift` |
| 4 | Herbivore seek+eat | herbivore moves toward nearest plant, plant energy drops | contact→eat in ≤5 sim-sec | `herbivore_eats` |
| 5 | Herbivore reproduce | energy>threshold + cooldown → 1 offspring | litter 1, cooldown ±15% | `herbivore_repro` |
| 6 | Death | plants die of age, herbs die of starvation | both occur in 300 sim-sec run | `death_occurs` |
| 7 | Perf | 3000 entities (2000 plants + 1000 herbs) steady FPS over 60s on your PC | p95 frame ≤16.7ms (60+ FPS) | manual perf log |
| 8 | No-copy | `rg -l "the_sapling|Assembly-CSharp|decompil" src tests` empty + all meshes procedural | exact | `cargo test` + rg |

Done = all 8 green + `cargo clippy` clean.

## Open Questions
1. RESOLVED (can't baseline): using self-baseline defaults above; no action needed.
2. RESOLVED: perf load = 3000 entities (2000 plants + 1000 herbivores) representing original's "thousands but slow" case.
3. RESOLVED: Bevy 0.16.x.
4. RESOLVED: spec location ok.

Approve this spec to proceed to Plan, or request changes.
