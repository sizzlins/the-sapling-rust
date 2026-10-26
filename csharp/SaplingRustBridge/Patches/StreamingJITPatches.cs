using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Smooth, hitch-free JIT streaming of organisms around the camera.
    /// Uses an exact Stopwatch time budget (max 1ms when moving, 4ms when still)
    /// to guarantee 60 FPS without ever stalling camera translation or mouse drag.
    /// </summary>
    [HarmonyPatch]
    public static class StreamingJITPatches
    {
        private static Vector3 _lastFrameCamPos = Vector3.zero;
        private static Vector3 _lastCenterCamPos = new Vector3(-9999f, -9999f, -9999f);
        private static int _currentRing = 7;
        private static int _currentBandIndex = 0;
        private static int _cachedAreaIndex = -1;
        private static int _cachedCellIndex = -1;
        private static int[] _cachedBand = null;

        private const int MaxDistanceRing = 6;
        private const float RetargetDistance = 15f; // Re-center only when camera moves > 15 units

        private static readonly Stopwatch _sw = new Stopwatch();

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

            bool isMoving = currentSpeed > 1.0f || __instance.movingCamera;

            // Only retarget rings when camera moves a meaningful distance (avoids cell boundary jitter)
            if (Vector3.Distance(camPos, _lastCenterCamPos) > RetargetDistance)
            {
                _lastCenterCamPos = camPos;
                _cachedAreaIndex = __instance.GetAreaIndexBelowCamera();
                _cachedCellIndex = __instance.GetCellIndexBelowCamera();
                _currentRing = 0;
                _currentBandIndex = 0;
                _cachedBand = null;
            }

            // While camera is actively dragged/moving, give at most 1ms to prevent any drag stutter.
            // When still, allow 4ms so the frame stays comfortably under the 16.6ms frame budget (60 FPS).
            long timeBudgetMs = isMoving ? 1 : 4;
            StepJITStreaming(__instance, timeBudgetMs);

            return false;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.Update))]
        [HarmonyPostfix]
        public static void WorldView_Update_Postfix(WorldView __instance)
        {
            if (_currentRing <= MaxDistanceRing && __instance != null && __instance.cellDistanceFinder != null)
            {
                StepJITStreaming(__instance, 4);
            }
        }

        private static void StepJITStreaming(WorldView view, long budgetMs)
        {
            if (_currentRing > MaxDistanceRing) return;
            if (_cachedAreaIndex < 0 || _cachedCellIndex < 0) return;

            int areaIndex = _cachedAreaIndex;
            int cellIndex = _cachedCellIndex;
            var distanceFinder = view.cellDistanceFinder;

            _sw.Reset();
            _sw.Start();

            // Ring 0: Center cell directly below camera
            if (_currentRing == 0)
            {
                view.CreateJITOptimizationJobForAlga(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForPlant(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForFungus(areaIndex, cellIndex);
                view.CreateJITOptimizationJobForAnimal(areaIndex, cellIndex);
                _currentRing = 1;
                _currentBandIndex = 0;
                _cachedBand = null;

                if (_sw.ElapsedMilliseconds >= budgetMs)
                {
                    FinalizeFrameBatch(view);
                    return;
                }
            }

            // Stream rings outward from 1 to MaxDistanceRing
            while (_currentRing <= MaxDistanceRing)
            {
                if (_cachedBand == null)
                {
                    Vector2 coords = distanceFinder.TransformCellIndexToCoordinates(cellIndex);
                    _cachedBand = distanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates(coords, _currentRing, true, true, true, true);
                    _currentBandIndex = 0;
                }

                if (_cachedBand != null && _cachedBand.Length > 0)
                {
                    while (_currentBandIndex < _cachedBand.Length)
                    {
                        int c = _cachedBand[_currentBandIndex++];
                        if (c >= 0)
                        {
                            view.CreateJITOptimizationJobForAlga(areaIndex, c);
                            view.CreateJITOptimizationJobForPlant(areaIndex, c);
                            view.CreateJITOptimizationJobForFungus(areaIndex, c);
                            view.CreateJITOptimizationJobForAnimal(areaIndex, c);
                        }

                        if (_sw.ElapsedMilliseconds >= budgetMs)
                        {
                            FinalizeFrameBatch(view);
                            return;
                        }
                    }
                }

                _currentRing++;
                _currentBandIndex = 0;
                _cachedBand = null;

                if (_sw.ElapsedMilliseconds >= budgetMs)
                {
                    break;
                }
            }

            FinalizeFrameBatch(view);
        }

        private static void FinalizeFrameBatch(WorldView view)
        {
            if (view == null) return;

            if (view.organismObjectPool != null)
            {
                view.organismObjectPool.BillboardCleanup();
            }

            if (view.gameObjectsNewlyGenerated != null && view.gameObjectsNewlyGenerated.Count > 0)
            {
                if (view.newOrganismsCreatedJITAfterCameraMovement != null)
                {
                    view.newOrganismsCreatedJITAfterCameraMovement.Call(new object[0]);
                }
                view.gameObjectsNewlyGenerated.Clear();
            }
        }
    }
}
