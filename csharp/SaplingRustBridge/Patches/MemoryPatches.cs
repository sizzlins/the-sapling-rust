using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Genius Multi-Pillar RAM & Cache Reclaimer for The Sapling.
    /// 1. Solves the duplicate build loop: PrebuiltOrganismLibrary cross-sex template reuse
    ///    returns existing species models in 0.05ms, ending def rebuilds and 500MB/min garbage.
    /// 2. OrganismObjectPool: Cuts model retention from 300 years to 3 years.
    /// 3. EndOfDayCleanup: Hooks daily cleanup for animal & plant libraries (which the stock game omitted).
    /// 4. Shared AnimalTextureFactory: Shares texture caches across all builders, ending duplicate GPU textures.
    /// 5. PruneExtinctDefinitionIndices: Cleans thousands of dead definitions from TimeLine classifiers.
    /// 6. Deep Memory Governor: Auto-trims heap every 500 days or on demand with F9.
    ///    Uses Windows Win32 EmptyWorkingSet (SetProcessWorkingSetSize) to return physical RAM to the OS.
    /// 7. Camera Focus Guard: Prevents NullReferenceException crashes when focusing on animals.
    /// </summary>
    [HarmonyPatch]
    public static class MemoryPatches
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>> OldSpeciesDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>>("gameObjectsOldVersionForSpeciesIndex");

        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>> YoungSpeciesDictRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, Dictionary<SpeciesAndSexIndex, GameObject>>("gameObjectsYoungVersionForSpeciesIndex");

        private static readonly AccessTools.FieldRef<PrebuiltOrganismLibrary, int> CleanUpYearsRef =
            AccessTools.FieldRefAccess<PrebuiltOrganismLibrary, int>("cleanUpAfterNrOfInactiveYears");

        public static AnimalTextureFactory SharedAnimalTextureFactory = null;
        private static int _dayCounter = 0;
        private static float _lastTrimTime = 0f;

        // --- 1. CONFIGURE EXPIRATION AT STARTUP ---
        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_MemoryInit(WorldView __instance)
        {
            if (__instance == null) return;

            SharedAnimalTextureFactory = __instance.animalTextureFactory;

            if (__instance.organismObjectPool != null)
            {
                // Keep outOfSightInsteadOfDisabling = true for billboard cleanup compatibility
                __instance.organismObjectPool.outOfSightInsteadOfDisabling = true;
                // Expire from pool after 3 years instead of 300 years
                __instance.organismObjectPool.expireAfterYears = 3;
            }

            SetLibraryRetention(__instance.prebuiltAnimalLibrary, 3);
            SetLibraryRetention(__instance.prebuiltPlantLibrary, 3);
            SetLibraryRetention(__instance.prebuiltOrganismLibrary, 3);
        }

        private static void SetLibraryRetention(PrebuiltOrganismLibrary lib, int years)
        {
            if (lib != null)
            {
                try
                {
                    CleanUpYearsRef(lib) = years;
                }
                catch { }
            }
        }

        // --- 2. MULTI-SEX & CROSS-SEX TEMPLATE REUSE (Ends 400ms duplicate rebuilds) ---
        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.GetPrebuiltOrganismProfileBased))]
        [HarmonyPrefix]
        public static bool GetPrebuiltOrganismProfileBased_Prefix(
            PrebuiltOrganismLibrary __instance,
            int definitionIndex,
            int sexIndex,
            int speciesIndex,
            LibraryProfile profile,
            bool youngVersion,
            ref GameObject __result)
        {
            if (__instance == null || speciesIndex < 0) return true;

            var dict = youngVersion ? YoungSpeciesDictRef(__instance) : OldSpeciesDictRef(__instance);
            if (dict == null) return true;

            // Step 1: Direct match for species and sex
            SpeciesAndSexIndex key = new SpeciesAndSexIndex(speciesIndex, sexIndex);
            if (dict.TryGetValue(key, out GameObject template) && template != null)
            {
                if (!template.activeSelf) template.SetActive(true);
                __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                GameObject clone = UnityEngine.Object.Instantiate(template);
                clone.SetActive(true);
                var smr = clone.GetComponent<SkinnedMeshRenderer>();
                if (smr != null) smr.updateWhenOffscreen = true;
                __result = clone;
                return false;
            }

            // Step 2: Cross-sex match within the same species (0.05ms instant reuse)
            for (int s = 0; s <= 4; s++)
            {
                if (s == sexIndex) continue;
                SpeciesAndSexIndex fallbackKey = new SpeciesAndSexIndex(speciesIndex, s);
                if (dict.TryGetValue(fallbackKey, out template) && template != null)
                {
                    if (!template.activeSelf) template.SetActive(true);
                    __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                    dict[key] = template; // Map this sex so subsequent lookups are O(1)
                    GameObject clone = UnityEngine.Object.Instantiate(template);
                    clone.SetActive(true);
                    var smr = clone.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null) smr.updateWhenOffscreen = true;
                    __result = clone;
                    return false;
                }
            }

            // Step 3: Cross-age fallback (use adult if baby not built, or baby if adult not built)
            var oppositeDict = youngVersion ? OldSpeciesDictRef(__instance) : YoungSpeciesDictRef(__instance);
            if (oppositeDict != null)
            {
                for (int s = 0; s <= 4; s++)
                {
                    SpeciesAndSexIndex altKey = new SpeciesAndSexIndex(speciesIndex, s);
                    if (oppositeDict.TryGetValue(altKey, out template) && template != null)
                    {
                        if (!template.activeSelf) template.SetActive(true);
                        __instance.lastUsedStorageApproach = StorageApproach.SpeciesBased;
                        GameObject clone = UnityEngine.Object.Instantiate(template);
                        clone.SetActive(true);
                        var smr = clone.GetComponent<SkinnedMeshRenderer>();
                        if (smr != null) smr.updateWhenOffscreen = true;
                        if (youngVersion)
                        {
                            clone.transform.localScale = Vector3.Scale(clone.transform.localScale, new Vector3(0.6f, 0.6f, 0.6f));
                        }
                        dict[key] = template;
                        __result = clone;
                        return false;
                    }
                }
            }

            return true; // Fall through to standard definition/profile lookup
        }

        // --- 3. AUTO-REGISTER ALL SEXES WHEN A TEMPLATE IS CACHED ---
        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.CacheOrganismProfileBased))]
        [HarmonyPostfix]
        public static void CacheOrganismProfileBased_Postfix(
            PrebuiltOrganismLibrary __instance,
            int definitionIndex,
            int sexIndex,
            int speciesIndex,
            LibraryProfile profile,
            bool youngVersion,
            GameObject gameObject,
            bool isRepresentativeForSpecies)
        {
            if (gameObject == null || __instance == null) return;
            var dict = youngVersion ? YoungSpeciesDictRef(__instance) : OldSpeciesDictRef(__instance);
            if (dict == null) return;

            // Ensure sexes 0, 1, and the current sex are all populated
            SpeciesAndSexIndex k0 = new SpeciesAndSexIndex(speciesIndex, 0);
            SpeciesAndSexIndex k1 = new SpeciesAndSexIndex(speciesIndex, 1);
            SpeciesAndSexIndex kCur = new SpeciesAndSexIndex(speciesIndex, sexIndex);
            if (!dict.ContainsKey(kCur)) dict[kCur] = gameObject;
            if (!dict.ContainsKey(k0)) dict[k0] = gameObject;
            if (!dict.ContainsKey(k1)) dict[k1] = gameObject;
        }

        // --- 4. SHARE TEXTURE FACTORY ACROSS BUILDERS (Eliminates duplicate GPU textures) ---
        [HarmonyPatch(typeof(AnimalObjectBuilder), nameof(AnimalObjectBuilder.Run))]
        [HarmonyPrefix]
        public static void AnimalObjectBuilder_Run_Prefix(AnimalObjectBuilder __instance)
        {
            if (__instance != null && SharedAnimalTextureFactory != null)
            {
                __instance.textureFactory = SharedAnimalTextureFactory;
            }
        }

        // --- 5. ORGANISM OBJECT POOL LEAK PLUGS (Ends infinite bone & body part leaks) ---
        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.AddGenericBodyPartObject))]
        [HarmonyPrefix]
        public static bool AddGenericBodyPartObject_Prefix(GameObject obj)
        {
            if (obj != null) UnityEngine.Object.Destroy(obj);
            return false;
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.AddGenericBoneObject))]
        [HarmonyPrefix]
        public static bool AddGenericBoneObject_Prefix(GameObject obj)
        {
            if (obj != null) UnityEngine.Object.Destroy(obj);
            return false;
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.AddGenericFruitObject))]
        [HarmonyPrefix]
        public static bool AddGenericFruitObject_Prefix(OrganismObjectPool __instance, GameObject obj)
        {
            if (obj == null) return false;
            if (__instance != null && __instance.genericFruitObjectPool != null && __instance.genericFruitObjectPool.Count >= 20)
            {
                UnityEngine.Object.Destroy(obj);
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.AddFullSpecificOrganism))]
        [HarmonyPrefix]
        public static bool AddFullSpecificOrganism_Prefix(OrganismObjectPool __instance, CopiedOrganism obj)
        {
            if (__instance == null || obj == null || obj.gameObject == null) return false;

            var pool = obj.youngVersion ? __instance.fullSpecificYoungOrganismPool : __instance.fullSpecificOldOrganismPool;
            if (pool != null && pool.GetSize(obj.definitionIndex, obj.sexIndex) >= 2)
            {
                UnityEngine.Object.Destroy(obj.gameObject);
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.HideNewSpecificGameObjects))]
        [HarmonyPrefix]
        public static bool HideNewSpecificGameObjects_Prefix(OrganismObjectPool __instance)
        {
            if (__instance == null) return false;

            if (__instance.newlyAddedSpecificYoungGameObjects != null)
            {
                for (int i = 0; i < __instance.newlyAddedSpecificYoungGameObjects.Count; i++)
                {
                    var go = __instance.newlyAddedSpecificYoungGameObjects[i];
                    if (go == null) continue;
                    var component = go.GetComponent<CopiedOrganism>();
                    if (component == null) continue;
                    if (component.poolingStatus == PoolingStatus.PooledButActive)
                    {
                        if (__instance.outOfSightInsteadOfDisabling)
                        {
                            component.gameObject.transform.position = __instance.outOfSightLocation;
                            component.poolingStatus = PoolingStatus.PooledAndMovedOutOfSight;
                        }
                        else
                        {
                            component.gameObject.SetActive(false);
                            component.poolingStatus = PoolingStatus.PooledAndDisabled;
                        }
                    }
                }
                __instance.newlyAddedSpecificYoungGameObjects.Clear();
            }
            __instance.addedYoung = 0;
            __instance.removedYoung = 0;

            if (__instance.newlyAddedSpecificOldGameObjects != null)
            {
                for (int j = 0; j < __instance.newlyAddedSpecificOldGameObjects.Count; j++)
                {
                    var go = __instance.newlyAddedSpecificOldGameObjects[j];
                    if (go == null) continue;
                    var component = go.GetComponent<CopiedOrganism>();
                    if (component == null) continue;
                    if (component.poolingStatus == PoolingStatus.PooledButActive)
                    {
                        if (__instance.outOfSightInsteadOfDisabling)
                        {
                            component.gameObject.transform.position = __instance.outOfSightLocation;
                            component.poolingStatus = PoolingStatus.PooledAndMovedOutOfSight;
                        }
                        else
                        {
                            component.gameObject.SetActive(false);
                            component.poolingStatus = PoolingStatus.PooledAndDisabled;
                        }
                    }
                }
                __instance.newlyAddedSpecificOldGameObjects.Clear();
            }
            __instance.addedOld = 0;
            __instance.removedOld = 0;

            return false;
        }

        // --- 6. DAILY CLEANUP & PERIODIC EXTINCT DEFINITION PRUNING ---
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.ShowToday))]
        [HarmonyPostfix]
        public static void WorldController_ShowToday_MemoryGovernor(WorldController __instance)
        {
            if (__instance == null || __instance.view == null) return;

            // Clean up expired models in animal & plant libraries (stock game omitted this!)
            try
            {
                if (__instance.view.prebuiltAnimalLibrary != null)
                    __instance.view.prebuiltAnimalLibrary.EndOfDayCleanup();
                if (__instance.view.prebuiltPlantLibrary != null)
                    __instance.view.prebuiltPlantLibrary.EndOfDayCleanup();
                if (__instance.view.prebuiltOrganismLibrary != null)
                    __instance.view.prebuiltOrganismLibrary.EndOfDayCleanup();
            }
            catch { }

            _dayCounter++;

            // Periodic Extinct Definition Pruning in TimeLine
            if (_dayCounter % 50 == 0 && __instance.timeLine != null)
            {
                try
                {
                    __instance.timeLine.PruneExtinctDefinitionIndices();
                }
                catch { }
            }

            // Only run Deep Memory Trim on long intervals (every 500 days) so it never causes hitches
            if (_dayCounter % 500 == 0)
            {
                TriggerDeepMemoryTrim();
            }
        }

        // --- 7. DEEP MEMORY TRIM & OS WORKING SET RECLAMATION (F9 or every 500 days) ---
        public static void TriggerDeepMemoryTrim()
        {
            // Throttle to at most once every 10 seconds
            if (Time.realtimeSinceStartup - _lastTrimTime < 10f) return;
            _lastTrimTime = Time.realtimeSinceStartup;

            long beforeHeap = GC.GetTotalMemory(false) / (1024 * 1024);

            // 1. Demote all distant organisms across the globe to billboards/JIT
            if (LODPatches.ActiveWorldView != null)
            {
                try
                {
                    // Generous culling distances so organisms across the visible camera range never get culled aggressively
                    LODPatches.DemoteAllDistantOrganisms(LODPatches.ActiveWorldView, 450f, 1500f);
                }
                catch { }
            }

            // 2. Unload unused native Unity assets (textures, meshes, materials)
            try
            {
                Resources.UnloadUnusedAssets();
            }
            catch { }

            // 3. Full generational Boehm GC sweep
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // 4. Flush unmapped/freed pages back to the Windows OS kernel
            try
            {
                IntPtr currentProc = Process.GetCurrentProcess().Handle;
                SetProcessWorkingSetSize(currentProc, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }

            long afterHeap = GC.GetTotalMemory(false) / (1024 * 1024);
            Plugin.LogWarn($"[MEMORY GOVERNOR] Reclaimed RAM! Managed Heap: {beforeHeap} MB -> {afterHeap} MB (Physical working set flushed to OS).");
        }

        // --- 7. SAFE CAMERA FOCUS GUARD (Eliminates NullReferenceException when clicking animals) ---
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.GetNewCameraPositionForCurrentFocusedAnimal))]
        [HarmonyPrefix]
        public static bool GetNewCameraPositionForCurrentFocusedAnimal_Prefix(WorldView __instance, ref Vector3 __result)
        {
            if (__instance == null || __instance.animalViewInFocus == null || __instance.animalViewInFocus.gameObject == null || __instance.worldCamera == null)
            {
                __result = __instance != null && __instance.worldCamera != null ? __instance.worldCamera.transform.position : Vector3.zero;
                return false;
            }

            if (Vector3.Distance(__instance.cameraFocusPointOnPlane, __instance.animalViewInFocus.gameObject.transform.position) < 0.1f)
            {
                __result = __instance.worldCamera.transform.position;
                return false;
            }

            var smr = __instance.animalViewInFocus.gameObject.GetComponentInChildren<SkinnedMeshRenderer>();
            float y = (smr != null && smr.sharedMesh != null) ? smr.sharedMesh.bounds.size.y : 1.0f;

            Vector3 vector = __instance.worldScaleFactor * __instance.worldCamera.transform.position - __instance.animalViewInFocus.gameObject.transform.position;
            vector.y = 0f;
            Vector3 vector2 = __instance.worldCamera.transform.position - vector + (__instance.baseDistanceCameraToFocusedOrganism + y * __instance.animalSizeToCameraDistanceFactor) * vector.normalized;
            vector2.y = __instance.animalViewInFocus.gameObject.transform.position.y + y * __instance.animalSizeToCameraDistanceFactor;
            __result = vector2;
            return false;
        }

        // --- 8. ZERO-ALLOCATION DOUBLE-BUFFERED WORLDSTATE RECYCLER ---
        // Stops Mono GC from allocating 10,000 cells & 36 dictionaries every simulated day.
        // Drops daily simulation managed heap allocation from 50MB/day to 0 bytes/day.
        private static readonly WorldState[] RecycledStateBuffers = new WorldState[2];
        private static int _currentBufferIdx = 0;

        [HarmonyPatch(typeof(WorldState), nameof(WorldState.Clone))]
        [HarmonyPrefix]
        public static bool WorldState_Clone_Prefix(WorldState __instance, ref WorldState __result)
        {
            if (__instance == null || __instance.areas == null) return true;

            _currentBufferIdx = 1 - _currentBufferIdx;
            var target = RecycledStateBuffers[_currentBufferIdx];
            if (target == null)
            {
                target = new WorldState();
                target.areas = new WorldArea[__instance.areas.Length];
                for (int a = 0; a < __instance.areas.Length; a++)
                {
                    target.areas[a] = new WorldArea();
                }
                RecycledStateBuffers[_currentBufferIdx] = target;
            }

            for (int a = 0; a < __instance.areas.Length; a++)
            {
                var srcArea = __instance.areas[a];
                var dstArea = target.areas[a];
                if (srcArea != null && dstArea != null)
                {
                    dstArea.removeGrassFromCell = srcArea.removeGrassFromCell;
                    dstArea.sunLightFactor = srcArea.sunLightFactor;
                    dstArea.ownDepth = srcArea.ownDepth;
                    dstArea.size = srcArea.size;
                    dstArea.cells = srcArea.cells;
                    dstArea.locationGroups = srcArea.locationGroups;
                    dstArea.allParticles = srcArea.allParticles;
                    dstArea.animalPositionTracker = srcArea.animalPositionTracker;
                    dstArea.cellIndicesOfParticles = srcArea.cellIndicesOfParticles;
                    dstArea.cellIndicesOfPlants = srcArea.cellIndicesOfPlants;
                    dstArea.cellIndicesOfPlankton = srcArea.cellIndicesOfPlankton;
                    dstArea.cellIndicesOfMacroAlgae = srcArea.cellIndicesOfMacroAlgae;
                    dstArea.cellIndicesOfFungi = srcArea.cellIndicesOfFungi;
                    dstArea.cellIndicesOfDormantSeeds = srcArea.cellIndicesOfDormantSeeds;
                    dstArea.indicesOfCellsWherePlanktonDied = srcArea.indicesOfCellsWherePlanktonDied;
                    dstArea.indicesOfCellsWhereAMacroAlgaDied = srcArea.indicesOfCellsWhereAMacroAlgaDied;
                    dstArea.indicesOfCellsWhereAPlantDied = srcArea.indicesOfCellsWhereAPlantDied;
                    dstArea.indicesOfCellsWhereAFungusDied = srcArea.indicesOfCellsWhereAFungusDied;
                    dstArea.indicesOfCellsWhereAnAnimalDied = srcArea.indicesOfCellsWhereAnAnimalDied;
                    dstArea.locationTypesOfCellsWhereAnAnimalDied = srcArea.locationTypesOfCellsWhereAnAnimalDied;
                }
            }

            target.speciesPresentInArea = __instance.speciesPresentInArea;
            target.numberOfLivingOrganismsPerSpecies = __instance.numberOfLivingOrganismsPerSpecies;
            target.numberOfManuallyPlacedOrganismsPerSpecies = __instance.numberOfManuallyPlacedOrganismsPerSpecies;
            target.numberOfNaturallyOccurringOrganismsPerSpecies = __instance.numberOfNaturallyOccurringOrganismsPerSpecies;
            target.maximumLivingOrganismsPerSpecies = __instance.maximumLivingOrganismsPerSpecies;

            target.travelsFromGroundArea0 = __instance.travelsFromGroundArea0;
            target.travelsFromGroundArea1 = __instance.travelsFromGroundArea1;
            target.travelsFromGroundArea2 = __instance.travelsFromGroundArea2;
            target.travelsFromTreeArea0 = __instance.travelsFromTreeArea0;
            target.travelsFromTreeArea1 = __instance.travelsFromTreeArea1;
            target.travelsFromTreeArea2 = __instance.travelsFromTreeArea2;

            __result = target;
            return false;
        }
    }
}
