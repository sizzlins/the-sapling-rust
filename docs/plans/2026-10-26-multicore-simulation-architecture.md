# Multicore Simulation & Background Time Skip Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform *The Sapling*'s single-threaded simulation into a high-performance multicore architecture by (1) moving the blocking Time Skip loop to an asynchronous background worker so the game's UI/rendering stays at 60–144 FPS with zero stalling, and (2) offloading heavy $O(N)$ spatial instinct lookups to a multithreaded native Rust CSR engine via P/Invoke.

**Architecture:** A two-tier concurrency pipeline:
1. **Tier A (Unity/C# Threading Boundary):** An asynchronous worker thread (`AsyncJumpWorker`) runs `TimeLine.JumpAhead` in the background during Time Jumps. The Unity main thread remains 100% responsive, running at 60–144 FPS to animate progress bars, interpolate camera positions, and process user input without OS freezes. Once the jump completes, state synchronization safely marshals back to the main thread before organism meshes are rebuilt.
2. **Tier B (Native Rust Multicore Core):** A Rayon-powered multithreaded spatial index in `sapling_rust.dll`. Slices organism food and mate searches across all physical CPU cores using Compressed Sparse Row (CSR) spatial hashing and AVX2 vectorization, returning results to C# with zero heap allocations.

**Tech Stack:** C# 3.5 / Mono (BepInEx 5.4.21, HarmonyX), Rust 2021 (Rayon, SIMD, FFI C-ABI).

## Global Constraints
- **Correctness First:** Mechanics, genetics, mutation rates, reproduction rules, and save files (`*.sav16`) must remain 100% identical to vanilla.
- **Unity Thread Safety:** No `UnityEngine.Object`, `Transform`, `GameObject`, or `GetComponent` calls may be executed from background worker threads.
- **Zero Allocations in Steady State:** Steady-state simulation days must not allocate temporary arrays or lists on the Mono GC heap.
- **Ponytail Senior Dev Rules:** Boring over clever, shortest working diff, standard library / native features first, deletion over addition.

---

## File Structure

```
d:/put gitrepos here/sapling-rust/
├── csharp/SaplingRustBridge/
│   ├── NativeSim.cs                          # P/Invoke signatures for native Rust multicore functions
│   ├── Patches/
│   │   ├── AsyncJumpWorker.cs                # [NEW] Background thread coordinator for Time Jumps
│   │   ├── SimulationPatches.cs              # O(S) species tracking & mate pruning
│   │   ├── TimeJumpPatches.cs                # Time jump hooking & buffer pooling
│   │   └── SpatialPatches.cs                 # Native Rust spatial query delegate hooks
├── src/
│   ├── ffi.rs                                # C-ABI exported functions for batch spatial queries
│   ├── lib.rs                                # Crate root and module declarations
│   └── sim.rs                                # CSR grid, SoA layout, and Rayon parallel iterator
└── tests/
    └── multicore.rs                          # [NEW] Multicore performance and race-freedom tests
```

---

## Phase 0: Documentation Discovery & Thread-Safety Audit

- **Audit Findings:**
  1. `TimeLine.cs` manages pure data structures (`WorldState`, `WorldArea`, `WorldCell`, `LivingAnimal`, `LivingPlant`). It contains **zero** `GameObject`, `Transform`, `GetComponent`, or `Instantiate` calls.
  2. The only Unity API used in `TimeLine.LiveADay()` is `UnityEngine.Random` (53 call sites), `Vector2`/`Vector3` (value types), and `Mathf` (pure static math).
  3. `WorldView.cs` is the only class managing visual GameObjects, SkinnedMeshRenderers, and camera rendering. It must remain exclusively on the main thread.
- **Allowed Threading Model:**
  - Background Thread: `timeLine.JumpAhead(days)` and native Rust SIMD execution.
  - Main Thread: `WorldController.Update()`, `WorldView.ShowToday()`, `uiView.UpdateProgress()`.

---

## Implementation Tasks

### Task 1: Background Time Skip Worker (`AsyncJumpWorker.cs`)

**Files:**
- Create: `csharp/SaplingRustBridge/Patches/AsyncJumpWorker.cs`
- Modify: `csharp/SaplingRustBridge/Patches/TimeJumpPatches.cs`

**Interfaces:**
- Consumes: `TimeLine.JumpAhead(int numberOfDays)`
- Produces: `AsyncJumpWorker.StartJump(TimeLine, int totalDays, Action onComplete, Action<int> onProgress)`

- [x] **Step 1: Write `AsyncJumpWorker.cs`**
  Implement a dedicated background thread worker that executes days in chunks, reporting progress via volatile counters and dispatching finalization back to the main thread:
  ```csharp
  using System;
  using System.Threading;

  namespace SaplingRustBridge.Patches
  {
      public static class AsyncJumpWorker
      {
          private static Thread _workerThread;
          private static volatile bool _isRunning = false;
          private static volatile int _daysRemaining = 0;
          private static volatile int _totalDays = 0;
          private static Action _onCompleteCallback;

          public static bool IsRunning => _isRunning;
          public static int DaysRemaining => _daysRemaining;
          public static int TotalDays => _totalDays;

          public static void StartJump(TimeLine timeLine, int totalDays, Action onComplete)
          {
              if (_isRunning || timeLine == null || totalDays <= 0) return;

              _isRunning = true;
              _totalDays = totalDays;
              _daysRemaining = totalDays;
              _onCompleteCallback = onComplete;

              _workerThread = new Thread(() =>
              {
                  try
                  {
                      while (_daysRemaining > 0 && _isRunning)
                      {
                          int batch = Math.Min(25, _daysRemaining);
                          timeLine.JumpAhead(batch);
                          _daysRemaining -= batch;
                      }
                  }
                  finally
                  {
                      _isRunning = false;
                  }
              })
              {
                  IsBackground = true,
                  Name = "SaplingAsyncJumpWorker"
              };

              _workerThread.Start();
          }

          public static void CheckMainThreadCompletion()
          {
              if (!_isRunning && _onCompleteCallback != null)
              {
                  var cb = _onCompleteCallback;
                  _onCompleteCallback = null;
                  cb.Invoke();
              }
          }
      }
  }
  ```

- [x] **Step 2: Hook `TimeJumpController.Update` and `WorldController.Update`**
  In `TimeJumpPatches.cs`, intercept the main thread update during Time Skip:
  - If `AsyncJumpWorker.IsRunning`, update the UI progress bar and skip blocking `JumpAhead` on the main thread.
  - When `AsyncJumpWorker` finishes, trigger `AsyncJumpWorker.CheckMainThreadCompletion()` to run `view.CleanUpAllOrganismViews()` and `ShowToday()` on the main thread safely.

- [x] **Step 3: Compile and Verify Build**
  Run: `dotnet build -c Release` in `csharp/SaplingRustBridge`.
  Expected: Build succeeds with 0 errors.

---

### Task 2: Multithreaded Spatial Query Engine in Rust (`src/sim.rs` & `src/ffi.rs`)

**Files:**
- Modify: `Cargo.toml` (add `rayon = "1.10"`)
- Modify: `src/sim.rs`
- Modify: `src/ffi.rs`
- Test: `tests/multicore.rs`

**Interfaces:**
- Produces: `sapling_spatial_find_nearest_batch(world, in_x, in_y, max_dist, out_indices, count)`

- [x] **Step 1: Add Rayon dependency to `Cargo.toml`**
  ```toml
  [dependencies]
  rayon = "1.10"
  ```

- [x] **Step 2: Implement Parallel Spatial Batch Query in `src/sim.rs`**
  Use Rayon parallel iterators to query nearest food and mates across all CPU cores simultaneously:
  ```rust
  use rayon::prelude::*;

  impl World {
      pub fn find_nearest_plants_parallel(
          &self,
          query_x: &[f32],
          query_y: &[f32],
          max_distance: f32,
          out_indices: &mut [i32],
      ) {
          assert_eq!(query_x.len(), query_y.len());
          assert_eq!(query_x.len(), out_indices.len());

          let grid = &self.grid;
          let plant_x = &self.plants.x;
          let plant_y = &self.plants.y;

          out_indices
              .par_iter_mut()
              .enumerate()
              .for_each(|(i, out_idx)| {
                  let qx = query_x[i];
                  let qy = query_y[i];
                  let nearest = grid.nearest_item(qx, qy, max_distance, plant_x, plant_y);
                  *out_idx = nearest.map(|idx| idx as i32).unwrap_or(-1);
              });
      }
  }
  ```

- [x] **Step 3: Export C-ABI Function in `src/ffi.rs`**
  ```rust
  #[no_mangle]
  pub unsafe extern "C" fn sapling_find_nearest_plants_batch(
      world: *const World,
      query_x: *const f32,
      query_y: *const f32,
      max_dist: f32,
      out_indices: *mut i32,
      count: u32,
  ) -> u32 {
      let w = match world.as_ref() {
          Some(w) => w,
          None => return 0,
      };
      if query_x.is_null() || query_y.is_null() || out_indices.is_null() || count == 0 {
          return 0;
      }
      let qx = std::slice::from_raw_parts(query_x, count as usize);
      let qy = std::slice::from_raw_parts(query_y, count as usize);
      let out = std::slice::from_raw_parts_mut(out_indices, count as usize);

      w.find_nearest_plants_parallel(qx, qy, max_dist, out);
      count
  }
  ```

- [x] **Step 4: Write Multicore Verification Test in `tests/multicore.rs`**
  ```rust
  use sapling_rust::sim::World;

  #[test]
  fn test_parallel_spatial_query_parity() {
      let mut world = World::new(42);
      world.populate(5000, 1000, 200.0);

      let count = 1000;
      let query_x: Vec<f32> = (0..count).map(|i| (i as f32 * 0.2)).collect();
      let query_y: Vec<f32> = (0..count).map(|i| (i as f32 * 0.2)).collect();
      let mut out_indices = vec![-1i32; count];

      world.find_nearest_plants_parallel(&query_x, &query_y, 25.0, &mut out_indices);

      // Verify at least some plants found and no index out of bounds
      let found = out_indices.iter().filter(|&&idx| idx >= 0).count();
      assert!(found > 0, "Parallel search must find nearest neighbors");
  }
  ```

- [x] **Step 5: Run Cargo Tests**
  Run: `cargo test --test multicore`
  Expected: PASS

---

### Task 3: P/Invoke Bridge & Integration (`NativeSim.cs`)

**Files:**
- Modify: `csharp/SaplingRustBridge/NativeSim.cs`
- Modify: `csharp/SaplingRustBridge/Patches/SpatialPatches.cs`

- [x] **Step 1: Declare P/Invoke Signature in `NativeSim.cs`**
  ```csharp
  [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
  public static extern uint sapling_find_nearest_plants_batch(
      IntPtr world,
      [In] float[] queryX,
      [In] float[] queryY,
      float maxDist,
      [Out] int[] outIndices,
      uint count);
  ```

- [x] **Step 2: Connect Batch Query to Animal Food Searches in `SpatialPatches.cs`**
  Hook animal food discovery to use the native multi-core CSR grid query when animal count $> 100$.

- [x] **Step 3: Compile and Deploy**
  Run: `cargo build --release` and `dotnet build -c Release`.
  Deploy `sapling_rust.dll` and `SaplingRustBridge.dll` to `D:\The_Sapling\The Sapling\BepInEx\plugins\`.

---

## Phase 4: Final Verification & Performance Scoreboard

- [ ] **1. UI Responsiveness Check:**
  Trigger a 1,000-year Time Skip in a crowded world.
  Verify with Windows Performance Monitor that Unity UI stays at 60+ FPS and window never reports "Not Responding".
- [ ] **2. Multicore CPU Utilization Check:**
  Verify CPU utilization scales from 1 core (~100%) across multiple CPU cores during native spatial passes.
- [ ] **3. Save File Parity Check:**
  Save the game after 500 years of multithreaded simulation. Reload the save and confirm all species definitions, population counts, and world cells match 100%.
