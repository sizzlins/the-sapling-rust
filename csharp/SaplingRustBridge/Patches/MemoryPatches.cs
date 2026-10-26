using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Aggressive memory reduction patches targeting the sub-200MB / 100MB footprint:
    /// 
    /// 1. OBJECT POOL CAPPING:
    ///    - Prevents 300-year hoarding of thousands of active 3D GameObjects at y=1000 in the sky.
    ///    - Instantly disables pooled objects (SetActive(false)).
    ///    - Caps maximum pool size to 5 instances per definition/sex; excess is destroyed immediately.
    ///    - Sets expireAfterYears to 1.
    /// 
    /// 2. PREBUILT LIBRARY CAPPING:
    ///    - Sets cleanUpAfterNrOfInactiveYears to 1 instead of 300.
    /// 
    /// 3. ZERO-ALLOC WORLDSTATE / WORLDAREA CLONING:
    ///    - Replaces daily new WorldState() with FormatterServices.GetUninitializedObject.
    ///    - Eliminates the daily creation of 36 throwaway Dictionaries and 6 Hashtables that
    ///      rapidly fragment Mono's Boehm GC heap into gigabytes.
    /// 
    /// 4. AGGRESSIVE WORKING SET & NATIVE ASSET TRIMMING:
    ///    - Invokes Resources.UnloadUnusedAssets, GC.Collect, and Windows EmptyWorkingSet
    ///      after Time Jumps and periodic simulation milestones to return memory to the OS.
    /// </summary>
    [HarmonyPatch]
    public static class MemoryPatches
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        private const int MaxPoolSizePerKey = 5;
        private static int _daysSinceLastTrim = 0;

        // ── 1. TAME ORGANISM OBJECT POOL & PREBUILT LIBRARY ──

        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_MemoryInit(WorldView __instance)
        {
            if (__instance == null) return;

            if (__instance.organismObjectPool != null)
            {
                __instance.organismObjectPool.expireAfterYears = 1;
                __instance.organismObjectPool.outOfSightInsteadOfDisabling = false;
            }

            if (__instance.prebuiltAnimalLibrary != null)
            {
                var field = AccessTools.Field(typeof(PrebuiltOrganismLibrary), "cleanUpAfterNrOfInactiveYears");
                field?.SetValue(__instance.prebuiltAnimalLibrary, 1);
            }

            if (__instance.prebuiltPlantLibrary != null)
            {
                var field = AccessTools.Field(typeof(PrebuiltOrganismLibrary), "cleanUpAfterNrOfInactiveYears");
                field?.SetValue(__instance.prebuiltPlantLibrary, 1);
            }
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.AddFullSpecificOrganism))]
        [HarmonyPrefix]
        public static bool OrganismObjectPool_AddFullSpecificOrganism_Prefix(
            OrganismObjectPool __instance,
            CopiedOrganism obj,
            bool keepSharedMeshAtExpiration)
        {
            if (__instance == null || obj == null || obj.gameObject == null) return false;

            // Immediately disable so it consumes 0 CPU/GPU/transform memory while pooled
            obj.gameObject.SetActive(false);
            obj.poolingStatus = PoolingStatus.PooledAndDisabled;

            DictListGameObjects pool = (!obj.youngVersion)
                ? __instance.fullSpecificOldOrganismPool
                : __instance.fullSpecificYoungOrganismPool;

            // Cap pool size! Destroy excess creatures immediately instead of hoarding in RAM
            if (pool != null && pool.GetSize(obj.definitionIndex, obj.sexIndex) >= MaxPoolSizePerKey)
            {
                var smr = obj.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && !keepSharedMeshAtExpiration && smr.sharedMesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(smr.sharedMesh, true);
                }
                UnityEngine.Object.Destroy(obj.gameObject);
                return false; // Handled and destroyed, do not add to pool
            }

            return true; // Let vanilla pool up to MaxPoolSizePerKey
        }

        // ── 2. ZERO-ALLOC WORLDSTATE CLONE ──

        [HarmonyPatch(typeof(WorldState), nameof(WorldState.Clone))]
        [HarmonyPrefix]
        public static bool WorldState_Clone_Prefix(WorldState __instance, ref WorldState __result)
        {
            if (__instance == null) return true;

            // Allocate raw object without running constructor that allocates 36 Dictionaries & 6 Hashtables
            WorldState clone = (WorldState)FormatterServices.GetUninitializedObject(typeof(WorldState));

            // Clone areas
            if (__instance.areas != null)
            {
                clone.areas = new WorldArea[__instance.areas.Length];
                for (int i = 0; i < __instance.areas.Length; i++)
                {
                    clone.areas[i] = __instance.areas[i] != null ? __instance.areas[i].Clone() : null;
                }
            }

            // Share demographics & population tracking references
            clone.speciesPresentInArea = __instance.speciesPresentInArea;
            clone.numberOfLivingOrganismsPerSpecies = __instance.numberOfLivingOrganismsPerSpecies;
            clone.numberOfManuallyPlacedOrganismsPerSpecies = __instance.numberOfManuallyPlacedOrganismsPerSpecies;
            clone.numberOfNaturallyOccurringOrganismsPerSpecies = __instance.numberOfNaturallyOccurringOrganismsPerSpecies;
            clone.maximumLivingOrganismsPerSpecies = __instance.maximumLivingOrganismsPerSpecies;

            // Share travel dictionaries
            clone.travelsFromGroundArea0 = __instance.travelsFromGroundArea0;
            clone.travelsFromGroundArea1 = __instance.travelsFromGroundArea1;
            clone.travelsFromGroundArea2 = __instance.travelsFromGroundArea2;
            clone.travelsFromTreeArea0 = __instance.travelsFromTreeArea0;
            clone.travelsFromTreeArea1 = __instance.travelsFromTreeArea1;
            clone.travelsFromTreeArea2 = __instance.travelsFromTreeArea2;
            clone.travelsFromAirArea0 = __instance.travelsFromAirArea0;
            clone.travelsFromAirArea1 = __instance.travelsFromAirArea1;
            clone.travelsFromAirArea2 = __instance.travelsFromAirArea2;
            clone.travelsFromWaterSurfaceArea0 = __instance.travelsFromWaterSurfaceArea0;
            clone.travelsFromWaterSurfaceArea1 = __instance.travelsFromWaterSurfaceArea1;
            clone.travelsFromWaterSurfaceArea2 = __instance.travelsFromWaterSurfaceArea2;
            clone.travelsFromUnderWaterArea0 = __instance.travelsFromUnderWaterArea0;
            clone.travelsFromUnderWaterArea1 = __instance.travelsFromUnderWaterArea1;
            clone.travelsFromUnderWaterArea2 = __instance.travelsFromUnderWaterArea2;
            clone.travelsFromDeepSeaArea0 = __instance.travelsFromDeepSeaArea0;
            clone.travelsFromDeepSeaArea1 = __instance.travelsFromDeepSeaArea1;
            clone.travelsFromDeepSeaArea2 = __instance.travelsFromDeepSeaArea2;

            clone.travelsToGroundArea0 = __instance.travelsToGroundArea0;
            clone.travelsToGroundArea1 = __instance.travelsToGroundArea1;
            clone.travelsToGroundArea2 = __instance.travelsToGroundArea2;
            clone.travelsToTreeArea0 = __instance.travelsToTreeArea0;
            clone.travelsToTreeArea1 = __instance.travelsToTreeArea1;
            clone.travelsToTreeArea2 = __instance.travelsToTreeArea2;
            clone.travelsToAirArea0 = __instance.travelsToAirArea0;
            clone.travelsToAirArea1 = __instance.travelsToAirArea1;
            clone.travelsToAirArea2 = __instance.travelsToAirArea2;
            clone.travelsToWaterSurfaceArea0 = __instance.travelsToWaterSurfaceArea0;
            clone.travelsToWaterSurfaceArea1 = __instance.travelsToWaterSurfaceArea1;
            clone.travelsToWaterSurfaceArea2 = __instance.travelsToWaterSurfaceArea2;
            clone.travelsToUnderWaterArea0 = __instance.travelsToUnderWaterArea0;
            clone.travelsToUnderWaterArea1 = __instance.travelsToUnderWaterArea1;
            clone.travelsToUnderWaterArea2 = __instance.travelsToUnderWaterArea2;
            clone.travelsToDeepSeaArea0 = __instance.travelsToDeepSeaArea0;
            clone.travelsToDeepSeaArea1 = __instance.travelsToDeepSeaArea1;
            clone.travelsToDeepSeaArea2 = __instance.travelsToDeepSeaArea2;

            clone.sourcesOfArrivalsAtCellIndicesArea0 = __instance.sourcesOfArrivalsAtCellIndicesArea0;
            clone.sourcesOfArrivalsAtCellIndicesArea1 = __instance.sourcesOfArrivalsAtCellIndicesArea1;
            clone.sourcesOfArrivalsAtCellIndicesArea2 = __instance.sourcesOfArrivalsAtCellIndicesArea2;

            clone.sounds = __instance.sounds;
            clone.echos = __instance.echos;
            clone.indicesOfSpeciesThatDiedOutToday = __instance.indicesOfSpeciesThatDiedOutToday;

            __result = clone;
            return false;
        }

        // ── 3. ZERO-ALLOC WORLDAREA CLONE ──

        [HarmonyPatch(typeof(WorldArea), nameof(WorldArea.Clone))]
        [HarmonyPrefix]
        public static bool WorldArea_Clone_Prefix(WorldArea __instance, ref WorldArea __result)
        {
            if (__instance == null) return true;

            WorldArea clone = (WorldArea)FormatterServices.GetUninitializedObject(typeof(WorldArea));
            clone.removeGrassFromCell = __instance.removeGrassFromCell;
            clone.sunLightFactor = __instance.sunLightFactor;
            clone.ownDepth = __instance.ownDepth;
            clone.size = __instance.size;
            clone.cells = __instance.cells;
            clone.locationGroups = __instance.locationGroups;
            clone.allParticles = __instance.allParticles;
            clone.animalPositionTracker = __instance.animalPositionTracker;
            clone.cellIndicesOfParticles = __instance.cellIndicesOfParticles;
            clone.cellIndicesOfPlants = __instance.cellIndicesOfPlants;
            clone.cellIndicesOfPlankton = __instance.cellIndicesOfPlankton;
            clone.cellIndicesOfMacroAlgae = __instance.cellIndicesOfMacroAlgae;
            clone.cellIndicesOfFungi = __instance.cellIndicesOfFungi;
            clone.cellIndicesOfDormantSeeds = __instance.cellIndicesOfDormantSeeds;
            clone.indicesOfCellsWherePlanktonDied = __instance.indicesOfCellsWherePlanktonDied;
            clone.indicesOfCellsWhereAMacroAlgaDied = __instance.indicesOfCellsWhereAMacroAlgaDied;
            clone.indicesOfCellsWhereAPlantDied = __instance.indicesOfCellsWhereAPlantDied;
            clone.indicesOfCellsWhereAFungusDied = __instance.indicesOfCellsWhereAFungusDied;
            clone.indicesOfCellsWhereAnAnimalDied = __instance.indicesOfCellsWhereAnAnimalDied;
            clone.locationTypesOfCellsWhereAnAnimalDied = __instance.locationTypesOfCellsWhereAnAnimalDied;

            __result = clone;
            return false;
        }

        // ── 4. PERIODIC MEMORY TRIMMING & WORKING SET COMPACTION ──

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPostfix]
        public static void TimeLine_LiveADay_TrimMemory()
        {
            _daysSinceLastTrim++;
            if (_daysSinceLastTrim >= 100)
            {
                _daysSinceLastTrim = 0;
                TrimMemory();
            }
        }

        /// <summary>
        /// Flushes unused native assets, forces GC collection, and compacts Windows working set.
        /// </summary>
        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                Resources.UnloadUnusedAssets();

                // Tell Windows to physically release clean and freed pages from process working set
                IntPtr proc = Process.GetCurrentProcess().Handle;
                if (proc != IntPtr.Zero)
                {
                    EmptyWorkingSet(proc);
                }
            }
            catch { }
        }
    }
}
