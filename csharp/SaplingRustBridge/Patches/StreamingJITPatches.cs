using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Smooth, hitch-free organism streaming and spawning.
    /// Fixes two critical engine bottlenecks:
    /// 1. Disables strict crucial profile parameter checks in PrebuiltOrganismLibrary so mutant
    ///    creatures and plants reuse the prebuilt species 3D model (taking < 1ms via Instantiate)
    ///    instead of freezing the engine for 350ms-2700ms building meshes from scratch on the main thread.
    /// </summary>
    [HarmonyPatch]
    public static class StreamingJITPatches
    {
        // 1. Enable species-based model reuse in PrebuiltOrganismLibrary:
        // Allows mutant creatures to share the prebuilt 3D model of their species in < 1ms,
        // eliminating the 14-second freezes where the game built 14+ models synchronously.
        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_ConfigureLibraries(WorldView __instance)
        {
            if (__instance == null) return;

            if (__instance.prebuiltAnimalLibrary != null)
            {
                __instance.prebuiltAnimalLibrary.profileParameter0IsCrucial = false;
                __instance.prebuiltAnimalLibrary.profileParameter1IsCrucial = false;
                __instance.prebuiltAnimalLibrary.profileParameter2IsCrucial = false;
                __instance.prebuiltAnimalLibrary.profileParameter3IsCrucial = false;
            }

            if (__instance.prebuiltPlantLibrary != null)
            {
                __instance.prebuiltPlantLibrary.profileParameter0IsCrucial = false;
                __instance.prebuiltPlantLibrary.profileParameter1IsCrucial = false;
                __instance.prebuiltPlantLibrary.profileParameter2IsCrucial = false;
                __instance.prebuiltPlantLibrary.profileParameter3IsCrucial = false;
            }

            // Generous thresholds so models spawn reliably and remain visible
            __instance.algaJITOptimizationThreshold = 20;
            __instance.plantJITOptimizationThreshold = 25;
            __instance.animalJITOptimizationThreshold = 20;

            // Catmull-Clark subdivision iterations: exactly 1 iteration (vanilla requirement for rigs and bone weights)
            __instance.builderSubdividerIterations = 1;
        }

        // 4. Ensure AnimalObjectBuilder runs with at least 1 subdivision iteration
        // so CatmullClarkSubdivider is non-null for AnimalArmatureBuilder.RecalculateBoneWeightsForSubdividedMesh
        [HarmonyPatch(typeof(AnimalObjectBuilder), nameof(AnimalObjectBuilder.Run))]
        [HarmonyPrefix]
        public static void AnimalObjectBuilder_Run_Prefix(AnimalObjectBuilder __instance)
        {
            if (__instance != null && __instance.subdividerIterations < 1)
            {
                __instance.subdividerIterations = 1;
            }
        }

        [HarmonyPatch(typeof(AnimalObjectBuilder), nameof(AnimalObjectBuilder.Run))]
        [HarmonyPostfix]
        public static void AnimalObjectBuilder_Run_Postfix(GameObject __result)
        {
            if (__result != null)
            {
                AnimalInfo info = __result.GetComponent<AnimalInfo>();
                if (info != null)
                {
                    info.hangingBabyFace = null;
                }
            }
        }

        // 5. Ensure PlantObjectBuilder has at least 1 subdivision iteration
        [HarmonyPatch(typeof(PlantObjectBuilder), nameof(PlantObjectBuilder.Run))]
        [HarmonyPrefix]
        public static void PlantObjectBuilder_Run_Prefix(PlantObjectBuilder __instance)
        {
            if (__instance != null && __instance.subdividerIterations < 1)
            {
                __instance.subdividerIterations = 1;
            }
        }

        // 6. Thread-safe MutationTracker to eliminate cross-thread collection modification exceptions
        [HarmonyPatch(typeof(MutationTracker), nameof(MutationTracker.CountRecentMutations))]
        [HarmonyPrefix]
        public static bool MutationTracker_CountRecentMutations_Prefix(MutationTracker __instance, ref int __result)
        {
            if (__instance == null) { __result = 0; return false; }
            lock (__instance)
            {
                try
                {
                    var queue = Traverse.Create(__instance).Field<Queue<int>>("mutationHistory").Value;
                    if (queue != null)
                    {
                        int sum = 0;
                        foreach (int item in queue.ToArray()) sum += item;
                        __result = sum;
                        return false;
                    }
                }
                catch { __result = 0; return false; }
            }
            return true;
        }

        [HarmonyPatch(typeof(MutationTracker), nameof(MutationTracker.EndOfDay))]
        [HarmonyPrefix]
        public static void MutationTracker_EndOfDay_Prefix(MutationTracker __instance)
        {
            System.Threading.Monitor.Enter(__instance);
        }

        [HarmonyPatch(typeof(MutationTracker), nameof(MutationTracker.EndOfDay))]
        [HarmonyPostfix]
        public static void MutationTracker_EndOfDay_Postfix(MutationTracker __instance)
        {
            System.Threading.Monitor.Exit(__instance);
        }
    }
}
