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
        // 1. Measure WorldController.ShowToday (Daily visual update)
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.ShowToday))]
        [HarmonyPrefix]
        public static void ShowToday_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldController), nameof(WorldController.ShowToday))]
        [HarmonyPostfix]
        public static void ShowToday_Postfix(Stopwatch __state)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 20)
            {
                Plugin.LogWarn($"[PROFILER TODAY] WorldController.ShowToday took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 2. Measure TimeLine.LiveADay (Daily simulation tick)
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPrefix]
        public static void LiveADay_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LiveADay))]
        [HarmonyPostfix]
        public static void LiveADay_Postfix(Stopwatch __state)
        {
            if (!TimeJumpPatches.IsTimeJumpActive && __state != null && __state.ElapsedMilliseconds >= 20)
            {
                Plugin.LogWarn($"[PROFILER SIM] TimeLine.LiveADay took {__state.ElapsedMilliseconds} ms!");
            }
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllPlantsInAreaLiveADay))]
        [HarmonyPrefix]
        public static void Plants_Prefix(out Stopwatch __state) { __state = Stopwatch.StartNew(); }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllPlantsInAreaLiveADay))]
        [HarmonyPostfix]
        public static void Plants_Postfix(Stopwatch __state)
        {
            if (!TimeJumpPatches.IsTimeJumpActive && __state != null && __state.ElapsedMilliseconds >= 15)
                Plugin.LogWarn($"[PROFILER SIM-PLANTS] LetAllPlants took {__state.ElapsedMilliseconds} ms!");
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllAnimalsInAreaLiveADay))]
        [HarmonyPrefix]
        public static void Animals_Prefix(out Stopwatch __state) { __state = Stopwatch.StartNew(); }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllAnimalsInAreaLiveADay))]
        [HarmonyPostfix]
        public static void Animals_Postfix(Stopwatch __state)
        {
            if (!TimeJumpPatches.IsTimeJumpActive && __state != null && __state.ElapsedMilliseconds >= 15)
                Plugin.LogWarn($"[PROFILER SIM-ANIMALS] LetAllAnimals took {__state.ElapsedMilliseconds} ms!");
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllAlgaeInAreaLiveADay))]
        [HarmonyPrefix]
        public static void Algae_Prefix(out Stopwatch __state) { __state = Stopwatch.StartNew(); }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllAlgaeInAreaLiveADay))]
        [HarmonyPostfix]
        public static void Algae_Postfix(Stopwatch __state)
        {
            if (!TimeJumpPatches.IsTimeJumpActive && __state != null && __state.ElapsedMilliseconds >= 15)
                Plugin.LogWarn($"[PROFILER SIM-ALGAE] LetAllAlgae took {__state.ElapsedMilliseconds} ms!");
        }

        // 3. Measure WorldView.BuildAndCacheAnimal (Procedural animal generation from scratch)
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPrefix]
        public static void BuildAnimal_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPostfix]
        public static void BuildAnimal_Postfix(Stopwatch __state, AnimalDefinition animalDefinition)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 15)
            {
                Plugin.LogWarn($"[PROFILER BUILD-ANIMAL] BuildAndCacheAnimal (def {animalDefinition?.definitionIndex}) took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 4. Measure PlantObjectBuilder.Run (Procedural plant generation from scratch)
        [HarmonyPatch(typeof(PlantObjectBuilder), nameof(PlantObjectBuilder.Run))]
        [HarmonyPrefix]
        public static void BuildPlant_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(PlantObjectBuilder), nameof(PlantObjectBuilder.Run))]
        [HarmonyPostfix]
        public static void BuildPlant_Postfix(Stopwatch __state)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 15)
            {
                Plugin.LogWarn($"[PROFILER BUILD-PLANT] PlantObjectBuilder.Run took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 5. Measure WorldView.UpdateCameraPositionDuringGeneralMovement (Camera move step)
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateCameraPositionDuringGeneralMovement))]
        [HarmonyPrefix]
        public static void CamMove_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateCameraPositionDuringGeneralMovement))]
        [HarmonyPostfix]
        public static void CamMove_Postfix(Stopwatch __state)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 20)
            {
                Plugin.LogWarn($"[PROFILER CAM-MOVE] UpdateCameraPositionDuringGeneralMovement took {__state.ElapsedMilliseconds} ms!");
            }
        }

        // 6. Measure WorldController.AddHoverActionsToNewlyGeneratedGameObjects
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.AddHoverActionsToNewlyGeneratedGameObjects))]
        [HarmonyPrefix]
        public static void Hover_Prefix(out Stopwatch __state)
        {
            __state = Stopwatch.StartNew();
        }

        [HarmonyPatch(typeof(WorldController), nameof(WorldController.AddHoverActionsToNewlyGeneratedGameObjects))]
        [HarmonyPostfix]
        public static void Hover_Postfix(Stopwatch __state, GameObject[] gameObjectsNewlyGenerated)
        {
            if (__state != null && __state.ElapsedMilliseconds >= 15)
            {
                Plugin.LogWarn($"[PROFILER HOVER] AddHoverActions ({gameObjectsNewlyGenerated?.Length} objs) took {__state.ElapsedMilliseconds} ms!");
            }
        }
    }
}
