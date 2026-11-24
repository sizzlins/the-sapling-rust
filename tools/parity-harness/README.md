# Parity harness

Proves that a native reimplementation matches the retail game, instead of
asserting that it looks right.

## Why this exists

`tests/parity.rs` compares the Rust sim against the Rust sim's own intentions
("plants must grow", "herbivore should eat"). It can be 100% green while the
port disagrees with the game. That is how the previous native attempt shipped a
distance-band implementation that broke animal food instincts: nothing ever
compared it to retail.

This harness uses the retail assembly as the oracle. No reference model is
written by hand:

```csharp
var finder = new CellDistanceFinder();
finder.Initialize(new Vector2(sizeX, sizeY));
int[] retail = finder.GetAllCellsWithinDistanceBandOfCellWithIndex(
    center, distance, top, left, right, bottom);
```

`Assembly-CSharp-firstpass.dll` loads in a plain .NET Framework process because
`Mathf.Ceil`/`Mathf.Floor` are managed IL in 2017.4, not native internal calls.

## What is compared

Per case: array length, then every int in order. `-1` holes are part of the
contract and are compared like any other value.

| axis | coverage |
| --- | --- |
| world sizes | 64x64, 80x50, 47x31, 3x3, 1x1, 63.5x40.25 (fractional) |
| centres | every cell on small worlds; border + stride-5 interior on large; plus -1, -7, size, size+123 |
| distances | 0..22 (0 and 21..22 have no retail case and must return an empty array) |
| direction masks | all 16 combinations of top/left/right/bottom |

943,552 cases, 58,418,176 ints.

Fractional sizes matter: retail bounds-checks against the float
(`(float)(centerX + x) < size.x`) but strides cells by `(int)size.y`, so the two
cannot be collapsed into integers.

## Run it

```powershell
.\verify.ps1
```

That runs, in order:

1. `cargo test --lib band::` - the implementation's own unit tests.
2. `cargo build --release` - the good native library.
3. `dotnet build -c Release` - this harness.
4. `ParityHarness dump` - fingerprints the oracle alone.
5. `ParityHarness compare <good dll>` - must print `PASS`.
6. `cargo build --release --features mutate --target-dir target-mutate` - a
   build with the left/right gates deliberately swapped.
7. `ParityHarness compare <mutated dll>` - must print `FAIL`.

Step 7 is the point. A harness that passes everything proves nothing; the
mutated build must be caught, and it is, with the first differing slot named.

Manual equivalents:

```powershell
dotnet build -c Release --project .
.\bin\Release\net48\ParityHarness.exe dump
.\bin\Release\net48\ParityHarness.exe compare ..\..\..\target\x86_64-pc-windows-gnu\release\sapling_rust.dll
.\bin\Release\net48\ParityHarness.exe mutation
```

`mutation` is a second sanity check: it diffs retail against a *plausible*
reimplementation (cells within Euclidean radius). 95.6% of cases differ, which
is the measured reason the offset table must be transcribed rather than derived.

## Current result

```
cases          943552
ints compared  58418176
mismatches     0
RESULT         PASS - byte-identical to retail
```

```
cases          943552
mismatches     408002   (mutated build: left/right gates swapped)
RESULT         FAIL
```

## Scope

This proves `sapling_band_fill` equals
`CellDistanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates` over the
matrix above. It does not prove anything about callers, about the instinct
ring-search (`sapling_grid_search_instinct`, a different function with different
semantics), or about in-game behaviour. Those need their own comparisons.
