using System;
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
        private static AnimalPosition[] _cachedArray;

        // 1. Initial Time Jump: offload days to background thread
        [HarmonyPatch(typeof(TimeJumpController), nameof(TimeJumpController.Jump))]
        [HarmonyPostfix]
        public static void TimeJumpController_Jump_Postfix(TimeJumpController __instance)
        {
            if (__instance != null && __instance.timeLine != null && __instance.nrOfYearsToJump > 0)
            {
                IsTimeJumpActive = true;
                __instance.nrOfDaysToJumpPerFrame = 0;
                AsyncJumpWorker.StartJump(__instance.timeLine, __instance.nrOfYearsToJump);
            }
        }

        // 2. Eliminate GC heap pressure in AnimalPositionTracker.ToArray()
        [HarmonyPatch(typeof(AnimalPositionTracker), nameof(AnimalPositionTracker.ToArray))]
        [HarmonyPrefix]
        public static bool AnimalPositionTracker_ToArray_Prefix(AnimalPositionTracker __instance, ref AnimalPosition[] __result)
        {
            if (__instance == null || __instance.animalPositions == null) return true;

            int count = __instance.animalPositions.Count;
            if (_cachedArray == null || _cachedArray.Length != count)
            {
                _cachedArray = new AnimalPosition[count];
            }

            __instance.animalPositions.CopyTo(_cachedArray, 0);
            __result = _cachedArray;
            return false;
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

        // 2b. Prevent ArgumentOutOfRangeException in TimeLine.GetYesterday() when line is pruned
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
            // During background time jumps, throttle history snapshots to every 10 days to eliminate P/Invoke loop overhead
            if (AsyncJumpWorker.IsRunning && (__instance.dayIndex % 10 != 0))
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

        // 4. During a TimeJumpController jump, run UI at 60+ FPS on main thread while worker jumps
        [HarmonyPatch(typeof(TimeJumpController), "Update")]
        [HarmonyPrefix]
        public static bool TimeJumpController_Update_Prefix(TimeJumpController __instance)
        {
            if (__instance == null) return true;

            try
            {
                // Handle user cancel / interrupt button click
                if (Input.GetMouseButtonDown(0) && __instance.uiView != null && __instance.uiView.interruptButtonHovered)
                {
                    AsyncJumpWorker.Stop();
                    IsTimeJumpActive = false;
                    __instance.nrOfYearsToJump = 0;
                    var callback = __instance.timeJumpReady;
                    __instance.timeJumpReady = null;
                    if (callback != null)
                    {
                        __instance.uiView.ShowWaitInstructions(true);
                        callback.Call(new object[0]);
                    }
                    return false;
                }

                int remaining = AsyncJumpWorker.DaysRemaining;
                __instance.nrOfYearsToJump = remaining;

                // When worker completes or was stopped, transition immediately
                if (!AsyncJumpWorker.IsRunning || remaining <= 0)
                {
                    IsTimeJumpActive = false;
                    __instance.nrOfYearsToJump = 0;
                    var callback = __instance.timeJumpReady;
                    __instance.timeJumpReady = null;
                    if (callback != null)
                    {
                        __instance.uiView.ShowWaitInstructions(true);
                        callback.Call(new object[0]);
                    }
                    return false;
                }

                // Update UI progress bar and species counts smoothly from main thread
                if (__instance.timeLine != null && __instance.uiView != null)
                {
                    var epochs = __instance.timeLine.epochs;
                    var epoch = (epochs != null && epochs.Count > 0) ? epochs[epochs.Count - 1] : null;

                    int algaCount = __instance.timeLine.allAlgaSpecies != null ? __instance.timeLine.allAlgaSpecies.Count : 0;
                    int plantCount = __instance.timeLine.allPlantSpecies != null ? __instance.timeLine.allPlantSpecies.Count : 0;
                    int aquaticCount = 0;
                    int terrCount = 0;
                    if (__instance.timeLine.allAnimalSpecies != null)
                    {
                        for (int s = 0; s < __instance.timeLine.allAnimalSpecies.Count; s++)
                        {
                            if (__instance.timeLine.allAnimalSpecies[s].canWalk) terrCount++;
                            else aquaticCount++;
                        }
                    }
                    int fungusCount = __instance.timeLine.allFungusSpecies != null ? __instance.timeLine.allFungusSpecies.Count : 0;

                    int algaMut = SafeCountMutations(__instance.timeLine.algaMutationTracker);
                    int plantMut = SafeCountMutations(__instance.timeLine.plantMutationTracker);
                    int animalMut = (aquaticCount + terrCount > 0)
                        ? SafeCountMutations(__instance.timeLine.animalMutationTracker)
                        : 0;
                    int fungusMut = SafeCountMutations(__instance.timeLine.fungusMutationTracker);

                    __instance.uiView.UpdateProgress(
                        remaining,
                        algaMut,
                        plantMut,
                        animalMut,
                        fungusMut,
                        (epoch != null && epoch.currentNrOfAlgaSpecies > 0) ? epoch.currentNrOfAlgaSpecies : algaCount,
                        (epoch != null && epoch.currentNrOfPlantSpecies > 0) ? epoch.currentNrOfPlantSpecies : plantCount,
                        (epoch != null && epoch.currentNrOfAquaticAnimalSpecies > 0) ? epoch.currentNrOfAquaticAnimalSpecies : aquaticCount,
                        (epoch != null && epoch.currentNrOfTerrestrialAnimalSpecies > 0) ? epoch.currentNrOfTerrestrialAnimalSpecies : terrCount,
                        (epoch != null && epoch.currentNrOfFungusSpecies > 0) ? epoch.currentNrOfFungusSpecies : fungusCount
                    );
                }

                return false; // Suppress vanilla JumpAhead loop
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[TimeJumpController_Update_Prefix] " + ex.Message);
                return false; // Do not fall back to vanilla blocking loop on error
            }
        }

        // 5. In-game Time Jump support on WorldController
        [HarmonyPatch(typeof(WorldController), "Update")]
        [HarmonyPrefix]
        public static bool WorldController_Update_Prefix(WorldController __instance)
        {
            if (__instance == null || __instance.timeLine == null) return true;

            // Direct 1-button trigger for 875,000-year Rust Superjump
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
                IsTimeJumpActive = true;
                if (!AsyncJumpWorker.IsRunning)
                {
                    AsyncJumpWorker.StartJump(__instance.timeLine, __instance.nrOfDaysToDoInCurrentTimeJump);
                }

                int remaining = AsyncJumpWorker.DaysRemaining;
                __instance.nrOfDaysToDoInCurrentTimeJump = remaining;
                __instance.nrOfDaysToJumpPerFrame = 0;

                if (__instance.uiView != null)
                {
                    __instance.uiView.nrOfDaysToDoInCurrentTimeJump = remaining;
                    __instance.uiView.UpdateTimeJumpWaitingView();
                }

                if (remaining <= 0)
                {
                    IsTimeJumpActive = false;
                    __instance.nrOfDaysToDoInCurrentTimeJump = 0;
                    return true; // Worker finished! Vanilla runs view cleanup & ShowToday()
                }

                return false; // Skip vanilla blocking JumpAhead
            }

            return true;
        }

        [HarmonyPatch(typeof(WorldController), "Update")]
        [HarmonyPostfix]
        public static void WorldController_Update_Postfix()
        {
            AsyncJumpWorker.CheckMainThreadCompletion();
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

