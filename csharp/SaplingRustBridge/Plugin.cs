using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge
{
    [BepInPlugin("com.sizzlins.saplingrust", "SaplingRustBridge", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static IntPtr RustWorldHandle => Instance != null ? Instance._rustWorld : IntPtr.Zero;

        private IntPtr _rustWorld = IntPtr.Zero;
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

        private float _lastAutoTrimTime = 0f;

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9) || (Time.realtimeSinceStartup - _lastAutoTrimTime > 60f))
            {
                _lastAutoTrimTime = Time.realtimeSinceStartup;
                Patches.MemoryPatches.TriggerDeepMemoryTrim();
            }
        }

        void Awake()
        {
            Instance = this;
            Patches.ThreadRandomPatch.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
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
                    NativeSim.sapling_world_populate(_rustWorld, 50, 0, 100.0f);
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

            // Initialize Harmony with resilient per-class patching
            _harmony = new Harmony("com.sizzlins.saplingrust");
            Type[] patchTypes = new Type[]
            {
                typeof(Patches.MemoryPatches),
                typeof(Patches.LODPatches),
                typeof(Patches.StreamingJITPatches),
                typeof(Patches.SimulationPatches),
                typeof(Patches.SpatialPatches),
                typeof(Patches.TimeJumpPatches),
                typeof(Patches.ThreadRandomPatch),
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
        }

        void OnDestroy()
        {
            if (_rustWorld != IntPtr.Zero)
            {
                NativeSim.sapling_world_free(_rustWorld);
                _rustWorld = IntPtr.Zero;
                Logger.LogInfo("SaplingRustBridge: Native Rust world freed.");
            }

            Patches.SpatialPatches.NativeGridManager.Free();

            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
