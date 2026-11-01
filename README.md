# sapling-rust — performance patches for The Sapling

A BepInEx 5 plugin for **The Sapling** (Unity 2017.4, legacy Mono) that fixes the late-game
memory balloon and the worst of the framerate collapse.

It is a **managed-only** mod today. The original Rust engine (`sapling_rust.dll`) is still in
the repository but is **not loaded and not called**; see [Status](#status).

---

## What this actually fixes

### 1. The 10–32 GB RAM balloon and the out-of-memory crash on load — *measured*

`GameSaver.Decode` rebuilds **one `WorldState` per simulated day**:

```csharp
dayIndex = GetHighInteger();
worldStates = new List<WorldState>();
for (int l = 0; l < dayIndex; l++)
    worldStates.Add(GetWorldState());
```

`WorldState`'s constructor eagerly allocates 39 `Dictionary<int,Travel>` / `Dictionary<int,int>`,
9 `Hashtable`s and 4 `List<int>`. A load probe measured the real cost at **~28.3 KB of committed
memory per instance** (commit deltas of 2,807 / 2,841 / 2,824 / 2,840 MB per 100,000 states — dead
flat). At day 878,501 that is roughly **25 GB** of objects that are never read again, and the load
dies inside `mono_array_new_specific` as a `KERNELBASE.dll Breakpoint (0x80000003)`.

The running game discards them anyway: `TimeLine.LiveADay()` sets `line[line.Count - 2] = null`
every day, so at rest only the last two entries hold data. `AddWorldState(state, saveAllData: false)`
already writes four zero counts for non-final entries, so the save format itself treats history as
empty.

**Fix:** a Harmony prefix on `GameSaver.GetWorldState` consumes the four `(count, values…)` blocks
with `GetHighInteger()` to keep the stream aligned, returns `null` for historical entries, and keeps
the last two. `line.Count`, `dayIndex` and every species index stay identical to vanilla.

Measured on a day-878,501 save:

| | before | after |
|---|---|---|
| commit at loop end | 24,225 MB @ index 800,000 | **1,627 MB @ index 878,501** |
| `WorldState` objects built | 878,501 | **2** |
| `Decode` duration | 37 s, then crash | **716 ms** |
| crash dirs | every load | none |

> **Trap for future maintainers:** `GameSaver` derives from `MonoBehaviour` but the game constructs
> it with `new GameSaver()` (`OverallController.cs:306`, `SaveGameController.cs:1900/1991/…`). Such an
> object has a zero native pointer, so Unity's overloaded `==` reports a live object as `null`. Any
> prefix must use `ReferenceEquals(__instance, null)`, not `== null`.

### 2. Framerate collapse at high population

Late-game scenes hold hundreds of thousands of active Transforms (roughly 78 bone transforms per
organism, with both young and old hierarchies alive). The patches that address this are described
below; their effect has been verified by reading them against the decompiled game, **not** yet by
profile numbers.

---

## The patches

| Class | What it does |
|---|---|
| `MemoryPatches` | Skips historical `WorldState` reconstruction on load (the multi-GB spike); shares one `AnimalTextureFactory`; prunes extinct definitions every 100 days; F9 runs a deep trim. |
| `LODPatches` | Every 250 ms, demotes animals beyond the camera distance to `SkinQuality.Bone2`, shadow-casting off, and no IK/walk controller. The 350 m / 1000 m thresholds are **vanilla's own defaults** (`WorldView.cs:1551-1552`) — an earlier comment claimed ">35m", which was simply wrong. Renderer arrays are cached per GameObject so a camera sweep does not allocate. |
| `StreamingJITPatches` | Reuses prebuilt species models for mutants instead of rebuilding meshes on the main thread; fixes the `AnimalInfo.hangingBabyFace` cyclic-serialization crash; thread-safe `MutationTracker`. |
| `SimulationPatches` | Replaces the O(S²) boxed scan in `Notificator.ProcessNewState` with O(S) set lookups; keeps the calendar/night cycle in sync with `dayIndex`. |
| `TimeJumpPatches` | Budgeted main-thread time jumps (150 ms/frame, 25 days per batch) with a responsive UI; ping-pong `WorldState` buffers to make `Clone` allocation-free during a jump; fixes `GetYesterday`/`JumpAhead` edge cases and the cheat-console pause bug. |
| `CleanLogPatches` | Removes duplicate child `LODGroup`s, filters the 77,000-line LOD/animation log spam, and adds bounds/finalizer guards for the vanilla `IndexOutOfRange`/`NullReference` crashes it was hitting. |

## Status

* **Native engine unwired.** Only six FFI calls were ever made. The only live one was
  `TimeLine_LiveADay_Postfix`, which ticked a separate 50-plant Rust sandbox and pushed an
  unbounded `Vec<DeltaSnapshot>` (`species_counts: species.to_vec()`) on every normal-play day.
  Nothing ever read it back (`sapling_timeline_get` had no call site), so it cost CPU and leaked
  Rust heap for no benefit. That hook is gone.
* `NativeSim.cs`, `SpatialPatches.cs`, `AsyncJumpWorker.cs` and `ThreadRandomPatch.cs` are **kept on
  disk but no longer registered or called**. They are reference material for reviving the native
  path deliberately.
* `sapling_rust.dll` is **not required** at runtime.

## Profiler

The plugin now measures before it optimises. `ProfilerPatches` wraps 17 methods with
`Stopwatch` timestamps and writes an inclusive-time table every 5 seconds:

* to the BepInEx log, and
* to `%USERPROFILE%\AppData\LocalLow\Wessel Stoop\The Sapling\sapling_profile.log`
  (append-only, so it survives a hard crash — BepInEx's log is buffered and loses its tail).

Press **F8** to toggle it, which also lets you measure the profiler's own overhead (5 s on,
5 s off, compare).

```
[PROF] === window 5.0s | frames 300 | days 0 | commit 1832 MB | gc 420 MB ===
[PROF] WorldView.Update                    3821.4ms   76.4%   60.0/s max   21.30ms
[PROF] WorldController.Update               611.2ms   12.2%   60.0/s max    4.10ms
[PROF] PlantView.Update                     902.7ms   18.1%  4800.0/s max    0.42ms
...
```

Times are **inclusive**: `WorldView.Update` contains the per-view rows beneath it, so percentages
overlap and do not sum to 100.

---

## Installation

1. Ensure **[BepInEx 5.4.21 x64](https://github.com/BepInEx/BepInEx/releases)** is installed in your
   *The Sapling* directory.
2. Copy `SaplingRustBridge.dll` into `The Sapling/BepInEx/plugins/`.
3. Launch the game normally.

`sapling_rust.dll` is not needed and is not loaded.

## Building

```bash
cd csharp/SaplingRustBridge
dotnet build -c Release
```

The build references the game's managed assemblies directly from
`D:\The_Sapling\The Sapling\the_sapling_Data\Managed\`; adjust `SaplingRustBridge.csproj` if your
install lives elsewhere.

## Known loose ends

* `MutationTracker.EndOfDay` takes a `Monitor.Enter` in a prefix and `Monitor.Exit` in a postfix,
  which leaks the lock if the original throws.
* `SimulationPatches`' class comment lists four optimizations; the birth-notification branch also
  requires `threshold == 0` while the extinction branch uses `>= threshold`. Worth a closer look
  before trusting the notification behaviour.
