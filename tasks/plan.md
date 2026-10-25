# Implementation Plan: sapling-rust v1

## Overview
Original Bevy 0.16.x 3D ecosystem sim: 2000 procedural plants + 1000 herbivores at 60+ FPS. Pure-logic `sim.rs` (seeded, testable) + thin Bevy wiring in `main.rs`. Done = 8 SPEC rows green.

## Architecture Decisions
- `sim.rs` keeps no Bevy types (unit-testable without renderer; fastest path to parity checks).
- Instanced low-poly meshes (1 draw call per species-stage), fixed timestep 60Hz sim, seeded RNG (`seed=42` in tests).
- `ponytail: O(n²)` herbivore seek is v1 ceiling; spatial hash only if perf row fails past 5000 entities.
- No editors/predators/seasons in v1 (SPEC out of scope); CLI flags `--entities/--seconds` only.

## Dependency Graph
```
sim plants (T2) ─┐
                 ├─→ Bevy wiring (T4) ─→ parity tests (T5) ─→ perf+lint (T6)
sim herbivores ──┘          ↑
Bevy scaffold (T1) ─────────┘
```

Build order: T1 → T2 → T3 → checkpoint → T4 → T5 → checkpoint → T6.

## Task List

### Phase 1: Foundation
- [ ] T1: Bevy scaffold + procedural scene (todo.md#task-1)
- [ ] T2: Plant sim pure logic (todo.md#task-2)
- [ ] T3: Herbivore sim pure logic (todo.md#task-3)

### Checkpoint: Foundation
- [ ] `cargo test` green (T2+T3 unit tests), `cargo run --release` opens window with camera + HUD

### Phase 2: Core + Parity
- [ ] T4: Bevy wiring — instancing + fixed timestep + spawn 3000 (todo.md#task-4)
- [ ] T5: `tests/parity.rs` 6 mechanic rows (todo.md#task-5)

### Checkpoint: Core
- [ ] All parity tests green at seed=42, window sim visibly grows/spreads/eats/reproduces

### Phase 3: Perf + No-copy
- [ ] T6: Perf log + clippy + no-copy rg (todo.md#task-6)

### Checkpoint: Complete
- [ ] 8/8 SPEC rows green, `cargo clippy -- -D warnings` clean, ready for review

Tasks tracked in `tasks/todo.md` (default target). No external tracker.

## Risks and Mitigations
| Risk | Impact | Mitigation |
|---|---|---|
| Bevy 0.16 API churn vs installed toolchain | Med | Pin `0.16.x` in Cargo.toml; if resolve fails, fall back to 0.15 and note in SPEC |
| O(n²) seek misses 60 FPS at 3000 | High | Measure early in T4; instancing first, spatial hash only if p95 >16.7ms |
| Seeded determinism broken by Bevy schedule | Low | Keep RNG + logic in `sim.rs`; Bevy only calls `tick()` with fixed dt |

## Open Questions
- None blocking. Perf hardware = your PC (manual log in T6).
