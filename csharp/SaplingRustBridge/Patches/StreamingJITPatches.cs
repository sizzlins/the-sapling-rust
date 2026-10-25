using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Eliminates the half-second camera movement freeze when entering crowded areas.
    /// 
    /// Root cause: WorldView.CreateOrganismsAroundCameraJIT() was synchronously running 10 concentric
    /// loops of distance queries (checking the same cells up to 10 times in a single frame), synchronously
    /// building up to 50+ procedural 3D meshes and bone armatures at once on the main thread.
    /// 
    /// Solution:
    /// 1. Time-slice / frame-budget organism construction (max 2-3 organisms generated per frame).
    /// 2. Use non-overlapping distance bands instead of re-scanning inner cells 10 times.
    /// 3. Stream remaining rings smoothly across consecutive frames at a solid 60 FPS.
    /// </summary>
    [HarmonyPatch]
    public static class StreamingJITPatches
    {
        private static Vector3 _lastCamPos = new Vector3(-9999f, -9999f, -9999f);
        private static int _currentRing = 11; // 11 means all 10 rings are completed
        private static int _cachedAreaIndex = -1;
        private static int _cachedCellIndex = -1;

        private const int MaxBuildsPerFrame = 2; // Keep each frame under 16ms
        private const int MaxDistanceRing = 10;

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPrefix]
        public static bool CreateOrganismsAroundCameraJIT_Prefix(WorldView __instance)
        {
            if (__instance == null || __instance.worldCamera == null || __instance.cellDistanceFinder == null)
            {
                return true;
            }

            Vector3 camPos = __instance.worldCamera.transform.position;
            float moveDistSq = (camPos - _lastCamPos).sqrMagnitude;

            // If camera moved significantly (>= 0.5 world units), reset stream from center outward
            if (moveDistSq >= 0.25f)
            {
                _lastCamPos = camPos;
                _currentRing = 1;
                _cachedAreaIndex = __instance.GetAreaIndexBelowCamera();
                _cachedCellIndex = __instance.GetCellIndexBelowCamera();
            }

            StepJITStreaming(__instance);

            // Skip the original unbudgeted 10-ring freeze
            return false;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.Update))]
        [HarmonyPostfix]
        public static void WorldView_Update_Postfix(WorldView __instance)
        {
            // If there are still rings waiting to stream in after the camera stops moving, continue streaming smoothly
            if (_currentRing <= MaxDistanceRing && __instance != null && __instance.cellDistanceFinder != null)
            {
                StepJITStreaming(__instance);
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

            // Distance 0 (center cell)
            if (_currentRing == 1)
            {
                view.CreateJITOptimizationJobForAlga(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForPlant(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForFungus(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForAnimal(areaIndex, cellIndex);
            }

            // Stream rings outward from currentRing up to MaxDistanceRing
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

                        // If we've reached our budget for this frame, yield to the next frame!
                        if (view.gameObjectsNewlyGenerated.Count - initialCount >= MaxBuildsPerFrame)
                        {
                            break;
                        }
                    }
                }

                _currentRing++;

                // Yield to next frame if budget exhausted
                if (view.gameObjectsNewlyGenerated.Count - initialCount >= MaxBuildsPerFrame)
                {
                    break;
                }
            }

            if (view.organismObjectPool != null)
            {
                view.organismObjectPool.BillboardCleanup();
            }

            // Trigger hover action callback if new objects were created
            if (view.gameObjectsNewlyGenerated.Count > initialCount && view.newOrganismsCreatedJITAfterCameraMovement != null)
            {
                view.newOrganismsCreatedJITAfterCameraMovement.Call(new object[0]);
            }
        }
    }
}
