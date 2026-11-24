using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Smooth, hitch-free organism streaming and spawning.
    /// Disables strict crucial profile parameter checks in PrebuiltOrganismLibrary so mutant
    /// creatures and plants reuse the prebuilt species 3D model (taking < 1ms via Instantiate)
    /// instead of freezing the engine for 350ms-2700ms building meshes from scratch on the main thread.
    /// Also sanitises AnimalInfo to avoid a cyclic-graph serialization crash and makes
    /// MutationTracker thread-safe.
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
                // Reuse the species' prebuilt 3D model for mutated individuals instead of
                // synchronously running Catmull-Clark + armature builds (400-800ms + ~150MB GC each)
                // whenever a mutant's limbs differ slightly from its species example.
                // Brand-new species still build their own unique 3D model cleanly.
                __instance.prebuiltAnimalLibrary.profileParameter0IsCrucial = false; // mouth
                __instance.prebuiltAnimalLibrary.profileParameter1IsCrucial = false; // front limbs
                __instance.prebuiltAnimalLibrary.profileParameter2IsCrucial = false; // back limbs
                __instance.prebuiltAnimalLibrary.profileParameter3IsCrucial = false; // accessories
            }


            // Restore vanilla JIT optimization thresholds (vanilla is 10, 15, 10)
            // Higher thresholds prevent billboard proxies and inflate memory with 600k+ active GameObjects
            __instance.algaJITOptimizationThreshold = 10;
            __instance.plantJITOptimizationThreshold = 15;
            __instance.animalJITOptimizationThreshold = 10;

            // Catmull-Clark subdivision iterations: exactly 1 iteration (vanilla requirement for rigs and bone weights)
            __instance.builderSubdividerIterations = 1;
        }

        // 2. Prevent Unity from spamming 'Default clip could not be found in attached animations list'
        // Unity defaults Animation.playAutomatically = true, which throws every frame if clip is null
        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.AddAnimationsToGameObject))]
        [HarmonyPostfix]
        public static void AnimalView_AddAnimationsToGameObject_FixClip(GameObject gameObject)
        {
            if (gameObject == null) return;
            var anim = gameObject.GetComponent<Animation>();
            if (anim != null)
            {
                anim.playAutomatically = false;
                anim.cullingType = AnimationCullingType.BasedOnRenderers;
            }
        }

        // 3. Prevent cyclic graph serialization crash in AnimalInfo.hangingBabyFace
        public static void SanitizeAnimalInfo(GameObject go)
        {
            if (go == null) return;
            AnimalInfo info = go.GetComponent<AnimalInfo>();
            if (info != null)
            {
                info.hangingBabyFace = null;
            }
        }

        // 4. AnimalObjectBuilder runs with 1 subdivision iteration for CatmullClarkSubdivider


        // 5. Ensure AnimalObjectBuilder runs with at least 1 subdivision iteration
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
                SanitizeAnimalInfo(__result);

                // Start frustum-culled so pooled and distant rigs are not skinned offscreen;
                // LODPatches.ApplyAnimalLOD promotes Close animals when in camera range.
                var smrs = __result.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < smrs.Length; i++)
                {
                    smrs[i].updateWhenOffscreen = false;
                }
                var anims = __result.GetComponentsInChildren<Animation>(true);
                for (int i = 0; i < anims.Length; i++)
                {
                    anims[i].playAutomatically = false;
                    anims[i].cullingType = AnimationCullingType.BasedOnRenderers;
                }
            }
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.BuildAnimalGameObject))]
        [HarmonyPostfix]
        public static void AnimalView_BuildAnimalGameObject_Postfix(GameObject __result)
        {
            if (__result != null)
            {
                SanitizeAnimalInfo(__result);
            }
        }

        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.CacheOrganismProfileBased))]
        [HarmonyPrefix]
        public static void PrebuiltOrganismLibrary_CacheOrganismProfileBased_Prefix(GameObject gameObject)
        {
            if (gameObject != null)
            {
                SanitizeAnimalInfo(gameObject);
            }
        }

        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.CacheOrganism), new Type[] { typeof(int), typeof(int), typeof(bool), typeof(GameObject) })]
        [HarmonyPrefix]
        public static void PrebuiltOrganismLibrary_CacheOrganism_Prefix(GameObject gameObject)
        {
            if (gameObject != null)
            {
                SanitizeAnimalInfo(gameObject);
            }
        }

        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.GetPrebuiltOrganismProfileBased))]
        [HarmonyPostfix]
        public static void PrebuiltOrganismLibrary_GetPrebuiltOrganismProfileBased_Postfix(
            PrebuiltOrganismLibrary __instance,
            int definitionIndex,
            int sexIndex,
            int speciesIndex,
            LibraryProfile profile,
            bool youngVersion,
            ref GameObject __result)
        {
            // Root fix for 10+ second/day freezes & 100 MB/day RAM leaks on multi-sex species:
            // WorldView.ShowAnimalViewsForArea caches speciesDefinition.exampleOrganism (currentSex == 0)
            // under (speciesIndex, 0). Animals with currentSex > 0 then miss (speciesIndex, sexIndex)
            // on every single day, causing vanilla to rebuild exampleOrganism twice per individual forever.
            // If no sex-specific model exists yet, fall back to the already-cached sex 0 model.
            if (__result == null && sexIndex != 0 && __instance != null)
            {
                __result = __instance.GetPrebuiltOrganismProfileBased(definitionIndex, 0, speciesIndex, profile, youngVersion);
            }
            if (__result != null)
            {
                SanitizeAnimalInfo(__result);
            }
        }


        private static readonly AccessTools.FieldRef<MutationTracker, Queue<int>> MutationHistoryRef =
            AccessTools.FieldRefAccess<MutationTracker, Queue<int>>("mutationHistory");

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
                    var queue = MutationHistoryRef(__instance);
                    if (queue != null)
                    {
                        int sum = 0;
                        foreach (int item in queue) sum += item;
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
        public static void MutationTracker_EndOfDay_Prefix(MutationTracker __instance, ref bool __state)
        {
            if (__instance != null)
            {
                System.Threading.Monitor.Enter(__instance);
                __state = true;
            }
        }

        [HarmonyPatch(typeof(MutationTracker), nameof(MutationTracker.EndOfDay))]
        [HarmonyFinalizer]
        public static Exception MutationTracker_EndOfDay_Finalizer(MutationTracker __instance, bool __state, Exception __exception)
        {
            if (__state && __instance != null)
            {
                System.Threading.Monitor.Exit(__instance);
            }
            return __exception;
        }
    }
}
