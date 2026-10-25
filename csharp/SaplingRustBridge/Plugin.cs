using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge
{
    [BepInPlugin("com.sizzlins.saplingrust", "SaplingRustBridge", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private IntPtr _rustWorld = IntPtr.Zero;
        private Harmony _harmony;

        void Awake()
        {
            // Disable expensive C# stack trace string allocations on benign warnings and logs
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Debug.unityLogger.filterLogType = LogType.Warning;

            // Turn on "continuous" cheatcode by default so the game keeps running when alt-tabbed
            Application.runInBackground = true;

            Logger.LogInfo("SaplingRustBridge: Initializing Native Rust Engine...");

            try
            {
                // Test native Rust connection
                _rustWorld = NativeSim.sapling_world_new(42);
                if (_rustWorld != IntPtr.Zero)
                {
                    NativeSim.sapling_world_populate(_rustWorld, 50, 10, 100.0f);
                    NativeSim.sapling_world_counts(_rustWorld, out uint p, out uint h);
                    Logger.LogInfo($"SaplingRustBridge: Native Rust connected! Initialized world with {p} plants and {h} herbivores.");
                }
                else
                {
                    Logger.LogError("SaplingRustBridge: Failed to allocate native Rust world!");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"SaplingRustBridge: P/Invoke failed: {ex.Message}\n{ex.StackTrace}");
            }

            // Initialize Harmony
            try
            {
                _harmony = new Harmony("com.sizzlins.saplingrust");
                _harmony.PatchAll();
                Logger.LogInfo("SaplingRustBridge: Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"SaplingRustBridge: Harmony patching failed: {ex.Message}");
            }
        }

        void OnDestroy()
        {
            if (_rustWorld != IntPtr.Zero)
            {
                NativeSim.sapling_world_free(_rustWorld);
                _rustWorld = IntPtr.Zero;
                Logger.LogInfo("SaplingRustBridge: Native Rust world freed.");
            }

            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
