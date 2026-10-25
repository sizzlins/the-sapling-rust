# Todo: sapling-rust v1

## Task 1: Bevy scaffold + procedural scene

**Description:** `cargo init` + Bevy 0.16.x dep, window opens with orbit camera, ground plane, procedural plant/herbivore placeholder meshes (cone/icosahedron, originals only), FPS HUD, `--entities/--seconds` CLI args parsed.

**Acceptance criteria:**
- [ ] `cargo run --release` opens 3D window, orbit camera works
- [ ] Zero files from `the_sapling_Data`; `rg -l "the_sapling|Assembly-CSharp" src` empty
- [ ] `--entities 3000 --seconds 60` accepted without panic

**Verification:**
- [ ] Build succeeds: `cargo build --release`
- [ ] Manual check: window + HUD visible for 10s

**Dependencies:** None

**Files likely touched:**
- `Cargo.toml`
- `src/main.rs`

**Estimated scope:** Small (1-2 files)

## Task 2: Plant sim pure logic

**Description:** `src/sim.rs` plants: grow (scale/energy += rate*dt), spread (seed within R on cooldown), mutate (child params drift <10%), die of age. Seeded RNG, no Bevy types.

**Acceptance criteria:**
- [ ] Growth mean +20–40% over 120 sim-sec at seed=42
- [ ] Spread creates sprouts within R ±15%
- [ ] Mutation drift >0 and <10% per gen; death occurs in 300 sim-sec run

**Verification:**
- [ ] Tests pass: `cargo test plant_`
- [ ] Build succeeds: `cargo build`

**Dependencies:** Task 1

**Files likely touched:**
- `src/sim.rs`

**Estimated scope:** Small (1 file)

## Task 3: Herbivore sim pure logic

**Description:** `src/sim.rs` herbivores: seek nearest plant (ponytail O(n²) v1), move, eat on contact (plant energy → herb energy), reproduce at energy threshold + cooldown (1 offspring), die of starvation/age.

**Acceptance criteria:**
- [ ] Contact → eat within ≤5 sim-sec at seed=42
- [ ] Repro yields exactly 1 offspring, cooldown honored
- [ ] Starvation death occurs when no plants in 300 sim-sec run

**Verification:**
- [ ] Tests pass: `cargo test herbivore_`
- [ ] Build succeeds: `cargo build`

**Dependencies:** Task 2

**Files likely touched:**
- `src/sim.rs`

**Estimated scope:** Small (1 file)

## Checkpoint: After Tasks 1-3
- [ ] All tests pass: `cargo test`
- [ ] Application builds without errors: `cargo build --release`
- [ ] Window opens, camera + placeholders work
- [ ] Review with human before proceeding

## Task 4: Bevy wiring — instancing + fixed timestep

**Description:** Wire `sim.rs` into Bevy fixed-timestep loop; render plants/herbivores as instanced low-poly meshes (1 draw call per group); spawn default 2000 plants + 1000 herbivores; HUD shows FPS + counts.

**Acceptance criteria:**
- [ ] 3000 entities spawn without panic on your PC
- [ ] Sim visibly progresses (growth/spread/eat/repro observable over 60s)
- [ ] p95 frame time measured and logged to stdout

**Verification:**
- [ ] Tests pass: `cargo test`
- [ ] Build succeeds: `cargo build --release`
- [ ] Manual check: `cargo run --release -- --entities 3000 --seconds 60` runs full duration

**Dependencies:** Tasks 1-3

**Files likely touched:**
- `src/main.rs`
- `src/sim.rs`

**Estimated scope:** Medium (2 files)

## Task 5: Parity tests (anti-cheap-out)

**Description:** `tests/parity.rs` implements SPEC rows 1–6 at seed=42 using only `sim.rs` (no renderer). Each row fails if its mechanic is stubbed.

**Acceptance criteria:**
- [ ] 6 tests map 1:1 to SPEC rows 1–6 with tolerances from SPEC
- [ ] All pass at seed=42; at least one fails if corresponding logic is commented out (spot-checked)

**Verification:**
- [ ] Tests pass: `cargo test --test parity`
- [ ] Build succeeds: `cargo build`

**Dependencies:** Tasks 2-4

**Files likely touched:**
- `tests/parity.rs`
- `src/sim.rs` (test-only tweaks if needed)

**Estimated scope:** Small (1-2 files)

## Checkpoint: After Tasks 4-5
- [ ] All tests pass: `cargo test`
- [ ] End-to-end sim works: spawn → grow/spread/eat/repro/death observable
- [ ] Review with human before proceeding

## Task 6: Perf + lint + no-copy gate

**Description:** 60s perf run at 3000 entities, record p95 frame time (target ≤16.7ms); `cargo clippy -- -D warnings` clean; `cargo fmt --check` clean; no-copy `rg` empty.

**Acceptance criteria:**
- [ ] Perf log shows p95 ≤16.7ms (60+ FPS steady) on your PC, or documents exact shortfall + next step
- [ ] `cargo clippy -- -D warnings` clean, `cargo fmt --check` clean
- [ ] `rg -l "the_sapling|Assembly-CSharp|decompil" src tests` returns nothing

**Verification:**
- [ ] Tests pass: `cargo test`
- [ ] Build succeeds: `cargo build --release`
- [ ] Manual check: paste perf log lines into review

**Dependencies:** Tasks 4-5

**Files likely touched:**
- (none, or `src/main.rs` perf-HUD tweak only)

**Estimated scope:** XS (0-1 files)

## Checkpoint: Complete
- [ ] All acceptance criteria met (8/8 SPEC rows)
- [ ] Ready for review (human approves before any v2 scope)
