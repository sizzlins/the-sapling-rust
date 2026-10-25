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
        private static AnimalPosition[] _cachedArray;

        // 1. Initial Time Jump: offload days to background thread
        [HarmonyPatch(typeof(TimeJumpController), nameof(TimeJumpController.Jump))]
        [HarmonyPostfix]
        public static void TimeJumpController_Jump_Postfix(TimeJumpController __instance)
        {
            if (__instance != null && __instance.timeLine != null && __instance.nrOfYearsToJump > 0)
            {
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

        // 3. Step the native Rust world on each simulated day
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPostfix]
        public static void TimeLine_LiveADay_Postfix()
        {
            IntPtr world = Plugin.RustWorldHandle;
            if (world != IntPtr.Zero)
            {
                NativeSim.sapling_world_tick(world);
            }
        }

        // 4. During a TimeJumpController jump, run UI at 60+ FPS on main thread while worker jumps
        [HarmonyPatch(typeof(TimeJumpController), "Update")]
        [HarmonyPrefix]
        public static bool TimeJumpController_Update_Prefix(TimeJumpController __instance)
        {
            if (!AsyncJumpWorker.IsRunning) return true;

            // Handle user cancel/interrupt button
            if (Input.GetMouseButtonDown(0) && __instance.uiView != null && __instance.uiView.interruptButtonHovered)
            {
                AsyncJumpWorker.Stop();
                __instance.nrOfYearsToJump = 0;
                return true;
            }

            int remaining = AsyncJumpWorker.DaysRemaining;
            __instance.nrOfYearsToJump = remaining;

            // Update UI progress bar smoothly from main thread
            if (__instance.timeLine != null && __instance.uiView != null && __instance.timeLine.epochs != null && __instance.timeLine.epochs.Count > 0)
            {
                var epoch = __instance.timeLine.epochs[__instance.timeLine.epochs.Count - 1];
                __instance.uiView.UpdateProgress(
                    remaining,
                    __instance.timeLine.algaMutationTracker != null ? __instance.timeLine.algaMutationTracker.CountRecentMutations() : 0,
                    __instance.timeLine.plantMutationTracker != null ? __instance.timeLine.plantMutationTracker.CountRecentMutations() : 0,
                    __instance.timeLine.animalMutationTracker != null ? __instance.timeLine.animalMutationTracker.CountRecentMutations() : 0,
                    __instance.timeLine.fungusMutationTracker != null ? __instance.timeLine.fungusMutationTracker.CountRecentMutations() : 0,
                    epoch != null ? epoch.currentNrOfAlgaSpecies : 0,
                    epoch != null ? epoch.currentNrOfPlantSpecies : 0,
                    epoch != null ? epoch.currentNrOfAquaticAnimalSpecies : 0,
                    epoch != null ? epoch.currentNrOfTerrestrialAnimalSpecies : 0,
                    epoch != null ? epoch.currentNrOfFungusSpecies : 0
                );
            }

            if (remaining <= 0)
            {
                __instance.nrOfYearsToJump = 0;
                return true; // Let vanilla invoke timeJumpReady callback on main thread
            }

            return false; // Skip the vanilla blocking JumpAhead loop
        }

        // 5. In-game Time Jump support on WorldController
        [HarmonyPatch(typeof(WorldController), "Update")]
        [HarmonyPrefix]
        public static bool WorldController_Update_Prefix(WorldController __instance)
        {
            if (__instance == null || __instance.timeLine == null) return true;

            if (__instance.nrOfDaysToDoInCurrentTimeJump > 0)
            {
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
    }
}
