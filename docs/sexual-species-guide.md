# How to Build a Successful Sexual Species (Herbivore or Obligate Carnivore)

Sexually reproducing animals in **The Sapling** fail much more easily than asexuals because every generation needs two compatible adults to **survive, find each other, mate, and still hold enough energy to lay eggs**. This guide covers the exact body parts, instincts, spawning rules, and engine mechanics needed to build self-sustaining sexual herbivores and obligate carnivores.

Reference templates in this repo:
- Herbivore: `ArchivedAnimals/Sex Herbivore.species16`
- Obligate carnivore: `ArchivedAnimals/Sex Obligate Carnivore.species16`

---

## 1. Prerequisites (`SaplingRustBridge.dll`)

Vanilla *The Sapling* has three bugs that break sexual species (and especially sexual carnivores) over multi-thousand-day runs. All three are fixed in `SaplingRustBridge.dll` (`FastSimPatches.cs`):

1. **Species-wide male instinct corruption:** Vanilla `AnimalMorph.Clone` shallow-copies `InstinctDefinition` references, so one male mutating an instinct overwrites every living male in the species (often locking all males into `Attack` and collapsing mating).
2. **2-sex carnivore reproductive cap:** Vanilla hardcodes `maximumNrOfChildren = 3` for all meat-eaters. That gives asexual carnivores 3.0 daughters per parent, but 50/50 sexual carnivores only 1.5 daughters per female (below replacement). `SaplingRustBridge` scales this to `3 × (sexes)` = `6` for strictly sexual 2-sex carnivores (3.0 daughters/female parity) while leaving asexual carnivores at `3`.
3. **Low-gravity save-load paralysis:** Vanilla `GameSaver.GetLivingAnimal` hardcodes `gravity = 1f` instead of the planet's gravity on reload, crushing evolved low-gravity animals to `canWalk = false, speed = 0`.

---

## 2. Core Blueprint (Both Herbivores & Carnivores)

### Sexes & Reproduction
- **Sex A (Female / index 0):** Sexual birth **ON**, asexual birth **OFF**, internal fertilization **OFF**.
- **Sex B (Male / index 1):** Internal fertilization **ON**, both births **OFF**.
- **Offspring sex ratio:** Chance-based **50/50** (6 brackets Sex A, 6 brackets Sex B).
- **Embryo time inside mother:** `0` (egg-laying). Live pregnancy burns `2× dailyEnergyLoss` every day and delays re-mating.
- **Egg & hatch temperature limits:** Wide (`0.0` to `1.0`) so seasons don't halt recruitment.

### Vision (The #1 Killer of Small Founder Groups)
Newly spawned juveniles wander randomly until maturity (`matureAtAge ≈ 30` at size 12). If their eyes have short range, they disperse beyond sight range and die of `OldAgeNoMate` (Allee effect).

- **Best eye:** **`stalk`** (`#15`, 360° FOV, **20-cell** daytime & twilight vision — covers ~1,256 cells).
- **Runner-up:** **`peduncle`** (`#10`, 180° FOV, 20-cell daytime vision).
- **Avoid for small spawns:**
  - `receptor` (`#13`) — only **4-cell** vision (~50 cells). Small groups of 10–20 founders will drift apart and die of `OldAgeNoMate`. *(Note: `ArchivedAnimals/Sex Herbivore.species16` still has `receptor` eyes — swap to `stalk` in the editor if spawning <40 founders.)*
  - `pseudopupil` (`#21`) at large eye scale — its daytime sight curve `[0.6, 0.0]` *shrinks* with eye size, dropping to ~2.7 cells on large eyes.

### Body, Legs, Skin & Mouth Orientation
- **Legs:** Fast walking limbs (`hoof`, speed `3.5`) with comfortable weight margin (`weight < weightSupportedForWalking`). Remember: an animal designed on a low-gravity planet may be overweight on a 1.0g planet.
- **Skin:** **`Feathered`** gives `+10°C` cold and `+5°C` heat tolerance (e.g., `[-14..+30°C]` at size 12) for 0 weight, preventing winter wipeouts.
- **Mouth orientation:** Make sure the mouth attaches on the **outward-facing** side of the head (away from the torso), not pointed inward into the head.

### Instincts (Order Matters)
`GoAwayFrom`, `Cry`, and `Attack` all set `actionIsInstinctivelyPrioritized = true`, which **skips both mating and eating for that entire day**.

Use this exact minimal stack:
- **Herbivores (both sexes):**
  1. `See: A Potential Mate → Go Towards`
  2. `See: Food → Go Towards`
- **Carnivores (Female):**
  1. `See: A Potential Mate → Go Towards`
  2. `See: Food → Go Towards`
  3. *(Optional, MUST be last)* `Feel: Ready To Mate → Cry` — never put `Cry` above `See: Food`, because `ReadyToMate` stays true even after fertilization until pregnant, starving females if it outranks hunting.
- **Carnivores (Male):**
  1. `See: A Potential Mate → Go Towards`
  2. *(Optional, with `elven` ears)* `Hear: A Sound → Go Towards`
  3. `See: Food → Go Towards`
- **NEVER add `Feel: Too Warm / Too Cold → Go Away From`** — it locks animals out of eating and mating for entire seasons.

---

## 3. Building a Sexual Herbivore

1. Start from the core blueprint above (size **11–12**, `hoof` legs, `stalk` eyes, `Feathered` skin).
2. **Mouth:** **`chew`** (`Grass: 3.0`, `SoftPlantTissue: 2.0`). Grass regrows reliably across flat terrain and sustains large populations (2,000–5,000+). Fruit/seed/fungus specialists also work once plants/fungi are dense, but grass is the most forgiving starter food.
3. **Spawning:**
   - Spawn **both Sex A and Sex B** tightly together on flat, grassy land.
   - With `stalk` eyes: **15–20+ total founders** (roughly half A, half B) is plenty.
   - With short-range eyes (`receptor`): spawn **40–60+ founders** in one tight spot so they boom before dispersing.

---

## 4. Building a Sexual Obligate Carnivore

Obligate carnivores (`eatsMeat = true`, `eatsPlants = false`) have four strict engine constraints:

1. **Walking speed MUST be `> 2.0`:** In vanilla `TimeLine.cs`, a predator with `speed <= 2.0` tries to step onto its prey's occupied cell and aborts the hunt. Use **`hoof`** (`speed = 3.5`).
2. **Choose body size carefully relative to your prey:**
   - **Same size as prey (e.g., Size 12 carnivore vs Size 12 herbivore — recommended for long co-evolution):**
     - Adult carnivores **cannot** swallow adult prey whole (`carnSize > preySize` is false), so they hunt **prey juveniles (`isChild`) + carcasses**.
     - This creates a built-in **adult prey refuge** — carnivores can never over-hunt adult herbivores to extinction, producing stable multi-thousand-day Lotka-Volterra cycles.
     - **Requirement:** Herbivores must already be dense (**~1,500–2,000+ animals** on the continent) before you drop the carnivores, because only ~30% of herbivores are edible juveniles.
   - **1 size larger than prey (e.g., Size 13 carnivore vs Size 12 herbivore):**
     - Can eat **adult** prey whole (triples available food, works starting around ~800–1,000 herbivores).
     - **Risk:** No adult prey refuge — a carnivore boom can eat every herbivore and then starve unless the map has a high herbivore carrying capacity.
3. **Mouth choice:** **`scavenger`** (`OldMeat: 7.0`, `FreshMeat: 7.0`, `WholeAnimal: 6.0`) is the most resilient obligate-carnivore mouth. It actively hunts live prey (`WholeAnimal: 6.0`), leaves prey eggs alone (protecting prey recruitment), and uses carcasses (`OldMeat: 7.0`) as a food buffer during prey troughs.
4. **Wide vision + ears:** Use **`stalk`** eyes (20-cell 360° vision) + **`elven`** ears (`hear = 4.0`). Carnivores live at much lower density than herbivores (50–250 vs 1,500–3,000), so long sight range is mandatory to prevent low-density mate-finding collapse.
5. **Spawning:**
   - Wait until herbivores have expanded to **1,500–2,000+** (for same-size) or **800–1,000+** (for +1 size).
   - Drop **25–30+ carnivores** (both Sex A and Sex B) directly on top of the densest herbivore cluster.

---

## 5. Quick Troubleshooting

| Symptom | Root Cause | Fix |
|---|---|---|
| Founders scatter and die of `OldAgeNoMate` | Short-range eyes (`receptor` / large `pseudopupil`) or too few founders spawned as babies | Swap eyes to `stalk` (`#15`) and spawn 20+ (both sexes) in one tight spot |
| Carnivores mate (`100%` fertilized) but lay almost no eggs and starve | Prey density too low (same-size carnivores only eat juveniles + carcasses) | Time-skip herbivores to 1,500–2,000+ first, or make carnivore 1 size larger than prey |
| Carnivores won't catch live prey | Walking speed `<= 2.0` | Use faster legs (`hoof`, speed `3.5`) |
| Healthy sexual species suddenly stops mating after 200–4,000 days | Vanilla `AnimalMorph.Clone` male-instinct overwrite bug | Install updated `SaplingRustBridge.dll` |
| Animals freeze (`speed = 0`) after reloading a low-gravity save | Vanilla `GameSaver` hardcoded `gravity = 1f` on load | Install updated `SaplingRustBridge.dll` |
| Species eats nothing during winter/summer | `TooWarm` / `TooCold → GoAwayFrom` instinct blocking eating & mating | Remove temperature `GoAwayFrom` instincts; use `Feathered` skin |
