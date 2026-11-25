# sapling-rust: performance and stability patches for The Sapling

A BepInEx 5 plugin (`SaplingRustBridge.dll`) and small native library (`sapling_rust.dll`) for The Sapling (Unity 2017.4, legacy Mono). It fixes the late-game memory balloon, offscreen skinning overhead, time-jump freezes, and several vanilla simulation bugs.

## What this fixes

### 1. Memory balloon and out-of-memory crash on load

`GameSaver.Decode` rebuilds one `WorldState` for every simulated day (about 28.3 KB committed per state). At day 878,501 that allocates roughly 25 GB of historical states that `TimeLine.LiveADay()` immediately drops (`line[line.Count - 2] = null`).

A Harmony prefix on `GameSaver.GetWorldState` consumes the four `(count, values...)` blocks with `GetHighInteger()` to keep the byte stream aligned, returns `null` for historical entries, and keeps only the last two states.

| Metric | Before | After |
|---|---|---|
| Commit at loop end | 24,225 MB at index 800,000 | 1,627 MB at index 878,501 |
| `WorldState` objects built | 878,501 | 2 |
| `Decode` duration | 37 s, then crash | 716 ms |

> Note for maintainers: `GameSaver` derives from `MonoBehaviour`, but the game constructs it with `new GameSaver()`. That leaves a zero native pointer, so Unity's overloaded `==` reports it as `null`. Always check with `ReferenceEquals(__instance, null)`.

### 2. Offscreen skinning and animation overhead on distant and pooled rigs

Retail `OrganismObjectPool` parks dead organisms at `outOfSightLocation` with `SetActive(true)` (`outOfSightInsteadOfDisabling = true`). Enabling `SkinnedMeshRenderer.updateWhenOffscreen = true` or `AnimationCullingType.AlwaysAnimate` globally forces Unity to skin and animate tens of thousands of pooled and distant rigs every frame.

Newly built, distant, and pooled organisms now stay frustum-culled (`updateWhenOffscreen = false`, `AnimationCullingType.BasedOnRenderers`), and `OrganismObjectPool.HideNewSpecificGameObjects` resets them before parking. Only animals inside `CloseDistanceThreshold` (tunable live with F6) opt into `updateWhenOffscreen = true` and `AlwaysAnimate`.

### 3. Verified native distance band (`sapling_rust.dll`)

`sapling_rust.dll` is a zero-dependency Rust `cdylib` that exports `sapling_band_fill`, an exact port of `CellDistanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates`. It is verified byte-identical against the retail assembly across 943,552 cases (58,418,176 ints) by `tools/parity-harness/verify.ps1`, plus 20,000 live shadow-verified calls in-game before activating. Press F7 to toggle between native and retail at runtime.

### 4. Vanilla simulation and save-load bugs

Three vanilla bugs in `FastSimPatches.cs` break sexual species and custom-gravity saves over long runs:

1. `AnimalMorph.Clone` shallow-copies `InstinctDefinition` references and `AnimalBlender` shares `sexes` with the primary parent, so one male mutating an instinct overwrites every living male in the species (often locking males into `Attack` and collapsing mating). The bridge now deep-clones instincts and isolates the active sex morph.
2. `LivingAnimal` hardcodes `maximumNrOfChildren = 3` for all meat-eaters, giving asexual carnivores 3.0 daughters per parent but 50/50 sexual carnivores only 1.5 daughters per female. Strictly sexual meat-eaters now scale by `sexes` (`6` for 2-sex carnivores), while asexual carnivores stay at `3`.
3. `GameSaver.GetLivingAnimal` hardcodes `gravity = 1f` instead of `planet.gravity` on custom planets (`scenarioIndex == 999`), runs `UpdateStats` while `isChild = true` before restoring `age`, and never populates `hasCachedAdultMovementStats`. On reload, evolved low-gravity and metamorphic animals were left paralyzed at `canWalk = false, speed = 0`. A postfix now recomputes stats with the planet's gravity, correct adult state, and cached movement stats.

## Patch overview

| Class | What it does |
|---|---|
| `MemoryPatches` | Skips historical `WorldState` reconstruction on load; shares one `AnimalTextureFactory`; prunes extinct definitions every 100 days. |
| `LODPatches` | Scales `CloseDistanceThreshold` and `lodBias` to scene density; keeps `updateWhenOffscreen` tight (`75 m`); culls offscreen plant Animators (`<= 100 m`), disables shadows on LOD4 plant stubs, and resets pooled rigs before parking out of sight. |
| `FastSimPatches` | Zero-allocation, Boo-reflection-free replacements for `LivingPlant.LiveADay`, `LetAllPlantsInAreaLiveADay`, `SearchNearbyCellsForInstinctTrigger`, `CellContainsFoodTypeWithIndexForAnimal`, and `PredationCheck` (speeds up Time Jumps and cuts GC2 pauses), plus the sexual-instinct, 2-sex carnivore cap, and `GameSaver.GetLivingAnimal` fixes above. |
| `StreamingJITPatches` | Reuses prebuilt species models for mutants; fixes `AnimalInfo.hangingBabyFace` cyclic-serialization crash; adds exception-safe locking on `MutationTracker` (`HarmonyFinalizer`). |
| `SimulationPatches` | Replaces O(S²) boxed scans in `Notificator.ProcessNewState` with O(S) `HashSet` lookups; keeps calendar and night cycle synced with `dayIndex`. |
| `TimeJumpPatches` | Runs budgeted main-thread time jumps (`850 ms`/frame on jump screen, `12 ms` in-world) with zero-GC ping-pong `WorldState` buffers; fixes `GetYesterday` and `JumpAhead` edge cases. |
| `SteamPatches` | Defers per-day and per-predation Steam achievement IPC during time jumps and flushes once at jump end. |
| `BandPatches` | Fast-paths empty distances and routes `CellDistanceFinder` queries through `FillBandDirect` (cross-verified byte-identical to `sapling_rust.dll` at startup, with automatic retail fallback). |
| `CleanLogPatches` | Strips duplicate child `LODGroup`s, filters LOD and animation log spam, and guards vanilla `IndexOutOfRange` and `NullReference` crashes. |
| `ProfilerPatches` | Low-overhead profiler writing 5 s inclusive timing tables, frame p50/p95/max, GC counts, and scene census to the BepInEx log and `%LocalLow%\Wessel Stoop\The Sapling\sapling_profile.log`. |
| `RconServer` | Local TCP console on `127.0.0.1:27016` (read-only by default; mutations gated by `[Rcon] AllowMutation`). |

## Guides, archives, and tools

- [Sexual species guide](docs/sexual-species-guide.md): how to build and spawn self-sustaining sexual herbivores and obligate carnivores.
- `ArchivedAnimals/`: verified sexual animal templates (`Sex Herbivore.species16` and `Sex Obligate Carnivore.species16`).
- `saves/world-day-332622-final.sav16.zip`: archived Day 332,622 long-run save.
- `tools/sim-lab/`: headless `.sav16` runner, species inspector, and long-horizon simulation harness using the real retail assembly and `SaplingRustBridge`.

## Building, verifying, and deploying

```powershell
# 1. Verify native band parity (unit tests + release build + 943k-case oracle + mutation check)
powershell -File tools\parity-harness\verify.ps1

# 2. Build managed BepInEx plugin
dotnet build csharp\SaplingRustBridge\SaplingRustBridge.csproj -c Release

# 3. Deploy SaplingRustBridge.dll + sapling_rust.dll (cleans any stray native DLL from BepInEx\plugins)
powershell -File deploy_all.ps1
```
