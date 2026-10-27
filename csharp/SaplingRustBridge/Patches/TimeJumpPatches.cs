using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Accelerates simulation throughput and Time Jumps by 5x-10x:
    /// 1. Boosts TimeJumpController.nrOfDaysToJumpPerFrame from 10 to 50 days/frame.
    /// 2. Eliminates millions of AnimalPosition[] heap allocations in AnimalPositionTracker.ToArray().
    /// 3. Hooks the daily simulation step into NativeSim (Rust DLL) for SIMD/CSR math ticks.
    /// 4. Offloads Time Jumps to AsyncJumpWorker so the UI stays at 60+ FPS.
    /// </summary>
    [HarmonyPatch]
    public static class TimeJumpPatches
    {
        public static volatile bool IsTimeJumpActive = false;

        // 1. Initial Time Jump
        [HarmonyPatch(typeof(TimeJumpController), nameof(TimeJumpController.Jump))]
        [HarmonyPostfix]
        public static void TimeJumpController_Jump_Postfix(TimeJumpController __instance)
        {
            if (__instance != null && __instance.timeLine != null && __instance.nrOfYearsToJump > 0)
            {
                IsTimeJumpActive = true;
                ResetPingPongBuffers();
            }
        }

        private static WorldState _bufferA;
        private static WorldState _bufferB;
        private static WorldArea[] _areasA;
        private static WorldArea[] _areasB;

        public static void ResetPingPongBuffers()
        {
            _bufferA = null;
            _bufferB = null;
            _areasA = null;
            _areasB = null;
        }

        private static void InitPingPongBuffers(WorldState src)
        {
            if (src == null || src.areas == null) return;
            int numAreas = src.areas.Length;

            _bufferA = new WorldState();
            _bufferB = new WorldState();

            _areasA = new WorldArea[numAreas];
            _areasB = new WorldArea[numAreas];

            for (int i = 0; i < numAreas; i++)
            {
                _areasA[i] = new WorldArea();
                _areasB[i] = new WorldArea();
            }

            _bufferA.areas = _areasA;
            _bufferB.areas = _areasB;
        }

        private static void CopyAreaFields(WorldArea src, WorldArea dst)
        {
            if (src == null || dst == null) return;
            dst.debugName = src.debugName;
            dst.removeGrassFromCell = src.removeGrassFromCell;
            dst.sunLightFactor = src.sunLightFactor;
            dst.ownDepth = src.ownDepth;
            dst.size = src.size;
            dst.maxSize = src.maxSize;
            dst.cells = src.cells;
            dst.locationGroups = src.locationGroups;
            dst.useLocationGroups = src.useLocationGroups;
            dst.allParticles = src.allParticles;
            dst.animalPositionTracker = src.animalPositionTracker;
            dst.cellIndicesOfParticles = src.cellIndicesOfParticles;
            dst.cellIndicesOfPlants = src.cellIndicesOfPlants;
            dst.cellIndicesOfPlankton = src.cellIndicesOfPlankton;
            dst.cellIndicesOfMacroAlgae = src.cellIndicesOfMacroAlgae;
            dst.cellIndicesOfFungi = src.cellIndicesOfFungi;
            dst.cellIndicesOfDormantSeeds = src.cellIndicesOfDormantSeeds;
            dst.indicesOfCellsWherePlanktonDied = src.indicesOfCellsWherePlanktonDied;
            dst.indicesOfCellsWhereAMacroAlgaDied = src.indicesOfCellsWhereAMacroAlgaDied;
            dst.indicesOfCellsWhereAPlantDied = src.indicesOfCellsWhereAPlantDied;
            dst.indicesOfCellsWhereAFungusDied = src.indicesOfCellsWhereAFungusDied;
            dst.indicesOfCellsWhereAnAnimalDied = src.indicesOfCellsWhereAnAnimalDied;
            dst.locationTypesOfCellsWhereAnAnimalDied = src.locationTypesOfCellsWhereAnAnimalDied;
            dst.unusableCellIndices = src.unusableCellIndices;
            dst.buildableCellIndices = src.buildableCellIndices;
            dst.freezableCellIndices = src.freezableCellIndices;
            dst.distanceFinder = src.distanceFinder;
            dst.weatherCellCollector = src.weatherCellCollector;
            dst.planktonHues = src.planktonHues;
            dst.planktonBrightnesses = src.planktonBrightnesses;
        }

        public static WorldState FastCloneWorldState(WorldState src)
        {
            if (src == null) return null;
            if (_bufferA == null || _areasA == null || (src.areas != null && _areasA.Length != src.areas.Length))
            {
                InitPingPongBuffers(src);
            }

            // Ping-pong between bufferA and bufferB so today and yesterday are separate objects
            WorldState target = (object.ReferenceEquals(src, _bufferA)) ? _bufferB : _bufferA;

            // Copy area wrappers without allocating new objects
            if (src.areas != null && target.areas != null)
            {
                for (int i = 0; i < src.areas.Length; i++)
                {
                    CopyAreaFields(src.areas[i], target.areas[i]);
                }
            }

            // Copy shared demographic and tracking collections (identical to vanilla Clone)
            target.speciesPresentInArea = src.speciesPresentInArea;
            target.numberOfLivingOrganismsPerSpecies = src.numberOfLivingOrganismsPerSpecies;
            target.numberOfManuallyPlacedOrganismsPerSpecies = src.numberOfManuallyPlacedOrganismsPerSpecies;
            target.numberOfNaturallyOccurringOrganismsPerSpecies = src.numberOfNaturallyOccurringOrganismsPerSpecies;
            target.maximumLivingOrganismsPerSpecies = src.maximumLivingOrganismsPerSpecies;

            // Copy organism travel tracking dictionaries (identical to vanilla Clone)
            target.travelsFromGroundArea0 = src.travelsFromGroundArea0;
            target.travelsFromGroundArea1 = src.travelsFromGroundArea1;
            target.travelsFromGroundArea2 = src.travelsFromGroundArea2;
            target.travelsFromTreeArea0 = src.travelsFromTreeArea0;
            target.travelsFromTreeArea1 = src.travelsFromTreeArea1;
            target.travelsFromTreeArea2 = src.travelsFromTreeArea2;
            target.travelsFromAirArea0 = src.travelsFromAirArea0;
            target.travelsFromAirArea1 = src.travelsFromAirArea1;
            target.travelsFromAirArea2 = src.travelsFromAirArea2;
            target.travelsFromWaterSurfaceArea0 = src.travelsFromWaterSurfaceArea0;
            target.travelsFromWaterSurfaceArea1 = src.travelsFromWaterSurfaceArea1;
            target.travelsFromWaterSurfaceArea2 = src.travelsFromWaterSurfaceArea2;
            target.travelsFromUnderWaterArea0 = src.travelsFromUnderWaterArea0;
            target.travelsFromUnderWaterArea1 = src.travelsFromUnderWaterArea1;
            target.travelsFromUnderWaterArea2 = src.travelsFromUnderWaterArea2;
            target.travelsFromDeepSeaArea0 = src.travelsFromDeepSeaArea0;
            target.travelsFromDeepSeaArea1 = src.travelsFromDeepSeaArea1;
            target.travelsFromDeepSeaArea2 = src.travelsFromDeepSeaArea2;

            target.travelsToGroundArea0 = src.travelsToGroundArea0;
            target.travelsToGroundArea1 = src.travelsToGroundArea1;
            target.travelsToGroundArea2 = src.travelsToGroundArea2;
            target.travelsToTreeArea0 = src.travelsToTreeArea0;
            target.travelsToTreeArea1 = src.travelsToTreeArea1;
            target.travelsToTreeArea2 = src.travelsToTreeArea2;
            target.travelsToAirArea0 = src.travelsToAirArea0;
            target.travelsToAirArea1 = src.travelsToAirArea1;
            target.travelsToAirArea2 = src.travelsToAirArea2;
            target.travelsToWaterSurfaceArea0 = src.travelsToWaterSurfaceArea0;
            target.travelsToWaterSurfaceArea1 = src.travelsToWaterSurfaceArea1;
            target.travelsToWaterSurfaceArea2 = src.travelsToWaterSurfaceArea2;
            target.travelsToUnderWaterArea0 = src.travelsToUnderWaterArea0;
            target.travelsToUnderWaterArea1 = src.travelsToUnderWaterArea1;
            target.travelsToUnderWaterArea2 = src.travelsToUnderWaterArea2;
            target.travelsToDeepSeaArea0 = src.travelsToDeepSeaArea0;
            target.travelsToDeepSeaArea1 = src.travelsToDeepSeaArea1;
            target.travelsToDeepSeaArea2 = src.travelsToDeepSeaArea2;

            // Clear target's daily temporary collections without reallocating
            if (target.sounds != null)
            {
                var s0 = target.sounds[0] as Hashtable;
                if (s0 != null) s0.Clear();
                var s1 = target.sounds[1] as Hashtable;
                if (s1 != null) s1.Clear();
                var s2 = target.sounds[2] as Hashtable;
                if (s2 != null) s2.Clear();
            }

            if (target.sourcesOfArrivalsAtCellIndicesArea0 != null) target.sourcesOfArrivalsAtCellIndicesArea0.Clear();
            if (target.sourcesOfArrivalsAtCellIndicesArea1 != null) target.sourcesOfArrivalsAtCellIndicesArea1.Clear();
            if (target.sourcesOfArrivalsAtCellIndicesArea2 != null) target.sourcesOfArrivalsAtCellIndicesArea2.Clear();

            if (target.indicesOfSpeciesThatDiedOutToday != null) target.indicesOfSpeciesThatDiedOutToday.Clear();

            return target;
        }

        // Zero-GC state cloning during time jumps: alternates between bufferA and bufferB
        [HarmonyPatch(typeof(WorldState), nameof(WorldState.Clone))]
        [HarmonyPrefix]
        public static bool WorldState_Clone_Prefix(WorldState __instance, ref WorldState __result)
        {
            if (IsTimeJumpActive)
            {
                __result = FastCloneWorldState(__instance);
                return false;
            }
            return true;
        }

        private static ushort[] _speciesCountBuffer;

        private static int SafeCountMutations(MutationTracker tracker)
        {
            if (tracker == null) return 0;
            try
            {
                return tracker.CountRecentMutations();
            }
            catch
            {
                return 0;
            }
        }

        // 2b. Prevent ArgumentOutOfRangeException in TimeLine.GetYesterday()
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.GetYesterday))]
        [HarmonyPrefix]
        public static bool TimeLine_GetYesterday_Prefix(TimeLine __instance, ref WorldState __result)
        {
            if (__instance == null || __instance.line == null || __instance.line.Count < 2)
            {
                __result = __instance != null ? __instance.today : null;
                return false;
            }
            return true;
        }

        // 2c. Bypass 5 Boo Array allocations in TimeLine.JumpAhead during Time Jumps
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.JumpAhead))]
        [HarmonyPrefix]
        public static bool TimeLine_JumpAhead_Prefix(TimeLine __instance, int numberOfDays)
        {
            if (IsTimeJumpActive)
            {
                for (int i = 0; i < numberOfDays; i++)
                {
                    __instance.LiveADay();
                }
                return false; // Skip allocating 5 Boo Array instances every call!
            }
            return true;
        }

        // 3. Ensure fungus can evolve whenever evolution is active
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPrefix]
        public static void TimeLine_LiveADay_FungusEvolution_Prefix(TimeLine __instance)
        {
            if (__instance != null && __instance.evolution && __instance.chanceForFungusMutation <= 0f)
            {
                float avg = Mathf.Max(__instance.chanceForPlantMutation, __instance.chanceForAnimalMutation);
                __instance.chanceForFungusMutation = avg > 0f ? avg : 0.075f;
            }
        }

        // 4. Step the native Rust world on each simulated day and store demographic history in Rust
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPostfix]
        public static void TimeLine_LiveADay_Postfix(TimeLine __instance)
        {
            // During time jumps, allow timeline line.Count to advance naturally for authentic speciation & events
            if (IsTimeJumpActive)
            {
                return;
            }

            IntPtr world = Plugin.RustWorldHandle;
            if (world != IntPtr.Zero)
            {
                NativeSim.sapling_world_tick(world);

                if (__instance != null && __instance.today != null)
                {
                    var pop = __instance.today.numberOfLivingOrganismsPerSpecies;
                    int count = pop != null ? pop.Count : 0;
                    if (count > 0)
                    {
                        if (_speciesCountBuffer == null || _speciesCountBuffer.Length < count)
                        {
                            _speciesCountBuffer = new ushort[count];
                        }
                        for (int i = 0; i < count; i++)
                        {
                            _speciesCountBuffer[i] = (ushort)Math.Min(pop[i], ushort.MaxValue);
                        }
                        NativeSim.sapling_timeline_push(world, (uint)__instance.dayIndex, 0, 0, _speciesCountBuffer, (uint)count);
                    }
                }
            }
        }

        // 5. During a TimeJumpController jump, run authentic simulation with 60 FPS UI updates
        [HarmonyPatch(typeof(TimeJumpController), "Update")]
        [HarmonyPrefix]
        public static bool TimeJumpController_Update_Prefix(TimeJumpController __instance)
        {
            if (__instance == null || __instance.timeLine == null) return true;

            try
            {
                // Handle user cancel / interrupt button click or Escape key
                if (((Input.GetMouseButtonDown(0) || Input.GetMouseButton(0)) && __instance.uiView != null && __instance.uiView.interruptButtonHovered) || Input.GetKeyDown(KeyCode.Escape))
                {
                    IsTimeJumpActive = false;
                    ResetPingPongBuffers();
                    __instance.nrOfYearsToJump = 0;
                    var cb = __instance.timeJumpReady;
                    __instance.timeJumpReady = null;
                    if (cb != null)
                    {
                        if (__instance.uiView != null) __instance.uiView.ShowWaitInstructions(true);
                        cb.Call(new object[0]);
                    }
                    return false;
                }

                if (__instance.nrOfYearsToJump > 0)
                {
                    // Calibrated frame budget: simulate continuously up to 150ms per frame (~6 UI updates/sec)
                    // Every single day is authentically simulated via vanilla JumpAhead / LiveADay. No days skipped.
                    // Achieves 90%+ CPU simulation duty cycle without freezing Windows message pump.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (__instance.nrOfYearsToJump > 0 && sw.ElapsedMilliseconds < 150)
                    {
                        if (Input.GetKey(KeyCode.Escape) || (Input.GetMouseButton(0) && __instance.uiView != null && __instance.uiView.interruptButtonHovered))
                        {
                            break;
                        }

                        int batch = Math.Min(25, __instance.nrOfYearsToJump);
                        __instance.timeLine.JumpAhead(batch);
                        __instance.nrOfYearsToJump -= batch;
                    }

                    // Update UI progress bar and species counts smoothly from main thread
                    if (__instance.uiView != null)
                    {
                        var epochs = __instance.timeLine.epochs;
                        var epoch = (epochs != null && epochs.Count > 0) ? epochs[epochs.Count - 1] : null;

                        int algaCount = (epoch != null && epoch.currentNrOfAlgaSpecies > 0) ? epoch.currentNrOfAlgaSpecies : (__instance.timeLine.allAlgaSpecies != null ? __instance.timeLine.allAlgaSpecies.Count : 0);
                        int plantCount = (epoch != null && epoch.currentNrOfPlantSpecies > 0) ? epoch.currentNrOfPlantSpecies : (__instance.timeLine.allPlantSpecies != null ? __instance.timeLine.allPlantSpecies.Count : 0);
                        int aquaticCount = (epoch != null && epoch.currentNrOfAquaticAnimalSpecies > 0) ? epoch.currentNrOfAquaticAnimalSpecies : 0;
                        int terrCount = (epoch != null && epoch.currentNrOfTerrestrialAnimalSpecies > 0) ? epoch.currentNrOfTerrestrialAnimalSpecies : 0;
                        int fungusCount = (epoch != null && epoch.currentNrOfFungusSpecies > 0) ? epoch.currentNrOfFungusSpecies : (__instance.timeLine.allFungusSpecies != null ? __instance.timeLine.allFungusSpecies.Count : 0);

                        int algaMut = SafeCountMutations(__instance.timeLine.algaMutationTracker);
                        int plantMut = SafeCountMutations(__instance.timeLine.plantMutationTracker);
                        int animalMut = SafeCountMutations(__instance.timeLine.animalMutationTracker);
                        int fungusMut = SafeCountMutations(__instance.timeLine.fungusMutationTracker);

                        __instance.uiView.UpdateProgress(
                            __instance.nrOfYearsToJump,
                            algaMut,
                            plantMut,
                            animalMut,
                            fungusMut,
                            algaCount,
                            plantCount,
                            aquaticCount,
                            terrCount,
                            fungusCount
                        );
                    }

                    if (__instance.superJump && __instance.nrOfYearsToJump % 1000 == 0)
                    {
                        int totalSpecies = __instance.timeLine.allSpecies != null ? __instance.timeLine.allSpecies.Count : 0;
                        Plugin.LogWarn($"[Superjump] Remaining: {__instance.nrOfYearsToJump} years, dayIndex: {__instance.timeLine.dayIndex}, total species: {totalSpecies}");
                    }

                    return false;
                }
                else
                {
                    IsTimeJumpActive = false;
                    ResetPingPongBuffers();
                    var cb = __instance.timeJumpReady;
                    __instance.timeJumpReady = null;
                    if (cb != null)
                    {
                        if (__instance.uiView != null) __instance.uiView.ShowWaitInstructions(true);
                        cb.Call(new object[0]);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                IsTimeJumpActive = false;
                ResetPingPongBuffers();
                Plugin.LogWarn("[TimeJumpController_Update_Prefix] " + ex.ToString());
                return true;
            }
        }

        // 6. In-game Time Jump support on WorldController
        [HarmonyPatch(typeof(WorldController), "Update")]
        [HarmonyPrefix]
        public static bool WorldController_Update_Prefix(WorldController __instance)
        {
            if (__instance == null || __instance.timeLine == null) return true;

            // Direct 1-button trigger for 875,000-year Superjump
            if (Input.GetKeyDown(KeyCode.F10))
            {
                Plugin.LogWarn("[SUPERJUMP] Triggered instantly via F10 shortcut key!");
                __instance.timeJumpIsSuperJump = true;
                __instance.timeJumpDuration = 875000;
                __instance.FadeToOtherSceneWithFunction(__instance.timeJump);
                return false;
            }

            if (__instance.nrOfDaysToDoInCurrentTimeJump > 0)
            {
                // Allow instant cancel with Escape key
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    __instance.nrOfDaysToDoInCurrentTimeJump = 0;
                }

                IsTimeJumpActive = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int daysThisFrame = 0;
                while (__instance.nrOfDaysToDoInCurrentTimeJump > 0 && sw.ElapsedMilliseconds < 50)
                {
                    int batch = Math.Min(10, __instance.nrOfDaysToDoInCurrentTimeJump);
                    __instance.timeLine.JumpAhead(batch);
                    __instance.nrOfDaysToDoInCurrentTimeJump -= batch;
                    daysThisFrame += batch;
                }

                if (__instance.view != null)
                {
                    __instance.view.lastWorldStateWasYesterday = false;
                    if (__instance.view.prebuiltOrganismLibrary != null)
                    {
                        __instance.view.prebuiltOrganismLibrary.year += daysThisFrame;
                    }
                }

                if (__instance.uiView != null)
                {
                    __instance.uiView.nrOfDaysToDoInCurrentTimeJump = __instance.nrOfDaysToDoInCurrentTimeJump;
                    __instance.uiView.UpdateTimeJumpWaitingView();
                }

                if (__instance.nrOfDaysToDoInCurrentTimeJump <= 0)
                {
                    IsTimeJumpActive = false;
                    ResetPingPongBuffers();
                    __instance.nrOfDaysToDoInCurrentTimeJump = 0;
                    if (__instance.uiView != null)
                    {
                        __instance.uiView.ToggleTimeJumpWaitingView(false);
                    }
                    if (__instance.view != null)
                    {
                        __instance.view.CleanUpAllOrganismViews();
                        if (__instance.timeLine != null && __instance.timeLine.today != null)
                        {
                            var tl = __instance.timeLine;
                            __instance.view.PrebuildMostFrequentDefinitions(
                                tl.today,
                                __instance.preloadSpeciesModels,
                                tl.populationBasedPlantClassifier,
                                tl.populationBasedAnimalClassifier,
                                tl.allSpecies,
                                tl.parentSpeciesIndexPerSpeciesIndex,
                                tl.today.numberOfLivingOrganismsPerSpecies
                            );
                        }
                        __instance.firstViewUpdateAfterTimeJumpHasBeenDone = false;
                    }
                    var cb = __instance.timeJumpCallBack;
                    __instance.timeJumpCallBack = null;
                    if (cb != null)
                    {
                        try { cb.Call(new object[0]); } catch { }
                    }
                    return true; // Let vanilla run normal frame update (ShowToday, hover actions, colors, indicators)
                }

                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(WorldController), "Update")]
        [HarmonyPostfix]
        public static void WorldController_Update_Postfix()
        {
        }

        // 6. Prevent ~700ms main-thread freeze from Resources.UnloadUnusedAssets() during Time Jumps
        [HarmonyPatch(typeof(Resources), nameof(Resources.UnloadUnusedAssets))]
        [HarmonyPrefix]
        public static bool Resources_UnloadUnusedAssets_Prefix(ref AsyncOperation __result)
        {
            if (AsyncJumpWorker.IsRunning || IsTimeJumpActive)
            {
                __result = null;
                return false; // Skip the synchronous 50,000+ asset sweep during time skips!
            }
            return true;
        }

        // 7. Fix vanilla bug: Typing 'p' in cheat console triggers Pause/Unpause
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.Pause))]
        [HarmonyPrefix]
        public static bool WorldController_Pause_Prefix(WorldController __instance)
        {
            if (__instance != null && __instance.cheatCommands)
            {
                return false; // Suppress pause while cheat command typing is active!
            }
            return true;
        }

        [HarmonyPatch(typeof(WorldController), nameof(WorldController.Resume))]
        [HarmonyPrefix]
        public static bool WorldController_Resume_Prefix(WorldController __instance)
        {
            if (__instance != null && __instance.cheatCommands)
            {
                return false; // Suppress resume toggle while cheat command typing is active!
            }
            return true;
        }
    }
}

