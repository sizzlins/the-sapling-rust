# Designing animals against the retail simulation

How `Solara` and the obligate carnivore template were built, and how to build the next one. The method is white-box: read the retail code, design against the mechanics it actually runs, then prove the design in the same code.

## The harness

`tools/sim-lab/SimLab.csproj` builds a console app against the retail assemblies from `D:\The_Sapling\The Sapling\the_sapling_Data\Managed`. It loads `Assembly-UnityScript.dll` and the real `BodyPartStats/*.yaml` and part configs into an `OrganismObjectFileImporter`, then drives `TimeLine.LiveADay()` directly. Harmony replaces the Unity random and debug calls and patches the crash paths.

Modes used in this work:

- `--inspect <file.species16>` prints the stats of both sexes plus body parts, diet, instincts, and problem detection.
- `--export-top-sexual <save> <outDir>` finds a sexual species in a save and writes a validated `.species16`.
- `--save <save> <days> <reportEvery> <stamp> band` runs a world forward. `days = -1` runs until a `STOP` file appears.
- `--screen` compares candidate builds across worlds.

The save path is the only place to start from when the subject is an existing world. The species path is the unit of reuse: a `.species16` file can be inspected, seeded into a save, and round-tripped through `GameSaver` without opening the game.

## Mechanics that decide whether a design survives

Read these before changing a body part. Each one has cost us a failed run.

**Birth needs `canWalk`, `sessile`, or water.** The delivery condition in `rea/TimeLine.cs:2183` includes `oceanDepth != -1f || canWalk || sessile`. `GameSaver.GetLivingAnimal` reloads animals with `gravity = 1f`, ignoring `planet.gravity`. On a low-gravity world every animal comes back overweight with `canWalk = false`, gets pregnant normally, and then never delivers. SimLab now sets `tl.gravity` from the save and recomputes stats before anything else runs. Any tool that loads a save without that step will report zero births for no obvious reason.

**Sex is stored per morph, and cloning drops the offspring sex.** `AnimalDefinition.Clone()` saves `currentSex`, zeroes it to read the base morph, and restores it on the source, but never writes it to the clone (`rea/AnimalDefinition.cs:2293`). `AnimalBlender.BlendAnimals` clones one parent, so every cross-variant child starts as sex 0. `SaplingRustBridge` installs `AnimalBlender_BlendAnimals_PreserveSex_Postfix` to fix it. Without that patch a two-sex species becomes female-only within a few generations.

**Offspring sex comes from brackets, not from the sexes array.** `AnimalStatsCalculator` builds `offSpringSexChances` from twelve `offspringSexChanceBracket*` values on the current morph. Mutated brackets can send almost every birth to one sex. The durable fix is to write six zero brackets and six one brackets into the definition itself, with `offspringSexDecisionType = Chance`, because the calculated array is rebuilt on every stats refresh.

**Movement overshoots adjacent mates.** `TimeLine.ManageTravelsForAnimal` moves `Ceil(speed)` cells. A speed above 1 can land on the target's cell when it is one cell away, and the engine rejects the move. `Solara` uses `speed = 1.00` so a mate at distance 1 is reached without overshoot. Legs also set `maxSteepness`; `splithoof` gives 1.00 and climbs anything.

**Mouth capability is not diet.** `eatsMeat` and `eatsPlants` are flags on the mouth part (`AnimalStatsCalculator.cs:735`). The food that actually feeds the animal is `mouthDefinition.energyPerFoodType`, which becomes `orderedDetailedFoodTypes` and `energyPerDetailedFoodType`. A creature with a meat mouth and zero meat energy will show as a carnivore in `eatsMeatOnly` and still live on fungus. Count real predators from the energy list.

**Egg layers and live bearers have different timing.** `embryoTimeInsideMother = 0` gives live birth. A long `timeInEgg` plus `matureAtAge` sets the generation time, and a short `expectedAge` leaves no room to replace the parent. At body size 11 the Solara template has `expectedAge = 80` days, which is barely one clutch after egg and maturity; size 12 gives 120 and size 13 gives 160.

**Weight support comes from legs, not the torso.** Trimming torso diameter down to 1.2 changed weight by nothing. Adding a second leg pair to `torso[3]` took support from 8.0 to 16.0 and let a size-12 or size-13 carnivore walk.

## Procedure

1. Fix the world first. Load the save, confirm the gravity fix is active, and record animals, sexes, fertility, and the delivery-gate counters. A design tested on a broken load tells you nothing.
2. Choose a habitat and read its gates. Land needs `canWalk` and `maxSteepness` against the local cells. Water needs `canSwim`, an aquatic limb behavior, and `timeInWater` that permits the cell type; `timeOnLand = -1` keeps the animal out of land cells. `--inspect` prints `swim`, `aq`, `inW`, and `onL`.
3. Pick a base. Solara was assembled from raw parts in `BuildSolaraDefinition`: `new AnimalDefinition()`, `CreateMouthWithShapeIndex(32)`, `CreateEyeWithShapeIndex(15)`, `CreateLimbWithShapeIndex(65)`, and `new TorsoPartDefinition(...)` segments. Adapting an existing `.species16` from `ArchivedAnimals` is the faster path when the body plan is already close; `SeedFromArchivedBodyplan` does that for the carnivore template.
4. Set reproduction explicitly. Female: sexual birth on, asexual off, internal fertilization off. Male: internal fertilization on, both birth flags off. Sex decision `Chance`, six zero brackets and six one brackets. Mate-first instinct, then food.
5. Screen candidates. Run each build in several worlds with the same seeds and compare final population, peak, births by sex, and deaths by cause. A single world proves nothing; the Solara sweep used five seeds over 5,000 days each.
6. Run a negative control. Six females and no males must produce zero births and die out. If it does not, the species is reproducing asexually and the design is not what you think.
7. Export and round-trip. Write the archive with `GameSaver.AddSpecies`, read it back with `GetSpecies`, and check both sexes report valid.

## Failure modes seen so far

| Symptom | Cause | Fix |
|---|---|---|
| Pregnant animals, `readyToGiveBirth` stuck, zero eggs | `canWalk = false` from the gravity reload | Set `tl.gravity` from the save and recompute stats |
| All-female population after a few generations | `Clone()` loses `currentSex` during blending | `SaplingRustBridge` sex-preservation patch |
| One sex dominates despite `Chance` | Mutated chance brackets or `Temperature` decision | Write 6/0 and 6/1 brackets, set `Chance` |
| Founders die in one wave | Every founder spawned at the same age | Stagger founder ages |
| Seeded species never inseminated | Classifier returns `-1` for a new species | Pair by shared `definitionIndex` when lookup fails |
| Predator declines while pregnant | Generation time exceeds lifespan | Larger body size, or shorter egg and maturity |
| Carnivore cannot walk at size 12 or 13 | Weight exceeds leg support | Add a second leg pair |

## Reproducing a run

```powershell
cd "D:\put gitrepos here\sapling-rust\tools\sim-lab\bin\Audit"
$env:SIM_ANIMAL_SAFE="1"
.\SimLab.exe --save "<save.sav16>" -1 1000 20261125 band
```

Create `sim-results\STOP` to save the current world and exit. Checkpoints are written every 25,000 days in forever mode.

For a seeded species:

```powershell
$env:SIM_SEED_ARCHIVED="C:\Users\LOQ\AppData\LocalLow\Wessel Stoop\The Sapling\ArchivedAnimals\Solara.species16"
$env:SIM_CARNIVORE_COUNT="120"
$env:SIM_CARNIVORE_SIZE="13"
$env:SIM_ANIMAL_SAFE="1"
.\SimLab.exe --save "<save.sav16>" 800 50 20261125 band
```

## Aquatic species

Water changes four mechanics that the land templates do not exercise.

- `EggType.Ovoid` maps to `EggLocation.DryLand`, so `EggCanSurviveInCell` fails in water and no birth ever passes the gate. Use `Capsule` (UnderWater).
- `EnoughSpaceForReproduction` steps to a neighbour with `maximumSteepness`. A pure fin swimmer has 0, so every ready female fails `badSpace`. Add `webbed` feet (0.4) to clear it.
- The archived-bodyplan seeder rejects every cell with `oceanDepth >= 0`; aquatic species need their own water placement path.
- `Plankton` in a mouth's energy map sets `filterFeeder`, which is what makes plankton edible across nearby cells. Current deaths come from `maximumOceanCurrent`, which is 0.95 for a size-14 swimmer; give the species 1.0.

The working design is `Aquara`: filter mouth (id 9, plankton plus macro algae as SoftPlantTissue and MarineSnow, meat removed so the 3-birth carnivore cap does not apply), two `waterwing` pairs and `webbed` feet, `aquavision` eyes (18), `elven` ears (2), body size 14. It is hermaphroditic: every sex-0 adult both fertilizes and bears, with asexual birth off. That removes the male/female pairing bottleneck without cloning. It survived 8,000 days unaided, oscillating between 13 and 87 animals with 1,895 births.

## Limits

`SeedFromArchivedBodyplan` places animals on walkable land cells only. `SeedAquatic` is the separate water path, and it assumes the definition is already built for swimming; it does not adapt an arbitrary archived body plan.

The screens are still stochastic. A species can pass five worlds and fail on a sixth, so treat a sweep as evidence, not proof.
