using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Eliminates the 200-600ms hitch/freeze when entering crowded areas with genetic mutations.
    /// 
    /// Root cause:
    /// In vanilla, PrebuiltOrganismLibrary.ProfilesMatchOnCrucialParameters rejects cached species
    /// blueprints whenever an individual organism has minor genetic drift (different limb/eye indices).
    /// When rejected, WorldView halts the main thread for 300-600ms per organism to run
    /// AnimalObjectBuilder procedural mesh subdivision, rigging, and texture synthesis from scratch.
    /// 
    /// Solution:
    /// 1. Share species blueprints across all genetic drift variants of that species.
    ///    Object.Instantiate() takes 0.1ms vs 400ms for procedural generation (4,000x faster).
    /// 2. If an exact mutation profile isn't cached, instantly fall back to the species representative model.
    /// 3. When a new species model is built, always mark it representative so all sibling organisms reuse it.
    /// </summary>
    [HarmonyPatch]
    public static class ModelSharingPatches
    {
        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.ProfilesMatchOnCrucialParameters))]
        [HarmonyPrefix]
        public static bool ProfilesMatchOnCrucialParameters_Prefix(ref bool __result)
        {
            // All organisms of the same species share the prebuilt 3D model
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.GetPrebuiltOrganismProfileBased))]
        [HarmonyPostfix]
        public static void GetPrebuiltOrganismProfileBased_Postfix(
            PrebuiltOrganismLibrary __instance,
            ref GameObject __result,
            int definitionIndex,
            int sexIndex,
            int speciesIndex,
            LibraryProfile profile,
            bool youngVersion)
        {
            if (__result != null || __instance == null) return;

            // Robust fallback: If exact sex/profile match failed, check any model for this species
            var dict = youngVersion
                ? AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsYoungVersionForSpeciesIndex")?.GetValue(__instance) as Dictionary<SpeciesAndSexIndex, GameObject>
                : AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsOldVersionForSpeciesIndex")?.GetValue(__instance) as Dictionary<SpeciesAndSexIndex, GameObject>;

            if (dict != null)
            {
                var key0 = new SpeciesAndSexIndex(speciesIndex, 0);
                if (dict.TryGetValue(key0, out var go0) && go0 != null)
                {
                    if (!go0.activeSelf) go0.SetActive(true);
                    __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                    __result = Object.Instantiate(go0);
                    return;
                }

                var key1 = new SpeciesAndSexIndex(speciesIndex, 1);
                if (dict.TryGetValue(key1, out var go1) && go1 != null)
                {
                    if (!go1.activeSelf) go1.SetActive(true);
                    __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                    __result = Object.Instantiate(go1);
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPrefix]
        public static void BuildAndCacheAnimal_Prefix(ref bool representativeForSpecies)
        {
            // When building an animal model, always cache it for the entire species
            representativeForSpecies = true;
        }

        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_ModelSharing(WorldView __instance)
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
        }
    }
}
