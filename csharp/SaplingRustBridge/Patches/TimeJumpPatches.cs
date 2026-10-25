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
    /// </summary>
    [HarmonyPatch]
    public static class TimeJumpPatches
    {
        private static AnimalPosition[] _cachedArray;

        // 1. Boost Time Jump speed: run 50 days per frame instead of 10
        [HarmonyPatch(typeof(TimeJumpController), nameof(TimeJumpController.Jump))]
        [HarmonyPostfix]
        public static void TimeJumpController_Jump_Postfix(TimeJumpController __instance)
        {
            if (__instance != null)
            {
                __instance.nrOfDaysToJumpPerFrame = 50;
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
    }
}
