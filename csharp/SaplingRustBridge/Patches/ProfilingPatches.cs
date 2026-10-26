using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Temporary profiling patches to find what's actually causing the freeze
    /// when camera moves into a populated area.
    /// </summary>
    [HarmonyPatch]
    public static class ProfilingPatches
    {
        private static readonly Stopwatch _sw = new Stopwatch();

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowState))]
        [HarmonyPrefix]
        public static void ShowState_Pre() { _sw.Reset(); _sw.Start(); }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowState))]
        [HarmonyPostfix]
        public static void ShowState_Post()
        {
            _sw.Stop();
            if (_sw.ElapsedMilliseconds > 5)
                UnityEngine.Debug.LogWarning("[PROF] ShowState: " + _sw.ElapsedMilliseconds + "ms");
        }

        private static readonly Stopwatch _swAnimal = new Stopwatch();
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyPrefix]
        public static void ShowAnimal_Pre() { _swAnimal.Reset(); _swAnimal.Start(); }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyPostfix]
        public static void ShowAnimal_Post()
        {
            _swAnimal.Stop();
            if (_swAnimal.ElapsedMilliseconds > 5)
                UnityEngine.Debug.LogWarning("[PROF] ShowAnimalViewsForArea: " + _swAnimal.ElapsedMilliseconds + "ms");
        }

        private static readonly Stopwatch _swJIT = new Stopwatch();
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPrefix]
        public static void JIT_Pre() { _swJIT.Reset(); _swJIT.Start(); }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.CreateOrganismsAroundCameraJIT))]
        [HarmonyPostfix]
        public static void JIT_Post()
        {
            _swJIT.Stop();
            if (_swJIT.ElapsedMilliseconds > 5)
                UnityEngine.Debug.LogWarning("[PROF] CreateOrganismsAroundCameraJIT: " + _swJIT.ElapsedMilliseconds + "ms");
        }

        private static readonly Stopwatch _swInit = new Stopwatch();
        private static int _initCount = 0;
        private static int _initFrame = -1;

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.InitializeAnimalView),
            new[] { typeof(AnimalView), typeof(AnimalDefinition), typeof(Vector3),
                    typeof(CellLocationType), typeof(bool) })]
        [HarmonyPrefix]
        public static void InitAnimal_Pre()
        {
            if (Time.frameCount != _initFrame)
            {
                if (_initCount > 0 && _swInit.ElapsedMilliseconds > 5)
                    UnityEngine.Debug.LogWarning("[PROF] InitializeAnimalView x" + _initCount + ": " + _swInit.ElapsedMilliseconds + "ms (prev frame)");
                _initFrame = Time.frameCount;
                _initCount = 0;
                _swInit.Reset();
                _swInit.Start();
            }
            _initCount++;
        }

        private static readonly Stopwatch _swUpdate = new Stopwatch();
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateGraphicsAfterCameraMovement))]
        [HarmonyPrefix]
        public static void UpdateGfx_Pre() { _swUpdate.Reset(); _swUpdate.Start(); }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateGraphicsAfterCameraMovement))]
        [HarmonyPostfix]
        public static void UpdateGfx_Post()
        {
            _swUpdate.Stop();
            if (_swUpdate.ElapsedMilliseconds > 5)
                UnityEngine.Debug.LogWarning("[PROF] UpdateGraphicsAfterCameraMovement: " + _swUpdate.ElapsedMilliseconds + "ms");
        }

        private static readonly Stopwatch _swBuild = new Stopwatch();
        private static int _buildCount = 0;
        private static int _buildFrame = -1;

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.BuildAndCacheAnimal))]
        [HarmonyPrefix]
        public static void Build_Pre()
        {
            if (Time.frameCount != _buildFrame)
            {
                if (_buildCount > 0)
                    UnityEngine.Debug.LogWarning("[PROF] BuildAndCacheAnimal x" + _buildCount + ": " + _swBuild.ElapsedMilliseconds + "ms (prev frame)");
                _buildFrame = Time.frameCount;
                _buildCount = 0;
                _swBuild.Reset();
                _swBuild.Start();
            }
            _buildCount++;
        }

        // Track UpdateAnimationsBetweenWorldStates
        private static readonly Stopwatch _swAnim = new Stopwatch();
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateAnimationsBetweenWorldStates))]
        [HarmonyPrefix]
        public static void Anim_Pre() { _swAnim.Reset(); _swAnim.Start(); }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateAnimationsBetweenWorldStates))]
        [HarmonyPostfix]
        public static void Anim_Post()
        {
            _swAnim.Stop();
            if (_swAnim.ElapsedMilliseconds > 5)
                UnityEngine.Debug.LogWarning("[PROF] UpdateAnimationsBetweenWorldStates: " + _swAnim.ElapsedMilliseconds + "ms");
        }
    }
}
