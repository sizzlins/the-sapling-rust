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

        private static readonly System.Diagnostics.Stopwatch _frameStopwatch = new System.Diagnostics.Stopwatch();
        private static readonly System.Diagnostics.Stopwatch _updateStopwatch = new System.Diagnostics.Stopwatch();
        private static int _lastGcCount = 0;

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

        void Update()
        {
            if (_frameStopwatch.IsRunning)
            {
                long totalFrameMs = _frameStopwatch.ElapsedMilliseconds;
                int gcCount = GC.CollectionCount(0);
                bool gcRan = gcCount != _lastGcCount;
                _lastGcCount = gcCount;

                if (totalFrameMs >= 40)
                {
                    long heapMb = GC.GetTotalMemory(false) / (1024 * 1024);
                    Logger.LogWarning($"[PROFILER FRAME HITCH] Full frame took {totalFrameMs} ms! (GC ran: {gcRan}, Heap: {heapMb} MB)");
                }
            }
            _frameStopwatch.Reset();
            _frameStopwatch.Start();

            _updateStopwatch.Reset();
            _updateStopwatch.Start();

            if (Input.GetKeyDown(KeyCode.F9))
            {
                Patches.MemoryPatches.TriggerDeepMemoryTrim();
            }
        }

        void LateUpdate()
        {
            _updateStopwatch.Stop();
            long updateMs = _updateStopwatch.ElapsedMilliseconds;
            if (updateMs >= 35)
            {
                Logger.LogWarning($"[PROFILER SCRIPT STALL] MonoBehaviours Update phase took {updateMs} ms!");
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
                typeof(Patches.DiagnosticsProfiler),
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
