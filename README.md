# sapling-rust — performance & stability patches for The Sapling

A BepInEx 5 plugin (`SaplingRustBridge.dll`) + minimal native library (`sapling_rust.dll`) for
**The Sapling** (Unity 2017.4, legacy Mono) that fixes the late-game memory balloon, offscreen
skinning overhead, time-jump freezes, and vanilla runtime bugs.

---

## What this fixes

### 1. The 10–32 GB RAM balloon and out-of-memory crash on load — *measured*

`GameSaver.Decode` rebuilds **one `WorldState` per simulated day** (~28.3 KB committed per state).
At day 878,501 that allocates ~25 GB of historical states that `TimeLine.LiveADay()` immediately
discards anyway (`line[line.Count - 2] = null`).

**Fix:** a Harmony prefix on `GameSaver.GetWorldState` consumes the four `(count, values…)` blocks
with `GetHighInteger()` to keep the stream aligned, returns `null` for historical entries, and keeps
the last two.

| | before | after |
|---|---|---|
| commit at loop end | 24,225 MB @ index 800,000 | **1,627 MB @ index 878,501** |
| `WorldState` objects built | 878,501 | **2** |
| `Decode` duration | 37 s, then crash | **716 ms** |

> **Trap for future maintainers:** `GameSaver` derives from `MonoBehaviour` but the game constructs
> it with `new GameSaver()`. Such an object has a zero native pointer, so Unity's overloaded `==`
> reports it as `null`. Always use `ReferenceEquals(__instance, null)`.

### 2. Offscreen skinning & animation overhead on distant and pooled rigs

Retail's `OrganismObjectPool` parks dead organisms at `outOfSightLocation` with `SetActive(true)`
(`outOfSightInsteadOfDisabling = true`). Setting `SkinnedMeshRenderer.updateWhenOffscreen = true`
or `AnimationCullingType.AlwaysAnimate` globally forces Unity to skin and evaluate tens of thousands
of pooled/distant rigs every frame.

**Fix:** newly built, distant, and pooled organisms stay frustum-culled (`updateWhenOffscreen = false`,
`AnimationCullingType.BasedOnRenderers`), and `OrganismObjectPool.HideNewSpecificGameObjects` resets
them before parking. Only animals inside `CloseDistanceThreshold` (tunable live with **F6**) opt into
`updateWhenOffscreen = true` and `AlwaysAnimate`.

### 3. Verified native distance band (`sapling_rust.dll`)

`sapling_rust.dll` is a zero-dependency Rust `cdylib` exporting only `sapling_band_fill` — an exact
port of `CellDistanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates`, verified
byte-identical against the retail assembly over 943,552 cases / 58,418,176 ints by
`tools/parity-harness/verify.ps1`, plus 20,000 live shadow-verified calls in-game before activating.
Press **F7** to toggle native/retail live.

---

## The patches

| Class | What it does |
|---|---|
| `MemoryPatches` | Skips historical `WorldState` reconstruction on load; shares one `AnimalTextureFactory`; prunes extinct definitions every 100 days. |
| `LODPatches` | Automatically scales `CloseDistanceThreshold` and `lodBias` to scene density; keeps `updateWhenOffscreen` tight (`75 m`); culls offscreen plant Animators (`<= 100 m`), disables shadows on LOD4 plant stubs, and resets pooled rigs before parking out of sight. |
| `FastSimPatches` | Zero-allocation, Boo-reflection-free replacements for `LivingPlant.LiveADay`, `LetAllPlantsInAreaLiveADay`, `SearchNearbyCellsForInstinctTrigger`, `CellContainsFoodTypeWithIndexForAnimal`, and `PredationCheck` (accelerates Time Jumps and cuts GC2 pauses). |
| `StreamingJITPatches` | Reuses prebuilt species models for mutants; fixes `AnimalInfo.hangingBabyFace` cyclic-serialization crash; exception-safe locking on `MutationTracker` (`HarmonyFinalizer`). |
| `SimulationPatches` | Replaces O(S²) boxed scans in `Notificator.ProcessNewState` with O(S) `HashSet` lookups; keeps calendar/night cycle synced with `dayIndex`. |
| `TimeJumpPatches` | Budgeted main-thread time jumps (`850 ms`/frame on jump screen, `12 ms` in-world) with zero-GC ping-pong `WorldState` buffers; fixes `GetYesterday`/`JumpAhead` edge cases. |
| `SteamPatches` | Defers per-day / per-predation Steam achievement IPC during time jumps and flushes once at jump end. |
| `BandPatches` | Fast-paths empty distances and routes `CellDistanceFinder` queries through `FillBandDirect` (cross-verified byte-identical to `sapling_rust.dll` at startup, with automatic retail fallback). |
| `CleanLogPatches` | Strips duplicate child `LODGroup`s, filters LOD/animation log spam, and guards vanilla `IndexOutOfRange`/`NullReference` crashes. |
| `ProfilerPatches` | Low-overhead profiler writing 5 s inclusive timing tables, frame p50/p95/max, GC counts, and scene census to BepInEx log and `%LocalLow%\Wessel Stoop\The Sapling\sapling_profile.log`. |
| `RconServer` | Local TCP console on `127.0.0.1:27016` (read-only by default; mutations gated by `[Rcon] AllowMutation`). |

---

## Building, verifying, and deploying

```powershell
# 1. Verify native band parity (unit tests + release build + 943k-case oracle + mutation check)
powershell -File tools\parity-harness\verify.ps1

# 2. Build managed BepInEx plugin
dotnet build csharp\SaplingRustBridge\SaplingRustBridge.csproj -c Release

# 3. Deploy SaplingRustBridge.dll + sapling_rust.dll (cleans any stray native DLL from BepInEx\plugins)
powershell -File deploy_all.ps1
```
