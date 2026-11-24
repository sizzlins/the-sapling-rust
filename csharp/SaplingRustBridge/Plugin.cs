using System;
using System.Collections.Generic;
using System.Reflection;
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

            Logger.LogInfo("SaplingRustBridge: starting with managed patches + verified native distance band (sapling_rust.dll).");

            // Initialize Harmony with resilient per-class patching
            _harmony = new Harmony("com.sizzlins.saplingrust");

            // Automatic shadow-pass cap: 2 cascades and 100 m shadow distance cut shadow draw calls
            // in half on dense late-game worlds while keeping shadows crisp around the camera.
            if (QualitySettings.shadowDistance > 100f) QualitySettings.shadowDistance = 100f;
            if (QualitySettings.shadowCascades > 2) QualitySettings.shadowCascades = 2;

            Patches.BandPatches.VerifyManagedAgainstNative();

            Type[] patchTypes = new Type[]
            {
                typeof(Patches.MemoryPatches),
                typeof(Patches.LODPatches),
                typeof(Patches.StreamingJITPatches),
                typeof(Patches.SimulationPatches),
                typeof(Patches.TimeJumpPatches),
                typeof(Patches.SteamPatches),
                typeof(Patches.CleanLogPatches),
                typeof(Patches.BandPatches),
                typeof(Patches.FastSimPatches)
            };

            foreach (var t in patchTypes)
            {
                PatchClassResilient(t);
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

        private void PatchClassResilient(Type t)
        {
            int patched = 0;
            int failed = 0;
            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                Logger.LogError($"SaplingRustBridge: Inspecting {t.Name} failed: {ex.Message}");
                return;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                List<HarmonyMethod> attrs;
                try
                {
                    attrs = HarmonyMethodExtensions.GetFromMethod(m);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"SaplingRustBridge: Reading attributes on {t.Name}.{m.Name} failed: {ex.Message}");
                    failed++;
                    continue;
                }

                if (attrs == null || attrs.Count == 0) continue;

                HarmonyMethod info = HarmonyMethod.Merge(attrs);
                if (info.declaringType == null) continue;

                bool isPrefix = m.IsDefined(typeof(HarmonyPrefix), false);
                bool isPostfix = m.IsDefined(typeof(HarmonyPostfix), false);
                bool isTranspiler = m.IsDefined(typeof(HarmonyTranspiler), false);
                bool isFinalizer = m.IsDefined(typeof(HarmonyFinalizer), false);
                if (!isPrefix && !isPostfix && !isTranspiler && !isFinalizer) continue;

                try
                {
                    MethodBase original = ResolveOriginalMethod(info);
                    if (original == null)
                    {
                        Logger.LogError($"SaplingRustBridge: Target not found for {t.Name}.{m.Name} ({info.declaringType.Name}.{info.methodName}).");
                        failed++;
                        continue;
                    }

                    HarmonyMethod hm = new HarmonyMethod(m);
                    _harmony.Patch(
                        original,
                        prefix: isPrefix ? hm : null,
                        postfix: isPostfix ? hm : null,
                        transpiler: isTranspiler ? hm : null,
                        finalizer: isFinalizer ? hm : null,
                        ilmanipulator: null);
                    patched++;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"SaplingRustBridge: Patching {t.Name}.{m.Name} failed: {ex.Message}");
                    failed++;
                }
            }

            if (failed == 0)
            {
                Logger.LogInfo($"SaplingRustBridge: Patched {t.Name} successfully ({patched} patches).");
            }
            else
            {
                Logger.LogWarning($"SaplingRustBridge: Patched {t.Name} with {patched} ok, {failed} failed.");
            }
        }

        private static MethodBase ResolveOriginalMethod(HarmonyMethod info)
        {
            MethodType kind = info.methodType ?? MethodType.Normal;
            switch (kind)
            {
                case MethodType.Constructor:
                    return AccessTools.Constructor(info.declaringType, info.argumentTypes ?? new Type[0]);
                case MethodType.StaticConstructor:
                    return AccessTools.Constructor(info.declaringType, null, true);
                case MethodType.Getter:
                    return AccessTools.PropertyGetter(info.declaringType, info.methodName);
                case MethodType.Setter:
                    return AccessTools.PropertySetter(info.declaringType, info.methodName);
                default:
                    return AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes);
            }
        }

        void OnDestroy()
        {
            RconServer.Stop();
            Patches.BandPatches.Cleanup();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
