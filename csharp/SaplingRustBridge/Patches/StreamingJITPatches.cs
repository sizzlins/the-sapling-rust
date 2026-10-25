using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Eliminates the camera movement freeze when entering or panning across crowded sectors.
    /// 
    /// Strategy:
    /// 1. While the camera is actively panning or dragging, defer heavy procedural mesh generation.
    ///    This keeps camera navigation locked at 60 FPS without hitches.
    /// 2. When the camera settles or moves at inspecting speeds, stream in unbuilt organisms smoothly
    ///    at a strict budget of 1 build per frame.
    /// 3. Rings stream outward from center (radius 0 to 6), covering the visible viewport without
    ///    redundant 10x re-checking of inner cells.
    /// </summary>
    [HarmonyPatch]
    public static class StreamingJITPatches
    {
        private static Vector3 _lastFrameCamPos = Vector3.zero;
        private static Vector3 _settledCamPos = new Vector3(-9999f, -9999f, -9999f);
        private static int _currentRing = 7; // 7 means all rings (0..6) completed
        private static int _cachedAreaIndex = -1;
        private static int _cachedCellIndex = -1;
        private static float _timeCameraSettled = 0f;

        private const int MaxDistanceRing = 6;
        private const int MaxBuildsPerFrame = 1; // Strict 1 build per frame guarantees zero frame hitches

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

            // While camera is actively moving (> 3 units/sec), defer heavy procedural construction!
            if (currentSpeed > 3.0f || __instance.movingCamera)
            {
                _timeCameraSettled = 0f;
                return false; // Skip freeze completely during camera drag
            }

            // Camera is stationary or moving slowly
            _timeCameraSettled += dt;

            // Check if camera settled at a new location
            float distFromSettled = (camPos - _settledCamPos).sqrMagnitude;
            if (distFromSettled > 1.0f)
            {
                _settledCamPos = camPos;
                _currentRing = 0;
                _cachedAreaIndex = __instance.GetAreaIndexBelowCamera();
                _cachedCellIndex = __instance.GetCellIndexBelowCamera();
            }

            StepJITStreaming(__instance);

            return false;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.Update))]
        [HarmonyPostfix]
        public static void WorldView_Update_Postfix(WorldView __instance)
        {
            // When camera has settled and still has rings to stream in, tick one batch per frame
            if (_currentRing <= MaxDistanceRing && __instance != null && __instance.cellDistanceFinder != null)
            {
                if (!__instance.movingCamera)
                {
                    StepJITStreaming(__instance);
                }
            }
        }

        private static void StepJITStreaming(WorldView view)
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

                if (view.gameObjectsNewlyGenerated.Count - initialCount >= MaxBuildsPerFrame)
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

                        // If 1 organism was generated, yield immediately so this frame stays at 60 FPS
                        if (view.gameObjectsNewlyGenerated.Count - initialCount >= MaxBuildsPerFrame)
                        {
                            FinalizeFrameBatch(view, initialCount);
                            return;
                        }
                    }
                }

                _currentRing++;

                if (view.gameObjectsNewlyGenerated.Count - initialCount >= MaxBuildsPerFrame)
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
