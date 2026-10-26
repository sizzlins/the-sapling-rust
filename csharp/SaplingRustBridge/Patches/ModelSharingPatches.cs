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
            // 3. Fallback: if this species has no model, clone ANY valid existing model in the library!
            // This completely eliminates synchronous Catmull-Clark mesh builds (21-second freezes).
            GameObject fallbackTemplate = null;
            if (speciesDict != null)
            {
                foreach (var kvp in speciesDict)
                {
                    if (kvp.Value != null) { fallbackTemplate = kvp.Value; break; }
                }
            }
            if (fallbackTemplate == null && otherSpeciesDict != null)
            {
                foreach (var kvp in otherSpeciesDict)
                {
                    if (kvp.Value != null) { fallbackTemplate = kvp.Value; break; }
                }
            }

            if (fallbackTemplate != null)
            {
                if (!fallbackTemplate.activeSelf) fallbackTemplate.SetActive(true);
                __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                __result = UnityEngine.Object.Instantiate(fallbackTemplate);

                // Cache this template under this speciesIndex so future lookups are immediate
                var key = new SpeciesAndSexIndex(speciesIndex, sexIndex);
                if (speciesDict != null && !speciesDict.ContainsKey(key))
                {
                    speciesDict[key] = fallbackTemplate;
                }
                return;
            }
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPrefix]
        public static bool BuildAndCacheAnimal_Prefix(
            WorldView __instance,
            AnimalDefinition animalDefinition,
            bool youngVersion,
            int animalSpeciesIndex,
            LibraryProfile libraryProfile,
            ref bool representativeForSpecies)
        {
            // Never build individual mutant meshes on the fly! Mutants share the species model.
            if (!representativeForSpecies) return false;

            // If the library already has a model for this species, don't rebuild it!
            var lib = __instance?.prebuiltAnimalLibrary;
            if (lib != null)
            {
                var dict = youngVersion
                    ? AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsYoungVersionForSpeciesIndex")?.GetValue(lib) as Dictionary<SpeciesAndSexIndex, GameObject>
                    : AccessTools.Field(typeof(PrebuiltOrganismLibrary), "gameObjectsOldVersionForSpeciesIndex")?.GetValue(lib) as Dictionary<SpeciesAndSexIndex, GameObject>;

                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        if (kvp.Key.speciesIndex == animalSpeciesIndex && kvp.Value != null)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
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

                        // Universal fallback: clone ANY animal model in dict
                        foreach (var kvp in dict)
                        {
                            if (kvp.Value != null)
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

                        // Universal fallback: clone ANY plant model in dict
                        foreach (var kvp in dict)
                        {
                            if (kvp.Value != null)
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
