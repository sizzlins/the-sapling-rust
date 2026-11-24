using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace SimLab
{
    internal static class Bootstrap
    {
        private static int Main(string[] args)
        {
            StripUnityInternalCalls();
            return Program.Run(args);
        }

        // Strips [InternalCall] from local copy of UnityEngine.CoreModule.dll (in bin/)
        // so desktop .NET 4.8 CLR lets Harmony detour UnityEngine.Random, Debug, and Object.
        private static void StripUnityInternalCalls()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string dllPath = Path.Combine(exeDir, "UnityEngine.CoreModule.dll");
            if (!File.Exists(dllPath)) return;

            string tmpPath = dllPath + ".tmp";
            bool changed = false;
            using (var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadWrite = false }))
            {
                foreach (var type in asm.MainModule.Types)
                {
                    if (type.FullName != "UnityEngine.Random" &&
                        type.FullName != "UnityEngine.Debug" &&
                        type.FullName != "UnityEngine.DebugLogHandler" &&
                        type.FullName != "UnityEngine.Object")
                    {
                        continue;
                    }
                    foreach (var m in type.Methods)
                    {
                        if (!m.IsInternalCall) continue;
                        changed = true;
                        m.IsInternalCall = false;
                        m.ImplAttributes &= ~Mono.Cecil.MethodImplAttributes.InternalCall;
                        m.ImplAttributes |= Mono.Cecil.MethodImplAttributes.IL;
                        m.Body = new Mono.Cecil.Cil.MethodBody(m);
                        var il = m.Body.GetILProcessor();
                        string rt = m.ReturnType.FullName;
                        if (rt == "System.Void")
                        {
                            il.Append(il.Create(OpCodes.Ret));
                        }
                        else if (rt == "System.Single")
                        {
                            il.Append(il.Create(OpCodes.Ldc_R4, 0f));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                        else if (rt == "System.Double")
                        {
                            il.Append(il.Create(OpCodes.Ldc_R8, 0.0));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                        else if (rt == "System.Int32" || rt == "System.Boolean" || rt == "System.UInt32")
                        {
                            il.Append(il.Create(OpCodes.Ldc_I4_0));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                        else if (rt == "System.IntPtr")
                        {
                            il.Append(il.Create(OpCodes.Ldsfld, asm.MainModule.ImportReference(typeof(IntPtr).GetField("Zero"))));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                        else if (m.ReturnType.IsValueType)
                        {
                            var loc = new VariableDefinition(m.ReturnType);
                            m.Body.Variables.Add(loc);
                            il.Append(il.Create(OpCodes.Ldloca_S, loc));
                            il.Append(il.Create(OpCodes.Initobj, m.ReturnType));
                            il.Append(il.Create(OpCodes.Ldloc_0));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                        else
                        {
                            il.Append(il.Create(OpCodes.Ldnull));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                    }
                }
                if (changed) asm.Write(tmpPath);
            }
            if (changed)
            {
                File.Delete(dllPath);
                File.Move(tmpPath, dllPath);
            }
        }
    }

    internal static class Program
    {
        private const string GameDataDir = @"D:\The_Sapling\The Sapling\the_sapling_Data";
        private const string ArchiveDir = @"C:\Users\LOQ\AppData\LocalLow\Wessel Stoop\The Sapling\ArchivedAnimals";

        private static System.Random _rng = new System.Random(20261125);
        private static int _totalMatings = 0;
        private static int _totalBirths = 0;
        private static int _birthsF = 0;
        private static int _birthsM = 0;
        private static int _totalMutations = 0;
        private static Dictionary<DeathCause, int> _allDeaths = new Dictionary<DeathCause, int>();

        // Deep-time guardrails: keep taxonomy/definition maps bounded for multi-hundred-
        // thousand-day runs. Trait mutations still happen; new species stop at the cap.
        private static TimeLine _activeTimeLine = null;
        private static int _speciesCap = 2000;
        private static long _speciesCapHits = 0;

        // --- lightweight profiling of the four kingdom passes ---
        private static long _profPlants, _profAnimals, _profAlgae, _profFungi;
        private static long _cntPlants, _cntAnimals, _cntAlgae, _cntFungi;
        private static long _profStart;

        private static void ProfPlants_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfPlants_Postfix(long __state) { _profPlants += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntPlants++; }
        private static void ProfAnimals_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfAnimals_Postfix(long __state) { _profAnimals += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntAnimals++; }
        private static void ProfAlgae_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfAlgae_Postfix(long __state) { _profAlgae += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntAlgae++; }
        private static void ProfFungi_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfFungi_Postfix(long __state) { _profFungi += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntFungi++; }

        private static long _cntInstinct;
        private static long _cntInstinctHit;
        private static double _sumInstinctRange;

        private static void RustSearch_Prefix(float x, float y, bool isSee, InstinctConditionObject cond)
        {
            _cntInstinct++;
            _sumInstinctRange += (y - x);
        }

        private static void RustSearch_Postfix(int __result)
        {
            if (__result >= 0) _cntInstinctHit++;
        }

        private static bool Classify_Prefix(PopulationBasedClassifier __instance, int definition, int parentDefinition, int branchOffThreshold, bool forceNewSpecies, ref int __result)
        {
            if (_speciesCap <= 0 || _activeTimeLine == null) return true;
            if (__instance.speciesPerDefinition.ContainsKey(definition)) return true;
            if (_activeTimeLine.allSpecies.Count < _speciesCap) return true;

            int sp;
            if (!__instance.speciesPerDefinition.TryGetValue(parentDefinition, out sp))
            {
                sp = -1;
                foreach (var kv in __instance.speciesPerDefinition) { sp = kv.Value; break; }
                if (sp < 0) return true;
            }
            __instance.speciesPerDefinition[definition] = sp;
            __instance.nrOfClassificationsPerDefinition[definition] = 1;
            __result = sp;
            _speciesCapHits++;
            return false;
        }

        // =========================================================================
        // 1. Minimal Harmony shims for Unity native engine icalls (Random, Debug,
        //    UnityEngine.Object equality) so retail Assembly-CSharp runs headless.
        // =========================================================================
        private static bool RandomRangeFloat_Prefix(float min, float max, ref float __result)
        {
            __result = min + (float)_rng.NextDouble() * (max - min);
            return false;
        }

        private static bool RandomRangeInt_Prefix(int min, int max, ref int __result)
        {
            if (max <= min) { __result = min; return false; }
            __result = _rng.Next(min, max);
            return false;
        }

        private static bool RandomValue_Prefix(ref float __result)
        {
            __result = (float)_rng.NextDouble();
            return false;
        }

        private static bool SwallowLog_Prefix() => false;

        private static bool UnityObjectEq_Prefix(UnityEngine.Object x, UnityEngine.Object y, ref bool __result)
        {
            __result = object.ReferenceEquals(x, y);
            return false;
        }

        private static bool UnityObjectNeq_Prefix(UnityEngine.Object x, UnityEngine.Object y, ref bool __result)
        {
            __result = !object.ReferenceEquals(x, y);
            return false;
        }

        // Loading a very long timeline: keep only the last two WorldStates (the rest are
        // historical population counters the sim never reads) so a 1M-day save doesn't
        // allocate a gigabyte-scale object graph.
        private static bool GameSaver_GetWorldState_Prefix(GameSaver __instance, ref WorldState __result)
        {
            if (ReferenceEquals(__instance, null)) return true;
            int dayIndex = __instance.dayIndex;
            if (dayIndex <= 2 || __instance.worldStates == null || __instance.worldStates.Count >= dayIndex - 2) return true;

            for (int group = 0; group < 4; group++)
            {
                int count = __instance.GetHighInteger();
                for (int i = 0; i < count; i++) __instance.GetHighInteger();
            }
            __result = null;
            return false;
        }

        private static void TrackFertilize_Postfix()
        {
            _totalMatings++;
        }

        private static void TrackBirth_Postfix(LivingAnimal baby)
        {
            _totalBirths++;
            if (baby != null && baby.definition != null)
            {
                if (baby.definition.currentSex == 0) _birthsF++;
                else _birthsM++;
            }
        }

        private static void TrackMutation_Postfix()
        {
            _totalMutations++;
        }

        private static bool _fixBlendSexBug = false;

        private static void FixBlendSex_Postfix(AnimalDefinition animalA, AnimalDefinition __result)
        {
            if (_fixBlendSexBug && animalA != null && __result != null)
            {
                __result.currentSex = animalA.currentSex;
            }
        }

        private static void TrackDeath_Postfix(DeathCause cause)
        {
            int cur;
            _allDeaths.TryGetValue(cause, out cur);
            _allDeaths[cause] = cur + 1;
        }

        // Applies only the verified distance-band native fast path (byte-identical).
        private static void ApplyBandPatchesOnly()
        {
            var h = new Harmony("com.saplingrust.band");
            Type t = typeof(SaplingRustBridge.Patches.BandPatches);
            int ok = 0, bad = 0;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attrs = HarmonyMethodExtensions.GetFromMethod(m);
                if (attrs == null || attrs.Count == 0) continue;
                HarmonyMethod info = HarmonyMethod.Merge(attrs);
                if (info.declaringType == null) continue;
                bool pre = m.IsDefined(typeof(HarmonyPrefix), false), post = m.IsDefined(typeof(HarmonyPostfix), false);
                bool tr = m.IsDefined(typeof(HarmonyTranspiler), false), fin = m.IsDefined(typeof(HarmonyFinalizer), false);
                if (!pre && !post && !tr && !fin) continue;
                try
                {
                    MethodBase orig = (info.methodType ?? MethodType.Normal) == MethodType.Constructor
                        ? (MethodBase)AccessTools.Constructor(info.declaringType, info.argumentTypes ?? new Type[0])
                        : AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes);
                    if (orig == null) { Console.WriteLine($"[BAND] target missing {m.Name}"); bad++; continue; }
                    var hm = new HarmonyMethod(m);
                    h.Patch(orig, prefix: pre ? hm : null, postfix: post ? hm : null, transpiler: tr ? hm : null, finalizer: fin ? hm : null, ilmanipulator: null);
                    ok++;
                }
                catch (Exception ex) { Console.WriteLine($"[BAND] {m.Name} failed: {ex.Message}"); bad++; }
            }
            try { SaplingRustBridge.Patches.BandPatches.VerifyManagedAgainstNative(); }
            catch (Exception ex) { Console.WriteLine("[BAND] verify: " + ex.Message); }
            Console.WriteLine($"[BAND] applied {ok} patch methods ({bad} failed)");
        }

        // Applies the shipped SaplingRustBridge simulation patches exactly like Plugin.PatchClassResilient.
        private static void ApplyBridgePatches(bool jump)
        {
            ApplyBridgePatches(jump, null);
        }

        private static void ApplyBridgePatches(bool jump, Type[] onlyClasses)
        {
            var h = new Harmony("com.saplingrust.bridge");
            var simTypes = new HashSet<Type> { typeof(TimeLine), typeof(WorldState) };
            Type[] classes = onlyClasses ?? new Type[]
            {
                typeof(SaplingRustBridge.Patches.BandPatches),
                typeof(SaplingRustBridge.Patches.SimulationPatches),
                typeof(SaplingRustBridge.Patches.TimeJumpPatches),
                typeof(SaplingRustBridge.Patches.FastSimPatches),
            };
            int ok = 0, bad = 0;
            foreach (Type t in classes)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attrs = HarmonyMethodExtensions.GetFromMethod(m);
                    if (attrs == null || attrs.Count == 0) continue;
                    HarmonyMethod info = HarmonyMethod.Merge(attrs);
                    if (info.declaringType == null) continue;
                    if (t == typeof(SaplingRustBridge.Patches.TimeJumpPatches) && !simTypes.Contains(info.declaringType)) continue;
                    bool pre = m.IsDefined(typeof(HarmonyPrefix), false), post = m.IsDefined(typeof(HarmonyPostfix), false);
                    bool tr = m.IsDefined(typeof(HarmonyTranspiler), false), fin = m.IsDefined(typeof(HarmonyFinalizer), false);
                    if (!pre && !post && !tr && !fin) continue;
                    try
                    {
                        MethodBase orig = (info.methodType ?? MethodType.Normal) == MethodType.Constructor
                            ? (MethodBase)AccessTools.Constructor(info.declaringType, info.argumentTypes ?? new Type[0])
                            : AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes);
                        if (orig == null) { Console.WriteLine($"[PATCH] target missing {t.Name}.{m.Name}"); bad++; continue; }
                        var hm = new HarmonyMethod(m);
                        h.Patch(orig, prefix: pre ? hm : null, postfix: post ? hm : null, transpiler: tr ? hm : null, finalizer: fin ? hm : null, ilmanipulator: null);
                        ok++;
                    }
                    catch (Exception ex) { Console.WriteLine($"[PATCH] {t.Name}.{m.Name} failed: {ex.Message}"); bad++; }
                }
            }
            try { SaplingRustBridge.Patches.BandPatches.VerifyManagedAgainstNative(); } catch (Exception ex) { Console.WriteLine("[PATCH] band verify: " + ex.Message); }
            SaplingRustBridge.Patches.TimeJumpPatches.IsTimeJumpActive = jump;
            Console.WriteLine($"[PATCH] applied {ok} bridge patches ({bad} failed), jump={jump}");
        }

        private static void InstallHeadlessShims()
        {
            Harmony h = new Harmony("com.saplingrust.simlab");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;

            h.Patch(typeof(UnityEngine.Random).GetMethod("Range", new[] { typeof(float), typeof(float) }),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(RandomRangeFloat_Prefix), flags)));
            h.Patch(typeof(UnityEngine.Random).GetMethod("Range", new[] { typeof(int), typeof(int) }),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(RandomRangeInt_Prefix), flags)));
            h.Patch(typeof(UnityEngine.Random).GetProperty("value").GetGetMethod(),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(RandomValue_Prefix), flags)));

            foreach (var m in typeof(UnityEngine.Debug).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "Log" || m.Name == "LogWarning" || m.Name == "LogError")
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SwallowLog_Prefix), flags)));
                }
            }

            h.Patch(typeof(UnityEngine.Object).GetMethod("op_Equality", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) }),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(UnityObjectEq_Prefix), flags)));
            h.Patch(typeof(UnityEngine.Object).GetMethod("op_Inequality", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) }),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(UnityObjectNeq_Prefix), flags)));

            h.Patch(typeof(LivingAnimal).GetMethod(nameof(LivingAnimal.Fertilize)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(TrackFertilize_Postfix), flags)));
            h.Patch(typeof(TimeLine).GetMethod(nameof(TimeLine.BabyGetsBorn)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(TrackBirth_Postfix), flags)));
            h.Patch(typeof(MutationTracker).GetMethod(nameof(MutationTracker.RegisterMutation)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(TrackMutation_Postfix), flags)));
            h.Patch(typeof(DeathTracker).GetMethod(nameof(DeathTracker.TrackDeath)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(TrackDeath_Postfix), flags)));
            h.Patch(typeof(AnimalBlender).GetMethod(nameof(AnimalBlender.BlendAnimals)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(FixBlendSex_Postfix), flags)));

            h.Patch(AccessTools.Method(typeof(PopulationBasedClassifier), "Classify"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(Classify_Prefix), flags)));

            h.Patch(typeof(GameSaver).GetMethod("GetWorldState"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(GameSaver_GetWorldState_Prefix), flags)));

            h.Patch(typeof(TimeLine).GetMethod("LetAllPlantsInAreaLiveADay"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfPlants_Prefix), flags)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfPlants_Postfix), flags)));
            h.Patch(typeof(TimeLine).GetMethod("LetAllAnimalsInAreaLiveADay"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfAnimals_Prefix), flags)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfAnimals_Postfix), flags)));
            h.Patch(typeof(TimeLine).GetMethod("LetAllAlgaeInAreaLiveADay"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfAlgae_Prefix), flags)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfAlgae_Postfix), flags)));
            h.Patch(typeof(TimeLine).GetMethod("LetAllFungiInAreaLiveADay"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfFungi_Prefix), flags)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProfFungi_Postfix), flags)));

            MethodInfo rustSearch = AccessTools.Method(typeof(SaplingRustBridge.Patches.FastSimPatches), "SearchNearbyRust");
            if (rustSearch != null)
            {
                h.Patch(rustSearch,
                    prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(RustSearch_Prefix), flags)),
                    postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(RustSearch_Postfix), flags)));
            }
        }

        // =========================================================================
        // 2. Load real game BodyPartStats/*.yaml + Resources/Raw/BodyParts/*/config.json
        //    into a genuine OrganismObjectFileImporter instance.
        // =========================================================================
        private static Dictionary<string, Dictionary<string, string>> ParseSimpleYaml(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            Dictionary<string, string> cur = null;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                if (!char.IsWhiteSpace(rawLine[0]) && rawLine.TrimEnd().EndsWith(":"))
                {
                    string key = rawLine.TrimEnd().TrimEnd(':').ToLowerInvariant();
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[key] = cur;
                }
                else if (cur != null)
                {
                    int colon = rawLine.IndexOf(':');
                    if (colon > 0)
                    {
                        string k = rawLine.Substring(0, colon).Trim();
                        string v = rawLine.Substring(colon + 1).Trim();
                        cur[k] = v;
                    }
                }
            }
            return result;
        }

        private static float ParseFloat(Dictionary<string, string> d, string key, float def = 0f)
        {
            string s;
            if (!d.TryGetValue(key, out s) || string.IsNullOrEmpty(s)) return def;
            s = s.Trim().Trim('[', ']');
            int comma = s.IndexOf(',');
            if (comma >= 0) s = s.Substring(0, comma).Trim();
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static Vector2 ParseRange(Dictionary<string, string> d, string key, float def = 0f)
        {
            string s;
            if (!d.TryGetValue(key, out s) || string.IsNullOrEmpty(s)) return new Vector2(def, def);
            s = s.Trim();
            if (s.StartsWith("[") && s.EndsWith("]"))
            {
                string[] parts = s.Substring(1, s.Length - 2).Split(',');
                float a = def, b = def;
                if (parts.Length >= 1) float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                if (parts.Length >= 2) float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
                else b = a;
                return new Vector2(a, b);
            }
            float single;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out single))
                return new Vector2(single, single);
            return new Vector2(def, def);
        }

        private static bool ParseBool(Dictionary<string, string> d, string key, bool def = false)
        {
            string s;
            if (!d.TryGetValue(key, out s)) return def;
            return string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ParseStr(Dictionary<string, string> d, string key, string def = "")
        {
            string s;
            return d.TryGetValue(key, out s) ? s.Trim() : def;
        }

        private static OrganismObjectFileImporter BuildRealImporter()
        {
            var imp = new OrganismObjectFileImporter();
            imp.importFinished = true;
            imp.bottomOnlyLimbIndices = new HashSet<int>();

            string rawRoot = Path.Combine(GameDataDir, @"Resources\Raw\BodyParts");
            string yamlRoot = Path.Combine(GameDataDir, @"StreamingAssets\BodyPartStats");

            var foodKeyMap = new Dictionary<string, DetailedFoodType>(StringComparer.OrdinalIgnoreCase)
            {
                { "energyFromPlankton", DetailedFoodType.Plankton },
                { "energyFromSoftPlantTissue", DetailedFoodType.SoftPlantTissue },
                { "energyFromAlgae", DetailedFoodType.SoftPlantTissue },
                { "energyFromSap", DetailedFoodType.Sap },
                { "energyFromBark", DetailedFoodType.Bark },
                { "energyFromNectar", DetailedFoodType.Nectar },
                { "energyFromSeed", DetailedFoodType.Seed },
                { "energyFromNut", DetailedFoodType.Nut },
                { "energyFromFruit", DetailedFoodType.Fruit },
                { "energyFromWholeAnimal", DetailedFoodType.WholeAnimal },
                { "energyFromFreshMeat", DetailedFoodType.FreshMeat },
                { "energyFromOldMeat", DetailedFoodType.OldMeat },
                { "energyFromEgg", DetailedFoodType.Egg },
                { "energyFromBlood", DetailedFoodType.Blood },
                { "energyFromFungus", DetailedFoodType.Fungus },
                { "energyFromGrass", DetailedFoodType.Grass },
                { "energyFromMarineSnow", DetailedFoodType.MarineSnow },
            };

            string[] categories = { "Mouth", "Limb", "Eye", "Ear", "AnimalAccessoire", "Leaf", "Seed", "PollinationOrgan", "PlantAccessoire" };
            var idRx = new Regex("\"id\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
            var parentRx = new Regex("\"parent\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
            var bottomRx = new Regex("\"bottomOnly\"\\s*:\\s*true", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (string cat in categories)
            {
                var yaml = ParseSimpleYaml(Path.Combine(yamlRoot, cat + "Stats.yaml"));
                var idToName = new Dictionary<int, string>();
                var nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var nameToParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string catDir = Path.Combine(rawRoot, cat);
                if (!Directory.Exists(catDir)) continue;

                foreach (string cfgPath in Directory.GetFiles(catDir, "config.json", SearchOption.AllDirectories))
                {
                    string shape = Path.GetFileName(Path.GetDirectoryName(cfgPath)).ToLowerInvariant();
                    string json = File.ReadAllText(cfgPath);
                    var mId = idRx.Match(json);
                    if (!mId.Success) continue;
                    int id = int.Parse(mId.Groups[1].Value, CultureInfo.InvariantCulture);

                    idToName[id] = shape;
                    nameToId[shape] = id;
                    var mParent = parentRx.Match(json);
                    if (mParent.Success && !string.IsNullOrEmpty(mParent.Groups[1].Value))
                    {
                        nameToParent[shape] = mParent.Groups[1].Value.ToLowerInvariant();
                    }

                    Dictionary<string, string> y;
                    if (!yaml.TryGetValue(shape, out y)) y = new Dictionary<string, string>();

                    imp.materialTypeForBodyPartShape[shape] = MaterialType.Main;
                    imp.specialMaterialTypeForBodyPartShape[shape] = MaterialType.None;
                    imp.lightIntensityForBodyPartShape[shape] = 0f;
                    imp.attackParticleEffectForBodyPartShape[shape] = string.Empty;
                    imp.descriptionForBodyPartShape[shape] = ParseStr(y, "description", shape);
                    imp.mergeWithTorsoForBodyPartShape[shape] = false;
                    imp.mergeTopModelWithTorsoForBodyPartShape[shape] = false;
                    imp.mergeFrontModelWithTorsoForBodyPartShape[shape] = false;
                    imp.separateModelForTopAndFrontBodyPartShape[shape] = false;

                    // Populate generic plant/seed/leaf hashtables so CreateSeedWithShapeIndex / CreateLeafWithShapeIndex work natively.
                    if (cat == "Leaf" || cat == "Seed" || cat == "PollinationOrgan" || cat == "PlantAccessoire")
                    {
                        imp.extraEnergyForAnimalsForBodyPartShapeName[shape] = ParseFloat(y, "extraEnergyForAnimals", 0f);
                        imp.distanceWithoutWindForBodyPartShapeName[shape] = ParseFloat(y, "distanceWithoutWind", 1f);
                        imp.distanceWithStrongWindForBodyPartShapeName[shape] = ParseFloat(y, "distanceWithStrongWind", 3f);
                        imp.chanceOfStickingToAnimalForBodyPartShapeName[shape] = ParseFloat(y, "chanceOfStickingToAnimal", 0f);
                        imp.floatsInWaterForBodyPartShapeName[shape] = ParseBool(y, "floatsInWater", false);
                        imp.sinksInWaterForBodyPartShapeName[shape] = ParseBool(y, "sinksInWater", false);
                        imp.harderToEatForBodyPartShapeName[shape] = ParseBool(y, "harderToEat", false);
                        imp.survivesBeingEatenForBodyPartShapeName[shape] = ParseBool(y, "survivesBeingEaten", false);
                        imp.requiresFireForBodyPartShapeName[shape] = ParseBool(y, "requiresFire", false);
                        imp.canBeEatenDuringFlightForBodyPartShapeName[shape] = ParseBool(y, "canBeEatenDuringFlight", false);
                        imp.pollenMovementDistanceForBodyPartShapeName[shape] = ParseFloat(y, "pollenMovementDistance", 3f);
                        imp.chanceOfCatchingAnimalForBodyPartShapeName[shape] = ParseFloat(y, "chanceOfCatchingAnimal", 0f);
                        imp.nrOfSporesForBodyPartShape[shape] = (int)ParseFloat(y, "nrOfSpores", 0f);
                        imp.coldResistanceForBodyPartShape[shape] = ParseFloat(y, "coldResistance", 0f);
                        imp.heatLossForBodyPartShape[shape] = ParseFloat(y, "heatLoss", 0f);
                        imp.poisonStrengthForBodyPartShape[shape] = ParseFloat(y, "poisonStrength", 0f);
                        imp.defenseAgainstSpikesForBodyPartShape[shape] = ParseFloat(y, "defenseAgainstSpikes", 0f);
                        imp.energyDecreaseForSoilNestCreationForBodyPartShape[shape] = 0f;
                        imp.weightForBodyPartShapeName[shape] = ParseRange(y, "weight", 0f);
                        imp.embryoDevelopmentTimeForBodyPartShapeName[shape] = ParseFloat(y, "embryoDevelopmentTime", 0f);
                        imp.attractivenessForBodyPartShape[shape] = ParseRange(y, "attractiveness", 0f);
                        imp.sunLightCaughtDuringDayTimeForBodyPartShape[shape] = ParseRange(y, "sunLightCaughtDuringDayTime", 1f);
                        imp.sunLightCaughtAtTwilightForBodyPartShape[shape] = ParseRange(y, "sunLightCaughtAtTwilight", 0.5f);
                        imp.windResistanceForBodyPartShape[shape] = ParseRange(y, "windResistance", 0.5f);
                        imp.minimumTemperatureForBodyPartShape[shape] = ParseFloat(y, "minimumTemperature", 0f);
                        imp.maximumTemperatureForBodyPartShape[shape] = ParseFloat(y, "maximumTemperature", 1f);
                        imp.moistureResistanceForBodyPartShape[shape] = ParseFloat(y, "moistureResistance", 0.5f);
                        imp.droughtResistanceForBodyPartShape[shape] = ParseFloat(y, "droughtResistance", 0.5f);
                        imp.waterLossForBodyPartShape[shape] = ParseFloat(y, "waterLoss", 0.1f);
                        imp.attackForBodyPartShapeName[shape] = ParseFloat(y, "attack", 0f);
                        imp.usesWindPollinationForBodyPartShapeName[shape] = ParseBool(y, "usesWindPollination", false);
                        imp.usesAnimalPollinationForBodyPartShapeName[shape] = ParseBool(y, "usesAnimalPollination", false);
                        imp.nrOfPollenForBodyPartShapeName[shape] = ParseFloat(y, "nrOfPollen", 1f);
                        continue;
                    }

                    if (cat == "Mouth")
                    {
                        var md = new MouthDefinition(id, shape);
                        md.drag = ParseFloat(y, "drag", 0f);
                        md.attractiveness = ParseRange(y, "attractiveness", 0f);
                        md.embryoDevelopmentTime = (int)ParseFloat(y, "embryoDevelopmentTime", 0f);
                        md.weight = ParseRange(y, "weight", 0f);
                        md.cryPitch = ParseFloat(y, "callPitch", 0f);
                        md.cryLoudness = ParseFloat(y, "callLoudness", 0f);
                        md.attack = (int)ParseFloat(y, "attack", 0f);
                        md.heatLoss = ParseFloat(y, "heatLoss", 0f);
                        md.defenseAgainstSpikes = ParseFloat(y, "defenseAgainstSpikes", 0f);
                        md.increasesEatingHeight = ParseFloat(y, "increasesEatingHeight", 0f);
                        md.venomStrength = ParseFloat(y, "poisonStrength", 0f);
                        md.canWax = ParseBool(y, "canWax", false);
                        md.soundPack = string.Empty;
                        md.soundPackReferenceBodySize = 11;
                        md.description = ParseStr(y, "description", shape);
                        md.energyPerFoodType = new Dictionary<DetailedFoodType, float>();

                        float maxPlant = 0f, maxMeat = 0f;
                        foreach (var kv in foodKeyMap)
                        {
                            float ev = ParseFloat(y, kv.Key, 0f);
                            if (ev > 0f)
                            {
                                md.energyPerFoodType[kv.Value] = ev;
                                if (kv.Value == DetailedFoodType.Nectar) md.eatsNectar = true;
                                if (kv.Value == DetailedFoodType.SoftPlantTissue || kv.Value == DetailedFoodType.Grass ||
                                    kv.Value == DetailedFoodType.Fruit || kv.Value == DetailedFoodType.Seed ||
                                    kv.Value == DetailedFoodType.Nut || kv.Value == DetailedFoodType.Bark ||
                                    kv.Value == DetailedFoodType.Sap || kv.Value == DetailedFoodType.Plankton)
                                {
                                    md.eatsPlants = true;
                                    if (ev > maxPlant) maxPlant = ev;
                                }
                                if (kv.Value == DetailedFoodType.WholeAnimal || kv.Value == DetailedFoodType.FreshMeat ||
                                    kv.Value == DetailedFoodType.OldMeat || kv.Value == DetailedFoodType.Egg)
                                {
                                    md.eatsMeat = true;
                                    if (ev > maxMeat) maxMeat = ev;
                                }
                                if (kv.Value == DetailedFoodType.Blood) md.drinksBlood = true;
                            }
                        }
                        md.getsMostEnergyFromMeat = maxMeat > maxPlant;

                        string oxy = ParseStr(y, "mainOxygenResource", "air").ToLowerInvariant();
                        int dwo = (int)ParseFloat(y, "daysWithoutOxygen", 2f);
                        imp.mainOxygenResourceForBodyPartShapeName[shape] = oxy;
                        imp.daysWithoutOxygenForBodyPartShapeName[shape] = dwo;
                        if (oxy == "air") { md.timeOnLand = -1; md.timeInWater = dwo; imp.mouthAquaticPerShapeIndex[id] = false; }
                        else if (oxy == "water") { md.timeOnLand = dwo; md.timeInWater = -1; imp.mouthAquaticPerShapeIndex[id] = true; }
                        else { md.timeOnLand = -1; md.timeInWater = -1; imp.mouthAquaticPerShapeIndex[id] = false; }

                        imp.precreatedMouths[id] = md;
                    }
                    else if (cat == "Limb")
                    {
                        var ld = new LimbDefinition(id, shape);
                        ld.drag = ParseFloat(y, "drag", 0f);
                        ld.attractiveness = ParseRange(y, "attractiveness", 0f);
                        ld.embryoDevelopmentTime = (int)ParseFloat(y, "embryoDevelopmentTime", 0f);
                        ld.weight = ParseRange(y, "weight", 0f);
                        ld.stepSize = ParseFloat(y, "stepSize", 0f);
                        ld.stepLoudness = ParseFloat(y, "stepLoudness", 0f);
                        ld.stepPitch = ParseFloat(y, "stepPitch", 0f);
                        ld.maximumSteepness = ParseFloat(y, "maximumSteepness", 0f);
                        ld.underWaterSpeed = ParseFloat(y, "underWaterSpeed", 0f);
                        ld.waterSurfaceBoost = ParseFloat(y, "waterSurfaceBoost", 0f);
                        ld.weightSupportedForWalking = ParseFloat(y, "weightSupportedForWalking", -1f);
                        ld.weightSupportedForClimbing = ParseFloat(y, "weightSupportedForClimbing", -1f);
                        ld.weightSupportedForGliding = ParseFloat(y, "weightSupportedForGliding", -1f);
                        ld.weightSupportedForFlying = ParseFloat(y, "weightSupportedForFlying", -1f);
                        ld.canWalk = ld.weightSupportedForWalking > 0f;
                        ld.canClimb = ld.weightSupportedForClimbing > 0f;
                        ld.canGlide = ld.weightSupportedForGliding > 0f;
                        ld.canFly = ld.weightSupportedForFlying > 0f;
                        ld.requireTwice = ParseBool(y, "requireTwice", false);
                        ld.canDig = ParseBool(y, "canDig", false);
                        ld.canHangAsBaby = ParseBool(y, "canHangAsBaby", false);
                        ld.canHangFromBranch = ParseBool(y, "canHangFromBranch", false);
                        ld.canHangUpsideDownFromBranch = ParseBool(y, "canHangUpsideDownFromBranch", false);
                        ld.canSteerInTheAir = ParseBool(y, "canSteerInTheAir", false);
                        ld.feathersRequiredForGlidingAndFlying = ParseBool(y, "feathersRequiredForGlidingAndFlying", false);
                        ld.attack = (int)ParseFloat(y, "attack", 0f);
                        ld.soundPack = string.Empty;
                        ld.description = ParseStr(y, "description", shape);

                        string aq = ParseStr(y, "aquaticBehavior", "swim").ToLowerInvariant();
                        if (aq == "sessile") ld.aquaticBehavior = LimbAquaticBehavior.Sessile;
                        else if (aq == "walkonwatersurface") ld.aquaticBehavior = LimbAquaticBehavior.WalkOnWaterSurface;
                        else if (aq == "walkonoceanfloor") ld.aquaticBehavior = LimbAquaticBehavior.WalkOnOceanFloor;
                        else ld.aquaticBehavior = LimbAquaticBehavior.Swim;

                        if (bottomRx.IsMatch(json)) imp.bottomOnlyLimbIndices.Add(id);
                        imp.canWalkPerShapeIndex[id] = ld.canWalk;
                        imp.canGlideOrFlyPerShapeIndex[id] = ld.canGlide || ld.canFly;
                        imp.precreatedLimbs[id] = ld;
                    }
                    else if (cat == "Eye")
                    {
                        var ed = new EyeDefinition(id, shape);
                        ed.drag = ParseFloat(y, "drag", 0f);
                        ed.attractiveness = ParseRange(y, "attractiveness", 0f);
                        ed.weight = ParseRange(y, "weight", 0f);
                        ed.fieldOfView = (int)ParseFloat(y, "fieldOfView", 180f);
                        ed.maximumSightDistanceDuringDayTime = ParseRange(y, "maximumSightDistanceDuringDayTime", 0.4f);
                        ed.maximumSightDistanceAtTwilight = ParseRange(y, "maximumSightDistanceAtTwilight", 0.2f);
                        ed.maximumSightDistanceAtNight = ParseRange(y, "maximumSightDistanceAtNight", 0f);
                        ed.maximumUnderWaterSightDistanceDuringDayTime = ParseRange(y, "maximumUnderWaterSightDistanceDuringDayTime", 0.2f);
                        ed.maximumUnderWaterSightDistanceAtTwilight = ParseRange(y, "maximumUnderWaterSightDistanceAtTwilight", 0f);
                        ed.maximumUnderWaterSightDistanceAtNight = ParseRange(y, "maximumUnderWaterSightDistanceAtNight", 0f);
                        ed.colorSeeingRange = new Vector2(0f, 1f);
                        ed.description = ParseStr(y, "description", shape);
                        imp.precreatedEyes[id] = ed;
                    }
                    else if (cat == "Ear")
                    {
                        var er = new EarDefinition(id, shape);
                        er.drag = ParseFloat(y, "drag", 0f);
                        er.weight = ParseRange(y, "weight", 0f);
                        er.heatLoss = ParseFloat(y, "heatLoss", 0f);
                        er.pitchHearingRange = new Vector2(ParseFloat(y, "hearingRangeMinPitch", 0.2f), ParseFloat(y, "hearingRangeMaxPitch", 0.8f));
                        er.loudnessHearingRange = new Vector2(ParseFloat(y, "hearingRangeMinLoudness", 0.2f), ParseFloat(y, "hearingRangeMaxLoudness", 1f));
                        er.description = ParseStr(y, "description", shape);
                        imp.precreatedEars[id] = er;
                    }
                    else if (cat == "AnimalAccessoire")
                    {
                        var ad = new AnimalAccessoireDefinition(id, shape);
                        ad.drag = ParseFloat(y, "drag", 0f);
                        ad.attractiveness = ParseRange(y, "attractiveness", 0f);
                        ad.embryoDevelopmentTime = (int)ParseFloat(y, "embryoDevelopmentTime", 0f);
                        ad.weight = ParseRange(y, "weight", 0f);
                        ad.attack = (int)ParseFloat(y, "attack", 0f);
                        ad.armor = (int)ParseFloat(y, "armor", 0f);
                        ad.heatLoss = ParseFloat(y, "heatLoss", 0f);
                        ad.glowing = ParseBool(y, "glowing", false);
                        ad.liquidStorage = ParseBool(y, "liquidStorage", false);
                        ad.providesMilk = ParseFloat(y, "providesMilk", 0f);
                        ad.venomStrength = ParseFloat(y, "poisonStrength", 0f);
                        ad.shellCover = ParseBool(y, "shellCover", false);
                        ad.soundPack = string.Empty;
                        ad.description = ParseStr(y, "description", shape);
                        imp.precreatedAnimalAccessoires[id] = ad;
                    }
                }

                imp.shapeNamesPerBodyPartName[cat] = idToName;
                var parentIdxMap = new Dictionary<int, int>();
                foreach (var kv in idToName)
                {
                    string pName;
                    int pId;
                    if (nameToParent.TryGetValue(kv.Value, out pName) && nameToId.TryGetValue(pName, out pId))
                        parentIdxMap[kv.Key] = pId;
                    else
                        parentIdxMap[kv.Key] = -1;
                }
                imp.parentIndexPerBodyPartType[cat] = parentIdxMap;
            }

            return imp;
        }

        // =========================================================================
        // 2b. Build the equivalent of OrganismObjectFileImporter2 (MushroomCap)
        //     straight from the shipped config.json files. The real ImportAllFiles
        //     needs Unity AssetBundles + meshes, but headless simulation only needs
        //     the cap statistics, so we populate the private dictionaries directly.
        // =========================================================================
        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new MissingFieldException(target.GetType().Name, name);
            f.SetValue(target, value);
        }

        private static Vector2 ParseJsonRange(string json, string key)
        {
            var arrayRx = new Regex("\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.IgnoreCase);
            Match m = arrayRx.Match(json);
            if (m.Success)
            {
                string[] parts = m.Groups[1].Value.Split(',');
                float a = 0f, b = 0f;
                if (parts.Length > 0) float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                if (parts.Length > 1) float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
                else b = a;
                return new Vector2(a, b);
            }
            var singleRx = new Regex("\"" + key + "\"\\s*:\\s*(-?[0-9.]+)", RegexOptions.IgnoreCase);
            m = singleRx.Match(json);
            if (m.Success)
            {
                float v;
                if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return new Vector2(v, v);
            }
            return new Vector2(0f, 0f);
        }

        private static string ParseJsonString(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        private static OrganismObjectFileImporter2 BuildRealImporter2()
        {
            var imp = new OrganismObjectFileImporter2();

            var shapeNames = new Dictionary<string, string[]>();
            var bodyPartId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nrSpores = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
            var coldRes = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
            var sporeMethod = new Dictionary<string, SporeDistributionMethod>(StringComparer.OrdinalIgnoreCase);
            var scales = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var vertical = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string dir = Path.Combine(GameDataDir, @"Resources\Raw\BodyParts\MushroomCap");
            var idToName = new Dictionary<int, string>();
            if (Directory.Exists(dir))
            {
                foreach (string cfg in Directory.GetFiles(dir, "config.json", SearchOption.AllDirectories))
                {
                    string shape = Path.GetFileName(Path.GetDirectoryName(cfg)).ToLowerInvariant();
                    string json = File.ReadAllText(cfg);
                    Match idm = Regex.Match(json, "\"id\"\\s*:\\s*(\\d+)");
                    if (!idm.Success) continue;
                    int id = int.Parse(idm.Groups[1].Value, CultureInfo.InvariantCulture);

                    idToName[id] = shape;
                    bodyPartId[shape] = id;
                    descriptions[shape] = ParseJsonString(json, "description");
                    nrSpores[shape] = ParseJsonRange(json, "nrOfSpores");
                    coldRes[shape] = ParseJsonRange(json, "coldResistance");
                    scales[shape] = ParseJsonString(json, "scalesVertically").Equals("true", StringComparison.OrdinalIgnoreCase);
                    vertical[shape] = ParseFloat(new Dictionary<string, string>(), "x");
                    string vm = ParseJsonString(json, "verticalCorrection");
                    float vf;
                    if (float.TryParse(vm, NumberStyles.Float, CultureInfo.InvariantCulture, out vf)) vertical[shape] = vf;
                    parents[shape] = ParseJsonString(json, "parent");

                    string method = ParseJsonString(json, "sporeDistributionMethod").ToLowerInvariant();
                    switch (method)
                    {
                        case "raindrops": sporeMethod[shape] = SporeDistributionMethod.Raindrops; break;
                        case "animaltouch": sporeMethod[shape] = SporeDistributionMethod.AnimalTouch; break;
                        case "eaten": sporeMethod[shape] = SporeDistributionMethod.Eaten; break;
                        default: sporeMethod[shape] = SporeDistributionMethod.Wind; break;
                    }
                }
            }
            var ordered = new List<string>();
            var ids = new List<int>(idToName.Keys);
            ids.Sort();
            foreach (int id in ids) ordered.Add(idToName[id]);
            shapeNames["MushroomCap"] = ordered.ToArray();

            SetPrivateField(imp, "shapeNamesPerBodyPartName", shapeNames);
            SetPrivateField(imp, "bodyPartIdentifierForShapeName", bodyPartId);
            SetPrivateField(imp, "descriptionForBodyPartShape", descriptions);
            SetPrivateField(imp, "nrOfSporesForBodyPartShape", nrSpores);
            SetPrivateField(imp, "coldResistanceForBodyPartShape", coldRes);
            SetPrivateField(imp, "sporeDistributionMethodForBodyPartShape", sporeMethod);
            SetPrivateField(imp, "parentForCap", parents);
            SetPrivateField(imp, "quadMeshForBodyPartShape", new Dictionary<string, QuadMesh>());
            SetPrivateField(imp, "meshForBodyPartShape", new Dictionary<string, Mesh>());
            SetPrivateField(imp, "stipelessQuadMeshForBodyPartShape", new Dictionary<string, QuadMesh>());
            SetPrivateField(imp, "stipelessMeshForBodyPartShape", new Dictionary<string, Mesh>());
            SetPrivateField(imp, "importFinished", true);

            imp.scalesVerticallyForBodyPartShape = scales;
            imp.verticalCorrectionForBodyPartShape = vertical;

            var parentChildren = new Dictionary<int, List<int>>();
            foreach (string shape in ordered)
            {
                string parentName = parents.ContainsKey(shape) ? parents[shape] : string.Empty;
                if (string.IsNullOrEmpty(parentName)) continue;
                int childId = bodyPartId[shape];
                int parentId;
                if (!bodyPartId.TryGetValue(parentName, out parentId)) continue;
                if (!parentChildren.ContainsKey(parentId)) parentChildren[parentId] = new List<int>();
                parentChildren[parentId].Add(childId);
                if (!parentChildren.ContainsKey(childId)) parentChildren[childId] = new List<int>();
                parentChildren[childId].Add(parentId);
            }
            // Caps without relatives would make ChangeCapShape throw; give them a self-loop no-op.
            foreach (int id in ids)
            {
                if (!parentChildren.ContainsKey(id)) parentChildren[id] = new List<int> { id };
            }
            imp.parentAndChildrenShapesForCap = parentChildren;
            return imp;
        }

        // =========================================================================
        // 2c. Load a real .sav16 file and wire it into a live TimeLine exactly the
        //     way OverallController does after GameSaver.Load().
        // =========================================================================
        private static void AddDefinitionOf(HashSet<int> set, LifeForm lifeForm)
        {
            if (lifeForm == null) return;
            if (lifeForm.definitionIndexOfParent >= 0) set.Add(lifeForm.definitionIndexOfParent);

            LivingPlant plant = lifeForm as LivingPlant;
            if (plant != null)
            {
                set.Add(plant.definition.definitionIndex);
                if (plant.infectedByParasite != null) set.Add(plant.infectedByParasite.definition.definitionIndex);
                return;
            }
            LivingAnimal animal = lifeForm as LivingAnimal;
            if (animal != null)
            {
                set.Add(animal.definition.definitionIndex);
                if (animal.infectedByParasite != null) set.Add(animal.infectedByParasite.definition.definitionIndex);
                return;
            }
            LivingFungus fungus = lifeForm as LivingFungus;
            if (fungus != null)
            {
                set.Add(fungus.definition.definitionIndex);
                return;
            }
            LivingAlga alga = lifeForm as LivingAlga;
            if (alga != null)
            {
                set.Add(alga.definition.definitionIndex);
            }
        }

        // Removes classifier entries for definitions that no living organism (or its parent /
        // seed / egg / grass cell) still references. Keeps the long run's dictionaries bounded.
        private static string PruneDefinitionMaps(TimeLine tl)
        {
            var alive = new HashSet<int>();
            for (int a = 0; a < tl.today.areas.Length; a++)
            {
                WorldArea area = tl.today.areas[a];
                for (int i = 0; i < area.cells.Length; i++)
                {
                    WorldCell c = area.cells[i];
                    AddDefinitionOf(alive, c.alga);
                    AddDefinitionOf(alive, c.plankton);
                    AddDefinitionOf(alive, c.plant);
                    AddDefinitionOf(alive, c.fungus);
                    AddDefinitionOf(alive, c.dormantSeed);
                    AddDefinitionOf(alive, c.seed);
                    AddDefinitionOf(alive, c.algaSpore);
                    AddDefinitionOf(alive, c.animal);
                    AddDefinitionOf(alive, c.animalInTree);
                    AddDefinitionOf(alive, c.animalInTheAir);
                    AddDefinitionOf(alive, c.animalUnderWater);
                    AddDefinitionOf(alive, c.animalAtWaterSurface);
                    AddDefinitionOf(alive, c.animalInDeepSea);
                    if (c.eggs != null)
                    {
                        for (int e = 0; e < c.eggs.Length; e++) AddDefinitionOf(alive, c.eggs[e]);
                    }
                    if (c.grassHeight > 0 && c.grassDefinitionIndex >= 0) alive.Add(c.grassDefinitionIndex);
                }
                foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                {
                    AddDefinitionOf(alive, area.GetAnimalInCell(pos.cellIndex, pos.locationType));
                }
            }

            int before = tl.speciesIndexPerDefinitionIndex.Count;
            var dead = new List<int>();
            foreach (int def in tl.speciesIndexPerDefinitionIndex.Keys)
                if (!alive.Contains(def)) dead.Add(def);
            for (int i = 0; i < dead.Count; i++) tl.speciesIndexPerDefinitionIndex.Remove(dead[i]);

            PruneClassifier(tl.populationBasedAlgaClassifier, alive);
            PruneClassifier(tl.populationBasedPlantClassifier, alive);
            PruneClassifier(tl.populationBasedAnimalClassifier, alive);
            PruneClassifier(tl.populationBasedFungusClassifier, alive);

            return $"[PRUNE] definitions map {before} -> {tl.speciesIndexPerDefinitionIndex.Count}, living defs={alive.Count}";
        }
        private static void PruneClassifier(PopulationBasedClassifier classifier, HashSet<int> alive)
        {
            var dead = new List<int>();
            foreach (int def in classifier.speciesPerDefinition.Keys)
                if (!alive.Contains(def)) dead.Add(def);
            for (int i = 0; i < dead.Count; i++)
            {
                classifier.speciesPerDefinition.Remove(dead[i]);
                classifier.nrOfClassificationsPerDefinition.Remove(dead[i]);
            }
        }

        private static void InitWeatherFromCells(TimeLine tl)
        {
            if (tl.today == null || tl.today.areas == null || tl.today.areas.Length == 0) return;
            WorldArea area = tl.today.areas[0];
            area.weatherCellCollector.Initialize();
            for (int i = 0; i < area.cells.Length; i++)
            {
                WorldCell c = area.cells[i];
                area.weatherCellCollector.ConsiderCell(i, c.oceanDepth, c.shallowGroundWater, c.temperature, c.windStrength);
            }
            tl.weatherSimulation.Initialize(
                area.weatherCellCollector.GetCellsForWeatherType(WeatherType.Rain),
                area.weatherCellCollector.GetCellsForWeatherType(WeatherType.Snow),
                area.weatherCellCollector.GetCellsForWeatherType(WeatherType.DryThunder),
                area.weatherCellCollector.GetCellsForWeatherType(WeatherType.WetThunder),
                area.weatherCellCollector.GetCellsForWeatherType(WeatherType.Mist),
                area.weatherCellCollector.GetCellsForWeatherType(WeatherType.SandStorm));
        }

        private static TimeLine WireTimeline(GameSaver gs, OrganismObjectFileImporter importer, OrganismObjectFileImporter2 importer2)
        {
            TimeLine tl = new TimeLine(gs.areas, importer, importer2);
            _activeTimeLine = tl;

            tl.allSpecies = gs.allSpecies;
            tl.allAlgaSpecies.Clear();
            tl.allPlantSpecies.Clear();
            tl.allFungusSpecies.Clear();
            tl.allAnimalSpecies.Clear();
            for (int i = 0; i < gs.allSpecies.Count; i++)
            {
                SpeciesDefinition sp = gs.allSpecies[i];
                switch (sp.speciesType)
                {
                    case OrganismType.Alga: tl.allAlgaSpecies.Add(sp); break;
                    case OrganismType.Plant: tl.allPlantSpecies.Add(sp); break;
                    case OrganismType.Fungus: tl.allFungusSpecies.Add(sp); break;
                    case OrganismType.Animal: tl.allAnimalSpecies.Add(sp); break;
                }
            }

            tl.algaClassifier = gs.algaClassifier;
            tl.plantClassifier = gs.plantClassifier;
            tl.animalClassifier = gs.animalClassifier;
            tl.fungusClassifier = gs.fungusClassifier;
            tl.populationBasedAlgaClassifier = gs.populationBasedAlgaClassifier;
            tl.populationBasedPlantClassifier = gs.populationBasedPlantClassifier;
            tl.populationBasedAnimalClassifier = gs.populationBasedAnimalClassifier;
            tl.populationBasedFungusClassifier = gs.populationBasedFungusClassifier;
            tl.speciesIndexPerDefinitionIndex = gs.speciesIndexPerDefinitionIndex;
            tl.parentSpeciesIndexPerSpeciesIndex = gs.parentSpeciesIndexPerSpeciesIndex;
            tl.nextSpeciesIndex = gs.allSpecies.Count;
            tl.nextLifeFormIndex = gs.nextLifeFormIndex;
            tl.nextDefinitionIndex = gs.nextDefinitionIndex;

            tl.algaOrganismCap = gs.algaOrganismCap;
            tl.plantOrganismCap = gs.plantOrganismCap;
            tl.animalOrganismCap = gs.animalOrganismCap;
            tl.fungusOrganismCap = gs.fungusOrganismCap;

            tl.line = gs.worldStates;
            tl.dayIndex = gs.dayIndex;
            WorldState state = tl.line[tl.line.Count - 1];
            state.areas = gs.areas;
            tl.today = state;
            tl.today.weather = tl.climate.CreateNewWeatherState();

            // species population bookkeeping (vanilla adds one row per species, then trims)
            for (int i = 0; i < gs.allSpecies.Count; i++) tl.today.AddSpeciesToPopulationTracking();
            tl.CleanUpPopulationData();

            tl.epochs = gs.epochs;
            tl.historicEvents = gs.historicEvents;
            tl.deathTracker = gs.deathTracker;
            tl.speciesDietTracker = gs.speciesDietTracker;
            tl.eatenByTracker = gs.eatenByTracker;
            tl.terrainHeightChangeMap = gs.terrainHeightChangeMap;
            tl.fungusAdministration = gs.fungusAdministration;
            tl.grassAdministration = gs.grassAdministration;

            tl.globalTemperature = gs.globalTemperature;
            tl.currentSeasonIndex = gs.currentSeasonIndex;
            tl.useSeasons = gs.planet.useSeasons;
            tl.seasons = gs.planet.seasons;
            tl.SetDayDuration(gs.planet.dayDuration);
            if (gs.planet.nightCyclePeriodBoundaries != null && gs.planet.nightCyclePeriodBoundaries.Length == 4)
                tl.nightCyclePeriodBoundaries = gs.planet.nightCyclePeriodBoundaries;
            tl.gravity = gs.planet.gravity;
            tl.volcanoHeightChangeGroupIndices = gs.planet.volcanoHeightChangeGroupIndices ?? new int[0];
            tl.allowedAlgaColors = PlanetBuilder.CalculatePossiblePlantColorsForLightColor(
                gs.planet.redLight, gs.planet.greenLight, gs.planet.blueLight,
                gs.planet.redStable, gs.planet.greenStable, gs.planet.blueStable, true, 0f);
            tl.allowedPlantColors = PlanetBuilder.CalculatePossiblePlantColorsForLightColor(
                gs.planet.redLight, gs.planet.greenLight, gs.planet.blueLight,
                gs.planet.redStable, gs.planet.greenStable, gs.planet.blueStable, false, 0f);

            tl.SetMutationChance(OrganismType.Alga, gs.chanceForAlgaMutation);
            tl.SetMutationChance(OrganismType.Plant, gs.chanceForPlantMutation);
            tl.SetMutationChance(OrganismType.Animal, gs.chanceForAnimalMutation);
            tl.SetMutationChance(OrganismType.Fungus, gs.chanceForFungusMutation);

            // Sandbox mutators (CreateTimeLineForScenario only wires these for Sandbox)
            tl.algaMutator = new AlgaMutator();
            tl.plantMutator = new PlantMutator();
            tl.plantMutator.organismObjectFileImporter = importer;
            tl.animalMutator = new AnimalMutator();
            tl.animalMutator.organismObjectFileImporter = importer;
            tl.fungusMutator = new FungusMutator();
            tl.fungusMutator.importer = importer2;

            tl.chanceOfMeteorites = gs.planet.chanceOfMeteorites;
            tl.chanceOfVolcanoEruptions = gs.planet.chanceOfVolcanoEruptions;
            tl.chanceLightningCausesWildFire = gs.planet.chanceOfForestFire;
            tl.temperatureChangePerDecade = 0f;
            tl.oceanDepthChangePerCentury = 0;
            tl.events = new ScriptedEvent[0];

            InitWeatherFromCells(tl);

            // Vanilla OverallController re-syncs every loaded plant with its cell environment.
            for (int a = 0; a < tl.today.areas.Length; a++)
            {
                WorldArea area = tl.today.areas[a];
                for (int i = 0; i < area.cellIndicesOfPlants.Count; i++)
                {
                    int idx = area.cellIndicesOfPlants[i];
                    WorldCell c = area.cells[idx];
                    LivingPlant lp = c.plant;
                    if (lp == null) continue;
                    lp.windStrength = c.windStrength;
                    lp.environmentTemperature = c.temperature;
                    lp.shallowGroundWater = c.shallowGroundWater;
                    lp.deepGroundWater = c.deepGroundWater;
                    lp.FillStats(false);
                }
            }
            return tl;
        }

        private static void CountWorld(TimeLine tl, out int plants, out int fungi, out int animals,
            out int plankton, out int macroAlgae, out int grassCells, out int dormantSeeds, out int eggs)
        {
            plants = fungi = animals = plankton = macroAlgae = grassCells = dormantSeeds = eggs = 0;
            for (int a = 0; a < tl.today.areas.Length; a++)
            {
                WorldArea area = tl.today.areas[a];
                for (int i = 0; i < area.cells.Length; i++)
                {
                    WorldCell c = area.cells[i];
                    if (c.plant != null) plants++;
                    if (c.fungus != null) fungi++;
                    if (c.plankton != null) plankton++;
                    else if (c.alga != null) macroAlgae++;
                    if (c.grassHeight > 0) grassCells++;
                    if (c.dormantSeed != null) dormantSeeds++;
                    if (c.eggs != null)
                    {
                        for (int e = 0; e < c.eggs.Length; e++) if (c.eggs[e] != null) eggs++;
                    }
                }
                if (area.animalPositionTracker != null && area.animalPositionTracker.animalPositions != null)
                {
                    foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal an = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (an != null && !an.eaten) animals++;
                    }
                }
            }
        }

        private static int GetFirstAnimalMatureAge(TimeLine tl)
        {
            for (int a = 0; a < tl.today.areas.Length; a++)
            {
                foreach (AnimalPosition pos in tl.today.areas[a].animalPositionTracker.animalPositions)
                {
                    LivingAnimal an = tl.today.areas[a].GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (an != null && !an.eaten) return an.matureAtAge;
                }
            }
            return 0;
        }

        private static void CountAnimalSexes(TimeLine tl, out int females, out int males)
        {
            females = males = 0;
            for (int a = 0; a < tl.today.areas.Length; a++)
            {
                foreach (AnimalPosition pos in tl.today.areas[a].animalPositionTracker.animalPositions)
                {
                    LivingAnimal an = tl.today.areas[a].GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (an == null || an.eaten) continue;
                    if (an.definition.currentSex == 0) females++; else males++;
                }
            }
        }

        private static void CountSpeciesAlive(TimeLine tl, out int plantSpp, out int fungusSpp, out int animalSpp, out int algaSpp)
        {
            plantSpp = fungusSpp = animalSpp = algaSpp = 0;
            List<int> counts = tl.today.numberOfLivingOrganismsPerSpecies;
            for (int i = 0; i < tl.allSpecies.Count && i < counts.Count; i++)
            {
                if (counts[i] <= 0) continue;
                switch (tl.allSpecies[i].speciesType)
                {
                    case OrganismType.Plant: plantSpp++; break;
                    case OrganismType.Fungus: fungusSpp++; break;
                    case OrganismType.Animal: animalSpp++; break;
                    case OrganismType.Alga: algaSpp++; break;
                }
            }
        }

        private static bool SaveCheckpoint(TimeLine tl, PlanetSettings planet, OrganismObjectFileImporter importer, OrganismObjectFileImporter2 importer2, string path)
        {
            try
            {
                var scenario = new ScenarioDefinition();
                scenario.index = 999;
                scenario.gameMode = GameMode.Sandbox;
                scenario.milestones = new MilestoneDefinition[0];
                scenario.objective = null;

                var saver = new GameSaver();
                saver.importer = importer;
                saver.importer2 = importer2;
                saver.currentSaveFileFormatVersion = 16;
                saver.savingBytes = new List<byte>();
                saver.flushAfterNrOfBytes = int.MaxValue;
                saver.Encode(true, scenario, planet, tl,
                    true, true, true, true, true, true, true, true, true, true, true,
                    null,
                    new Vector3[0], new Quaternion[0], new Vector3[0], false);
                File.WriteAllBytes(path, saver.savingBytes.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CHECKPOINT] failed: " + ex);
                return false;
            }
        }

        private static void WriteBgString(BinaryWriter bw, string s)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (b.Length > 65535) b = System.Text.Encoding.UTF8.GetBytes((s ?? string.Empty).Substring(0, 20000));
            bw.Write((ushort)b.Length);
            bw.Write(b);
        }

        private static T FindPlantPart<T>(BranchPartDefinition[] stem) where T : class
        {
            if (stem == null) return null;
            for (int i = 0; i < stem.Length; i++)
            {
                BranchPartDefinition b = stem[i];
                if (b == null || b.children == null) continue;
                for (int c = 0; c < b.children.Length; c++)
                {
                    object child = b.children[c];
                    if (child is T) return (T)child;
                    BranchDefinition branch = child as BranchDefinition;
                    if (branch != null)
                    {
                        T nested = FindPlantPart<T>(branch.parts.ToArray());
                        if (nested != null) return nested;
                    }
                }
            }
            return null;
        }

        private static int ExportBiogenesis(string savePath, string outPath, int seed)
        {
            var importer = BuildRealImporter();
            var importer2 = BuildRealImporter2();
            var gs = new GameSaver();
            gs.importer = importer;
            gs.importer2 = importer2;
            gs.currentSaveFileFormatVersion = 16;
            Console.WriteLine("Loading save: " + savePath);
            gs.DecodeFromFile(savePath);
            TimeLine tl = WireTimeline(gs, importer, importer2);
            Console.WriteLine($"Loaded day {tl.dayIndex}, species {tl.allSpecies.Count}, areas {tl.today.areas.Length}");

            WorldArea area = tl.today.areas[0];
            int n = area.cells.Length;

            var plantDefs = new Dictionary<int, PlantDefinition>();
            var plantDefCells = new List<int>();
            for (int i = 0; i < area.cellIndicesOfPlants.Count; i++)
            {
                int idx = area.cellIndicesOfPlants[i];
                LivingPlant p = area.cells[idx].plant;
                if (p == null) continue;
                plantDefCells.Add(idx);
                if (!plantDefs.ContainsKey(p.definition.definitionIndex)) plantDefs[p.definition.definitionIndex] = p.definition;
            }
            var fungusDefs = new Dictionary<int, FungusDefinition>();
            var fungusCells = new List<int>();
            for (int i = 0; i < area.cellIndicesOfFungi.Count; i++)
            {
                int idx = area.cellIndicesOfFungi[i];
                LivingFungus f = area.cells[idx].fungus;
                if (f == null) continue;
                fungusCells.Add(idx);
                if (!fungusDefs.ContainsKey(f.definition.definitionIndex)) fungusDefs[f.definition.definitionIndex] = f.definition;
            }
            var animalDefs = new Dictionary<int, AnimalDefinition>();
            var animals = new List<Tuple<int, CellLocationType, LivingAnimal>>();
            foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
            {
                LivingAnimal a = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (a == null || a.eaten) continue;
                animals.Add(Tuple.Create(pos.cellIndex, pos.locationType, a));
                if (!animalDefs.ContainsKey(a.definition.definitionIndex)) animalDefs[a.definition.definitionIndex] = a.definition;
            }

            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8))
            {
                bw.Write(new byte[] { (byte)'B', (byte)'G', (byte)'S', (byte)'I', (byte)'M', (byte)'1', 0, 0 });
                bw.Write(1); // format version
                bw.Write((ulong)tl.dayIndex);
                bw.Write((byte)(tl.evolution ? 1 : 0));
                bw.Write((uint)area.size.x);
                bw.Write((uint)area.size.y);
                bw.Write(tl.chanceForPlantMutation * 100f);
                bw.Write(tl.chanceForAnimalMutation * 100f);
                bw.Write(tl.chanceForFungusMutation * 100f);
                bw.Write(tl.chanceForAlgaMutation * 100f);
                bw.Write((int)gs.planet.seed);
                bw.Write(tl.globalTemperature);

                // Cells
                bw.Write((uint)n);
                for (int i = 0; i < n; i++)
                {
                    WorldCell c = area.cells[i];
                    bw.Write(c.temperature);
                    bw.Write(c.windStrength);
                    bw.Write(c.shallowGroundWater);
                    bw.Write(c.deepGroundWater);
                    bw.Write(c.oceanDepth);
                    bw.Write(c.oceanCurrentSpeed);
                    bw.Write(c.nutrients);
                    bw.Write((float)c.detritus);
                    bw.Write(c.softness);
                    bw.Write(c.steepness);
                    bw.Write(c.distanceToWater);
                    bw.Write(c.baseTemperature);
                    bw.Write(c.baseWindStrength);
                    bw.Write(c.baseShallowGroundWater);
                    bw.Write((byte)c.climateZone);
                    bw.Write((byte)(c.usable ? 1 : 0));
                    bw.Write((byte)(c.walkable ? 1 : 0));
                    bw.Write((byte)Mathf.Clamp((int)(c.oceanDepth == -1f ? 0.6f : 0.0f) * 255f, 0, 255));
                    // grass
                    bw.Write((byte)Mathf.Clamp(c.grassHeight, 0, 3));
                    bw.Write(c.grassHue);
                    bw.Write((byte)(c.grassRootType == RootType.Tap ? 1 : 0));
                }

                // Plant definitions
                bw.Write((uint)plantDefs.Count);
                foreach (var kv in plantDefs)
                {
                    PlantDefinition d = kv.Value;
                    var pc = new PlantStatsCalculator();
                    try { pc.Calculate(d); } catch { }
                    int speciesIdx = tl.populationBasedPlantClassifier.LookUp(d.definitionIndex);
                    string spName = (speciesIdx >= 0 && speciesIdx < tl.allSpecies.Count) ? tl.allSpecies[speciesIdx].name : ("Plant " + d.definitionIndex);
                    LeafDefinition leafPart = FindPlantPart<LeafDefinition>(d.stem);
                    PollinationOrganDefinition flowerPart = FindPlantPart<PollinationOrganDefinition>(d.stem);
                    SeedDefinition seedPart = FindPlantPart<SeedDefinition>(d.stem);
                    bw.Write(d.definitionIndex);
                    WriteBgString(bw, spName);
                    bw.Write((byte)d.plantSize);
                    bw.Write((byte)(d.rootType == RootType.Tap ? 1 : 0));
                    bw.Write(Mathf.Clamp01((float)d.rootDepth / 3f));
                    bw.Write(0.5f);
                    bw.Write((uint)Mathf.Max(1, pc.nrOfLeaves));
                    bw.Write(pc.minimumTemperatureThisOrganismCanHandle);
                    bw.Write(pc.maximumTemperatureThisOrganismCanHandle);
                    bw.Write(0.10f);
                    bw.Write(0.90f);
                    bw.Write(0.5f);
                    bw.Write((uint)Mathf.Clamp(pc.longestSeedMovementDistance > 0 ? pc.longestSeedMovementDistance : 2, 1, 5));
                    bw.Write(d.colorHue);
                    bw.Write(d.colorBrightness);
                    bw.Write((byte)(int)d.barkType);
                    bw.Write(d.flowerHue);
                    bw.Write(d.fruitHue);
                    bw.Write(d.seedDormantBelowTemperature);
                    bw.Write(d.seedDormantAboveTemperature);
                    bw.Write(d.leafAbscissionBelowTemperature);
                    bw.Write(d.leafAbscissionAboveTemperature);
                    bw.Write(d.flowerAbscissionBelowTemperature);
                    bw.Write(d.flowerAbscissionAboveTemperature);
                    bw.Write((uint)Mathf.Max(0, d.fruitLength));
                    bw.Write((byte)(d.fruitCurved ? 1 : 0));
                    bw.Write((byte)(d.part0Thick ? 1 : 0));
                    bw.Write((byte)(d.part1Thick ? 1 : 0));
                    bw.Write((byte)(d.part2Thick ? 1 : 0));
                    bw.Write(d.fruitPoisonStrength);
                    bw.Write((uint)Mathf.Max(0, d.fruitPoisonType));
                    bw.Write((uint)Mathf.Max(1, pc.nrOfSeeds));
                    bw.Write(speciesIdx);

                    // Organ summary (leaf/flower/seed) used to build the port's stem.
                    bw.Write((byte)(leafPart != null ? 1 : 0));
                    bw.Write((byte)(flowerPart != null ? 1 : 0));
                    bw.Write((byte)(flowerPart != null && flowerPart.usesWindPollination ? 1 : 0));
                    bw.Write((byte)(flowerPart != null && flowerPart.usesAnimalPollination ? 1 : 0));
                    bw.Write(flowerPart != null ? (float)flowerPart.pollenMovementDistance : 0f);
                    bw.Write(flowerPart != null ? flowerPart.minimumTemperature : 0.1f);
                    bw.Write(flowerPart != null ? flowerPart.maximumTemperature : 0.9f);
                    bw.Write(flowerPart != null && flowerPart.glowing ? (byte)1 : (byte)0);
                    bw.Write(flowerPart != null ? (float)flowerPart.nrOfPollen : 0f);
                    bw.Write((byte)(seedPart != null ? 1 : 0));
                    bw.Write(seedPart != null ? (float)seedPart.distanceWithStrongWind : 2f);
                    bw.Write(seedPart != null ? seedPart.extraEnergyForAnimals : 0f);
                    bw.Write(seedPart != null && seedPart.floatsInWater ? (byte)1 : (byte)0);
                    bw.Write(seedPart != null && seedPart.harderToEat ? (byte)1 : (byte)0);
                    bw.Write(seedPart != null && seedPart.survivesBeingEaten ? (byte)1 : (byte)0);
                    bw.Write(seedPart != null && seedPart.requiresFire ? (byte)1 : (byte)0);
                    bw.Write(leafPart != null ? leafPart.minimumTemperature : 0f);
                    bw.Write(leafPart != null ? leafPart.maximumTemperature : 1f);
                }

                // Living plants
                bw.Write((uint)plantDefCells.Count);
                for (int i = 0; i < plantDefCells.Count; i++)
                {
                    int idx = plantDefCells[i];
                    LivingPlant p = area.cells[idx].plant;
                    bw.Write((uint)idx);
                    bw.Write(p.definition.definitionIndex);
                    bw.Write((uint)Mathf.Max(0, p.age));
                    bw.Write(p.health);
                    bw.Write((p.energySavedPerSeed != null && p.energySavedPerSeed.Length > 0) ? p.energySavedPerSeed[0] : 0f);
                }

                // Fungus definitions
                bw.Write((uint)fungusDefs.Count);
                foreach (var kv in fungusDefs)
                {
                    FungusDefinition d = kv.Value;
                    int speciesIdx = tl.populationBasedFungusClassifier.LookUp(d.definitionIndex);
                    string spName = (speciesIdx >= 0 && speciesIdx < tl.allSpecies.Count) ? tl.allSpecies[speciesIdx].name : ("Fungus " + d.definitionIndex);
                    bw.Write(d.definitionIndex);
                    WriteBgString(bw, spName);
                    bw.Write((byte)(int)d.fungusSize);
                    bw.Write(d.colorHue);
                    bw.Write(d.colorBrightness);
                    bw.Write(d.capSize);
                    bw.Write(d.stipeHeight);
                    bw.Write(d.bioluminescence);
                    bw.Write(d.poisonStrength);
                    bw.Write((uint)Mathf.Max(1, d.cap != null ? 60 : 60));
                    bw.Write(0.15f);
                    bw.Write((byte)(d.cap != null ? Mathf.Clamp(d.cap.index, 0, 7) : 0));
                    bw.Write((byte)(d.cap != null ? 1 : 0));
                    bw.Write((byte)(d.myceliumType == Mycelium.Parasite ? 1 : 0));
                    bw.Write(speciesIdx);
                }

                // Living fungi
                bw.Write((uint)fungusCells.Count);
                for (int i = 0; i < fungusCells.Count; i++)
                {
                    int idx = fungusCells[i];
                    LivingFungus f = area.cells[idx].fungus;
                    bw.Write((uint)idx);
                    bw.Write(f.definition.definitionIndex);
                    bw.Write((uint)Mathf.Max(0, f.age));
                    bw.Write(true ? 1f : 0f);
                }

                // Animal definitions
                bw.Write((uint)animalDefs.Count);
                foreach (var kv in animalDefs)
                {
                    AnimalDefinition d = kv.Value;
                    var ac = new AnimalStatsCalculator(importer, 1f);
                    try { ac.Calculate(d, false); } catch { }
                    int speciesIdx = tl.populationBasedAnimalClassifier.LookUp(d.definitionIndex);
                    string spName = (speciesIdx >= 0 && speciesIdx < tl.allSpecies.Count) ? tl.allSpecies[speciesIdx].name : ("Animal " + d.definitionIndex);
                    int sz = d._bodySize <= 8 ? 0 : (d._bodySize <= 12 ? 1 : (d._bodySize <= 16 ? 2 : 3));
                    int diet = (ac.eatsMeat && ac.eatsPlants) ? 2 : (ac.eatsMeat ? 1 : 0);
                    int loco = ac.canFly ? 3 : (ac.canSwim && ac.canWalk ? 2 : (ac.canSwim ? 1 : 0));
                    bw.Write(d.definitionIndex);
                    WriteBgString(bw, spName);
                    bw.Write(speciesIdx);
                    bw.Write((byte)sz);
                    bw.Write((byte)diet);
                    bw.Write((byte)loco);
                    bw.Write((uint)Mathf.Clamp((int)Mathf.Ceil(ac.speed), 1, 3));
                    bw.Write(ac.minimumTemperatureThisOrganismCanHandle);
                    bw.Write(ac.maximumTemperatureThisOrganismCanHandle);
                    bw.Write((uint)Mathf.Clamp((int)Mathf.Ceil(ac.furthestSeeingDistanceDuringDayTime), 1, 4));
                    bw.Write(d.colorHue);
                    bw.Write(d.colorBrightness);
                    bw.Write((byte)(ac.canWalk ? 1 : 0));
                    bw.Write((byte)(ac.canClimb ? 1 : 0));
                    bw.Write((byte)(ac.canFly ? 1 : 0));
                    bw.Write((byte)(ac.canSwim ? 1 : 0));
                    bw.Write((byte)(ac.canGlide ? 1 : 0));
                    bw.Write((byte)0); // canDig (not a definition-level field in retail)
                    bw.Write(ac.maximumSteepness);
                    bw.Write((float)Mathf.Max(0, d._bodySize) * 0.05f);
                    bw.Write((byte)(int)d.sleepPattern);
                    bw.Write(0.10f);
                    bw.Write(0.90f);
                    bw.Write(d._embryoTimeInsideMother);
                    bw.Write((byte)(d.canGiveBirthAsexually ? 1 : 0));
                    bw.Write((byte)(d.canGiveBirthSexually ? 1 : 0));
                    bw.Write((byte)(d.canFertilizeInternally ? 1 : 0));
                    bw.Write((byte)(d.canFertilizeExternally ? 1 : 0));
                    int sexCount = (d.sexes != null ? d.sexes.Length + 1 : 1);
                    if (d.canGiveBirthSexually || d.canFertilizeInternally) sexCount = Mathf.Max(2, sexCount);
                    bw.Write((byte)Mathf.Clamp(sexCount, 1, 8));

                    // Real instinct + hormone genome (retail arrays; the port's InstinctDefinition
                    // has the same field set).
                    InstinctDefinition[] instincts = d.instincts ?? new InstinctDefinition[0];
                    if (instincts.Length == 0 && d._instincts != null) instincts = d._instincts;
                    bw.Write((ushort)instincts.Length);
                    for (int ii = 0; ii < instincts.Length; ii++)
                    {
                        InstinctDefinition ins = instincts[ii];
                        bw.Write((byte)ins.conditionSense);
                        bw.Write((byte)ins.conditionObject);
                        bw.Write(ins.conditionRange.x); bw.Write(ins.conditionRange.y);
                        bw.Write(ins.conditionAdditionalRange.x); bw.Write(ins.conditionAdditionalRange.y);
                        bw.Write(ins.conditionAdditionalRange2.x); bw.Write(ins.conditionAdditionalRange2.y);
                        bw.Write(ins.conditionAdditionalRange3.x); bw.Write(ins.conditionAdditionalRange3.y);
                        int nc = (ins.conditionChoices != null ? ins.conditionChoices.Length : 0);
                        bw.Write((ushort)nc);
                        for (int cc = 0; cc < nc; cc++) bw.Write((byte)(ins.conditionChoices[cc] ? 1 : 0));
                        bw.Write((byte)ins.action);
                        bw.Write(ins.actionChoice);
                        bw.Write(ins.actionValue);
                    }
                    HormoneDefinition[] hormones = d.hormones ?? new HormoneDefinition[0];
                    if (hormones.Length == 0 && d._hormones != null) hormones = d._hormones;
                    bw.Write((ushort)hormones.Length);
                    for (int hi = 0; hi < hormones.Length; hi++)
                    {
                        WriteBgString(bw, hormones[hi].name);
                        bw.Write(hormones[hi].initialLevel);
                        bw.Write(hormones[hi].changePerDay);
                    }
                }

                // Living animals
                bw.Write((uint)animals.Count);
                for (int i = 0; i < animals.Count; i++)
                {
                    var t = animals[i];
                    LivingAnimal a = t.Item3;
                    bw.Write((uint)t.Item1);
                    bw.Write((byte)(int)t.Item2);
                    bw.Write(a.definition.definitionIndex);
                    bw.Write((uint)Mathf.Max(0, a.age));
                    bw.Write(a.health);
                    bw.Write(a.energy);
                    bw.Write(a.oxygen);
                    bw.Write((byte)Mathf.Clamp(a.definition.currentSex, 0, 1));
                    bw.Write((int)(a.pregnant ? 1 : 0));
                    bw.Write((byte)(a.dormant ? 1 : 0));
                    bw.Write((byte)(a.sleeping ? 1 : 0));
                }

                // Species roster (id, isPlant, name)
                bw.Write((uint)tl.allSpecies.Count);
                for (int i = 0; i < tl.allSpecies.Count; i++)
                {
                    SpeciesDefinition sp = tl.allSpecies[i];
                    bw.Write(i);
                    bw.Write((byte)(sp.speciesType == OrganismType.Plant ? 1 : (sp.speciesType == OrganismType.Animal ? 2 : (sp.speciesType == OrganismType.Fungus ? 3 : 0))));
                    WriteBgString(bw, sp.name);
                }
            }
            Console.WriteLine("Wrote " + outPath + " (" + new FileInfo(outPath).Length + " bytes)");
            return 0;
        }

        private static int RunSaveSimulation(string savePath, int days, int reportEvery, int seed, string patchMode)
        {
            string outDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "sim-results");
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(outDir, "save-sim-" + stamp + ".log");
            File.WriteAllText(Path.Combine(outDir, "latest-log.txt"),
                logPath + Environment.NewLine + "days=" + days + " reportEvery=" + reportEvery + " seed=" + seed + " patches=" + patchMode + Environment.NewLine);
            var liveLog = new StreamWriter(logPath, false);
            liveLog.AutoFlush = true;
            Action<string> Log = delegate(string s)
            {
                Console.WriteLine(s);
                Console.Out.Flush();
                liveLog.WriteLine(s);
            };

            _rng = new System.Random(seed);
            // The user's game runs SaplingRustBridge, whose CleanLogPatches preserve the
            // parent's sex through AnimalBlender (vanilla wipes mutated offspring to sex 0).
            _fixBlendSexBug = true;
            _totalMatings = 0; _totalBirths = 0; _birthsF = 0; _birthsM = 0; _totalMutations = 0;
            _allDeaths.Clear();
            _speciesCapHits = 0;

            Log("Building importer (body parts) ...");
            OrganismObjectFileImporter importer = BuildRealImporter();
            OrganismObjectFileImporter2 importer2 = BuildRealImporter2();
            Log($"Importer ready: {importer.precreatedMouths.Count} mouths, {importer.precreatedLimbs.Count} limbs, mushrooms={importer2.GetShapeNamesPerBodyPartName("MushroomCap").Length} caps");

            if (patchMode == "bridge") ApplyBridgePatches(false);
            else if (patchMode == "band") ApplyBandPatchesOnly();
            else if (patchMode == "fastsim") ApplyBridgePatches(false, new Type[] { typeof(SaplingRustBridge.Patches.FastSimPatches) });

            var gs = new GameSaver();
            gs.importer = importer;
            gs.importer2 = importer2;
            gs.currentSaveFileFormatVersion = 16;
            Log("Loading save: " + savePath);
            var loadSw = System.Diagnostics.Stopwatch.StartNew();
            gs.DecodeFromFile(savePath);
            loadSw.Stop();
            Log($"Loaded in {loadSw.Elapsed.TotalSeconds:F1}s: dayIndex={gs.dayIndex}, worldStates={gs.worldStates.Count}, species={gs.allSpecies.Count}, areas={gs.areas.Length}, size={gs.areas[0].size.x}x{gs.areas[0].size.y}");

            TimeLine tl = WireTimeline(gs, importer, importer2);

            int p, f, an, pl, ma, gr, ds, eg;
            CountWorld(tl, out p, out f, out an, out pl, out ma, out gr, out ds, out eg);
            int females, males;
            CountAnimalSexes(tl, out females, out males);
            int ps, fs, ans, als;
            CountSpeciesAlive(tl, out ps, out fs, out ans, out als);
            Log($"State: plants={p}, fungi={f}, animals={an}, plankton={pl}, macroAlgae={ma}, grassCells={gr}, dormantSeeds={ds}, eggs={eg}");
            Log($"Species: {gs.allSpecies.Count} total (plants={ps}, fungi={fs}, animals={ans}, algae={als} with living members)");
            Log($"Mutation %: alga={gs.chanceForAlgaMutation}, plant={gs.chanceForPlantMutation}, animal={gs.chanceForAnimalMutation}, fungus={gs.chanceForFungusMutation}; evolution={tl.evolution}; seasons={tl.useSeasons}");
            Log($"Taxonomy cap: {_speciesCap} species (trait mutations continue; prevents unbounded species-list growth over deep time)");

            // Exact species roster at load (from the save's own allSpecies + population counts).
            {
                string rosterPath = Path.Combine(outDir, "species-at-load-" + stamp + ".csv");
                var roster = new StreamWriter(rosterPath);
                roster.WriteLine("kingdom,index,name,living");
                for (int i = 0; i < gs.allSpecies.Count; i++)
                {
                    int pop = i < tl.today.numberOfLivingOrganismsPerSpecies.Count ? tl.today.numberOfLivingOrganismsPerSpecies[i] : 0;
                    SpeciesDefinition sp = gs.allSpecies[i];
                    roster.WriteLine($"{sp.speciesType},{i},\"{sp.name}\",{pop}");
                }
                roster.Close();
                Log($"Species roster: {rosterPath}");
            }

            // Detailed animal census (sex/age/pregnancy) at load time
            {
                var animalCounts = new Dictionary<int, int[]>();
                var ageSums = new Dictionary<int, long>();
                var preg = new Dictionary<int, int>();
                var fert = new Dictionary<int, int>();
                var children = new Dictionary<int, int>();
                for (int a = 0; a < tl.today.areas.Length; a++)
                {
                    WorldArea area = tl.today.areas[a];
                    foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal anml = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (anml == null || anml.eaten) continue;
                        int sp = tl.usePopulationBasedClassification
                            ? tl.populationBasedAnimalClassifier.LookUp(anml.definition.definitionIndex)
                            : -1;
                        if (!animalCounts.ContainsKey(sp)) { animalCounts[sp] = new int[2]; ageSums[sp] = 0; preg[sp] = 0; fert[sp] = 0; children[sp] = 0; }
                        animalCounts[sp][anml.definition.currentSex == 0 ? 0 : 1]++;
                        ageSums[sp] += anml.age;
                        if (anml.pregnant) preg[sp]++;
                        if (anml.fertilized) fert[sp]++;
                        if (anml.isChild) children[sp]++;
                    }
                }
                foreach (var kv in animalCounts)
                {
                    int total = kv.Value[0] + kv.Value[1];
                    SpeciesDefinition sp = (kv.Key >= 0 && kv.Key < gs.allSpecies.Count) ? gs.allSpecies[kv.Key] : null;
                    Log($"  Animal spp {kv.Key} '{(sp != null ? sp.name : "?")}': {total} (A/F={kv.Value[0]}, B/M={kv.Value[1]}), avgAge={(total > 0 ? (double)ageSums[kv.Key] / total : 0):F0}, matureAt={(total > 0 ? GetFirstAnimalMatureAge(tl) : 0)}, pregnant={preg[kv.Key]}, fertilized={fert[kv.Key]}, children={children[kv.Key]}");
                }
            }

            if (days <= 0) { liveLog.Close(); return 0; }
            if (reportEvery <= 0) reportEvery = 1000;

            string csvPath = Path.Combine(outDir, "save-sim-" + stamp + ".csv");
            string speciesPath = Path.Combine(outDir, "save-sim-species-" + stamp + ".csv");
            var csv = new StreamWriter(csvPath);
            csv.WriteLine("day,plants,fungi,animals,plankton,macroAlgae,grassCells,dormantSeeds,eggs,plantSpp,fungusSpp,animalSpp,algaSpp,totalSpp,matings,births,mutations,daysPerSec,memMB");
            var speciesCsv = new StreamWriter(speciesPath);
            speciesCsv.WriteLine("day,kingdom,speciesIndex,name,population");
            Log($"Results CSV: {csvPath}");
            Log($"Live log:    {logPath}");

            CountSpeciesAlive(tl, out ps, out fs, out ans, out als);
            csv.WriteLine($"{tl.dayIndex},{p},{f},{an},{pl},{ma},{gr},{ds},{eg},{ps},{fs},{ans},{als},{tl.allSpecies.Count},{_totalMatings},{_totalBirths},{_totalMutations},0,{GC.GetTotalMemory(false) / 1048576.0:F0}");
            csv.Flush();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int startDay = tl.dayIndex;
            for (int d = 0; d < days; d++)
            {
                tl.LiveADay();
                int done = d + 1;
                if (done % 1000 == 0)
                {
                    int defCount = tl.speciesIndexPerDefinitionIndex.Count
                        + tl.populationBasedAlgaClassifier.speciesPerDefinition.Count
                        + tl.populationBasedPlantClassifier.speciesPerDefinition.Count
                        + tl.populationBasedAnimalClassifier.speciesPerDefinition.Count
                        + tl.populationBasedFungusClassifier.speciesPerDefinition.Count;
                    if (defCount > 150000) Log(PruneDefinitionMaps(tl));
                }
                bool last = done == days;
                if (last || done % reportEvery == 0)
                {
                    CountWorld(tl, out p, out f, out an, out pl, out ma, out gr, out ds, out eg);
                    CountAnimalSexes(tl, out females, out males);
                    CountSpeciesAlive(tl, out ps, out fs, out ans, out als);
                    double elapsed = sw.Elapsed.TotalSeconds;
                    double dps = elapsed > 0 ? done / elapsed : 0;
                    double etaH = dps > 0 ? (days - done) / dps / 3600.0 : 0;
                    long mem = GC.GetTotalMemory(false) / 1048576;
                    Log($"Day {tl.dayIndex,9} | P={p,5} F={f,4} A={an,5} (F/M={females}/{males}) | grass={gr,5} plank={pl,5} alga={ma,4} seeds={ds,4} eggs={eg,4} | spp alive P={ps} F={fs} A={ans} | totalSpp={tl.allSpecies.Count} capHits={_speciesCapHits} | mat={_totalMatings} births={_totalBirths} mut={_totalMutations} | {dps:F1} d/s ETA {etaH:F1}h mem={mem}MB");
                    if (_allDeaths.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var kv in _allDeaths) parts.Add($"{kv.Key}={kv.Value}");
                        Log("    deaths: " + string.Join(", ", parts));
                    }
                    {
                        double freq = System.Diagnostics.Stopwatch.Frequency;
                        double tp = _profPlants * 1000.0 / freq;
                        double ta = _profAnimals * 1000.0 / freq;
                        double tl2 = _profAlgae * 1000.0 / freq;
                        double tf = _profFungi * 1000.0 / freq;
                        double tt = elapsed * 1000.0;
                        double other = tt - tp - ta - tl2 - tf;
                        Log($"    profile: plants={tp / tt * 100:F1}% animals={ta / tt * 100:F1}% algae={tl2 / tt * 100:F1}% fungi={tf / tt * 100:F1}% other={other / tt * 100:F1}% (plants {tp:F0}ms, animals {ta:F0}ms)");
                        try
                        {
                            var fsType = typeof(SaplingRustBridge.Patches.FastSimPatches);
                            FieldInfo rv = fsType.GetField("_rustVisionAvailable", BindingFlags.Static | BindingFlags.NonPublic);
                            if (rv != null) Log("    rustVisionAvailable=" + rv.GetValue(null));
                            MethodInfo mb = fsType.GetMethod("ConsumeAnimalBreakdown", BindingFlags.Static | BindingFlags.Public);
                            if (mb != null) { string s = mb.Invoke(null, null) as string; if (!string.IsNullOrEmpty(s)) Log("    " + s); }
                            MethodInfo mp = fsType.GetMethod("ConsumePlantBreakdown", BindingFlags.Static | BindingFlags.Public);
                            if (mp != null) { string s2 = mp.Invoke(null, null) as string; if (!string.IsNullOrEmpty(s2)) Log("    " + s2); }
                            double avgRange = _cntInstinct > 0 ? _sumInstinctRange / _cntInstinct : 0;
                            Log($"    instinct: calls={_cntInstinct} hits={_cntInstinctHit} avgDepth={avgRange:F1}");
                            _cntInstinct = 0; _cntInstinctHit = 0; _sumInstinctRange = 0;
                        }
                        catch { }
                    }
                    csv.WriteLine($"{tl.dayIndex},{p},{f},{an},{pl},{ma},{gr},{ds},{eg},{ps},{fs},{ans},{als},{tl.allSpecies.Count},{_totalMatings},{_totalBirths},{_totalMutations},{dps:F1},{mem}");
                    csv.Flush();
                    if (last || done % (reportEvery * 20) == 0)
                    {
                        for (int i = 0; i < tl.allSpecies.Count && i < tl.today.numberOfLivingOrganismsPerSpecies.Count; i++)
                        {
                            int pop = tl.today.numberOfLivingOrganismsPerSpecies[i];
                            if (pop <= 0) continue;
                            SpeciesDefinition sp = tl.allSpecies[i];
                            speciesCsv.WriteLine($"{tl.dayIndex},{sp.speciesType},{i},\"{sp.name}\",{pop}");
                        }
                        speciesCsv.Flush();
                    }
                }
                if (done % 100000 == 0)
                {
                    string ckPath = Path.Combine(outDir, "world-day-" + tl.dayIndex + ".sav16");
                    var ckSw = System.Diagnostics.Stopwatch.StartNew();
                    if (SaveCheckpoint(tl, gs.planet, importer, importer2, ckPath))
                        Log($"[CHECKPOINT] {ckPath} ({new FileInfo(ckPath).Length / 1048576.0:F1} MB, {ckSw.Elapsed.TotalSeconds:F0}s)");
                }
            }
            sw.Stop();
            csv.Close();
            speciesCsv.Close();
            Log($"Done: {startDay} -> {tl.dayIndex} ({days} days) in {sw.Elapsed.TotalHours:F2}h, {days / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} days/s");
            string finalPath = Path.Combine(outDir, "world-day-" + tl.dayIndex + "-final.sav16");
            var finalSw = System.Diagnostics.Stopwatch.StartNew();
            if (SaveCheckpoint(tl, gs.planet, importer, importer2, finalPath))
                Log($"[CHECKPOINT] final world: {finalPath} ({new FileInfo(finalPath).Length / 1048576.0:F1} MB, {finalSw.Elapsed.TotalSeconds:F0}s)");
            Log($"CSV: {csvPath}");
            Log($"Species census: {speciesPath}");
            liveLog.Close();
            return 0;
        }

        // =========================================================================
        // 3. Build our thriving two-sex species ("Solara": Gender A = Female,
        //    Gender B = Male, strictly sexual reproduction, zero asexual fallback).
        // =========================================================================
        private static InstinctDefinition MakeInstinct(InstinctConditionSense sense, InstinctConditionObject obj, InstinctAction action)
        {
            var inst = new InstinctDefinition(sense, obj, 0f, action);
            inst.conditionRange = new Vector2(0f, 1f);
            inst.conditionAdditionalRange = new Vector2(0f, 1f);
            inst.conditionAdditionalRange2 = new Vector2(0f, 1f);
            inst.conditionAdditionalRange3 = new Vector2(0f, 1f);
            inst.nrOfAdditionalRanges = 0;
            inst.conditionChoices = new bool[0];
            inst.actionChoice = 0;
            inst.actionValue = 0f;
            return inst;
        }

        private static AnimalDefinition BuildSolaraDefinition(OrganismObjectFileImporter importer, int bodySize = 11, bool useSlowOnly = false)
        {
            var def = new AnimalDefinition();
            def.definitionIndex = 0;
            def._bodySize = bodySize;
            def._skinType = SkinType.Scaled;
            def._skinPattern = SkinPattern.Plain;
            def._colorHue = 0.11f;          // Gender A (Female): warm golden amber
            def._colorBrightness = 0.68f;
            def._secondaryColorHue = 0.16f;
            def._secondaryColorBrightness = 0.80f;
            def._upright = false;
            def._nestType = NestType.None;
            def._nestEnclosed = false;
            def._embryoTimeInsideMother = 4; // Viviparous (live birth — zero egg vulnerability)
            def._eggType = EggType.Ovoid;
            def._noEggsBelowTemperature = 0f;
            def._noEggsAboveTemperature = 1f;
            def._noHatchBelowTemperature = 0f;
            def._noHatchAboveTemperature = 1f;
            def._sleepPattern = SleepPattern.AtNight; // wait: check enum value 0 below
            def._sleepPattern = (SleepPattern)0;      // NoPattern — stays active day/twilight/night until rest==0

            // Full broad diet color ranges so it eats any grass/plant/fruit color
            def._fruitDietHueRange = new Vector2(0f, 1f);
            def._invertFruitDietColorRange = false;
            def._softPlantTissueDietHueRange = new Vector2(0f, 1f);
            def._softPlantTissueDietBrightnessRange = new Vector2(0f, 1f);
            def._invertSoftPlantTissueDietColorRange = false;
            def._wholeAnimalDietHueRange = new Vector2(0f, 1f);
            def._wholeAnimalDietBrightnessRange = new Vector2(0f, 1f);
            def._wholeAnimalDietSecondaryHueRange = new Vector2(0f, 1f);
            def._wholeAnimalDietSecondaryBrightnessRange = new Vector2(0f, 1f);
            def._invertWholeAnimalDietColorRange = false;
            def._fungusDietHueRange = new Vector2(0f, 1f);
            def._fungusDietBrightnessRange = new Vector2(0f, 1f);
            def._invertFungusDietColorRange = false;
            def._excludeFromDiet = new HashSet<DetailedFoodType>();
            def._excludeFruitShapesFromDiet = new HashSet<FruitShape>();
            def._wholeAnimalDietExcludeSkinPatterns = new HashSet<SkinPattern>();
            def.hormones = new HormoneDefinition[0];

            // 4-segment balanced terrestrial hexapod engineered for EXACT speed = 1.00:
            //   Why speed = 1.00? In The Sapling's ManageTravelsForAnimal, stepping Ceil(speed) cells
            //   toward a mate overshoots onto the mate's occupied cell whenever Ceil(speed) >= dist,
            //   causing the animal to bounce away on a random walk. At speed = 1.00 (Ceil = 1),
            //   every step lands on an empty cell and stops adjacent (dist = 1) -> instant mating!
            // - seg0 (head): saiga mouth (id=32: grass/soft/seed=3, emb=0, heatLoss=+7C, weight=1, air)
            //                + stalk eyes (id=15: 360-deg FOV, 4.8-cell day & twilight sight)
            // - seg1 (chest, diam=7): splithoof legs (id=65: step=2, maxSteepness=1.0, walkW=6, emb=1)
            // - seg2 (mid, diam=6):   slow legs (id=19: step=1, walkW=5, emb=0)
            // - seg3 (hind, diam=3):  slow legs (id=19: step=1, walkW=5, emb=0)
            //   -> speed = 2 - (3-1)*0.5 = 1.00, maxSteepness = 1.00, weightSupported = 16.0!
            var seg0 = new TorsoPartDefinition(0f, 3.0f);
            var mouth = importer.CreateMouthWithShapeIndex(32); // saiga
            mouth.scale = 1.0f;
            var eye = importer.CreateEyeWithShapeIndex(15);     // stalk (360-deg FOV)
            eye.scale = 1.0f;
            seg0.SetChild(mouth, TorsoPartSide.Front);
            seg0.SetChild(eye, TorsoPartSide.Side);

            if (useSlowOnly)
            {
                // Ultra-lean 2-segment body with ZERO limb embryoDevelopmentTime (preg=3d @ sz10, 6d @ sz11!)
                // 1 pair of slow legs -> exact speed=1.00, balanced=true, weightSupported=5.0
                var seg1 = new TorsoPartDefinition(0f, 3.9f);
                var leg = importer.CreateLimbWithShapeIndex(19);  // slow (step=1, emb=0, walkW=5)
                leg.scale = 1.0f;
                var longEye = importer.CreateEyeWithShapeIndex(10); // peduncle
                longEye.scale = 1.0f;
                seg1.SetChild(leg, TorsoPartSide.Side);
                seg1.SetChild(longEye, TorsoPartSide.Top);
                def.torso = new TorsoPartDefinition[] { seg0, seg1 };
            }
            else
            {
                var seg1 = new TorsoPartDefinition(0f, 7.0f);
                var frontLeg = importer.CreateLimbWithShapeIndex(65); // splithoof
                frontLeg.scale = 1.0f;
                var longEye = importer.CreateEyeWithShapeIndex(10);   // peduncle (6.9-cell day sight)
                longEye.scale = 1.0f;
                seg1.SetChild(frontLeg, TorsoPartSide.Side);
                seg1.SetChild(longEye, TorsoPartSide.Top);

                var seg2 = new TorsoPartDefinition(0f, 6.0f);
                var midLeg = importer.CreateLimbWithShapeIndex(19);   // slow
                midLeg.scale = 1.0f;
                seg2.SetChild(midLeg, TorsoPartSide.Side);

                var seg3 = new TorsoPartDefinition(0f, 3.0f);
                var rearLeg = importer.CreateLimbWithShapeIndex(19);  // slow
                rearLeg.scale = 1.0f;
                seg3.SetChild(rearLeg, TorsoPartSide.Side);

                def.torso = new TorsoPartDefinition[] { seg0, seg1, seg2, seg3 };
            }

            // Instincts: thermal safety + active mate-seeking + active foraging!
            def._instincts = new InstinctDefinition[]
            {
                MakeInstinct(InstinctConditionSense.Feel, InstinctConditionObject.TooWarm, InstinctAction.GoAwayFrom),
                MakeInstinct(InstinctConditionSense.Feel, InstinctConditionObject.TooCold, InstinctAction.GoAwayFrom),
                MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, InstinctAction.GoTowards),
                MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.Food, InstinctAction.GoTowards),
            };

            // STRICTLY SEXUAL REPRODUCTION:
            // Gender A (currentSex = 0) = FEMALE (gives birth sexually ONLY, cannot self-fertilize)
            def._canGiveBirthAsexually = false;
            def._canGiveBirthSexually = true;
            def._canFertilizeInternally = false;
            def._canFertilizeExternally = false;

            // 50% Gender A (0) / 50% Gender B (1) offspring sex ratio
            def._offspringSexDecisionType = OffspringSexDecisionType.Chance;
            def._offspringSexChanceBracket0 = 0;
            def._offspringSexChanceBracket1 = 0;
            def._offspringSexChanceBracket2 = 0;
            def._offspringSexChanceBracket3 = 0;
            def._offspringSexChanceBracket4 = 0;
            def._offspringSexChanceBracket5 = 0;
            def._offspringSexChanceBracket6 = 1;
            def._offspringSexChanceBracket7 = 1;
            def._offspringSexChanceBracket8 = 1;
            def._offspringSexChanceBracket9 = 1;
            def._offspringSexChanceBracket10 = 1;
            def._offspringSexChanceBracket11 = 1;
            def._offspringSexTemperatureBracket0 = 0;
            def._offspringSexTemperatureBracket1 = 0;
            def._offspringSexTemperatureBracket2 = 0;
            def._offspringSexTemperatureBracket3 = 0;
            def._offspringSexTemperatureBracket4 = 1;
            def._offspringSexTemperatureBracket5 = 1;
            def._offspringSexTemperatureBracket6 = 1;

            def.sexes = new AnimalMorph[0];
            def.youngSexes = new AnimalMorph[0];
            def.currentSex = 0;

            // Young morph for Gender A (Female)
            def.young = AnimalMorph.CreateMorphBasedOnAnimalDefinition(def);

            // Gender B (currentSex = 1, sexes[0]) = MALE (fertilizes internally ONLY, cannot give birth)
            AnimalMorph maleMorph = AnimalMorph.CreateMorphBasedOnAnimalDefinition(def);
            maleMorph.canGiveBirthAsexually = false;
            maleMorph.canGiveBirthSexually = false;
            maleMorph.canFertilizeInternally = true;
            maleMorph.canFertilizeExternally = false;
            // Visible sexual dimorphism: sapphire teal with golden spots + ornamental head tuft (id=45)
            maleMorph.colorHue = 0.56f;
            maleMorph.colorBrightness = 0.68f;
            maleMorph.secondaryColorHue = 0.12f;
            maleMorph.secondaryColorBrightness = 0.85f;
            maleMorph.skinPattern = SkinPattern.SmallSpots;
            maleMorph.bodyPartDiffs = new Dictionary<BodyPartDiffLocation, BodyPartDiff>();
            var tuftLoc = new BodyPartDiffLocation
            {
                torsoPart = 0,
                side = TorsoPartSide.Top,
                offset = TorsoPartLocationOffset.None
            };
            maleMorph.bodyPartDiffs[tuftLoc] = new BodyPartDiff
            {
                type = BodyPartDiffType.OtherBodyPart,
                otherBodyPartTypeIndex = 10, // AnimalAccessoire
                otherBodyPartShapeIndex = 45, // tuft
                otherScale = 1.0f
            };
            // Copy exact 50/50 sex brackets into male morph too
            maleMorph.offspringSexChanceBracket0 = 0;
            maleMorph.offspringSexChanceBracket1 = 0;
            maleMorph.offspringSexChanceBracket2 = 0;
            maleMorph.offspringSexChanceBracket3 = 0;
            maleMorph.offspringSexChanceBracket4 = 0;
            maleMorph.offspringSexChanceBracket5 = 0;
            maleMorph.offspringSexChanceBracket6 = 1;
            maleMorph.offspringSexChanceBracket7 = 1;
            maleMorph.offspringSexChanceBracket8 = 1;
            maleMorph.offspringSexChanceBracket9 = 1;
            maleMorph.offspringSexChanceBracket10 = 1;
            maleMorph.offspringSexChanceBracket11 = 1;

            AnimalMorph youngMaleMorph = AnimalMorph.Clone(maleMorph);

            def.sexes = new AnimalMorph[] { maleMorph };
            def.youngSexes = new AnimalMorph[] { youngMaleMorph };

            return def;
        }

        // =========================================================================
        // 4. Build a realistic custom world ("Solara Valley", 40x40 = 1,600 cells)
        //    with coastline, varied hills, latitude temperature gradient, and grass.
        // =========================================================================
        private static TimeLine BuildCustomWorld(OrganismObjectFileImporter importer, int size, out WorldArea area)
        {
            area = new WorldArea();
            area.debugName = "SolaraValley";
            area.size = new Vector2(size, size);
            area.sunLightFactor = 1f;
            area.useLocationGroups = false;
            int n = size * size;
            area.cells = new WorldCell[n];
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    int idx = x * size + y;
                    bool isCoast = (x < 2 || y < 2 || x >= size - 2 || y >= size - 2);
                    float oceanDepth = isCoast ? 0.5f : -1f;
                    // Realistic temperature gradient: north = cool (0.45 = -5C), center = temperate (0.62 = +12C),
                    // south = warm (0.78 = +28C, plus a hot pocket at 0.88 = +38C to test heat-avoidance!).
                    float lat = (float)x / (float)(size - 1);
                    float temp = 0.48f + lat * 0.28f;
                    if (x >= size - 5 && y >= size - 5 && !isCoast) temp = 0.88f; // 38C hot zone (> 33C maxTemp)

                    float steep = isCoast ? 0.05f : (0.05f + 0.35f * (float)Math.Abs(Math.Sin(x * 0.35) * Math.Cos(y * 0.35)));
                    float shallowWater = isCoast ? 1f : 0.60f;
                    float deepWater = isCoast ? 1f : 0.65f;
                    float softness = 0.65f;
                    float wind = 0.25f;

                    var cell = new WorldCell(softness, shallowWater, deepWater, temp, wind, oceanDepth, 0.1f, steep);
                    cell.usable = true;
                    cell.walkable = true;
                    cell.buildable = !isCoast;
                    cell.nutrients = 0.9f;
                    cell.detritus = 2;
                    cell.distanceToWater = isCoast ? 0f : (float)Math.Min(Math.Min(x - 1, y - 1), Math.Min(size - 2 - x, size - 2 - y));
                    area.cells[idx] = cell;
                }
            }

            // Link closest land/water cells (used by pregnant animals if needed)
            int firstLand = 2 * size + 2;
            int firstWater = 0;
            for (int i = 0; i < n; i++)
            {
                area.cells[i].closestLandCellIndex = firstLand;
                area.cells[i].closestAquaticCellIndex = firstWater;
            }

            var tl = new TimeLine(new WorldArea[] { area }, importer, null);
            tl.evolution = true;
            tl.sexualReproduction = true;
            tl.chanceForAnimalMutation = 0.05f;
            tl.chanceForPlantMutation = 0.02f;
            tl.globalTemperature = 0.62f;
            tl.globalLandTemperature = 0.62f;
            tl.globalMoisture = 0.65f;
            tl.chanceLightningCausesWildFire = 0f;
            tl.chanceOfMeteorites = 0f;
            tl.chanceOfVolcanoEruptions = 0f;
            tl.animalMutator = new AnimalMutator();
            tl.animalMutator.organismObjectFileImporter = importer;
            tl.plantMutator = new PlantMutator();
            tl.plantMutator.organismObjectFileImporter = importer;
            tl.fungusMutator = new FungusMutator();
            tl.animalOrganismCap = -1; // uncapped natural carrying capacity

            // Seed lush grass ecosystem across the valley
            int grassDefIdx = tl.nextDefinitionIndex++;
            for (int x = 3; x < size - 3; x += 2)
            {
                for (int y = 3; y < size - 3; y += 2)
                {
                    int idx = x * size + y;
                    tl.AddGrassToCell(area, 0, idx, 2f, 0.33f, 0.55f, 1, 0.33f, RootType.Fibrous, GrassSeedType.SticksToFur, grassDefIdx, -1, true);
                }
            }
            // Let grass establish for 15 days before introducing Solara
            for (int d = 0; d < 15; d++)
            {
                tl.LiveADay();
            }

            return tl;
        }

        private static void SpawnSolara(TimeLine tl, AnimalDefinition baseDef, int cellIdx, int sex)
        {
            AnimalDefinition instDef = (AnimalDefinition)baseDef.Clone();
            instDef.definitionIndex = baseDef.definitionIndex;
            instDef.currentSex = sex;
            tl.nextSpeciesName = "Solara";
            tl.InsertAnimal(instDef, 0, cellIdx, CellLocationType.Ground, true, -1);
            // Match adult manual spawn (as in CleanLogPatches #15)
            LivingAnimal placed = tl.today.areas[0].GetAnimalInCell(cellIdx, CellLocationType.Ground);
            if (placed != null)
            {
                placed.isChild = false;
                placed.UpdateTemperature(tl.today.areas[0].cells[cellIdx].temperature);
            }
        }

        private static void CountSolaraPop(WorldArea area, out int total, out int females, out int males, out int pregnant, out int fertilized, out int children)
        {
            total = females = males = pregnant = fertilized = children = 0;
            foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
            {
                LivingAnimal a = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (a == null || a.eaten) continue;
                total++;
                if (a.definition.currentSex == 0) females++;
                else males++;
                if (a.pregnant) pregnant++;
                if (a.fertilized) fertilized++;
                if (a.isChild) children++;
            }
        }

        private static void RunExperiment(string label, OrganismObjectFileImporter importer, AnimalDefinition baseDef, int numFemales, int numMales, int days, int seed, float animalMutChance, bool fixBlendSex)
        {
            _rng = new System.Random(seed);
            _fixBlendSexBug = fixBlendSex;
            _totalMatings = 0;
            _totalBirths = 0;
            _birthsF = 0;
            _birthsM = 0;
            _totalMutations = 0;
            _allDeaths.Clear();
            int size = 40;
            WorldArea area;
            TimeLine tl = BuildCustomWorld(importer, size, out area);
            tl.chanceForAnimalMutation = animalMutChance;
            tl.evolution = (animalMutChance > 0f || tl.chanceForPlantMutation > 0f);

            baseDef.definitionIndex = tl.nextDefinitionIndex++;

            int cx = size / 2;
            int cy = size / 2;
            int offset = 0;
            for (int i = 0; i < numFemales; i++)
            {
                SpawnSolara(tl, baseDef, (cx + offset) * size + cy, 0);
                offset += 2;
            }
            for (int i = 0; i < numMales; i++)
            {
                SpawnSolara(tl, baseDef, (cx + offset) * size + (cy + 1), 1);
                offset += 2;
            }

            Console.WriteLine();
            Console.WriteLine($"=====================================================================");
            Console.WriteLine($"EXPERIMENT: {label} (Founders: {numFemales} Female[A] + {numMales} Male[B], {days} days)");
            Console.WriteLine($"=====================================================================");

            int peakPop = numFemales + numMales;
            int peakDay = 0;
            int minPopAfter100 = int.MaxValue;
            int grassCells = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int d = 1; d <= days; d++)
            {
                tl.LiveADay();
                int tot, fem, mal, preg, fert, kids;
                CountSolaraPop(area, out tot, out fem, out mal, out preg, out fert, out kids);
                if (tot > peakPop) { peakPop = tot; peakDay = d; }
                if (d >= 100 && tot < minPopAfter100) minPopAfter100 = tot;

                if (d == 50 || d == 100 || d == 250 || d == 500 || (d % 1000 == 0) || d == days || (tot == 0 && d < 300))
                {
                    grassCells = 0;
                    for (int ci = 0; ci < area.cells.Length; ci++) if (area.cells[ci].grassHeight > 0) grassCells++;
                    int animalSpeciesCount = 0;
                    for (int s = 0; s < tl.allSpecies.Count; s++)
                        if (tl.allSpecies[s].speciesType == OrganismType.Animal) animalSpeciesCount++;
                    Console.WriteLine(
                        $"  Day {d,5} | Total={tot,4} (Female[A]={fem,3}, Male[B]={mal,3}, Preg={preg,3}, Fert={fert,3}, Young={kids,3}) | Matings={_totalMatings,6} | AnimalSpp={animalSpeciesCount,2} | Grass={grassCells,4}");
                    if (d == 250 && tot > 0)
                    {
                        int asexCnt = 0, tempDec = 0, s0Only = 0, canFertCnt = 0, canBirthCnt = 0;
                        float sumP0 = 0f; int p0Cnt = 0;
                        foreach (AnimalPosition p in area.animalPositionTracker.animalPositions)
                        {
                            LivingAnimal a = area.GetAnimalInCell(p.cellIndex, p.locationType);
                            if (a == null || a.eaten) continue;
                            if (a.definition.canGiveBirthAsexually) asexCnt++;
                            if (a.definition.canFertilizeInternally) canFertCnt++;
                            if (a.definition.canGiveBirthSexually) canBirthCnt++;
                            if (a.definition.sexes == null || a.definition.sexes.Length == 0) s0Only++;
                            if (a.definition.offspringSexDecisionType == OffspringSexDecisionType.Temperature) tempDec++;
                            if (a.offSpringSexChances != null && a.offSpringSexChances.Length > 0) { sumP0 += a.offSpringSexChances[0]; p0Cnt++; }
                        }
                        Console.WriteLine($"    [Diag d250] asex={asexCnt}, canBirth={canBirthCnt}, canFert={canFertCnt}, noSexes={s0Only}, tempDec={tempDec}, avgP0={(p0Cnt > 0 ? sumP0 / p0Cnt : -1f):F2}");
                    }
                    if (tot == 0) break;
                }
            }
            sw.Stop();

            int finalTot, finalFem, finalMal, finalPreg, finalFert, finalKids;
            CountSolaraPop(area, out finalTot, out finalFem, out finalMal, out finalPreg, out finalFert, out finalKids);
            Console.WriteLine($"  --- Summary ({sw.Elapsed.TotalSeconds:F2}s, {days / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} days/s) ---");
            Console.WriteLine($"  Final Day {days}: Living={finalTot} (Female[A]={finalFem}, Male[B]={finalMal}), Peak={peakPop} (day {peakDay}), MinAfterDay100={(minPopAfter100 == int.MaxValue ? 0 : minPopAfter100)}");
            Console.WriteLine($"  Cumulative: Matings={_totalMatings}, LiveBirths={_totalBirths} (F={_birthsF}, M={_birthsM}), Mutations={_totalMutations}, AnimalSpecies={tl.allAnimalSpecies.Count}");

            if (_allDeaths.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in _allDeaths) parts.Add($"{kv.Key}={kv.Value}");
                Console.WriteLine("  Death causes: " + string.Join(", ", parts));
            }
        }

        public static int Run(string[] args)
        {
            InstallHeadlessShims();

            Console.WriteLine("Loading real game BodyPartStats YAML & JSON definitions...");
            OrganismObjectFileImporter importer = BuildRealImporter();
            Console.WriteLine($"Loaded: {importer.precreatedMouths.Count} mouths, {importer.precreatedLimbs.Count} limbs, {importer.precreatedEyes.Count} eyes, {importer.precreatedEars.Count} ears, {importer.precreatedAnimalAccessoires.Count} accessories.");

            // Export the loaded retail save into the Rust port's importer format:
            //   SimLab.exe --export-biogenesis "<save.sav16>" "<out.bgsim>"
            if (args != null && args.Length > 2 && args[0] == "--export-biogenesis")
            {
                return ExportBiogenesis(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 42);
            }

            // Load a real save and continue its ecosystem for N days:
            //   SimLab.exe --save "<path.sav16>" [days] [reportEvery] [seed]
            if (args != null && args.Length > 0 && args[0] == "--save")
            {
                string path = args.Length > 1 ? args[1] : "";
                int days = args.Length > 2 ? int.Parse(args[2]) : 0;
                int reportEvery = args.Length > 3 ? int.Parse(args[3]) : 1000;
                int seed = args.Length > 4 ? int.Parse(args[4]) : 20261125;
                string patchMode = args.Length > 5 ? args[5] : "";
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine("Save file not found: " + path);
                    return 1;
                }
                return RunSaveSimulation(path, days, reportEvery, seed, patchMode);
            }

            // Quick 3-seed screening of top candidate life-history builds if --screen passed
            if (args != null && args.Length > 0 && args[0] == "--screen")
            {
                var configs = new[]
                {
                    Tuple.Create("sz10-slow(emb0)", 10, true),
                    Tuple.Create("sz11-slow(emb0)", 11, true),
                    Tuple.Create("sz12-slow(emb0)", 12, true),
                    Tuple.Create("sz12-hex(steep1)", 12, false),
                    Tuple.Create("sz13-hex(steep1)", 13, false),
                };
                foreach (var cfg in configs)
                {
                    var cand = BuildSolaraDefinition(importer, cfg.Item2, cfg.Item3);
                    cand.currentSex = 0;
                    var yc = new AnimalStatsCalculator(importer, 1f); yc.Calculate(cand, true);
                    var ac = new AnimalStatsCalculator(importer, 1f); ac.Calculate(cand, false);
                    var p0 = OrganismProblemDetection.DetectProblemInAnimal(cand, yc, ac);
                    cand.currentSex = 1;
                    var yc1 = new AnimalStatsCalculator(importer, 1f); yc1.Calculate(cand, true);
                    var ac1 = new AnimalStatsCalculator(importer, 1f); ac1.Calculate(cand, false);
                    var p1 = OrganismProblemDetection.DetectProblemInAnimal(cand, yc1, ac1);
                    cand.currentSex = 0;
                    if (p0 != OrganismProblem.NoProblem || p1 != OrganismProblem.NoProblem)
                    {
                        Console.WriteLine($"[SKIP] {cfg.Item1}: p0={p0}, p1={p1}");
                        continue;
                    }
                    int surv = 0; int maxPk = 0; int sumFinal = 0;
                    foreach (int sd in new[] { 1001, 2002, 3003, 4004, 5005 })
                    {
                        _rng = new System.Random(sd); _fixBlendSexBug = true;
                        _totalMatings = 0; _totalBirths = 0; _birthsF = 0; _birthsM = 0; _totalMutations = 0; _allDeaths.Clear();
                        WorldArea wa; TimeLine tl = BuildCustomWorld(importer, 40, out wa);
                        tl.chanceForAnimalMutation = 0.05f;
                        cand.definitionIndex = tl.nextDefinitionIndex++;
                        for (int i = 0; i < 3; i++) SpawnSolara(tl, cand, (18 + i * 2) * 40 + 20, 0);
                        for (int i = 0; i < 3; i++) SpawnSolara(tl, cand, (18 + i * 2) * 40 + 21, 1);
                        int pk = 6;
                        for (int d = 1; d <= 5000; d++)
                        {
                            tl.LiveADay();
                            int c = wa.animalPositionTracker.animalPositions.Count;
                            if (c > pk) pk = c;
                            if (c == 0) break;
                        }
                        int fin = wa.animalPositionTracker.animalPositions.Count;
                        if (fin > 0) surv++;
                        if (pk > maxPk) maxPk = pk;
                        sumFinal += fin;
                    }
                    Console.WriteLine($"[CAND] {cfg.Item1,-17} | age={ac.expectedAge,3}d preg={ac.pregnancyTime,2}d spd={ac.speed:F2} stp={ac.maximumSteepness:F1} w={ac.weight:F1}/{ac.weightSupportedForWalking:F1} | 5-seed={surv}/5 avgFinal={sumFinal / 5.0:F1} maxPeak={maxPk}");
                }
                return 0;
            }

            AnimalDefinition solara = BuildSolaraDefinition(importer, 13, false);

            // A/B harness: same worlds/seeds with (vanilla | patched | jump) = retail engine alone,
            // retail + SaplingRustBridge sim patches (normal play), or those + IsTimeJumpActive (Time Jump).
            if (args != null && args.Length > 0 && args[0] == "--ab")
            {
                string mode = args.Length > 1 ? args[1] : "vanilla";
                int nSeeds = args.Length > 2 ? int.Parse(args[2]) : 5;
                int abDays = args.Length > 3 ? int.Parse(args[3]) : 3000;
                bool jump = mode == "jump";
                if (mode != "vanilla") ApplyBridgePatches(jump);
                Console.WriteLine($"[AB] mode={mode} seeds={nSeeds} days={abDays}");
                int alive = 0;
                for (int s = 0; s < nSeeds; s++)
                {
                    int sd = 1001 + s * 1001;
                    _rng = new System.Random(sd); _fixBlendSexBug = true;
                    _totalMatings = 0; _totalBirths = 0; _birthsF = 0; _birthsM = 0; _totalMutations = 0; _allDeaths.Clear();
                    WorldArea wa; TimeLine tl = BuildCustomWorld(importer, 40, out wa);
                    tl.chanceForAnimalMutation = 0.05f;
                    solara.definitionIndex = tl.nextDefinitionIndex++;
                    for (int i = 0; i < 3; i++) SpawnSolara(tl, solara, (18 + i * 2) * 40 + 20, 0);
                    for (int i = 0; i < 3; i++) SpawnSolara(tl, solara, (18 + i * 2) * 40 + 21, 1);
                    var trace = new System.Text.StringBuilder();
                    int pk = 6, extinctDay = -1;
                    for (int d = 1; d <= abDays; d++)
                    {
                        if (jump) tl.JumpAhead(1); else tl.LiveADay();
                        int c = wa.animalPositionTracker.animalPositions.Count;
                        if (c > pk) pk = c;
                        if (d % 500 == 0) trace.Append($" d{d}={c}");
                        if (c == 0) { extinctDay = d; break; }
                    }
                    int f = wa.animalPositionTracker.animalPositions.Count;
                    if (f > 0) alive++;
                    var dp = new List<string>(); foreach (var kv in _allDeaths) dp.Add($"{kv.Key}={kv.Value}");
                    Console.WriteLine($"  seed {sd}: final={f} peak={pk} extinctDay={extinctDay} matings={_totalMatings} births={_totalBirths}(F{_birthsF}/M{_birthsM}) |{trace}");
                    Console.WriteLine($"      deaths: {string.Join(", ", dp)}");
                }
                Console.WriteLine($"[AB] mode={mode}: {alive}/{nSeeds} worlds alive at day {abDays}");
                return 0;
            }

            // Validate BOTH sexes with the game's own AnimalStatsCalculator + OrganismProblemDetection!
            for (int sex = 0; sex <= 1; sex++)
            {
                solara.currentSex = sex;
                var youngCalc = new AnimalStatsCalculator(importer, 1f);
                youngCalc.Calculate(solara, true);
                var adultCalc = new AnimalStatsCalculator(importer, 1f);
                adultCalc.Calculate(solara, false);
                OrganismProblem prob = OrganismProblemDetection.DetectProblemInAnimal(solara, youngCalc, adultCalc);

                string label = (sex == 0) ? "Gender A (Female)" : "Gender B (Male)";
                Console.WriteLine();
                Console.WriteLine($"=== {label} Stats (Problem={prob}) ===");
                Console.WriteLine($"  Sexual flags: asex={solara.canGiveBirthAsexually}, sexBirth={solara.canGiveBirthSexually}, fertInt={solara.canFertilizeInternally}, fertExt={solara.canFertilizeExternally}");
                Console.WriteLine($"  Movement: canWalk={adultCalc.canWalk}, balanced={adultCalc.balancedForWalking}, canClimb={adultCalc.canClimb}, speed={adultCalc.speed:F2}, maxSteep={adultCalc.maximumSteepness:F2}, weight={adultCalc.weight:F2}/{adultCalc.weightSupportedForWalking:F2}");
                Console.WriteLine($"  Survival: temp=[{adultCalc.minimumTemperatureThisOrganismCanHandle:F1}C .. {adultCalc.maximumTemperatureThisOrganismCanHandle:F1}C], avoidsHeat={adultCalc.avoidsHeat}, avoidsCold={adultCalc.avoidsCold}, timeOnLand={adultCalc.timeOnLand}, timeInWater={adultCalc.timeInWater}");
                Console.WriteLine($"  Life: expectedAge={adultCalc.expectedAge}d, matureAt={adultCalc.matureAtAge}d, preg={adultCalc.pregnancyTime}d, viviparous={adultCalc.viviparous}, energyToRepro={adultCalc.energyNeededToReproduce}, attract={adultCalc.attractiveness:F2}");
                Console.WriteLine($"  Vision: FOV={adultCalc.fieldOfView}, daySight={adultCalc.furthestSeeingDistanceDuringDayTime:F1}, twiSight={adultCalc.furthestSeeingDistanceAtTwilight:F1}");
                Console.WriteLine($"  Diet: {string.Join(", ", adultCalc.orderedDetailedFoodTypes)}");
                if (adultCalc.offSpringSexChances != null)
                {
                    Console.WriteLine($"  Offspring sex ratio: Female[A]={adultCalc.offSpringSexChances[0] * 100f:F0}%, Male[B]={(1f - adultCalc.offSpringSexChances[0]) * 100f:F0}%");
                }
                if (prob != OrganismProblem.NoProblem)
                {
                    Console.Error.WriteLine($"ERROR: {label} failed OrganismProblemDetection: {prob}");
                    return 1;
                }
            }
            solara.currentSex = 0;

            // 1. NEGATIVE CONTROL: 6 Females ONLY (0 Males) -> MUST produce 0 births & go extinct!
            RunExperiment("NEGATIVE CONTROL (Females Only — proves NO asexual reproduction)", importer, solara, 6, 0, 300, 101, 0f, false);

            // 2. PURE VANILLA (unmodified engine, no mutation sex-wipe): 1F+1M and 3F+3M for 10,000 days!
            RunExperiment("PURE VANILLA — MINIMAL PAIR (1F + 1M, 10,000 days, mut=0)", importer, solara, 1, 1, 10000, 202, 0f, false);
            RunExperiment("PURE VANILLA — FOUNDING GROUP (3F + 3M, 10,000 days, mut=0)", importer, solara, 3, 3, 10000, 303, 0f, false);

            // 3. EVOLUTION ON (mut=5%) WITH 1-line AnimalBlender.currentSex preservation fix: 1F+1M & 3F+3M for 10,000 days!
            RunExperiment("EVOLUTION ON (mut=5%, BlendSex fix) — MINIMAL PAIR (1F + 1M, 10,000 days)", importer, solara, 1, 1, 10000, 202, 0.05f, true);
            RunExperiment("EVOLUTION ON (mut=5%, BlendSex fix) — FOUNDING GROUP (3F + 3M, 10,000 days)", importer, solara, 3, 3, 10000, 303, 0.05f, true);

            // 4. MULTI-SEED ROBUSTNESS CHECK (5 independent worlds x 5,000 days each — Evolution ON + BlendSex fix)
            Console.WriteLine();
            Console.WriteLine("=====================================================================");
            Console.WriteLine("MULTI-SEED ROBUSTNESS SWEEP (5 seeds, 3F+3M, 5,000d, Evolution ON + BlendSex fix)");
            Console.WriteLine("=====================================================================");
            int survivedSeeds = 0;
            int[] seeds = new[] { 1001, 2002, 3003, 4004, 5005 };
            foreach (int sd in seeds)
            {
                _rng = new System.Random(sd);
                _fixBlendSexBug = true;
                _totalMatings = 0; _totalBirths = 0; _birthsF = 0; _birthsM = 0; _totalMutations = 0; _allDeaths.Clear();
                WorldArea wa;
                TimeLine tl = BuildCustomWorld(importer, 40, out wa);
                tl.chanceForAnimalMutation = 0.05f;
                solara.definitionIndex = tl.nextDefinitionIndex++;
                for (int i = 0; i < 3; i++) SpawnSolara(tl, solara, (18 + i * 2) * 40 + 20, 0);
                for (int i = 0; i < 3; i++) SpawnSolara(tl, solara, (18 + i * 2) * 40 + 21, 1);
                int pk = 6;
                for (int d = 1; d <= 5000; d++)
                {
                    tl.LiveADay();
                    int c = wa.animalPositionTracker.animalPositions.Count;
                    if (c > pk) pk = c;
                    if (c == 0) break;
                }
                int ft, ff, fm, fp, fz, fk;
                CountSolaraPop(wa, out ft, out ff, out fm, out fp, out fz, out fk);
                if (ft > 0) survivedSeeds++;
                Console.WriteLine($"  Seed {sd}: FinalDay5000={ft,4} (Female[A]={ff,3}, Male[B]={fm,3}) | Peak={pk,4} | Matings={_totalMatings,6} | Births={_totalBirths,6} (F={_birthsF},M={_birthsM}) | Mut={_totalMutations,5} | Spp={tl.allAnimalSpecies.Count}");
            }
            Console.WriteLine($"  Robustness Result: {survivedSeeds}/{seeds.Length} independent worlds thrived through Day 5,000!");

            // 4. Export real Solara.species16 to ArchivedAnimals and verify round-trip load!
            Directory.CreateDirectory(ArchiveDir);
            string outPath = Path.Combine(ArchiveDir, "Solara.species16");
            solara.definitionIndex = -1;
            solara.currentSex = 0;
            var sp = new SpeciesDefinition(solara, "Solara", 0);
            sp.speciesType = OrganismType.Animal;
            sp.manuallyAdded = true;
            sp.birthPlaces = new List<int>();
            sp.numberOfYearsNotExtinct = 0;
            sp.peakNumberOfLivingOrganisms = 0;
            sp.extinctSinceYear = -1;

            var saver = new GameSaver();
            saver.importer = importer;
            saver.savingBytes = new List<byte>();
            saver.AddSpecies(sp);
            byte[] bytes = saver.savingBytes.ToArray();
            File.WriteAllBytes(outPath, bytes);
            Console.WriteLine();
            Console.WriteLine($"Saved archive: {outPath} ({bytes.Length} bytes)");

            // Round-trip verification via retail GameSaver.GetSpecies()!
            var loader = new GameSaver();
            loader.importer = importer;
            loader.currentSaveFileFormatVersion = saver.currentSaveFileFormatVersion;
            loader.loadingBytes = bytes;
            SpeciesDefinition loaded = loader.GetSpecies();
            AnimalDefinition lDef = (AnimalDefinition)loaded.exampleOrganism;
            lDef.currentSex = 0;
            bool fBirth = lDef.canGiveBirthSexually && !lDef.canGiveBirthAsexually && !lDef.canFertilizeInternally;
            lDef.currentSex = 1;
            bool mFert = lDef.canFertilizeInternally && !lDef.canGiveBirthSexually && !lDef.canGiveBirthAsexually;
            Console.WriteLine($"Round-trip GameSaver.GetSpecies() OK: name={loaded.name}, sexes={lDef.sexes.Length + 1}, Female[A]Valid={fBirth}, Male[B]Valid={mFert}");

            return (fBirth && mFert) ? 0 : 2;
        }
    }
}
