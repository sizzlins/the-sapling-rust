using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Frame/day profiler used to find the real hot spots instead of guessing at them.
    ///
    /// Every listed method is wrapped with a Stopwatch timestamp prefix and a postfix that
    /// accumulates inclusive wall time. A table is written every 5 seconds to the BepInEx log
    /// and to %LocalLow%\Wessel Stoop\The Sapling\sapling_profile.log (the BepInEx log is
    /// buffered and loses its tail on a hard crash, the append-only file does not).
    ///
    /// Times are INCLUSIVE: WorldView.Update contains the per-view Update calls below it, so
    /// nested rows overlap and the percentages do not sum to 100.
    /// </summary>
    public static class ProfilerPatches
    {
        private const int ReportIntervalMs = 5000;

        private sealed class Sample
        {
            public string Name;
            public long Ticks;
            public long Calls;
            public long MaxTicks;
        }

        // Harmony declares __state as a local of this type, so a struct costs no allocation and
        // lets the postfix skip a second dictionary lookup.
        private struct ProfState
        {
            public long Start;
            public Sample Sample;
        }

        private static bool _enabled = true;
        private static bool _installed = false;
        private static long _windowStart = 0;

        private static readonly Dictionary<MethodBase, Sample> _byMethod = new Dictionary<MethodBase, Sample>();
        private static readonly List<Sample> _samples = new List<Sample>();
        private static Sample _frameSample;
        private static Sample _daySample;

        private static string _logPath = null;
        private static bool _logPathResolved = false;

        // Frame-time distribution and GC pressure per window. The method table
        // alone cannot distinguish "uniformly slow" from "a few long stalls",
        // or "GC churn" from "real work" - these can.
        private static readonly List<float> _frameMs = new List<float>(4096);
        private static int _gc0AtStart;
        private static int _gc2AtStart;
        private static long _renderTicks;
        private static long _renderStart;
        private static int _windowCount;

        public static bool Enabled { get { return _enabled; } }

        public static void Toggle()
        {
            _enabled = !_enabled;
            _windowStart = _enabled ? Stopwatch.GetTimestamp() : 0;
            Plugin.LogWarn("[PROF] profiler " + (_enabled ? "ON" : "OFF"));
        }

        public static bool ProfileInnerLoops = false;

        public static void Install(Harmony harmony)
        {
            if (_installed) return;
            _installed = true;

            MethodInfo prefix = typeof(ProfilerPatches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(ProfilerPatches).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix == null || postfix == null)
            {
                Plugin.LogWarn("[PROF] could not resolve own prefix/postfix; profiler disabled.");
                _enabled = false;
                return;
            }

            // --- per-frame / per-day entry points ---
            _frameSample = Add(harmony, prefix, postfix, typeof(WorldView), "Update");
            _daySample = Add(harmony, prefix, postfix, typeof(TimeLine), "LiveADay");

            // Inner per-cell/per-plant methods run 100k-1.2M times/sec.
            // Only patch them when explicitly requested via ProfileInnerLoops = true,
            // otherwise the Harmony + Stopwatch trampoline itself burns significant CPU.
            if (ProfileInnerLoops)
            {
                Add(harmony, prefix, postfix, typeof(TimeLine), "SearchNearbyCellsForInstinctTrigger");
                Add(harmony, prefix, postfix, typeof(TimeLine), "CheckCellForVisualInstinctTrigger");
                Add(harmony, prefix, postfix, typeof(TimeLine), "CellContainsFoodTypeWithIndexForAnimal");
                Add(harmony, prefix, postfix, typeof(PlantView), "Update");
            }
            Add(harmony, prefix, postfix, typeof(WorldController), "Update");
            Add(harmony, prefix, postfix, typeof(WorldController), "ShowToday");

            // --- the expensive "show every organism" rebuild paths ---
            Add(harmony, prefix, postfix, typeof(WorldView), "ShowAnimalViewsForArea");
            Add(harmony, prefix, postfix, typeof(WorldView), "BuildAndCacheAnimal");
            Add(harmony, prefix, postfix, typeof(WorldView), "ShowPlantViewsForArea");
            Add(harmony, prefix, postfix, typeof(WorldView), "UpdateNests");
            Add(harmony, prefix, postfix, typeof(WorldView), "SetAnimationsToSpeedWithIndex");
            Add(harmony, prefix, postfix, typeof(PrebuiltOrganismLibrary), "GetPrebuiltOrganismProfileBased");
            Add(harmony, prefix, postfix, typeof(MaterialFactory), "RecolorAnimal");

            // --- per-organism view work ---
            Add(harmony, prefix, postfix, typeof(AnimalView), "UpdateAnimationActivityBasedOnDistanceToCamera");
            Add(harmony, prefix, postfix, typeof(PlantView), "UpdateAnimatorActivityBasedOnDistanceToCamera");

            // --- simulation ---
            Add(harmony, prefix, postfix, typeof(TimeLine), "JumpAhead");
            Add(harmony, prefix, postfix, typeof(Notificator), "ProcessNewState");

            // --- the per-day hot path (these run inside LiveADay, so they are subsets of it) ---
            Add(harmony, prefix, postfix, typeof(TimeLine), "LetAllAlgaeInAreaLiveADay");
            Add(harmony, prefix, postfix, typeof(TimeLine), "LetAllPlantsInAreaLiveADay");
            Add(harmony, prefix, postfix, typeof(TimeLine), "LetAllAnimalsInAreaLiveADay");
            Add(harmony, prefix, postfix, typeof(TimeLine), "LetAllFungiInAreaLiveADay");
            Add(harmony, prefix, postfix, typeof(TimeLine), "GetAllSpecies");
            Add(harmony, prefix, postfix, typeof(TimeLine), "UpdateEpoch");
            Add(harmony, prefix, postfix, typeof(TimeLine), "UpdateSpeciesPopulationSizes");
            Add(harmony, prefix, postfix, typeof(TimeLine), "BabyGetsBorn");
            Add(harmony, prefix, postfix, typeof(TimeLine), "AddGrassToCell");
            Add(harmony, prefix, postfix, typeof(WorldState), "Clone");

            // --- Steam IPC, which the daily simulation calls through ICallable callbacks ---
            // UnlockAchive runs once per day forever (playedALongTimeCallBack) and once per
            // predation event (animalGetsEatenCallBack); both do SteamUserStats IPC + StoreStats.
            Add(harmony, prefix, postfix, typeof(SteamEvents), "UnlockAchive");
            Add(harmony, prefix, postfix, typeof(SteamEvents), "StoreStats");

            // --- this mod's own hooks, so their cost is visible too ---
            Add(harmony, prefix, postfix, typeof(LODPatches), "WorldView_Update_LODGovernor");
            Add(harmony, prefix, postfix, typeof(MemoryPatches), "WorldController_ShowToday_MemoryGovernor");
            Add(harmony, prefix, postfix, typeof(FastSimPatches), "TimeLine_LetAllPlantsInAreaLiveADay_Prefix");
            Add(harmony, prefix, postfix, typeof(FastSimPatches), "LetAllAnimalsInAreaLiveADay_Prefix");

            _windowStart = Stopwatch.GetTimestamp();
            _gc0AtStart = GC.CollectionCount(0);
            _gc2AtStart = GC.CollectionCount(2);
            Plugin.LogWarn("[PROF] profiling " + _samples.Count + " methods; table every " + (ReportIntervalMs / 1000) + "s.");

            // Whether we are capped or genuinely slow decides the whole strategy.
            try
            {
                Plugin.LogWarn(string.Format(
                    "[PROF] display vsync={0} refresh={1}Hz targetFps={2} quality={3} res={4}x{5}",
                    QualitySettings.vSyncCount,
                    Screen.currentResolution.refreshRate,
                    Application.targetFrameRate,
                    QualitySettings.GetQualityLevel(),
                    Screen.width,
                    Screen.height));

                // Splits the frame into "our managed work" (the table below) and
                // "the engine drawing it" (this row). Everything else in a frame
                // is engine internals we cannot probe from managed code.
                Camera.onPreRender += OnPreRender;
                Camera.onPostRender += OnPostRender;
            }
            catch { }
        }

        private static Sample Add(Harmony harmony, MethodInfo prefix, MethodInfo postfix, Type type, string name)
        {
            MethodInfo target = null;
            try
            {
                target = AccessTools.Method(type, name);
            }
            catch (Exception e)
            {
                Plugin.LogWarn("[PROF] ambiguous target " + type.Name + "." + name + ": " + e.Message);
                return null;
            }

            if (target == null)
            {
                Plugin.LogWarn("[PROF] target missing: " + type.Name + "." + name);
                return null;
            }

            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                Sample s = new Sample();
                s.Name = type.Name + "." + name;
                _byMethod[target] = s;
                _samples.Add(s);
                return s;
            }
            catch (Exception e)
            {
                Plugin.LogWarn("[PROF] patch failed: " + type.Name + "." + name + ": " + e.Message);
                return null;
            }
        }

        // Harmony injects __state (prefix by ref, postfix by value) and __originalMethod.
        private static void Prefix(ref ProfState __state, MethodBase __originalMethod)
        {
            if (!_enabled) return;

            Sample s;
            if (!_byMethod.TryGetValue(__originalMethod, out s)) return;

            __state.Sample = s;
            __state.Start = Stopwatch.GetTimestamp();
        }

        private static void Postfix(ProfState __state)
        {
            if (!_enabled || __state.Sample == null || __state.Start == 0) return;

            long d = Stopwatch.GetTimestamp() - __state.Start;
            Sample s = __state.Sample;
            s.Ticks += d;
            s.Calls++;
            if (d > s.MaxTicks) s.MaxTicks = d;
        }

        public static void Tick()
        {
            if (!_enabled || _windowStart == 0) return;

            // Called once per frame from Plugin.Update.
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);

            long now = Stopwatch.GetTimestamp();
            long elapsed = now - _windowStart;
            if (elapsed * 1000L / Stopwatch.Frequency < ReportIntervalMs) return;

            Report(elapsed);
            _windowStart = now;
        }

        private static void Report(long elapsedTicks)
        {
            double secs = elapsedTicks / (double)Stopwatch.Frequency;

            _samples.Sort(delegate (Sample a, Sample b) { return b.Ticks.CompareTo(a.Ticks); });

            int frames = (_frameSample != null) ? (int)_frameSample.Calls : 0;
            int days = (_daySample != null) ? (int)_daySample.Calls : 0;

            int gc0 = GC.CollectionCount(0) - _gc0AtStart;
            int gc2 = GC.CollectionCount(2) - _gc2AtStart;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine();
            sb.AppendLine(string.Format(
                "[PROF] === window {0:0.0}s | frames {1} | days {2} | commit {3} MB | gc {4} MB | {5} | gc0 {6} gc2 {7} ===",
                secs, frames, days, CommitMb(), GC.GetTotalMemory(false) / 1048576L, FrameStats(), gc0, gc2));

            for (int i = 0; i < _samples.Count; i++)
            {
                Sample s = _samples[i];
                if (s.Calls == 0) continue;

                double ms = s.Ticks * 1000.0 / Stopwatch.Frequency;
                double pct = s.Ticks / (double)elapsedTicks * 100.0;
                double perSec = s.Calls / secs;
                double maxMs = s.MaxTicks * 1000.0 / Stopwatch.Frequency;
                sb.AppendLine(string.Format(
                    "[PROF] {0,-52} {1,9:0.0}ms {2,6:0.0}% {3,8:0.0}/s max {4,7:0.00}ms",
                    s.Name, ms, pct, perSec, maxMs));
            }

            // Engine-side rendering, measured around the camera's own callback.            // Everything in a frame that is neither this nor the table above is
            // engine internals (culling, animation, physics, GC).
            {
                double renderMs = _renderTicks * 1000.0 / Stopwatch.Frequency;
                double renderPct = _renderTicks / (double)elapsedTicks * 100.0;
                sb.AppendLine(string.Format(
                    "[PROF] {0,-52} {1,9:0.0}ms {2,6:0.0}% {3,8:0.0}/s max {4,7:0.00}ms",
                    "engine.render (Camera pre->post)", renderMs, renderPct, frames / secs, 0.0));
                _renderTicks = 0;
            }

            string animalBreakdown = FastSimPatches.ConsumeAnimalBreakdown();
            if (animalBreakdown != null)
            {
                sb.AppendLine(animalBreakdown);
            }

            string plantBreakdown = FastSimPatches.ConsumePlantBreakdown();
            if (plantBreakdown != null)
            {
                sb.AppendLine(plantBreakdown);
            }

            string adminBreakdown = FastSimPatches.ConsumeAdminBreakdown();
            if (adminBreakdown != null)
            {
                sb.AppendLine(adminBreakdown);
            }

            // Scene census, occasionally. This is what tells us whether the
            // unaccounted frame time is "the world" or "objects that should not
            // exist any more". Sampled rarely because FindObjectsOfType is costly.
            _windowCount++;
            if (_windowCount % 6 == 1)
            {
                sb.AppendLine(Census());
            }

            string text = sb.ToString();
            try { Plugin.LogWarn(text); } catch { }
            AppendToFile(text);

            for (int i = 0; i < _samples.Count; i++)
            {
                _samples[i].Ticks = 0;
                _samples[i].Calls = 0;
                _samples[i].MaxTicks = 0;
            }

            _frameMs.Clear();
            _gc0AtStart = GC.CollectionCount(0);
            _gc2AtStart = GC.CollectionCount(2);
        }

        /// <summary>p50 / p95 / max frame time in the current window.</summary>
        private static string FrameStats()
        {
            if (_frameMs.Count == 0)
            {
                return "frame n/a";
            }
            _frameMs.Sort();
            int n = _frameMs.Count;
            float p50 = _frameMs[n / 2];
            float p95 = _frameMs[Math.Min(n - 1, (int)(n * 0.95))];
            float max = _frameMs[n - 1];
            return string.Format("frame p50 {0:0.0} p95 {1:0.0} max {2:0.0} ms", p50, p95, max);
        }

        private static void OnPreRender(Camera cam)
        {
            _renderStart = Stopwatch.GetTimestamp();
        }
        private static void OnPostRender(Camera cam)
        {
            if (_renderStart == 0)
            {
                return;
            }
            _renderTicks += Stopwatch.GetTimestamp() - _renderStart;
            _renderStart = 0;
        }

        /// <summary>
        /// Live object counts. A frame that is slow with few living organisms is
        /// almost always slow because of objects that are still in the scene -
        /// pooled bodies, orphaned views, animators on things that are not
        /// visible. Counting them separates "the world is busy" from "the world
        /// is leaking".
        /// </summary>
        private static string Census()
        {
            try
            {
                SkinnedMeshRenderer[] skinned = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
                int offscreenSkinned = 0;
                for (int i = 0; i < skinned.Length; i++)
                {
                    if (skinned[i] != null && skinned[i].updateWhenOffscreen) offscreenSkinned++;
                }
                return string.Format(
                    "[PROF] census animators={0} renderers={1} skinned={2} offscreenSkinned={3} lodBias={4:0.00} shadows={5}({6:0}m/{7}c) | {8}",
                    UnityEngine.Object.FindObjectsOfType<Animator>().Length,
                    UnityEngine.Object.FindObjectsOfType<Renderer>().Length,
                    skinned.Length,
                    offscreenSkinned,
                    QualitySettings.lodBias,
                    QualitySettings.shadows,
                    QualitySettings.shadowDistance,
                    QualitySettings.shadowCascades,
                    BandPatches.Status());
            }
            catch (Exception ex)
            {
                return "[PROF] census failed: " + ex.GetType().Name;
            }
        }

        // --- output channel -------------------------------------------------------

        private static void AppendToFile(string text)
        {
            string path = ResolveLogPath();
            if (path == null) return;

            try { File.AppendAllText(path, text); } catch { }
        }

        private static string ResolveLogPath()
        {
            if (_logPathResolved) return _logPath;
            _logPathResolved = true;

            string dir = null;
            try { dir = Application.persistentDataPath; } catch { }
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = Directory.GetCurrentDirectory(); } catch { }
            }
            if (string.IsNullOrEmpty(dir)) return null;

            try
            {
                _logPath = Path.Combine(dir, "sapling_profile.log");
                File.AppendAllText(_logPath,
                    Environment.NewLine +
                    "=== SaplingRustBridge profile session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " (Unity " + Application.unityVersion + ") ===" + Environment.NewLine);
            }
            catch
            {
                _logPath = null;
            }

            return _logPath;
        }

        // --- process commit charge ------------------------------------------------
        // Mono's Process.PrivateMemorySize64 returns 0 on this build, so ask Win32 instead.

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS counters, uint size);

        private static long CommitMb()
        {
            try
            {
                PROCESS_MEMORY_COUNTERS c;
                c.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
                c.PageFaultCount = 0;
                c.PeakWorkingSetSize = IntPtr.Zero;
                c.WorkingSetSize = IntPtr.Zero;
                c.QuotaPeakPagedPoolUsage = IntPtr.Zero;
                c.QuotaPagedPoolUsage = IntPtr.Zero;
                c.QuotaPeakNonPagedPoolUsage = IntPtr.Zero;
                c.QuotaNonPagedPoolUsage = IntPtr.Zero;
                c.PagefileUsage = IntPtr.Zero;
                c.PeakPagefileUsage = IntPtr.Zero;

                if (K32GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb))
                {
                    return (long)c.PagefileUsage / 1048576L;
                }
            }
            catch { }
            return -1;
        }
    }
}
