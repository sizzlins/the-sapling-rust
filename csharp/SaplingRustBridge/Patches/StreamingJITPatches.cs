using System;
using System.Collections.Generic;
using System.Diagnostics;
using Boo.Lang.Runtime;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// High-performance entity streaming and crowd optimization.
    ///
    /// Key engineering innovations:
    /// 1. VIEW CONE CULLING: Only spawns entities inside the camera's forward FOV cone.
    ///    Ignores the 180° blindspot behind the camera and extreme flanks.
    /// 2. DISTANCE HORIZON CULLING: Restricts active 3D meshes to 5 rings (~40m).
    ///    Distant creatures stay as ultra-lightweight billboards until approached.
    /// 3. TWO-PHASE DE-JIT:
    ///    - Phase 1 (~0.1ms): Clone model, position, activate immediately (zero pop-in).
    ///    - Phase 2 (staggered): Setup animations, IK, and materials smoothly across idle frames.
    /// 4. SPATIAL MOVEMENT DEADZONE: Only scans when the camera actually moves >1.5m or rotates >8°.
    ///    While stationary, CPU overhead drops to ZERO.
    /// 5. ANIMATION RATE OPTIMIZATION (ARO): Clamps cameraCloseDistanceThreshold to 45m (down from 350m).
    ///    Distant creatures disable expensive 60 FPS bone & IK updates.
    /// 6. BUFFER DEDUPLICATION: Clears gameObjectsNewlyGenerated after processing,
    ///    preventing thousands of redundant GetComponent calls on every frame of camera movement.
    /// </summary>
    [HarmonyPatch]
    public static class StreamingJITPatches
    {
        private static int _currentRing = 0;
        private static int _resumeIndexInRing = 0;

        private const int ViewRadius = 5;          // ~40m active 3D radius (down from 10)
        private const double BudgetMs = 1.5;        // Max 1.5ms per frame budget
        private const int MaxSpawnsPerFrame = 2;    // Smooth throttle per frame
        private const int NearThreshold = 3;        // Day-tick initial radius (down from 10)
        private const float AnimCloseThreshold = 45f; // Disable IK/anim beyond 45m (down from 350m)
        private const float AnimMidThreshold = 80f;

        private static readonly Stopwatch _sw = new Stopwatch();

        // ── Spatial deadzone tracking ──
        private static Vector3 _lastScanCamPos = Vector3.zero;
        private static Quaternion _lastScanCamRot = Quaternion.identity;
        private static bool _needsScan = true;

        // ── Phase 2 deferred setup queue ──
        private struct DeferredSetup
        {
            public WorldView worldView;
            public AnimalView animalView;
            public AnimalDefinition definition;
            public CellLocationType locationType;
        }
        private static readonly Queue<DeferredSetup> _deferredQueue = new Queue<DeferredSetup>();

        // ── 1. Threshold & Distance Clamps (Day-tick + Anim ARO) ──

        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_ClampDistances(WorldView __instance)
        {
            if (__instance == null) return;
            __instance.cameraCloseDistanceThreshold = AnimCloseThreshold;
            __instance.cameraMidDistanceThreshold = AnimMidThreshold;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyPrefix]
        public static void ClampAnimalThreshold(WorldView __instance)
        {
            if (__instance == null) return;
            _deferredQueue.Clear();
            __instance.cameraCloseDistanceThreshold = AnimCloseThreshold;
            __instance.cameraMidDistanceThreshold = AnimMidThreshold;

            if (__instance.animalJITOptimizationThreshold > NearThreshold)
                __instance.animalJITOptimizationThreshold = NearThreshold;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowPlantViewsForArea))]
        [HarmonyPrefix]
        public static void ClampPlantThreshold(WorldView __instance)
        {
            if (__instance == null) return;
            if (__instance.plantJITOptimizationThreshold > NearThreshold)
                __instance.plantJITOptimizationThreshold = NearThreshold;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAlgaViewsForArea))]
        [HarmonyPrefix]
        public static void ClampAlgaThreshold(WorldView __instance)
        {
            if (__instance == null) return;
            if (__instance.algaJITOptimizationThreshold > NearThreshold)
                __instance.algaJITOptimizationThreshold = NearThreshold;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowFungusViewsForArea))]
        [HarmonyPrefix]
        public static void ClampFungusThreshold(WorldView __instance)
        {
            if (__instance == null) return;
            if (__instance.algaJITOptimizationThreshold > NearThreshold)
                __instance.algaJITOptimizationThreshold = NearThreshold;
        }

        // ── 2. Phase 1: Lightweight Immediate Animal De-JIT (~0.1ms) ──

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateJITOptimizationJobForAnimalView))]
        [HarmonyPrefix]
        public static bool CreateJITOptimizationJobForAnimalView_Prefix(
            WorldView __instance,
            AnimalView animalView,
            CellLocationType locationType,
            ref JITOptimizationAction __result)
        {
            if (animalView == null || animalView.definition == null)
            {
                __result = JITOptimizationAction.None;
                return false;
            }

            try
            {
                AnimalDefinition definition = animalView.definition;
                int definitionIndex = definition.definitionIndex;
                int currentSex = definition.currentSex;

                int speciesIndex = RuntimeServices.UnboxInt32(
                    __instance.getSpeciesIndexForDefinitionIndex.Call(
                        new object[] { definitionIndex, OrganismType.Animal }));

                object speciesObj = __instance.getSpeciesForIndex.Call(new object[] { speciesIndex });
                if (!(speciesObj is SpeciesDefinition))
                    speciesObj = RuntimeServices.Coerce(speciesObj, typeof(SpeciesDefinition));
                SpeciesDefinition speciesDef = (SpeciesDefinition)speciesObj;

                // Get prebuilt clone (ModelSharingPatches guarantees instant fallback)
                GameObject clone = __instance.prebuiltAnimalLibrary.GetPrebuiltOrganismProfileBased(
                    definitionIndex, currentSex, speciesIndex,
                    speciesDef.libraryProfile, youngVersion: false);

                if (clone == null)
                {
                    // Fallback to vanilla if library is completely empty
                    return true;
                }

                // Recolor if profile-based
                if (__instance.prebuiltAnimalLibrary.lastUsedStorageApproach == StorageApproach.ProfileBased)
                {
                    __instance.recolorBasedOnSpeciesColor.Call(new object[] { clone, speciesIndex });
                }

                // ── Phase 1: Place model and make visible immediately (0.1ms) ──
                animalView.JITOptimized = false;
                animalView.gameObjectOldVersion = clone;
                animalView.gameObject = clone;
                animalView.definition = definition;

                clone.name = "Organism";
                clone.transform.localScale = animalView.GetModelScale(definition.bodySize, false);
                clone.transform.position = animalView.referencePosition
                    - animalView.GetHeightAboveGroundForGameObject(clone);
                clone.SetActive(true);

                __instance.gameObjectsNewlyGenerated.Add(clone);

                // ── Phase 2: Queue expensive animation / IK setup for staggered idle frames ──
                _deferredQueue.Enqueue(new DeferredSetup
                {
                    worldView = __instance,
                    animalView = animalView,
                    definition = definition,
                    locationType = locationType
                });

                __result = JITOptimizationAction.UseLibrary;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[SaplingRust] Phase1 error, falling back to vanilla: " + e.Message);
                return true;
            }

            return false;
        }

        // ── 3. Frame Update: Process Phase 2 queue & stream when moving ──

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.Update))]
        [HarmonyPostfix]
        public static void Update_ProcessDeferredAndStream(WorldView __instance)
        {
            // Process up to 2 deferred setups per frame (smoothly spreads animation setup)
            int processed = 0;
            while (_deferredQueue.Count > 0 && processed < 2)
            {
                var item = _deferredQueue.Dequeue();
                processed++;
                try
                {
                    if (item.animalView != null && item.animalView.gameObject != null && item.worldView != null)
                    {
                        item.worldView.InitializeAnimalView(
                            item.animalView, item.definition,
                            item.animalView.referencePosition,
                            item.locationType, false);
                    }
                }
                catch { }
            }

            // Background streaming step if scan is in progress
            if (__instance != null && __instance.worldCamera != null && __instance.cellDistanceFinder != null)
            {
                CheckCameraMovement(__instance);
                if (_needsScan)
                {
                    StepJITStreaming(__instance);
                }
            }
        }

        // ── 4. Spatial Gating & View-Cone Streamer ──

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPrefix]
        public static bool CreateOrganismsAroundCameraJIT_Prefix(WorldView __instance)
        {
            if (__instance == null || __instance.worldCamera == null || __instance.cellDistanceFinder == null)
                return true;

            CheckCameraMovement(__instance);
            if (_needsScan)
            {
                StepJITStreaming(__instance);
            }
            return false;
        }

        private static void CheckCameraMovement(WorldView view)
        {
            Vector3 camPos = view.worldCamera.transform.position;
            Quaternion camRot = view.worldCamera.transform.rotation;

            float distSq = (camPos - _lastScanCamPos).sqrMagnitude;
            float angle = Quaternion.Angle(camRot, _lastScanCamRot);

            // Gating: only trigger new scan if camera moved >1.5m or rotated >8°
            if (distSq > 2.25f || angle > 8f)
            {
                _lastScanCamPos = camPos;
                _lastScanCamRot = camRot;
                _needsScan = true;
                _currentRing = 0;
                _resumeIndexInRing = 0;
            }
        }

        private static void StepJITStreaming(WorldView view)
        {
            int areaIndex = view.GetAreaIndexBelowCamera();
            int cellIndex = view.GetCellIndexBelowCamera();
            if (areaIndex < 0 || cellIndex < 0) return;

            int initialCount = view.gameObjectsNewlyGenerated.Count;
            var distanceFinder = view.cellDistanceFinder;

            // Camera forward vector projected onto the 2D plane (XZ)
            Vector3 camFwd3 = view.worldCamera.transform.forward;
            Vector2 camForward = new Vector2(camFwd3.x, camFwd3.z);
            float fwdLen = camForward.magnitude;
            if (fwdLen > 0.001f) camForward /= fwdLen;
            Vector2 camCoords = distanceFinder.TransformCellIndexToCoordinates(cellIndex);

            _sw.Reset();
            _sw.Start();

            // Ring 0: Cell directly underneath camera (always de-JIT immediately)
            if (_currentRing == 0)
            {
                view.CreateJITOptimizationJobForAlga(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForPlant(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForFungus(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForAnimal(areaIndex, cellIndex);
                _currentRing = 1;
                _resumeIndexInRing = 0;

                if (BudgetExhausted(view, initialCount))
                {
                    _sw.Stop();
                    FinalizeBillboards(view);
                    return;
                }
            }

            // Rings 1 to ViewRadius (~40m)
            while (_currentRing <= ViewRadius)
            {
                int[] band = distanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates(
                    camCoords, _currentRing, true, true, true, true);

                if (band != null)
                {
                    for (int i = _resumeIndexInRing; i < band.Length; i++)
                    {
                        int c = band[i];
                        if (c < 0) continue;

                        // ── View Cone Culling: Skip cells outside camera FOV ──
                        if (_currentRing > 1)
                        {
                            Vector2 cellCoords = distanceFinder.TransformCellIndexToCoordinates(c);
                            Vector2 dir = cellCoords - camCoords;
                            float dirLen = dir.magnitude;
                            if (dirLen > 0.001f)
                            {
                                float dot = (dir.x * camForward.x + dir.y * camForward.y) / dirLen;
                                // dot < 0.15f cuts off the rear 180° and extreme flanks (~160° forward cone)
                                if (dot < 0.15f) continue;
                            }
                        }

                        view.CreateJITOptimizationJobForAlga(areaIndex, c);
                        view.CreateJITOptimizationJobForPlant(areaIndex, c);
                        view.CreateJITOptimizationJobForFungus(areaIndex, c);
                        view.CreateJITOptimizationJobForAnimal(areaIndex, c);

                        if (BudgetExhausted(view, initialCount))
                        {
                            _resumeIndexInRing = i + 1;
                            _sw.Stop();
                            FinalizeBillboards(view);
                            return;
                        }
                    }
                }

                _currentRing++;
                _resumeIndexInRing = 0;

                if (_sw.Elapsed.TotalMilliseconds >= BudgetMs)
                    break;
            }

            // Sweep finished for this camera viewpoint
            if (_currentRing > ViewRadius)
            {
                _needsScan = false;
                _currentRing = 0;
                _resumeIndexInRing = 0;
            }

            _sw.Stop();
            FinalizeBillboards(view);
        }

        private static bool BudgetExhausted(WorldView view, int initialCount)
        {
            int spawned = view.gameObjectsNewlyGenerated.Count - initialCount;
            return spawned >= MaxSpawnsPerFrame || _sw.Elapsed.TotalMilliseconds >= BudgetMs;
        }

        private static void FinalizeBillboards(WorldView view)
        {
            if (view.organismObjectPool != null)
                view.organismObjectPool.BillboardCleanup();
        }

        // ── 5. Buffer Cleanup: Prevent Cumulative Leak ──
        // Clears gameObjectsNewlyGenerated after hover delegates are attached,
        // stopping thousand-item loops on every camera movement frame.

        [HarmonyPatch(typeof(WorldController), nameof(WorldController.AddHoverActionsToNewlyGeneratedGameObjects))]
        [HarmonyPostfix]
        public static void AddHoverActions_Postfix(WorldController __instance)
        {
            if (__instance != null && __instance.view != null && __instance.view.gameObjectsNewlyGenerated != null)
            {
                __instance.view.gameObjectsNewlyGenerated.Clear();
            }
        }
    }
}
