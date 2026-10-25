using System;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Prevents the 10-32 GB RAM balloon by capping unbounded historical timeline states
    /// and periodically compacting the Mono GC heap during extended simulation runs.
    /// </summary>
    [HarmonyPatch]
    public static class MemoryPatches
    {
        private const int MaxHistoryDays = 300;
        private static int _daysSinceLastGC = 0;

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPostfix]
        public static void TimeLine_LiveADay_Postfix(TimeLine __instance)
        {
            if (__instance == null || __instance.line == null) return;

            // Trim the unbounded historical line list to prevent leaking thousands of WorldState clones
            if (__instance.line.Count > MaxHistoryDays + 50)
            {
                int itemsToRemove = __instance.line.Count - MaxHistoryDays;
                for (int i = 0; i < itemsToRemove; i++)
                {
                    var oldState = __instance.line[0];
                    if (oldState != null)
                    {
                        // Help GC decouple circular or array references
                        oldState.areas = null;
                        oldState.sounds = null;
                        oldState.echos = null;
                    }
                    __instance.line.RemoveAt(0);
                }
            }

            // Periodic GC compaction every 200 simulated days to keep Mono heap tight
            _daysSinceLastGC++;
            if (_daysSinceLastGC >= 200)
            {
                _daysSinceLastGC = 0;
                GC.Collect();
            }
        }
    }
}
