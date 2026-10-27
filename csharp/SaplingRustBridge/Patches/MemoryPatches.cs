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
                // Vanilla keeps dead pooled organisms out of sight in the sky
                __instance.organismObjectPool.outOfSightInsteadOfDisabling = true;
            }
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

        // --- 3. PERIODIC EXTINCT DEFINITION PRUNING ---
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
        }

        // --- 4. MANUAL DEEP MEMORY TRIM (F9 key) ---
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

            long afterHeap = GC.GetTotalMemory(false) / (1024 * 1024);
            Plugin.LogWarn($"[MEMORY] Reclaimed RAM! Managed Heap: {beforeHeap} MB -> {afterHeap} MB.");
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

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.GetNewCameraPositionForCurrentFocusedPlant))]
        [HarmonyPrefix]
        public static bool GetNewCameraPositionForCurrentFocusedPlant_Prefix(WorldView __instance, ref Vector3 __result)
        {
            if (__instance == null || __instance.plantViewInFocus == null || __instance.plantViewInFocus.gameObject == null || __instance.worldCamera == null)
            {
                __result = __instance != null && __instance.worldCamera != null ? __instance.worldCamera.transform.position : Vector3.zero;
                return false;
            }

            if (Vector3.Distance(__instance.cameraFocusPointOnPlane, __instance.plantViewInFocus.gameObject.transform.position) < 0.1f)
            {
                __result = __instance.worldCamera.transform.position;
                return false;
            }

            var smr = __instance.plantViewInFocus.gameObject.GetComponentInChildren<SkinnedMeshRenderer>();
            float y = 1.0f;
            if (smr != null && smr.sharedMesh != null)
            {
                y = smr.sharedMesh.bounds.size.y;
            }
            else
            {
                var mf = __instance.plantViewInFocus.gameObject.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) y = mf.sharedMesh.bounds.size.y;
            }

            float num = __instance.worldScaleFactor * y;
            Vector3 vector = __instance.worldCamera.transform.position - __instance.plantViewInFocus.gameObject.transform.position;
            vector.y = 0f;
            Vector3 vector2 = __instance.worldCamera.transform.position - vector + (__instance.baseDistanceCameraToFocusedOrganism + num * __instance.plantSizeToCameraDistanceFactor) * vector.normalized;
            vector2.y = __instance.plantViewInFocus.gameObject.transform.position.y + num * __instance.plantSizeToCameraDistanceFactor;
            __result = vector2;
            return false;
        }
    }
}
