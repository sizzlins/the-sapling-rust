using System;
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
    /// blueprints whenever an individual organism has minor genetic drift.
    /// Furthermore, if gameObjectOldVersion is null, AnimalView.CreateAndPrepareGameObject and
    /// PlantView.CreateAndPrepareGameObject run full procedural Catmull-Clark mesh generation
    /// synchronously on the main thread for every mutant creature.
    /// 
    /// Solution:
    /// 1. Share species blueprints across all genetic drift variants and sexes of that species.
    /// 2. Intercept AnimalView and PlantView CreateAndPrepareGameObject to instantly clone existing
    ///    species models, completely bypassing Catmull-Clark subdivision (0.1ms vs 400ms).
    /// </summary>
    [HarmonyPatch]
    public static class ModelSharingPatches
    {
        public static WorldView WorldViewInstance;

        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_ModelSharing(WorldView __instance)
        {
            if (__instance == null) return;
            WorldViewInstance = __instance;

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

        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.ProfilesMatchOnCrucialParameters))]
        [HarmonyPrefix]
        public static bool ProfilesMatchOnCrucialParameters_Prefix(ref bool __result)
        {
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

            var speciesDict = youngVersion
                ? AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsYoungVersionForSpeciesIndex")?.GetValue(__instance) as Dictionary<SpeciesAndSexIndex, GameObject>
                : AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsOldVersionForSpeciesIndex")?.GetValue(__instance) as Dictionary<SpeciesAndSexIndex, GameObject>;

            if (speciesDict != null)
            {
                foreach (var kvp in speciesDict)
                {
                    if (kvp.Key.speciesIndex == speciesIndex && kvp.Value != null)
                    {
                        if (!kvp.Value.activeSelf) kvp.Value.SetActive(true);
                        __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                        __result = UnityEngine.Object.Instantiate(kvp.Value);
                        return;
                    }
                }
            }

            var otherSpeciesDict = youngVersion
                ? AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsOldVersionForSpeciesIndex")?.GetValue(__instance) as Dictionary<SpeciesAndSexIndex, GameObject>
                : AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsYoungVersionForSpeciesIndex")?.GetValue(__instance) as Dictionary<SpeciesAndSexIndex, GameObject>;

            if (otherSpeciesDict != null)
            {
                foreach (var kvp in otherSpeciesDict)
                {
                    if (kvp.Key.speciesIndex == speciesIndex && kvp.Value != null)
                    {
                        if (!kvp.Value.activeSelf) kvp.Value.SetActive(true);
                        __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                        __result = UnityEngine.Object.Instantiate(kvp.Value);
                        return;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPrefix]
        public static void BuildAndCacheAnimal_Prefix(ref bool representativeForSpecies)
        {
            representativeForSpecies = true;
        }

        // Intercept procedural animal mesh subdivision: if an existing species model exists, clone it instantly!
        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.CreateAndPrepareGameObject))]
        [HarmonyPrefix]
        public static bool AnimalView_CreateAndPrepareGameObject_Prefix(
            AnimalView __instance,
            AnimalObjectBuilder builder,
            int builderSubdividerIterations,
            Vector3 location,
            bool young,
            ref GameObject __result)
        {
            if (__instance == null || __instance.definition == null || WorldViewInstance == null) return true;

            try
            {
                var lib = WorldViewInstance.prebuiltAnimalLibrary;
                if (lib != null && WorldViewInstance.getSpeciesIndexForDefinitionIndex != null)
                {
                    int defIdx = __instance.definition.definitionIndex;
                    int speciesIdx = Convert.ToInt32(WorldViewInstance.getSpeciesIndexForDefinitionIndex.Call(new object[] { defIdx, OrganismType.Animal }));

                    var dict = young
                        ? AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsYoungVersionForSpeciesIndex")?.GetValue(lib) as Dictionary<SpeciesAndSexIndex, GameObject>
                        : AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsOldVersionForSpeciesIndex")?.GetValue(lib) as Dictionary<SpeciesAndSexIndex, GameObject>;

                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (kvp.Key.speciesIndex == speciesIdx && kvp.Value != null)
                            {
                                if (!kvp.Value.activeSelf) kvp.Value.SetActive(true);
                                GameObject clone = UnityEngine.Object.Instantiate(kvp.Value);
                                clone.transform.localScale = __instance.GetModelScale(__instance.definition.bodySize, false);
                                clone.transform.position = location - __instance.GetHeightAboveGroundForGameObject(clone);
                                __result = clone;
                                return false; // Skip Catmull-Clark mesh subdivision!
                            }
                        }
                    }
                }
            }
            catch { }

            return true;
        }

        // Intercept procedural plant mesh subdivision: if an existing species model exists, clone it instantly!
        [HarmonyPatch(typeof(PlantView), nameof(PlantView.CreateAndPrepareGameObject))]
        [HarmonyPrefix]
        public static bool PlantView_CreateAndPrepareGameObject_Prefix(
            PlantView __instance,
            PlantObjectBuilder builder,
            int builderSubdividerIterations,
            Vector3 location,
            bool young,
            ref GameObject __result)
        {
            if (__instance == null || __instance.definition == null || WorldViewInstance == null) return true;

            try
            {
                var lib = WorldViewInstance.prebuiltPlantLibrary;
                if (lib != null && WorldViewInstance.getSpeciesIndexForDefinitionIndex != null)
                {
                    int defIdx = __instance.definition.definitionIndex;
                    int speciesIdx = Convert.ToInt32(WorldViewInstance.getSpeciesIndexForDefinitionIndex.Call(new object[] { defIdx, OrganismType.Plant }));

                    var dict = young
                        ? AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsYoungVersionForSpeciesIndex")?.GetValue(lib) as Dictionary<SpeciesAndSexIndex, GameObject>
                        : AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsOldVersionForSpeciesIndex")?.GetValue(lib) as Dictionary<SpeciesAndSexIndex, GameObject>;

                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (kvp.Key.speciesIndex == speciesIdx && kvp.Value != null)
                            {
                                if (!kvp.Value.activeSelf) kvp.Value.SetActive(true);
                                GameObject clone = UnityEngine.Object.Instantiate(kvp.Value);
                                clone.transform.position = location;
                                __result = clone;
                                return false; // Skip procedural plant subdivision!
                            }
                        }
                    }
                }
            }
            catch { }

            return true;
        }
    }
}
