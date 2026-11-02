using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge
{
    [BepInPlugin("com.sizzlins.saplingrust", "SaplingRustBridge", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }

        private Harmony _harmony;

        public static void LogWarn(string msg)
        {
            if (Instance != null && Instance.Logger != null)
            {
                Instance.Logger.LogWarning(msg);
            }
            else
            {
                UnityEngine.Debug.LogWarning(msg);
            }
        }

        public static void LogInfo(string msg)
        {
            if (Instance != null && Instance.Logger != null)
            {
                Instance.Logger.LogInfo(msg);
            }
            else
            {
                UnityEngine.Debug.Log(msg);
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Patches.MemoryPatches.TriggerDeepMemoryTrim();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                try { Patches.ProfilerPatches.Toggle(); } catch { }
            }

            try { Patches.ProfilerPatches.Tick(); } catch { }

            // Drain RCON requests here: the socket thread only enqueues, so every
            // Unity object is read on the main thread.
            try { RconServer.Tick(); } catch { }
        }

        void Awake()
        {
            Instance = this;

            // Disable expensive C# stack trace string allocations on benign warnings and logs
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Debug.unityLogger.filterLogType = LogType.Warning;

            // Turn on "continuous" cheatcode by default so the game keeps running when alt-tabbed
            Application.runInBackground = true;

            // The native Rust engine is deliberately NOT loaded. Only six FFI entry points were
            // ever called, and the only live one (a per-day 50-plant sandbox tick plus an
            // unbounded Vec<DeltaSnapshot> push) cost CPU and leaked heap without feeding
            // anything back into the game. NativeSim.cs / SpatialPatches.cs / AsyncJumpWorker.cs
            // / ThreadRandomPatch.cs are kept on disk for reference but are no longer wired up.
            Logger.LogInfo("SaplingRustBridge: starting in managed-only mode (native engine unwired).");

            // Initialize Harmony with resilient per-class patching
            _harmony = new Harmony("com.sizzlins.saplingrust");
            Type[] patchTypes = new Type[]
            {
                typeof(Patches.MemoryPatches),
                typeof(Patches.LODPatches),
                typeof(Patches.StreamingJITPatches),
                typeof(Patches.SimulationPatches),
                typeof(Patches.TimeJumpPatches),
                typeof(Patches.SteamPatches),
                typeof(Patches.CleanLogPatches)
            };

            foreach (var t in patchTypes)
            {
                try
                {
                    _harmony.CreateClassProcessor(t).Patch();
                    Logger.LogInfo($"SaplingRustBridge: Patched {t.Name} successfully.");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"SaplingRustBridge: Patching {t.Name} failed: {ex.Message}");
                }
            }

            try
            {
                Patches.ProfilerPatches.Install(_harmony);
            }
            catch (Exception ex)
            {
                Logger.LogError($"SaplingRustBridge: Profiler install failed (game is unaffected): {ex.Message}");
            }

            try
            {
                RconServer.AllowMutation = Config.Bind(
                    "Rcon",
                    "AllowMutation",
                    false,
                    "Allow mutating commands (kill_animals). Back up the save first.").Value;
                RconServer.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError($"SaplingRustBridge: RCON failed to start (game is unaffected): {ex.Message}");
            }
        }

        void OnDestroy()
        {
            RconServer.Stop();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
