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

        // Fast private field accessors for PrebuiltOrganismLibrary species blueprint caches
        // so GetPrebuiltOrganismProfileBased can reuse recycled GameObjects from OrganismObjectPool
        // keyed by the species blueprint's CopiedOrganism (definitionIndex, sexIndex).
        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>> SpeciesYoungDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>>("gameObjectsYoungVersionForSpeciesIndex");
        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>> SpeciesOldDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>>("gameObjectsOldVersionForSpeciesIndex");
        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<DefinitionAndSexIndex, GameObject>> DefYoungDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<DefinitionAndSexIndex, GameObject>>("gameObjectsYoungVersionForDefinitionIndex");
        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<DefinitionAndSexIndex, GameObject>> DefOldDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<DefinitionAndSexIndex, GameObject>>("gameObjectsOldVersionForDefinitionIndex");
        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<LibraryProfile, GameObject>> ProfileYoungDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<LibraryProfile, GameObject>>("gameObjectsYoungVersionForProfile");
        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<LibraryProfile, GameObject>> ProfileOldDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<LibraryProfile, GameObject>>("gameObjectsOldVersionForProfile");

        private static bool _inShowAnimalViews;
        private static bool _buildingExtraSexes;

        // WorldView.ShowAnimalViewsForArea (L4007, L4065) calls the 2-arg OrganismObjectPool.GetFullSpecificOrganism(def, young)
        // which hardcodes sexIndex = 0 and hands pooled female models to males (and vice versa).
        // Skip that 2-arg lookup during ShowAnimalViewsForArea so execution falls through to
        // GetPrebuiltOrganismProfileBased(..., currentSex, ...) which pools by the exact sexIndex.
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyPrefix]
        public static void WorldView_ShowAnimalViewsForArea_EnterSexGuard()
        {
            _inShowAnimalViews = true;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyFinalizer]
        public static Exception WorldView_ShowAnimalViewsForArea_ExitSexGuard(Exception __exception)
        {
            _inShowAnimalViews = false;
            return __exception;
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.GetFullSpecificOrganism), new Type[] { typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        public static bool OrganismObjectPool_GetFullSpecificOrganism2_SkipInAnimalViews(ref GameObject __result)
        {
            if (!_inShowAnimalViews) return true;
            __result = null;
            return false;
        }

        // Root fix for 1-2.5s/day ShowAnimalViewsForArea freezes & +1.7 GB native RAM churn:
        // WorldView.ShowAnimalViewsForArea looks up OrganismObjectPool by the individual mutant's
        // definitionIndex, which misses 99% of the time because blueprints stamp CopiedOrganism with
        // species.exampleOrganism.definitionIndex (or the shared profile blueprint's definitionIndex).
        // Every birth/grow-up then fell through to Object.Instantiate() while thousands of recycled
        // GameObjects piled up unused in the pool. Pop a recycled copy of the matched blueprint from
        // OrganismObjectPool before instantiating, and mark ProfileBased so ShowAnimalViewsForArea
        // applies the exact species color via our cached sharedMaterial RecolorAnimal.
        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.GetPrebuiltOrganismProfileBased))]
        [HarmonyPrefix]
        public static bool PrebuiltOrganismLibrary_GetPrebuiltOrganismProfileBased_Prefix(
            PrebuiltOrganismLibrary __instance,
            int definitionIndex,
            int sexIndex,
            int speciesIndex,
            LibraryProfile profile,
            bool youngVersion,
            ref GameObject __result)
        {
            WorldView wv = LODPatches.ActiveWorldView;
            if (__instance == null || wv == null || !object.ReferenceEquals(__instance, wv.prebuiltAnimalLibrary))
            {
                return true;
            }
            if (__instance.profileParameter0IsCrucial || __instance.profileParameter1IsCrucial ||
                __instance.profileParameter2IsCrucial || __instance.profileParameter3IsCrucial)
            {
                return true;
            }
            OrganismObjectPool pool = wv.organismObjectPool;

            GameObject blueprint = null;
            bool fromProfileFallback = false;

            Dictionary<SpeciesAndSexIndex, GameObject> sDict = youngVersion ? SpeciesYoungDictRef(__instance) : SpeciesOldDictRef(__instance);
            if (sDict != null)
            {
                sDict.TryGetValue(new SpeciesAndSexIndex(speciesIndex, sexIndex), out blueprint);
            }

            if (blueprint == null)
            {
                Dictionary<DefinitionAndSexIndex, GameObject> dDict = youngVersion ? DefYoungDictRef(__instance) : DefOldDictRef(__instance);
                if (dDict != null)
                {
                    dDict.TryGetValue(new DefinitionAndSexIndex(definitionIndex, sexIndex), out blueprint);
                }
            }

            if (blueprint == null && profile != null)
            {
                Dictionary<LibraryProfile, GameObject> pDict = youngVersion ? ProfileYoungDictRef(__instance) : ProfileOldDictRef(__instance);
                if (pDict != null && pDict.TryGetValue(profile, out blueprint) && blueprint != null)
                {
                    fromProfileFallback = true;
                }
            }

            if (blueprint == null) return true;

            CopiedOrganism co = blueprint.GetComponent<CopiedOrganism>();
            // Never serve a wrong-sex profile blueprint when this species hasn't built its own models yet;
            // return null so ShowAnimalViewsForArea calls BuildAndCacheAnimal (which builds all sexes).
            if (fromProfileFallback && sexIndex != 0 && co != null && co.sexIndex != sexIndex &&
                (sDict == null || !sDict.ContainsKey(new SpeciesAndSexIndex(speciesIndex, 0))))
            {
                __instance.lastUsedStorageApproach = StorageApproach.None;
                __result = null;
                return false;
            }

            if (pool != null && pool.enabled && co != null && co.sexIndex == sexIndex)
            {
                GameObject recycled = pool.GetFullSpecificOrganism(co.definitionIndex, co.sexIndex, youngVersion);
                if (recycled != null)
                {
                    // Use ProfileBased so ShowAnimalViewsForArea refreshes species colors (fast & leak-free
                    // via our cached sharedMaterial RecolorAnimal) even if this pooled copy previously served
                    // another species sharing the same LibraryProfile.
                    __instance.lastUsedStorageApproach = StorageApproach.ProfileBased;
                    SanitizeAnimalInfo(recycled);
                    __result = recycled;
                    return false;
                }
            }
            return true;
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
            // Fallback to sex 0 ONLY when (speciesIndex, 0) was already built (meaning exampleOrganism had
            // no sex > 0 morph to build). Multi-sex species build all sexes in BuildAndCacheAnimal_Postfix.
            if (__result == null && sexIndex != 0 && __instance != null)
            {
                Dictionary<SpeciesAndSexIndex, GameObject> sDict = youngVersion ? SpeciesYoungDictRef(__instance) : SpeciesOldDictRef(__instance);
                if (sDict != null && sDict.ContainsKey(new SpeciesAndSexIndex(speciesIndex, 0)))
                {
                    __result = __instance.GetPrebuiltOrganismProfileBased(definitionIndex, 0, speciesIndex, profile, youngVersion);
                }
            }
            if (__result != null)
            {
                SanitizeAnimalInfo(__result);
            }
        }

        // Fix vanilla PrebuiltOrganismLibrary.CacheOrganismProfileBased (L189-191) which sets
        // copiedOrganism.definitionIndex and youngVersion but leaves sexIndex at 0, causing
        // OrganismObjectPool to mix recycled male and female models in bucket (def, 0).
        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.CacheOrganismProfileBased))]
        [HarmonyPostfix]
        public static void PrebuiltOrganismLibrary_CacheOrganismProfileBased_FixSexIndex(
            int sexIndex,
            GameObject gameObject)
        {
            if (gameObject == null) return;
            CopiedOrganism co = gameObject.GetComponent<CopiedOrganism>();
            if (co != null)
            {
                co.sexIndex = sexIndex;
            }
        }

        // Fix vanilla WorldView.ShowAnimalViewsForArea (L4027, L4085) which only calls BuildAndCacheAnimal
        // for speciesDefinition.exampleOrganism (currentSex == 0), never caching sex 1..N models.
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPostfix]
        public static void WorldView_BuildAndCacheAnimal_BuildAllSexes(
            WorldView __instance,
            AnimalDefinition animalDefinition,
            bool youngVersion,
            int animalSpeciesIndex,
            LibraryProfile libraryProfile,
            bool representativeForSpecies)
        {
            if (_buildingExtraSexes || !representativeForSpecies || __instance == null ||
                __instance.prebuiltAnimalLibrary == null || animalDefinition == null ||
                animalDefinition.sexes == null || animalDefinition.sexes.Length == 0)
            {
                return;
            }

            _buildingExtraSexes = true;
            try
            {
                int totalSexes = animalDefinition.sexes.Length;
                for (int s = 0; s <= totalSexes; s++)
                {
                    if (s == animalDefinition.currentSex) continue;
                    if (__instance.prebuiltAnimalLibrary.HasSpeciesSpecificModel(
                        animalDefinition.definitionIndex, s, animalSpeciesIndex, youngVersion))
                    {
                        continue;
                    }
                    AnimalDefinition sexClone = animalDefinition.Clone() as AnimalDefinition;
                    if (sexClone == null) continue;
                    sexClone.currentSex = s;
                    __instance.BuildAndCacheAnimal(sexClone, youngVersion, animalSpeciesIndex, libraryProfile, true);
                }
            }
            finally
            {
                _buildingExtraSexes = false;
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
