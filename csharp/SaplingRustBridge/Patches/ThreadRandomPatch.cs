using System;
using System.Threading;
using HarmonyLib;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Makes UnityEngine.Random safe to call from background worker threads (like AsyncJumpWorker).
    /// Unity's native Random icalls throw UnityException when called outside the main thread.
    /// This patch redirects background thread calls to a ThreadStatic System.Random instance,
    /// while main-thread calls continue using vanilla Unity Random.
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
