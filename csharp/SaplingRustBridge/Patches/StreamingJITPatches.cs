using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Smooth, hitch-free JIT streaming of organisms around the camera.
    /// Replaces the unbudgeted 10-ring concentric scan with a frame-budgeted ring streamer.
    /// </summary>
    [HarmonyPatch]
    public static class StreamingJITPatches
    {
        private static Vector3 _lastFrameCamPos = Vector3.zero;
        private static int _currentRing = 7;
        private static int _cachedAreaIndex = -1;
        private static int _cachedCellIndex = -1;

        private const int MaxDistanceRing = 6;
        private const int MaxBuildsPerFrameMoving = 3;
        private const int MaxBuildsPerFrameStill = 8;

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPrefix]
        public static bool CreateOrganismsAroundCameraJIT_Prefix(WorldView __instance)
        {
            if (__instance == null || __instance.worldCamera == null || __instance.cellDistanceFinder == null)
            {
                return true;
            }

            Vector3 camPos = __instance.worldCamera.transform.position;
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            float currentSpeed = (camPos - _lastFrameCamPos).magnitude / dt;
            _lastFrameCamPos = camPos;

            bool isMoving = currentSpeed > 1.5f || __instance.movingCamera;
            int budget = isMoving ? MaxBuildsPerFrameMoving : MaxBuildsPerFrameStill;

            int currentArea = __instance.GetAreaIndexBelowCamera();
            int currentCell = __instance.GetCellIndexBelowCamera();

            // Only restart rings when the camera crosses into a new world cell
            if (currentArea != _cachedAreaIndex || currentCell != _cachedCellIndex)
            {
                _cachedAreaIndex = currentArea;
                _cachedCellIndex = currentCell;
                _currentRing = 0;
            }

            StepJITStreaming(__instance, budget);

            return false;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.Update))]
        [HarmonyPostfix]
        public static void WorldView_Update_Postfix(WorldView __instance)
        {
            if (_currentRing <= MaxDistanceRing && __instance != null && __instance.cellDistanceFinder != null)
            {
                StepJITStreaming(__instance, MaxBuildsPerFrameStill);
            }
        }

        private static void StepJITStreaming(WorldView view, int budget)
        {
            if (_currentRing > MaxDistanceRing) return;
            if (_cachedAreaIndex < 0 || _cachedCellIndex < 0) return;

            int areaIndex = _cachedAreaIndex;
            int cellIndex = _cachedCellIndex;
            int initialCount = view.gameObjectsNewlyGenerated.Count;
            var distanceFinder = view.cellDistanceFinder;

            // Ring 0: Center cell directly below camera
            if (_currentRing == 0)
            {
                view.CreateJITOptimizationJobForAlga(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForPlant(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForFungus(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForAnimal(areaIndex, cellIndex);
                _currentRing++;

                if (view.gameObjectsNewlyGenerated.Count - initialCount >= budget)
                {
                    FinalizeFrameBatch(view, initialCount);
                    return;
                }
            }

            // Stream rings outward from 1 to MaxDistanceRing
            while (_currentRing <= MaxDistanceRing)
            {
                int dist = _currentRing;
                Vector2 coords = distanceFinder.TransformCellIndexToCoordinates(cellIndex);
                int[] band = distanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates(coords, dist, true, true, true, true);

                if (band != null)
                {
                    for (int i = 0; i < band.Length; i++)
                    {
                        int c = band[i];
                        if (c < 0) continue;

                        view.CreateJITOptimizationJobForAlga(areaIndex, c);
                        view.CreateJITOptimizationJobForPlant(areaIndex, c);
                        view.CreateJITOptimizationJobForFungus(areaIndex, c);
                        view.CreateJITOptimizationJobForAnimal(areaIndex, c);

                        if (view.gameObjectsNewlyGenerated.Count - initialCount >= budget)
                        {
                            FinalizeFrameBatch(view, initialCount);
                            return;
                        }
                    }
                }

                _currentRing++;

                if (view.gameObjectsNewlyGenerated.Count - initialCount >= budget)
                {
                    break;
                }
            }

            FinalizeFrameBatch(view, initialCount);
        }

        private static void FinalizeFrameBatch(WorldView view, int initialCount)
        {
            if (view.organismObjectPool != null)
            {
                view.organismObjectPool.BillboardCleanup();
            }

            if (view.gameObjectsNewlyGenerated.Count > initialCount && view.newOrganismsCreatedJITAfterCameraMovement != null)
            {
                view.newOrganismsCreatedJITAfterCameraMovement.Call(new object[0]);
            }
        }
    }
}
