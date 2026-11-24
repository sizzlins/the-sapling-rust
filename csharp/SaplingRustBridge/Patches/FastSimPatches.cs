using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Zero-allocation, Boo-reflection-free replacements for the measured Time Jump hot loops.
    ///
    /// Every method here is a line-for-line faithful port of the decompiled retail C# with three
    /// strictly behaviour-preserving optimizations:
    ///   1. Direct null checks (`!= null`) instead of `Boo.Lang.Runtime.RuntimeServices.EqualityOperator`
    ///      (which performs dynamic runtime type/reflection dispatch on every cell check).
    ///   2. Zero-allocation scratch buffers for distance bands, seed lists, visible-direction masks,
    ///      and predation counters — eliminating millions of short-lived heap allocations per second
    ///      that were driving Mono GC2 stop-the-world pauses during Time Jumps.
    ///   3. Skipping the boxed `new Vector2` UI-hint array pushes during active Time Jumps (vanilla
    ///      `JumpAhead` discards those arrays after every batch anyway).
    /// </summary>
    [HarmonyPatch]
    public static class FastSimPatches
    {
        public static bool Enabled = true;

        // Re-entrant-safe scratch buffers for band iteration (max retail band is 128 ints).
        private const int MaxBandScratchDepth = 4;
        private static readonly int[][] _bandScratch = new int[MaxBandScratchDepth][]
        {
            new int[128], new int[128], new int[128], new int[128]
        };
        private static int _bandDepth;

        // Reusable scratch buffers for plant seed filtering, predation counting,
        // fungus reproduction, travel steps, and animal iteration (eliminates per-day churn).
        private static readonly List<int> _seedKeepScratch = new List<int>(16);
        private static readonly List<int> _fungusCellsScratch = new List<int>(128);
        private static readonly List<int> _travelStepsScratch = new List<int>(16);
        private static readonly List<int> _emptyIntList = new List<int>(0);
        private static readonly Dictionary<int, int> _eggCounter = new Dictionary<int, int>(16);
        private static readonly Dictionary<int, int> _preyCounter = new Dictionary<int, int>(16);
        private static AnimalPosition[] _animalPosScratch = new AnimalPosition[1024];
        private static readonly object[] EmptyArgs = new object[0];
        private static readonly PlantStatsCalculator _sharedPlantStatsCalc = new PlantStatsCalculator();
        private static readonly HeatTextureBuilder _sharedHeatBuilder = new HeatTextureBuilder();
        private static readonly float[] _emptyFloatArray = new float[0];
        private static readonly float[] _tempSexBrackets = new float[7] { 0.1f, 0.3f, 0.45f, 0.55f, 0.7f, 0.9f, 1f };

        // Fast shallow copy for AnimalDefinition using CLR object.MemberwiseClone.
        // Safe for unmutated births and AnimalBlender (which only overwrites scalar fields and
        // replaces the instincts array); AnimalMutator still receives a full deep .Clone().
        private delegate object MemberwiseCloneDelegate(object obj);
        private static readonly MemberwiseCloneDelegate _memberwiseClone = CreateMemberwiseCloneDelegate();
        private static MemberwiseCloneDelegate CreateMemberwiseCloneDelegate()
        {
            try
            {
                MethodInfo mi = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null)
                {
                    return (MemberwiseCloneDelegate)Delegate.CreateDelegate(typeof(MemberwiseCloneDelegate), mi);
                }
            }
            catch { }
            return null;
        }

        // Case-preserving caches for OrganismObjectFileImporter body-part metadata lookups
        // (eliminates .ToLower() string allocations + Hashtable boxing inside AnimalStatsCalculator.Calculate).
        private static OrganismObjectFileImporter _cachedImporter;
        private static readonly Dictionary<string, string> _attackParticleCache = new Dictionary<string, string>(64, StringComparer.Ordinal);
        private static readonly Dictionary<string, MaterialType> _materialTypeCache = new Dictionary<string, MaterialType>(64, StringComparer.Ordinal);
        private static readonly Dictionary<string, MaterialType> _specialMaterialTypeCache = new Dictionary<string, MaterialType>(64, StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _lightIntensityCache = new Dictionary<string, float>(64, StringComparer.Ordinal);

        private static void EnsureImporterCache(OrganismObjectFileImporter importer)
        {
            if (!object.ReferenceEquals(_cachedImporter, importer))
            {
                _cachedImporter = importer;
                _attackParticleCache.Clear();
                _materialTypeCache.Clear();
                _specialMaterialTypeCache.Clear();
                _lightIntensityCache.Clear();
            }
        }

        // Cached night-cycle lookup per (TimeLine, dayIndex, dayDuration) so
        // UpdateCurrentlyActiveInstinctForAnimal doesn't recompute it 6,000+ times/day.
        private static TimeLine _cachedNightTl;
        private static int _cachedNightDay = int.MinValue;
        private static float _cachedNightDur = -1f;
        private static float _cachedNightUnwarped;

        // Zero-alloc sub-phase tick accumulators for LetAllAnimalsInAreaLiveADay_Prefix,
        // reported once every 5 seconds by ProfilerPatches so we know exact ms per phase.
        private static long _aTicksLive;
        private static long _aTicksDie;
        private static long _aTicksEatDmg;
        private static long _aTicksBirth;
        private static long _aTicksBirthGive;
        private static long _aTicksBirthBlend;
        private static long _aTicksBirthClone;
        private static long _aTicksBirthMut;
        private static long _aTicksBirthProc;
        private static long _aTicksBirthSpawn;
        private static long _aTicksInstinct;
        private static long _aTicksTravel;
        private static long _aTicksMateFood;
        private static long _aCountAnimals;
        private static long _aCountBirths;
        private static long _aCountMutations;
        private static long _aCountInstinctCalls;

        public static string ConsumeAnimalBreakdown()
        {
            long total = _aTicksLive + _aTicksDie + _aTicksEatDmg + _aTicksBirth + _aTicksInstinct + _aTicksTravel + _aTicksMateFood;
            if (total == 0) return null;
            double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string line = string.Format(
                "[PROF] animals n={0} births={1} muts={15} instCalls={2} | live={3:0.0}ms die={4:0.0}ms eatDmg={5:0.0}ms birth={6:0.0}ms(g={10:0.0} b={11:0.0} cl={16:0.0} m={12:0.0} p={13:0.0} s={14:0.0}) instinct={7:0.0}ms travel={8:0.0}ms mateFood={9:0.0}ms",
                _aCountAnimals, _aCountBirths, _aCountInstinctCalls,
                _aTicksLive * k, _aTicksDie * k, _aTicksEatDmg * k, _aTicksBirth * k,
                _aTicksInstinct * k, _aTicksTravel * k, _aTicksMateFood * k,
                _aTicksBirthGive * k, _aTicksBirthBlend * k, _aTicksBirthMut * k, _aTicksBirthProc * k, _aTicksBirthSpawn * k,
                _aCountMutations, _aTicksBirthClone * k);
            _aTicksLive = _aTicksDie = _aTicksEatDmg = _aTicksBirth = _aTicksInstinct = _aTicksTravel = _aTicksMateFood = 0;
            _aTicksBirthGive = _aTicksBirthBlend = _aTicksBirthClone = _aTicksBirthMut = _aTicksBirthProc = _aTicksBirthSpawn = 0;
            _aCountAnimals = _aCountBirths = _aCountMutations = _aCountInstinctCalls = 0;
            return line;
        }

        // =========================================================================
        // 1. LivingPlant.LiveADay — eliminates 1M+/s `new List<int>()` allocations
        //    (LivingPlant.cs:204-281)
        // =========================================================================
        [HarmonyPatch(typeof(LivingPlant), nameof(LivingPlant.LiveADay))]
        [HarmonyPrefix]
        public static bool LivingPlant_LiveADay_Prefix(LivingPlant __instance, int periodIndex)
        {
            if (!Enabled || __instance == null) return true;

            __instance.age++;
            if (__instance.burning)
            {
                __instance.health -= __instance.damageByFire;
                if (!(__instance.health > 0f))
                {
                    __instance.dying = true;
                }
            }
            if (__instance.dormant)
            {
                __instance.healthDeterioratesAtAge++;
                __instance.maximumAge++;
                if (__instance.maximumAge >= __instance.absoluteMaximumAge)
                {
                    __instance.dying = true;
                }
                return false;
            }
            if (!((float)__instance.age < __instance.healthDeterioratesAtAge))
            {
                __instance.health -= __instance.healthDeteriorationRatePerDayAtOldAge;
            }
            __instance.health -= __instance.damagePerDay;
            __instance.health -= __instance.parasiteDamage;
            if (__instance.age >= __instance.maximumAge || !(__instance.health >= 0f))
            {
                __instance.dying = true;
            }

            float num = 0f;
            switch (periodIndex)
            {
                case 2:
                    num = __instance.energyProfitPerDayDuringDayTime;
                    break;
                case 1:
                case 3:
                    num = __instance.energyProfitPerDayAtTwilight;
                    break;
                case 0:
                case 4:
                    num = __instance.energyProfitPerDayAtNight;
                    break;
            }

            // Vanilla allocates `new List<int>()` on every plant every day even though
            // nothing in LiveADay populates it. Clearing when non-empty is 0 allocs.
            var almostReady = __instance.indicesOfFruitsThatAreAlmostReady;
            if (almostReady == null)
            {
                __instance.indicesOfFruitsThatAreAlmostReady = new List<int>();
            }
            else if (almostReady.Count > 0)
            {
                almostReady.Clear();
            }

            if (!__instance.damageByDrought && !__instance.damageByHighTemperature && !__instance.damageByLowTemperature &&
                !__instance.damageByWind && !__instance.burning && !__instance.damageByMoisture)
            {
                int nrOfSeeds = __instance.nrOfSeeds;
                float[] energySaved = __instance.energySavedPerSeed;
                float needed = __instance.energyNeededForFruitProduction;
                var ready = __instance.indicesOfSeedsThatAreReady;
                for (int i = 0; i < nrOfSeeds; i++)
                {
                    float saved = energySaved[i];
                    if (saved != -1f)
                    {
                        if (!(saved + num >= needed))
                        {
                            energySaved[i] = saved + num;
                            break;
                        }
                        ready.Add(i);
                        num -= needed - saved;
                        energySaved[i] = -1f;
                    }
                }
            }

            if (__instance.young && __instance.age >= __instance.ageToGrowOld)
            {
                __instance.young = false;
            }

            if (!(__instance.nectarAvailabilityPercentage >= 1f))
            {
                __instance.nectarAvailabilityPercentage += __instance.nectarRegenerationSpeed;
                if (!(__instance.nectarAvailabilityPercentage < 1f))
                {
                    __instance.nectarAvailabilityPercentage = 1f;
                    __instance.hasNectar = true;
                }
            }

            return false;
        }

        // =========================================================================
        // 2. TimeLine.LetAllPlantsInAreaLiveADay — 28-50% of Time Jump CPU
        //    (TimeLine.cs:1388-1731)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllPlantsInAreaLiveADay))]
        [HarmonyPrefix]
        public static bool TimeLine_LetAllPlantsInAreaLiveADay_Prefix(TimeLine __instance, int areaIndex)
        {
            if (!Enabled || __instance == null || __instance.today == null || __instance.today.areas == null)
            {
                return true;
            }

            WorldArea worldArea = __instance.today.areas[areaIndex];
            if (worldArea == null || worldArea.cells == null || worldArea.cellIndicesOfPlants == null)
            {
                return true;
            }

            worldArea.indicesOfCellsWhereAPlantDied.Clear();
            bool jumping = TimeJumpPatches.IsTimeJumpActive;
            WorldCell[] cells = worldArea.cells;
            var plantIndices = worldArea.cellIndicesOfPlants;
            int period = __instance.currentNightCyclePeriod;
            bool randomBoosts = __instance.randomEnergyBoosts;
            bool popTrackerActive = __instance.populationSizeTracker != null && __instance.populationSizeTracker.active;
            bool usePopClass = __instance.usePopulationBasedClassification;
            float lightningChance = __instance.chanceLightningCausesWildFire;
            bool checkLightning = !(lightningChance <= 0f) && __instance.weatherSimulation != null && __instance.weatherSimulation.weatherPerCell != null;
            var weatherMap = checkLightning ? __instance.weatherSimulation.weatherPerCell : null;

            float sizeX = worldArea.size.x;
            float sizeY = worldArea.size.y;
            bool canDirectBand = BandPatches.Enabled && BandPatches.ManagedBandReady && (int)sizeY > 0 && sizeX > 0f;

            for (int num5 = plantIndices.Count - 1; num5 >= 0; num5--)
            {
                int num = plantIndices[num5];
                WorldCell worldCell = cells[num];
                LivingPlant livingPlant = worldCell.plant;
                if (livingPlant == null) continue;

                livingPlant.LiveADay(period);

                if (!(worldCell.distanceToWater <= 4f))
                {
                    float n = worldCell.nutrients - 0.001f;
                    worldCell.nutrients = (n < 0f) ? 0f : ((n > 1f) ? 1f : n);
                }

                if (randomBoosts)
                {
                    float[] eps = livingPlant.energySavedPerSeed;
                    for (int i = 0; i < eps.Length; i++)
                    {
                        if (eps[i] != -1f)
                        {
                            eps[i] += UnityEngine.Random.Range(0f, 0.5f);
                            break;
                        }
                    }
                }

                // Vanilla JumpAhead discards these three arrays every batch, so skip
                // boxing `new Vector2(areaIndex, num)` during active Time Jumps.
                if (!jumping)
                {
                    if (livingPlant.damageByWind)
                    {
                        __instance.areaAndCellIndexOfPlantsRecentlyDamagedByWind.Push(new Vector2(areaIndex, num));
                    }
                    else if (livingPlant.damageByHighTemperature)
                    {
                        __instance.areaAndCellIndexOfPlantsRecentlyDamagedByHighTemperature.Push(new Vector2(areaIndex, num));
                    }
                    else if (livingPlant.damageByLowTemperature)
                    {
                        __instance.areaAndCellIndexOfPlantsRecentlyDamagedByLowTemperature.Push(new Vector2(areaIndex, num));
                    }
                }

                if (livingPlant.usesWindPollination && livingPlant.age == livingPlant.ageToSpreadPollen)
                {
                    int num8 = livingPlant.nrOfPollen / 1000;
                    int maxDist = livingPlant.pollenMovementDistance;
                    int ownDefIdx = livingPlant.definition.definitionIndex;
                    int[] ownOrgans = livingPlant.shapeIndicesOfWindPollinationOrgans;

                    if (canDirectBand && _bandDepth < MaxBandScratchDepth)
                    {
                        int[] buf = _bandScratch[_bandDepth++];
                        try
                        {
                            for (int num11 = 0; num11 < num8; num11++)
                            {
                                int num9 = UnityEngine.Random.Range(0, maxDist);
                                if (num9 <= 0 || num9 > 20) continue;
                                int written = BandPatches.FillBandDirect(sizeX, sizeY, num, num9, 0x0F, buf);
                                for (int num6 = 0; num6 < written; num6++)
                                {
                                    int num10 = buf[num6];
                                    if (num10 == -1) continue;
                                    LivingPlant livingPlant2 = cells[num10].plant;
                                    if (livingPlant2 != null && !livingPlant2.fertilized && livingPlant2.usesWindPollination &&
                                        livingPlant2.definition.definitionIndex != ownDefIdx &&
                                        livingPlant2.CanBeFertilizedByPollenFromTheseWindPollinationOrgans(ownOrgans))
                                    {
                                        livingPlant2.Fertilize(livingPlant.definition, false, livingPlant.usesAnimalPollination);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            _bandDepth--;
                        }
                    }
                    else
                    {
                        for (int num11 = 0; num11 < num8; num11++)
                        {
                            int num9 = UnityEngine.Random.Range(0, maxDist);
                            int[] array = worldArea.GetAllCellsWithinDistanceBandOfCellWithIndex(num, num9, true, true, true, true);
                            for (int num6 = 0; num6 < array.Length; num6++)
                            {
                                int num10 = array[num6];
                                if (num10 == -1) continue;
                                LivingPlant livingPlant2 = cells[num10].plant;
                                if (livingPlant2 != null &&
                                    livingPlant2.CanBeFertilizedByPollenFromTheseWindPollinationOrgans(ownOrgans) &&
                                    livingPlant2.definition.definitionIndex != ownDefIdx)
                                {
                                    livingPlant2.Fertilize(livingPlant.definition, false, livingPlant.usesAnimalPollination);
                                }
                            }
                        }
                    }
                }

                if (livingPlant.nutrientsOkay)
                {
                    var readySeeds = livingPlant.indicesOfSeedsThatAreReady;
                    int readyCount = readySeeds.Count;
                    if (readyCount > 0)
                    {
                        _seedKeepScratch.Clear();
                        for (int num6 = 0; num6 < readyCount; num6++)
                        {
                            int num12 = readySeeds[num6];
                            if (livingPlant.age < livingPlant.minimumAgeBeforeNextSeed)
                            {
                                _seedKeepScratch.Add(num12);
                                continue;
                            }

                            int num13 = -1;
                            if (livingPlant.floatsInWaterPerSeed[num12] &&
                                worldCell.linkedCoastCellIndices != null &&
                                worldCell.linkedCoastCellIndices.Length > 0)
                            {
                                num13 = worldCell.linkedCoastCellIndices[UnityEngine.Random.Range(0, worldCell.linkedCoastCellIndices.Length)];
                            }
                            else
                            {
                                int num14 = livingPlant.movementDistancePerSeed[num12];
                                if (num14 < 1)
                                {
                                    _seedKeepScratch.Add(num12);
                                    continue;
                                }
                                if (!livingPlant.animalPollinated && !(UnityEngine.Random.value <= 1f))
                                {
                                    continue;
                                }
                                num13 = __instance.DisperseSeed(worldArea, num, num14, livingPlant.sinksInWaterPerSeed[num12]);
                            }

                            if (num13 != -1)
                            {
                                WorldCell seedCell = cells[num13];
                                LivingPlant seed = __instance.PickSeedFromPlant(livingPlant);
                                bool plantChanged = livingPlant.fertilized || livingPlant.mutatedLatestOffspring;
                                __instance.PlantSeed(seed, worldArea, seedCell, areaIndex, num13, plantChanged, false);
                            }
                        }

                        readySeeds.Clear();
                        for (int k = 0; k < _seedKeepScratch.Count; k++)
                        {
                            readySeeds.Add(_seedKeepScratch[k]);
                        }
                    }
                }

                if (!livingPlant.nutrientsOkay && !(livingPlant.chanceOfCatchingAnimal <= 0f))
                {
                    LivingAnimal animalInCell = worldCell.animal;
                    if (animalInCell != null && !(UnityEngine.Random.value >= livingPlant.chanceOfCatchingAnimal))
                    {
                        animalInCell.eaten = true;
                        livingPlant.nutrientsOkay = true;
                        int num17;
                        int num18;
                        if (usePopClass)
                        {
                            num17 = __instance.populationBasedAnimalClassifier.LookUp(animalInCell.definition.definitionIndex);
                            num18 = __instance.populationBasedPlantClassifier.LookUp(livingPlant.definition.definitionIndex);
                        }
                        else
                        {
                            num17 = __instance.speciesIndexPerDefinitionIndex[animalInCell.definition.definitionIndex];
                            num18 = __instance.speciesIndexPerDefinitionIndex[livingPlant.definition.definitionIndex];
                        }
                        __instance.eatenByTracker.TrackSpeciesInteraction(num17, num18, DetailedFoodType.WholeAnimal);
                        __instance.RegisterDeathCause(animalInCell.definition.definitionIndex, animalInCell.manuallyAdded, DeathCause.CaughtByPlant, OrganismType.Animal);
                        worldArea.indicesOfCellsWhereAnAnimalDied.Add(num);
                        worldArea.locationTypesOfCellsWhereAnAnimalDied.Add(CellLocationType.Ground);
                        worldArea.RemoveAnimalFromCell(num, CellLocationType.Ground);
                    }
                }

                if (livingPlant.dying)
                {
                    worldArea.RemovePlantFromCell(num5, num);
                    if (livingPlant.nutrientsOkay && livingPlant.indicesOfSeedsThatAreReady.Count > 0)
                    {
                        int seedCount = livingPlant.indicesOfSeedsThatAreReady.Count;
                        var list2 = new List<LivingPlant>(seedCount);
                        var list3 = new List<bool>(seedCount);
                        for (int num12 = 0; num12 < seedCount; num12++)
                        {
                            list2.Add(__instance.PickSeedFromPlant(livingPlant));
                            list3.Add(livingPlant.mutatedLatestOffspring);
                        }
                        __instance.PlantSeedPackage(list2, worldArea, areaIndex, num, list3);
                    }
                    if (livingPlant.infectedByParasite != null)
                    {
                        __instance.FungusReproduces(livingPlant.infectedByParasite, worldArea, areaIndex, num, -1);
                    }
                    worldCell.detritus = (int)((float)worldCell.detritus + (PlantStats.PlantSizeToNumber(livingPlant.definition.plantSize) - 9f));

                    DeathCause deathCause = default(DeathCause);
                    if (livingPlant.burning) deathCause = DeathCause.Wildfire;
                    else if (livingPlant.damageByWind) deathCause = DeathCause.DamagedByWind;
                    else if (livingPlant.damageByLowTemperature) deathCause = DeathCause.TooColdForPlant;
                    else if (livingPlant.damageByHighTemperature) deathCause = DeathCause.TooWarmForPlant;
                    else if (livingPlant.damageByDrought) deathCause = DeathCause.DamagedByDrought;
                    else if (livingPlant.damageByMoisture) deathCause = DeathCause.DamagedByMoisture;
                    else if (livingPlant.energyProfitPerDayDuringDayTime != 0f)
                    {
                        if (livingPlant.expectedAmountOfFruits != 0)
                        {
                            if (livingPlant.expectedAmountOfFruits == 1) deathCause = DeathCause.NotEnoughOffspring;
                            else deathCause = (livingPlant.parasiteDamage <= 0f) ? DeathCause.OldAge : DeathCause.Parasite;
                        }
                        else deathCause = DeathCause.NotEnoughEnergyForOffspring;
                    }
                    else if (livingPlant.waterNotUsedPerDay == 0f) deathCause = DeathCause.NotEnoughGroundWater;
                    else if (livingPlant.sunLightNotUsedPerDay == 0f) deathCause = DeathCause.NotEnoughSunLight;
                    else Debug.LogWarning("Death cause cannot be determined.");

                    __instance.RegisterDeathCause(livingPlant.definition.definitionIndex, livingPlant.manuallyAdded, deathCause, OrganismType.Plant);

                    LivingAnimal animalInTree = worldCell.animalInTree;
                    if (animalInTree != null)
                    {
                        int num19 = (worldCell.animal == null) ? num : worldArea.FindEmptyCellNearCell(num);
                        if (num19 == -1)
                        {
                            animalInTree.dying = true;
                        }
                        else
                        {
                            worldArea.RemoveAnimalFromCell(num, CellLocationType.Canopy);
                            worldArea.AddAnimalToCell(animalInTree, num19, CellLocationType.Ground);
                            animalInTree.UpdateTemperature(cells[num19].temperature);
                            __instance.AttachSeedsToAnimal(animalInTree, worldArea, num19);
                        }
                    }
                }
                else if (popTrackerActive)
                {
                    int speciesId = 0;
                    if (usePopClass)
                    {
                        switch (livingPlant.organismType)
                        {
                            case OrganismType.Plant: speciesId = __instance.populationBasedPlantClassifier.LookUp(livingPlant.definition.definitionIndex); break;
                            case OrganismType.Alga: speciesId = __instance.populationBasedAlgaClassifier.LookUp(livingPlant.definition.definitionIndex); break;
                            case OrganismType.Animal: speciesId = __instance.populationBasedAnimalClassifier.LookUp(livingPlant.definition.definitionIndex); break;
                            case OrganismType.Fungus: speciesId = __instance.populationBasedFungusClassifier.LookUp(livingPlant.definition.definitionIndex); break;
                        }
                    }
                    else
                    {
                        speciesId = __instance.speciesIndexPerDefinitionIndex[livingPlant.definition.definitionIndex];
                    }
                    __instance.populationSizeTracker.Count(speciesId, livingPlant.manuallyAdded);
                    if (livingPlant.infectedByParasite != null)
                    {
                        speciesId = usePopClass
                            ? __instance.populationBasedFungusClassifier.LookUp(livingPlant.infectedByParasite.definition.definitionIndex)
                            : __instance.speciesIndexPerDefinitionIndex[livingPlant.infectedByParasite.definition.definitionIndex];
                        __instance.populationSizeTracker.Count(speciesId, livingPlant.infectedByParasite.manuallyAdded);
                    }
                }

                if (livingPlant.burning)
                {
                    int[] around = worldArea.GetAllCellsDirectlyAround(num);
                    for (int j = 0; j < around.Length; j++)
                    {
                        int adj = around[j];
                        if (adj == -1) continue;
                        WorldCell adjCell = cells[adj];
                        __instance.RemoveGrassFromCell(adjCell, adj);
                        if (CanBurnFast(adjCell))
                        {
                            __instance.IgnitePlantInCell(adjCell.plant, adjCell, false);
                            __instance.IgniteDormantSeedInCell(worldArea, adjCell, areaIndex, adj);
                        }
                    }
                }
                else if (checkLightning && CanBurnFast(worldCell))
                {
                    WeatherType wt;
                    if (weatherMap.TryGetValue(num, out wt) && wt == WeatherType.DryThunder &&
                        !(UnityEngine.Random.Range(0f, 1f) >= lightningChance))
                    {
                        __instance.IgnitePlantInCell(worldCell.plant, worldCell, false);
                        __instance.IgniteDormantSeedInCell(worldArea, worldCell, areaIndex, num);
                    }
                }
            }

            if (!__instance.plantCapped && __instance.plantOrganismCap != -1 && plantIndices.Count > __instance.plantOrganismCap)
            {
                __instance.plantCapped = true;
            }
            else if (__instance.plantCapped && (__instance.plantOrganismCap == -1 || plantIndices.Count < __instance.plantOrganismCap))
            {
                __instance.plantCapped = false;
            }
            __instance.plantMutationTracker.EndOfDay();
            return false;
        }

        private static bool CanBurnFast(WorldCell cell)
        {
            if (cell.shallowGroundWater > 0.5f || cell.temperature < 0.5f || cell.oceanDepth != -1f)
            {
                return false;
            }
            LivingPlant p = cell.plant;
            return p != null && !p.burning && !p.fireResistant;
        }

        // =========================================================================
        // 3. TimeLine.SearchNearbyCellsForInstinctTrigger — 12-36% of Time Jump CPU
        //    + inlined CheckCellForVisualInstinctTrigger (1.2M calls/sec!)
        //    (TimeLine.cs:3897-4093)
        // =========================================================================
        private static byte GetVisibleDirectionsMask(LivingAnimal animal)
        {
            int fov = animal.fieldOfView;
            if (fov > 180) return 0x0F; // top | left | right | bottom

            switch (animal.lookingInDirection)
            {
                case WindDirection.North:
                    return (fov <= 90) ? (byte)0x01 : (byte)0x07; // T / T+L+R
                case WindDirection.NorthEast:
                    return 0x03; // T+L
                case WindDirection.NorthWest:
                    return 0x05; // T+R
                case WindDirection.West:
                    return (fov <= 90) ? (byte)0x04 : (byte)0x0D; // R / T+R+B
                case WindDirection.SouthWest:
                    return 0x0C; // R+B
                case WindDirection.East:
                    return (fov <= 90) ? (byte)0x02 : (byte)0x0B; // L / T+L+B
                case WindDirection.South:
                    return (fov <= 90) ? (byte)0x08 : (byte)0x0E; // B / L+R+B
                case WindDirection.SouthEast:
                    return 0x0A; // L+B
                default:
                    return 0x0F;
            }
        }

        private static LivingAnimal GetAnimalInCellFast(WorldCell cell, CellLocationType locationType)
        {
            switch (locationType)
            {
                case CellLocationType.Ground: return cell.animal;
                case CellLocationType.Canopy: return cell.animalInTree;
                case CellLocationType.Air: return cell.animalInTheAir;
                case CellLocationType.WaterSurface: return cell.animalAtWaterSurface;
                case CellLocationType.UnderWater: return cell.animalUnderWater;
                case CellLocationType.DeepSea: return cell.animalInDeepSea;
                default: return null;
            }
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.SearchNearbyCellsForInstinctTrigger))]
        [HarmonyPrefix]
        public static bool SearchNearbyCellsForInstinctTrigger_Prefix(
            TimeLine __instance,
            InstinctDefinition instinct,
            LivingAnimal animal,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType,
            Vector2 sensingRange,
            SpeciesDefinition ownSpecies,
            float unwarpedPlaceInDayNightCycle,
            ref int __result)
        {
            if (!Enabled || __instance == null || instinct == null || animal == null) return true;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            int res = SearchNearbyCellsFast(__instance, instinct, animal, areaIndex, cellIndex, locationType, sensingRange.x, sensingRange.y, ownSpecies, unwarpedPlaceInDayNightCycle);
            if (res == int.MinValue) return true;
            __result = res;
            return false;
        }

        private static int SearchNearbyCellsFast(
            TimeLine tl,
            InstinctDefinition instinct,
            LivingAnimal animal,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType,
            float x,
            float y,
            SpeciesDefinition ownSpecies,
            float unwarpedPlaceInDayNightCycle)
        {
            if (x == -1f || y == -1f) return -1;

            bool isSee = instinct.conditionSense == InstinctConditionSense.See;
            bool isHear = instinct.conditionSense == InstinctConditionSense.Hear;
            InstinctConditionObject cond = instinct.conditionObject;
            int cursor = tl.cursorCellIndex;

            if (isSee)
            {
                if (y < 1f) return -1;
                // Matches retail TimeLine.cs:4053-4056: OwnSpecies always returns false.
                if (cond == InstinctConditionObject.OwnSpecies) return -1;
                // Matches retail TimeLine.cs:4018-4020: ALight only matches cursorCellIndex.
                if (cond == InstinctConditionObject.ALight && cursor == -1) return -1;
                if (cond == InstinctConditionObject.Food && animal.orderedDetailedFoodTypes.Length == 0) return -1;
            }

            WorldArea worldArea = tl.today.areas[areaIndex];
            float sizeX = worldArea.size.x;
            float sizeY = worldArea.size.y;
            int sizeXInt = (int)sizeX;
            int sizeYInt = (int)sizeY;
            if (sizeYInt <= 0 || sizeX <= 0f) return int.MinValue;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return int.MinValue;

            bool useCompact = ((float)sizeXInt == sizeX) && ((float)sizeYInt == sizeY);
            int centerX = (int)Math.Floor((double)((float)cellIndex / sizeY));
            int centerY = cellIndex % sizeYInt;

            WorldCell[] cells = worldArea.cells;
            byte mask = isSee ? GetVisibleDirectionsMask(animal) : (byte)0x0F;

            int num4 = (int)x;
            bool flag = true;
            int[] buf = _bandScratch[_bandDepth++];

            try
            {
                while (true)
                {
                    if (num4 >= 1 && num4 <= 20)
                    {
                        int written = useCompact
                            ? BandPatches.FillBandCompact(sizeXInt, sizeYInt, centerX, centerY, num4, mask, buf)
                            : BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, num4, mask, buf);

                        if (isSee)
                        {
                            for (int i = 0; i < written; i++)
                            {
                                int num3 = buf[i];
                                if (!useCompact && num3 == -1) continue;
                                if (CheckCellVisualFast(tl, worldArea, cells, areaIndex, num3, animal, instinct, cond, cursor, unwarpedPlaceInDayNightCycle, locationType, ownSpecies))
                                {
                                    return num3;
                                }
                            }
                        }
                        else if (!flag && isHear)
                        {
                            float minPitch = instinct.conditionRange.x;
                            float maxPitch = instinct.conditionRange.y;
                            for (int i = 0; i < written; i++)
                            {
                                int num3 = buf[i];
                                if (!useCompact && num3 == -1) continue;
                                LivingAnimal animalInCell = GetAnimalInCellFast(cells[num3], locationType);
                                if (animalInCell == null) continue;
                                SoundDefinition stepSound = animalInCell.stepSound;
                                if (stepSound == null || (float)num4 - stepSound.loudness > y) continue;
                                float[] pitches = stepSound.pitches;
                                for (int p = 0; p < pitches.Length; p++)
                                {
                                    float pitch = pitches[p];
                                    if (pitch > minPitch && pitch < maxPitch)
                                    {
                                        return num3;
                                    }
                                }
                            }
                        }
                    }

                    num4++;
                    if (!((float)num4 <= y))
                    {
                        if (isSee) return -1;
                        if (flag)
                        {
                            flag = false;
                        }
                        else if (!((float)num4 <= y + 4f))
                        {
                            return -1;
                        }
                    }
                }
            }
            finally
            {
                _bandDepth--;
            }
        }

        private static bool CheckCellVisualFast(
            TimeLine tl,
            WorldArea area,
            WorldCell[] cells,
            int areaIndex,
            int cellIndex,
            LivingAnimal animal,
            InstinctDefinition instinct,
            InstinctConditionObject cond,
            int cursorCellIndex,
            float unwarpedPlaceInDayNightCycle,
            CellLocationType locationType,
            SpeciesDefinition ownSpecies)
        {
            if (cond == InstinctConditionObject.ALight)
            {
                return cellIndex == cursorCellIndex;
            }
            if (cond == InstinctConditionObject.Food)
            {
                return CellContainsFoodFast(tl, area, cells, areaIndex, cellIndex, CellLocationType.Ground, animal) != -1;
            }

            WorldCell worldCell = cells[cellIndex];

            if (cond == InstinctConditionObject.APlant)
            {
                LivingPlant p = worldCell.plant;
                if (p == null) return false;
                PlantDefinition pdef = p.definition;
                if (!(pdef.colorHue > instinct.conditionRange.x && pdef.colorHue < instinct.conditionRange.y &&
                      pdef.colorBrightness > instinct.conditionAdditionalRange.x && pdef.colorBrightness < instinct.conditionAdditionalRange.y))
                {
                    return false;
                }
                float r2x = instinct.conditionAdditionalRange2.x;
                float r2y = instinct.conditionAdditionalRange2.y;
                return (pdef.fruitHue > r2x && pdef.fruitHue < r2y) || (pdef.flowerHue > r2x && pdef.flowerHue < r2y);
            }
            if (cond == InstinctConditionObject.AnAlga)
            {
                LivingAlga a = worldCell.alga ?? worldCell.plankton;
                if (a == null) return false;
                AlgaDefinition adef = a.definition;
                return adef.colorHue > instinct.conditionRange.x && adef.colorHue < instinct.conditionRange.y &&
                       adef.colorBrightness > instinct.conditionAdditionalRange.x && adef.colorBrightness < instinct.conditionAdditionalRange.y;
            }
            if (worldCell.nestType != NestType.None && worldCell.nestEnclosed)
            {
                return false;
            }

            LivingAnimal animalInCell = GetAnimalInCellFast(worldCell, locationType);
            if (animalInCell == null) return false;

            if (cond == InstinctConditionObject.AnAnimal)
            {
                AnimalDefinition def = animalInCell.definition;
                int patIdx = (int)def.skinPattern;
                if (patIdx < 0 || patIdx >= instinct.conditionChoices.Length || !instinct.conditionChoices[patIdx]) return false;
                return def.colorHue > instinct.conditionRange.x && def.colorHue < instinct.conditionRange.y &&
                       def.colorBrightness > instinct.conditionAdditionalRange.x && def.colorBrightness < instinct.conditionAdditionalRange.y &&
                       def.secondaryColorHue > instinct.conditionAdditionalRange2.x && def.secondaryColorHue < instinct.conditionAdditionalRange2.y &&
                       def.secondaryColorBrightness > instinct.conditionAdditionalRange3.x && def.secondaryColorBrightness < instinct.conditionAdditionalRange3.y;
            }
            if (cond == InstinctConditionObject.AColor)
            {
                return !(animalInCell.mostSalientColor <= instinct.conditionRange.x) &&
                       !(animalInCell.mostSalientColor >= instinct.conditionRange.y) &&
                       !(animalInCell.colorBrightness <= instinct.conditionAdditionalRange.x) &&
                       !(animalInCell.colorBrightness >= instinct.conditionAdditionalRange.y);
            }
            if (cond == InstinctConditionObject.APotentialMate)
            {
                int num = tl.populationBasedAnimalClassifier.LookUp(animal.definition.definitionIndex);
                if (num == -1) return false;
                SpeciesDefinition rhs = tl.allSpecies[num];
                bool sameSpecies = object.ReferenceEquals(ownSpecies, rhs);
                if (!sameSpecies)
                {
                    HashSet<int> ancestors = tl.GetAncestorSpeciesIndicesForAnimal(animal);
                    int[] ancArr = new int[ancestors.Count];
                    ancestors.CopyTo(ancArr);
                    if (!tl.SpeciesCanFertilizeAnimal(ancArr, animalInCell))
                    {
                        return false;
                    }
                }
                if ((animal.ReadyForInternalFertilization(false) && animalInCell.ReadyForInternalFertilization(true)) ||
                    (animal.ReadyForInternalFertilization(true) && animalInCell.ReadyForInternalFertilization(false)))
                {
                    return true;
                }
                return false;
            }

            return InstinctTools.LifeFormMatchesInstinctTrigger(instinct, animalInCell, unwarpedPlaceInDayNightCycle, false, false);
        }

        // =========================================================================
        // 4. TimeLine.CellContainsFoodTypeWithIndexForAnimal — 9-19% of Time Jump CPU
        //    (TimeLine.cs:6517-6865)
        // =========================================================================
        private static float GetRealFoodHeightFast(PlantDefinition plantDef, float normalizedFoodHeight)
        {
            switch (plantDef.plantSize)
            {
                case PlantSize.Grass: return 0f;
                case PlantSize.ExtraSmall: return normalizedFoodHeight - 1f;
                case PlantSize.Small: return normalizedFoodHeight;
                case PlantSize.Medium: return normalizedFoodHeight + 1f;
                case PlantSize.Large: return normalizedFoodHeight + 2f;
                default: return 0f;
            }
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.CellContainsFoodTypeWithIndexForAnimal))]
        [HarmonyPrefix]
        public static bool CellContainsFoodTypeWithIndexForAnimal_Prefix(
            TimeLine __instance,
            WorldArea area,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType,
            LivingAnimal animal,
            ref int __result)
        {
            if (!Enabled || __instance == null || area == null || animal == null) return true;
            __result = CellContainsFoodFast(__instance, area, area.cells, areaIndex, cellIndex, locationType, animal);
            return false;
        }

        private static int CellContainsFoodFast(
            TimeLine tl,
            WorldArea area,
            WorldCell[] cells,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType,
            LivingAnimal animal)
        {
            if (cellIndex < 0 || cellIndex >= cells.Length) return -1;

            WorldCell worldCell = cells[cellIndex];
            tl.predationCheckRun = false;
            tl.softPlantTissueTypeChosen = SoftPlantTissueType.None;
            float num3 = animal.canEatAtHeight * 10f;
            AnimalDefinition definition = animal.definition;
            DetailedFoodType[] foodTypes = animal.orderedDetailedFoodTypes;
            int len = foodTypes.Length;

            int cachedPopSpecies = int.MinValue;
            int cachedDictSpecies = int.MinValue;

            for (int num4 = 0; num4 < len; num4++)
            {
                switch (foodTypes[num4])
                {
                    case DetailedFoodType.SoftPlantTissue:
                    {
                        if (worldCell.alga != null)
                        {
                            tl.softPlantTissueTypeChosen = SoftPlantTissueType.Alga;
                            return num4;
                        }
                        LivingPlant lp = worldCell.plant;
                        if (lp != null)
                        {
                            PlantDefinition pdef = lp.definition;
                            bool inRange = !(pdef.colorHue < definition.softPlantTissueDietHueRange.x)
                                && !(pdef.colorHue > definition.softPlantTissueDietHueRange.y)
                                && !(pdef.colorBrightness < definition.softPlantTissueDietBrightnessRange.x)
                                && !(pdef.colorBrightness > definition.softPlantTissueDietBrightnessRange.y);
                            if (definition.invertSoftPlantTissueDietColorRange ? !inRange : inRange)
                            {
                                if (pdef.barkType == BarkType.None || animal.canFly || animal.canClimb)
                                {
                                    tl.softPlantTissueTypeChosen = SoftPlantTissueType.SinglePlant;
                                    return num4;
                                }
                                float h = GetRealFoodHeightFast(pdef, lp.lowestLeafHeight);
                                if (!(num3 < h))
                                {
                                    tl.softPlantTissueTypeChosen = SoftPlantTissueType.SinglePlant;
                                    return num4;
                                }
                            }
                        }
                        break;
                    }
                    case DetailedFoodType.Grass:
                    {
                        if (worldCell.grassHeight > 0)
                        {
                            bool inRange = !(worldCell.grassHue < definition.softPlantTissueDietHueRange.x)
                                && !(worldCell.grassHue > definition.softPlantTissueDietHueRange.y)
                                && !(worldCell.grassBrightness < definition.softPlantTissueDietBrightnessRange.x)
                                && !(worldCell.grassBrightness > definition.softPlantTissueDietBrightnessRange.y);
                            if (definition.invertSoftPlantTissueDietColorRange ? !inRange : inRange)
                            {
                                return num4;
                            }
                        }
                        break;
                    }
                    case DetailedFoodType.Plankton:
                        if (worldCell.plankton != null) return num4;
                        break;
                    case DetailedFoodType.MarineSnow:
                        if (worldCell.hasDeepSeaSlot && worldCell.plankton != null) return num4;
                        break;
                    case DetailedFoodType.Sap:
                        if (worldCell.plant != null) return num4;
                        break;
                    case DetailedFoodType.Bark:
                    {
                        LivingPlant lp = worldCell.plant;
                        if (lp != null && lp.definition.barkType != BarkType.None) return num4;
                        break;
                    }
                    case DetailedFoodType.Nectar:
                    {
                        LivingPlant lp = worldCell.plant;
                        if (lp != null && lp.hasNectar) return num4;
                        break;
                    }
                    case DetailedFoodType.Seed:
                    {
                        LivingPlant lp = worldCell.plant;
                        if (lp != null && lp.lowestSeedHeight != -1f &&
                            GetRealFoodHeightFast(lp.definition, lp.lowestSeedHeight) <= num3)
                        {
                            return num4;
                        }
                        break;
                    }
                    case DetailedFoodType.Nut:
                    {
                        LivingPlant lp = worldCell.plant;
                        if (lp != null && lp.lowestNutHeight != -1f &&
                            GetRealFoodHeightFast(lp.definition, lp.lowestNutHeight) <= num3)
                        {
                            return num4;
                        }
                        break;
                    }
                    case DetailedFoodType.Fruit:
                    {
                        LivingPlant lp = worldCell.plant;
                        if (lp != null && lp.lowestFruitHeight != -1f &&
                            GetRealFoodHeightFast(lp.definition, lp.lowestFruitHeight) <= num3)
                        {
                            PlantDefinition pdef = lp.definition;
                            bool inRange = !(pdef.fruitHue < definition.fruitDietHueRange.x)
                                && !(pdef.fruitHue > definition.fruitDietHueRange.y);
                            if ((definition.invertFruitDietColorRange ? !inRange : inRange) &&
                                !definition.excludeFruitShapesFromDiet.Contains(lp.fruitShape))
                            {
                                return num4;
                            }
                        }
                        break;
                    }
                    case DetailedFoodType.WholeAnimal:
                    {
                        int sp = LookupAnimalSpecies(tl, definition.definitionIndex, ref cachedPopSpecies);
                        if (sp == -1) break;
                        if (!tl.predationCheckRun) PredationCheckFast(tl, animal, sp, area, cellIndex, locationType);
                        if (tl.predationCheckWholeAnimalCellIndexChosen != -1) return num4;
                        break;
                    }
                    case DetailedFoodType.FreshMeat:
                    {
                        if (locationType == CellLocationType.WaterSurface || worldCell.carrionSize == -1 || worldCell.carrionOld) break;
                        int sp = LookupAnimalSpecies(tl, definition.definitionIndex, ref cachedPopSpecies);
                        if (sp != -1 && worldCell.carrionSpeciesIndex != sp) return num4;
                        break;
                    }
                    case DetailedFoodType.OldMeat:
                    {
                        if (locationType == CellLocationType.WaterSurface || worldCell.carrionSize == -1) break;
                        int sp = LookupAnimalSpecies(tl, definition.definitionIndex, ref cachedPopSpecies);
                        if (sp != -1 && worldCell.carrionSpeciesIndex != sp) return num4;
                        break;
                    }
                    case DetailedFoodType.Egg:
                    {
                        // Matches retail TimeLine.cs:6784 (checks speciesIndexPerDefinitionIndex only).
                        int sp = LookupAnimalSpeciesDictOnly(tl, definition.definitionIndex, ref cachedDictSpecies);
                        if (sp == -1) break;
                        if (!tl.predationCheckRun) PredationCheckFast(tl, animal, sp, area, cellIndex, locationType);
                        if (tl.predationCheckEggCellIndexChosen != -1) return num4;
                        break;
                    }
                    case DetailedFoodType.Blood:
                    {
                        int sp = LookupAnimalSpecies(tl, definition.definitionIndex, ref cachedPopSpecies);
                        if (sp == -1) break;
                        if (!tl.predationCheckRun) PredationCheckFast(tl, animal, sp, area, cellIndex, locationType);
                        if (tl.predationCheckBloodCellIndexChosen != -1) return num4;
                        break;
                    }
                    case DetailedFoodType.Milk:
                    {
                        int sp = (cachedPopSpecies != int.MinValue) ? cachedPopSpecies : -1;
                        if (!tl.predationCheckRun) PredationCheckFast(tl, animal, sp, area, cellIndex, locationType);
                        if (tl.predationCheckMilkCellIndexChosen != -1) return num4;
                        break;
                    }
                    case DetailedFoodType.Fungus:
                    {
                        // Matches retail TimeLine.cs:6838-6858 verbatim (returns num4 whenever fungus != null).
                        if (worldCell.fungus != null) return num4;
                        break;
                    }
                }
            }

            return -1;
        }

        private static int LookupAnimalSpecies(TimeLine tl, int defIdx, ref int cached)
        {
            if (cached != int.MinValue) return cached;
            if (tl.usePopulationBasedClassification)
            {
                cached = tl.populationBasedAnimalClassifier.LookUp(defIdx);
            }
            else
            {
                int val;
                cached = tl.speciesIndexPerDefinitionIndex.TryGetValue(defIdx, out val) ? val : -1;
            }
            return cached;
        }

        private static int LookupAnimalSpeciesDictOnly(TimeLine tl, int defIdx, ref int cached)
        {
            if (cached != int.MinValue) return cached;
            int val;
            cached = tl.speciesIndexPerDefinitionIndex.TryGetValue(defIdx, out val) ? val : -1;
            return cached;
        }

        // =========================================================================
        // 5. TimeLine.PredationCheck — eliminates 200k+/s `new Dictionary<int,int>()`
        //    (TimeLine.cs:7525-7713)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.PredationCheck))]
        [HarmonyPrefix]
        public static bool PredationCheck_Prefix(
            TimeLine __instance,
            LivingAnimal carnivore,
            int carnivoreSpeciesIndex,
            WorldArea area,
            int indexOfBaseCell,
            CellLocationType locationType,
            ref int __result)
        {
            if (!Enabled || __instance == null || carnivore == null || area == null) return true;
            __result = PredationCheckFast(__instance, carnivore, carnivoreSpeciesIndex, area, indexOfBaseCell, locationType);
            return false;
        }

        private static bool CountFast(Dictionary<int, int> counter, int key)
        {
            int cur;
            counter.TryGetValue(key, out cur);
            cur++;
            counter[key] = cur;
            return cur >= 3;
        }

        private static int PredationCheckFast(
            TimeLine tl,
            LivingAnimal carnivore,
            int carnivoreSpeciesIndex,
            WorldArea area,
            int indexOfBaseCell,
            CellLocationType locationType)
        {
            if (locationType != CellLocationType.Ground &&
                locationType != CellLocationType.UnderWater &&
                locationType != CellLocationType.WaterSurface)
            {
                return -1;
            }

            tl.predationCheckWholeAnimalCellIndexChosen = -1;
            tl.predationCheckBloodCellIndexChosen = -1;
            tl.predationCheckEggCellIndexChosen = -1;
            tl.predationCheckEggIndexChosen = -1;

            bool prudent = carnivore.prudentPredation;
            if (prudent)
            {
                if (_eggCounter.Count > 0) _eggCounter.Clear();
                if (_preyCounter.Count > 0) _preyCounter.Clear();
            }

            WindDirection[] dirs = tl.windDirections;
            WorldCell[] cells = area.cells;
            bool usePop = tl.usePopulationBasedClassification;
            AnimalDefinition definition = carnivore.definition;
            int parentLf = carnivore.lifeFormIndexOfParent;
            bool carnIsChild = carnivore.isChild;

            for (int i = -1; i < dirs.Length; i++)
            {
                int num2 = (i != -1)
                    ? area.StepFromCellWithIndexInDirection(indexOfBaseCell, dirs[i], false, true)
                    : indexOfBaseCell;
                if (num2 == -1) continue;

                WorldCell worldCell = cells[num2];
                if (locationType == CellLocationType.WaterSurface ||
                    (worldCell.nestType != NestType.None && worldCell.nestEnclosed))
                {
                    continue;
                }

                LivingAnimal[] eggs = worldCell.eggs;
                for (int num = 0; num < eggs.Length; num++)
                {
                    LivingAnimal egg = eggs[num];
                    if (egg == null) continue;

                    int num3;
                    if (usePop)
                    {
                        num3 = tl.populationBasedAnimalClassifier.LookUp(egg.definition.definitionIndex);
                        if (num3 == -1) continue;
                    }
                    else
                    {
                        if (!tl.speciesIndexPerDefinitionIndex.TryGetValue(egg.definition.definitionIndex, out num3)) continue;
                    }

                    if (num3 != carnivoreSpeciesIndex && (!prudent || CountFast(_eggCounter, num3)))
                    {
                        tl.predationCheckEggCellIndexChosen = num2;
                        tl.predationCheckEggIndexChosen = num;
                    }
                }

                LivingAnimal livingAnimal = (locationType == CellLocationType.Ground)
                    ? worldCell.animal
                    : ((locationType == CellLocationType.UnderWater) ? worldCell.animalUnderWater : worldCell.animalAtWaterSurface);
                if (livingAnimal == null) continue;

                if (parentLf == livingAnimal.lifeFormIndex)
                {
                    tl.predationCheckMilkCellIndexChosen = num2;
                }

                AnimalDefinition definition2 = livingAnimal.definition;
                int num4;
                if (usePop)
                {
                    num4 = tl.populationBasedAnimalClassifier.LookUp(definition2.definitionIndex);
                    if (num4 == -1) continue;
                }
                else
                {
                    if (!tl.speciesIndexPerDefinitionIndex.TryGetValue(definition2.definitionIndex, out num4)) continue;
                }

                if (num4 == carnivoreSpeciesIndex || livingAnimal.hidingInShell || !(livingAnimal.camouflaged <= 0.75f))
                {
                    continue;
                }

                bool inRange = !(definition2.colorHue < definition.wholeAnimalDietHueRange.x)
                    && !(definition2.colorHue > definition.wholeAnimalDietHueRange.y)
                    && !(definition2.colorBrightness < definition.wholeAnimalDietBrightnessRange.x)
                    && !(definition2.colorBrightness > definition.wholeAnimalDietBrightnessRange.y)
                    && !(definition2.secondaryColorHue < definition.wholeAnimalDietSecondaryHueRange.x)
                    && !(definition2.secondaryColorHue > definition.wholeAnimalDietSecondaryHueRange.y)
                    && !(definition2.secondaryColorBrightness < definition.wholeAnimalDietSecondaryBrightnessRange.x)
                    && !(definition2.secondaryColorBrightness > definition.wholeAnimalDietSecondaryBrightnessRange.y)
                    && !definition.wholeAnimalDietExcludeSkinPatterns.Contains(definition2.skinPattern);

                if (definition.invertWholeAnimalDietColorRange ? inRange : !inRange)
                {
                    continue;
                }

                int carnSize = definition.bodySize;
                int preySize = definition2.bodySize;

                if (carnIsChild)
                {
                    if ((carnSize - preySize > 1 || (carnSize > preySize && (livingAnimal.isChild || locationType != CellLocationType.UnderWater))) &&
                        (!prudent || CountFast(_preyCounter, definition2.definitionIndex)))
                    {
                        tl.predationCheckWholeAnimalCellIndexChosen = num2;
                        tl.predationCheckCellLocationTypeChosen = locationType;
                    }
                    if (carnSize < preySize)
                    {
                        tl.predationCheckBloodCellIndexChosen = num2;
                        tl.predationCheckCellLocationTypeChosen = locationType;
                    }
                }
                else if (!livingAnimal.isChild)
                {
                    if (carnSize > preySize && (!prudent || CountFast(_preyCounter, definition2.definitionIndex)))
                    {
                        tl.predationCheckWholeAnimalCellIndexChosen = num2;
                        tl.predationCheckCellLocationTypeChosen = locationType;
                    }
                    if (carnSize < preySize)
                    {
                        tl.predationCheckBloodCellIndexChosen = num2;
                        tl.predationCheckCellLocationTypeChosen = locationType;
                    }
                }
                else
                {
                    if ((carnSize > preySize || locationType != CellLocationType.UnderWater) &&
                        (!prudent || CountFast(_preyCounter, definition2.definitionIndex)))
                    {
                        tl.predationCheckWholeAnimalCellIndexChosen = num2;
                        tl.predationCheckCellLocationTypeChosen = locationType;
                    }
                    if (carnSize < preySize)
                    {
                        tl.predationCheckBloodCellIndexChosen = num2;
                        tl.predationCheckCellLocationTypeChosen = locationType;
                    }
                }
            }

            tl.predationCheckRun = true;
            return 0;
        }

        // =========================================================================
        // 6. TimeLine.InstinctActivatesBasedOnEchosAtCell — eliminates Boo
        //    UnityRuntimeServices enumerator + Coerce/Unbox/Update per Hear instinct
        //    (TimeLine.cs:3844-3883)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.InstinctActivatesBasedOnEchosAtCell))]
        [HarmonyPrefix]
        public static bool InstinctActivatesBasedOnEchosAtCell_Prefix(
            TimeLine __instance,
            InstinctDefinition instinct,
            Hashtable echos,
            int areaIndex,
            int cellIndex,
            Vector2 sensingRange,
            ref int __result)
        {
            if (!Enabled || __instance == null || instinct == null) return true;
            if (echos == null || echos.Count == 0)
            {
                __result = -1;
                return false;
            }

            float x = sensingRange.x;
            float y = sensingRange.y;
            float best = -1f;
            int result = -1;
            WorldArea area = __instance.today.areas[areaIndex];

            IDictionaryEnumerator en = echos.GetEnumerator();
            while (en.MoveNext())
            {
                int key = (int)en.Key;
                if (key == cellIndex) continue;
                SoundDefinition soundDef = en.Value as SoundDefinition;
                if (soundDef == null || !InstinctTools.SoundMatchesInstinctTrigger(instinct, soundDef)) continue;

                float dist = area.DistanceBetweenTwoCells(key, cellIndex);
                float effective = dist - soundDef.loudness;
                if (effective < 0f) effective = 0f;

                if (!(effective > y) && effective >= x && (best == -1f || !(effective >= best)))
                {
                    best = effective;
                    result = key;
                }
            }

            __result = result;
            return false;
        }

        // =========================================================================
        // 7. TimeLine.MatingCheck — eliminates Boo EqualityOperator on 8 neighbors
        //    per animal per day (TimeLine.cs:7370-7500)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.MatingCheck))]
        [HarmonyPrefix]
        public static bool MatingCheck_Prefix(
            TimeLine __instance,
            LivingAnimal animal,
            WorldArea area,
            int indexOfBaseCell,
            CellLocationType locationType,
            ref MatingCheckResult __result)
        {
            if (!Enabled || __instance == null || animal == null || area == null) return true;
            __result = MatingCheckFast(__instance, animal, area, indexOfBaseCell, locationType);
            return false;
        }

        private static MatingCheckResult MatingCheckFast(
            TimeLine tl,
            LivingAnimal animal,
            WorldArea area,
            int indexOfBaseCell,
            CellLocationType locationType)
        {
            bool flag = animal.ReadyForInternalFertilization(false);
            bool flag2 = animal.ReadyForExternalFertilization();
            if (!flag2 && !flag)
            {
                return (MatingCheckResult)0;
            }

            bool usePop = tl.usePopulationBasedClassification;
            int ownSpecies = usePop
                ? tl.populationBasedAnimalClassifier.LookUp(animal.definition.definitionIndex)
                : tl.speciesIndexPerDefinitionIndex[animal.definition.definitionIndex];

            float attractiveness = animal.attractiveness;
            bool computedAncestors = false;
            int[] sourceAncestors = null;
            int mateCount = 0;
            float bestAttractiveness = 0f;
            bool foundEgg = false;

            WindDirection[] dirs = tl.windDirections;
            WorldCell[] cells = area.cells;

            for (int i = 0; i < dirs.Length; i++)
            {
                int cellIdx = StepFromCellFast(area, indexOfBaseCell, dirs[i], false, true);
                if (cellIdx == int.MinValue) cellIdx = area.StepFromCellWithIndexInDirection(indexOfBaseCell, dirs[i], false, true);
                if (cellIdx == -1) continue;

                WorldCell worldCell = cells[cellIdx];

                if (flag2 && !foundEgg)
                {
                    LivingAnimal[] eggs = worldCell.eggs;
                    for (int j = 0; j < eggs.Length; j++)
                    {
                        LivingAnimal egg = eggs[j];
                        if (egg == null || egg.fertilizedAsEgg) continue;

                        int eggSp;
                        if (usePop)
                        {
                            eggSp = tl.populationBasedAnimalClassifier.LookUp(egg.definition.definitionIndex);
                            if (eggSp == -1) continue;
                        }
                        else
                        {
                            if (!tl.speciesIndexPerDefinitionIndex.TryGetValue(egg.definition.definitionIndex, out eggSp)) continue;
                        }

                        if (eggSp == ownSpecies)
                        {
                            foundEgg = true;
                            tl.matingCheckEgg = egg;
                            break;
                        }

                        if (!computedAncestors)
                        {
                            computedAncestors = true;
                            HashSet<int> anc = tl.GetAncestorSpeciesIndicesForAnimal(animal);
                            sourceAncestors = new int[anc.Count];
                            anc.CopyTo(sourceAncestors);
                        }
                        if (tl.SpeciesCanFertilizeAnimal(sourceAncestors, egg))
                        {
                            foundEgg = true;
                            tl.matingCheckEgg = egg;
                            break;
                        }
                    }
                }

                if (!flag) continue;

                LivingAnimal other = GetAnimalInCellFast(worldCell, locationType);
                if (other == null || !other.ReadyForInternalFertilization(true) || !(attractiveness >= other.attractivenessOfLastMate))
                {
                    continue;
                }

                int otherSp;
                if (usePop)
                {
                    otherSp = tl.populationBasedAnimalClassifier.LookUp(other.definition.definitionIndex);
                    if (otherSp == -1) continue;
                }
                else
                {
                    if (!tl.speciesIndexPerDefinitionIndex.TryGetValue(other.definition.definitionIndex, out otherSp)) continue;
                }

                if (otherSp == ownSpecies)
                {
                    if (bestAttractiveness == 0f || !(other.attractiveness <= bestAttractiveness))
                    {
                        tl.matingCheckOtherAnimal = other;
                        bestAttractiveness = other.attractiveness;
                    }
                    mateCount++;
                    continue;
                }

                if (!computedAncestors)
                {
                    computedAncestors = true;
                    HashSet<int> anc = tl.GetAncestorSpeciesIndicesForAnimal(animal);
                    sourceAncestors = new int[anc.Count];
                    anc.CopyTo(sourceAncestors);
                }
                if (tl.SpeciesCanFertilizeAnimal(sourceAncestors, other))
                {
                    if (bestAttractiveness == 0f || !(other.attractiveness <= bestAttractiveness))
                    {
                        tl.matingCheckOtherAnimal = other;
                        bestAttractiveness = other.attractiveness;
                    }
                    mateCount++;
                }
            }

            return (MatingCheckResult)((mateCount > 0) ? 1 : (foundEgg ? 2 : 0));
        }

        // =========================================================================
        // 8. TimeLine.TravelCanBeFinalized — removes Boo EqualityOperator on every
        //    moving animal every day (TimeLine.cs:3528-3531)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.TravelCanBeFinalized))]
        [HarmonyPrefix]
        public static bool TravelCanBeFinalized_Prefix(Travel travel, WorldCell goalCell, ref bool __result)
        {
            if (!Enabled || travel == null || goalCell == null) return true;
            __result = GetAnimalInCellFast(goalCell, travel.goalLocationType) == null;
            return false;
        }

        // =========================================================================
        // 9. Remaining zero-alloc spatial band callers — eliminates millions of
        //    int[] allocations in BandPatches that drove Mono GC2 pauses
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.DisperseSeed))]
        [HarmonyPrefix]
        public static bool DisperseSeed_Prefix(
            TimeLine __instance,
            WorldArea area,
            int startingCellIndex,
            float movementDistance,
            object sinksInWater,
            ref int __result)
        {
            if (!Enabled || __instance == null || area == null) return true;

            bool sinks = (sinksInWater is bool) ? (bool)sinksInWater : (sinksInWater != null && Convert.ToBoolean(sinksInWater));
            WorldCell[] cells = area.cells;
            int num = -1;

            if (area.cellIndicesOfPlants.Count > __instance.noCheatingAboveNumberOfLifeForms)
            {
                int direction = UnityEngine.Random.Range(0, __instance.windDirections.Length);
                num = startingCellIndex;
                for (int i = 0; (float)i < movementDistance; i++)
                {
                    int step = area.StepFromCellWithIndexInDirection(num, (WindDirection)direction, true, false);
                    if (step == -1) break;
                    num = step;
                }
                if (num == -1 || (cells[num].oceanDepth != -1f && !sinks) || cells[num].plant != null || cells[num].seed != null)
                {
                    __result = -1;
                    return false;
                }
                __result = num;
                return false;
            }

            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;
            float sizeX = area.size.x;
            float sizeY = area.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            int dist = (int)movementDistance;
            if (dist <= 0 || dist > 20)
            {
                __result = -1;
                return false;
            }

            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, startingCellIndex, dist, 0x0F, buf);
                for (int i = 0; i < written; i++)
                {
                    int idx = buf[i];
                    if (idx != -1 && (cells[idx].oceanDepth == -1f || sinks) && cells[idx].plant == null && cells[idx].seed == null)
                    {
                        num = idx;
                    }
                }
            }
            finally
            {
                _bandDepth--;
            }

            __result = num;
            return false;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.PlantSeedPackage))]
        [HarmonyPrefix]
        public static bool PlantSeedPackage_Prefix(
            TimeLine __instance,
            List<LivingPlant> seedPackage,
            WorldArea area,
            int areaIndex,
            int cellIndex,
            List<bool> changedGenes)
        {
            if (!Enabled || __instance == null || seedPackage == null || area == null) return true;
            if (seedPackage.Count == 0) return false;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = area.size.x;
            float sizeY = area.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, 1, 0x0F, buf);
                WorldCell[] cells = area.cells;
                for (int i = 0; i < seedPackage.Count; i++)
                {
                    int pick = buf[UnityEngine.Random.Range(0, written)];
                    int target = (pick == -1) ? cellIndex : pick;
                    __instance.PlantSeed(seedPackage[i], area, cells[target], areaIndex, target, changedGenes[i], true);
                }
            }
            finally
            {
                _bandDepth--;
            }

            return false;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.FindNumberOfAnimalsOfSameSpeciesAroundCell))]
        [HarmonyPrefix]
        public static bool FindNumberOfAnimalsOfSameSpeciesAroundCell_Prefix(
            TimeLine __instance,
            LivingAnimal lifeForm,
            WorldArea area,
            int cellIndex,
            CellLocationType cellLocationType,
            ref int __result)
        {
            if (!Enabled || __instance == null || lifeForm == null || area == null) return true;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = area.size.x;
            float sizeY = area.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            bool usePop = __instance.usePopulationBasedClassification;
            int ownSpecies;
            if (usePop)
            {
                ownSpecies = __instance.populationBasedAnimalClassifier.LookUp(lifeForm.definition.definitionIndex);
                if (ownSpecies == -1)
                {
                    __result = 0;
                    return false;
                }
            }
            else
            {
                if (!__instance.speciesIndexPerDefinitionIndex.TryGetValue(lifeForm.definition.definitionIndex, out ownSpecies))
                {
                    __result = 0;
                    return false;
                }
            }

            int count = 0;
            WorldCell[] cells = area.cells;
            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, 1, 0x0F, buf);
                for (int i = 0; i < written; i++)
                {
                    int idx = buf[i];
                    if (idx == -1) continue;
                    LivingAnimal other = GetAnimalInCellFast(cells[idx], cellLocationType);
                    if (other == null) continue;

                    int otherSp = usePop
                        ? __instance.populationBasedAnimalClassifier.LookUp(other.definition.definitionIndex)
                        : __instance.speciesIndexPerDefinitionIndex[other.definition.definitionIndex];
                    if (otherSp == ownSpecies) count++;
                }
            }
            finally
            {
                _bandDepth--;
            }

            __result = count;
            return false;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.FindNumberOfAnimalsAroundCell))]
        [HarmonyPrefix]
        public static bool FindNumberOfAnimalsAroundCell_Prefix(
            WorldArea area,
            int cellIndex,
            CellLocationType cellLocationType,
            ref int __result)
        {
            if (!Enabled || area == null || area.cells == null) return true;
            float sizeX = area.size.x;
            float sizeY = area.size.y;
            int sizeYInt = (int)sizeY;
            if (sizeYInt <= 0 || sizeX <= 0f) return true;

            int cx = (int)Math.Floor((double)((float)cellIndex / sizeY));
            int cy = cellIndex % sizeYInt;
            bool left = cx > 0;
            bool right = (float)cx < sizeX - 1f;
            bool down = cy > 0;
            bool up = (float)cy < sizeY - 1f;

            WorldCell[] cells = area.cells;
            int len = cells.Length;
            int count = 0;

            if (left) count += CellHasAnimalOrEggs(cells, len, (cx - 1) * sizeYInt + cy, cellLocationType);
            if (right) count += CellHasAnimalOrEggs(cells, len, (cx + 1) * sizeYInt + cy, cellLocationType);
            if (down) count += CellHasAnimalOrEggs(cells, len, cx * sizeYInt + (cy - 1), cellLocationType);
            if (up) count += CellHasAnimalOrEggs(cells, len, cx * sizeYInt + (cy + 1), cellLocationType);
            if (left && down) count += CellHasAnimalOrEggs(cells, len, (cx - 1) * sizeYInt + (cy - 1), cellLocationType);
            if (right && up) count += CellHasAnimalOrEggs(cells, len, (cx + 1) * sizeYInt + (cy + 1), cellLocationType);
            if (left && up) count += CellHasAnimalOrEggs(cells, len, (cx - 1) * sizeYInt + (cy + 1), cellLocationType);
            if (right && down) count += CellHasAnimalOrEggs(cells, len, (cx + 1) * sizeYInt + (cy - 1), cellLocationType);

            __result = count;
            return false;
        }

        private static int CellHasAnimalOrEggs(WorldCell[] cells, int len, int idx, CellLocationType loc)
        {
            if (idx < 0 || idx >= len) return 0;
            WorldCell c = cells[idx];
            return (GetAnimalInCellFast(c, loc) != null || c.ContainsEggs()) ? 1 : 0;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.FindEmptyTreeForAnimalAroundCellAtDistance))]
        [HarmonyPrefix]
        public static bool FindEmptyTreeForAnimalAroundCellAtDistance_Prefix(
            TimeLine __instance,
            LivingAnimal animal,
            WorldArea area,
            int cellIndex,
            float distance,
            ref int __result)
        {
            if (!Enabled || __instance == null || animal == null || area == null) return true;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = area.size.x;
            float sizeY = area.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            int d = (int)distance;
            if (d <= 0 || d > 20)
            {
                __result = 0; // Matches retail TimeLine.cs:6488
                return false;
            }

            WorldCell[] cells = area.cells;
            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, d, 0x0F, buf);
                for (int i = 0; i < written; i++)
                {
                    int idx = buf[i];
                    if (idx != -1 && cells[idx].animalInTree == null && __instance.TreeCanHostAnimal(cells[idx].plant, animal))
                    {
                        __result = idx;
                        return false;
                    }
                }
            }
            finally
            {
                _bandDepth--;
            }

            __result = 0;
            return false;
        }

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.FindEmptyCellNearCell))]
        [HarmonyPrefix]
        public static bool WorldArea_FindEmptyCellNearCell_Prefix(WorldArea __instance, int cellIndex, ref int __result)
        {
            if (!Enabled || __instance == null || __instance.cells == null) return true;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = __instance.size.x;
            float sizeY = __instance.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            WorldCell[] cells = __instance.cells;
            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, 1, 0x0F, buf);
                for (int i = 0; i < written; i++)
                {
                    int idx = buf[i];
                    if (idx != -1 && cells[idx].animal == null)
                    {
                        __result = idx;
                        return false;
                    }
                }
            }
            finally
            {
                _bandDepth--;
            }

            __result = -1;
            return false;
        }

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.GetEmptyCellNearCell))]
        [HarmonyPrefix]
        public static bool WorldArea_GetEmptyCellNearCell_Prefix(WorldArea __instance, int cellIndex, CellLocationType locationType, ref int __result)
        {
            if (!Enabled || __instance == null || __instance.cells == null) return true;
            WorldCell[] cells = __instance.cells;
            if (GetAnimalInCellFast(cells[cellIndex], locationType) == null)
            {
                __result = cellIndex;
                return false;
            }
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = __instance.size.x;
            float sizeY = __instance.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, 1, 0x0F, buf);
                for (int i = 0; i < written; i++)
                {
                    int idx = buf[i];
                    if (idx != -1 && GetAnimalInCellFast(cells[idx], locationType) == null)
                    {
                        __result = idx;
                        return false;
                    }
                }
            }
            finally
            {
                _bandDepth--;
            }

            __result = -1;
            return false;
        }

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.FindCellWithDifferentTemperatureThanCell))]
        [HarmonyPrefix]
        public static bool WorldArea_FindCellWithDifferentTemperature_Prefix(WorldArea __instance, int cellIndex, bool warmer, ref int __result)
        {
            if (!Enabled || __instance == null || __instance.cells == null) return true;
            WorldCell[] cells = __instance.cells;
            if (cellIndex == -1 || cellIndex >= cells.Length)
            {
                __result = -1;
                return false;
            }
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = __instance.size.x;
            float sizeY = __instance.size.y;
            if ((int)sizeY <= 0 || sizeX <= 0f) return true;

            float temperature = cells[cellIndex].temperature;
            int[] buf = _bandScratch[_bandDepth++];
            try
            {
                int written = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, 2, 0x0F, buf);
                for (int i = 0; i < written; i++)
                {
                    int idx = buf[i];
                    if (idx == -1) continue;
                    if (warmer)
                    {
                        if (!(cells[idx].temperature <= temperature))
                        {
                            __result = idx;
                            return false;
                        }
                    }
                    else
                    {
                        if (!(cells[idx].temperature >= temperature))
                        {
                            __result = idx;
                            return false;
                        }
                    }
                }
            }
            finally
            {
                _bandDepth--;
            }

            __result = -1;
            return false;
        }

        // =========================================================================
        // 10. TimeLine.FungusReproduces — eliminates the remaining 2.12M BandPatches
        //     int[] allocations + 2.12M List<int>() allocations during Time Jumps
        //     (TimeLine.cs:8685-8801)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.FungusReproduces))]
        [HarmonyPrefix]
        public static bool FungusReproduces_Prefix(
            TimeLine __instance,
            LivingFungus fungus,
            WorldArea area,
            int areaIndex,
            int cellIndex,
            int fromStomachOfSpecies)
        {
            if (!Enabled || __instance == null || area == null) return true;
            if (fungus == null || __instance.fungusCapped) return false;
            if (!BandPatches.Enabled || !BandPatches.ManagedBandReady || _bandDepth >= MaxBandScratchDepth) return true;

            float sizeX = area.size.x;
            float sizeY = area.size.y;
            int sizeXInt = (int)sizeX;
            int sizeYInt = (int)sizeY;
            if (sizeYInt <= 0 || sizeX <= 0f) return true;

            WorldCell[] cells = area.cells;
            WorldCell worldCell = cells[cellIndex];
            int dist = (int)Mathf.Ceil(fungus.sporeTravelDistance * 5f);

            _fungusCellsScratch.Clear();
            if (dist >= 1 && dist <= 20)
            {
                int[] buf = _bandScratch[_bandDepth++];
                try
                {
                    if ((float)sizeXInt == sizeX && (float)sizeYInt == sizeY)
                    {
                        int cx = (int)Math.Floor((double)((float)cellIndex / sizeY));
                        int cy = cellIndex % sizeYInt;
                        int w = BandPatches.FillBandCompact(sizeXInt, sizeYInt, cx, cy, dist, 0x0F, buf);
                        for (int i = 0; i < w; i++) _fungusCellsScratch.Add(buf[i]);
                    }
                    else
                    {
                        int w = BandPatches.FillBandDirect(sizeX, sizeY, cellIndex, dist, 0x0F, buf);
                        for (int i = 0; i < w; i++)
                        {
                            if (buf[i] != -1) _fungusCellsScratch.Add(buf[i]);
                        }
                    }
                }
                finally
                {
                    _bandDepth--;
                }
            }
            if (_fungusCellsScratch.Count == 0)
            {
                _fungusCellsScratch.Add(cellIndex);
            }

            bool isParasite = fungus.definition.myceliumType == Mycelium.Parasite;
            int[] linkedCoast = worldCell.linkedCoastCellIndices;
            bool hasCoast = !isParasite && linkedCoast != null && linkedCoast.Length > 0;
            int expected = fungus.expectedOffspring;
            bool evolution = __instance.evolution;
            float mutChance = __instance.chanceForFungusMutation;

            for (int j = 0; j < expected; j++)
            {
                LivingFungus child = fungus.Clone();
                child.birthDay = __instance.dayIndex;
                child.lifeFormIndex = __instance.nextLifeFormIndex;
                child.definitionIndexOfParent = fungus.definition.definitionIndex;

                bool mutate = evolution && (UnityEngine.Random.value < mutChance);
                FungusDefinition origDef = null;
                FungusDefinition mutDef = null;
                if (mutate)
                {
                    origDef = fungus.definition;
                    mutDef = fungus.definition.Clone();
                    if (isParasite)
                    {
                        if (!(UnityEngine.Random.value >= 0.3f))
                        {
                            mutDef.myceliumType = Mycelium.Decomposer;
                            mutDef.targetHostSpeciesIndex = -1;
                        }
                    }
                    else if (fromStomachOfSpecies != -1 && !(UnityEngine.Random.value >= 0.6f))
                    {
                        mutDef.myceliumType = Mycelium.Parasite;
                        mutDef.targetHostSpeciesIndex = fromStomachOfSpecies;
                        mutDef.hostSpeciesType = OrganismType.Animal;
                    }
                }

                int targetIdx;
                if (hasCoast && !(UnityEngine.Random.value >= 0.5f))
                {
                    targetIdx = linkedCoast[UnityEngine.Random.Range(0, linkedCoast.Length)];
                }
                else
                {
                    targetIdx = _fungusCellsScratch[UnityEngine.Random.Range(0, _fungusCellsScratch.Count)];
                }
                WorldCell targetCell = cells[targetIdx];

                LifeForm host = null;
                if (isParasite)
                {
                    host = __instance.FindHostForParasiticFungusInCell(area, targetCell, targetIdx, fungus);
                    if (host == null) continue;
                }
                else if (targetCell.fungus != null || !__instance.FungusCanSurviveInCell(fungus, targetCell))
                {
                    continue;
                }

                __instance.nextLifeFormIndex++;
                if (mutate)
                {
                    __instance.fungusMutator.MutateFungusDefinition(mutDef);
                    mutDef.definitionIndex = __instance.nextDefinitionIndex;
                    __instance.nextDefinitionIndex++;
                    __instance.fungusMutationTracker.RegisterMutation();
                    child.definition = mutDef;
                    if (!isParasite && !__instance.FungusCanSurviveInCell(child, targetCell))
                    {
                        child.definition = origDef;
                    }
                    if (isParasite)
                    {
                        mutDef.targetHostSpeciesIndex = __instance.speciesOfMostRecentParasiteHost;
                    }
                }

                if (isParasite)
                {
                    host.infectedByParasite = child;
                    host.parasiteDamage += child.hostHealthDrain;
                    if (host.organismType == OrganismType.Plant)
                    {
                        child.AdjustStatsForTreeLife();
                    }
                    __instance.TrackParasitismAct(child, __instance.speciesOfMostRecentParasiteHost, host.organismType);
                }
                else
                {
                    area.AddFungusToCell(child, targetIdx);
                    __instance.fungusAdministration.Add(areaIndex, targetIdx, 0, __instance.dayIndex, __instance.dayIndex + child.cycleDuration + UnityEngine.Random.Range(-1, 2));
                }

                __instance.FungusBelongsToSpecies(child.definition, child.definitionIndexOfParent, false, targetIdx);
                __instance.today.CountLifeForm(__instance.indexOfLastDetectedSpecies, false);
                child.debugCounted++;
            }

            return false;
        }

        // =========================================================================
        // 11. TimeLine.UpdateCurrentlyActiveInstinctForAnimal — caches nightCycle
        //     once per day instead of 6,000+ times/day and bypasses Harmony/Boo
        //     (TimeLine.cs:3533-3823)
        // =========================================================================
        private static float GetUnwarpedNightCycleFast(TimeLine tl)
        {
            if (object.ReferenceEquals(_cachedNightTl, tl) && _cachedNightDay == tl.dayIndex && _cachedNightDur == tl.dayDuration)
            {
                return _cachedNightUnwarped;
            }
            tl.nightCycleConverter.ProcessDayIndex(tl.dayIndex, tl.dayDuration, tl.nightCyclePeriodBoundaries);
            _cachedNightTl = tl;
            _cachedNightDay = tl.dayIndex;
            _cachedNightDur = tl.dayDuration;
            _cachedNightUnwarped = tl.nightCycleConverter.unwarpedPlaceInNightCycle;
            return _cachedNightUnwarped;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.UpdateCurrentlyActiveInstinctForAnimal))]
        [HarmonyPrefix]
        public static bool UpdateCurrentlyActiveInstinctForAnimal_Prefix(
            TimeLine __instance,
            LivingAnimal animal,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType)
        {
            if (!Enabled || __instance == null || animal == null) return true;
            UpdateInstinctFast(__instance, animal, areaIndex, cellIndex, locationType);
            return false;
        }

        private static void UpdateInstinctFast(TimeLine tl, LivingAnimal animal, int areaIndex, int cellIndex, CellLocationType locationType)
        {
            if (animal.actionIsInstinctivelyPrioritized || animal.scripted) return;

            int spIdx = tl.populationBasedAnimalClassifier.LookUp(animal.definition.definitionIndex);
            if (spIdx == -1) return;

            SpeciesDefinition ownSpecies = tl.allSpecies[spIdx];
            animal.indexOfCurrentlyActiveInstinct = -1;
            animal.instinctivelyInterestedInCellWithIndex = -1;

            InstinctDefinition activeInstinct = null;
            int foundCell = -1;
            float unwarped = GetUnwarpedNightCycleFast(tl);

            bool canVocalize = true;
            if (animal.ageAtLastCry != -1 && animal.age - animal.ageAtLastCry < animal.nrOfDaysBetweenCriesOrNoise) canVocalize = false;
            if (animal.ageAtLastNoise != -1 && animal.age - animal.ageAtLastNoise < animal.nrOfDaysBetweenCriesOrNoise) canVocalize = false;

            InstinctDefinition[] instincts = animal.definition.instincts;
            WorldArea area = tl.today.areas[areaIndex];
            Hashtable areaEchos = tl.today.echos[areaIndex] as Hashtable;

            for (int i = 0; i < instincts.Length; i++)
            {
                InstinctDefinition inst = instincts[i];
                if (!canVocalize && inst.action == InstinctAction.Cry) continue;

                float rangeMin = 0f;
                float rangeMax = 0f;
                InstinctConditionSense sense = inst.conditionSense;

                if (inst.action == InstinctAction.Attack)
                {
                    rangeMin = 1f;
                    rangeMax = 1f;
                }
                else if (sense == InstinctConditionSense.See)
                {
                    rangeMin = 0f;
                    rangeMax = tl.GetSeeingDistance(animal, locationType);
                }
                else if (sense == InstinctConditionSense.Hear)
                {
                    rangeMin = animal.closestHearingDistance;
                    rangeMax = animal.furthestHearingDistance;
                    if (areaEchos != null && areaEchos.Count > 0)
                    {
                        foundCell = tl.InstinctActivatesBasedOnEchosAtCell(inst, areaEchos, areaIndex, cellIndex, new Vector2(rangeMin, rangeMax));
                        if (foundCell != -1)
                        {
                            if (inst.action == InstinctAction.Glow)
                            {
                                animal.StartGlowing();
                                break;
                            }
                            animal.indexOfCurrentlyActiveInstinct = i;
                            animal.actionIsInstinctivelyPrioritized =
                                inst.action == InstinctAction.GoAwayFrom ||
                                inst.action == InstinctAction.Cry ||
                                inst.action == InstinctAction.Attack;
                            activeInstinct = inst;
                            break;
                        }
                    }
                }
                else if (sense == InstinctConditionSense.Feel)
                {
                    WorldCell wc = area.cells[cellIndex];
                    bool felt = false;
                    switch (inst.conditionObject)
                    {
                        case InstinctConditionObject.TooWarm:
                            felt = !(PlantStats.ProportionToTemperature(wc.temperature) <= animal.maximumTemperatureThisOrganismCanHandle);
                            break;
                        case InstinctConditionObject.TooCold:
                            felt = !(PlantStats.ProportionToTemperature(wc.temperature) >= animal.minimumTemperatureThisOrganismCanHandle);
                            break;
                        case InstinctConditionObject.Pain:
                            felt = animal.age - animal.lastHurtAtAge < 2;
                            break;
                        case InstinctConditionObject.Tired:
                            felt = animal.rest < 10;
                            break;
                        case InstinctConditionObject.AHormone:
                            felt = tl.HormoneTriggersInstinct(animal, inst);
                            break;
                        case InstinctConditionObject.ReadyToMate:
                            felt = animal.ReadyForExternalFertilization() ||
                                   animal.ReadyForInternalFertilization(true) ||
                                   animal.ReadyForInternalFertilization(false);
                            break;
                        case InstinctConditionObject.Pregnant:
                            felt = animal.pregnant;
                            break;
                        case InstinctConditionObject.LowHealth:
                            felt = !(animal.health >= 5f);
                            break;
                    }
                    if (felt) foundCell = cellIndex;
                }

                if (sense != InstinctConditionSense.Feel && foundCell == -1)
                {
                    int sr = SearchNearbyCellsFast(tl, inst, animal, areaIndex, cellIndex, locationType, rangeMin, rangeMax, ownSpecies, unwarped);
                    foundCell = (sr == int.MinValue)
                        ? tl.SearchNearbyCellsForInstinctTrigger(inst, animal, areaIndex, cellIndex, locationType, new Vector2(rangeMin, rangeMax), ownSpecies, unwarped)
                        : sr;
                }

                if (foundCell != -1)
                {
                    switch (inst.action)
                    {
                        case InstinctAction.Glow:
                            animal.StartGlowing();
                            goto DoneInstinctLoop;
                        case InstinctAction.Display:
                            animal.Display(true);
                            goto DoneInstinctLoop;
                        case InstinctAction.ChangeHormone:
                            if (inst.actionChoice != -1 && inst.actionChoice < animal.hormoneLevels.Length)
                            {
                                animal.hormoneLevels[inst.actionChoice] = Mathf.Clamp(animal.hormoneLevels[inst.actionChoice] + inst.actionValue * 2f - 1f, 0f, 1f);
                            }
                            goto DoneInstinctLoop;
                        case InstinctAction.ChangeSex:
                            if (inst.actionChoice != -1 && inst.actionChoice <= animal.definition.sexes.Length)
                            {
                                animal.ChangeSex(inst.actionChoice);
                                animal.UpdateStats(tl.importer, tl.gravity);
                            }
                            goto DoneInstinctLoop;
                        case InstinctAction.HideInShell:
                            animal.StartHidingInShell();
                            goto DoneInstinctLoop;
                    }
                    if (animal.canMakeNoise && inst.action == InstinctAction.MakeNoise && canVocalize)
                    {
                        animal.MakeGenericNoise();
                        break;
                    }

                    animal.indexOfCurrentlyActiveInstinct = i;
                    animal.actionIsInstinctivelyPrioritized =
                        inst.action == InstinctAction.GoAwayFrom ||
                        inst.action == InstinctAction.Cry ||
                        inst.action == InstinctAction.Attack;
                    activeInstinct = inst;
                    break;
                }
            }

            DoneInstinctLoop:
            if (animal.indexOfCurrentlyActiveInstinct != -1)
            {
                animal.lastInstinctAction = activeInstinct.action;
                if (activeInstinct.conditionSense == InstinctConditionSense.Feel)
                {
                    if (activeInstinct.conditionObject == InstinctConditionObject.TooCold ||
                        activeInstinct.conditionObject == InstinctConditionObject.TooWarm)
                    {
                        bool warmer = true;
                        if (activeInstinct.conditionObject == InstinctConditionObject.TooCold && activeInstinct.action == InstinctAction.GoTowards) warmer = false;
                        else if (activeInstinct.conditionObject == InstinctConditionObject.TooWarm && activeInstinct.action == InstinctAction.GoAwayFrom) warmer = false;

                        int target = area.FindCellWithDifferentTemperatureThanCell(cellIndex, warmer);
                        if (!(animal.speed <= 1f) && target != -1)
                        {
                            for (int k = 1; (float)k < animal.speed; k++)
                            {
                                target = area.FindCellWithDifferentTemperatureThanCell(target, warmer);
                            }
                        }
                        animal.instinctivelyInterestedInCellWithIndex = target;
                    }
                    else if (activeInstinct.conditionObject == InstinctConditionObject.Pain && activeInstinct.action == InstinctAction.GoAwayFrom)
                    {
                        animal.instinctivelyInterestedInCellWithIndex = area.FindEmptyCellNearCell(cellIndex);
                    }
                    if (animal.instinctivelyInterestedInCellWithIndex == -1)
                    {
                        animal.indexOfCurrentlyActiveInstinct = -1;
                    }
                }
                else if (foundCell == cellIndex)
                {
                    animal.indexOfCurrentlyActiveInstinct = -1;
                }
                else if (activeInstinct.action == InstinctAction.GoTowards || activeInstinct.action == InstinctAction.Attack)
                {
                    animal.instinctivelyInterestedInCellWithIndex = foundCell;
                    if (activeInstinct.action == InstinctAction.GoTowards)
                    {
                        tl.instinctivelyGoingTowardHasOccurred = true;
                    }
                }
                else if (activeInstinct.action == InstinctAction.GoAwayFrom)
                {
                    animal.instinctivelyInterestedInCellWithIndex = area.FindGoalOppositeToCell(cellIndex, foundCell, 5, false, true);
                }
            }

            if (!animal.crying && (animal.ageAtLastIdleSound == -1 || animal.age - animal.ageAtLastIdleSound > animal.nrOfDaysBetweenIdleSounds))
            {
                animal.MakeIdleSound();
            }
        }

        // =========================================================================
        // 12. TimeLine.LetAllAnimalsInAreaLiveADay — 55-65% of Time Jump CPU
        //     Eliminates per-day AnimalPosition[] allocation, Boo reflection,
        //     and nest band allocations (TimeLine.cs:1966-2494)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllAnimalsInAreaLiveADay))]
        [HarmonyPrefix]
        public static bool LetAllAnimalsInAreaLiveADay_Prefix(TimeLine __instance, int areaIndex)
        {
            if (!Enabled || __instance == null || __instance.today == null || __instance.today.areas == null) return true;
            WorldArea worldArea = __instance.today.areas[areaIndex];
            if (worldArea == null || worldArea.cells == null || worldArea.animalPositionTracker == null) return true;

            var posSet = worldArea.animalPositionTracker.animalPositions;
            int count = posSet.Count;
            if (count > _animalPosScratch.Length)
            {
                int newSize = _animalPosScratch.Length * 2;
                while (newSize < count) newSize *= 2;
                _animalPosScratch = new AnimalPosition[newSize];
            }
            posSet.CopyTo(_animalPosScratch, 0);

            WorldCell[] cells = worldArea.cells;
            int period = __instance.currentNightCyclePeriod;
            bool usePop = __instance.usePopulationBasedClassification;
            bool popActive = __instance.populationSizeTracker != null && __instance.populationSizeTracker.active;
            bool nestBuilding = __instance.nestBuilding;
            bool debugWalk = __instance.debugProceduralWalking;
            bool evolution = __instance.evolution;
            float mutChance = __instance.chanceForAnimalMutation;
            float sizeX = worldArea.size.x;
            float sizeY = worldArea.size.y;
            bool canDirectBand = BandPatches.Enabled && BandPatches.ManagedBandReady && (int)sizeY > 0 && sizeX > 0f;

            for (int i = 0; i < count; i++)
            {
                int num = _animalPosScratch[i].cellIndex;
                CellLocationType cellLocationType = _animalPosScratch[i].locationType;
                LivingAnimal livingAnimal = GetAnimalInCellFast(cells[num], cellLocationType);
                if (livingAnimal == null || livingAnimal.eaten) continue;

                _aCountAnimals++;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                LiveADayAnimalFast(livingAnimal, period);
                _aTicksLive += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                WorldCell worldCell = cells[num];

                if (livingAnimal.carryingBaby && (livingAnimal.age == livingAnimal.stopCarryingBabyAtAge || livingAnimal.dying))
                {
                    LivingAnimal baby = livingAnimal.StopCarryingBaby();
                    int emptyCell = worldArea.GetEmptyCellNearCell(num, cellLocationType);
                    if (emptyCell == -1)
                    {
                        __instance.RegisterDeathCause(baby.definition.definitionIndex, baby.manuallyAdded, DeathCause.NotEnoughSpace, OrganismType.Animal);
                    }
                    else
                    {
                        CellLocationType babyLoc = (!(cells[emptyCell].oceanDepth <= 0f)) ? CellLocationType.UnderWater : CellLocationType.Ground;
                        worldArea.AddAnimalToCell(baby, emptyCell, babyLoc);
                    }
                }

                int spIdx = usePop
                    ? __instance.populationBasedAnimalClassifier.LookUp(livingAnimal.definition.definitionIndex)
                    : __instance.speciesIndexPerDefinitionIndex[livingAnimal.definition.definitionIndex];

                if (livingAnimal.dying)
                {
                    long td = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (livingAnimal.verboseMating) Debug.Log(livingAnimal.logString);
                    if (livingAnimal.buildLifeReport) livingAnimal.LogLifeReport();

                    AnimalDefinition definition = livingAnimal.definition;
                    int definitionIndex = definition.definitionIndex;
                    worldArea.RemoveAnimalAtLocation(num, cellLocationType);
                    __instance.PlantSeedPackage(livingAnimal.seedsCarryingAround, worldArea, areaIndex, num, livingAnimal.changedGenesForSeedsCarryingAround);
                    __instance.PlantFungalSporePackage(livingAnimal.fungalSporesCarryingAround, worldArea, areaIndex, num, livingAnimal.changedGenesForFungalSporesCarryingAround, spIdx);
                    if (livingAnimal.infectedByParasite != null)
                    {
                        __instance.FungusReproduces(livingAnimal.infectedByParasite, worldArea, areaIndex, num, -1);
                    }
                    worldCell.detritus += definition.bodySize - 9;

                    bool manuallyAdded = livingAnimal.manuallyAdded;
                    EggLocation eggLoc = __instance.importer.GetLocationForEggType(definition.eggType);
                    bool eggDry = eggLoc == EggLocation.DryLand;
                    bool eggWater = eggLoc == EggLocation.UnderWater || eggLoc == EggLocation.Alga;

                    if (!(livingAnimal.oxygen > 0f))
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.Suffocated, OrganismType.Animal);
                    }
                    else if (livingAnimal.age - livingAnimal.lastHurtAtAge < 2)
                    {
                        if (!(livingAnimal.dailyPoisonDamage <= 0f)) __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.Poisoned, OrganismType.Animal);
                        else if (!(livingAnimal.parasiteDamage <= 0f)) __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.Parasite, OrganismType.Animal);
                        else __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.Hurt, OrganismType.Animal);
                    }
                    else if (livingAnimal.pregnant && !(definition.embryoTimeInsideMother >= 4f) && eggWater && cellLocationType != CellLocationType.UnderWater)
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.CouldNotReachWaterToLayEggs, OrganismType.Animal);
                    }
                    else if (livingAnimal.pregnant && !(definition.embryoTimeInsideMother >= 4f) && eggDry && cellLocationType != CellLocationType.Ground && cellLocationType != CellLocationType.Canopy)
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.CouldNotReachLandToLayEggs, OrganismType.Animal);
                    }
                    else if (!livingAnimal.belowOceanCurrentSpeedThreshold)
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.OceanCurrentTooStrong, OrganismType.Animal);
                    }
                    else if (livingAnimal.insideTemperatureRange && !(livingAnimal.energy >= 1f))
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.NothingToEat, OrganismType.Animal);
                    }
                    else if (!(PlantStats.ProportionToTemperature(worldCell.temperature) <= livingAnimal.maximumTemperatureThisOrganismCanHandle))
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.TooWarmForAnimalOrFungus, OrganismType.Animal);
                    }
                    else if (!(PlantStats.ProportionToTemperature(worldCell.temperature) >= livingAnimal.minimumTemperatureThisOrganismCanHandle))
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.TooColdForAnimalOrFungus, OrganismType.Animal);
                    }
                    else if (cellLocationType == CellLocationType.Canopy && worldCell.plant == null)
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.Fell, OrganismType.Animal);
                    }
                    else if (!(livingAnimal.parasiteDamage <= 0f))
                    {
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, DeathCause.Parasite, OrganismType.Animal);
                    }
                    else
                    {
                        DeathCause dc;
                        if (!definition.canGiveBirthAsexually && !definition.canGiveBirthSexually && !livingAnimal.hasSharedGenes)
                        {
                            dc = DeathCause.OldAgeNoMate;
                        }
                        else if (!definition.canGiveBirthAsexually && definition.canGiveBirthSexually && livingAnimal.ageAtLastMating == -1)
                        {
                            dc = DeathCause.OldAgeNoMate;
                        }
                        else
                        {
                            dc = (definition.embryoTimeInsideMother == 4f || !livingAnimal.pregnant || livingAnimal.nrOfChildren != 0)
                                ? DeathCause.OldAge
                                : DeathCause.OldAgeLaidNoEggs;
                        }
                        __instance.RegisterDeathCause(definitionIndex, manuallyAdded, dc, OrganismType.Animal);
                    }

                    worldArea.indicesOfCellsWhereAnAnimalDied.Add(num);
                    worldArea.locationTypesOfCellsWhereAnAnimalDied.Add(cellLocationType);
                    __instance.AddCarrionToCell(definition, areaIndex, num);
                    _aTicksDie += System.Diagnostics.Stopwatch.GetTimestamp() - td;
                    continue;
                }

                if (popActive)
                {
                    __instance.populationSizeTracker.Count(spIdx, livingAnimal.manuallyAdded);
                    if (livingAnimal.infectedByParasite != null)
                    {
                        int fungSp = usePop
                            ? __instance.populationBasedFungusClassifier.LookUp(livingAnimal.infectedByParasite.definition.definitionIndex)
                            : __instance.speciesIndexPerDefinitionIndex[livingAnimal.infectedByParasite.definition.definitionIndex];
                        __instance.populationSizeTracker.Count(fungSp, livingAnimal.infectedByParasite.manuallyAdded);
                    }
                }

                if (livingAnimal.dormant || livingAnimal.sleeping || livingAnimal.hidingInShell) continue;

                if (livingAnimal.eating)
                {
                    long te = System.Diagnostics.Stopwatch.GetTimestamp();
                    if ((livingAnimal.eatingFoodOfDetailedType == DetailedFoodType.Plankton || livingAnimal.eatingFoodOfDetailedType == DetailedFoodType.MarineSnow) && livingAnimal.filteringCells != null)
                    {
                        for (int j = 0; j < livingAnimal.filteringCells.Count; j++)
                        {
                            int fc = livingAnimal.filteringCells[j];
                            __instance.EatingAnimalDoesDamage(livingAnimal, worldArea, areaIndex, cells[fc], fc, cellLocationType);
                        }
                    }
                    else if (livingAnimal.aquaticBehavior != LimbAquaticBehavior.Sessile)
                    {
                        __instance.EatingAnimalDoesDamage(livingAnimal, worldArea, areaIndex, worldCell, num, cellLocationType);
                    }
                    else if (worldCell.plankton != null)
                    {
                        __instance.TrackEatingAct(livingAnimal, worldCell.plankton, OrganismType.Alga, DetailedFoodType.Plankton);
                    }
                    if (livingAnimal.attackingCell != -1) livingAnimal.attackingCell = -1;
                    livingAnimal.eating = false;
                    if (livingAnimal.verboseTravel) livingAnimal.ChangeLastLog("eating");
                    _aTicksEatDmg += System.Diagnostics.Stopwatch.GetTimestamp() - te;
                    continue;
                }

                if (livingAnimal.wantsToHibernate && __instance.CellContainsCorrectNestForAnimal(worldCell, livingAnimal))
                {
                    livingAnimal.dormant = true;
                    continue;
                }

                if (livingAnimal.isChild && livingAnimal.canHangAsBaby && __instance.BabyIsNearParent(livingAnimal, worldArea, num, cellLocationType))
                {
                    __instance.mostRecentCloseParent.StartCarryingBaby(livingAnimal);
                    worldArea.RemoveAnimalFromCell(num, cellLocationType);
                    continue;
                }

                if (livingAnimal.crying)
                {
                    __instance.MakeSoundOnLocation(livingAnimal.crySound, areaIndex, num);
                    livingAnimal.crying = false;
                    continue;
                }

                if (livingAnimal.makingGenericNoise)
                {
                    __instance.MakeSoundOnLocation(livingAnimal.noiseSound, areaIndex, num);
                    livingAnimal.makingGenericNoise = false;
                    continue;
                }

                if (livingAnimal.readyToGiveBirth &&
                    (livingAnimal.timeInWater == -1 || cellLocationType != CellLocationType.UnderWater) &&
                    __instance.CellContainsCorrectNestForAnimal(worldCell, livingAnimal) &&
                    (worldCell.oceanDepth != -1f || livingAnimal.canWalk || livingAnimal.sessile) &&
                    __instance.EggCanSurviveInCell(worldCell, livingAnimal) &&
                    __instance.EnoughSpaceForReproduction(livingAnimal, worldArea, areaIndex, num, cellLocationType) &&
                    !debugWalk)
                {
                    _aCountBirths++;
                    long tb = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool defChanged = false;
                    bool willBlend = livingAnimal.fertilized && livingAnimal.fertilizedByAnimal != null &&
                                     livingAnimal.definition.definitionIndex != livingAnimal.fertilizedByAnimal.definitionIndex;

                    LivingAnimal child = GiveBirthFast(livingAnimal, worldCell.temperature, willBlend);
                    child.lifeFormIndexOfParent = livingAnimal.lifeFormIndex;
                    int parentDefIdx = child.definition.definitionIndex;
                    long tb1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _aTicksBirthGive += tb1 - tb;

                    if (livingAnimal.fertilized)
                    {
                        child.definition = BlendAnimalsFast(__instance.animalBlender, child.definition, livingAnimal.fertilizedByAnimal);
                        defChanged = __instance.animalBlender.madeChanges;
                    }
                    long tb2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _aTicksBirthBlend += tb2 - tb1;

                    if (evolution && !(UnityEngine.Random.value >= mutChance))
                    {
                        _aCountMutations++;
                        // AnimalMutator mutates torso/sexes/HashSets in place, so ensure an isolated deep clone.
                        int savedSex = child.definition.currentSex;
                        AnimalDefinition deep = CloneAnimalDefinitionFast(child.definition);
                        deep.currentSex = savedSex;
                        child.definition = deep;
                        long tb2b = System.Diagnostics.Stopwatch.GetTimestamp();
                        _aTicksBirthClone += tb2b - tb2;

                        if (__instance.dayIndex > 10000 && __instance.epochs[__instance.epochs.Count - 1].currentNrOfTerrestrialAnimalSpecies < 6 && cells[__instance.latestCellToReproduceIn].oceanDepth == -1f)
                        {
                            __instance.animalMutator.CheatEvolveLandAnimal(child.definition);
                        }
                        else
                        {
                            __instance.animalMutator.SetStats(
                                livingAnimal.canGlow,
                                livingAnimal.crySound != null,
                                livingAnimal.attack > 0,
                                livingAnimal.canMakeNoise,
                                livingAnimal.canHideInShell,
                                livingAnimal.extraAttractivenessWhileDisplaying > 0f,
                                livingAnimal.definition.hormones.Length,
                                livingAnimal.definition.sexes.Length,
                                livingAnimal.timeOnLand);
                            bool flag6 = !child.canWalk && child.timeInWater == -1;
                            bool flag7 = child.canWalk && child.timeOnLand == -1 && child.speed < 0.5f;
                            try
                            {
                                __instance.animalMutator.MutateAnimalDefinition(child.definition, child.orderedDetailedFoodTypes, flag6, flag7);
                            }
                            catch (NullReferenceException)
                            {
                                // Vanilla AnimalMutator.UpgradeChildren edge case; keep unmutated child.
                            }
                        }
                        __instance.animalMutationTracker.RegisterMutation();
                        defChanged = true;
                        long tb3 = System.Diagnostics.Stopwatch.GetTimestamp();
                        _aTicksBirthMut += tb3 - tb2b;
                        tb2 = tb3;
                    }
                    long tb3End = tb2;

                    if (defChanged && !ProcessNewAnimalDefinitionFast(__instance, child, livingAnimal, cellLocationType, true))
                    {
                        child = GiveBirthFast(livingAnimal, worldCell.temperature, false);
                    }
                    long tb4 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _aTicksBirthProc += tb4 - tb3End;

                    child.definitionIndexOfParent = parentDefIdx;
                    if (livingAnimal.fertilized)
                    {
                        child.definitionIndexOfOtherParent = livingAnimal.fertilizedByAnimal.definitionIndex;
                    }
                    if (nestBuilding && livingAnimal.definition.nestType != NestType.None && livingAnimal.ableToBuildNest)
                    {
                        __instance.BuildNestInCell(livingAnimal.definition.nestType, livingAnimal.definition.nestEnclosed, livingAnimal.definition.eggType, livingAnimal.definition.bodySize, areaIndex, __instance.latestCellToReproduceIn);
                        livingAnimal.energy -= livingAnimal.energyNeededForNestCreation;
                    }
                    if (child.timeInEgg == 0f)
                    {
                        __instance.BabyGetsBorn(child, worldArea, __instance.latestCellToReproduceIn);
                    }
                    else
                    {
                        __instance.LayEggInCell(child, areaIndex, __instance.latestCellToReproduceIn, livingAnimal.fertilized);
                    }
                    if (livingAnimal.verboseTravel) livingAnimal.ChangeLastLog("readyToGiveBirth");
                    long tb5 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _aTicksBirthSpawn += tb5 - tb4;
                    _aTicksBirth += tb5 - tb;
                    continue;
                }

                if (livingAnimal.pregnant && !__instance.LocationTypeIsOkayForEgg(worldCell, livingAnimal) && livingAnimal.instinctivelyInterestedInCellWithIndex == -1)
                {
                    int target = (worldCell.oceanDepth <= -1f) ? worldCell.closestAquaticCellIndex : worldCell.closestLandCellIndex;
                    if (target != -1)
                    {
                        livingAnimal.instinctivelyInterestedInCellWithIndex = target;
                        livingAnimal.actionIsInstinctivelyPrioritized = true;
                        livingAnimal.lastInstinctAction = InstinctAction.GoTowards;
                    }
                    continue;
                }

                if (nestBuilding && (livingAnimal.pregnant || livingAnimal.wantsToHibernate) && livingAnimal.ableToBuildNest &&
                    livingAnimal.definition.nestType != NestType.None &&
                    (!livingAnimal.actionIsInstinctivelyPrioritized || livingAnimal.lastInstinctAction != InstinctAction.GoTowards))
                {
                    if (__instance.CellContainsCorrectNestForAnimal(worldCell, livingAnimal)) continue;

                    int num5 = -1;
                    int num6 = -1;
                    if (canDirectBand && _bandDepth < MaxBandScratchDepth)
                    {
                        int[] buf = _bandScratch[_bandDepth++];
                        try
                        {
                            int w = BandPatches.FillBandDirect(sizeX, sizeY, num, 1, 0x0F, buf);
                            for (int k = 0; k < w; k++)
                            {
                                int idx = buf[k];
                                if (idx == -1) continue;
                                if (__instance.CellContainsCorrectNestForAnimal(cells[idx], livingAnimal))
                                {
                                    num5 = idx;
                                    break;
                                }
                                if (__instance.CellIsAppropriateNestingSpot(cells[idx], livingAnimal))
                                {
                                    num6 = idx;
                                }
                            }
                        }
                        finally
                        {
                            _bandDepth--;
                        }
                    }
                    else
                    {
                        int[] band = worldArea.GetAllCellsWithinDistanceBandOfCellWithIndex(num, 1, true, true, true, true);
                        for (int k = 0; k < band.Length; k++)
                        {
                            int idx = band[k];
                            if (idx == -1) continue;
                            if (__instance.CellContainsCorrectNestForAnimal(cells[idx], livingAnimal))
                            {
                                num5 = idx;
                                break;
                            }
                            if (__instance.CellIsAppropriateNestingSpot(cells[idx], livingAnimal))
                            {
                                num6 = idx;
                            }
                        }
                    }

                    if (num5 == -1)
                    {
                        if (__instance.CellIsAppropriateNestingSpot(worldCell, livingAnimal))
                        {
                            __instance.BuildNestInCell(livingAnimal.definition.nestType, livingAnimal.definition.nestEnclosed, livingAnimal.definition.eggType, livingAnimal.definition.bodySize, areaIndex, num);
                            livingAnimal.energy -= livingAnimal.energyNeededForNestCreation;
                        }
                        else
                        {
                            num5 = num6;
                        }
                    }
                    if (num5 != -1)
                    {
                        livingAnimal.instinctivelyInterestedInCellWithIndex = num5;
                        livingAnimal.actionIsInstinctivelyPrioritized = true;
                        livingAnimal.lastInstinctAction = InstinctAction.GoTowards;
                    }
                    continue;
                }

                if (!livingAnimal.moving || livingAnimal.instinctivelyInterestedInCellWithIndex == -1)
                {
                    _aCountInstinctCalls++;
                    long ti = System.Diagnostics.Stopwatch.GetTimestamp();
                    UpdateInstinctFast(__instance, livingAnimal, areaIndex, num, cellLocationType);
                    _aTicksInstinct += System.Diagnostics.Stopwatch.GetTimestamp() - ti;
                }

                long tt = System.Diagnostics.Stopwatch.GetTimestamp();
                Travel travel2 = ManageTravelsFast(__instance, livingAnimal, areaIndex, num, cellLocationType);
                _aTicksTravel += System.Diagnostics.Stopwatch.GetTimestamp() - tt;
                int num7;
                if (travel2 == null)
                {
                    num7 = num;
                    worldCell = cells[num7];
                }
                else
                {
                    num7 = travel2.goalCellIndex;
                    worldCell = cells[num7];
                    num = num7;
                    cellLocationType = travel2.goalLocationType;
                    if (__instance.TravelContainsDive(travel2, livingAnimal))
                    {
                        livingAnimal.Eat(travel2.diveToFoodTypeWithIndex, SoftPlantTissueType.None, travel2.diveToCellWithIndex, travel2.diveToCellWithIndex, travel2.diveToEggWithIndex, travel2.diveToCellWithIndex);
                    }
                    else if (livingAnimal.filterFeeder)
                    {
                        List<int> filtered = null;
                        var inter = travel2.intermediateCellIndices;
                        for (int l = 0; l < inter.Count; l++)
                        {
                            int cellIdx = inter[l];
                            if (cellIdx != -1 && cells[cellIdx].plankton != null)
                            {
                                if (filtered == null) filtered = new List<int>(inter.Count);
                                filtered.Add(cellIdx);
                            }
                        }
                        if (filtered != null)
                        {
                            livingAnimal.FilterCells(filtered);
                        }
                    }
                }

                if (livingAnimal.actionIsInstinctivelyPrioritized)
                {
                    if (livingAnimal.lastInstinctAction == InstinctAction.Cry && !(livingAnimal.crySound.loudness <= 0f))
                    {
                        livingAnimal.Cry();
                    }
                    else if (livingAnimal.lastInstinctAction == InstinctAction.Attack)
                    {
                        livingAnimal.Attack(livingAnimal.instinctivelyInterestedInCellWithIndex);
                        LivingAnimal victim = cells[livingAnimal.instinctivelyInterestedInCellWithIndex].animal;
                        if (victim != null)
                        {
                            int dmg = livingAnimal.attack - victim.armor;
                            if (dmg > 0) victim.Hurt(dmg);
                            if (!(livingAnimal.venomStrength <= 0f)) victim.GetPoisoned(livingAnimal.venomStrength, (int)livingAnimal.definition.venomType);
                        }
                    }
                    if (!livingAnimal.pregnant || livingAnimal.lastInstinctAction != InstinctAction.GoTowards)
                    {
                        livingAnimal.actionIsInstinctivelyPrioritized = false;
                    }
                }
                else
                {
                    if ((livingAnimal.attackingCell == -1 && worldCell == null && livingAnimal.aquaticBehavior != LimbAquaticBehavior.Sessile) || !livingAnimal.canEat || livingAnimal.readyToGiveBirth)
                    {
                        continue;
                    }
                    livingAnimal.attackingCell = -1;

                    long tmf = System.Diagnostics.Stopwatch.GetTimestamp();
                    MatingCheckResult mateRes = MatingCheckFast(__instance, livingAnimal, worldArea, num, cellLocationType);
                    if (mateRes == MatingCheckResult.Mate)
                    {
                        if (livingAnimal.verboseMating) livingAnimal.logString += "m";
                        __instance.matingCheckOtherAnimal.Fertilize(livingAnimal.definition, livingAnimal.attractiveness);
                        livingAnimal.hasSharedGenes = true;
                        if (livingAnimal.ReadyForInternalFertilization(true))
                        {
                            livingAnimal.Fertilize(__instance.matingCheckOtherAnimal.definition, __instance.matingCheckOtherAnimal.attractiveness);
                            __instance.matingCheckOtherAnimal.hasSharedGenes = true;
                        }
                        livingAnimal.ageAtLastMating = livingAnimal.age;
                        _aTicksMateFood += System.Diagnostics.Stopwatch.GetTimestamp() - tmf;
                        continue;
                    }
                    if (mateRes == MatingCheckResult.UnfertilizedEgg)
                    {
                        AnimalDefinition prevDef = __instance.matingCheckEgg.definition;
                        __instance.matingCheckEgg.definition = __instance.animalBlender.BlendAnimals(__instance.matingCheckEgg.definition, livingAnimal.definition);
                        if (__instance.animalBlender.madeChanges)
                        {
                            if (__instance.ProcessNewAnimalDefinition(__instance.matingCheckEgg, livingAnimal, cellLocationType, true))
                            {
                                __instance.matingCheckEgg.fertilizedAsEgg = true;
                                __instance.matingCheckEgg.definitionIndexOfOtherParent = livingAnimal.definition.definitionIndex;
                                livingAnimal.hasSharedGenes = true;
                            }
                            else
                            {
                                __instance.matingCheckEgg.definition = prevDef;
                            }
                        }
                        else
                        {
                            __instance.matingCheckEgg.fertilizedAsEgg = true;
                            __instance.matingCheckEgg.definitionIndexOfOtherParent = livingAnimal.definition.definitionIndex;
                        }
                        _aTicksMateFood += System.Diagnostics.Stopwatch.GetTimestamp() - tmf;
                        continue;
                    }

                    LivingPlant plantAtCell = (cellLocationType != CellLocationType.Air) ? worldCell.plant : null;
                    int foodIdx = CellContainsFoodFast(__instance, worldArea, cells, areaIndex, num7, cellLocationType, livingAnimal);
                    if (foodIdx == -1)
                    {
                        _aTicksMateFood += System.Diagnostics.Stopwatch.GetTimestamp() - tmf;
                        continue;
                    }

                    DetailedFoodType foodType = livingAnimal.orderedDetailedFoodTypes[foodIdx];
                    if (foodType == DetailedFoodType.Milk)
                    {
                        livingAnimal.Drink(livingAnimal.providesMilk);
                    }
                    else if (__instance.AnimalCanSenseFood(livingAnimal, foodType, cellLocationType))
                    {
                        livingAnimal.Eat(foodIdx, __instance.softPlantTissueTypeChosen, __instance.predationCheckWholeAnimalCellIndexChosen, __instance.predationCheckEggCellIndexChosen, __instance.predationCheckEggIndexChosen, __instance.predationCheckBloodCellIndexChosen);
                        if (foodType == DetailedFoodType.WholeAnimal || foodType == DetailedFoodType.Egg || foodType == DetailedFoodType.Blood || foodType == DetailedFoodType.FreshMeat || foodType == DetailedFoodType.OldMeat)
                        {
                            if (__instance.animalGetsEatenCallBack != null) __instance.animalGetsEatenCallBack.Call(EmptyArgs);
                        }
                    }

                    if (plantAtCell == null)
                    {
                        _aTicksMateFood += System.Diagnostics.Stopwatch.GetTimestamp() - tmf;
                        continue;
                    }

                    if (foodType == DetailedFoodType.Seed || foodType == DetailedFoodType.Fruit || foodType == DetailedFoodType.Nut)
                    {
                        __instance.MakeAnimalCarrySeedsFromPlant(livingAnimal, plantAtCell, false, true, foodType == DetailedFoodType.Nut);
                    }
                    if (foodType == DetailedFoodType.Fruit && !(plantAtCell.definition.fruitPoisonStrength <= 0f))
                    {
                        livingAnimal.GetPoisoned(plantAtCell.definition.fruitPoisonStrength, plantAtCell.definition.fruitPoisonType);
                    }
                    if (foodType == DetailedFoodType.Nectar)
                    {
                        plantAtCell.hasNectar = false;
                        plantAtCell.nectarAvailabilityPercentage = 0f;
                    }
                    if (!livingAnimal.eatsNectar)
                    {
                        _aTicksMateFood += System.Diagnostics.Stopwatch.GetTimestamp() - tmf;
                        continue;
                    }

                    if (!plantAtCell.fertilized)
                    {
                        PlantDefinition pollen = livingAnimal.CheckAttachedPollen(plantAtCell.shapeIndicesOfAnimalPollinationOrgans, plantAtCell.definition.definitionIndex);
                        if (pollen != null && pollen.definitionIndex != plantAtCell.definition.definitionIndex)
                        {
                            plantAtCell.Fertilize(pollen, true, true);
                            __instance.animalPollinationHasOccurred = true;
                        }
                    }
                    livingAnimal.AttachPollen(plantAtCell.shapeIndicesOfAnimalPollinationOrgans, plantAtCell.definition);
                    _aTicksMateFood += System.Diagnostics.Stopwatch.GetTimestamp() - tmf;
                }
            }

            int liveAnimals = worldArea.animalPositionTracker.animalPositions.Count;
            if (!__instance.animalCapped && __instance.animalOrganismCap != -1 && liveAnimals > __instance.animalOrganismCap)
            {
                __instance.animalCapped = true;
            }
            else if (__instance.animalCapped && (__instance.animalOrganismCap == -1 || liveAnimals < __instance.animalOrganismCap))
            {
                __instance.animalCapped = false;
            }
            __instance.animalMutationTracker.EndOfDay();
            return false;
        }

        // =========================================================================
        // 13. WorldArea.StepFromCellWithIndexInDirection — fast integer math,
        //     eliminates float modulo and recursive virtual calls on diagonals
        //     (WorldArea.cs:1020-1079)
        // =========================================================================
        private static bool CellIsUsableFast(WorldCell[] cells, int idx, bool unwalkableOk, bool frozenOk)
        {
            if ((uint)idx >= (uint)cells.Length) return false;
            WorldCell c = cells[idx];
            if (!c.usable && !(frozenOk && c.frozen)) return false;
            return unwalkableOk || c.walkable;
        }

        private static int StepCardinalFast(WorldCell[] cells, int sizeXInt, int sizeYInt, int maxCell, int index, WindDirection dir, bool unwalkableOk, bool frozenOk)
        {
            switch (dir)
            {
                case WindDirection.East:
                {
                    int n = index + 1;
                    if (n % sizeYInt == 0 || !CellIsUsableFast(cells, n, unwalkableOk, frozenOk)) return -1;
                    return n;
                }
                case WindDirection.West:
                {
                    int n = index - 1;
                    if (n < 0 || n % sizeYInt == sizeYInt - 1 || !CellIsUsableFast(cells, n, unwalkableOk, frozenOk)) return -1;
                    return n;
                }
                case WindDirection.North:
                {
                    int n = index - sizeXInt;
                    if (n < 0 || !CellIsUsableFast(cells, n, unwalkableOk, frozenOk)) return -1;
                    return n;
                }
                case WindDirection.South:
                {
                    int n = index + sizeXInt;
                    if (n > maxCell || !CellIsUsableFast(cells, n, unwalkableOk, frozenOk)) return -1;
                    return n;
                }
                default:
                    return 0;
            }
        }

        private static int StepFromCellFast(WorldArea area, int index, WindDirection direction, bool unwalkableOk, bool frozenOk)
        {
            float sx = area.size.x;
            float sy = area.size.y;
            int sizeXInt = (int)sx;
            int sizeYInt = (int)sy;
            if (sizeYInt <= 0 || sizeXInt <= 0 || (float)sizeXInt != sx || (float)sizeYInt != sy)
            {
                return int.MinValue;
            }
            WorldCell[] cells = area.cells;
            int maxCell = sizeXInt * sizeYInt - 1;
            switch (direction)
            {
                case WindDirection.East:
                case WindDirection.West:
                case WindDirection.North:
                case WindDirection.South:
                    return StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, direction, unwalkableOk, frozenOk);
                case WindDirection.NorthWest:
                {
                    int n = StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.North, unwalkableOk, frozenOk);
                    return (n != -1) ? StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.West, unwalkableOk, frozenOk) : -1;
                }
                case WindDirection.NorthEast:
                {
                    int n = StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.North, unwalkableOk, frozenOk);
                    return (n != -1) ? StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.East, unwalkableOk, frozenOk) : -1;
                }
                case WindDirection.SouthWest:
                {
                    int n = StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.South, unwalkableOk, frozenOk);
                    return (n != -1) ? StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.West, unwalkableOk, frozenOk) : -1;
                }
                case WindDirection.SouthEast:
                {
                    int n = StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.South, unwalkableOk, frozenOk);
                    return (n != -1) ? StepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.East, unwalkableOk, frozenOk) : -1;
                }
                default:
                    return 0;
            }
        }

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.StepFromCellWithIndexInDirection))]
        [HarmonyPrefix]
        public static bool StepFromCellWithIndexInDirection_Prefix(
            WorldArea __instance,
            int index,
            WindDirection direction,
            bool unwalkableGoalIsOkay,
            bool frozenGoalIsOkay,
            ref int __result)
        {
            if (!Enabled || __instance == null || __instance.cells == null) return true;
            int r = StepFromCellFast(__instance, index, direction, unwalkableGoalIsOkay, frozenGoalIsOkay);
            if (r == int.MinValue) return true;
            __result = r;
            return false;
        }

        // =========================================================================
        // 14. TimeLine.PlantSeed — reuses static PlantStatsCalculator and skips
        //     boxed Vector2 UI hints during Time Jumps (TimeLine.cs:1876-1956)
        // =========================================================================
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.PlantSeed))]
        [HarmonyPrefix]
        public static bool PlantSeed_Prefix(
            TimeLine __instance,
            LivingPlant seed,
            WorldArea area,
            WorldCell seedCell,
            int areaIndex,
            int indexOfSeedCell,
            bool plantChanged,
            bool randomDelay)
        {
            if (!Enabled || __instance == null || seed == null || area == null || seedCell == null) return true;

            PlantDefinition def = seed.definition;
            if (def.plantSize == PlantSize.Grass)
            {
                __instance.AddPlantDefinitionToCellAsGrass(area, areaIndex, indexOfSeedCell, def, seed.definitionIndexOfParent, false, false);
                return false;
            }

            seed.lifeFormIndex = __instance.nextLifeFormIndex++;
            if (seedCell.oceanDepth != -1f && !(seed.maximumOceanDepth >= seedCell.oceanDepth))
            {
                __instance.RegisterDeathCause(def.definitionIndex, seed.manuallyAdded, DeathCause.SeedLandedInWater, OrganismType.Plant);
                return false;
            }
            if (!(area.sunLightFactor >= 0.5f))
            {
                __instance.RegisterDeathCause(def.definitionIndex, seed.manuallyAdded, DeathCause.NotEnoughSunLight, OrganismType.Plant);
                return false;
            }

            bool jumping = TimeJumpPatches.IsTimeJumpActive;
            _sharedPlantStatsCalc.UpdateStatsForRootType(def.rootType);
            if (!_sharedPlantStatsCalc.PlantCanSurviveAtGroundSoftness(def, seedCell.softness))
            {
                if (!jumping) __instance.areaAndCellIndexOfPlantsRecentlyDiedFromSoftness.Push(new Vector2(areaIndex, indexOfSeedCell));
                DeathCause dc = default(DeathCause);
                if (!(seedCell.softness >= _sharedPlantStatsCalc.minimumSoftnessThisPlantCanHandle)) dc = DeathCause.GroundTooHard;
                else if (!(seedCell.softness <= _sharedPlantStatsCalc.maximumSoftnessThisPlantCanHandle)) dc = DeathCause.GroundTooSoft;
                __instance.RegisterDeathCause(def.definitionIndex, seed.manuallyAdded, dc, OrganismType.Plant);
                return false;
            }

            bool waterOk = !(seedCell.shallowGroundWater < _sharedPlantStatsCalc.minimumShallowGroundWaterThisOrganismRequires)
                || (def.rootDepth == 1 && !(seedCell.deepGroundWater <= 0.5f));
            if (!waterOk)
            {
                if (!jumping) __instance.areaAndCellIndexOfPlantsRecentlyDiedFromIncorrectGroundWater.Push(new Vector2(areaIndex, indexOfSeedCell));
                __instance.RegisterDeathCause(def.definitionIndex, seed.manuallyAdded, DeathCause.GroundTooDry, OrganismType.Plant);
                return false;
            }
            if (!(seedCell.shallowGroundWater <= _sharedPlantStatsCalc.maximumShallowGroundWaterThisOrganismCanHandle))
            {
                if (!jumping) __instance.areaAndCellIndexOfPlantsRecentlyDiedFromIncorrectGroundWater.Push(new Vector2(areaIndex, indexOfSeedCell));
                __instance.RegisterDeathCause(def.definitionIndex, seed.manuallyAdded, DeathCause.GroundTooWet, OrganismType.Plant);
                return false;
            }

            if (plantChanged)
            {
                seed.plantStatsCalculator.allowedColors = __instance.allowedPlantColors;
                seed.environmentTemperature = seedCell.temperature;
                PlantStatsCalculator calc = seed.FillStats();
                if (OrganismProblemDetection.DetectProblemInPlant(calc, true) != OrganismProblem.NoProblem || !seed.plantStatsCalculator.colorAllowed)
                {
                    __instance.RegisterDeathCause(def.definitionIndex, seed.manuallyAdded, DeathCause.UnluckyMutation, OrganismType.Plant);
                    return false;
                }
            }
            else
            {
                seed.UpdateEnvironmentDamageAndDormancy(seedCell.temperature, seedCell.windStrength, seedCell.shallowGroundWater);
            }

            int germDay = __instance.dayIndex + 1;
            if (randomDelay && !(UnityEngine.Random.value >= 0.5f)) germDay++;
            seedCell.seed = seed;
            seedCell.seedGerminationDay = germDay;
            __instance.seedAdministration.Add(areaIndex, indexOfSeedCell, 0, __instance.dayIndex, germDay);
            return false;
        }

        // =========================================================================
        // 15. TimeLine.ManageTravelsForAnimal — eliminates ~180k/s List<int>() and
        //     ~100k/s HeatTextureBuilder() allocations plus Boo reflection
        //     (TimeLine.cs:2946-3488)
        // =========================================================================
        private static bool AnimalIsReadyForNewTravelFast(TimeLine tl, LivingAnimal animal, WorldArea area, WorldCell[] cells, int areaIndex, int cellIndex, CellLocationType locationType)
        {
            if (animal.sessile && locationType == CellLocationType.Ground) return false;
            if (!animal.canWalk && cells[cellIndex].oceanDepth == -1f) return false;
            if (animal.indexOfCurrentlyActiveInstinct != -1 &&
                (animal.lastInstinctAction == InstinctAction.GoTowards || animal.lastInstinctAction == InstinctAction.GoAwayFrom) &&
                animal.actionIsInstinctivelyPrioritized)
            {
                return false;
            }
            if (animal.readyToGiveBirth && !(UnityEngine.Random.value <= 0.5f)) return false;

            int foodIdx = CellContainsFoodFast(tl, area, cells, areaIndex, cellIndex, locationType, animal);
            if (foodIdx != -1 && animal.orderedDetailedFoodTypes[foodIdx] != DetailedFoodType.Plankton)
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.ManageTravelsForAnimal))]
        [HarmonyPrefix]
        public static bool ManageTravelsForAnimal_Prefix(
            TimeLine __instance,
            LivingAnimal animal,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType,
            ref Travel __result)
        {
            if (!Enabled || __instance == null || animal == null || __instance.today == null) return true;
            __result = ManageTravelsFast(__instance, animal, areaIndex, cellIndex, locationType);
            return false;
        }

        private static Travel ManageTravelsFast(
            TimeLine __instance,
            LivingAnimal animal,
            int areaIndex,
            int cellIndex,
            CellLocationType locationType)
        {
            WorldArea worldArea = __instance.today.areas[areaIndex];
            WorldCell[] cells = worldArea.cells;
            Dictionary<int, Travel> travelsBySource = __instance.today.GetTravelsBySource(areaIndex, locationType);
            Dictionary<int, int> sourcesOfArrivals = __instance.today.GetSourcesOfArrivals(areaIndex);

            Travel travel;
            if (!travelsBySource.TryGetValue(cellIndex, out travel))
            {
                if (!AnimalIsReadyForNewTravelFast(__instance, animal, worldArea, cells, areaIndex, cellIndex, locationType))
                {
                    return null;
                }

                int num = -1;
                int num2 = 0;
                int num3 = 0;
                ModeOfTransport modeOfTransport = ModeOfTransport.Walk;
                CellLocationType cellLocationType = CellLocationType.Ground;
                int num4 = -1;
                int diveToCellWithIndex = -1;
                bool trackSteps = animal.filterFeeder || animal.canFly;
                if (trackSteps) _travelStepsScratch.Clear();

                if (__instance.debugProceduralWalking)
                {
                    num = worldArea.MoveSingleStepInDirectionOfCell(cellIndex, __instance.debugProceduralWalkingGoalCell, true, true);
                    modeOfTransport = ModeOfTransport.Walk;
                    animal.age = 0;
                    animal.health = 100f;
                    animal.energy = 100f;
                    animal.rest = 100;
                    animal.energyNeededToReproduce = 9999f;
                }
                else if (animal.instinctivelyInterestedInCellWithIndex != -1)
                {
                    float num5 = (locationType == CellLocationType.WaterSurface)
                        ? Mathf.Ceil(animal.swimmingSpeed + animal.waterSurfaceBoost)
                        : ((locationType == CellLocationType.UnderWater) ? Mathf.Ceil(animal.swimmingSpeed) : Mathf.Ceil(animal.speed));
                    num = cellIndex;
                    float stepsLimit = Mathf.Ceil(num5);
                    for (num3 = 0; (float)num3 < stepsLimit; num3++)
                    {
                        num = worldArea.MoveSingleStepInDirectionOfCellWithMaxSteepness(num, animal.instinctivelyInterestedInCellWithIndex, animal.maximumSteepness);
                        if (num == -1) break;
                        if (trackSteps) _travelStepsScratch.Add(num);
                        num2 = num3;
                        if (num == animal.instinctivelyInterestedInCellWithIndex) break;
                    }
                    if (num != -1)
                    {
                        num2 = (int)Mathf.Ceil(animal.speed);
                        EggLocation eggLoc = __instance.importer.GetLocationForEggType(animal.definition.eggType);
                        bool flag = eggLoc == EggLocation.DryLand;
                        bool flag3 = eggLoc == EggLocation.UnderWater || eggLoc == EggLocation.Alga;
                        if (animal.pregnant && locationType == CellLocationType.UnderWater && flag)
                        {
                            num = -1;
                            animal.actionIsInstinctivelyPrioritized = true;
                        }
                        else if (animal.pregnant && locationType == CellLocationType.WaterSurface && flag)
                        {
                            modeOfTransport = ModeOfTransport.SwimAtSurface;
                            cellLocationType = (cells[num].oceanDepth != -1f) ? CellLocationType.UnderWater : CellLocationType.Ground;
                        }
                        else if (animal.pregnant && locationType == CellLocationType.Ground && flag3)
                        {
                            if (cells[num].oceanDepth == -1f && animal.canWalk)
                            {
                                cellLocationType = CellLocationType.Ground;
                                modeOfTransport = ModeOfTransport.Walk;
                            }
                            else
                            {
                                cellLocationType = CellLocationType.UnderWater;
                                modeOfTransport = ModeOfTransport.SwimUnderWater;
                            }
                        }
                        else if (!(cells[num].oceanDepth <= 0f))
                        {
                            if (locationType == CellLocationType.WaterSurface)
                            {
                                modeOfTransport = ModeOfTransport.SwimAtSurface;
                                cellLocationType = CellLocationType.WaterSurface;
                            }
                            else if (locationType == CellLocationType.UnderWater)
                            {
                                modeOfTransport = ModeOfTransport.SwimUnderWater;
                                cellLocationType = CellLocationType.UnderWater;
                            }
                        }
                        else if (animal.canFly)
                        {
                            modeOfTransport = ModeOfTransport.Fly;
                            cellLocationType = CellLocationType.Air;
                            for (int i = 0; i < _travelStepsScratch.Count && _travelStepsScratch[i] != num; i++)
                            {
                                num4 = __instance.CellContainsFoodToEatDuringFlight(worldArea, areaIndex, _travelStepsScratch[i], CellLocationType.Ground, animal);
                                if (num4 != -1)
                                {
                                    diveToCellWithIndex = _travelStepsScratch[i];
                                    break;
                                }
                            }
                            if (num4 == -1 && CellContainsFoodFast(__instance, worldArea, cells, areaIndex, num, CellLocationType.Ground, animal) != -1)
                            {
                                cellLocationType = (!(cells[num].oceanDepth <= 0f))
                                    ? CellLocationType.WaterSurface
                                    : ((!animal.eatsMeatOnly && __instance.TreeCanHostAnimal(cells[num].plant, animal)) ? CellLocationType.Canopy : CellLocationType.Ground);
                            }
                        }
                        else if (animal.canGlide)
                        {
                            modeOfTransport = ModeOfTransport.Glide;
                            cellLocationType = (!(cells[num].oceanDepth <= 0f)) ? CellLocationType.WaterSurface : CellLocationType.Ground;
                        }
                        else if (locationType == CellLocationType.Canopy)
                        {
                            num = -1;
                        }
                    }

                    if (!(animal.speed <= 2f) && animal.eatsMeatOnly && (num == -1 || GetAnimalInCellFast(cells[num], cellLocationType) != null))
                    {
                        num = cellIndex;
                        float lim = Mathf.Ceil(animal.speed - 1f);
                        for (num3 = 1; (float)num3 < lim; num3++)
                        {
                            num = worldArea.MoveSingleStepInDirectionOfCellWithMaxSteepness(num, animal.instinctivelyInterestedInCellWithIndex, animal.maximumSteepness);
                            if (num == -1) break;
                            if (trackSteps) _travelStepsScratch.Add(num);
                            num2 = num3;
                        }
                    }
                }

                if (num == -1 || GetAnimalInCellFast(cells[num], cellLocationType) != null)
                {
                    if (animal.canFly && locationType != CellLocationType.UnderWater &&
                        (locationType != CellLocationType.WaterSurface || CellContainsFoodFast(__instance, worldArea, cells, areaIndex, cellIndex, locationType, animal) != -1))
                    {
                        if (locationType == CellLocationType.Air && CellContainsFoodFast(__instance, worldArea, cells, areaIndex, cellIndex, locationType, animal) != -1)
                        {
                            num2 = 1;
                            num = cellIndex;
                            modeOfTransport = ModeOfTransport.Fly;
                            if (!(cells[cellIndex].oceanDepth <= 0f)) cellLocationType = CellLocationType.WaterSurface;
                            else if (__instance.TreeCanHostAnimal(cells[cellIndex].plant, animal)) cellLocationType = CellLocationType.Canopy;
                        }
                        else
                        {
                            WindDirection wd = __instance.windDirections[UnityEngine.Random.Range(0, __instance.windDirections.Length)];
                            num = cellIndex;
                            for (int j = 1; (float)j <= animal.speed; j++)
                            {
                                int step = StepFromCellFast(worldArea, num, wd, true, true);
                                if (step == int.MinValue) step = worldArea.StepFromCellWithIndexInDirection(num, wd, true, true);
                                if (step == -1) break;
                                num = step;
                                num2 = j;
                                if (num4 == -1)
                                {
                                    num4 = __instance.CellContainsFoodToEatDuringFlight(worldArea, areaIndex, num, locationType, animal);
                                    diveToCellWithIndex = num;
                                }
                                if (trackSteps) _travelStepsScratch.Add(num);
                            }
                            if (cells[num].animalInTheAir != null) num = -1;
                            modeOfTransport = ModeOfTransport.Fly;
                            if (num == -1 || num4 != -1 || CellContainsFoodFast(__instance, worldArea, cells, areaIndex, num, locationType, animal) == -1)
                            {
                                cellLocationType = CellLocationType.Air;
                            }
                            else
                            {
                                cellLocationType = (!(cells[num].oceanDepth <= 0f))
                                    ? CellLocationType.WaterSurface
                                    : (__instance.TreeCanHostAnimal(cells[num].plant, animal) ? CellLocationType.Canopy : CellLocationType.Ground);
                            }
                        }
                    }
                    else if (locationType == CellLocationType.Canopy)
                    {
                        if (animal.canGlide || animal.canHangFromBranch)
                        {
                            float num7 = (animal.canGlide && !(animal.speed <= 1f)) ? Mathf.Ceil(animal.speed) : 1f;
                            for (num3 = 1; (float)num3 <= num7; num3++)
                            {
                                num = __instance.FindEmptyTreeForAnimalAroundCellAtDistance(animal, worldArea, cellIndex, num3);
                                if (num != -1)
                                {
                                    num2 = num3;
                                    modeOfTransport = ModeOfTransport.Glide;
                                    cellLocationType = CellLocationType.Canopy;
                                    break;
                                }
                            }
                        }
                        if (num == -1)
                        {
                            modeOfTransport = ModeOfTransport.Climb;
                            WindDirection wd = __instance.windDirections[UnityEngine.Random.Range(0, __instance.windDirections.Length)];
                            num = StepFromCellFast(worldArea, cellIndex, wd, false, false);
                            if (num == int.MinValue) num = worldArea.StepFromCellWithIndexInDirection(cellIndex, wd, false, false);
                            num2 = 1;
                        }
                        if (num != -1 && cells[num].animal != null) num = -1;
                    }
                    else if (locationType == CellLocationType.WaterSurface)
                    {
                        num2 = 1;
                        if (animal.eatsPlants && animal.aquaticBehavior != LimbAquaticBehavior.WalkOnWaterSurface && cells[cellIndex].alga != null && cells[cellIndex].animalUnderWater == null)
                        {
                            modeOfTransport = ModeOfTransport.SwimUnderWater;
                            cellLocationType = CellLocationType.UnderWater;
                            num = cellIndex;
                        }
                        else if (animal.eatsMeat && animal.aquaticBehavior != LimbAquaticBehavior.WalkOnWaterSurface && cells[cellIndex].animalUnderWater == null && __instance.CarnivoreFoodAvailableInCell(animal, worldArea, areaIndex, cellIndex, CellLocationType.UnderWater) != 0)
                        {
                            modeOfTransport = ModeOfTransport.SwimUnderWater;
                            cellLocationType = CellLocationType.UnderWater;
                            num = cellIndex;
                        }
                        else if ((animal.sessile || animal.aquaticBehavior == LimbAquaticBehavior.WalkOnOceanFloor) && !(cells[cellIndex].oceanDepth <= 0f))
                        {
                            modeOfTransport = ModeOfTransport.SwimUnderWater;
                            cellLocationType = CellLocationType.Ground;
                            num = cellIndex;
                        }
                        else
                        {
                            WindDirection wd = __instance.windDirections[UnityEngine.Random.Range(0, __instance.windDirections.Length)];
                            num = cellIndex;
                            float lim = Mathf.Ceil(1f + animal.waterSurfaceBoost);
                            for (int k = 0; (float)k < lim; k++)
                            {
                                int step = StepFromCellFast(worldArea, num, wd, false, true);
                                if (step == int.MinValue) step = worldArea.StepFromCellWithIndexInDirection(num, wd, false, true);
                                num = step;
                                if (num == -1 || cells[num].oceanDepth == -1f) break;
                            }
                            if (num != -1 && cells[num].animalAtWaterSurface != null) num = -1;
                            modeOfTransport = ModeOfTransport.SwimAtSurface;
                            cellLocationType = CellLocationType.WaterSurface;
                        }
                    }
                    else if (locationType == CellLocationType.UnderWater && (animal.sessile || animal.aquaticBehavior == LimbAquaticBehavior.WalkOnOceanFloor) && !(cells[cellIndex].oceanDepth <= 0f))
                    {
                        num2 = 1;
                        modeOfTransport = ModeOfTransport.SwimUnderWater;
                        cellLocationType = CellLocationType.Ground;
                        num = cellIndex;
                    }
                    else if (locationType == CellLocationType.UnderWater && ((animal.timeInWater != -1 && animal.oxygen < 4f) || !(animal.waterSurfaceBoost <= 0f)))
                    {
                        num2 = 1;
                        modeOfTransport = ModeOfTransport.SwimUnderWater;
                        cellLocationType = CellLocationType.WaterSurface;
                        num = cellIndex;
                    }
                    else if (locationType == CellLocationType.UnderWater)
                    {
                        if (cells[cellIndex].hasDeepSeaSlot && !(UnityEngine.Random.value <= 0.5f))
                        {
                            cellLocationType = CellLocationType.DeepSea;
                            modeOfTransport = ModeOfTransport.SwimUnderWater;
                            num = cellIndex;
                        }
                        else
                        {
                            WindDirection wd = __instance.windDirections[UnityEngine.Random.Range(0, __instance.windDirections.Length)];
                            num = cellIndex;
                            float lim = Mathf.Ceil(animal.swimmingSpeed);
                            for (int m = 0; (float)m < lim; m++)
                            {
                                int step = StepFromCellFast(worldArea, num, wd, false, true);
                                if (step == int.MinValue) step = worldArea.StepFromCellWithIndexInDirection(num, wd, false, true);
                                num = step;
                                if (trackSteps) _travelStepsScratch.Add(num);
                                if (num == -1 || cells[num].oceanDepth == -1f) break;
                                num2++;
                            }
                            if (num != -1 && cells[num].animalUnderWater != null) num = -1;
                            if (num != -1 && cells[num].oceanDepth == -1f)
                            {
                                if (animal.canWalk)
                                {
                                    cellLocationType = CellLocationType.Ground;
                                    modeOfTransport = ModeOfTransport.Walk;
                                }
                                else num = -1;
                            }
                            else
                            {
                                cellLocationType = CellLocationType.UnderWater;
                                modeOfTransport = ModeOfTransport.SwimUnderWater;
                            }
                        }
                    }
                    else if (locationType == CellLocationType.DeepSea)
                    {
                        WindDirection wd = __instance.windDirections[UnityEngine.Random.Range(0, __instance.windDirections.Length)];
                        num = cellIndex;
                        float lim = Mathf.Ceil(animal.swimmingSpeed);
                        for (int n = 0; (float)n < lim; n++)
                        {
                            int step = StepFromCellFast(worldArea, num, wd, false, true);
                            if (step == int.MinValue) step = worldArea.StepFromCellWithIndexInDirection(num, wd, false, true);
                            num = step;
                            if (trackSteps) _travelStepsScratch.Add(num);
                            if (num == -1 || cells[num].oceanDepth == -1f) break;
                            num2++;
                        }
                        if (num != -1 && cells[num].animalInDeepSea != null) num = -1;
                        cellLocationType = (num == -1 || cells[num].hasDeepSeaSlot) ? CellLocationType.DeepSea : CellLocationType.UnderWater;
                        modeOfTransport = ModeOfTransport.SwimUnderWater;
                    }
                    else
                    {
                        if (animal.canClimb && __instance.TreeCanHostAnimal(cells[cellIndex].plant, animal) && cells[cellIndex].animalInTree == null)
                        {
                            modeOfTransport = ModeOfTransport.Climb;
                            num = cellIndex;
                            cellLocationType = CellLocationType.Canopy;
                            num2 = 1;
                        }
                        else
                        {
                            WindDirection wd = __instance.windDirections[UnityEngine.Random.Range(0, __instance.windDirections.Length)];
                            num2 = 0;
                            num = cellIndex;
                            float lim = Mathf.Ceil(animal.speed);
                            for (int l = 0; (float)l < lim; l++)
                            {
                                num = worldArea.StepFromCellWithIndexInDirectionWithMaxSteepness(num, wd, animal.maximumSteepness);
                                if (trackSteps) _travelStepsScratch.Add(num);
                                num2++;
                            }
                            if (animal.canGlide) modeOfTransport = ModeOfTransport.Glide;
                            if (num != -1 && !(cells[num].oceanDepth <= -1f) && animal.aquaticBehavior == LimbAquaticBehavior.Swim)
                            {
                                cellLocationType = CellLocationType.WaterSurface;
                            }
                            if (num != -1 && cells[num].animal != null) num = -1;
                        }
                    }
                }

                if (num != -1 && animal.instinctivelyInterestedInCellWithIndex == -1)
                {
                    if (animal.avoidsHeat && !(PlantStats.ProportionToTemperature(cells[num].temperature) <= animal.maximumTemperatureThisOrganismCanHandle)) num = -1;
                    else if (animal.avoidsCold && !(PlantStats.ProportionToTemperature(cells[num].temperature) >= animal.minimumTemperatureThisOrganismCanHandle)) num = -1;
                }

                if (modeOfTransport == ModeOfTransport.Walk && !animal.canWalk) num = -1;
                else if (modeOfTransport == ModeOfTransport.Climb && !animal.canClimb) num = -1;
                else if (modeOfTransport == ModeOfTransport.Glide && !animal.canGlide && !animal.canHangFromBranch) num = -1;
                else if (modeOfTransport == ModeOfTransport.Fly && !animal.canFly) num = -1;
                else if (modeOfTransport == ModeOfTransport.SwimUnderWater && !animal.canSwim && (cellLocationType == CellLocationType.UnderWater || cellLocationType == CellLocationType.DeepSea)) num = -1;

                if (num != -1)
                {
                    Dictionary<int, Travel> travelsByGoal = __instance.today.GetTravelsByGoal(areaIndex, cellLocationType);
                    if (!travelsByGoal.ContainsKey(num))
                    {
                        travel = new Travel(animal, modeOfTransport, cellIndex, num, locationType, cellLocationType, num2);
                        // Only filterFeeders ever read Travel.intermediateCellIndices (TimeLine.cs:2331).
                        travel.intermediateCellIndices = (animal.filterFeeder && _travelStepsScratch.Count > 0)
                            ? new List<int>(_travelStepsScratch)
                            : _emptyIntList;
                        if (num4 != -1)
                        {
                            travel.AddDive(diveToCellWithIndex, num4, __instance.predationCheckEggIndexChosen);
                        }
                        travelsBySource[cellIndex] = travel;
                        travelsByGoal[num] = travel;
                        animal.moving = true;
                        animal.lookingInDirection = travel.direction;
                        if (travel.goalLocationType == CellLocationType.WaterSurface)
                        {
                            animal.FillUpOxygen();
                        }
                        if (animal.verboseTravel) animal.ChangeLastLog("newTravel");
                    }
                    else if (animal.verboseTravel) animal.ChangeLastLog("newTravelFailed");
                }
                else if (animal.verboseTravel) animal.ChangeLastLog("newTravelFailed");

                return null;
            }
            else
            {
                if (!(travel.progress < (float)travel.distance))
                {
                    travel.currentDuration++;
                    travel.finished = true;
                    int num8 = travel.goalCellIndex;
                    animal.moving = false;
                    if (GetAnimalInCellFast(cells[num8], travel.goalLocationType) == null)
                    {
                        worldArea.RemoveAnimalFromCell(cellIndex, travel.sourceLocationType);
                        worldArea.AddAnimalToCell(animal, num8, travel.goalLocationType);
                        __instance.AttachSeedsToAnimal(animal, worldArea, num8);

                        WorldCell worldCell = cells[num8];
                        if (worldCell.grassHeight > 0)
                        {
                            float num9 = 1f - SimulationTools.CalculateDifferenceBetweenTwoColors(animal.definition.colorHue, animal.definition.colorBrightness, worldCell.grassHue, worldCell.grassBrightness);
                            float num10 = 1f - (float)(animal.definition.bodySize - worldCell.grassHeight - 7) * 0.2f;
                            animal.camouflaged = num10 * num9;
                        }
                        else if (worldCell.oceanDepth != -1f)
                        {
                            Color color = _sharedHeatBuilder.CalculateSoilColor(worldCell.temperature, worldCell.shallowGroundWater, 0f, Color.white, 0f, 0f, false);
                            Color color2 = SimulationTools.HueAndBrightnessToColor(animal.definition.colorHue, animal.definition.colorBrightness);
                            float dr = color.r - color2.r;
                            float dg = color.g - color2.g;
                            float db = color.b - color2.b;
                            animal.camouflaged = 1f - Mathf.Sqrt(dr * dr + dg * dg + db * db);
                        }
                        else
                        {
                            animal.camouflaged = 0f;
                        }

                        sourcesOfArrivals[num8] = cellIndex;
                        if (travel.goalLocationType != CellLocationType.Canopy)
                        {
                            animal.UpdateTemperature(worldCell.temperature);
                            if (worldCell.oceanDepth != -1f)
                            {
                                animal.UpdateOceanCurrentSpeed(travel.goalLocationType == CellLocationType.DeepSea ? 0.2f : worldCell.oceanCurrentSpeed);
                            }
                        }
                        travel.succeeded = true;
                    }
                    else
                    {
                        num8 = -1;
                    }

                    __instance.today.GetTravelsBySource(areaIndex, travel.sourceLocationType).Remove(travel.sourceCellIndex);
                    __instance.today.GetTravelsByGoal(areaIndex, travel.goalLocationType).Remove(travel.goalCellIndex);
                    if (animal.instinctivelyInterestedInCellWithIndex == num8 && !animal.scripted)
                    {
                        animal.instinctivelyInterestedInCellWithIndex = -1;
                        animal.actionIsInstinctivelyPrioritized = false;
                    }
                    if (animal.verboseTravel) animal.ChangeLastLog("finalizeTravel");
                    if (num8 != -1)
                    {
                        return travel;
                    }
                }
                else
                {
                    travel.currentDuration++;
                    travel.Progress();
                    animal.moving = true;
                    if (animal.verboseTravel) animal.ChangeLastLog("travelProgress");
                }

                return null;
            }
        }

        // =========================================================================
        // 16. Fast integer steepness stepping & single-step-toward-goal
        //     (WorldArea.cs:1081-1265)
        // =========================================================================
        private static bool CellBelowSteepnessFast(WorldCell[] cells, int idx, float maxSteepness)
        {
            return (uint)idx < (uint)cells.Length && !(cells[idx].CalculateNormalizedSteepness() > maxSteepness);
        }

        private static int StepSteepCardinalFast(WorldCell[] cells, int sizeXInt, int sizeYInt, int maxCell, int index, WindDirection dir, float maxSteepness)
        {
            switch (dir)
            {
                case WindDirection.East:
                {
                    int n = index + 1;
                    if (n % sizeYInt == 0 || !CellBelowSteepnessFast(cells, n, maxSteepness)) return -1;
                    return n;
                }
                case WindDirection.West:
                {
                    int n = index - 1;
                    if (n < 0 || n % sizeYInt == sizeYInt - 1 || !CellBelowSteepnessFast(cells, n, maxSteepness)) return -1;
                    return n;
                }
                case WindDirection.North:
                {
                    int n = index - sizeXInt;
                    if (n < 0 || !CellBelowSteepnessFast(cells, n, maxSteepness)) return -1;
                    return n;
                }
                case WindDirection.South:
                {
                    int n = index + sizeXInt;
                    if (n > maxCell || !CellBelowSteepnessFast(cells, n, maxSteepness)) return -1;
                    return n;
                }
                default:
                    return 0;
            }
        }

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.StepFromCellWithIndexInDirectionWithMaxSteepness))]
        [HarmonyPrefix]
        public static bool StepFromCellWithMaxSteepness_Prefix(WorldArea __instance, int index, WindDirection direction, float maxSteepness, ref int __result)
        {
            if (!Enabled || __instance == null || __instance.cells == null) return true;
            float sx = __instance.size.x;
            float sy = __instance.size.y;
            int sizeXInt = (int)sx;
            int sizeYInt = (int)sy;
            if (sizeYInt <= 0 || sizeXInt <= 0 || (float)sizeXInt != sx || (float)sizeYInt != sy) return true;

            WorldCell[] cells = __instance.cells;
            int maxCell = sizeXInt * sizeYInt - 1;
            switch (direction)
            {
                case WindDirection.East:
                case WindDirection.West:
                case WindDirection.North:
                case WindDirection.South:
                    __result = StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, direction, maxSteepness);
                    return false;
                case WindDirection.NorthWest:
                {
                    int n = StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.North, maxSteepness);
                    __result = (n != -1) ? StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.West, maxSteepness) : -1;
                    return false;
                }
                case WindDirection.NorthEast:
                {
                    int n = StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.North, maxSteepness);
                    __result = (n != -1) ? StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.East, maxSteepness) : -1;
                    return false;
                }
                case WindDirection.SouthWest:
                {
                    int n = StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.South, maxSteepness);
                    __result = (n != -1) ? StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.West, maxSteepness) : -1;
                    return false;
                }
                case WindDirection.SouthEast:
                {
                    int n = StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, index, WindDirection.South, maxSteepness);
                    __result = (n != -1) ? StepSteepCardinalFast(cells, sizeXInt, sizeYInt, maxCell, n, WindDirection.East, maxSteepness) : -1;
                    return false;
                }
                default:
                    __result = 0;
                    return false;
            }
        }

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.MoveSingleStepInDirectionOfCellWithMaxSteepness))]
        [HarmonyPrefix]
        public static bool MoveSingleStepWithMaxSteepness_Prefix(WorldArea __instance, int currentCellIndex, int goalCellIndex, float maxSteepness, ref int __result)
        {
            if (!Enabled || __instance == null || __instance.cells == null) return true;
            WorldCell[] cells = __instance.cells;
            int len = cells.Length;
            if (currentCellIndex == -1 || currentCellIndex >= len || goalCellIndex == -1 || goalCellIndex >= len || currentCellIndex == goalCellIndex)
            {
                __result = -1;
                return false;
            }
            float sx = __instance.size.x;
            float sy = __instance.size.y;
            int sizeXInt = (int)sx;
            int sizeYInt = (int)sy;
            if (sizeYInt <= 0 || sizeXInt <= 0 || (float)sizeXInt != sx || (float)sizeYInt != sy) return true;

            int cx = currentCellIndex / sizeYInt;
            int cy = currentCellIndex % sizeYInt;
            int gx = goalCellIndex / sizeYInt;
            int gy = goalCellIndex % sizeYInt;
            int dx = gx - cx;
            int dy = gy - cy;
            int adx = (dx < 0) ? -dx : dx;
            int ady = (dy < 0) ? -dy : dy;
            if (adx > ady) cx += (dx > 0) ? 1 : -1;
            else cy += (dy > 0) ? 1 : -1;

            if (cx < 0) cx = 0;
            else if (cx > sizeYInt - 1) cx = sizeYInt - 1;
            if (cy < 0) cy = 0;
            else if (cy > sizeXInt - 1) cy = sizeXInt - 1;

            int next = cx * sizeYInt + cy;
            __result = ((uint)next < (uint)len && cells[next].CalculateNormalizedSteepness() <= maxSteepness) ? next : -1;
            return false;
        }

        // =========================================================================
        // 19. Fast LivingAnimal.LiveADay — inlined zero-alloc daily animal tick
        //     (LivingAnimal.cs:626-848)
        // =========================================================================
        private static void LiveADayAnimalFast(LivingAnimal a, int periodIndex)
        {
            if (a.dormant)
            {
                if (a.wantsToHibernate) return;
                a.dormant = false;
            }

            a.age++;
            AnimalDefinition def = a.definition;
            if (a.rest < 30)
            {
                SleepPattern sp = def.sleepPattern;
                if ((sp == SleepPattern.AtNight && (periodIndex == 0 || periodIndex == 4)) ||
                    (sp == SleepPattern.DuringTheDay && periodIndex == 2))
                {
                    if (!a.sleeping)
                    {
                        a.ageAtLastSleep = a.age;
                        a.sleeping = true;
                    }
                }
            }

            if (a.sleeping)
            {
                a.rest += a.restRestorationSpeed;
                a.lifeReportDaysSleeping++;
                if (a.rest < a.maximumRest) return;
                a.rest = a.maximumRest;
                a.sleeping = false;
            }

            if (a.carryingBaby)
            {
                a.babyBeingCarried.age++;
            }

            if (a.verboseEnergy || a.verboseMating)
            {
                a.logString += ".";
                Debug.Log(a.lifeFormIndex + " energy " + a.energy + " threshold " + a.CalculateReproductionThreshold());
            }

            a.energy -= a.dailyEnergyLoss;
            a.health -= a.dailyHealthLoss;
            a.rest--;

            CellLocationType loc = a.locationType;
            bool oceanFloor = a.oceanFloor;
            if (loc == CellLocationType.UnderWater || oceanFloor)
            {
                if (a.timeInWater == -1)
                {
                    if (!(a.oxygen >= (float)a.timeOnLand)) a.oxygen = a.timeOnLand;
                }
                else if (a.oxygen == 0f)
                {
                    a.health -= a.dailyHealthLossWhileSuffocating;
                }
                else
                {
                    a.oxygen--;
                }
            }
            else if (loc == CellLocationType.Ground && !oceanFloor)
            {
                if (a.timeOnLand == -1)
                {
                    if (!(a.oxygen >= (float)a.timeInWater)) a.oxygen = a.timeInWater;
                }
                else if (a.oxygen == 0f)
                {
                    a.health -= a.dailyHealthLossWhileSuffocating;
                }
                else
                {
                    a.oxygen--;
                }
            }
            else if (loc == CellLocationType.WaterSurface)
            {
                if (a.timeOnLand != -1 && !(a.oxygen >= (float)a.timeOnLand)) a.oxygen = a.timeOnLand;
                if (a.timeInWater != -1 && !(a.oxygen >= (float)a.timeInWater)) a.oxygen = a.timeInWater;
            }

            if (!a.insideTemperatureRange)
            {
                a.nrOfDaysOutsideTempRange++;
                if (a.nrOfDaysOutsideTempRange > 2)
                {
                    a.health -= a.dailyHealthLossOutsideTemperatureRange;
                }
            }
            else
            {
                a.nrOfDaysOutsideTempRange = 0;
            }

            if (!a.belowOceanCurrentSpeedThreshold)
            {
                a.health -= a.dailyHealthLossBelowCurrentSpeedThreshold;
            }

            if (!(a.dailyPoisonDamage <= 0f))
            {
                a.health -= a.dailyPoisonDamage;
                a.lastHurtAtAge = a.age;
            }
            if (!(a.parasiteDamage <= 0f))
            {
                a.health -= a.parasiteDamage;
                a.lastHurtAtAge = a.age;
            }

            if (!(a.energy > 1f))
            {
                a.energy = 0f;
                a.health -= a.dailyHealthLossWhileStarving;
            }
            else if (a.pregnant)
            {
                if (a.babyDueAt <= a.age)
                {
                    a.readyToGiveBirth = true;
                }
                else
                {
                    a.energy -= a.dailyEnergyLoss * 2f;
                }
            }
            else if (!a.eatsMeat || a.nrOfChildren < a.maximumNrOfChildren)
            {
                int rem = a.expectedAge - a.age;
                if (rem < 0) rem = 0;
                else if (rem > 20) rem = 20;
                float thresh = (float)rem * a.dailyEnergyLoss + a.energyNeededToReproduce + a.energyNeededForNestCreation;
                if (!(a.energy <= thresh) && def.CanGiveBirth() && (def.canGiveBirthAsexually || a.fertilized))
                {
                    a.energy -= a.energyNeededToReproduce;
                    if (!(a.energy <= thresh)) a.energy = thresh;
                    a.pregnant = true;
                    if (a.verboseEnergy || a.verboseMating) a.logString += "p";
                    if (a.pregnancyTime == 0f)
                    {
                        a.readyToGiveBirth = true;
                    }
                    else
                    {
                        a.babyDueAt = (int)((float)a.age + a.pregnancyTime);
                    }
                }
            }

            if (!(a.health >= 0f))
            {
                a.dying = true;
            }

            if (a.isChild && a.age > a.matureAtAge)
            {
                a.isChild = false;
                if (a.hasCachedAdultMovementStats)
                {
                    a.sessile = a.cachedAdultSessile;
                    a.canWalk = a.cachedAdultCanWalk;
                    a.canClimb = a.cachedAdultCanClimb;
                    a.canGlide = a.cachedAdultCanGlide;
                    a.canFly = a.cachedAdultCanFly;
                    a.canSwim = a.cachedAdultCanSwim;
                    a.aquaticBehavior = a.cachedAdultAquaticBehavior;
                    a.speed = a.cachedAdultSpeed;
                    a.swimmingSpeed = a.cachedAdultSwimmingSpeed;
                    a.waterSurfaceBoost = a.cachedAdultWaterSurfaceBoost;
                    a.maximumSteepness = a.cachedAdultMaximumSteepness;
                    a.canSteerInTheAir = a.cachedAdultCanSteerInTheAir;
                }
                if (a.verboseMating) a.logString += "o";
            }

            if (!((float)a.age < a.healthDeterioratesAtAge))
            {
                a.health -= a.healthDeteriorationRatePerDayAtOldAge;
            }

            if (a.rest <= 0 && !a.sleeping)
            {
                a.ageAtLastSleep = a.age;
                a.sleeping = true;
            }

            if (a.glowTimer > 0)
            {
                a.glowTimer--;
                if (a.glowTimer == 0) a.glowing = false;
            }
            if (a.hideTimer > 0)
            {
                a.hideTimer--;
                if (a.hideTimer == 0) a.hidingInShell = false;
            }
            if (a.makingIdleSound) a.makingIdleSound = false;
        }

        // =========================================================================
        // 20. Copy-on-write AnimalDefinition + fast GiveBirth & AnimalBlender
        //     Eliminates ~150-300 heap allocations per birth (55% of Time Jump CPU)
        // =========================================================================
        private static AnimalDefinition ShallowCopyAnimalDefinition(AnimalDefinition src, bool cloneInstincts)
        {
            if (_memberwiseClone == null)
            {
                return (AnimalDefinition)src.Clone();
            }

            AnimalDefinition copy = (AnimalDefinition)_memberwiseClone(src);
            copy.currentSex = 0;
            copy.cachedMinifiedDescription = null;
            copy.cachedShapeNames = null;
            copy.cachedBytes = null;

            if (cloneInstincts && copy._instincts != null)
            {
                InstinctDefinition[] srcInst = copy._instincts;
                InstinctDefinition[] dstInst = new InstinctDefinition[srcInst.Length];
                for (int i = 0; i < srcInst.Length; i++)
                {
                    dstInst[i] = srcInst[i].Clone() as InstinctDefinition;
                }
                copy._instincts = dstInst;
            }
            return copy;
        }

        private static LivingAnimal GiveBirthFast(LivingAnimal p, float temperature, bool skipDefClone)
        {
            p.readyToGiveBirth = false;
            p.pregnant = false;
            p.prudentPredation = false;
            p.ageAtLastBirth = p.age;
            p.instinctivelyInterestedInCellWithIndex = -1;
            p.actionIsInstinctivelyPrioritized = false;
            if (p.verboseEnergy || p.verboseMating) p.logString += "b";
            if (p.buildLifeReport) p.lifeReportDaysReproducing++;

            AnimalDefinition pDef = p.definition;
            AnimalDefinition childDef = skipDefClone ? pDef : ShallowCopyAnimalDefinition(pDef, false);
            LivingAnimal c = new LivingAnimal(childDef);
            c.manuallyAdded = false;
            c.energy = p.energyNeededToReproduce;
            c.mostSalientColor = p.mostSalientColor;
            c.colorBrightness = p.colorBrightness;
            c.canEat = p.canEat;
            c.eatsNectar = p.eatsNectar;
            c.eatsPlants = p.eatsPlants;
            c.eatsMeat = p.eatsMeat;
            c.eatsMeatOnly = p.eatsMeatOnly;
            c.getsMostEnergyFromMeat = p.getsMostEnergyFromMeat;
            c.orderedDetailedFoodTypes = p.orderedDetailedFoodTypes;
            c.energyPerDetailedFoodType = p.energyPerDetailedFoodType;
            c.expectedAge = p.expectedAge;
            c.matureAtAge = p.matureAtAge;
            c.healthDeterioratesAtAge = p.healthDeterioratesAtAge;
            c.healthDeteriorationRatePerDayAtOldAge = p.healthDeteriorationRatePerDayAtOldAge;
            c.baseAttractiveness = p.baseAttractiveness;
            c.attractiveness = p.baseAttractiveness;
            c.extraAttractivenessWhileDisplaying = p.extraAttractivenessWhileDisplaying;
            c.energyNeededToReproduce = p.energyNeededToReproduce;
            c.stepSound = p.stepSound;

            c.hasCachedAdultMovementStats = p.hasCachedAdultMovementStats;
            c.cachedYoungSessile = p.cachedYoungSessile;
            c.cachedYoungCanWalk = p.cachedYoungCanWalk;
            c.cachedYoungCanClimb = p.cachedYoungCanClimb;
            c.cachedYoungCanGlide = p.cachedYoungCanGlide;
            c.cachedYoungCanFly = p.cachedYoungCanFly;
            c.cachedYoungCanSwim = p.cachedYoungCanSwim;
            c.cachedYoungAquaticBehavior = p.cachedYoungAquaticBehavior;
            c.cachedYoungSpeed = p.cachedYoungSpeed;
            c.cachedYoungSwimmingSpeed = p.cachedYoungSwimmingSpeed;
            c.cachedYoungWaterSurfaceBoost = p.cachedYoungWaterSurfaceBoost;
            c.cachedYoungMaximumSteepness = p.cachedYoungMaximumSteepness;
            c.cachedYoungCanSteerInTheAir = p.cachedYoungCanSteerInTheAir;
            c.cachedAdultSessile = p.cachedAdultSessile;
            c.cachedAdultCanWalk = p.cachedAdultCanWalk;
            c.cachedAdultCanClimb = p.cachedAdultCanClimb;
            c.cachedAdultCanGlide = p.cachedAdultCanGlide;
            c.cachedAdultCanFly = p.cachedAdultCanFly;
            c.cachedAdultCanSwim = p.cachedAdultCanSwim;
            c.cachedAdultAquaticBehavior = p.cachedAdultAquaticBehavior;
            c.cachedAdultSpeed = p.cachedAdultSpeed;
            c.cachedAdultSwimmingSpeed = p.cachedAdultSwimmingSpeed;
            c.cachedAdultWaterSurfaceBoost = p.cachedAdultWaterSurfaceBoost;
            c.cachedAdultMaximumSteepness = p.cachedAdultMaximumSteepness;
            c.cachedAdultCanSteerInTheAir = p.cachedAdultCanSteerInTheAir;

            if (p.hasCachedAdultMovementStats)
            {
                c.sessile = p.cachedYoungSessile;
                c.canWalk = p.cachedYoungCanWalk;
                c.canClimb = p.cachedYoungCanClimb;
                c.canGlide = p.cachedYoungCanGlide;
                c.canFly = p.cachedYoungCanFly;
                c.canSwim = p.cachedYoungCanSwim;
                c.aquaticBehavior = p.cachedYoungAquaticBehavior;
                c.speed = p.cachedYoungSpeed;
                c.swimmingSpeed = p.cachedYoungSwimmingSpeed;
                c.waterSurfaceBoost = p.cachedYoungWaterSurfaceBoost;
                c.maximumSteepness = p.cachedYoungMaximumSteepness;
                c.canSteerInTheAir = p.cachedYoungCanSteerInTheAir;
            }
            else
            {
                c.sessile = p.sessile;
                c.canWalk = p.canWalk;
                c.canClimb = p.canClimb;
                c.canGlide = p.canGlide;
                c.canFly = p.canFly;
                c.canSwim = p.canSwim;
                c.aquaticBehavior = p.aquaticBehavior;
                c.speed = p.speed;
                c.swimmingSpeed = p.swimmingSpeed;
                c.waterSurfaceBoost = p.waterSurfaceBoost;
                c.maximumSteepness = p.maximumSteepness;
                c.canSteerInTheAir = p.canSteerInTheAir;
            }

            c.maximumOceanCurrent = p.maximumOceanCurrent;
            c.timeInWater = p.timeInWater;
            c.timeOnLand = p.timeOnLand;
            c.animationPack = p.animationPack;
            c.swimAnimationType = p.swimAnimationType;
            c.flyAnimationType = p.flyAnimationType;
            c.walkingStyle = p.walkingStyle;
            c.sprawlingWalk = p.sprawlingWalk;
            c.attackParticleEffect = p.attackParticleEffect;
            c.lowestDetectablePitch = p.lowestDetectablePitch;
            c.highestDetectablePitch = p.highestDetectablePitch;
            c.colorSeeingMinLimit = p.colorSeeingMinLimit;
            c.colorSeeingMaxLimit = p.colorSeeingMaxLimit;
            c.closestHearingDistance = p.closestHearingDistance;
            c.furthestHearingDistance = p.furthestHearingDistance;
            c.furthestSeeingDistanceDuringDayTime = p.furthestSeeingDistanceDuringDayTime;
            c.furthestSeeingDistanceAtTwilight = p.furthestSeeingDistanceAtTwilight;
            c.furthestSeeingDistanceAtNight = p.furthestSeeingDistanceAtNight;
            c.furthestUnderWaterSeeingDistanceDuringDayTime = p.furthestUnderWaterSeeingDistanceDuringDayTime;
            c.furthestUnderWaterSeeingDistanceAtTwilight = p.furthestUnderWaterSeeingDistanceAtTwilight;
            c.furthestUnderWaterSeeingDistanceAtNight = p.furthestUnderWaterSeeingDistanceAtNight;
            c.fieldOfView = p.fieldOfView;
            c.crySound = p.crySound;
            c.noiseSound = p.noiseSound;
            c.highestMouthHeight = p.highestMouthHeight;
            c.canEatAtHeight = p.canEatAtHeight;
            c.doesEatingDamage = p.doesEatingDamage;
            c.filterFeeder = p.filterFeeder;
            c.canGlow = p.canGlow;
            c.alwaysGlowing = p.alwaysGlowing;
            c.glowing = p.alwaysGlowing;
            c.canHangAsBaby = p.canHangAsBaby;
            c.canHangFromBranch = p.canHangFromBranch;
            c.canHangUpsideDownFromBranch = p.canHangUpsideDownFromBranch;
            c.hasLiquidStorage = p.hasLiquidStorage;
            c.minimumTemperatureThisOrganismCanHandle = p.minimumTemperatureThisOrganismCanHandle;
            c.maximumTemperatureThisOrganismCanHandle = p.maximumTemperatureThisOrganismCanHandle;
            c.dailyHealthLossOutsideTemperatureRange = p.dailyHealthLossOutsideTemperatureRange;
            c.dailyHealthLossWhileSuffocating = p.dailyHealthLossWhileSuffocating;
            c.dailyHealthLossBelowCurrentSpeedThreshold = p.dailyHealthLossBelowCurrentSpeedThreshold;
            c.pregnancyTime = p.pregnancyTime;
            c.timeInEgg = p.timeInEgg;
            c.attack = p.attack;
            c.armor = p.armor;
            c.defenseForEatingSpikes = p.defenseForEatingSpikes;
            c.venomStrength = p.venomStrength;
            c.avoidsHeat = p.avoidsHeat;
            c.avoidsCold = p.avoidsCold;
            c.ableToBuildNest = p.ableToBuildNest;
            c.energyNeededForNestCreation = p.energyNeededForNestCreation;
            c.canHibernate = p.canHibernate;
            c.providesMilk = p.providesMilk;
            c.canBlow = p.canBlow;
            c.canHideInShell = p.canHideInShell;
            c.canMakeNoise = p.canMakeNoise;
            c.noiseDuringMovement = p.noiseDuringMovement;
            c.callSoundPack = p.callSoundPack;
            c.callSoundPackReferenceBodySize = p.callSoundPackReferenceBodySize;
            c.genericNoiseSoundPack = p.genericNoiseSoundPack;
            c.genericNoiseSoundPackReferenceBodySize = p.genericNoiseSoundPackReferenceBodySize;
            c.movementNoiseSoundPack = p.movementNoiseSoundPack;
            c.movementNoiseSoundPackReferenceBodySize = p.movementNoiseSoundPackReferenceBodySize;
            c.offSpringSexChances = p.offSpringSexChances;

            if (pDef.sexes != null && pDef.sexes.Length > 0)
            {
                int chosenSex = 0;
                OffspringSexDecisionType decType = pDef.offspringSexDecisionType;
                if (decType == OffspringSexDecisionType.Chance)
                {
                    float[] chances = p.offSpringSexChances;
                    if (chances != null && chances.Length > 0)
                    {
                        float r = UnityEngine.Random.Range(0f, 1f);
                        bool picked = false;
                        float acc = 0f;
                        for (int i = 0; i < chances.Length; i++)
                        {
                            acc += chances[i];
                            if (!(r >= acc))
                            {
                                chosenSex = i;
                                picked = true;
                                break;
                            }
                        }
                        if (!picked) chosenSex = chances.Length;
                    }
                }
                else if (decType == OffspringSexDecisionType.Temperature)
                {
                    for (int j = 0; j < 7; j++)
                    {
                        if (!(temperature >= _tempSexBrackets[j]))
                        {
                            chosenSex = pDef.GetOffspringSexTemperatureBracket(j);
                            break;
                        }
                    }
                }
                if (chosenSex > pDef.sexes.Length) chosenSex = 0;
                if (!skipDefClone) childDef.currentSex = chosenSex;
            }

            HormoneDefinition[] hormones = pDef.hormones;
            int hLen = (hormones != null) ? hormones.Length : 0;
            if (hLen == 0)
            {
                c.hormoneLevels = _emptyFloatArray;
            }
            else
            {
                float[] hl = new float[hLen];
                int copyLen = (p.hormoneLevels != null && p.hormoneLevels.Length < hLen) ? p.hormoneLevels.Length : hLen;
                for (int k = 0; k < copyLen; k++) hl[k] = hormones[k].initialLevel;
                c.hormoneLevels = hl;
            }

            c.libraryProfile = p.libraryProfile;
            p.nrOfChildren++;
            return c;
        }

        [HarmonyPatch(typeof(AnimalBlender), nameof(AnimalBlender.BlendAnimals))]
        [HarmonyPrefix]
        public static bool AnimalBlender_BlendAnimals_Prefix(
            AnimalBlender __instance,
            AnimalDefinition animalA,
            AnimalDefinition animalB,
            ref AnimalDefinition __result)
        {
            if (!Enabled || __instance == null || animalA == null || animalB == null) return true;
            __result = BlendAnimalsFast(__instance, animalA, animalB);
            return false;
        }

        private static AnimalDefinition BlendAnimalsFast(AnimalBlender blender, AnimalDefinition animalA, AnimalDefinition animalB)
        {
            if (animalA.definitionIndex == animalB.definitionIndex)
            {
                blender.madeChanges = false;
                blender.definitionIndexOfParent = animalA.definitionIndex;
                return animalA;
            }

            AnimalDefinition primary;
            AnimalDefinition secondary;
            if (!(UnityEngine.Random.value <= 0.5f))
            {
                primary = animalA;
                secondary = animalB;
                blender.definitionIndexOfParent = animalA.definitionIndex;
            }
            else
            {
                primary = animalB;
                secondary = animalA;
                blender.definitionIndexOfParent = animalB.definitionIndex;
            }

            // Shallow-copy primary with currentSex = 0; BlendAnimals only overwrites scalar
            // fields and replaces the instincts array.
            AnimalDefinition dst = ShallowCopyAnimalDefinition(primary, false);

            if (!(UnityEngine.Random.value <= 0.5f)) dst.bodySize = secondary.bodySize;
            if (!(UnityEngine.Random.value <= 0.5f)) dst.colorHue = secondary.colorHue;
            if (!(UnityEngine.Random.value <= 0.5f)) dst.colorBrightness = secondary.colorBrightness;
            if (!(UnityEngine.Random.value <= 0.5f)) dst.skinPattern = secondary.skinPattern;
            if (!(UnityEngine.Random.value <= 0.5f)) dst.skinType = secondary.skinType;
            if (!(UnityEngine.Random.value <= 0.5f))
            {
                dst.embryoTimeInsideMother = secondary.embryoTimeInsideMother;
                dst.eggType = secondary.eggType;
                dst.noEggsBelowTemperature = secondary.noEggsBelowTemperature;
                dst.noEggsAboveTemperature = secondary.noEggsAboveTemperature;
                dst.noHatchBelowTemperature = secondary.noHatchBelowTemperature;
                dst.noHatchAboveTemperature = secondary.noHatchAboveTemperature;
            }
            if (!(UnityEngine.Random.value <= 0.5f))
            {
                dst.nestType = secondary.nestType;
                dst.nestEnclosed = secondary.nestEnclosed;
                dst.hibernateBelowTemperature = secondary.hibernateBelowTemperature;
                dst.aestivateAboveTemperature = secondary.aestivateAboveTemperature;
            }
            if (!(UnityEngine.Random.value <= 0.5f))
            {
                dst.poisonResistance = secondary.poisonResistance;
                dst.resistantAgainstPoisonType = secondary.resistantAgainstPoisonType;
            }

            // Always give dst its own cloned instincts array because ProcessNewAnimalDefinition
            // mutates InstinctDefinition.conditionRange/action in place (InstinctTools.cs:243-251).
            InstinctDefinition[] instSrc = (!(UnityEngine.Random.value <= 0.5f)) ? secondary.instincts : dst._instincts;
            if (instSrc != null)
            {
                InstinctDefinition[] instCopy = new InstinctDefinition[instSrc.Length];
                for (int i = 0; i < instSrc.Length; i++)
                {
                    instCopy[i] = instSrc[i].Clone() as InstinctDefinition;
                }
                dst.instincts = instCopy;
            }

            blender.madeChanges = true;
            return dst;
        }

        // =========================================================================
        // 21. Cached OrganismObjectFileImporter body-part metadata lookups
        //     Eliminates repeated .ToLower() string allocations + Hashtable boxing
        //     inside AnimalStatsCalculator.Calculate during sexual births.
        // =========================================================================
        [HarmonyPatch(typeof(OrganismObjectFileImporter), nameof(OrganismObjectFileImporter.ImportAttackParticleEffect))]
        [HarmonyPrefix]
        public static bool Importer_ImportAttackParticleEffect_Prefix(OrganismObjectFileImporter __instance, string bodyPartShapeName, ref string __result)
        {
            if (!Enabled || __instance == null || !__instance.importFinished || bodyPartShapeName == null) return true;
            EnsureImporterCache(__instance);
            if (_attackParticleCache.TryGetValue(bodyPartShapeName, out __result)) return false;

            object raw = __instance.attackParticleEffectForBodyPartShape[bodyPartShapeName.ToLower()];
            __result = (raw as string) ?? string.Empty;
            _attackParticleCache[bodyPartShapeName] = __result;
            return false;
        }

        [HarmonyPatch(typeof(OrganismObjectFileImporter), nameof(OrganismObjectFileImporter.ImportMaterialType))]
        [HarmonyPrefix]
        public static bool Importer_ImportMaterialType_Prefix(OrganismObjectFileImporter __instance, string bodyPartShapeName, ref MaterialType __result)
        {
            if (!Enabled || __instance == null || !__instance.importFinished || bodyPartShapeName == null) return true;
            EnsureImporterCache(__instance);
            if (_materialTypeCache.TryGetValue(bodyPartShapeName, out __result)) return false;

            object raw = __instance.materialTypeForBodyPartShape[bodyPartShapeName.ToLower()];
            if (raw == null) return true;
            __result = (MaterialType)raw;
            _materialTypeCache[bodyPartShapeName] = __result;
            return false;
        }

        [HarmonyPatch(typeof(OrganismObjectFileImporter), nameof(OrganismObjectFileImporter.ImportSpecialMaterialType))]
        [HarmonyPrefix]
        public static bool Importer_ImportSpecialMaterialType_Prefix(OrganismObjectFileImporter __instance, string bodyPartShapeName, ref MaterialType __result)
        {
            if (!Enabled || __instance == null || !__instance.importFinished || bodyPartShapeName == null) return true;
            EnsureImporterCache(__instance);
            if (_specialMaterialTypeCache.TryGetValue(bodyPartShapeName, out __result)) return false;

            object raw = __instance.specialMaterialTypeForBodyPartShape[bodyPartShapeName.ToLower()];
            __result = (raw != null) ? (MaterialType)raw : MaterialType.None;
            _specialMaterialTypeCache[bodyPartShapeName] = __result;
            return false;
        }

        [HarmonyPatch(typeof(OrganismObjectFileImporter), nameof(OrganismObjectFileImporter.ImportLightIntensityOfBodyPartShape))]
        [HarmonyPrefix]
        public static bool Importer_ImportLightIntensity_Prefix(OrganismObjectFileImporter __instance, string bodyPartShapeName, ref float __result)
        {
            if (!Enabled || __instance == null || !__instance.importFinished || bodyPartShapeName == null) return true;
            EnsureImporterCache(__instance);
            if (_lightIntensityCache.TryGetValue(bodyPartShapeName, out __result)) return false;

            object raw = __instance.lightIntensityForBodyPartShape[bodyPartShapeName.ToLower()];
            __result = (raw != null) ? Convert.ToSingle(raw) : 0f;
            _lightIntensityCache[bodyPartShapeName] = __result;
            return false;
        }

        // =========================================================================
        // 22. Zero-Boo AnimalDefinition.Clone / TorsoPartDefinition.Clone / AnimalMorph.Clone
        //     Replaces boxed UnityRuntimeServices.GetEnumerator + Update + wasted constructor
        //     HashSets with direct struct enumerators and MemberwiseClone.
        // =========================================================================
        [HarmonyPatch(typeof(AnimalDefinition), nameof(AnimalDefinition.Clone))]
        [HarmonyPrefix]
        public static bool AnimalDefinition_Clone_Prefix(AnimalDefinition __instance, ref object __result)
        {
            if (!Enabled || __instance == null || _memberwiseClone == null) return true;
            __result = CloneAnimalDefinitionFast(__instance);
            return false;
        }

        [HarmonyPatch(typeof(TorsoPartDefinition), nameof(TorsoPartDefinition.Clone))]
        [HarmonyPrefix]
        public static bool TorsoPartDefinition_Clone_Prefix(TorsoPartDefinition __instance, ref object __result)
        {
            if (!Enabled || __instance == null || _memberwiseClone == null) return true;
            __result = CloneTorsoPartFast(__instance);
            return false;
        }

        [HarmonyPatch(typeof(AnimalMorph), nameof(AnimalMorph.Clone))]
        [HarmonyPrefix]
        public static bool AnimalMorph_Clone_Prefix(AnimalMorph @base, ref AnimalMorph __result)
        {
            if (!Enabled) return true;
            __result = CloneAnimalMorphFast(@base);
            return false;
        }

        private static BodyPartDefinition CloneBodyPartFast(BodyPartDefinition src)
        {
            if (src == null) return null;
            // All BodyPartDefinition subclasses only carry value/string fields plus
            // MouthDefinition.energyPerFoodType (immutable after creation), so MemberwiseClone
            // is exact and avoids allocating a brand-new Dictionary<DetailedFoodType,float> per mouth.
            return (BodyPartDefinition)_memberwiseClone(src);
        }

        private static TorsoPartDefinition CloneTorsoPartFast(TorsoPartDefinition src)
        {
            TorsoPartDefinition dst = new TorsoPartDefinition(src.displacement, src.diameter);
            Dictionary<TorsoPartDetailedLocation, BodyPartDefinition> srcKids = src.children;
            if (srcKids != null && srcKids.Count > 0)
            {
                Dictionary<TorsoPartDetailedLocation, BodyPartDefinition> dstKids = new Dictionary<TorsoPartDetailedLocation, BodyPartDefinition>(srcKids.Count);
                foreach (KeyValuePair<TorsoPartDetailedLocation, BodyPartDefinition> kv in srcKids)
                {
                    dstKids[kv.Key] = CloneBodyPartFast(kv.Value);
                }
                dst.children = dstKids;
            }
            return dst;
        }

        private static AnimalMorph CloneAnimalMorphFast(AnimalMorph src)
        {
            AnimalMorph dst = src;
            if (src.instincts != null)
            {
                int len = src.instincts.Length;
                InstinctDefinition[] arr = new InstinctDefinition[len];
                Array.Copy(src.instincts, arr, len);
                dst.instincts = arr;
            }
            if (src.hormoneInitialLevels != null)
            {
                int len = src.hormoneInitialLevels.Length;
                if (len == 0)
                {
                    dst.hormoneInitialLevels = _emptyFloatArray;
                    dst.hormoneChangesPerDay = _emptyFloatArray;
                }
                else
                {
                    float[] initArr = new float[len];
                    float[] chgArr = new float[len];
                    Array.Copy(src.hormoneInitialLevels, initArr, len);
                    if (src.hormoneChangesPerDay != null)
                    {
                        Array.Copy(src.hormoneChangesPerDay, chgArr, Math.Min(len, src.hormoneChangesPerDay.Length));
                    }
                    dst.hormoneInitialLevels = initArr;
                    dst.hormoneChangesPerDay = chgArr;
                }
            }
            if (src.excludeFromDiet != null)
            {
                HashSet<DetailedFoodType> hs = new HashSet<DetailedFoodType>();
                if (src.excludeFromDiet.Count > 0)
                {
                    foreach (DetailedFoodType v in src.excludeFromDiet) hs.Add(v);
                }
                dst.excludeFromDiet = hs;
            }
            if (src.excludeFruitShapesFromDiet != null)
            {
                HashSet<FruitShape> hs = new HashSet<FruitShape>();
                if (src.excludeFruitShapesFromDiet.Count > 0)
                {
                    foreach (FruitShape v in src.excludeFruitShapesFromDiet) hs.Add(v);
                }
                dst.excludeFruitShapesFromDiet = hs;
            }
            if (src.wholeAnimalDietExcludeSkinPatterns != null)
            {
                HashSet<SkinPattern> hs = new HashSet<SkinPattern>();
                if (src.wholeAnimalDietExcludeSkinPatterns.Count > 0)
                {
                    foreach (SkinPattern v in src.wholeAnimalDietExcludeSkinPatterns) hs.Add(v);
                }
                dst.wholeAnimalDietExcludeSkinPatterns = hs;
            }
            if (src.bodyPartDiffs != null)
            {
                Dictionary<BodyPartDiffLocation, BodyPartDiff> d = new Dictionary<BodyPartDiffLocation, BodyPartDiff>(src.bodyPartDiffs.Count);
                foreach (KeyValuePair<BodyPartDiffLocation, BodyPartDiff> kv in src.bodyPartDiffs)
                {
                    d[kv.Key] = kv.Value;
                }
                dst.bodyPartDiffs = d;
            }
            return dst;
        }

        private static AnimalDefinition CloneAnimalDefinitionFast(AnimalDefinition src)
        {
            if (_memberwiseClone == null) return (AnimalDefinition)src.Clone();

            AnimalDefinition dst = (AnimalDefinition)_memberwiseClone(src);
            dst.currentSex = 0;
            dst.cachedMinifiedDescription = null;
            dst.cachedShapeNames = null;
            dst.cachedBytes = null;

            if (src.torso != null)
            {
                int tLen = src.torso.Length;
                TorsoPartDefinition[] tArr = new TorsoPartDefinition[tLen];
                for (int i = 0; i < tLen; i++)
                {
                    tArr[i] = CloneTorsoPartFast(src.torso[i]);
                }
                dst.torso = tArr;
            }

            if (src._instincts != null)
            {
                int iLen = src._instincts.Length;
                InstinctDefinition[] iArr = new InstinctDefinition[iLen];
                for (int i = 0; i < iLen; i++)
                {
                    iArr[i] = src._instincts[i].Clone() as InstinctDefinition;
                }
                dst._instincts = iArr;
            }

            if (src._hormones != null)
            {
                int hLen = src._hormones.Length;
                HormoneDefinition[] hArr = new HormoneDefinition[hLen];
                Array.Copy(src._hormones, hArr, hLen);
                dst._hormones = hArr;
            }

            HashSet<DetailedFoodType> exDiet = new HashSet<DetailedFoodType>();
            if (src._excludeFromDiet != null && src._excludeFromDiet.Count > 0)
            {
                foreach (DetailedFoodType v in src._excludeFromDiet) exDiet.Add(v);
            }
            dst._excludeFromDiet = exDiet;

            HashSet<SkinPattern> exPat = new HashSet<SkinPattern>();
            if (src._wholeAnimalDietExcludeSkinPatterns != null && src._wholeAnimalDietExcludeSkinPatterns.Count > 0)
            {
                foreach (SkinPattern v in src._wholeAnimalDietExcludeSkinPatterns) exPat.Add(v);
            }
            dst._wholeAnimalDietExcludeSkinPatterns = exPat;

            HashSet<FruitShape> exFruit = new HashSet<FruitShape>();
            if (src._excludeFruitShapesFromDiet != null && src._excludeFruitShapesFromDiet.Count > 0)
            {
                foreach (FruitShape v in src._excludeFruitShapesFromDiet) exFruit.Add(v);
            }
            dst._excludeFruitShapesFromDiet = exFruit;

            dst.young = CloneAnimalMorphFast(src.young);
            if (src.sexes != null)
            {
                int sLen = src.sexes.Length;
                AnimalMorph[] sexesArr = new AnimalMorph[sLen];
                AnimalMorph[] ySexesArr = new AnimalMorph[sLen];
                for (int i = 0; i < sLen; i++)
                {
                    sexesArr[i] = CloneAnimalMorphFast(src.sexes[i]);
                    ySexesArr[i] = CloneAnimalMorphFast(src.youngSexes[i]);
                }
                dst.sexes = sexesArr;
                dst.youngSexes = ySexesArr;
            }

            return dst;
        }

        // =========================================================================
        // 23. Zero-Alloc AnimalStatsCalculator.Calculate + Reusable Calculators in
        //     TimeLine.ProcessNewAnimalDefinition (eliminates 880ms + GC2 churn)
        // =========================================================================
        private static readonly Dictionary<DetailedFoodType, float> _calcFoodDict = new Dictionary<DetailedFoodType, float>(16);
        private static readonly List<float> _calcCryPitches = new List<float>(8);
        private static readonly List<float> _calcStepPitches = new List<float>(8);
        private static readonly Dictionary<TorsoPartDetailedLocation, BodyPartDefinition> _calcTorsoKids = new Dictionary<TorsoPartDetailedLocation, BodyPartDefinition>(8);
        private static readonly Dictionary<int, int> _calcSexCounts = new Dictionary<int, int>(8);
        private static readonly float[] _oneFloatArr = new float[1] { 1f };

        private static AnimalStatsCalculator _sharedCalcA;
        private static AnimalStatsCalculator _sharedCalcB;

        private static void AddUniquePitch(List<float> list, float pitch)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == pitch) return;
            }
            list.Add(pitch);
        }

        private static BodyPartDefinition LookupTemplateBodyPart(OrganismObjectFileImporter importer, BodyPartDefinition bp)
        {
            int idx = bp.shapeIndex;
            switch (bp.bodyPartTypeIndex)
            {
                case 4:
                {
                    LimbDefinition v;
                    if (importer.precreatedLimbs.TryGetValue(idx, out v)) return v;
                    return importer.CreateLimbWithShapeIndex(idx);
                }
                case 5:
                {
                    MouthDefinition v;
                    if (importer.precreatedMouths.TryGetValue(idx, out v)) return v;
                    return importer.CreateMouthWithShapeIndex(idx);
                }
                case 6:
                {
                    EarDefinition v;
                    if (importer.precreatedEars.TryGetValue(idx, out v)) return v;
                    return importer.CreateEarWithShapeIndex(idx);
                }
                case 7:
                {
                    EyeDefinition v;
                    if (importer.precreatedEyes.TryGetValue(idx, out v)) return v;
                    return importer.CreateEyeWithShapeIndex(idx);
                }
                case 10:
                {
                    AnimalAccessoireDefinition v;
                    if (importer.precreatedAnimalAccessoires.TryGetValue(idx, out v)) return v;
                    return importer.CreateAnimalAccessoireWithShapeIndex(idx);
                }
                default:
                    return null;
            }
        }

        private static void ResetAnimalStatsCalculator(AnimalStatsCalculator c, OrganismObjectFileImporter importer, float gravity)
        {
            c.importer = importer;
            c.gravity = gravity;
            c.soundDistanceFactor = 4f;
            c.sightDistanceFactor = 8f;
            c.fatBoundary = 6f;
            c.maximumInsulationByFat = 15f;
            c.weightLessTorsoBoundary = 4f;
            c.lightWeightTorsoBoundary = 8f;
            c.highestEnergyPerMeal = -1f;
            c.speed = 0.25f;
            c.lowestDetectablePitch = -1f;
            c.highestDetectablePitch = -1f;
            c.colorSeeingMinLimit = -1f;
            c.colorSeeingMaxLimit = -1f;
            c.shapeNameOfEyeWithWidestColorVision = string.Empty;
            c.closestHearingDistance = -1f;
            c.furthestHearingDistance = -1f;
            c.furthestSeeingDistanceDuringDayTime = -1f;
            c.furthestSeeingDistanceAtTwilight = -1f;
            c.furthestSeeingDistanceAtNight = -1f;
            c.furthestUnderWaterSeeingDistanceDuringDayTime = -1f;
            c.furthestUnderWaterSeeingDistanceAtTwilight = -1f;
            c.furthestUnderWaterSeeingDistanceAtNight = -1f;
            c.fieldOfView = -1f;
            c.highestMouthHeight = 999999f;
            c.canEatAtHeight = -1f;
            c.relativeDamageOutsideTemperatureRange = 0.5f;
            c.relativeDamageBelowOceanCurrentThreshold = 0.5f;
            c.relativeDamageWhileSuffocating = 0.5f;
            c.healthDeteriorationRatePerDayAtOldAge = 8f;
            c.aquaticBehavior = LimbAquaticBehavior.Swim;
            c.swimAnimationType = SwimAnimationType.Horizontal;
            c.flyAnimationType = FlyAnimationType.Flapping;
            c.timeInWater = -1;
            c.timeOnLand = -1;
            c.ableToBuildNest = true;
            c.drag = 0f;
            c.underWaterBodyPartSpeed = 0f;
            c.waterSurfaceBoost = 0f;
            c.maximumSteepness = 0f;
            c.swimmingSpeed = 0f;
            c.maximumOceanCurrent = 0f;
            c.doesEatingDamage = 0f;
            c.drinksBlood = false;
            c.filterFeeder = false;
            c.hasLiquidStorage = false;
            c.hasFur = false;
            c.providesMilk = 0f;
            c.canBlow = false;
            c.canHideInShell = false;
            c.canMakeNoise = false;
            c.noiseDuringMovement = false;
            c.sprawlingWalk = false;
            c.canSteerInTheAir = false;
            c.balancedForWalking = false;
            c.canSwim = false;
            c.attack = 0;
            c.armor = 0;
            c.defenseForEatingSpikes = 0;
            c.mouthIncreasesEatingHeight = 0f;
            c.venomStrength = 0f;
            c.embryoDevelopmentTime = 0;
            c.pregnancyTime = 0;
            c.crySound = null;
            c.noiseSound = null;
            c.stepSound = null;
            c.callSoundPack = null;
            c.callSoundPackReferenceBodySize = 0;
            c.genericNoiseSoundPack = null;
            c.genericNoiseSoundPackReferenceBodySize = 0;
            c.movementNoiseSoundPack = null;
            c.movementNoiseSoundPackReferenceBodySize = 0;
            c.attackParticleEffect = null;
            c.animationPack = default(AnimationPack);
            c.walkingStyle = default(WalkingStyle);
            c.libraryProfile = default(LibraryProfile);
            c.offSpringSexChances = null;
            c.orderedDetailedFoodTypes = null;
            c.energyPerDetailedFoodType = null;
        }

        [HarmonyPatch(typeof(AnimalStatsCalculator), nameof(AnimalStatsCalculator.Calculate), new Type[] { typeof(AnimalDefinition), typeof(bool) })]
        [HarmonyPrefix]
        public static bool AnimalStatsCalculator_Calculate_Prefix(AnimalStatsCalculator __instance, AnimalDefinition animal, bool youngVersion)
        {
            if (!Enabled || __instance == null || animal == null || __instance.importer == null) return true;
            CalculateAnimalStatsFast(__instance, animal, youngVersion);
            return false;
        }

        private static void CalculateAnimalStatsFast(AnimalStatsCalculator c, AnimalDefinition animal, bool youngVersion)
        {
            OrganismObjectFileImporter importer = c.importer;
            c.canEat = false;
            c.canSee = false;
            c.canHear = false;
            c.eatsNectar = false;
            c.eatsPlants = false;
            c.eatsMeat = false;
            c.getsMostEnergyFromMeat = false;
            c.canGlow = false;
            c.alwaysGlowing = false;
            c.armor = 0;
            c.weight = 0f;
            c.bodyDrag = 1f;
            c.canWalk = false;
            c.canClimb = false;
            c.canGlide = false;
            c.canFly = false;
            c.sessile = false;
            c.canDig = false;
            c.canHangAsBaby = false;
            c.canHangFromBranch = false;
            c.canHangUpsideDownFromBranch = false;
            c.canWax = false;
            c.ableToBuildNest = true;
            c.energyNeededForNestCreation = 0f;
            c.canHibernate = false;
            c.hasLimbsForWalking = false;
            c.hasLimbsForClimbing = false;
            c.hasLimbsForGliding = false;
            c.hasLimbsForFlying = false;
            c.weightSupportedForWalking = 0f;
            c.weightSupportedForClimbing = 0f;
            c.weightSupportedForGliding = 0f;
            c.weightSupportedForFlying = 0f;
            c.avoidsHeat = false;
            c.avoidsCold = false;

            c.eggLocation = importer.GetLocationForEggType(animal.eggType);
            c.eggsInWater = (c.eggLocation == EggLocation.UnderWater || c.eggLocation == EggLocation.WetSoil || c.eggLocation == EggLocation.Alga);
            c.eggsOnLand = (c.eggLocation == EggLocation.DryLand || c.eggLocation == EggLocation.WetSoil);

            int bodySize = animal.bodySize;
            int sizeMinus9 = bodySize - 9;
            int sizeMinus10 = bodySize - 10;

            c.viviparous = animal.embryoTimeInsideMother == 4f;
            c.expectedAge = 40 * sizeMinus9;
            c.matureAtAge = 10 * sizeMinus9;
            c.healthDeterioratesAtAge = 0.9f * (float)c.expectedAge;
            c.health = 10 * sizeMinus9;
            c.embryoDevelopmentTime = 3 * sizeMinus9;
            c.energyNeededToReproduce = 1 + sizeMinus9;
            c.fieldOfView = 0f;

            SkinType skinType = animal.skinType;
            SkinTypeData skinTypeData = importer.GetSkinTypeData(skinType);
            c.minimumTemperatureThisOrganismCanHandle = 0f - skinTypeData.lowTemperatureResistance - (float)(sizeMinus10 * 3);
            c.maximumTemperatureThisOrganismCanHandle = 25f + skinTypeData.highTemperatureResistance - (float)sizeMinus10;
            c.bodyDrag += skinTypeData.drag;
            c.armor = (int)((float)c.armor + skinTypeData.defense);
            c.hasFur = (skinType == SkinType.Furry);
            c.canHideInShell = skinTypeData.shellCover;
            c.attractiveness = 0f;
            c.extraAttractivenessWhileDisplaying = 0f;

            NestType nestType = animal.nestType;
            bool nestEnclosed = animal.nestEnclosed;
            switch (nestType)
            {
                case NestType.None:
                    c.energyNeededForNestCreation = 0f;
                    break;
                case NestType.Twigs:
                case NestType.Burrow:
                    c.energyNeededForNestCreation = (nestEnclosed ? 3 : 1) * sizeMinus9;
                    break;
                case NestType.Wax:
                    c.energyNeededForNestCreation = (nestEnclosed ? 2 : 1) * sizeMinus9;
                    break;
            }
            c.canHibernate = nestEnclosed;

            bool hasGlowInstinct = false;
            InstinctDefinition[] instincts = animal.instincts;
            if (instincts != null)
            {
                for (int i = 0; i < instincts.Length; i++)
                {
                    InstinctAction act = instincts[i].action;
                    if (act == InstinctAction.Glow)
                    {
                        hasGlowInstinct = true;
                        break;
                    }
                    if (act == InstinctAction.GoAwayFrom)
                    {
                        InstinctConditionObject co = instincts[i].conditionObject;
                        if (co == InstinctConditionObject.TooCold) c.avoidsCold = true;
                        else if (co == InstinctConditionObject.TooWarm) c.avoidsHeat = true;
                    }
                }
            }

            SkinPattern skinPattern = animal.skinPattern;
            c.hasSecondaryColor = (skinPattern != SkinPattern.Plain);

            float maxCryLoudness = 0f;
            float maxStepLoudness = 0f;
            _calcCryPitches.Clear();
            _calcStepPitches.Clear();
            _calcFoodDict.Clear();
            if (c.canEatDetailedFoodTypes == null) c.canEatDetailedFoodTypes = new HashSet<DetailedFoodType>();
            else c.canEatDetailedFoodTypes.Clear();

            float prevDiameter = 2f;
            float prevY = 0f;
            float curY = 0f;
            float lowestY = -999999f;
            int mouthAttack = 0;
            int accAttack = 0;
            int limbAttack = 0;
            int accArmor = 0;
            float maxDigReduction = 0f;
            float fatInsulation = 0f;
            float maxWalkStep = 0f;
            float maxGlideStep = 0f;
            float maxFlyStep = 0f;
            int walkLimbCount = 0;
            float topSwimSpeed = 0f;
            float frontSwimSpeed = 0f;
            float sideSwimSpeed = 0f;
            int num22 = 0;
            int num23 = 0;
            int num24 = 0;
            int num25 = 0;
            bool requireTwice = false;

            if (bodySize == 11) c.weight += 0.1f;
            else if (bodySize == 12) c.weight += 0.2f;
            else if (bodySize == 13) c.weight += 0.3f;

            bool useMorph = youngVersion || animal.MorphNeedsToBeUsed();
            Dictionary<BodyPartDiffLocation, BodyPartDiff> morphDiffs = null;
            if (useMorph)
            {
                morphDiffs = animal.GetMorph(youngVersion).bodyPartDiffs;
            }
            Dictionary<BodyPartDiffLocation, BodyPartDiff> adultMorphDiffs = null;
            bool useAdultMorph = animal.MorphNeedsToBeUsed();
            if (useAdultMorph)
            {
                adultMorphDiffs = animal.GetMorph(false).bodyPartDiffs;
            }

            HashSet<DetailedFoodType> excludeFromDiet = animal.excludeFromDiet;
            TorsoPartDefinition[] torso = animal.torso;
            for (int tIdx = 0; tIdx < torso.Length; tIdx++)
            {
                TorsoPartDefinition seg = torso[tIdx];
                float diam = seg.diameter;
                if (!(fatInsulation >= c.maximumInsulationByFat) && !(diam <= c.fatBoundary))
                {
                    c.minimumTemperatureThisOrganismCanHandle -= (diam - c.fatBoundary) * 5f;
                    c.maximumTemperatureThisOrganismCanHandle -= diam - c.fatBoundary;
                    fatInsulation += (diam - c.fatBoundary) * 5f;
                }
                if (!(diam <= c.weightLessTorsoBoundary))
                {
                    c.weight += 0.1f;
                    if (!(diam <= c.lightWeightTorsoBoundary)) c.weight += 0.1f;
                }
                float dDiff = diam - prevDiameter;
                if (!(dDiff <= 0f)) c.bodyDrag += Mathf.Floor(dDiff / 3f);
                prevDiameter = diam;
                prevY = curY;
                curY += seg.displacement;
                if (lowestY == -999999f || !(curY >= lowestY)) lowestY = curY;

                bool segHasWalkLimb = false;

                // Build effective children for this torso segment in reusable scratch dictionary.
                Dictionary<TorsoPartDetailedLocation, BodyPartDefinition> kids = seg.children;
                if (useMorph && morphDiffs != null && morphDiffs.Count > 0)
                {
                    _calcTorsoKids.Clear();
                    if (kids != null)
                    {
                        foreach (KeyValuePair<TorsoPartDetailedLocation, BodyPartDefinition> kv in kids)
                        {
                            _calcTorsoKids[kv.Key] = kv.Value;
                        }
                    }
                    foreach (KeyValuePair<BodyPartDiffLocation, BodyPartDiff> dkv in morphDiffs)
                    {
                        if (dkv.Key.torsoPart != tIdx) continue;
                        TorsoPartDetailedLocation tLoc = new TorsoPartDetailedLocation(dkv.Key.side, dkv.Key.offset);
                        if (dkv.Value.type == BodyPartDiffType.OtherBodyPart)
                        {
                            _calcTorsoKids[tLoc] = AnimalMorph.DiffToBodyPart(dkv.Value);
                        }
                        else if (dkv.Value.type == BodyPartDiffType.NoBodyPart)
                        {
                            _calcTorsoKids.Remove(tLoc);
                        }
                    }
                    kids = _calcTorsoKids;
                }

                if (kids != null && kids.Count > 0)
                {
                    foreach (KeyValuePair<TorsoPartDetailedLocation, BodyPartDefinition> kv in kids)
                    {
                        TorsoPartSide side = kv.Key.side;
                        TorsoPartLocationOffset offset = kv.Key.locationOffset;
                        BodyPartDefinition rawPart = kv.Value;
                        if (rawPart == null) continue;

                        // Compute bodyPartScale identically to AnimalDefinition.GetBodyPartScale
                        float bodyPartScale;
                        bool scaleFromDiff = false;
                        if (useMorph && morphDiffs != null && morphDiffs.Count > 0)
                        {
                            BodyPartDiffLocation dKey = new BodyPartDiffLocation { torsoPart = tIdx, side = side, offset = offset };
                            BodyPartDiff diffVal;
                            if (morphDiffs.TryGetValue(dKey, out diffVal))
                            {
                                bodyPartScale = diffVal.otherScale;
                                scaleFromDiff = true;
                            }
                            else
                            {
                                bodyPartScale = 0f;
                            }
                        }
                        else
                        {
                            bodyPartScale = 0f;
                        }

                        if (!scaleFromDiff)
                        {
                            BodyPartDefinition basePart = null;
                            if (seg.children != null) seg.children.TryGetValue(kv.Key, out basePart);
                            if (basePart == null) bodyPartScale = -1f;
                            else if (youngVersion) bodyPartScale = (basePart.bodyPartTypeIndex != 7) ? 0.5f : 0.8f;
                            else bodyPartScale = basePart.scale;
                        }

                        // Resolve full stats template without allocating a clone
                        BodyPartDefinition part = useMorph ? LookupTemplateBodyPart(importer, rawPart) : rawPart;
                        if (part == null) continue;
                        float partOwnScale = useMorph
                            ? ((rawPart.bodyPartTypeIndex == 6 || rawPart.bodyPartTypeIndex == 7 || rawPart.bodyPartTypeIndex == 10) ? rawPart.scale : part.scale)
                            : part.scale;

                        c.weight += AnimalStats.ProcessRangeStatistic(part.weight, bodyPartScale);
                        c.drag += part.drag;
                        if (part.canDisplay)
                            c.extraAttractivenessWhileDisplaying += AnimalStats.ProcessRangeStatistic(part.attractiveness, bodyPartScale);
                        else
                            c.attractiveness += AnimalStats.ProcessRangeStatistic(part.attractiveness, bodyPartScale);

                        LimbDefinition limb = part as LimbDefinition;
                        if (limb != null)
                        {
                            if (!(limb.stepLoudness <= maxStepLoudness)) maxStepLoudness = limb.stepLoudness;
                            AddUniquePitch(_calcStepPitches, limb.stepPitch);
                            c.embryoDevelopmentTime += sizeMinus9 * limb.embryoDevelopmentTime;
                            if (limb.canWalk && (side == TorsoPartSide.Side || side == TorsoPartSide.Bottom))
                            {
                                c.weightSupportedForWalking += limb.weightSupportedForWalking;
                                c.hasLimbsForWalking = true;
                                segHasWalkLimb = true;
                                walkLimbCount++;
                                if (!(limb.stepSize <= maxWalkStep)) maxWalkStep = limb.stepSize;
                            }
                            if (limb.aquaticBehavior == LimbAquaticBehavior.Sessile) c.sessile = true;
                            c.walkingStyle = limb.walkingStyle;
                            if (limb.sprawlingWalk) c.sprawlingWalk = true;
                            if (limb.requireTwice) requireTwice = true;
                            if (limb.aquaticBehavior == LimbAquaticBehavior.WalkOnWaterSurface) c.aquaticBehavior = LimbAquaticBehavior.WalkOnWaterSurface;
                            else if (limb.aquaticBehavior == LimbAquaticBehavior.WalkOnOceanFloor) c.aquaticBehavior = LimbAquaticBehavior.WalkOnOceanFloor;
                            if (!(limb.waterSurfaceBoost <= c.waterSurfaceBoost)) c.waterSurfaceBoost = limb.waterSurfaceBoost;
                            c.animationPack = limb.animationPack;
                            if (c.swimAnimationType != SwimAnimationType.WalkOnWaterSurface)
                            {
                                SwimAnimationType st = limb.swimAnimationType;
                                if (st == SwimAnimationType.Wing || st == SwimAnimationType.Jet || st == SwimAnimationType.Undulating || st == SwimAnimationType.UndulatingFast || st == SwimAnimationType.WalkOnWaterSurface)
                                    c.swimAnimationType = st;
                                else if (st == SwimAnimationType.Paddle && c.swimAnimationType != SwimAnimationType.Wing)
                                    c.swimAnimationType = SwimAnimationType.Paddle;
                            }
                            if (side == TorsoPartSide.Side && !(limb.underWaterSpeed <= sideSwimSpeed))
                            {
                                sideSwimSpeed = limb.underWaterSpeed;
                            }
                            else if (side == TorsoPartSide.Front && !(limb.underWaterSpeed <= frontSwimSpeed))
                            {
                                frontSwimSpeed = limb.underWaterSpeed;
                                if (limb.swimAnimationType == SwimAnimationType.Vertical && c.swimAnimationType == SwimAnimationType.Horizontal)
                                    c.swimAnimationType = SwimAnimationType.Vertical;
                            }
                            else if (side == TorsoPartSide.Top && !(limb.underWaterSpeed <= topSwimSpeed))
                            {
                                topSwimSpeed = limb.underWaterSpeed;
                            }
                            if (limb.canClimb)
                            {
                                if (!(limb.weightSupportedForClimbing <= c.weightSupportedForClimbing)) c.weightSupportedForClimbing = limb.weightSupportedForClimbing;
                                c.hasLimbsForClimbing = true;
                            }
                            if (limb.canGlide && (skinType == SkinType.Feathered || !limb.feathersRequiredForGlidingAndFlying))
                            {
                                if (!(limb.weightSupportedForGliding <= c.weightSupportedForGliding)) c.weightSupportedForGliding = limb.weightSupportedForGliding;
                                c.hasLimbsForGliding = true;
                                if (!(limb.stepSize <= maxGlideStep)) maxGlideStep = limb.stepSize;
                            }
                            if (limb.canFly && (skinType == SkinType.Feathered || !limb.feathersRequiredForGlidingAndFlying))
                            {
                                if (!(limb.weightSupportedForFlying <= c.weightSupportedForFlying)) c.weightSupportedForFlying = limb.weightSupportedForFlying;
                                c.hasLimbsForFlying = true;
                                if (!(limb.stepSize <= maxFlyStep)) maxFlyStep = limb.stepSize;
                                if (limb.canSteerInTheAir) c.canSteerInTheAir = true;
                                c.flyAnimationType = limb.flyAnimationType;
                            }
                            if (!(limb.energyDecreaseForSoilNestCreation <= maxDigReduction)) maxDigReduction = limb.energyDecreaseForSoilNestCreation;
                            if (limb.canDig) c.canDig = true;
                            if (limb.canHangAsBaby) c.canHangAsBaby = true;
                            if (limb.canHangFromBranch) c.canHangFromBranch = true;
                            if (limb.canHangUpsideDownFromBranch) c.canHangUpsideDownFromBranch = true;
                            if (limb.attack > limbAttack) limbAttack = limb.attack;
                            if (!(limb.maximumSteepness <= c.maximumSteepness)) c.maximumSteepness = limb.maximumSteepness;
                            if (limb.noiseProduction)
                            {
                                c.canMakeNoise = true;
                                c.genericNoiseSoundPack = limb.soundPack as string;
                                c.genericNoiseSoundPackReferenceBodySize = limb.soundPackReferenceBodySize;
                            }
                            if (limb.noiseDuringMovement)
                            {
                                c.noiseDuringMovement = true;
                                c.movementNoiseSoundPack = limb.soundPack as string;
                                c.movementNoiseSoundPackReferenceBodySize = limb.soundPackReferenceBodySize;
                            }
                            string atkFx = importer.ImportAttackParticleEffect(limb.shapeName);
                            if (atkFx != string.Empty) c.attackParticleEffect = atkFx;
                            if (c.libraryProfile.parameter1 == 0 || c.libraryProfile.parameter1 == -1)
                                c.libraryProfile.parameter1 = limb.shapeIndex;
                            else
                                c.libraryProfile.parameter2 = limb.shapeIndex;
                        }
                        else if (part is MouthDefinition)
                        {
                            MouthDefinition mouth = (MouthDefinition)part;
                            if (mouth.eatsNectar) { c.eatsNectar = true; c.canEat = true; }
                            if (mouth.eatsPlants) { c.eatsPlants = true; c.canEat = true; }
                            if (mouth.eatsMeat)
                            {
                                c.eatsMeat = true;
                                c.canEat = true;
                                if (mouth.getsMostEnergyFromMeat) c.getsMostEnergyFromMeat = true;
                            }
                            if (mouth.drinksBlood) { c.drinksBlood = true; c.canEat = true; }
                            if (!c.canEat && mouth.energyPerFoodType.Count > 0) c.canEat = true;
                            if (!(mouth.cryLoudness <= maxCryLoudness)) maxCryLoudness = mouth.cryLoudness;
                            AddUniquePitch(_calcCryPitches, mouth.cryPitch);
                            foreach (KeyValuePair<DetailedFoodType, float> fkv in mouth.energyPerFoodType)
                            {
                                DetailedFoodType ft = fkv.Key;
                                c.canEatDetailedFoodTypes.Add(ft);
                                if (excludeFromDiet != null && excludeFromDiet.Contains(ft)) continue;
                                float existing;
                                if (_calcFoodDict.TryGetValue(ft, out existing))
                                {
                                    if (!(existing >= fkv.Value)) _calcFoodDict[ft] = fkv.Value;
                                }
                                else
                                {
                                    _calcFoodDict[ft] = fkv.Value;
                                }
                                if (ft == DetailedFoodType.Plankton) c.filterFeeder = true;
                            }
                            float midY = 0.5f * (curY + prevY);
                            if (c.highestMouthHeight == 999999f || !(midY <= c.highestMouthHeight)) c.highestMouthHeight = midY;
                            c.embryoDevelopmentTime += sizeMinus9 * mouth.embryoDevelopmentTime;
                            if (mouth.attack > mouthAttack) mouthAttack = mouth.attack;
                            if (!(mouth.defenseAgainstSpikes <= (float)c.defenseForEatingSpikes)) c.defenseForEatingSpikes = (int)mouth.defenseAgainstSpikes;
                            if (!(mouth.increasesEatingHeight <= c.mouthIncreasesEatingHeight)) c.mouthIncreasesEatingHeight = mouth.increasesEatingHeight;
                            if (!(mouth.venomStrength <= c.venomStrength)) c.venomStrength = mouth.venomStrength;
                            if (side == TorsoPartSide.Side)
                            {
                                c.weight += AnimalStats.ProcessRangeStatistic(mouth.weight, partOwnScale);
                                c.drag += mouth.drag;
                                c.embryoDevelopmentTime += sizeMinus9 * mouth.embryoDevelopmentTime;
                            }
                            c.maximumTemperatureThisOrganismCanHandle += mouth.heatLoss;
                            c.minimumTemperatureThisOrganismCanHandle += mouth.heatLoss;
                            if (mouth.canWax) c.canWax = true;
                            if (mouth.shapeName == "blowhole") c.canBlow = true;
                            c.callSoundPack = mouth.soundPack as string;
                            c.callSoundPackReferenceBodySize = mouth.soundPackReferenceBodySize;
                            if (mouth.timeInWater != -1 && mouth.timeInWater > c.timeInWater) c.timeInWater = mouth.timeInWater;
                            if (mouth.timeOnLand != -1 && mouth.timeOnLand > c.timeOnLand) c.timeOnLand = mouth.timeOnLand;
                            c.libraryProfile.parameter0 = mouth.shapeIndex;
                        }
                        else if (part is EarDefinition)
                        {
                            EarDefinition ear = (EarDefinition)part;
                            c.canHear = true;
                            if (c.lowestDetectablePitch == -1f || !(ear.pitchHearingRange.x >= c.lowestDetectablePitch)) c.lowestDetectablePitch = ear.pitchHearingRange.x;
                            if (c.highestDetectablePitch == -1f || !(ear.pitchHearingRange.y <= c.highestDetectablePitch)) c.highestDetectablePitch = ear.pitchHearingRange.y;
                            if (c.closestHearingDistance == -1f || !(1f - ear.loudnessHearingRange.y >= c.closestHearingDistance)) c.closestHearingDistance = 1f - ear.loudnessHearingRange.y;
                            if (c.furthestHearingDistance == -1f || !(1f - ear.loudnessHearingRange.x <= c.furthestHearingDistance)) c.furthestHearingDistance = 1f - ear.loudnessHearingRange.x;
                            c.maximumTemperatureThisOrganismCanHandle += ear.heatLoss;
                            c.minimumTemperatureThisOrganismCanHandle += ear.heatLoss;
                            if (side == TorsoPartSide.Side) c.drag += ear.drag;
                        }
                        else if (part is EyeDefinition)
                        {
                            EyeDefinition eye = (EyeDefinition)part;
                            c.canSee = true;
                            float d1 = AnimalStats.ProcessRangeStatistic(eye.maximumSightDistanceDuringDayTime, partOwnScale);
                            if (c.furthestSeeingDistanceDuringDayTime == -1f || !(d1 <= c.furthestSeeingDistanceDuringDayTime)) c.furthestSeeingDistanceDuringDayTime = d1;
                            float d2 = AnimalStats.ProcessRangeStatistic(eye.maximumSightDistanceAtTwilight, partOwnScale);
                            if (c.furthestSeeingDistanceAtTwilight == -1f || !(d2 <= c.furthestSeeingDistanceAtTwilight)) c.furthestSeeingDistanceAtTwilight = d2;
                            float d3 = AnimalStats.ProcessRangeStatistic(eye.maximumSightDistanceAtNight, partOwnScale);
                            if (c.furthestSeeingDistanceAtNight == -1f || !(d3 <= c.furthestSeeingDistanceAtNight)) c.furthestSeeingDistanceAtNight = d3;
                            float d4 = AnimalStats.ProcessRangeStatistic(eye.maximumUnderWaterSightDistanceDuringDayTime, partOwnScale);
                            if (c.furthestUnderWaterSeeingDistanceDuringDayTime == -1f || !(d4 <= c.furthestUnderWaterSeeingDistanceDuringDayTime)) c.furthestUnderWaterSeeingDistanceDuringDayTime = d4;
                            float d5 = AnimalStats.ProcessRangeStatistic(eye.maximumUnderWaterSightDistanceAtTwilight, partOwnScale);
                            if (c.furthestUnderWaterSeeingDistanceAtTwilight == -1f || !(d5 <= c.furthestUnderWaterSeeingDistanceAtTwilight)) c.furthestUnderWaterSeeingDistanceAtTwilight = d5;
                            float d6 = AnimalStats.ProcessRangeStatistic(eye.maximumUnderWaterSightDistanceAtNight, partOwnScale);
                            if (c.furthestUnderWaterSeeingDistanceAtNight == -1f || !(d6 <= c.furthestUnderWaterSeeingDistanceAtNight)) c.furthestUnderWaterSeeingDistanceAtNight = d6;
                            if (c.colorSeeingMinLimit == -1f || !(eye.colorSeeingRange.x >= c.colorSeeingMinLimit))
                            {
                                c.colorSeeingMinLimit = eye.colorSeeingRange.x;
                                c.shapeNameOfEyeWithWidestColorVision = eye.shapeName;
                            }
                            if (c.colorSeeingMaxLimit == -1f || !(eye.colorSeeingRange.y <= c.colorSeeingMaxLimit))
                            {
                                c.colorSeeingMaxLimit = eye.colorSeeingRange.y;
                                c.shapeNameOfEyeWithWidestColorVision = eye.shapeName;
                            }
                            if (!((float)eye.fieldOfView <= c.fieldOfView)) c.fieldOfView = eye.fieldOfView;
                            if (side == TorsoPartSide.Side) c.drag += eye.drag;
                            c.libraryProfile.parameter3 = eye.shapeIndex;
                        }
                        else if (part is AnimalAccessoireDefinition)
                        {
                            AnimalAccessoireDefinition acc = (AnimalAccessoireDefinition)part;
                            c.maximumTemperatureThisOrganismCanHandle += acc.heatLoss;
                            c.minimumTemperatureThisOrganismCanHandle += acc.heatLoss;
                            c.embryoDevelopmentTime += sizeMinus9 * acc.embryoDevelopmentTime;
                            if (acc.attack > accAttack) accAttack = acc.attack;
                            if (acc.armor > accArmor) accArmor = acc.armor;
                            if (acc.glowing)
                            {
                                c.canGlow = true;
                                if (!hasGlowInstinct) c.alwaysGlowing = true;
                            }
                            if (acc.liquidStorage) c.hasLiquidStorage = true;
                            if (!(acc.providesMilk <= c.providesMilk)) c.providesMilk = acc.providesMilk;
                            if (!(acc.venomStrength <= c.venomStrength)) c.venomStrength = acc.venomStrength;
                            if (acc.shellCover) c.canHideInShell = true;
                            if (acc.noiseProduction)
                            {
                                c.canMakeNoise = true;
                                c.genericNoiseSoundPack = acc.soundPack as string;
                                c.genericNoiseSoundPackReferenceBodySize = acc.soundPackReferenceBodySize;
                            }
                            if (side == TorsoPartSide.Side) c.drag += acc.drag;
                            string atkFx = importer.ImportAttackParticleEffect(acc.shapeName);
                            if (atkFx != string.Empty) c.attackParticleEffect = atkFx;
                        }

                        if (!c.hasSecondaryColor)
                        {
                            c.hasSecondaryColor = importer.ImportMaterialType(part.shapeName) == MaterialType.Secondary;
                            if (!c.hasSecondaryColor)
                            {
                                c.hasSecondaryColor = importer.ImportSpecialMaterialType(part.shapeName) == MaterialType.Secondary;
                                if (!c.hasSecondaryColor)
                                {
                                    c.hasSecondaryColor = importer.ImportLightIntensityOfBodyPartShape(part.shapeName) > 0f;
                                }
                            }
                        }
                    }
                }

                // Inline HasBodyPartOnLocation(tIdx, Side, None, youngVersion: false)
                bool hasSidePart = false;
                if (useAdultMorph && adultMorphDiffs != null && adultMorphDiffs.Count > 0)
                {
                    BodyPartDiffLocation sKey = new BodyPartDiffLocation { torsoPart = tIdx, side = TorsoPartSide.Side, offset = TorsoPartLocationOffset.None };
                    BodyPartDiff sDiff;
                    if (adultMorphDiffs.TryGetValue(sKey, out sDiff))
                    {
                        hasSidePart = (sDiff.type != BodyPartDiffType.NoBodyPart);
                    }
                    else
                    {
                        hasSidePart = seg.HasChild(TorsoPartSide.Side, TorsoPartLocationOffset.None);
                    }
                }
                else
                {
                    hasSidePart = seg.HasChild(TorsoPartSide.Side, TorsoPartLocationOffset.None);
                }
                num22 = hasSidePart ? 0 : (num22 + 1);

                if (segHasWalkLimb)
                {
                    num25++;
                    if (c.hasLimbsForWalking) num24 = 0;
                }
                else if (c.hasLimbsForWalking) num24++;
                else num23++;
            }

            if (!(c.providesMilk <= 0f))
            {
                c.canEatDetailedFoodTypes.Add(DetailedFoodType.Milk);
                _calcFoodDict[DetailedFoodType.Milk] = 0.001f;
            }
            if (c.closestHearingDistance != -1f) c.closestHearingDistance *= c.soundDistanceFactor;
            if (c.furthestHearingDistance != -1f) c.furthestHearingDistance *= c.soundDistanceFactor;
            if (maxCryLoudness != -1f) maxCryLoudness *= c.soundDistanceFactor;
            if (maxStepLoudness != -1f) maxStepLoudness *= c.soundDistanceFactor;
            if (c.furthestSeeingDistanceDuringDayTime != -1f) c.furthestSeeingDistanceDuringDayTime *= c.sightDistanceFactor;
            if (c.furthestSeeingDistanceAtTwilight != -1f) c.furthestSeeingDistanceAtTwilight *= c.sightDistanceFactor;
            if (c.furthestSeeingDistanceAtNight != -1f) c.furthestSeeingDistanceAtNight *= c.sightDistanceFactor;

            int fCount = _calcFoodDict.Count;
            DetailedFoodType[] orderedFoods = new DetailedFoodType[fCount];
            float[] foodEnergies = new float[fCount];
            int fIdx = 0;
            foreach (KeyValuePair<DetailedFoodType, float> fkv in _calcFoodDict)
            {
                orderedFoods[fIdx] = fkv.Key;
                foodEnergies[fIdx] = fkv.Value;
                fIdx++;
            }
            if (fCount > 1)
            {
                Array.Sort(foodEnergies, orderedFoods);
                Array.Reverse(foodEnergies);
                Array.Reverse(orderedFoods);
            }
            c.orderedDetailedFoodTypes = orderedFoods;
            c.energyPerDetailedFoodType = foodEnergies;

            c.highestMouthHeight -= lowestY;
            c.highestMouthHeight = (float)(bodySize - 6) + 0.15f * c.highestMouthHeight;
            if (!(c.highestMouthHeight <= 1000f))
            {
                c.canEatAtHeight = -1f;
            }
            else
            {
                if (!(c.highestMouthHeight <= 10f)) c.highestMouthHeight = 10f;
                c.canEatAtHeight = (c.highestMouthHeight - 1f) / 9f + c.mouthIncreasesEatingHeight;
            }

            c.attack = (mouthAttack > 0) ? (9 * sizeMinus10 + mouthAttack) : 0;
            if (limbAttack > 0) c.attack += 9 * sizeMinus10 + limbAttack;
            if (accAttack > 0) c.attack += 9 * sizeMinus10 + accAttack;
            if (accArmor > 0) c.armor += accArmor;

            c.minimumTemperatureThisOrganismCanHandle = (float)Math.Round(c.minimumTemperatureThisOrganismCanHandle, 1);
            c.maximumTemperatureThisOrganismCanHandle = (float)Math.Round(c.maximumTemperatureThisOrganismCanHandle, 1);
            c.pregnancyTime = (int)((float)c.embryoDevelopmentTime * (animal.embryoTimeInsideMother * 0.25f));
            c.underWaterBodyPartSpeed = frontSwimSpeed + topSwimSpeed + sideSwimSpeed;
            c.canSwim = c.underWaterBodyPartSpeed > 0f;
            c.swimmingSpeed = Mathf.Clamp(c.underWaterBodyPartSpeed - c.drag - c.bodyDrag, 1f, 10f);
            c.maximumOceanCurrent = Mathf.Clamp((2.5f + c.swimmingSpeed + (float)sizeMinus9) / 10f, 0f, 1f);
            c.weight *= SimulationTools.DenormalizeGravity(c.gravity);

            bool upright = animal.upright;
            if (upright) c.animationPack = AnimationPack.Upright;
            if (num22 == 0 && (c.swimAnimationType == SwimAnimationType.Horizontal || c.swimAnimationType == SwimAnimationType.Vertical))
            {
                c.swimAnimationType = SwimAnimationType.Flipper;
            }
            if (c.hasLimbsForWalking && !(c.weightSupportedForWalking < c.weight))
            {
                c.canWalk = true;
                if (c.sessile)
                {
                    c.speed = 0f;
                    c.aquaticBehavior = LimbAquaticBehavior.Sessile;
                }
                else
                {
                    float num34 = Mathf.Clamp(maxWalkStep, 0f, 1f) * 0.5f;
                    c.speed = Mathf.Clamp(maxWalkStep - (float)(walkLimbCount - 1) * num34, 0.25f, 25f);
                }
            }
            if (c.sessile) c.aquaticBehavior = LimbAquaticBehavior.Sessile;
            c.balancedForWalking = true;
            if (!upright && (num24 - num25 - num23 > 0 || num23 - num25 - num24 > 0 || (requireTwice && num25 < 2)))
            {
                c.balancedForWalking = false;
                c.canWalk = false;
            }
            if (c.hasLimbsForClimbing && !(c.weightSupportedForClimbing < c.weight))
            {
                c.canClimb = true;
                c.canEatAtHeight = 1f;
            }
            if (c.hasLimbsForGliding && !(c.weightSupportedForGliding < c.weight))
            {
                c.canGlide = true;
                c.speed = maxGlideStep;
            }
            if (c.hasLimbsForFlying && !(c.weightSupportedForFlying < c.weight))
            {
                c.canFly = true;
                c.speed = maxFlyStep;
                c.canEatAtHeight = 1f;
            }
            c.doesEatingDamage = (c.timeOnLand == -1) ? (10 * sizeMinus10) : sizeMinus10;
            if (c.doesEatingDamage == 0f) c.doesEatingDamage = 5f;

            if (c.canEat) c.crySound = new SoundDefinition(maxCryLoudness, _calcCryPitches.ToArray());
            if (c.canWalk) c.stepSound = new SoundDefinition(maxStepLoudness, _calcStepPitches.ToArray());
            else c.speed = 0f;
            if (c.canMakeNoise || c.noiseDuringMovement) c.noiseSound = new SoundDefinition(0.5f, _oneFloatArr);

            if (nestType == NestType.Burrow)
            {
                if (c.canDig) c.energyNeededForNestCreation -= maxDigReduction;
                else { c.energyNeededForNestCreation = 0f; c.ableToBuildNest = false; }
            }
            else if (nestType == NestType.Wax && !c.canWax)
            {
                c.energyNeededForNestCreation = 0f;
                c.ableToBuildNest = false;
            }
            if (skinPattern != SkinPattern.Plain)
            {
                c.attractiveness += SimulationTools.CalculateDifferenceBetweenTwoColors(animal.colorHue, animal.colorBrightness, animal.secondaryColorHue, animal.secondaryColorBrightness) * 0.4f;
            }

            if (animal.offspringSexDecisionType != OffspringSexDecisionType.Chance || animal.sexes == null || animal.sexes.Length <= 0)
            {
                return;
            }
            _calcSexCounts.Clear();
            for (int k = 0; k < 12; k++)
            {
                int b = animal.GetOffspringSexChanceBracket(k);
                int cur;
                _calcSexCounts.TryGetValue(b, out cur);
                _calcSexCounts[b] = cur + 1;
            }
            float[] chances = new float[animal.sexes.Length];
            for (int l = 0; l < chances.Length; l++)
            {
                int cnt;
                chances[l] = _calcSexCounts.TryGetValue(l, out cnt) ? ((float)cnt / 12f) : 0f;
            }
            c.offSpringSexChances = chances;
        }

        private static void ApplyStatsToLivingAnimal(LivingAnimal a, AnimalStatsCalculator s)
        {
            AnimalDefinition def = a.definition;
            a.mostSalientColor = def.colorHue;
            a.colorBrightness = def.colorBrightness;
            a.canWalk = s.canWalk;
            a.canClimb = s.canClimb;
            a.canGlide = s.canGlide;
            a.canFly = s.canFly;
            a.canSwim = s.canSwim;
            a.aquaticBehavior = s.aquaticBehavior;
            a.canEat = s.canEat;
            a.eatsPlants = s.eatsPlants;
            a.eatsNectar = s.eatsNectar;
            a.eatsMeat = s.eatsMeat;
            a.getsMostEnergyFromMeat = s.getsMostEnergyFromMeat;
            a.eatsMeatOnly = s.canEat && s.eatsMeat && !s.eatsPlants && !s.eatsNectar;
            a.doesEatingDamage = (int)s.doesEatingDamage;
            a.filterFeeder = s.filterFeeder;
            a.expectedAge = s.expectedAge;
            a.matureAtAge = s.matureAtAge;
            a.healthDeterioratesAtAge = s.healthDeterioratesAtAge;
            a.healthDeteriorationRatePerDayAtOldAge = s.healthDeteriorationRatePerDayAtOldAge;
            a.baseAttractiveness = s.attractiveness;
            if (!(a.baseAttractiveness <= a.attractiveness)) a.attractiveness = a.baseAttractiveness;
            a.extraAttractivenessWhileDisplaying = s.extraAttractivenessWhileDisplaying;
            a.energyNeededToReproduce = s.energyNeededToReproduce;
            a.sessile = s.sessile;
            a.speed = s.speed;
            a.stepSound = s.stepSound;
            a.swimmingSpeed = s.swimmingSpeed;
            a.maximumOceanCurrent = s.maximumOceanCurrent;
            a.timeInWater = s.timeInWater;
            a.timeOnLand = s.timeOnLand;
            a.waterSurfaceBoost = s.waterSurfaceBoost;
            a.maximumSteepness = s.maximumSteepness;
            a.animationPack = s.animationPack;
            a.swimAnimationType = s.swimAnimationType;
            a.flyAnimationType = s.flyAnimationType;
            a.walkingStyle = s.walkingStyle;
            a.sprawlingWalk = s.sprawlingWalk;
            a.canSteerInTheAir = s.canSteerInTheAir;
            a.attackParticleEffect = s.attackParticleEffect;
            a.lowestDetectablePitch = s.lowestDetectablePitch;
            a.highestDetectablePitch = s.highestDetectablePitch;
            a.colorSeeingMinLimit = s.colorSeeingMinLimit;
            a.colorSeeingMaxLimit = s.colorSeeingMaxLimit;
            a.closestHearingDistance = s.closestHearingDistance;
            a.furthestHearingDistance = s.furthestHearingDistance;
            a.furthestSeeingDistanceDuringDayTime = s.furthestSeeingDistanceDuringDayTime;
            a.furthestSeeingDistanceAtTwilight = s.furthestSeeingDistanceAtTwilight;
            a.furthestSeeingDistanceAtNight = s.furthestSeeingDistanceAtNight;
            a.furthestUnderWaterSeeingDistanceDuringDayTime = s.furthestUnderWaterSeeingDistanceDuringDayTime;
            a.furthestUnderWaterSeeingDistanceAtTwilight = s.furthestUnderWaterSeeingDistanceAtTwilight;
            a.furthestUnderWaterSeeingDistanceAtNight = s.furthestUnderWaterSeeingDistanceAtNight;
            a.fieldOfView = (int)s.fieldOfView;
            a.crySound = s.crySound;
            a.noiseSound = s.noiseSound;
            a.highestMouthHeight = s.highestMouthHeight;
            a.canEatAtHeight = s.canEatAtHeight;
            a.orderedDetailedFoodTypes = s.orderedDetailedFoodTypes;
            a.energyPerDetailedFoodType = s.energyPerDetailedFoodType;
            a.canGlow = s.canGlow;
            a.alwaysGlowing = s.alwaysGlowing;
            a.glowing = s.alwaysGlowing;
            a.canHangAsBaby = s.canHangAsBaby;
            a.canHangFromBranch = s.canHangFromBranch;
            a.canHangUpsideDownFromBranch = s.canHangUpsideDownFromBranch;
            a.hasLiquidStorage = s.hasLiquidStorage;
            a.minimumTemperatureThisOrganismCanHandle = s.minimumTemperatureThisOrganismCanHandle;
            a.maximumTemperatureThisOrganismCanHandle = s.maximumTemperatureThisOrganismCanHandle;
            a.dailyHealthLossOutsideTemperatureRange = s.relativeDamageOutsideTemperatureRange * a.maximumHealth;
            a.dailyHealthLossWhileSuffocating = s.relativeDamageWhileSuffocating * a.maximumHealth;
            a.dailyHealthLossBelowCurrentSpeedThreshold = s.relativeDamageBelowOceanCurrentThreshold * a.maximumHealth;
            a.pregnancyTime = s.pregnancyTime;
            a.timeInEgg = s.embryoDevelopmentTime - s.pregnancyTime;
            a.attack = s.attack;
            a.armor = s.armor;
            a.defenseForEatingSpikes = s.defenseForEatingSpikes;
            a.venomStrength = s.venomStrength;
            a.avoidsCold = s.avoidsCold;
            a.avoidsHeat = s.avoidsHeat;
            a.ableToBuildNest = s.ableToBuildNest;
            a.energyNeededForNestCreation = s.energyNeededForNestCreation;
            a.canHibernate = s.canHibernate;
            a.providesMilk = s.providesMilk;
            a.canBlow = s.canBlow;
            a.canHideInShell = s.canHideInShell;
            a.canMakeNoise = s.canMakeNoise;
            a.noiseDuringMovement = s.noiseDuringMovement;
            a.callSoundPack = s.callSoundPack;
            a.callSoundPackReferenceBodySize = s.callSoundPackReferenceBodySize;
            a.genericNoiseSoundPack = s.genericNoiseSoundPack;
            a.genericNoiseSoundPackReferenceBodySize = s.genericNoiseSoundPackReferenceBodySize;
            a.movementNoiseSoundPack = s.movementNoiseSoundPack;
            a.movementNoiseSoundPackReferenceBodySize = s.movementNoiseSoundPackReferenceBodySize;
            a.offSpringSexChances = s.offSpringSexChances;
            HormoneDefinition[] h = def.hormones;
            if (h == null || h.Length == 0)
            {
                a.hormoneLevels = _emptyFloatArray;
            }
            else
            {
                float[] hl = new float[h.Length];
                for (int i = 0; i < h.Length; i++) hl[i] = h[i].initialLevel;
                a.hormoneLevels = hl;
            }
            a.energy = a.energyNeededToReproduce;
            a.libraryProfile = s.libraryProfile;
        }

        private static bool ProcessNewAnimalDefinitionFast(TimeLine tl, LivingAnimal animal, LivingAnimal parent, CellLocationType locationType, bool newDefinitionIndex)
        {
            if (_sharedCalcA == null) _sharedCalcA = new AnimalStatsCalculator(tl.importer, tl.gravity);
            if (_sharedCalcB == null) _sharedCalcB = new AnimalStatsCalculator(tl.importer, tl.gravity);

            bool primaryIsYoung = animal.isChild && !animal.manuallyAdded;
            ResetAnimalStatsCalculator(_sharedCalcA, tl.importer, tl.gravity);
            CalculateAnimalStatsFast(_sharedCalcA, animal.definition, primaryIsYoung);
            ApplyStatsToLivingAnimal(animal, _sharedCalcA);

            tl.ChangeInstinctsAndDietToMatchSensingRangesAndPossibleActions(animal.definition, _sharedCalcA);

            ResetAnimalStatsCalculator(_sharedCalcB, tl.importer, tl.gravity);
            CalculateAnimalStatsFast(_sharedCalcB, animal.definition, !primaryIsYoung);

            AnimalStatsCalculator youngCalc = primaryIsYoung ? _sharedCalcA : _sharedCalcB;
            AnimalStatsCalculator adultCalc = primaryIsYoung ? _sharedCalcB : _sharedCalcA;

            if (OrganismProblemDetection.DetectProblemInAnimal(animal.definition, youngCalc, adultCalc) == OrganismProblem.NoProblem &&
                !OrganismProblemDetection.DetectUnlikelyOxygenSourceChange(animal, parent, locationType))
            {
                if (newDefinitionIndex)
                {
                    animal.definition.definitionIndex = tl.nextDefinitionIndex;
                    tl.nextDefinitionIndex++;
                }
                animal.visualSalienceOfDifferenceToParent = tl.animalMutator.visualSalienceOfLastChange;
                bool differs =
                    youngCalc.sessile != adultCalc.sessile ||
                    youngCalc.canWalk != adultCalc.canWalk ||
                    youngCalc.canClimb != adultCalc.canClimb ||
                    youngCalc.canGlide != adultCalc.canGlide ||
                    youngCalc.canFly != adultCalc.canFly ||
                    youngCalc.canSwim != adultCalc.canSwim ||
                    youngCalc.aquaticBehavior != adultCalc.aquaticBehavior ||
                    youngCalc.speed != adultCalc.speed ||
                    youngCalc.swimmingSpeed != adultCalc.swimmingSpeed ||
                    youngCalc.waterSurfaceBoost != adultCalc.waterSurfaceBoost ||
                    youngCalc.maximumSteepness != adultCalc.maximumSteepness ||
                    youngCalc.canSteerInTheAir != adultCalc.canSteerInTheAir;

                animal.hasCachedAdultMovementStats = differs;
                if (differs)
                {
                    animal.cachedYoungSessile = youngCalc.sessile;
                    animal.cachedYoungCanWalk = youngCalc.canWalk;
                    animal.cachedYoungCanClimb = youngCalc.canClimb;
                    animal.cachedYoungCanGlide = youngCalc.canGlide;
                    animal.cachedYoungCanFly = youngCalc.canFly;
                    animal.cachedYoungCanSwim = youngCalc.canSwim;
                    animal.cachedYoungAquaticBehavior = youngCalc.aquaticBehavior;
                    animal.cachedYoungSpeed = youngCalc.speed;
                    animal.cachedYoungSwimmingSpeed = youngCalc.swimmingSpeed;
                    animal.cachedYoungWaterSurfaceBoost = youngCalc.waterSurfaceBoost;
                    animal.cachedYoungMaximumSteepness = youngCalc.maximumSteepness;
                    animal.cachedYoungCanSteerInTheAir = youngCalc.canSteerInTheAir;
                    animal.cachedAdultSessile = adultCalc.sessile;
                    animal.cachedAdultCanWalk = adultCalc.canWalk;
                    animal.cachedAdultCanClimb = adultCalc.canClimb;
                    animal.cachedAdultCanGlide = adultCalc.canGlide;
                    animal.cachedAdultCanFly = adultCalc.canFly;
                    animal.cachedAdultCanSwim = adultCalc.canSwim;
                    animal.cachedAdultAquaticBehavior = adultCalc.aquaticBehavior;
                    animal.cachedAdultSpeed = adultCalc.speed;
                    animal.cachedAdultSwimmingSpeed = adultCalc.swimmingSpeed;
                    animal.cachedAdultWaterSurfaceBoost = adultCalc.waterSurfaceBoost;
                    animal.cachedAdultMaximumSteepness = adultCalc.maximumSteepness;
                    animal.cachedAdultCanSteerInTheAir = adultCalc.canSteerInTheAir;
                }
                return true;
            }
            return false;
        }
    }
}
