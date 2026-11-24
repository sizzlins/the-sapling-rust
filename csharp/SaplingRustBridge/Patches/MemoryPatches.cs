using System;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Memory governor and stability patches for The Sapling.
    /// 1. Shares AnimalTextureFactory across builders to eliminate duplicate GPU textures.
    /// 2. Periodic extinct-definition pruning.
    /// 3. Frees leaked red-flash Materials created whenever an animal takes damage.
    /// 4. Slims the historical timeline while loading a save (fixes the multi-GB load OOM).
    /// 5. Camera focus guards prevent NullReferenceException when focusing on organisms.
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
                // Disable pooled organisms instead of leaving them SetActive(true) in the sky,
                // so pooled Renderers/SkinnedMeshRenderers/Animators leave Unity's active scene.
                // Also expire unused pooled species after 15 years instead of 300.
                __instance.organismObjectPool.outOfSightInsteadOfDisabling = false;
                __instance.organismObjectPool.expireAfterYears = 15;
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

        // --- 3b. FREE LEAKED ANIMAL HURT-FLASH MATERIALS ---
        // WorldView.ShowAnimalViewsForArea allocates `new Material(Shader.Find("Standard"))`
        // every time an animal is hurt, and AnimalFlashingRed.Reset() only destroys the
        // MonoBehaviour without ever destroying redMaterial.
        [HarmonyPatch(typeof(AnimalFlashingRed), nameof(AnimalFlashingRed.Reset))]
        [HarmonyPostfix]
        public static void AnimalFlashingRed_Reset_FreeMaterial(AnimalFlashingRed __instance)
        {
            if (__instance != null && __instance.redMaterial != null)
            {
                UnityEngine.Object.Destroy(__instance.redMaterial);
                __instance.redMaterial = null;
            }
        }

        // --- 4. DEEP MEMORY TRIM (invocable via RCON) ---
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

        // --- 5. SLIM THE HISTORICAL TIMELINE WHILE LOADING A SAVE ---
        // GameSaver.Decode() rebuilds one WorldState per simulated day: a 878k-day save means
        // 878k WorldState objects. Measured from the load probe, WorldState's constructor costs
        // ~28.3 KB of committed memory per instance (39 Dictionary<int,Travel>/<int,int>, 9
        // Hashtables and 4 Lists, plus Mono's array overhead), and none of it is ever read again.
        // The running game already discards them: TimeLine.LiveADay() does
        // "line[line.Count - 2] = null" and appends the new day, so at rest only the last two
        // entries ever hold data. Skipping the historical ones keeps line.Count (and therefore
        // dayIndex and species indices) identical while removing the multi-GB allocation spike
        // that OOMs the load.
        private const long BytesPerWorldState = 28300L;
        private static long _skippedWorldStates = 0;

        [HarmonyPatch(typeof(GameSaver), "GetWorldState")]
        [HarmonyPrefix]
        public static bool GameSaver_GetWorldState_Prefix(GameSaver __instance)
        {
            // IMPORTANT: GameSaver derives from MonoBehaviour, but the game constructs it with
            // "new GameSaver()" (OverallController.cs:306, SaveGameController.cs:1900/1991/...),
            // so m_CachedPtr stays zero and Unity's overloaded == operator reports the live
            // object as null. ReferenceEquals tests the managed reference, which is what we
            // actually care about. Using "== null" here made every call take the early return,
            // so all 878k WorldStates were still built and the load still OOM'd.
            if (ReferenceEquals(__instance, null)) return true;

            int dayIndex = __instance.dayIndex;

            // Keep the last two entries, matching the steady-state shape LiveADay() leaves
            // behind: index dayIndex - 1 is TimeLine.today after the load, and dayIndex - 2 is
            // the "yesterday" that WorldController reads as line[line.Count - 2] and hands to
            // WorldView.UpdateAnimationsBetweenWorldStates, which dereferences its .areas
            // without a null check.
            if (dayIndex <= 2 || __instance.worldStates == null || __instance.worldStates.Count >= dayIndex - 2)
            {
                return true;
            }

            // Still consume the four (count, values...) blocks so the stream stays aligned.
            for (int group = 0; group < 4; group++)
            {
                int count = __instance.GetHighInteger();
                for (int i = 0; i < count; i++)
                {
                    __instance.GetHighInteger();
                }
            }

            _skippedWorldStates++;
            // Returning false without touching __result leaves the return value at its
            // default, which is null for a reference type: exactly the skipped marker.
            return false;
        }

        [HarmonyPatch(typeof(GameSaver), "Decode")]
        [HarmonyPrefix]
        public static void GameSaver_Decode_Prefix()
        {
            _skippedWorldStates = 0;
        }

        [HarmonyPatch(typeof(GameSaver), "Decode")]
        [HarmonyPostfix]
        public static void GameSaver_Decode_Postfix(GameSaver __instance)
        {
            if (_skippedWorldStates <= 0 || ReferenceEquals(__instance, null)) return;

            Plugin.LogWarn($"[MEMORY] Skipped {_skippedWorldStates} historical WorldState objects (~{_skippedWorldStates * BytesPerWorldState / 1048576L} MB) while loading day {__instance.dayIndex}.");
            _skippedWorldStates = 0;
        }

        // --- 6. SAFE CAMERA FOCUS GUARD (Eliminates NullReferenceException when clicking animals) ---
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
