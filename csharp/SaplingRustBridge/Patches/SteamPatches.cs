using System;
using System.Collections.Generic;
using HarmonyLib;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Suppresses Steam achievement IPC while a time jump is running.
    ///
    /// The daily simulation reaches Steam through two ICallable callbacks wired up in
    /// WorldController:
    ///   * playedALongTimeCallBack - fired EVERY day once dayIndex >= longTimeThreshold (5000).
    ///     In sandbox it calls SteamEvents.UnlockAchive("AdventurousArchitect").
    ///   * animalGetsEatenCallBack - fired on EVERY successful predation. In sandbox it calls
    ///     SteamEvents.UnlockAchive("CourageousCarnivores").
    ///
    /// Both end up in SteamUserStats.SetAchievement + StoreStats(), which is synchronous IPC to
    /// the Steam client. A super jump simulates 875,000 days, so that is hundreds of thousands of
    /// Steam calls that can never change the outcome after the first one.
    ///
    /// During a jump we record the requested achievement names and replay them once when the jump
    /// finishes, so the achievement is still unlocked and stats are still stored exactly once.
    /// This class is kept separate from TimeJumpPatches on purpose: if the Steamworks type is
    /// missing the class processor fails only here, and the time-jump patches stay active.
    /// </summary>
    [HarmonyPatch]
    public static class SteamPatches
    {
        private static readonly HashSet<string> _pending = new HashSet<string>();

        [HarmonyPatch(typeof(SteamEvents), nameof(SteamEvents.UnlockAchive))]
        [HarmonyPrefix]
        public static bool SteamEvents_UnlockAchive_Prefix(string achievement)
        {
            if (!TimeJumpPatches.IsTimeJumpActive) return true;

            if (!string.IsNullOrEmpty(achievement))
            {
                _pending.Add(achievement);
            }
            return false;
        }

        /// <summary>Replays achievements that were deferred during a jump. Safe to call anytime.</summary>
        public static void Flush()
        {
            if (_pending.Count == 0) return;

            string[] names = new string[_pending.Count];
            _pending.CopyTo(names);
            _pending.Clear();

            for (int i = 0; i < names.Length; i++)
            {
                try { SteamEvents.UnlockAchive(names[i]); }
                catch (Exception e) { Plugin.LogWarn("[Steam] deferred achievement failed: " + e.Message); }
            }
        }

        public static void Reset()
        {
            _pending.Clear();
        }
    }
}
