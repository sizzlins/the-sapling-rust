# The Sapling: Native Turbocharger Mod (`sapling-rust`)

A high-performance native mod and engine accelerator for **The Sapling** (Unity 2017.4 legacy Mono), engineered to solve the late-game memory leaks (10–32 GB RAM ballooning) and severe framerate drops (< 10 FPS with 1,000+ organisms).

100% compatible with existing Steam saves, in-game creature/plant editors, procedural body meshes, and graphics.

---

## What Problem This Solves

1. **The 10–32 GB RAM Balloon:**
   The game runs on Unity 2017's legacy Mono runtime with the Boehm Garbage Collector, which never returns allocated virtual memory back to the OS. Millions of temporary `int[]` and `List<int>` heap allocations created every simulated day by `CellDistanceFinder` during spatial queries severely fragment the heap. Furthermore, `TimeLine.line` retains historical `WorldState` copies indefinitely.
2. **The < 10 FPS Framerate Tank (600,000+ Transforms):**
   Late-game scenes maintain over 600,000 active GameObject Transforms (~78 bone transforms per organism, with both young and old version hierarchies existing simultaneously). Unity's internal transform matrix calculations account for ~92% of total frame time.

---

## How It Works

* **Native Rust Core (`sapling_rust.dll`):** High-speed Compressed Sparse Row (CSR) spatial partitioning grid and Structure-of-Arrays (SoA) entity memory buffers with zero GC pressure.
* **C# BepInEx Bridge (`SaplingRustBridge.dll`):**
  - **`SpatialPatches`**: Memoizes spatial distance bands and neighbor queries in static lookup tables, eliminating millions of short-lived array allocations per day.
  - **`LODPatches`**: Enforces `updateWhenOffscreen = false` on SkinnedMeshRenderers, renderer-based animation culling, and deactivates redundant young/old version GameObjects.
  - **`MemoryPatches`**: Caps unbounded historical timeline clones and triggers periodic Mono GC compaction during time jumps.

---

## Installation

1. Ensure **[BepInEx 5.4.21 x64](https://github.com/BepInEx/BepInEx/releases)** is installed in your *The Sapling* directory.
2. Download the latest release from this repository:
   - Place `sapling_rust.dll` into `The Sapling/` (and `the_sapling_Data/Plugins/`).
   - Place `SaplingRustBridge.dll` into `The Sapling/BepInEx/plugins/`.
3. Launch the game normally via Steam.

---

## Building from Source

### Prerequisites
- **Rust Toolchain:** `rustc 1.78+` (configured with `x86_64-pc-windows-gnu` / GCC or MSVC).
- **.NET SDK:** .NET SDK supporting `net35` with `Microsoft.NETFramework.ReferenceAssemblies`.

### Build Commands

```bash
# Build the native Rust simulation DLL (Release)
cargo build --release

# Run simulation complexity and parity tests
cargo test

# Build the C# BepInEx bridge
cd csharp/SaplingRustBridge
dotnet build -c Release
```
