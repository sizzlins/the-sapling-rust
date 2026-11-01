using System;
using System.Threading;
using HarmonyLib;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// UNWIRED (kept for reference). Makes UnityEngine.Random safe to call from background
    /// threads. It was only needed by AsyncJumpWorker, which is no longer started, so Plugin
    /// does not register this class. Keeping it out of the patch list also avoids silently
    /// replacing vanilla RNG behaviour on any background thread the game itself might own.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Random))]
    public static class ThreadRandomPatch
    {
        public static int MainThreadId = -1;

        [ThreadStatic]
        private static System.Random _threadRandom;

        private static System.Random Rng => _threadRandom ?? (_threadRandom = new System.Random());

        [HarmonyPatch(nameof(UnityEngine.Random.Range), typeof(int), typeof(int))]
        [HarmonyPrefix]
        public static bool RangeInt_Prefix(int min, int max, ref int __result)
        {
            if (MainThreadId != -1 && Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                if (min >= max)
                {
                    __result = min;
                    return false;
                }
                __result = Rng.Next(min, max);
                return false;
            }
            return true;
        }

        [HarmonyPatch(nameof(UnityEngine.Random.Range), typeof(float), typeof(float))]
        [HarmonyPrefix]
        public static bool RangeFloat_Prefix(float min, float max, ref float __result)
        {
            if (MainThreadId != -1 && Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                __result = min + (float)Rng.NextDouble() * (max - min);
                return false;
            }
            return true;
        }

        [HarmonyPatch(nameof(UnityEngine.Random.value), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool Value_Prefix(ref float __result)
        {
            if (MainThreadId != -1 && Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                __result = (float)Rng.NextDouble();
                return false;
            }
            return true;
        }
    }
}
