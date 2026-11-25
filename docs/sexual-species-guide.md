# How to build a sexual species in The Sapling

Sexual species fail much more easily than asexual ones because every generation depends on two compatible adults surviving, finding each other, mating, and still having enough energy left to lay eggs.

Two working templates are already in this repo:
- Herbivore: `ArchivedAnimals/Sex Herbivore.species16`
- Obligate carnivore: `ArchivedAnimals/Sex Obligate Carnivore.species16`

## Prerequisites (`SaplingRustBridge.dll`)

Vanilla The Sapling has three bugs that kill sexual species (especially carnivores) over long runs. All three are patched in `SaplingRustBridge.dll` (`FastSimPatches.cs`):

1. Vanilla `AnimalMorph.Clone` shallow-copies `InstinctDefinition` references, so a single male mutating an instinct overwrites every living male in the species. That often flips all males to `Attack` and stops mating across the whole population.
2. Vanilla hardcodes `maximumNrOfChildren = 3` for every meat-eater. Asexual carnivores average 3.0 daughters per parent, while 50/50 sexual carnivores average 1.5 daughters per female, which is below replacement. `SaplingRustBridge` scales the cap to `3 * sexes` (`6` for a 2-sex carnivore) for strictly sexual meat-eaters and leaves asexual carnivores at `3`.
3. Vanilla `GameSaver.GetLivingAnimal` hardcodes `gravity = 1f` on load instead of using the planet's gravity. Any low-gravity animal that evolved lighter legs gets reloaded with `canWalk = false` and `speed = 0`.

## Core setup for both herbivores and carnivores

### Sexes and reproduction

Configure Sex A (index 0, female) with sexual birth on, asexual birth off, and internal fertilization off. Configure Sex B (index 1, male) with internal fertilization on and both birth options off. Set offspring sex to chance-based 50/50 (6 brackets for Sex A, 6 for Sex B).

Keep embryo time inside the mother at `0` so the species lays eggs. Live pregnancy burns `2x dailyEnergyLoss` every day and delays the next mating. Set egg and hatch temperature limits to the full `0.0` to `1.0` range so seasonal temperature swings do not pause reproduction.

### Eyes

Newly spawned animals start as juveniles and wander randomly until they mature (`matureAtAge` is around 30 days at size 12). If their eyes have short range, a small founder group drifts apart before maturity and dies of `OldAgeNoMate`.

Use `stalk` eyes (`#15`) whenever possible. They have a 360-degree field of view and 20-cell vision during both day and twilight, covering roughly 1,256 cells. If you want an alternative, `peduncle` (`#10`) also reaches 20 cells during the day with a 180-degree field of view.

Avoid `receptor` (`#13`) for small spawns. Its 4-cell range covers only about 50 cells, so 10 to 20 founders usually scatter and go extinct. Note that `ArchivedAnimals/Sex Herbivore.species16` still has `receptor` eyes; swap them to `stalk` in the editor if you plan to spawn fewer than 40 founders. Also avoid `pseudopupil` (`#21`) at large eye scales, since its daytime sight curve (`[0.6, 0.0]`) shrinks as eye scale grows and drops to around 2.7 cells on large eyes.

### Legs and skin

Give land animals fast walking legs such as `hoof` (speed `3.5`) with comfortable weight margin (`weight < weightSupportedForWalking`). Keep gravity in mind when reusing an archived animal: a build that walks fine on a low-gravity planet can end up overweight on a 1.0g planet.

`Feathered` skin adds +10°C of cold tolerance and +5°C of heat tolerance (giving `[-14..+30°C]` at size 12) at zero weight, which prevents winter die-offs.

### Instincts

`GoAwayFrom`, `Cry`, and `Attack` all set `actionIsInstinctivelyPrioritized = true`. While that flag is active, the animal skips both mating and eating for the day. Keep the instinct list short and put mating first.

For herbivores (both sexes):
1. `See: A Potential Mate -> Go Towards`
2. `See: Food -> Go Towards`

For female carnivores:
1. `See: A Potential Mate -> Go Towards`
2. `See: Food -> Go Towards`
3. Optional, and always last: `Feel: Ready To Mate -> Cry`. Never put `Cry` above `See: Food`. `ReadyToMate` stays true even after fertilization until pregnancy starts, so a higher-priority `Cry` stops females from hunting and starves them.

For male carnivores:
1. `See: A Potential Mate -> Go Towards`
2. Optional (pair with `elven` ears): `Hear: A Sound -> Go Towards`
3. `See: Food -> Go Towards`

Do not add `Feel: Too Warm -> Go Away From` or `Feel: Too Cold -> Go Away From`. Those instincts lock animals out of eating and mating for entire seasons.

## Herbivores

Start with the core setup at size 11 or 12 (`hoof` legs, `stalk` eyes, `Feathered` skin) and use a `chew` mouth (`Grass: 3.0`, `SoftPlantTissue: 2.0`). Grass regrows reliably on flat ground and supports populations of 2,000 to 5,000+ animals. Fruit, seed, and fungus mouths work too once those food sources are dense, though grass is much easier to establish early on.

Spawn both Sex A and Sex B together in one tight cluster on flat grass. With `stalk` eyes, 15 to 20 total founders (roughly half of each sex) is enough. With short-range eyes like `receptor`, drop 40 to 60+ founders in the same spot so they grow dense before spreading out.

## Obligate carnivores

Obligate carnivores (`eatsMeat = true`, `eatsPlants = false`) have tighter limits in the simulation than herbivores:

1. Walking speed must be above `2.0`. In vanilla `TimeLine.cs`, a predator at `speed <= 2.0` tries to move onto the prey's occupied cell and aborts the hunt. Use `hoof` legs (`speed = 3.5`).
2. Match body size to your prey depending on what kind of ecosystem you want. At the same size as the prey (for example, size 12 carnivore and size 12 herbivore), adult carnivores cannot swallow adult prey whole because `carnSize > preySize` is false. They eat prey juveniles (`isChild`) and carcasses instead. That adult prey refuge prevents predators from wiping out the herbivore breeding stock, which gives stable multi-thousand-day Lotka-Volterra cycles, though you need 1,500 to 2,000+ herbivores first since only about 30% are juveniles. At one size larger (size 13 vs size 12), carnivores eat adults too and can survive starting around 800 to 1,000 herbivores, but without an adult refuge a predator boom can wipe out all herbivores unless the continent has a high herbivore carrying capacity.
3. Use a `scavenger` mouth (`OldMeat: 7.0`, `FreshMeat: 7.0`, `WholeAnimal: 6.0`). It hunts live prey, leaves prey eggs alone so herbivore recruitment keeps up, and falls back on carcasses during prey troughs.
4. Keep `stalk` eyes (20-cell 360-degree vision) and `elven` ears (`hear = 4.0`). Carnivores sit at much lower populations than herbivores (roughly 50 to 250 compared to 1,500 to 3,000), so long sight range is needed to find mates during population dips.
5. Wait until herbivores reach at least 1,500 to 2,000 (same-size predator) or 800 to 1,000 (+1 size predator), then drop 25 to 30+ carnivores (both Sex A and Sex B) directly on the densest herbivore cluster.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Founders scatter and die of `OldAgeNoMate` | Short-range eyes (`receptor` or large `pseudopupil`) or too few baby founders | Switch eyes to `stalk` (`#15`) and spawn 20+ of both sexes in one spot |
| Carnivores get fertilized (100%) but lay few eggs and starve | Prey density is too low (same-size carnivores only eat juveniles and carcasses) | Time-skip herbivores to 1,500 to 2,000+ first, or raise carnivore size by 1 |
| Carnivores fail to catch live prey | Walking speed is `<= 2.0` | Switch to faster legs (`hoof`, speed `3.5`) |
| An established sexual species stops mating after 200 to 4,000 days | Vanilla `AnimalMorph.Clone` male-instinct overwrite bug | Install updated `SaplingRustBridge.dll` |
| Animals freeze at `speed = 0` after reloading a low-gravity save | Vanilla `GameSaver` hardcodes `gravity = 1f` on load | Install updated `SaplingRustBridge.dll` |
| Animals stop eating and mating in winter or summer | `TooWarm` or `TooCold -> GoAwayFrom` instinct is blocking other actions | Remove temperature `GoAwayFrom` instincts and use `Feathered` skin |
