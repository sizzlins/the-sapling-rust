using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Memory governor and stability patches for The Sapling.
    /// 1. Shares AnimalTextureFactory across builders to eliminate duplicate GPU textures.
    /// 2. Camera focus guard prevents NullReferenceException when focusing on animals.
    /// 3. Deep memory trim (F9 key) flushes unused assets, runs GC, and returns physical RAM to Windows.
    /// </summary>
    [HarmonyPatch]
    public static class MemoryPatches
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        public static AnimalTextureFactory SharedAnimalTextureFactory = null;
        private static int _dayCounter = 0;
        private static float _lastTrimTime = 0f;

        // --- 1. CAPTURE SHARED TEXTURE FACTORY & CONFIGURE SANE POOL EXPIRATION ---
        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_MemoryInit(WorldView __instance)
        {
            if (__instance == null) return;
            SharedAnimalTextureFactory = __instance.animalTextureFactory;

            if (__instance.organismObjectPool != null)
            {
                // Vanilla keeps dead pooled organisms activeSelf=true in the sky for 300 years!
                __instance.organismObjectPool.outOfSightInsteadOfDisabling = false; // Disable dead pooled GameObjects!
                __instance.organismObjectPool.expireAfterYears = 3; // Clean dead pools after 3 years instead of 300!
            }

            // Vanilla keeps extinct species 3D blueprints in memory for 300 years!
            SetLibraryCleanupYears(__instance.prebuiltOrganismLibrary, 5);
            SetLibraryCleanupYears(__instance.prebuiltPlantLibrary, 5);
            SetLibraryCleanupYears(__instance.prebuiltAnimalLibrary, 5);
        }

        private static void SetLibraryCleanupYears(PrebuiltOrganismLibrary lib, int years)
        {
            if (lib == null) return;
            try
            {
                Traverse.Create(lib).Field("cleanUpAfterNrOfInactiveYears").SetValue(years);
            }
            catch { }
        }

        // --- 2. SHARE TEXTURE FACTORY ACROSS BUILDERS (Eliminates duplicate GPU textures) ---
        [HarmonyPatch(typeof(AnimalObjectBuilder), nameof(AnimalObjectBuilder.Run))]
        [HarmonyPrefix]
        public static void AnimalObjectBuilder_Run_Prefix(AnimalObjectBuilder __instance)
        {
            if (__instance != null && SharedAnimalTextureFactory != null)
            {
                __instance.textureFactory = SharedAnimalTextureFactory;
            }
        }

        // --- 3. PERIODIC EXTINCT DEFINITION PRUNING & AUTOMATIC MEMORY RECLAMATION ---
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.ShowToday))]
        [HarmonyPostfix]
        public static void WorldController_ShowToday_MemoryGovernor(WorldController __instance)
        {
            if (__instance == null || __instance.timeLine == null) return;

            _dayCounter++;

            // Periodically clean extinct definitions from TimeLine classifiers (vanilla only does this during autosave)
            if (_dayCounter % 100 == 0)
            {
                try
                {
                    __instance.timeLine.PruneExtinctDefinitionIndices();
                }
                catch { }
            }

            // Automatically reclaim unreferenced native assets and return physical RAM to Windows every 50 in-game days
            if (_dayCounter % 50 == 0)
            {
                TriggerDeepMemoryTrim();
            }
        }

        // --- 4. MANUAL DEEP MEMORY TRIM & OS WORKING SET RECLAMATION (F9 key) ---
        public static void TriggerDeepMemoryTrim()
        {
            if (Time.realtimeSinceStartup - _lastTrimTime < 5f) return;
            _lastTrimTime = Time.realtimeSinceStartup;

            long beforeHeap = GC.GetTotalMemory(false) / (1024 * 1024);

            try
            {
                Resources.UnloadUnusedAssets();
            }
            catch { }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            try
            {
                IntPtr currentProc = Process.GetCurrentProcess().Handle;
                SetProcessWorkingSetSize(currentProc, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }

            long afterHeap = GC.GetTotalMemory(false) / (1024 * 1024);
            Plugin.LogWarn($"[MEMORY GOVERNOR] Reclaimed RAM! Managed Heap: {beforeHeap} MB -> {afterHeap} MB (Physical working set flushed to OS).");
        }

        // --- 5. SAFE CAMERA FOCUS GUARD (Eliminates NullReferenceException when clicking animals) ---
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
    }
}
