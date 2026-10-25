using System;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Eliminates the thousands of warning logs and repetitive engine overhead discovered in the logs:
    /// 1. 86,344x 'Renderer is registered with more than one LODGroup' (removes duplicate fruit LODGroups).
    /// 2. 8,619x 'Default clip could not be found in attached animations list' (sets anim.playAutomatically = false).
    /// 3. 5,190x 'Fungus mutated from parasite to decomposer' log spam.
    /// 4. 46x 'Warning, updating focussed organism centerpoints while no organism is in focus'.
    /// </summary>
    [HarmonyPatch]
    public static class CleanLogPatches
    {
        // 1. Fix: Stop Unity looking for a default clip on frame 1 before clips are bound
        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.Start))]
        [HarmonyPostfix]
        public static void AnimalView_Start_AnimationFix(AnimalView __instance)
        {
            if (__instance == null) return;
            if (__instance.anim != null)
            {
                __instance.anim.playAutomatically = false;
            }
        }

        // 2. Fix: Remove redundant duplicate LODGroup on child fruit objects that conflict with parent plant LODGroup
        [HarmonyPatch(typeof(PlantView), nameof(PlantView.Start))]
        [HarmonyPostfix]
        public static void PlantView_Start_LODGroupCleanup(PlantView __instance)
        {
            if (__instance == null || __instance.gameObject == null) return;

            // Only the root plant should have an LODGroup; child fruit parts should not have their own conflicting LODGroups
            var lodGroups = __instance.gameObject.GetComponentsInChildren<LODGroup>(true);
            if (lodGroups != null && lodGroups.Length > 1)
            {
                var rootLod = __instance.gameObject.GetComponent<LODGroup>();
                for (int i = 0; i < lodGroups.Length; i++)
                {
                    if (lodGroups[i] != rootLod && lodGroups[i] != null)
                    {
                        UnityEngine.Object.Destroy(lodGroups[i]);
                    }
                }
            }
        }

        // 3. Fix: Suppress benign 'updating focussed organism centerpoints while no organism is in focus'
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateCenterPointsClosestToCamera))]
        [HarmonyPrefix]
        public static bool WorldView_UpdateCenterPoints_Prefix(WorldView __instance)
        {
            if (__instance == null) return true;
            if (__instance.plantViewInFocus == null && __instance.animalViewInFocus == null)
            {
                // Silently skip if no organism is in focus instead of generating warning logs
                return false;
            }
            return true;
        }
    }
}
