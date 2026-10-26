using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Phase 1 Systematic Debugging Instrumentation.
    /// Captures empirical timing data for every candidate bottleneck during camera movement.
    /// Pinpoints the exact root cause of camera move freezes.
    /// </summary>
    [HarmonyPatch]
    public static class DiagnosticsProfiler
    {
        private static readonly Stopwatch _frameSw = new Stopwatch();

        // 1. Frame Hitch Watchdog: Detects any frame that takes > 40ms (freezes)
        [HarmonyPatch(typeof(WorldView), "Update")]
        [HarmonyPrefix]
        public static void WorldView_Update_Start()
        {
            _frameSw.Reset();
            _frameSw.Start();
        }

        [HarmonyPatch(typeof(WorldView), "Update")]
        [HarmonyPostfix]
        public static void WorldView_Update_End()
        {
            _frameSw.Stop();
            long ms = _frameSw.ElapsedMilliseconds;
            if (ms >= 40)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER HITCH] WorldView.Update took {ms} ms! GC Heap: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
        }

        // 2. Measure CreateOrganismsAroundCameraJIT
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPrefix]
        public static void JIT_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPostfix]
        public static void JIT_Postfix(Stopwatch __state)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 15)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER JIT] CreateOrganismsAroundCameraJIT took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 3. Measure Camera Mouse Drag
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.MoveTheCameraSoPositionAIsSeenAtPositionB))]
        [HarmonyPrefix]
        public static void Drag_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.MoveTheCameraSoPositionAIsSeenAtPositionB))]
        [HarmonyPostfix]
        public static void Drag_Postfix(Stopwatch __state)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 20)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER DRAG] MoveTheCamera took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 4. Measure Grass Tessellation
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateTesselationDetailForGrassMaterial))]
        [HarmonyPrefix]
        public static void Tess_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateTesselationDetailForGrassMaterial))]
        [HarmonyPostfix]
        public static void Tess_Postfix(Stopwatch __state)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 10)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER TESS] UpdateTesselation took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 5. Measure Hover Actions
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.AddHoverActionsToNewlyGeneratedGameObjects))]
        [HarmonyPrefix]
        public static void Hover_Prefix(out Stopwatch __state, GameObject[] gameObjectsNewlyGenerated)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldController), nameof(WorldController.AddHoverActionsToNewlyGeneratedGameObjects))]
        [HarmonyPostfix]
        public static void Hover_Postfix(Stopwatch __state, GameObject[] gameObjectsNewlyGenerated)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 15)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER HOVER] AddHoverActions ({gameObjectsNewlyGenerated?.Length} objs) took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 6. Measure Plant JIT job
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateJITOptimizationJobForPlant))]
        [HarmonyPrefix]
        public static void PlantJIT_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateJITOptimizationJobForPlant))]
        [HarmonyPostfix]
        public static void PlantJIT_Postfix(Stopwatch __state, int areaIndex, int cellIndex)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 20)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER PLANT] Cell {cellIndex} plant build took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 7. Measure Animal JIT job
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateJITOptimizationJobForAnimalView))]
        [HarmonyPrefix]
        public static void AnimalJIT_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateJITOptimizationJobForAnimalView))]
        [HarmonyPostfix]
        public static void AnimalJIT_Postfix(Stopwatch __state, AnimalView animalView)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 20)
            {
                UnityEngine.Debug.LogWarning($"[PROFILER ANIMAL] Animal {animalView?.definition?.definitionIndex} build took {__state.ElapsedMilliseconds} ms!");
            }
        }
    }
}
