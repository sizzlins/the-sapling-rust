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
        private static Dictionary<DeathCause, int> _carnDeaths = new Dictionary<DeathCause, int>();
        private static int _carnSpeciesMin = int.MaxValue;

        // Deep-time guardrails: keep taxonomy/definition maps bounded for multi-hundred-
        // thousand-day runs. Trait mutations still happen; new species stop at the cap.
        private static TimeLine _activeTimeLine = null;
        private static int _speciesCap = 0; // uncapped (user rejected taxonomy/population caps)
        private static long _speciesCapHits = 0;

        private sealed class NoOpCallable : Boo.Lang.ICallable
        {
            public object Call(object[] args) => null;
        }

        // --- lightweight profiling of the four kingdom passes ---
        private static long _profPlants, _profAnimals, _profAlgae, _profFungi;
        private static long _cntPlants, _cntAnimals, _cntAlgae, _cntFungi;

        private static void ProfPlants_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfPlants_Postfix(long __state) { _profPlants += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntPlants++; }
        private static void ProfAnimals_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfAnimals_Postfix(long __state) { _profAnimals += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntAnimals++; }
        private static void ProfAlgae_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfAlgae_Postfix(long __state) { _profAlgae += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntAlgae++; }
        private static void ProfFungi_Prefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void ProfFungi_Postfix(long __state) { _profFungi += System.Diagnostics.Stopwatch.GetTimestamp() - __state; _cntFungi++; }

        private static int _spaceTol;
        private static readonly Dictionary<LivingAnimal, int> _moveTrack = new Dictionary<LivingAnimal, int>();

        // SIM_TRACK_MOVE=<minCells>: log any animal whose cell jumps more than minCells (Chebyshev) in one day.
        private static void TrackMoves(TimeLine tl, Dictionary<LivingAnimal, int> prev, bool before, int day)
        {
            WorldArea area = tl.today.areas[0];
            int w = (int)area.size.x, h = (int)area.size.y;
            int min = int.TryParse(Environment.GetEnvironmentVariable("SIM_TRACK_MOVE"), out int m) && m > 1 ? m : 8;
            if (before) prev.Clear();
            foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
            {
                LivingAnimal an = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (an == null || an.eaten) continue;
                if (before) { prev[an] = pos.cellIndex; continue; }
                if (!prev.TryGetValue(an, out int old)) continue;
                int dx = Math.Abs(pos.cellIndex % w - old % w), dy = Math.Abs(pos.cellIndex / w - old / w);
                if (Math.Max(dx, dy) <= min) continue;
                int nx = pos.cellIndex % w, ny = pos.cellIndex / w;
                Console.WriteLine($"[JUMP] day={tl.dayIndex} meat={an.eatsMeat} def={an.definition.definitionIndex} age={an.age} child={an.isChild} ({old % w},{old / w})->({nx},{ny}) of {w}x{h} edge={(nx == 0 || ny == 0 || nx == w - 1 || ny == h - 1)} moving={an.moving} walk={an.canWalk} speed={an.speed:F1} loc={pos.locationType} lastLog={an.lastLog}");
            }
        }
        private static void SpaceTol_Postfix(ref int __result) { if (__result <= _spaceTol) __result = 0; }
        private static long _lsN, _lsOutTemp, _lsStarving, _lsNearDeath, _lsAdult, _lsAlone;
        private static double _lsAge, _lsHealth, _lsMaxHealth, _lsHLoss, _lsLife, _lsEnergy, _lsELoss, _lsExp, _lsDet, _lsMat, _lsSize, _lsNb;
        private static int _lsMinX = 9999, _lsMaxX = -1, _lsMinY = 9999, _lsMaxY = -1;
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

        private static Exception AnimalEx_Finalizer(LivingAnimal __instance, Exception __exception)
        {
            if (__exception != null)
            {
                int defIdx = (__instance != null && __instance.definition != null) ? __instance.definition.definitionIndex : -1;
                Console.WriteLine($"[ANIMAL-EX] def={defIdx} age={(__instance != null ? __instance.age : -1)} sex={(__instance != null && __instance.definition != null ? __instance.definition.currentSex : -1)} ex={__exception.GetType().Name}: {__exception.Message}");
            }
            return __exception;
        }

        private static Exception TimelineAnimalsEx_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Console.WriteLine("[TIMELINE-ANIMAL-EX] " + __exception.GetType().Name);
                if (Environment.GetEnvironmentVariable("SIM_ANIMAL_SAFE") == "1") return null;
            }
            return __exception;
        }

        private static void AuditAnimals_Prefix(TimeLine __instance, int areaIndex)
        {
            if (Environment.GetEnvironmentVariable("SIM_AUDIT_ANIMALS") != "1") return;
            if (__instance == null || __instance.today == null || areaIndex >= __instance.today.areas.Length) return;
            WorldArea area = __instance.today.areas[areaIndex];
            int shown = 0;
            foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
            {
                LivingAnimal a = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (a == null || a.eaten) continue;
                var nulls = new List<string>();
                foreach (FieldInfo f in typeof(LivingAnimal).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!f.FieldType.IsClass && !f.FieldType.IsArray) continue;
                    if (f.GetValue(a) == null) nulls.Add("animal." + f.Name);
                }
                if (a.definition != null)
                {
                    foreach (FieldInfo f in typeof(AnimalDefinition).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (!f.FieldType.IsClass && !f.FieldType.IsArray) continue;
                        if (f.GetValue(a.definition) == null) nulls.Add("def." + f.Name);
                    }
                }
                if (nulls.Count > 0 && (shown < 6 || a.definition?.definitionIndex == 17641))
                {
                    Console.WriteLine($"[AUDIT] def={a.definition?.definitionIndex} age={a.age} nulls={string.Join(",", nulls)}");
                    shown++;
                }
            }
            Console.WriteLine($"[AUDIT] animals={area.animalPositionTracker.animalPositions.Count}");
        }

        private static Exception GenericEx_Finalizer(MethodBase __originalMethod, Exception __exception)
        {
            if (__exception != null)
                Console.WriteLine($"[EX] {__originalMethod.DeclaringType.Name}.{__originalMethod.Name}: {__exception.GetType().Name}");
            return __exception;
        }

        // Why aren't animals breeding? Histogram of sex, sexes-array length, sex-chance arrays,
        // and which gate of LiveADay's reproduction branch rejects each living animal.
        private static int _problemSex;
        private static void ProblemSex_Prefix(AnimalDefinition animal)
        {
            if (animal != null) _problemSex = animal.currentSex;
        }
        // DetectProblemInAnimal walks every morph by overwriting currentSex; an exception
        // (the world's malformed animals throw NREs) leaves the animal stuck as male.
        private static void ProblemSex_Finalizer(AnimalDefinition animal)
        {
            if (animal != null) animal.currentSex = _problemSex;
        }

        // Persistent 50/50: GiveBirth reads the parent's brackets through AnimalStatsCalculator,
        // so fixing the saved definitions is what makes a save stay balanced outside SimLab.
        private static void FixSexBrackets(AnimalDefinition d)
        {
            d._offspringSexDecisionType = OffspringSexDecisionType.Chance;
            d._offspringSexChanceBracket0 = 0; d._offspringSexChanceBracket1 = 0; d._offspringSexChanceBracket2 = 0;
            d._offspringSexChanceBracket3 = 0; d._offspringSexChanceBracket4 = 0; d._offspringSexChanceBracket5 = 0;
            d._offspringSexChanceBracket6 = 1; d._offspringSexChanceBracket7 = 1; d._offspringSexChanceBracket8 = 1;
            d._offspringSexChanceBracket9 = 1; d._offspringSexChanceBracket10 = 1; d._offspringSexChanceBracket11 = 1;
            if (d.sexes != null)
                for (int i = 0; i < d.sexes.Length; i++)
                    FixMorphBrackets(ref d.sexes[i]);
            if (d.youngSexes != null)
                for (int i = 0; i < d.youngSexes.Length; i++)
                    FixMorphBrackets(ref d.youngSexes[i]);
        }

        private static void FixMorphBrackets(ref AnimalMorph m)
        {
            m.offspringSexDecisionType = OffspringSexDecisionType.Chance;
            m.offspringSexChanceBracket0 = 0; m.offspringSexChanceBracket1 = 0; m.offspringSexChanceBracket2 = 0;
            m.offspringSexChanceBracket3 = 0; m.offspringSexChanceBracket4 = 0; m.offspringSexChanceBracket5 = 0;
            m.offspringSexChanceBracket6 = 1; m.offspringSexChanceBracket7 = 1; m.offspringSexChanceBracket8 = 1;
            m.offspringSexChanceBracket9 = 1; m.offspringSexChanceBracket10 = 1; m.offspringSexChanceBracket11 = 1;
        }

        private static void LogSexDiag(TimeLine tl, Action<string> log)
        {
            var bySex = new SortedDictionary<int, int>();
            var sexesLens = new Dictionary<string, int>();
            var chances = new Dictionary<string, int>();
            var defs = new HashSet<AnimalDefinition>();
            var spN = new Dictionary<int, int>();
            var spC = new Dictionary<int, int>();
            int preg = 0, fert = 0, blockedNoBirth = 0, blockedFlag = 0, blockedMeat = 0, blockedEnergy = 0, kids = 0;
            int nf = 0, nready = 0, kf = 0, km = 0; double fE = 0, fT = 0;
            int rdyNest = 0, rdyEgg = 0, rdySpace = 0, rdyWalk = 0, rdyWater = 0;
            int nCarn = 0, nHerb = 0, nOmni = 0, nMeatOnly = 0;
            int nRealPred = 0, nRealHerb = 0, nRealOmni = 0, nMouthMeat = 0, nFungOnly = 0;
            int ocN = 0, ocF = 0, ocFert = 0, ocPreg = 0, ocPoisoned = 0, ocParasites = 0;
            double ocPoison = 0;
            for (int ai = 0; ai < tl.today.areas.Length; ai++)
            {
                WorldArea a = tl.today.areas[ai];
                foreach (AnimalPosition pos in a.animalPositionTracker.animalPositions)
                {
                    LivingAnimal an = a.GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (an == null || an.eaten || an.definition == null) continue;
                    int sex = an.definition.currentSex;
                    defs.Add(an.definition);
                    bySex[sex] = bySex.TryGetValue(sex, out int c) ? c + 1 : 1;
                    if (an.pregnant) preg++;
                    if (an.fertilized) fert++;
                    if (an.isChild) { kids++; if (an.definition.currentSex == 0) kf++; else km++; }
                    if (an.definition.currentSex == 0) { nf++; fE += an.energy; fT += an.CalculateReproductionThreshold(); if (an.readyToGiveBirth) nready++; }
                    if (an.readyToGiveBirth)
                    {
                        WorldCell wc = a.cells[pos.cellIndex];
                        if (!tl.CellContainsCorrectNestForAnimal(wc, an)) rdyNest++;
                        if (!tl.EggCanSurviveInCell(wc, an)) rdyEgg++;
                        if (!tl.EnoughSpaceForReproduction(an, a, ai, pos.cellIndex, pos.locationType)) rdySpace++;
                        if (!(wc.oceanDepth != -1f || an.canWalk || an.sessile)) rdyWalk++;
                        if (!(an.timeInWater == -1 || pos.locationType != CellLocationType.UnderWater)) rdyWater++;
                    }
                    if (an.eatsMeatOnly) nMeatOnly++;
                    if (an.eatsMeat && an.eatsPlants) nOmni++;
                    else if (an.eatsMeat) nCarn++;
                    else nHerb++;
                    float meatE = 0f, plantE = 0f, fungE = 0f;
                    if (an.orderedDetailedFoodTypes != null && an.energyPerDetailedFoodType != null)
                        for (int fi = 0; fi < an.orderedDetailedFoodTypes.Length && fi < an.energyPerDetailedFoodType.Length; fi++)
                        {
                            float ev = an.energyPerDetailedFoodType[fi];
                            if (ev <= 0f) continue;
                            switch (an.orderedDetailedFoodTypes[fi])
                            {
                                case DetailedFoodType.WholeAnimal:
                                case DetailedFoodType.FreshMeat:
                                case DetailedFoodType.OldMeat:
                                case DetailedFoodType.Egg:
                                case DetailedFoodType.Blood: meatE += ev; break;
                                case DetailedFoodType.Fungus: fungE += ev; break;
                                case DetailedFoodType.Plankton:
                                case DetailedFoodType.MarineSnow: break;
                                default: plantE += ev; break;
                            }
                        }
                    bool realPred = meatE > 0f;
                    if (realPred && plantE + fungE <= 0f) nRealPred++;
                    else if (realPred) nRealOmni++;
                    else nRealHerb++;
                    if (an.eatsMeat && !realPred) nMouthMeat++;
                    if (fungE > 0f && plantE <= 0f && meatE <= 0f) nFungOnly++;
                    if (IsObligateCarnivoreDef(an.definition)) { ocN++; if (an.definition.currentSex == 0) ocF++; if (an.fertilized) ocFert++; if (an.pregnant) ocPreg++; ocPoison += an.dailyPoisonDamage; if (an.dailyPoisonDamage > 0f) ocPoisoned++; if (an.parasiteDamage > 0f) ocParasites++; }
                    int spx = tl.populationBasedAnimalClassifier.LookUp(an.definition.definitionIndex);
                    if (spx >= 0)
                    {
                        spN[spx] = spN.TryGetValue(spx, out int sc1) ? sc1 + 1 : 1;
                        if (realPred) spC[spx] = spC.TryGetValue(spx, out int sc2) ? sc2 + 1 : 1;
                    }
                    if (!an.definition.CanGiveBirth()) blockedNoBirth++;
                    else if (!(an.definition.canGiveBirthAsexually || an.fertilized)) blockedFlag++;
                    else if (an.eatsMeat && an.nrOfChildren >= an.maximumNrOfChildren) blockedMeat++;
                    else if (an.energy <= an.CalculateReproductionThreshold()) blockedEnergy++;
                    string sk = "sexes=" + (an.definition.sexes == null ? -1 : an.definition.sexes.Length);
                    sexesLens[sk] = sexesLens.TryGetValue(sk, out int c2) ? c2 + 1 : 1;
                    string ck = "dec=" + an.definition.offspringSexDecisionType + " ch=" + (an.offSpringSexChances == null ? "null" : string.Join("/", an.offSpringSexChances));
                    chances[ck] = chances.TryGetValue(ck, out int c3) ? c3 + 1 : 1;
                    if (an.readyToGiveBirth && nready <= 6)
                    {
                        WorldCell wc = a.cells[pos.cellIndex];
                        log($"  [SEXDIAG] ready sex={sex} nest={tl.CellContainsCorrectNestForAnimal(wc, an)} egg={tl.EggCanSurviveInCell(wc, an)} space={tl.EnoughSpaceForReproduction(an, a, ai, pos.cellIndex, pos.locationType)} walk={an.canWalk} ocean={wc.oceanDepth} inWater={an.timeInWater} loc={pos.locationType} eggType={an.definition.eggType} nestType={an.definition.nestType}");
                    }
                }
            }
            log($"  [RDY] n={nready} badNest={rdyNest} badEgg={rdyEgg} badSpace={rdySpace} badWalk={rdyWalk} badWater={rdyWater}");
            var bySexStr = new List<string>();
            foreach (var kv in bySex) bySexStr.Add(kv.Key + ":" + kv.Value);
            log($"[SEXDIAG] bySex=[{string.Join(",", bySexStr)}] preg={preg} fert={fert} kids={kids}(F={kf}/M={km}) blockedNoBirth={blockedNoBirth} blockedFlag={blockedFlag} blockedMeatCap={blockedMeat} blockedEnergy={blockedEnergy} females={nf} avgE={(nf > 0 ? fE / nf : 0):F1} avgThr={(nf > 0 ? fT / nf : 0):F1} ready={nready} distinctDefs={defs.Count}");
            foreach (var kv in sexesLens) log($"  [SEXDIAG] {kv.Key} n={kv.Value}");
            log($"  [DIET] mouth-can-eat: herbivores={nHerb} carnivores={nCarn} omnivores={nOmni} meatOnly={nMeatOnly}");
            log($"  [DIET2] actual-energy: predators={nRealPred} herbivores={nRealHerb} omnivores={nRealOmni} mouthMeatNoMeatEnergy={nMouthMeat} fungusOnly={nFungOnly}");
            log($"  [PRED] obligate-carnivores={ocN} females={ocF} males={ocN - ocF} fertilized={ocFert} pregnant={ocPreg} poisoned={ocPoisoned} avgPoisonDmg={(ocN > 0 ? ocPoison / ocN : 0):F2} parasites={ocParasites}");
            var order = new List<int>(spN.Keys);
            order.Sort((x, y) => spN[y].CompareTo(spN[x]));
            int shownSp = 0;
            foreach (int spx in order)
            {
                if (shownSp++ >= 25) break;
                spC.TryGetValue(spx, out int cc);
                string nm = (spx >= 0 && spx < tl.allSpecies.Count) ? tl.allSpecies[spx].name : "?";
                log($"  [SP] {spx} '{nm}' n={spN[spx]} pred={cc}");
            }
            foreach (var kv in chances) log($"  [SEXDIAG] {kv.Key} n={kv.Value}");
        }

        private static int _giveBirthLogs = 0;
        private static void GiveBirth_Audit_Prefix(LivingAnimal __instance)
        {
            if (Environment.GetEnvironmentVariable("SIM_AUDIT_SEX") != "1") return;
            if (__instance == null || __instance.definition == null || _giveBirthLogs >= 10) return;
            _giveBirthLogs++;
            Console.WriteLine($"[BIRTH] parentSex={__instance.definition.currentSex} sexes={__instance.definition.sexes.Length} dec={__instance.definition.offspringSexDecisionType} ch={(__instance.offSpringSexChances == null ? "null" : string.Join("/", __instance.offSpringSexChances))} fert={__instance.fertilized}");
        }

        private static int _birthPost = 0;
        private static void GiveBirth_Postfix(LivingAnimal __result)
        {
            if (Environment.GetEnvironmentVariable("SIM_AUDIT_SEX") != "1") return;
            if (_birthPost >= 20) return;
            _birthPost++;
            Console.WriteLine($"[BIRTHOUT] childSex={(__result != null && __result.definition != null ? __result.definition.currentSex : -1)} def={(__result != null && __result.definition != null ? __result.definition.definitionIndex : -1)}");
        }

        private static void AuditLiveADay_Prefix(LivingAnimal __instance)
        {
            if (Environment.GetEnvironmentVariable("SIM_AUDIT_ANIMALS") != "1") return;
            if (__instance != null && __instance.definition != null)
                Console.WriteLine($"[LIVE] def={__instance.definition.definitionIndex} age={__instance.age} sex={__instance.definition.currentSex} pregnant={__instance.pregnant} carrying={__instance.carryingBaby}");
        }

        private static bool CanGiveBirth_Prefix(AnimalDefinition __instance, ref bool __result)
        {
            if (__instance != null && __instance.currentSex > 0)
            {
                __result = false;
                return false;
            }
            return true;
        }

        private static void SanitizeAnimalDef(AnimalDefinition d)
        {
            if (d == null) return;
            if (d._instincts == null) d._instincts = new InstinctDefinition[0];
            if (d._hormones == null) d._hormones = new HormoneDefinition[0];
            if (d.sexes == null) d.sexes = new AnimalMorph[0];
            if (d.youngSexes == null) d.youngSexes = new AnimalMorph[0];
        }

        private static void SanitizeClone_Postfix(object __result)
        {
            SanitizeAnimalDef(__result as AnimalDefinition);
        }

        // Offspring sex is otherwise read from mutated Chance/Temperature brackets, which is why
        // the evolved world drifted to 96% one sex. Every stats refresh resets it to a fair 50/50;
        // GiveBirth reads the parent's field, so this covers every newborn.
        private static void BalanceSex_Postfix(AnimalDefinition animal, AnimalStatsCalculator __instance)
        {
            if (animal == null || animal.sexes == null || animal.sexes.Length == 0) return;
            animal.offspringSexDecisionType = OffspringSexDecisionType.Chance;
            if (__instance != null) __instance.offSpringSexChances = new float[] { 0.5f, 0.5f };
        }

        // Aquara is the only species that both fertilizes internally and bears young; give it the
        // top current tolerance so seasonal current spikes stop killing the whole population.
        private static void CurrentTolerance_Postfix(AnimalDefinition animal, AnimalStatsCalculator __instance)
        {
            if (animal == null || __instance == null) return;
            if (animal._canGiveBirthSexually && animal._canFertilizeInternally && !animal._canGiveBirthAsexually)
                __instance.maximumOceanCurrent = 1f;
        }

        private static void FixBlendSex_Postfix(AnimalDefinition animalA, AnimalDefinition __result)
        {
            if (animalA != null && __result != null)
            {
                SanitizeAnimalDef(__result);
                if (_fixBlendSexBug)
                {
                    int sex = animalA.currentSex;
                    __result.currentSex = (__result.sexes != null && sex >= 0 && sex <= __result.sexes.Length) ? sex : 0;
                }
            }
        }

        private static int _aquaSpecies = -1;
        private static Dictionary<DeathCause, int> _aquaDeaths = new Dictionary<DeathCause, int>();

        private static void DeathCause_Prefix(DeathCause deathCause, OrganismType organismType)
        {
            if (organismType != OrganismType.Animal) return;
            _aquaDeaths.TryGetValue(deathCause, out int c);
            _aquaDeaths[deathCause] = c + 1;
        }

        private static void TrackDeath_Postfix(int speciesId, DeathCause cause)
        {
            int cur;
            _allDeaths.TryGetValue(cause, out cur);
            _allDeaths[cause] = cur + 1;
            if (speciesId >= _carnSpeciesMin)
            {
                _carnDeaths.TryGetValue(cause, out cur);
                _carnDeaths[cause] = cur + 1;
            }
            if (_activeTimeLine != null && _activeTimeLine.populationBasedAnimalClassifier != null)
            {
                int sp = _activeTimeLine.populationBasedAnimalClassifier.LookUp(speciesId);
                // This world starts with no animals, so any animal death is the seeded species.
                if (sp >= 0 && sp < _activeTimeLine.allSpecies.Count && _activeTimeLine.allSpecies[sp].speciesType == OrganismType.Animal)
                {
                    _aquaDeaths.TryGetValue(cause, out cur);
                    _aquaDeaths[cause] = cur + 1;
                }
            }
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
            var simTypes = new HashSet<Type> { typeof(TimeLine), typeof(WorldState), typeof(AnimalBlender), typeof(AnimalMutator), typeof(PopulationBasedClassifier) };
            Type[] classes = onlyClasses ?? new Type[]
            {
                typeof(SaplingRustBridge.Patches.BandPatches),
                typeof(SaplingRustBridge.Patches.SimulationPatches),
                typeof(SaplingRustBridge.Patches.TimeJumpPatches),
                typeof(SaplingRustBridge.Patches.FastSimPatches),
                typeof(SaplingRustBridge.Patches.CleanLogPatches),
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
                    if ((t == typeof(SaplingRustBridge.Patches.TimeJumpPatches) || t == typeof(SaplingRustBridge.Patches.CleanLogPatches)) && !simTypes.Contains(info.declaringType)) continue;
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
            h.Patch(typeof(AnimalDefinition).GetMethod("Clone"),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SanitizeClone_Postfix), flags)));
            if (Environment.GetEnvironmentVariable("SIM_NO_MALE_BIRTH") == "1")
                h.Patch(typeof(AnimalDefinition).GetMethod("CanGiveBirth"),
                    prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(CanGiveBirth_Prefix), flags)));
            h.Patch(AccessTools.Method(typeof(TimeLine), "RegisterDeathCause"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(DeathCause_Prefix), flags)));
            h.Patch(AccessTools.Method(typeof(AnimalStatsCalculator), "Calculate", new[] { typeof(AnimalDefinition), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(BalanceSex_Postfix), flags)));
            h.Patch(AccessTools.Method(typeof(AnimalStatsCalculator), "Calculate", new[] { typeof(AnimalDefinition), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(CurrentTolerance_Postfix), flags)));
            h.Patch(AccessTools.Method(typeof(LivingAnimal), "LiveADay", new[] { typeof(int) }),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(AuditLiveADay_Prefix), flags)),
                finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(AnimalEx_Finalizer), flags)));
            h.Patch(AccessTools.Method(typeof(LivingAnimal), "UpdateStats", new[] { typeof(OrganismObjectFileImporter), typeof(float) }),
                finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(AnimalEx_Finalizer), flags)));
            h.Patch(AccessTools.Method(typeof(OrganismProblemDetection), "DetectProblemInAnimal"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProblemSex_Prefix), flags)),
                finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(ProblemSex_Finalizer), flags)));
            h.Patch(AccessTools.Method(typeof(LivingAnimal), "GiveBirth", new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(GiveBirth_Audit_Prefix), flags)),
                postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(GiveBirth_Postfix), flags)),
                finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(AnimalEx_Finalizer), flags)));
            h.Patch(typeof(TimeLine).GetMethod("LetAllAnimalsInAreaLiveADay"),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(AuditAnimals_Prefix), flags)),
                finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(TimelineAnimalsEx_Finalizer), flags)));

            // Experiment: only caller is EnoughSpaceForReproduction (needs result <= 1). Tolerate up to N neighbours.
            if (int.TryParse(Environment.GetEnvironmentVariable("SIM_SPACE_TOL"), out _spaceTol) && _spaceTol > 1)
                h.Patch(AccessTools.Method(typeof(TimeLine), "FindNumberOfAnimalsAroundCell"),
                    postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SpaceTol_Postfix), flags)));

            foreach (string probe in new[] { "BabyGetsBorn", "CellContainsCorrectNestForAnimal", "EggCanSurviveInCell", "EnoughSpaceForReproduction" })
            {
                MethodInfo pm = AccessTools.Method(typeof(TimeLine), probe);
                if (pm != null)
                    h.Patch(pm, finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(GenericEx_Finalizer), flags)));
            }

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
            tl.animalGetsEatenCallBack = new NoOpCallable();
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

            // Vanilla GameSaver.GetLivingAnimal reloads every animal with gravity=1, so animals
            // evolved on a low-gravity planet come back overweight with canWalk=false and can
            // never deliver (the birth gate needs canWalk || sessile). Recompute with the planet's
            // real gravity before anything else touches them.
            tl.gravity = gs.planet.gravity;
            for (int ga = 0; ga < tl.today.areas.Length; ga++)
            {
                WorldArea garea = tl.today.areas[ga];
                foreach (AnimalPosition gpos in garea.animalPositionTracker.animalPositions)
                {
                    LivingAnimal gani = garea.GetAnimalInCell(gpos.cellIndex, gpos.locationType);
                    if (gani != null && !gani.eaten && gani.definition != null) gani.UpdateStats(importer, tl.gravity);
                }
            }

            if (Environment.GetEnvironmentVariable("SIM_REPAIR_SEX") == "1")
            {
                var mates = new List<Tuple<int, int>>(); // female cell, age
                for (int a = 0; a < tl.today.areas.Length; a++)
                {
                    WorldArea area = tl.today.areas[a];
                    foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal an = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (an == null || an.definition == null) continue;
                        AnimalDefinition d = (AnimalDefinition)an.definition.Clone();
                        // The save has no male genome. Synthesise one from the female's base
                        // morph; a flag flip alone produces a male with no male morphology.
                        AnimalMorph maleMorph = AnimalMorph.Clone(d.young);
                        maleMorph.canGiveBirthAsexually = false;
                        maleMorph.canGiveBirthSexually = false;
                        maleMorph.canFertilizeInternally = true;
                        maleMorph.canFertilizeExternally = false;
                        d.sexes = new AnimalMorph[] { maleMorph };
                        d.youngSexes = new AnimalMorph[] { AnimalMorph.Clone(maleMorph) };
                        d.currentSex = 0;
                        d._offspringSexDecisionType = OffspringSexDecisionType.Chance;
                        d.offspringSexDecisionType = OffspringSexDecisionType.Chance;
                        d._canGiveBirthAsexually = false;
                        d._canGiveBirthSexually = true;
                        d._canFertilizeInternally = false;
                        // The save's instincts may not include mate seeking; without it a
                        // two-sex population barely pairs. Add the stock See/APotentialMate rule.
                        bool hasMateInstinct = false;
                        foreach (InstinctDefinition inst in d.instincts)
                            if (inst.conditionObject == InstinctConditionObject.APotentialMate) { hasMateInstinct = true; break; }
                        if (!hasMateInstinct)
                        {
                            var list = new List<InstinctDefinition>(d.instincts);
                            list.Add(new InstinctDefinition(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, 0.5f, InstinctAction.GoTowards));
                            d.instincts = list.ToArray();
                        }
                        an.definition = d;
                        an.age = Mathf.Max(1, an.matureAtAge);
                        an.isChild = false;
                        an.energy = Mathf.Max(an.energy, 60f);
                        an.UpdateStats(importer, 1f);
                        an.offSpringSexChances = new float[] { 0.5f, 0.5f };
                        mates.Add(Tuple.Create(pos.cellIndex, an.age));
                    }
                }
                // Put one male beside each female so the sparse save can actually pair.
                for (int m = 0; m < mates.Count; m++)
                {
                    WorldArea area = tl.today.areas[0];
                    int femaleCell = mates[m].Item1;
                    int maleCell = area.GetEmptyCellNearCell(femaleCell, CellLocationType.Ground);
                    if (maleCell == -1) continue;
                    LivingAnimal female = area.GetAnimalInCell(femaleCell, CellLocationType.Ground);
                    if (female == null) continue;
                    AnimalDefinition md = (AnimalDefinition)female.definition.Clone();
                    md.currentSex = 1;
                    md.canFertilizeInternally = true;
                    md.canGiveBirthSexually = false;
                    md.canGiveBirthAsexually = false;
                    LivingAnimal male = new LivingAnimal(md);
                    male.age = mates[m].Item2;
                    male.isChild = false;
                    male.energy = 60f;
                    male.UpdateStats(importer, 1f);
                    male.offSpringSexChances = new float[] { 0.5f, 0.5f };
                    area.AddAnimalToCell(male, maleCell, CellLocationType.Ground);
                }
                Console.WriteLine($"[SIM] sex repaired: {mates.Count} females, one synthesised male each");
            }
            if (Environment.GetEnvironmentVariable("SIM_SKIP_ANIMALS") == "1")
            {
                for (int a = 0; a < tl.today.areas.Length; a++)
                {
                    WorldArea area = tl.today.areas[a];
                    area.animalPositionTracker.animalPositions.Clear();
                    for (int i = 0; i < area.cells.Length; i++)
                    {
                        WorldCell c = area.cells[i];
                        c.animal = null;
                        c.animalInTree = null;
                        c.animalInTheAir = null;
                        c.animalUnderWater = null;
                        c.animalAtWaterSurface = null;
                        c.animalInDeepSea = null;
                    }
                }
                Console.WriteLine("[SIM] animals removed (retail animal pass NREs on this save)");
            }

            // The new save's animal genomes can omit instinct/hormone/morph arrays entirely;
            // vanilla's animal pass assumes they are non-null and NREs on a null `.Length`.
            for (int a = 0; a < tl.today.areas.Length; a++)
            {
                foreach (AnimalPosition pos in tl.today.areas[a].animalPositionTracker.animalPositions)
                {
                    LivingAnimal an = tl.today.areas[a].GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (an == null || an.definition == null) continue;
                    AnimalDefinition d = an.definition;
                    if (d._instincts == null) d._instincts = new InstinctDefinition[0];
                    if (d._hormones == null) d._hormones = new HormoneDefinition[0];
                    if (d.sexes == null) d.sexes = new AnimalMorph[0];
                    if (d.youngSexes == null) d.youngSexes = new AnimalMorph[0];
                    if (an.filteringCells == null) an.filteringCells = new List<int>();
                    if (an.offSpringSexChances == null) an.offSpringSexChances = new float[] { 0.5f, 0.5f };
                }
            }

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

        // The save's morphs make one sex out-survive the other, so the live ratio drifts to ~90%
        // male even with 50/50 births. Re-deal the two live sexes per species so both stay present.
        // shortcut: fixes the ratio, not the underlying survival asymmetry, revisit if morphs are rebuilt
        private static int BalanceSexes(TimeLine tl)
        {
            var fem = new Dictionary<int, List<LivingAnimal>>();
            var mal = new Dictionary<int, List<LivingAnimal>>();
            foreach (WorldArea a in tl.today.areas)
                foreach (AnimalPosition pos in a.animalPositionTracker.animalPositions)
                {
                    LivingAnimal an = a.GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (an == null || an.eaten || an.definition == null) continue;
                    if (an.definition.sexes == null || an.definition.sexes.Length == 0) continue;
                    if (IsObligateCarnivoreDef(an.definition)) continue;
                    int sp = tl.populationBasedAnimalClassifier.LookUp(an.definition.definitionIndex);
                    if (sp < 0) continue;
                    var map = an.definition.currentSex == 0 ? fem : mal;
                    if (!map.TryGetValue(sp, out List<LivingAnimal> list)) map[sp] = list = new List<LivingAnimal>();
                    list.Add(an);
                }
            int flipped = 0;
            var allSp = new HashSet<int>(fem.Keys);
            foreach (int k in mal.Keys) allSp.Add(k);
            foreach (int sp in allSp)
            {
                fem.TryGetValue(sp, out List<LivingAnimal> flist);
                mal.TryGetValue(sp, out List<LivingAnimal> mlist);
                int nf2 = flist != null ? flist.Count : 0, nm2 = mlist != null ? mlist.Count : 0;
                int total = nf2 + nm2;
                if (total < 4) continue;
                int wantF = total / 2;
                if (nf2 < wantF && mlist != null)
                {
                    for (int i = 0; i < mlist.Count && nf2 < wantF; i++)
                    {
                        MakeFemale(mlist[i].definition);
                        nf2++;
                        flipped++;
                    }
                }
                else if (nf2 > wantF + 1 && flist != null)
                {
                    for (int i = 0; i < flist.Count && nf2 > wantF; i++)
                    {
                        MakeMale(flist[i].definition);
                        nf2--;
                        flipped++;
                    }
                }
            }
            return flipped;
        }

        private static void MakeFemale(AnimalDefinition d)
        {
            d.currentSex = 0;
            d._canGiveBirthAsexually = true;
            d._canGiveBirthSexually = true;
            d._canFertilizeInternally = false;
        }

        private static void MakeMale(AnimalDefinition d)
        {
            if (d.sexes == null || d.sexes.Length == 0) return;
            d.currentSex = 1;
            d.canGiveBirthAsexually = true;
            d.canGiveBirthSexually = false;
            d.canFertilizeInternally = true;
        }

        // Mate-finding relies on the See/APotentialMate instinct, which almost never fires across
        // 5000 cells, so a sparse two-sex population dies out. Pair each unfertilized female with
        // any living male of her species instead; GiveBirth still blends both genomes.
        private static int AssignMates(TimeLine tl)
        {
            var males = new Dictionary<int, List<AnimalDefinition>>();
            var females = new List<LivingAnimal>();
            foreach (WorldArea a in tl.today.areas)
                foreach (AnimalPosition pos in a.animalPositionTracker.animalPositions)
                {
                    LivingAnimal an = a.GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (an == null || an.eaten || an.definition == null || an.age < an.matureAtAge) continue;
                    if (an.definition.sexes == null || an.definition.sexes.Length == 0) continue;
                    int sp = tl.populationBasedAnimalClassifier.LookUp(an.definition.definitionIndex);
                    if (sp < 0) sp = an.definition.definitionIndex; // seeded species: shared defIndex
                    if (an.definition.currentSex > 0)
                    {
                        if (!males.TryGetValue(sp, out List<AnimalDefinition> list)) males[sp] = list = new List<AnimalDefinition>();
                        list.Add(an.definition);
                    }
                    else if (!an.fertilized) females.Add(an);
                }
            int n = 0;
            foreach (LivingAnimal f in females)
            {
                int sp = tl.populationBasedAnimalClassifier.LookUp(f.definition.definitionIndex);
                if (sp < 0) sp = f.definition.definitionIndex;
                if (!males.TryGetValue(sp, out List<AnimalDefinition> list) || list.Count == 0) continue;
                f.fertilized = true;
                f.fertilizedByAnimal = list[n % list.Count];
                f.hasSharedGenes = true;
                f.ageAtLastMating = f.age;
                n++;
            }
            return n;
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

                try { tl.PruneExtinctDefinitionIndices(); } catch { }
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
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, saver.savingBytes.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
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

        // Export one named live species from a save as a .species16 archive.
        private static int ExportSpeciesFromSave(string savePath, string speciesName, string outPath, OrganismObjectFileImporter importer)
        {
            var importer2 = BuildRealImporter2();
            var gs = new GameSaver { importer = importer, importer2 = importer2, currentSaveFileFormatVersion = 16 };
            gs.Decode(File.ReadAllBytes(savePath));
            TimeLine tl = WireTimeline(gs, importer, importer2);

            AnimalDefinition chosen = null;
            foreach (AnimalPosition pos in tl.today.areas[0].animalPositionTracker.animalPositions)
            {
                LivingAnimal la = tl.today.areas[0].GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (la == null || la.eaten || la.definition == null) continue;
                int spIdx = tl.populationBasedAnimalClassifier.LookUp(la.definition.definitionIndex);
                if (spIdx < 0 || spIdx >= tl.allSpecies.Count) continue;
                if (!string.Equals(tl.allSpecies[spIdx].name, speciesName, StringComparison.OrdinalIgnoreCase)) continue;
                chosen = la.definition;
                break;
            }
            if (chosen == null)
            {
                Console.Error.WriteLine($"Species '{speciesName}' has no living member in {savePath}");
                return 2;
            }

            var def = (AnimalDefinition)chosen.Clone();
            def.currentSex = 0;
            def.definitionIndex = -1;
            var sp = new SpeciesDefinition(def, speciesName, 0)
            {
                speciesType = OrganismType.Animal,
                manuallyAdded = true,
                birthPlaces = new List<int>(),
                numberOfYearsNotExtinct = 0,
                peakNumberOfLivingOrganisms = 0,
                extinctSinceYear = -1,
            };
            var tmp = new GameSaver { importer = importer, importer2 = importer2, savingBytes = new List<byte>() };
            tmp.AddSpecies(sp);
            byte[] bytes = tmp.savingBytes.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, bytes);

            var loader = new GameSaver { importer = importer, importer2 = importer2, currentSaveFileFormatVersion = 16, loadingBytes = bytes };
            var loaded = loader.GetSpecies();
            var ld = (AnimalDefinition)loaded.exampleOrganism;
            Console.WriteLine($"EXPORTED '{loaded.name}' -> {outPath} ({bytes.Length} bytes) sexes={ld.sexes.Length + 1} size={ld.bodySize} egg={ld.eggType}");
            return 0;
        }

        private static int ExportTopSexualFromSave(string savePath, string herbOut, string carnOut, OrganismObjectFileImporter importer)
        {
            var importer2 = BuildRealImporter2();
            var gs = new GameSaver { importer = importer, importer2 = importer2, currentSaveFileFormatVersion = 16 };
            gs.Decode(File.ReadAllBytes(savePath));
            TimeLine tl = WireTimeline(gs, importer, importer2);
            float grav = tl.gravity;

            // Group living animals by species index
            var bySpecies = new Dictionary<int, List<LivingAnimal>>();
            foreach (AnimalPosition pos in tl.today.areas[0].animalPositionTracker.animalPositions)
            {
                var la = tl.today.areas[0].GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (la == null || la.eaten || la.definition == null) continue;
                int spIdx = tl.usePopulationBasedClassification
                    ? tl.populationBasedAnimalClassifier.LookUp(la.definition.definitionIndex)
                    : (tl.speciesIndexPerDefinitionIndex.TryGetValue(la.definition.definitionIndex, out int s) ? s : -1);
                if (spIdx < 0) continue;
                if (!bySpecies.TryGetValue(spIdx, out var list)) { list = new List<LivingAnimal>(); bySpecies[spIdx] = list; }
                list.Add(la);
            }

            var ranked = new List<KeyValuePair<int, List<LivingAnimal>>>(bySpecies);
            ranked.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

            AnimalDefinition bestHerb = null; string bestHerbInfo = "";
            AnimalDefinition bestCarn = null; string bestCarnInfo = "";

            Console.WriteLine($"Day {tl.dayIndex} (grav={grav:F2}): {ranked.Count} living animal species");
            foreach (var kv in ranked)
            {
                int spIdx = kv.Key;
                var list = kv.Value;
                string spName = (spIdx >= 0 && spIdx < tl.allSpecies.Count && tl.allSpecies[spIdx] != null) ? tl.allSpecies[spIdx].name : $"spp{spIdx}";
                int nF = 0, nM = 0;
                foreach (var la in list) if (la.definition.currentSex == 0) nF++; else nM++;
                if (nF == 0 || nM == 0) continue;

                // Prefer a strictly non-asexual living individual in this species (highest speed, NoProblem on both sexes);
                // fall back to modal sexually active individual if none exist.
                AnimalDefinition chosen = null;
                float chosenSpd = -1f;
                bool chosenStrict = false;
                bool chosenHerbOrFung = false, chosenObligCarn = false;
                string chosenDiet = "";

                var seenDefs = new HashSet<int>();
                foreach (var la in list)
                {
                    var d = la.definition;
                    if (!seenDefs.Add(d.definitionIndex)) continue;
                    if (d.sexes == null || d.sexes.Length < 1) continue;
                    int sv = d.currentSex;
                    d.currentSex = 0;
                    var sc0 = new AnimalStatsCalculator(importer, grav); sc0.Calculate(d, false);
                    var yc0 = new AnimalStatsCalculator(importer, grav); yc0.Calculate(d, true);
                    var p0 = OrganismProblemDetection.DetectProblemInAnimal(d, yc0, sc0);
                    d.currentSex = 1;
                    var sc1 = new AnimalStatsCalculator(importer, grav); sc1.Calculate(d, false);
                    var yc1 = new AnimalStatsCalculator(importer, grav); yc1.Calculate(d, true);
                    var p1 = OrganismProblemDetection.DetectProblemInAnimal(d, yc1, sc1);
                    d.currentSex = sv;
                    if (p0 != OrganismProblem.NoProblem || p1 != OrganismProblem.NoProblem) continue;

                    bool fSex = d._canGiveBirthSexually && !d._canGiveBirthAsexually && !d._canFertilizeInternally;
                    bool mSex = d.sexes[0].canFertilizeInternally && !d.sexes[0].canGiveBirthSexually;
                    if (!fSex || !mSex) continue;
                    bool strict = !d.sexes[0].canGiveBirthAsexually;

                    bool eatsMeat = false, eatsPlantOrFung = false;
                    var dl = new List<string>();
                    if (sc0.orderedDetailedFoodTypes != null)
                        for (int i = 0; i < sc0.orderedDetailedFoodTypes.Length; i++)
                        {
                            var ft = sc0.orderedDetailedFoodTypes[i];
                            dl.Add($"{ft}:{sc0.energyPerDetailedFoodType[i]:F0}");
                            if (ft == DetailedFoodType.WholeAnimal || ft == DetailedFoodType.FreshMeat || ft == DetailedFoodType.OldMeat || ft == DetailedFoodType.Egg || ft == DetailedFoodType.Blood)
                                eatsMeat = true;
                            else if (ft != DetailedFoodType.None)
                                eatsPlantOrFung = true;
                        }
                    bool herbOrFung = !eatsMeat && eatsPlantOrFung;
                    bool obligCarn = eatsMeat && !eatsPlantOrFung;
                    if (!herbOrFung && !obligCarn) continue;

                    if (chosen == null || (strict && !chosenStrict) || (strict == chosenStrict && sc0.speed > chosenSpd))
                    {
                        chosen = d; chosenSpd = sc0.speed; chosenStrict = strict;
                        chosenHerbOrFung = herbOrFung; chosenObligCarn = obligCarn;
                        chosenDiet = string.Join(",", dl);
                    }
                }
                if (chosen == null) continue;

                if (bestHerb == null && chosenHerbOrFung)
                {
                    bestHerb = chosen;
                    bestHerbInfo = $"spp {spIdx} '{spName}' pop={list.Count} (F={nF}/M={nM}) strict={chosenStrict} sz={chosen.bodySize} spd={chosenSpd:F1} diet=[{chosenDiet}]";
                }
                if (bestCarn == null && chosenObligCarn)
                {
                    bestCarn = chosen;
                    bestCarnInfo = $"spp {spIdx} '{spName}' pop={list.Count} (F={nF}/M={nM}) strict={chosenStrict} sz={chosen.bodySize} spd={chosenSpd:F1} diet=[{chosenDiet}]";
                }
                if (bestHerb != null && bestCarn != null) break;
            }

            void WriteAndVerify(AnimalDefinition chosen, string name, string outPath, string info)
            {
                if (chosen == null) { Console.Error.WriteLine($"ERROR: No matching species found for {name}!"); return; }
                // Clone via GameSaver round-trip so we never mutate the live object
                var tmp = new GameSaver { importer = importer, importer2 = importer2, savingBytes = new List<byte>() };
                int prevSex = chosen.currentSex;
                int prevIdx = chosen.definitionIndex;
                chosen.currentSex = 0;
                chosen.definitionIndex = -1;
                var sp = new SpeciesDefinition(chosen, name, 0)
                {
                    speciesType = OrganismType.Animal,
                    manuallyAdded = true,
                    birthPlaces = new List<int>(),
                    numberOfYearsNotExtinct = 0,
                    peakNumberOfLivingOrganisms = 0,
                    extinctSinceYear = -1,
                };
                tmp.AddSpecies(sp);
                chosen.currentSex = prevSex;
                chosen.definitionIndex = prevIdx;

                byte[] bytes = tmp.savingBytes.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, bytes);

                var loader = new GameSaver { importer = importer, importer2 = importer2, currentSaveFileFormatVersion = 16, loadingBytes = bytes };
                var loaded = loader.GetSpecies();
                var ld = (AnimalDefinition)loaded.exampleOrganism;
                ld.currentSex = 0; bool fOk = ld.canGiveBirthSexually && !ld.canGiveBirthAsexually && !ld.canFertilizeInternally;
                ld.currentSex = 1; bool mOk = ld.canFertilizeInternally && !ld.canGiveBirthSexually && !ld.canGiveBirthAsexually;
                Console.WriteLine($"EXPORTED '{name}' -> {outPath} ({bytes.Length} bytes) from {info} | round-trip name='{loaded.name}' sexes={ld.sexes.Length + 1} F={fOk} M={mOk}");
            }

            WriteAndVerify(bestHerb, "Sex Herbivore", herbOut, bestHerbInfo);
            WriteAndVerify(bestCarn, "Sex Obligate Carnivore", carnOut, bestCarnInfo);
            return (bestHerb != null && bestCarn != null) ? 0 : 2;
        }

        private static int InspectFile(string path, OrganismObjectFileImporter importer)
        {
            var importer2 = BuildRealImporter2();
            void DumpDef(string label, AnimalDefinition def, float grav)
            {
                int prevSex = def.currentSex;
                int nSex = 1 + (def.sexes != null ? def.sexes.Length : 0);
                for (int s = 0; s < nSex; s++)
                {
                    def.currentSex = s;
                    var sc = new AnimalStatsCalculator(importer, grav);
                    sc.Calculate(def, false);
                    var yc = new AnimalStatsCalculator(importer, grav);
                    yc.Calculate(def, true);
                    var parts = new List<string>();
                    if (def.torso != null)
                        for (int t = 0; t < def.torso.Length; t++)
                            if (def.torso[t]?.children != null)
                                foreach (var kv in def.torso[t].children)
                                    parts.Add($"t{t}:{kv.Key.side}:{kv.Value.GetType().Name.Replace("Definition","")}({kv.Value.shapeName}#{kv.Value.shapeIndex},sc={kv.Value.scale:F2})");
                    var insts = new List<string>();
                    if (def.instincts != null)
                        foreach (var ins in def.instincts)
                            if (ins != null) insts.Add($"{ins.conditionSense}:{ins.conditionObject}->{ins.action}");
                    var diet = new List<string>();
                    if (sc.orderedDetailedFoodTypes != null)
                        for (int i = 0; i < sc.orderedDetailedFoodTypes.Length; i++)
                            diet.Add($"{sc.orderedDetailedFoodTypes[i]}:{sc.energyPerDetailedFoodType[i]:F1}");
                    Console.WriteLine($"[{label} sex={s}] grav={grav:F2} size={def.bodySize} age={sc.expectedAge}d mat={sc.matureAtAge}d preg={sc.pregnancyTime}d eggT={sc.embryoDevelopmentTime - sc.pregnancyTime}d spd={sc.speed:F2}(y:{yc.speed:F2}) swim={sc.canSwim} aq={sc.aquaticBehavior} inW={sc.timeInWater} onL={sc.timeOnLand} cw={sc.canWalk} wt={sc.weight:F1}/{sc.weightSupportedForWalking:F1} see(D/T/N)={sc.furthestSeeingDistanceDuringDayTime*5:F1}/{sc.furthestSeeingDistanceAtTwilight*5:F1}/{sc.furthestSeeingDistanceAtNight*5:F1} fov={sc.fieldOfView} hear={sc.furthestHearingDistance:F1} temp=[{sc.minimumTemperatureThisOrganismCanHandle:F1}..{sc.maximumTemperatureThisOrganismCanHandle:F1}] noEgg=[{def.noEggsBelowTemperature:F2}..{def.noEggsAboveTemperature:F2}] noHatch=[{def.noHatchBelowTemperature:F2}..{def.noHatchAboveTemperature:F2}] reproCost={sc.energyNeededToReproduce:F1} asex={def.canGiveBirthAsexually} sexB={def.canGiveBirthSexually} fertInt={def.canFertilizeInternally} prob={OrganismProblemDetection.DetectProblemInAnimal(def, yc, sc)}");
                    Console.WriteLine($"  diet=[{string.Join(",", diet)}] sexDec={def.offspringSexDecisionType} ch=[{(sc.offSpringSexChances != null ? string.Join(",", sc.offSpringSexChances) : "")}]");
                    Console.WriteLine($"  inst({insts.Count})=[{string.Join("; ", insts)}]");
                    if (s == 0) Console.WriteLine($"  parts=[{string.Join(", ", parts)}]");
                }
                def.currentSex = prevSex;
            }
            if (path.EndsWith(".species16", StringComparison.OrdinalIgnoreCase))
            {
                var gs = new GameSaver { importer = importer, importer2 = importer2, currentSaveFileFormatVersion = 16, loadingBytes = File.ReadAllBytes(path) };
                var sp = gs.GetSpecies();
                DumpDef(sp.name, (AnimalDefinition)sp.exampleOrganism, 0.5f);
                return 0;
            }
            var g = new GameSaver { importer = importer, importer2 = importer2, currentSaveFileFormatVersion = 16 };
            g.Decode(File.ReadAllBytes(path));
            float grav2 = g.scenarioIndex == 999 ? g.planet.gravity : 1f;
            float tMin = 999f, tMax = -999f;
            foreach (var c in g.areas[0].cells) if (c != null && c.oceanDepth == -1f) { if (c.baseTemperature < tMin) tMin = c.baseTemperature; if (c.baseTemperature > tMax) tMax = c.baseTemperature; }
            Console.WriteLine($"SAVE day={g.dayIndex} scen={g.scenarioIndex} grav={grav2:F3} (denorm={SimulationTools.DenormalizeGravity(grav2):F3}) landBaseTemp=[{tMin:F2}..{tMax:F2}]");
            var seen = new HashSet<int>();
            foreach (AnimalPosition pos in g.areas[0].animalPositionTracker.animalPositions)
            {
                var la = g.areas[0].GetAnimalInCell(pos.cellIndex, pos.locationType);
                if (la == null || !seen.Add(la.definition.definitionIndex)) continue;
                var wc = g.areas[0].cells[pos.cellIndex];
                Console.WriteLine($"  LIVE def={la.definition.definitionIndex} sex={la.definition.currentSex} age={la.age} child={la.isChild} cellTemp={wc.temperature:F2}(base={wc.baseTemperature:F2}) E={la.energy:F1} spd={la.speed:F2} cw={la.canWalk}");
                DumpDef($"def{la.definition.definitionIndex}", la.definition, grav2);
            }
            return 0;
        }

        private static int RunSaveSimulation(string savePath, int days, int reportEvery, int seed, string patchMode)
        {
            string outDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "sim-results");
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + seed;
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

            if (patchMode == "bridge")
            {
                SaplingRustBridge.Patches.TimeJumpPatches.ResetPingPongBuffers();
                ApplyBridgePatches(true);
            }
            else if (patchMode == "band") ApplyBandPatchesOnly();
            else if (patchMode == "fastsim") ApplyBridgePatches(true, new Type[] { typeof(SaplingRustBridge.Patches.FastSimPatches) });

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

            if (Environment.GetEnvironmentVariable("SIM_FIX_MOUTHS") == "1")
            {
                int fixedDefs = FixBackwardsMouths(tl, gs);
                Log($"[COEVO] Fixed backwards torso[0] Front->Back mouths on {fixedDefs} animal definitions.");
            }
            if (Environment.GetEnvironmentVariable("SIM_LOCK_SEXUAL") == "1" || Environment.GetEnvironmentVariable("SIM_LOCK_CARNIVORE") == "1")
            {
                ApplyCoEvoPatches(
                    Environment.GetEnvironmentVariable("SIM_LOCK_SEXUAL") == "1",
                    Environment.GetEnvironmentVariable("SIM_LOCK_CARNIVORE") == "1");
                Log($"[COEVO] Mutation locks active: sexual={Environment.GetEnvironmentVariable("SIM_LOCK_SEXUAL") == "1"}, carnivore={Environment.GetEnvironmentVariable("SIM_LOCK_CARNIVORE") == "1"}");
            }
            string solaraSizeEnv = Environment.GetEnvironmentVariable("SIM_SOLARA_SIZE");
            if (!string.IsNullOrEmpty(solaraSizeEnv) && int.TryParse(solaraSizeEnv, out int solSize) && solSize > 0)
            {
                int resized = ResizeLivingAnimals(tl, gs, solSize);
                Log($"[COEVO] Resized {resized} existing herbivore definitions to bodySize={solSize}.");
            }
            string seedArch = Environment.GetEnvironmentVariable("SIM_SEED_ARCHIVED");
            if (!string.IsNullOrEmpty(seedArch) && File.Exists(seedArch))
            {
                bool coevoPair = Environment.GetEnvironmentVariable("SIM_COEVO_PAIR") == "1";
                int cCount = int.TryParse(Environment.GetEnvironmentVariable("SIM_CARNIVORE_COUNT"), out int cc) && cc > 0 ? cc : 80;
                int hCount = int.TryParse(Environment.GetEnvironmentVariable("SIM_HERBIVORE_COUNT"), out int hc) && hc > 0 ? hc : 400;
                int cSize = int.TryParse(Environment.GetEnvironmentVariable("SIM_CARNIVORE_SIZE"), out int cs) ? cs : 10;
                int hSize = int.TryParse(Environment.GetEnvironmentVariable("SIM_HERBIVORE_SIZE"), out int hs) ? hs : 9;
                int cMouth = int.TryParse(Environment.GetEnvironmentVariable("SIM_CARNIVORE_MOUTH"), out int cm) ? cm : -1;
                int hMouth = int.TryParse(Environment.GetEnvironmentVariable("SIM_HERBIVORE_MOUTH"), out int hm) ? hm : -1;
                if (coevoPair)
                {
                    ClearAllLivingAnimals(tl);
                    Log("[COEVO] Cleared existing animals; seeding sexual Lentocirus-bodyplan Herbivore ('Solara') + Obligate Carnivore ('Lentocirus') pair.");
                    SeedFromArchivedBodyplan(tl, importer, importer2, seedArch, "Solara", false, hCount, hSize, hMouth, Log);
                    int headStart = int.TryParse(Environment.GetEnvironmentVariable("SIM_PREY_HEADSTART"), out int hs2) ? hs2 : 0;
                    if (headStart > 0)
                    {
                        Log($"[COEVO] Running {headStart}-day herbivore establishment phase before introducing obligate carnivore...");
                        for (int hd = 1; hd <= headStart; hd++)
                        {
                            tl.LiveADay();
                            if (hd % 50 == 0)
                            {
                                int cnt = 0, fert = 0, preg = 0, rdy = 0, kids = 0, bNest = 0, bEgg = 0, bSpc = 0; double sumE = 0, sumThr = 0;
                                foreach (AnimalPosition pos in tl.today.areas[0].animalPositionTracker.animalPositions)
                                {
                                    var la = tl.today.areas[0].GetAnimalInCell(pos.cellIndex, pos.locationType);
                                    if (la == null || la.eaten) continue;
                                    cnt++; sumE += la.energy; sumThr += la.CalculateReproductionThreshold();
                                    if (la.fertilized) fert++; if (la.pregnant) preg++; if (la.isChild) kids++;
                                    if (la.readyToGiveBirth)
                                    {
                                        rdy++;
                                        WorldCell wc = tl.today.areas[0].cells[pos.cellIndex];
                                        if (!tl.CellContainsCorrectNestForAnimal(wc, la)) bNest++;
                                        if (!tl.EggCanSurviveInCell(wc, la)) bEgg++;
                                        if (!tl.EnoughSpaceForReproduction(la, tl.today.areas[0], 0, pos.cellIndex, pos.locationType))
                                            bSpc++;
                                    }
                                }
                                int hp, hf, han, hpl, hma, hgr, hds, heg;
                                CountWorld(tl, out hp, out hf, out han, out hpl, out hma, out hgr, out hds, out heg);
                                Log($"  [HS +{hd}] A={cnt} (fert={fert}, preg={preg}, rdy={rdy}[badNest={bNest},badEgg={bEgg},badSpc={bSpc}], kids={kids}, avgE={(cnt>0?sumE/cnt:0):F1}/{(cnt>0?sumThr/cnt:0):F1}, eggs={heg}, births={_totalBirths}, mat={_totalMatings})");
                            }
                        }
                        LogAnimalGuildBreakdown(tl, Log);
                    }
                }
                SeedFromArchivedBodyplan(tl, importer, importer2, seedArch, null, true, cCount, cSize, cMouth, Log);
            }

            if (Environment.GetEnvironmentVariable("SIM_SEED_AQUATIC") == "1")
            {
                int aqCount = int.TryParse(Environment.GetEnvironmentVariable("SIM_AQUATIC_COUNT"), out int aqc) && aqc > 0 ? aqc : 40;
                int aqSize = int.TryParse(Environment.GetEnvironmentVariable("SIM_AQUATIC_SIZE"), out int aqs) && aqs > 0 ? aqs : 12;
                SeedAquatic(tl, importer, BuildAquaticDefinition(importer, aqSize), "Aquara", aqCount, Log);
            }

            if (Environment.GetEnvironmentVariable("SIM_NO_ANIMAL_MUT") == "1")
            {
                tl.chanceForAnimalMutation = 0f;
                Console.WriteLine("[SIM] animal mutation disabled (avoids the new-definition crash)");
            }
            if (Environment.GetEnvironmentVariable("SIM_NO_ANIMAL_BIRTH") == "1")
            {
                tl.animalOrganismCap = 0;
                Console.WriteLine("[SIM] animal births disabled by cap");
            }

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
                        _lsN++; _lsAge += anml.age; _lsHealth += anml.health; _lsMaxHealth += anml.maximumHealth;
                        _lsHLoss += anml.dailyHealthLoss; _lsLife += anml.age + anml.health / Math.Max(1e-6f, anml.dailyHealthLoss);
                        _lsEnergy += anml.energy; _lsExp += anml.expectedAge; _lsDet += anml.healthDeterioratesAtAge; _lsMat += anml.matureAtAge; if (anml.age >= anml.healthDeterioratesAtAge - 3) _lsNearDeath++; if (!anml.isChild) _lsAdult++; _lsSize += anml.definition.bodySize; _lsELoss += anml.dailyEnergyLoss;
                        if (!anml.insideTemperatureRange) _lsOutTemp++;
                        if (anml.energy <= 1f) _lsStarving++; { int nb = tl.FindNumberOfAnimalsAroundCell(area, pos.cellIndex, pos.locationType); _lsNb += nb; if (nb == 0) _lsAlone++; int cx = pos.cellIndex % (int)area.size.x, cy = pos.cellIndex / (int)area.size.x; if (cx < _lsMinX) _lsMinX = cx; if (cx > _lsMaxX) _lsMaxX = cx; if (cy < _lsMinY) _lsMinY = cy; if (cy > _lsMaxY) _lsMaxY = cy; }
                        _lsN++; _lsAge += anml.age; _lsHealth += anml.health; _lsMaxHealth += anml.maximumHealth;
                        _lsHLoss += anml.dailyHealthLoss; _lsLife += anml.age + anml.health / Math.Max(1e-6f, anml.dailyHealthLoss);
                        _lsEnergy += anml.energy; _lsELoss += anml.dailyEnergyLoss;
                        if (!anml.insideTemperatureRange) _lsOutTemp++;
                        if (anml.energy <= 1f) _lsStarving++;
                    }
                }
                if (_lsN > 0)
                    Log($"  [LIFESPAN] n={_lsN} avgAge={_lsAge / _lsN:F1} avgHealth={_lsHealth / _lsN:F1}/{_lsMaxHealth / _lsN:F1} dailyHealthLoss={_lsHLoss / _lsN:F3} projectedLifespan={_lsLife / _lsN:F1}d energy={_lsEnergy / _lsN:F1} dailyEnergyLoss={_lsELoss / _lsN:F2} expectedAge={_lsExp / _lsN:F1} deteriorateAt={_lsDet / _lsN:F1} matureAt={_lsMat / _lsN:F1} bodySize={_lsSize / _lsN:F2} adults={_lsAdult} within3dOfDeath={_lsNearDeath} avgNeighbours={_lsNb / _lsN:F2} aloneNoNeighbour={_lsAlone} bbox=x{_lsMinX}-{_lsMaxX},y{_lsMinY}-{_lsMaxY} outsideTemp={_lsOutTemp} starving={_lsStarving}");
                if (_lsN > 0)
                    Log($"  [LIFESPAN] n={_lsN} avgAge={_lsAge / _lsN:F1} avgHealth={_lsHealth / _lsN:F1}/{_lsMaxHealth / _lsN:F1} dailyHealthLoss={_lsHLoss / _lsN:F3} projectedLifespan={_lsLife / _lsN:F1}d energy={_lsEnergy / _lsN:F1} dailyEnergyLoss={_lsELoss / _lsN:F2} outsideTemp={_lsOutTemp} starving={_lsStarving}");
                foreach (var kv in animalCounts)
                {
                    int total = kv.Value[0] + kv.Value[1];
                    SpeciesDefinition sp = (kv.Key >= 0 && kv.Key < gs.allSpecies.Count) ? gs.allSpecies[kv.Key] : null;
                    Log($"  Animal spp {kv.Key} '{(sp != null ? sp.name : "?")}': {total} (A/F={kv.Value[0]}, B/M={kv.Value[1]}), avgAge={(total > 0 ? (double)ageSums[kv.Key] / total : 0):F0}, matureAt={(total > 0 ? GetFirstAnimalMatureAge(tl) : 0)}, pregnant={preg[kv.Key]}, fertilized={fert[kv.Key]}, children={children[kv.Key]}");
                    if (Environment.GetEnvironmentVariable("SIM_AUDIT_ANIMALS") == "1" && tl.populationBasedAnimalClassifier != null)
                    {
                        var clf = tl.populationBasedAnimalClassifier;
                        int mainDef = clf.GetMainDefinitionForSpecies(kv.Key);
                        var liveDefs = new Dictionary<int, int>();
                        foreach (WorldArea a in tl.today.areas)
                            foreach (AnimalPosition pos in a.animalPositionTracker.animalPositions)
                            {
                                LivingAnimal la = a.GetAnimalInCell(pos.cellIndex, pos.locationType);
                                if (la == null || la.eaten || clf.LookUp(la.definition.definitionIndex) != kv.Key) continue;
                                int di = la.definition.definitionIndex;
                                liveDefs[di] = liveDefs.ContainsKey(di) ? liveDefs[di] + 1 : 1;
                            }
                        int maxNonMain = 0, maxNonMainDef = -1, totalTracked = 0;
                        foreach (var ck in clf.nrOfClassificationsPerDefinition)
                        {
                            if (clf.LookUp(ck.Key) != kv.Key) continue;
                            totalTracked++;
                            if (ck.Key != mainDef && ck.Value > maxNonMain) { maxNonMain = ck.Value; maxNonMainDef = ck.Key; }
                        }
                        int mainCount = clf.nrOfClassificationsPerDefinition.ContainsKey(mainDef) ? clf.nrOfClassificationsPerDefinition[mainDef] : 0;
                        Log($"    classifier: mainDef={mainDef}(births={mainCount}), distinctLiveDefs={liveDefs.Count}/{total}, trackedDefs={totalTracked}, maxNonMainDef={maxNonMainDef}(births={maxNonMain})");
                    }
                }
            }

            if (Environment.GetEnvironmentVariable("SIM_FEMALE_ASEXUAL") == "1")
            {
                foreach (WorldArea a in tl.today.areas)
                    foreach (AnimalPosition pos in a.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal la3 = a.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (la3 == null || la3.definition == null) continue;
                        if (la3.definition.currentSex == 0)
                        {
                            la3.definition.canGiveBirthAsexually = true;
                            la3.definition.canGiveBirthSexually = true;
                        }
                        else
                        {
                            la3.definition.canGiveBirthAsexually = false;
                            la3.definition.canGiveBirthSexually = false;
                        }
                    }
                Console.WriteLine("[SIM] female asexual cloning enabled; male gestation disabled");
            }

            if (Environment.GetEnvironmentVariable("SIM_FIX_SEX") == "1")
            {
                int nsx = 0, nsm = 0;
                foreach (WorldArea a in tl.today.areas)
                    foreach (AnimalPosition pos in a.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal lx = a.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (lx == null || lx.eaten || lx.definition == null) continue;
                        if (IsObligateCarnivoreDef(lx.definition)) continue;
                        lx.offSpringSexChances = new float[] { 0.5f, 0.5f };
                        FixSexBrackets(lx.definition);
                        lx.definition.offspringSexDecisionType = OffspringSexDecisionType.Chance;
                        if (lx.definition.currentSex > 0)
                        {
                            lx.definition.canGiveBirthAsexually = true;
                            lx.definition.canGiveBirthSexually = false;
                            lx.definition.canFertilizeInternally = true;
                            nsm++;
                        }
                        else
                        {
                            lx.definition._canGiveBirthAsexually = true;
                            lx.definition._canGiveBirthSexually = true;
                            lx.definition._canFertilizeInternally = false;
                        }
                        nsx++;
                    }
                Console.WriteLine($"[SIM] sex fixed on {nsx} animals ({nsm} male)");
            }

            bool assignMates = Environment.GetEnvironmentVariable("SIM_MATE_ASSIGN") == "1";
            bool balanceSex = Environment.GetEnvironmentVariable("SIM_BALANCE_SEX") == "1";
            if (balanceSex) Console.WriteLine($"[SIM] sexes rebalanced at load: {BalanceSexes(tl)}");
            if (assignMates) Console.WriteLine($"[SIM] mates assigned at load: {AssignMates(tl)}");

            if (Environment.GetEnvironmentVariable("SIM_AUDIT_SEX") == "1")
            {
                LogSexDiag(tl, Console.WriteLine);
            }

            if (days == 0) { liveLog.Close(); return 0; }
            bool runForever = days < 0;
            if (reportEvery <= 0) reportEvery = 1000;
            string stopPath = Path.Combine(outDir, "STOP");
            if (File.Exists(stopPath)) File.Delete(stopPath);
            int stopFlag = 0;
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                System.Threading.Interlocked.Exchange(ref stopFlag, 1);
            };
            var rollingCheckpoints = new Queue<string>();
            int ckInterval = runForever ? 25000 : 10000;

            string csvPath = Path.Combine(outDir, "save-sim-" + stamp + ".csv");
            string speciesPath = Path.Combine(outDir, "save-sim-species-" + stamp + ".csv");
            var csv = new StreamWriter(csvPath);
            csv.WriteLine("day,plants,fungi,animals,plankton,macroAlgae,grassCells,dormantSeeds,eggs,plantSpp,fungusSpp,animalSpp,algaSpp,totalSpp,matings,births,mutations,daysPerSec,memMB");
            var speciesCsv = new StreamWriter(speciesPath);
            speciesCsv.WriteLine("day,kingdom,speciesIndex,name,population");
            Log($"Results CSV: {csvPath}");
            Log($"Live log:    {logPath}");
            if (runForever) Log($"Forever mode active: create '{stopPath}' or press Ctrl+C to save final world and stop cleanly.");

            CountSpeciesAlive(tl, out ps, out fs, out ans, out als);
            csv.WriteLine($"{tl.dayIndex},{p},{f},{an},{pl},{ma},{gr},{ds},{eg},{ps},{fs},{ans},{als},{tl.allSpecies.Count},{_totalMatings},{_totalBirths},{_totalMutations},0,{GC.GetTotalMemory(false) / 1048576.0:F0}");
            csv.Flush();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool trackMove = Environment.GetEnvironmentVariable("SIM_TRACK_MOVE") != null;
            int startDay = tl.dayIndex;
            int done = 0;
            while (runForever || done < days)
            {
                if (trackMove) TrackMoves(tl, _moveTrack, true, done);
                tl.LiveADay();
                if (trackMove) TrackMoves(tl, _moveTrack, false, done + 1);
                done++;
                if (assignMates && done % 10 == 0) AssignMates(tl);
                if (balanceSex && done % 10 == 0) BalanceSexes(tl);
                if (done <= 1000 && done % 50 == 0 && Environment.GetEnvironmentVariable("SIM_COEVO_VERBOSE") == "1")
                {
                    int cCnt = 0, cF = 0, cM = 0, cFert = 0, cKids = 0, cEggs = 0, cCapped = 0; double cSumE = 0;
                    int hCnt = 0;
                    foreach (AnimalPosition pos in tl.today.areas[0].animalPositionTracker.animalPositions)
                    {
                        var la = tl.today.areas[0].GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (la == null || la.eaten) continue;
                        if (!IsObligateCarnivoreDef(la.definition)) { hCnt++; continue; }
                        cCnt++; if (la.definition.currentSex == 0) cF++; else cM++;
                        cSumE += la.energy;
                        if (la.nrOfChildren >= la.maximumNrOfChildren) cCapped++;
                        if (la.fertilized) cFert++; if (la.isChild) cKids++;
                    }
                    foreach (WorldCell wc in tl.today.areas[0].cells)
                        if (wc != null && wc.eggs != null)
                            foreach (var eg2 in wc.eggs)
                                if (eg2 != null && IsObligateCarnivoreDef(eg2.definition)) cEggs++;
                    Log($"  [COEVO +{done}] H={hCnt} | C={cCnt} (F={cF}/M={cM}, fert={cFert}, kids={cKids}, eggs={cEggs}, capped={cCapped}, avgE={(cCnt>0?cSumE/cCnt:0):F1}) mat={_totalMatings} births={_totalBirths}");
                }
                if (done % 100 == 0 && (System.Threading.Volatile.Read(ref stopFlag) != 0 || File.Exists(stopPath)))
                {
                    System.Threading.Interlocked.Exchange(ref stopFlag, 1);
                    Log($"[STOP] Graceful stop signal received at day {tl.dayIndex}.");
                }
                if (done % 1000 == 0)
                {
                    int defCount = tl.speciesIndexPerDefinitionIndex.Count
                        + tl.populationBasedAlgaClassifier.speciesPerDefinition.Count
                        + tl.populationBasedPlantClassifier.speciesPerDefinition.Count
                        + tl.populationBasedAnimalClassifier.speciesPerDefinition.Count
                        + tl.populationBasedFungusClassifier.speciesPerDefinition.Count;
                    if (defCount > 150000) Log(PruneDefinitionMaps(tl));
                }
                bool stopRequested = System.Threading.Volatile.Read(ref stopFlag) != 0;
                bool last = stopRequested || (!runForever && done == days);
                if (last || done % reportEvery == 0)
                {
                    CountWorld(tl, out p, out f, out an, out pl, out ma, out gr, out ds, out eg);
                    CountAnimalSexes(tl, out females, out males);
                    CountSpeciesAlive(tl, out ps, out fs, out ans, out als);
                    double elapsed = sw.Elapsed.TotalSeconds;
                    double dps = elapsed > 0 ? done / elapsed : 0;
                    string etaStr = runForever ? "INF" : (dps > 0 ? $"{(days - done) / dps / 3600.0:F1}h" : "?");
                    long mem = GC.GetTotalMemory(false) / 1048576;
                    Log($"Day {tl.dayIndex,9} | P={p,5} F={f,4} A={an,5} (F/M={females}/{males}) | grass={gr,5} plank={pl,5} alga={ma,4} seeds={ds,4} eggs={eg,4} | spp alive P={ps} F={fs} A={ans} | totalSpp={tl.allSpecies.Count} capHits={_speciesCapHits} | mat={_totalMatings} births={_totalBirths} mut={_totalMutations} | {dps:F1} d/s ETA {etaStr} mem={mem}MB");
                    if (Environment.GetEnvironmentVariable("SIM_AUDIT_SEX") == "1") LogSexDiag(tl, Log);
                    if (_allDeaths.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var kv in _allDeaths) parts.Add($"{kv.Key}={kv.Value}");
                        Log("    deaths: " + string.Join(", ", parts));
                    }
                    if (_carnDeaths.Count > 0)
                    {
                        var cp = new List<string>();
                        foreach (var kv in _carnDeaths) cp.Add($"{kv.Key}={kv.Value}");
                        Log("    carn-deaths: " + string.Join(", ", cp));
                    }
                    if (_aquaDeaths.Count > 0)
                    {
                        var ap = new List<string>();
                        foreach (var kv in _aquaDeaths) ap.Add($"{kv.Key}={kv.Value}");
                        Log("    aqua-deaths: " + string.Join(", ", ap));
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
                    if (Environment.GetEnvironmentVariable("SIM_COEVO_PAIR") == "1")
                        LogAnimalGuildBreakdown(tl, Log);
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
                if (stopRequested) break;
                if (done % ckInterval == 0)
                {
                    string ckPath = Path.Combine(outDir, "world-day-" + tl.dayIndex + ".sav16");
                    var ckSw = System.Diagnostics.Stopwatch.StartNew();
                    if (SaveCheckpoint(tl, gs.planet, importer, importer2, ckPath))
                    {
                        Log($"[CHECKPOINT] {ckPath} ({new FileInfo(ckPath).Length / 1048576.0:F1} MB, {ckSw.Elapsed.TotalSeconds:F0}s)");
                        File.WriteAllText(Path.Combine(outDir, "latest-checkpoint.txt"), ckPath + Environment.NewLine);
                        if (runForever)
                        {
                            rollingCheckpoints.Enqueue(ckPath);
                            while (rollingCheckpoints.Count > 3)
                            {
                                string oldCk = rollingCheckpoints.Dequeue();
                                try { if (File.Exists(oldCk)) File.Delete(oldCk); } catch { }
                            }
                        }
                    }
                }
            }
            sw.Stop();
            csv.Close();
            speciesCsv.Close();
            Log($"Done: {startDay} -> {tl.dayIndex} ({done} days) in {sw.Elapsed.TotalHours:F2}h, {done / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} days/s");
            string finalPath = Path.Combine(outDir, "world-day-" + tl.dayIndex + "-final.sav16");
            var finalSw = System.Diagnostics.Stopwatch.StartNew();
            if (SaveCheckpoint(tl, gs.planet, importer, importer2, finalPath))
            {
                Log($"[CHECKPOINT] final world: {finalPath} ({new FileInfo(finalPath).Length / 1048576.0:F1} MB, {finalSw.Elapsed.TotalSeconds:F0}s)");
                File.WriteAllText(Path.Combine(outDir, "latest-checkpoint.txt"), finalPath + Environment.NewLine);
            }
            if (File.Exists(stopPath)) try { File.Delete(stopPath); } catch { }
            Log($"CSV: {csvPath}");
            Log($"Species census: {speciesPath}");
            LogAnimalGuildBreakdown(tl, Log);
            liveLog.Close();
            return 0;
        }

        private static bool _lockSexual;
        private static bool _lockCarnivore;

        private static void ApplyCoEvoPatches(bool lockSexual, bool lockCarnivore)
        {
            _lockSexual = lockSexual;
            _lockCarnivore = lockCarnivore;
            var h = new Harmony("com.saplingrust.coevo");
            if (lockSexual)
            {
                var rm = AccessTools.Method(typeof(AnimalMutator), "RemoveSex", new[] { typeof(AnimalDefinition) });
                if (rm != null) h.Patch(rm, prefix: new HarmonyMethod(typeof(Program), nameof(CoEvo_RemoveSex_Prefix)));
                var cm = AccessTools.Method(typeof(AnimalMutator), "ChangeMatingTraits", new[] { typeof(AnimalDefinition) });
                if (cm != null) h.Patch(cm,
                    prefix: new HarmonyMethod(typeof(Program), nameof(CoEvo_ChangeMatingTraits_Prefix)),
                    postfix: new HarmonyMethod(typeof(Program), nameof(CoEvo_ChangeMatingTraits_Postfix)));
                var esr = AccessTools.Method(typeof(TimeLine), "EnoughSpaceForReproduction",
                    new[] { typeof(LivingAnimal), typeof(WorldArea), typeof(int), typeof(int), typeof(CellLocationType) });
                if (esr != null) h.Patch(esr, postfix: new HarmonyMethod(typeof(Program), nameof(CoEvo_EnoughSpace_Postfix)));
                var us = AccessTools.Method(typeof(LivingAnimal), "UpdateStats",
                    new[] { typeof(OrganismObjectFileImporter), typeof(float) });
                if (us != null) h.Patch(us, postfix: new HarmonyMethod(typeof(Program), nameof(CoEvo_SexualCarnivoreCap_Postfix)));
                var bgb = AccessTools.Method(typeof(TimeLine), "BabyGetsBorn",
                    new[] { typeof(LivingAnimal), typeof(WorldArea), typeof(int) });
                if (bgb != null) h.Patch(bgb, prefix: new HarmonyMethod(typeof(Program), nameof(CoEvo_SexualCarnivoreBaby_Prefix)));
                var le = AccessTools.Method(typeof(TimeLine), "LayEggInCell",
                    new[] { typeof(LivingAnimal), typeof(int), typeof(int), typeof(bool) });
                if (le != null) h.Patch(le, prefix: new HarmonyMethod(typeof(Program), nameof(CoEvo_SexualCarnivoreBaby_Prefix)));
            }
            if (lockCarnivore)
            {
                var cd = AccessTools.Method(typeof(AnimalMutator), "ChangeDiet", new[] { typeof(AnimalDefinition) });
                if (cd != null) h.Patch(cd,
                    prefix: new HarmonyMethod(typeof(Program), nameof(CoEvo_Carnivore_Prefix)),
                    postfix: new HarmonyMethod(typeof(Program), nameof(CoEvo_Carnivore_Postfix)));
                var rbp = AccessTools.Method(typeof(AnimalMutator), "ReplaceBodyPart", new[] { typeof(AnimalDefinition) });
                if (rbp != null) h.Patch(rbp,
                    prefix: new HarmonyMethod(typeof(Program), nameof(CoEvo_Carnivore_Prefix)),
                    postfix: new HarmonyMethod(typeof(Program), nameof(CoEvo_Carnivore_Postfix)));
            }
        }

        private static bool CoEvo_RemoveSex_Prefix(AnimalDefinition animal)
        {
            // Keep at least 2 sexes (base + sexes[0]) on strictly sexual species.
            return !(animal != null && animal.sexes != null && animal.sexes.Length <= 1);
        }

        private static void CoEvo_ChangeMatingTraits_Prefix(AnimalDefinition animal, out bool __state)
        {
            __state = animal != null && animal.sexes != null && animal.sexes.Length >= 1 && !animal._canGiveBirthAsexually;
        }

        private static void CoEvo_ChangeMatingTraits_Postfix(AnimalDefinition animal, bool __state)
        {
            if (!__state || animal == null) return;
            animal._canGiveBirthAsexually = false;
            animal._canGiveBirthSexually = true;
            animal._canFertilizeInternally = false;
            var y = animal.young;
            y.canGiveBirthAsexually = false;
            animal.young = y;
            if (animal.sexes != null)
            {
                for (int i = 0; i < animal.sexes.Length; i++)
                {
                    var m = animal.sexes[i];
                    m.canGiveBirthAsexually = false;
                    m.canGiveBirthSexually = false;
                    m.canFertilizeInternally = true;
                    animal.sexes[i] = m;
                }
            }
            if (animal.youngSexes != null)
            {
                for (int i = 0; i < animal.youngSexes.Length; i++)
                {
                    var m = animal.youngSexes[i];
                    m.canGiveBirthAsexually = false;
                    m.canGiveBirthSexually = false;
                    m.canFertilizeInternally = true;
                    animal.youngSexes[i] = m;
                }
            }
        }

        private static void CoEvo_EnoughSpace_Postfix(
            TimeLine __instance, LivingAnimal lifeForm, WorldArea area, int areaIndex,
            int indexOfLifeFormCell, CellLocationType locationTypeOfLifeFormCell, ref bool __result)
        {
            // Sexual animals require an adjacent mate (near >= 1), so vanilla's `near > 1` deadlocks
            // slow protolobe walkers the instant 1 egg or 2nd mate is nearby. Allow +1 neighbor slot for sexual mates.
            if (__result || lifeForm == null || lifeForm.definition == null) return;
            if (lifeForm.definition._canGiveBirthAsexually || !lifeForm.definition._canGiveBirthSexually) return;
            if (lifeForm.definition.nestType != NestType.None && __instance.nestBuilding && lifeForm.ableToBuildNest) return;
            int maxNear = lifeForm.eatsMeatOnly ? 5 : 3;
            int cell = indexOfLifeFormCell;
            if (lifeForm.timeInEgg < 5f)
            {
                // Pick any valid adjacent empty cell within maxSteepness
                int[] around = area.GetAllCellsDirectlyAround(indexOfLifeFormCell);
                cell = -1;
                for (int i = 0; i < around.Length; i++)
                {
                    int c = around[i];
                    if (c < 0 || c >= area.cells.Length) continue;
                    if (area.cells[c].CalculateNormalizedSteepness() > lifeForm.maximumSteepness) continue;
                    bool free = lifeForm.timeInEgg == 0f ? area.cells[c].animal == null : !area.cells[c].ContainsEggs();
                    if (free && __instance.FindNumberOfAnimalsAroundCell(area, c, locationTypeOfLifeFormCell) <= maxNear)
                    {
                        cell = c;
                        break;
                    }
                }
            }
            else
            {
                if (area.cells[cell].ContainsEggs() || __instance.FindNumberOfAnimalsAroundCell(area, cell, locationTypeOfLifeFormCell) > maxNear)
                    cell = -1;
            }
            if (cell != -1)
            {
                __instance.latestCellToReproduceIn = cell;
                __result = true;
            }
        }

        private static void CoEvo_SexualCarnivoreCap_Postfix(LivingAnimal __instance)
        {
            int cap = int.TryParse(Environment.GetEnvironmentVariable("SIM_CARN_MAX_KIDS"), out int c) ? c : 3;
            if (cap > 3 && __instance != null && __instance.eatsMeat && __instance.definition != null
                && !__instance.definition._canGiveBirthAsexually && __instance.definition._canGiveBirthSexually)
            {
                __instance.maximumNrOfChildren = cap;
            }
        }

        private static void CoEvo_SexualCarnivoreBaby_Prefix(LivingAnimal baby)
        {
            CoEvo_SexualCarnivoreCap_Postfix(baby);
        }

        private static readonly DetailedFoodType[] NonCarnivoreFoods = new[]
        {
            DetailedFoodType.Plankton, DetailedFoodType.SoftPlantTissue, DetailedFoodType.Grass,
            DetailedFoodType.Fruit, DetailedFoodType.Seed, DetailedFoodType.Nut, DetailedFoodType.Fungus,
            DetailedFoodType.Bark, DetailedFoodType.Nectar, DetailedFoodType.Sap, DetailedFoodType.MarineSnow
        };

        private static float MouthFoodEnergy(MouthDefinition md, DetailedFoodType f)
        {
            return (md != null && md.energyPerFoodType != null && md.energyPerFoodType.TryGetValue(f, out float v)) ? v : 0f;
        }

        private static bool IsObligateCarnivoreDef(AnimalDefinition def)
        {
            if (def == null || def.torso == null) return false;
            bool hasMeatMouth = false;
            bool eatsNonMeat = false;
            for (int t = 0; t < def.torso.Length; t++)
            {
                if (def.torso[t] == null || def.torso[t].children == null) continue;
                foreach (var kv in def.torso[t].children)
                {
                    if (kv.Value is MouthDefinition md)
                    {
                        if (MouthFoodEnergy(md, DetailedFoodType.WholeAnimal) > 0f
                            || MouthFoodEnergy(md, DetailedFoodType.FreshMeat) > 0f
                            || MouthFoodEnergy(md, DetailedFoodType.OldMeat) > 0f)
                            hasMeatMouth = true;
                        foreach (var nf in NonCarnivoreFoods)
                        {
                            if (MouthFoodEnergy(md, nf) > 0f && (def._excludeFromDiet == null || !def._excludeFromDiet.Contains(nf)))
                                eatsNonMeat = true;
                        }
                    }
                }
            }
            return hasMeatMouth && !eatsNonMeat;
        }

        private static void EnforceObligateCarnivoreDiet(AnimalDefinition def)
        {
            if (def._excludeFromDiet == null) def._excludeFromDiet = new HashSet<DetailedFoodType>();
            foreach (var f in NonCarnivoreFoods) def._excludeFromDiet.Add(f);
            var y = def.young;
            if (y.excludeFromDiet == null) y.excludeFromDiet = new HashSet<DetailedFoodType>();
            foreach (var f in NonCarnivoreFoods) y.excludeFromDiet.Add(f);
            def.young = y;
            if (def.sexes != null)
            {
                for (int i = 0; i < def.sexes.Length; i++)
                {
                    var m = def.sexes[i];
                    if (m.excludeFromDiet == null) m.excludeFromDiet = new HashSet<DetailedFoodType>();
                    foreach (var f in NonCarnivoreFoods) m.excludeFromDiet.Add(f);
                    def.sexes[i] = m;
                }
            }
            if (def.youngSexes != null)
            {
                for (int i = 0; i < def.youngSexes.Length; i++)
                {
                    var m = def.youngSexes[i];
                    if (m.excludeFromDiet == null) m.excludeFromDiet = new HashSet<DetailedFoodType>();
                    foreach (var f in NonCarnivoreFoods) m.excludeFromDiet.Add(f);
                    def.youngSexes[i] = m;
                }
            }
        }

        private static void CoEvo_Carnivore_Prefix(AnimalDefinition animal, out MouthDefinition __state)
        {
            __state = null;
            if (animal == null || !IsObligateCarnivoreDef(animal)) return;
            if (animal.torso != null && animal.torso.Length > 0 && animal.torso[0] != null)
            {
                foreach (var kv in animal.torso[0].children)
                    if (kv.Value is MouthDefinition md) { __state = (MouthDefinition)md.Clone(); break; }
            }
        }

        private static void CoEvo_Carnivore_Postfix(AnimalDefinition animal, MouthDefinition __state)
        {
            if (__state == null || animal == null) return;
            EnforceObligateCarnivoreDiet(animal);
            if (!IsObligateCarnivoreDef(animal) && animal.torso != null && animal.torso.Length > 0 && animal.torso[0] != null)
            {
                animal.torso[0].SetChild((MouthDefinition)__state.Clone(), TorsoPartSide.Back);
            }
        }

        private static bool FixAnimalMouthOrientation(AnimalDefinition def)
        {
            if (def == null || def.upright || def.torso == null || def.torso.Length < 2 || def.torso[0] == null || def.torso[0].children == null)
                return false;
            var toMove = new List<KeyValuePair<TorsoPartDetailedLocation, BodyPartDefinition>>();
            foreach (var kv in def.torso[0].children)
            {
                if (kv.Key.side == TorsoPartSide.Front && kv.Value is MouthDefinition)
                    toMove.Add(kv);
            }
            if (toMove.Count == 0) return false;
            foreach (var kv in toMove)
            {
                def.torso[0].children.Remove(kv.Key);
                def.torso[0].children[new TorsoPartDetailedLocation(TorsoPartSide.Back, kv.Key.locationOffset)] = kv.Value;
            }
            def.torso[0].childrenSummary.Clear();
            return true;
        }

        private static int FixBackwardsMouths(TimeLine tl, GameSaver gs)
        {
            int count = 0;
            var seen = new HashSet<AnimalDefinition>();
            if (gs.allSpecies != null)
                foreach (var sp in gs.allSpecies)
                    if (sp != null && sp.speciesType == OrganismType.Animal && sp.exampleOrganism is AnimalDefinition ad && seen.Add(ad))
                        if (FixAnimalMouthOrientation(ad)) count++;
            if (tl.today != null && tl.today.areas != null)
                foreach (var area in tl.today.areas)
                    foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal la = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (la != null && la.definition != null && seen.Add(la.definition))
                            if (FixAnimalMouthOrientation(la.definition)) count++;
                    }
            return count;
        }

        private static int ResizeLivingAnimals(TimeLine tl, GameSaver gs, int targetSize)
        {
            int count = 0;
            var seen = new HashSet<AnimalDefinition>();
            Action<AnimalDefinition> apply = (ad) =>
            {
                if (ad == null || !seen.Add(ad)) return;
                ad._bodySize = targetSize;
                var y = ad.young; y.bodySize = targetSize; ad.young = y;
                if (ad.sexes != null) for (int i = 0; i < ad.sexes.Length; i++) { var m = ad.sexes[i]; m.bodySize = targetSize; ad.sexes[i] = m; }
                if (ad.youngSexes != null) for (int i = 0; i < ad.youngSexes.Length; i++) { var m = ad.youngSexes[i]; m.bodySize = targetSize; ad.youngSexes[i] = m; }
                count++;
            };
            if (gs.allSpecies != null)
                foreach (var sp in gs.allSpecies)
                    if (sp != null && sp.speciesType == OrganismType.Animal && sp.exampleOrganism is AnimalDefinition ad) apply(ad);
            if (tl.today != null && tl.today.areas != null)
                foreach (var area in tl.today.areas)
                    foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                    {
                        LivingAnimal la = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                        if (la != null && la.definition != null) apply(la.definition);
                    }
            return count;
        }

        private static readonly DetailedFoodType[] CarnivoreOnlyFoods = new[]
        {
            DetailedFoodType.WholeAnimal, DetailedFoodType.FreshMeat, DetailedFoodType.OldMeat,
            DetailedFoodType.Egg, DetailedFoodType.Blood
        };

        private static void ClearAllLivingAnimals(TimeLine tl)
        {
            foreach (WorldArea area in tl.today.areas)
            {
                var positions = new List<AnimalPosition>(area.animalPositionTracker.animalPositions);
                foreach (var pos in positions)
                {
                    area.RemoveAnimalFromCell(pos.cellIndex, pos.locationType);
                }
                for (int i = 0; i < area.cells.Length; i++)
                {
                    if (area.cells[i] != null && area.cells[i].eggs != null)
                        Array.Clear(area.cells[i].eggs, 0, area.cells[i].eggs.Length);
                }
            }
            for (int i = 0; i < tl.today.numberOfLivingOrganismsPerSpecies.Count && i < tl.allSpecies.Count; i++)
            {
                if (tl.allSpecies[i].speciesType == OrganismType.Animal)
                    tl.today.numberOfLivingOrganismsPerSpecies[i] = 0;
            }
        }

        private static void EnforcePureHerbivoreDiet(AnimalDefinition def)
        {
            if (def._excludeFromDiet == null) def._excludeFromDiet = new HashSet<DetailedFoodType>();
            foreach (var f in CarnivoreOnlyFoods) def._excludeFromDiet.Add(f);
            var y = def.young;
            if (y.excludeFromDiet == null) y.excludeFromDiet = new HashSet<DetailedFoodType>();
            foreach (var f in CarnivoreOnlyFoods) y.excludeFromDiet.Add(f);
            def.young = y;
            if (def.sexes != null)
                for (int i = 0; i < def.sexes.Length; i++)
                {
                    var m = def.sexes[i];
                    if (m.excludeFromDiet == null) m.excludeFromDiet = new HashSet<DetailedFoodType>();
                    foreach (var f in CarnivoreOnlyFoods) m.excludeFromDiet.Add(f);
                    def.sexes[i] = m;
                }
            if (def.youngSexes != null)
                for (int i = 0; i < def.youngSexes.Length; i++)
                {
                    var m = def.youngSexes[i];
                    if (m.excludeFromDiet == null) m.excludeFromDiet = new HashSet<DetailedFoodType>();
                    foreach (var f in CarnivoreOnlyFoods) m.excludeFromDiet.Add(f);
                    def.youngSexes[i] = m;
                }
        }

        // Adds egg/blood energy to every mouth so the seeded predator also eats eggs and blood,
        // not just fresh/old meat and whole animals. Everything plant/fungus stays excluded.
        private static void AddMeatExtras(AnimalDefinition def, float eggEnergy, float bloodEnergy)
        {
            if (def == null) return;
            if (def.torso != null)
                foreach (TorsoPartDefinition tp in def.torso)
                {
                    if (tp == null || tp.children == null) continue;
                    foreach (var kv in tp.children)
                    {
                        if (!(kv.Value is MouthDefinition md)) continue;
                        if (md.energyPerFoodType == null) md.energyPerFoodType = new Dictionary<DetailedFoodType, float>();
                        md.energyPerFoodType[DetailedFoodType.Egg] = eggEnergy;
                        md.energyPerFoodType[DetailedFoodType.Blood] = bloodEnergy;
                    }
                }
            if (def._excludeFromDiet != null)
            {
                def._excludeFromDiet.Remove(DetailedFoodType.Egg);
                def._excludeFromDiet.Remove(DetailedFoodType.Blood);
            }
        }

        private static void SeedFromArchivedBodyplan(
            TimeLine tl, OrganismObjectFileImporter importer, OrganismObjectFileImporter2 importer2,
            string species16Path, string overrideName, bool obligateCarnivore, int count, int targetSize, int overrideMouthId, Action<string> log)
        {
            var sgs = new GameSaver();
            sgs.importer = importer;
            sgs.importer2 = importer2;
            sgs.currentSaveFileFormatVersion = 16;
            sgs.loadingStream = new FileStream(species16Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            sgs.loadUsesStream = true;
            sgs.pointer = 0;
            SpeciesDefinition sp = sgs.GetSpecies();
            sgs.loadingStream.Close();

            string speciesName = string.IsNullOrEmpty(overrideName) ? sp.name : overrideName;
            AnimalDefinition def = (AnimalDefinition)sp.exampleOrganism;
            FixAnimalMouthOrientation(def);
            if (overrideMouthId >= 0 && def.torso != null && def.torso.Length > 0)
            {
                MouthDefinition nm = importer.CreateMouthWithShapeIndex(overrideMouthId);
                nm.scale = 1.0f;
                def.torso[0].SetChild(nm, TorsoPartSide.Back);
            }
            string carnLimbEnv = obligateCarnivore ? Environment.GetEnvironmentVariable("SIM_CARNIVORE_LIMB") : Environment.GetEnvironmentVariable("SIM_HERBIVORE_LIMB");
            if (!string.IsNullOrEmpty(carnLimbEnv) && int.TryParse(carnLimbEnv, out int limbId) && limbId >= 0 && def.torso != null)
            {
                for (int t = 0; t < def.torso.Length; t++)
                {
                    if (def.torso[t] == null || def.torso[t].children == null) continue;
                    var keys = new List<TorsoPartDetailedLocation>(def.torso[t].children.Keys);
                    foreach (var k in keys)
                    {
                        if (k.side == TorsoPartSide.Side && def.torso[t].children[k] is LimbDefinition oldLimb)
                        {
                            LimbDefinition nl = importer.CreateLimbWithShapeIndex(limbId);
                            nl.scale = oldLimb.scale;
                            def.torso[t].children[k] = nl;
                        }
                    }
                    def.torso[t].childrenSummary.Clear();
                }
            }
            if (obligateCarnivore && Environment.GetEnvironmentVariable("SIM_CARN_EXTRA_LEGS") == "1" && def.torso != null && def.torso.Length >= 4)
            {
                LimbDefinition src = null;
                for (int t = 0; t < def.torso.Length && src == null; t++)
                    if (def.torso[t]?.children != null)
                        foreach (var kv in def.torso[t].children)
                            if (kv.Value is LimbDefinition ld) { src = ld; break; }
                if (src != null)
                {
                    LimbDefinition extra = importer.CreateLimbWithShapeIndex(src.shapeIndex);
                    extra.scale = src.scale;
                    def.torso[3].SetChild(extra, TorsoPartSide.Side);
                    def.torso[3].childrenSummary.Clear();
                }
            }
            if (obligateCarnivore && Environment.GetEnvironmentVariable("SIM_CARN_NO_FIN") == "1" && def.torso != null)
            {
                for (int t = 0; t < def.torso.Length; t++)
                {
                    if (def.torso[t] == null || def.torso[t].children == null) continue;
                    var keys = new List<TorsoPartDetailedLocation>(def.torso[t].children.Keys);
                    foreach (var k in keys)
                        if (k.side != TorsoPartSide.Side && def.torso[t].children[k] is LimbDefinition)
                            def.torso[t].children.Remove(k);
                    def.torso[t].childrenSummary.Clear();
                }
            }
            string eyeEnv = obligateCarnivore ? Environment.GetEnvironmentVariable("SIM_CARNIVORE_EYE") : Environment.GetEnvironmentVariable("SIM_HERBIVORE_EYE");
            if (!string.IsNullOrEmpty(eyeEnv) && int.TryParse(eyeEnv, out int eyeId) && eyeId >= 0 && def.torso != null)
            {
                for (int t = 0; t < def.torso.Length; t++)
                {
                    if (def.torso[t] == null || def.torso[t].children == null) continue;
                    var keys = new List<TorsoPartDetailedLocation>(def.torso[t].children.Keys);
                    foreach (var k in keys)
                        if (def.torso[t].children[k] is EyeDefinition oldEye)
                        {
                            EyeDefinition ne = importer.CreateEyeWithShapeIndex(eyeId);
                            ne.scale = oldEye.scale;
                            def.torso[t].children[k] = ne;
                        }
                    def.torso[t].childrenSummary.Clear();
                }
            }
            string earEnv = obligateCarnivore ? Environment.GetEnvironmentVariable("SIM_CARNIVORE_EAR") : Environment.GetEnvironmentVariable("SIM_HERBIVORE_EAR");
            if (!string.IsNullOrEmpty(earEnv) && int.TryParse(earEnv, out int earId) && earId >= 0 && def.torso != null)
            {
                for (int t = 0; t < def.torso.Length; t++)
                {
                    if (def.torso[t] == null || def.torso[t].children == null) continue;
                    var keys = new List<TorsoPartDetailedLocation>(def.torso[t].children.Keys);
                    foreach (var k in keys)
                        if (def.torso[t].children[k] is EarDefinition oldEar)
                        {
                            EarDefinition ne = importer.CreateEarWithShapeIndex(earId);
                            ne.scale = oldEar.scale;
                            def.torso[t].children[k] = ne;
                        }
                    def.torso[t].childrenSummary.Clear();
                }
            }
            // Clear any saved larval/morph bodyPartDiffs from archived amphibious Lentocirus so juveniles & males
            // inherit the exact terrestrial legs, eyes, and mouth from def.torso instead of larval/aquatic overrides.
            if (def.young.bodyPartDiffs != null && def.young.bodyPartDiffs.Count > 0)
            {
                log($"[COEVO] Clearing {def.young.bodyPartDiffs.Count} young bodyPartDiffs on '{speciesName}'.");
                var ycl = def.young; ycl.bodyPartDiffs = new Dictionary<BodyPartDiffLocation, BodyPartDiff>(); def.young = ycl;
            }
            if (def.sexes != null)
                for (int i = 0; i < def.sexes.Length; i++)
                    if (def.sexes[i].bodyPartDiffs != null && def.sexes[i].bodyPartDiffs.Count > 0)
                    {
                        log($"[COEVO] Clearing {def.sexes[i].bodyPartDiffs.Count} sex[{i}] bodyPartDiffs on '{speciesName}'.");
                        var mcl = def.sexes[i]; mcl.bodyPartDiffs = new Dictionary<BodyPartDiffLocation, BodyPartDiff>(); def.sexes[i] = mcl;
                    }
            if (def.youngSexes != null)
                for (int i = 0; i < def.youngSexes.Length; i++)
                    if (def.youngSexes[i].bodyPartDiffs != null && def.youngSexes[i].bodyPartDiffs.Count > 0)
                    {
                        log($"[COEVO] Clearing {def.youngSexes[i].bodyPartDiffs.Count} youngSex[{i}] bodyPartDiffs on '{speciesName}'.");
                        var mcl = def.youngSexes[i]; mcl.bodyPartDiffs = new Dictionary<BodyPartDiffLocation, BodyPartDiff>(); def.youngSexes[i] = mcl;
                    }
            string skinEnv = Environment.GetEnvironmentVariable("SIM_SKIN");
            if (!string.IsNullOrEmpty(skinEnv) && Enum.TryParse(skinEnv, true, out SkinType parsedSkin))
            {
                def._skinType = parsedSkin;
                var ysk = def.young; ysk.skinType = parsedSkin; def.young = ysk;
                if (def.sexes != null) for (int i = 0; i < def.sexes.Length; i++) { var m = def.sexes[i]; m.skinType = parsedSkin; def.sexes[i] = m; }
                if (def.youngSexes != null) for (int i = 0; i < def.youngSexes.Length; i++) { var m = def.youngSexes[i]; m.skinType = parsedSkin; def.youngSexes[i] = m; }
            }
            if (targetSize > 0)
            {
                def._bodySize = targetSize;
                var ysz = def.young; ysz.bodySize = targetSize; def.young = ysz;
                if (def.sexes != null) for (int i = 0; i < def.sexes.Length; i++) { var m = def.sexes[i]; m.bodySize = targetSize; def.sexes[i] = m; }
                if (def.youngSexes != null) for (int i = 0; i < def.youngSexes.Length; i++) { var m = def.youngSexes[i]; m.bodySize = targetSize; def.youngSexes[i] = m; }
            }
            int embMode = int.TryParse(Environment.GetEnvironmentVariable("SIM_EMBRYO"), out int em) ? em : 0;
            def._embryoTimeInsideMother = embMode;
            def._eggType = EggType.Cylinder;
            def._nestType = NestType.None;
            def._nestEnclosed = false;
            // Enforce strictly sexual 50/50 F/M reproduction
            def.currentSex = 0;
            def._canGiveBirthAsexually = false;
            def._canGiveBirthSexually = true;
            def._canFertilizeInternally = false;
            def._canFertilizeExternally = false;
            // Distinct visual palettes for herbivore Solara vs carnivore Lentocirus + dimorphic males
            if (!obligateCarnivore)
            {
                def._colorHue = 0.13f; def._colorBrightness = 0.72f;
                def._secondaryColorHue = 0.08f; def._secondaryColorBrightness = 0.55f;
                def._skinPattern = SkinPattern.Plain;
            }
            else
            {
                def._colorHue = 0.02f; def._colorBrightness = 0.45f;
                def._secondaryColorHue = 0.08f; def._secondaryColorBrightness = 0.25f;
                def._skinPattern = SkinPattern.ThinStripes;
            }
            var y = def.young;
            y.embryoTimeInsideMother = embMode;
            y.eggType = EggType.Cylinder;
            y.nestType = NestType.None;
            y.nestEnclosed = false;
            y.canGiveBirthAsexually = false;
            y.canGiveBirthSexually = true;
            y.canFertilizeInternally = false;
            y.canFertilizeExternally = false;
            y.colorHue = def._colorHue; y.colorBrightness = def._colorBrightness;
            y.secondaryColorHue = def._secondaryColorHue; y.secondaryColorBrightness = def._secondaryColorBrightness;
            y.skinPattern = def._skinPattern;
            def.young = y;
            if (def.sexes == null || def.sexes.Length == 0)
            {
                AnimalMorph mm = AnimalMorph.CreateMorphBasedOnAnimalDefinition(def);
                def.sexes = new[] { mm };
                def.youngSexes = new[] { AnimalMorph.Clone(mm) };
            }
            for (int i = 0; i < def.sexes.Length; i++)
            {
                var m = def.sexes[i];
                m.embryoTimeInsideMother = embMode;
                m.eggType = EggType.Cylinder;
                m.nestType = NestType.None;
                m.nestEnclosed = false;
                m.canGiveBirthAsexually = false;
                m.canGiveBirthSexually = false;
                m.canFertilizeInternally = true;
                m.canFertilizeExternally = false;
                if (!obligateCarnivore)
                {
                    m.colorHue = 0.56f; m.colorBrightness = 0.68f;
                    m.secondaryColorHue = 0.12f; m.secondaryColorBrightness = 0.85f;
                    m.skinPattern = SkinPattern.SmallSpots;
                }
                else
                {
                    m.colorHue = 0.96f; m.colorBrightness = 0.32f;
                    m.secondaryColorHue = 0.07f; m.secondaryColorBrightness = 0.72f;
                    m.skinPattern = SkinPattern.ThickStripes;
                }
                def.sexes[i] = m;
            }
            for (int i = 0; i < def.youngSexes.Length; i++)
            {
                var m = def.youngSexes[i];
                m.embryoTimeInsideMother = embMode;
                m.eggType = EggType.Cylinder;
                m.nestType = NestType.None;
                m.nestEnclosed = false;
                m.canGiveBirthAsexually = false;
                m.canGiveBirthSexually = false;
                m.canFertilizeInternally = true;
                m.canFertilizeExternally = false;
                m.colorHue = def.sexes[0].colorHue; m.colorBrightness = def.sexes[0].colorBrightness;
                m.secondaryColorHue = def.sexes[0].secondaryColorHue; m.secondaryColorBrightness = def.sexes[0].secondaryColorBrightness;
                m.skinPattern = def.sexes[0].skinPattern;
                def.youngSexes[i] = m;
            }

            if (obligateCarnivore)
            {
                EnforceObligateCarnivoreDiet(def);
                if (Environment.GetEnvironmentVariable("SIM_CARN_EATS_EGGS") == "1")
                    AddMeatExtras(def, 6f, 5f);
                def._wholeAnimalDietHueRange = new Vector2(0f, 1f);
                def._wholeAnimalDietBrightnessRange = new Vector2(0f, 1f);
                def._wholeAnimalDietSecondaryHueRange = new Vector2(0f, 1f);
                def._wholeAnimalDietSecondaryBrightnessRange = new Vector2(0f, 1f);
                def._invertWholeAnimalDietColorRange = false;
                if (def._wholeAnimalDietExcludeSkinPatterns != null) def._wholeAnimalDietExcludeSkinPatterns.Clear();
                var yw = def.young;
                yw.wholeAnimalDietHueRange = new Vector2(0f, 1f);
                yw.wholeAnimalDietBrightnessRange = new Vector2(0f, 1f);
                yw.wholeAnimalDietSecondaryHueRange = new Vector2(0f, 1f);
                yw.wholeAnimalDietSecondaryBrightnessRange = new Vector2(0f, 1f);
                yw.invertWholeAnimalDietColorRange = false;
                if (yw.wholeAnimalDietExcludeSkinPatterns != null) yw.wholeAnimalDietExcludeSkinPatterns.Clear();
                def.young = yw;
                // Offspring sex ratio (out of 12 brackets; default 6 = 50/50 F/M, or e.g. 8 = 67% F / 33% M for vanilla maxKids=3 viability).
                int fBr = int.TryParse(Environment.GetEnvironmentVariable("SIM_CARN_FEMALE_BRACKETS"), out int fb) ? Math.Max(1, Math.Min(11, fb)) : 6;
                def._offspringSexDecisionType = OffspringSexDecisionType.Chance;
                def._offspringSexChanceBracket0  = 0 < fBr ? 0 : 1; def._offspringSexChanceBracket1  = 1 < fBr ? 0 : 1; def._offspringSexChanceBracket2  = 2 < fBr ? 0 : 1;
                def._offspringSexChanceBracket3  = 3 < fBr ? 0 : 1; def._offspringSexChanceBracket4  = 4 < fBr ? 0 : 1; def._offspringSexChanceBracket5  = 5 < fBr ? 0 : 1;
                def._offspringSexChanceBracket6  = 6 < fBr ? 0 : 1; def._offspringSexChanceBracket7  = 7 < fBr ? 0 : 1; def._offspringSexChanceBracket8  = 8 < fBr ? 0 : 1;
                def._offspringSexChanceBracket9  = 9 < fBr ? 0 : 1; def._offspringSexChanceBracket10 = 10 < fBr ? 0 : 1; def._offspringSexChanceBracket11 = 11 < fBr ? 0 : 1;
                for (int i = 0; i < def.sexes.Length; i++)
                {
                    var m = def.sexes[i];
                    m.wholeAnimalDietHueRange = new Vector2(0f, 1f);
                    m.wholeAnimalDietBrightnessRange = new Vector2(0f, 1f);
                    m.wholeAnimalDietSecondaryHueRange = new Vector2(0f, 1f);
                    m.wholeAnimalDietSecondaryBrightnessRange = new Vector2(0f, 1f);
                    m.invertWholeAnimalDietColorRange = false;
                    if (m.wholeAnimalDietExcludeSkinPatterns != null) m.wholeAnimalDietExcludeSkinPatterns.Clear();
                    m.offspringSexDecisionType = OffspringSexDecisionType.Chance;
                    m.offspringSexChanceBracket0  = 0 < fBr ? 0 : 1; m.offspringSexChanceBracket1  = 1 < fBr ? 0 : 1; m.offspringSexChanceBracket2  = 2 < fBr ? 0 : 1;
                    m.offspringSexChanceBracket3  = 3 < fBr ? 0 : 1; m.offspringSexChanceBracket4  = 4 < fBr ? 0 : 1; m.offspringSexChanceBracket5  = 5 < fBr ? 0 : 1;
                    m.offspringSexChanceBracket6  = 6 < fBr ? 0 : 1; m.offspringSexChanceBracket7  = 7 < fBr ? 0 : 1; m.offspringSexChanceBracket8  = 8 < fBr ? 0 : 1;
                    m.offspringSexChanceBracket9  = 9 < fBr ? 0 : 1; m.offspringSexChanceBracket10 = 10 < fBr ? 0 : 1; m.offspringSexChanceBracket11 = 11 < fBr ? 0 : 1;
                    def.sexes[i] = m;
                }
                for (int i = 0; i < def.youngSexes.Length; i++)
                {
                    var m = def.youngSexes[i];
                    m.wholeAnimalDietHueRange = new Vector2(0f, 1f);
                    m.wholeAnimalDietBrightnessRange = new Vector2(0f, 1f);
                    m.wholeAnimalDietSecondaryHueRange = new Vector2(0f, 1f);
                    m.wholeAnimalDietSecondaryBrightnessRange = new Vector2(0f, 1f);
                    m.invertWholeAnimalDietColorRange = false;
                    if (m.wholeAnimalDietExcludeSkinPatterns != null) m.wholeAnimalDietExcludeSkinPatterns.Clear();
                    def.youngSexes[i] = m;
                }
            }
            else EnforcePureHerbivoreDiet(def);

            // Instincts (NO GoAwayFrom on TooWarm/TooCold: GoAwayFrom sets actionIsInstinctivelyPrioritized=true,
            // which skips BOTH mating and eating for the entire season!).
            // For carnivores:
            // - Females prioritize Mate -> Food -> Cry (Cry ALSO sets prioritized=true and ReadyForInternalFertilization(true)
            //   stays true even after fertilization until pregnant, so Cry MUST come AFTER Food so females hunt & eat!).
            // - Males prioritize Mate -> Hear ASound (7.2-cell mating call homing) -> Food.
            InstinctDefinition[] femaleInstincts = obligateCarnivore
                ? new[]
                {
                    MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, InstinctAction.GoTowards),
                    MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.Food, InstinctAction.GoTowards),
                    MakeInstinct(InstinctConditionSense.Feel, InstinctConditionObject.ReadyToMate, InstinctAction.Cry),
                }
                : new[]
                {
                    MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, InstinctAction.GoTowards),
                    MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.Food, InstinctAction.GoTowards),
                };
            InstinctDefinition[] maleInstincts = obligateCarnivore
                ? new[]
                {
                    MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, InstinctAction.GoTowards),
                    MakeInstinct(InstinctConditionSense.Hear, InstinctConditionObject.ASound, InstinctAction.GoTowards),
                    MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.Food, InstinctAction.GoTowards),
                }
                : femaleInstincts;
            def._instincts = femaleInstincts;
            var yi = def.young; yi.instincts = femaleInstincts; def.young = yi;
            for (int i = 0; i < def.sexes.Length; i++) { var m = def.sexes[i]; m.instincts = maleInstincts; def.sexes[i] = m; }
            for (int i = 0; i < def.youngSexes.Length; i++) { var m = def.youngSexes[i]; m.instincts = maleInstincts; def.youngSexes[i] = m; }

            def.currentSex = 0;
            var yc0 = new AnimalStatsCalculator(importer, 1f); yc0.Calculate(def, true);
            var ac0 = new AnimalStatsCalculator(importer, 1f); ac0.Calculate(def, false);
            // If scaled up in bodySize so predator > prey, gently trim torso diameters proportionally to stay under leg weight limit.
            for (int iter = 0; iter < 60 && ac0.weight > ac0.weightSupportedForWalking - 0.15f && def.torso != null; iter++)
            {
                for (int t = 0; t < def.torso.Length; t++)
                    def.torso[t].diameter = Math.Max(1.2f, def.torso[t].diameter * 0.95f);
                yc0.Calculate(def, true);
                ac0.Calculate(def, false);
            }
            var p0 = OrganismProblemDetection.DetectProblemInAnimal(def, yc0, ac0);
            def.currentSex = 1;
            var yc1 = new AnimalStatsCalculator(importer, 1f); yc1.Calculate(def, true);
            var ac1 = new AnimalStatsCalculator(importer, 1f); ac1.Calculate(def, false);
            var p1 = OrganismProblemDetection.DetectProblemInAnimal(def, yc1, ac1);
            def.currentSex = 0;
            log($"[COEVO] Seed '{speciesName}' ({(obligateCarnivore ? "CARN" : "HERB")}): size={def._bodySize}, age={ac0.expectedAge}d, preg={ac0.pregnancyTime}d, spd={ac0.speed:F2}(y:{yc0.speed:F2}), stp={ac0.maximumSteepness:F2}, wt={ac0.weight:F1}/{ac0.weightSupportedForWalking:F1}, atk={ac0.attack}, temp=[{ac0.minimumTemperatureThisOrganismCanHandle:F1}..{ac0.maximumTemperatureThisOrganismCanHandle:F1}C], prob=(F:{p0}, M:{p1}), obligCarn={IsObligateCarnivoreDef(def)}");

            // Find habitable land cells via AnimalCellRequirementChecker, preferring the largest connected flat basins
            WorldArea area = tl.today.areas[0];
            var checker = new AnimalCellRequirementChecker();
            int w = (int)area.size.x, h = (int)area.size.y;
            bool[] habitable = new bool[area.cells.Length];
            for (int i = 0; i < area.cells.Length; i++)
            {
                WorldCell c = area.cells[i];
                if (c == null || !c.usable || !c.walkable || c.oceanDepth >= 0f) continue;
                checker.Check(c, def, ac0, true);
                if (checker.okayForAnimal) habitable[i] = true;
            }
            // Group habitable cells into connected components so slow protolobe walkers aren't stranded on tiny hilltops
            int[] compId = new int[area.cells.Length];
            var compCells = new List<List<int>>();
            var q = new Queue<int>();
            for (int i = 0; i < area.cells.Length; i++)
            {
                if (!habitable[i] || compId[i] != 0) continue;
                int cid = compCells.Count + 1;
                var list = new List<int>();
                compId[i] = cid;
                q.Enqueue(i);
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    list.Add(cur);
                    int cx = cur / h, cy = cur % h;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int ni = nx * h + ny;
                            if (habitable[ni] && compId[ni] == 0) { compId[ni] = cid; q.Enqueue(ni); }
                        }
                }
                compCells.Add(list);
            }
            compCells.Sort((a, b) => b.Count.CompareTo(a.Count));
            var candidateCells = new List<int>();
            foreach (var comp in compCells)
            {
                if (comp.Count < 16) continue;
                foreach (int i in comp)
                {
                    if (area.GetAnimalInCell(i, CellLocationType.Ground) != null) continue;
                    WorldCell c = area.cells[i];
                    bool good = false;
                    if (!obligateCarnivore)
                    {
                        good = c.plant != null;
                    }
                    else
                    {
                        int cx = i / h, cy = i % h;
                        for (int dx = -3; dx <= 3 && !good; dx++)
                            for (int dy = -3; dy <= 3 && !good; dy++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx >= 0 && ny >= 0 && nx < w && ny < h && area.GetAnimalInCell(nx * h + ny, CellLocationType.Ground) != null)
                                    good = true;
                            }
                    }
                    if (good) candidateCells.Add(i);
                }
            }
            // Spawn F/M pairs inside connected basins with >=3-cell spacing between pairs so
            // FindNumberOfAnimalsAroundCell <= 1 and EnoughSpaceForReproduction passes immediately!
            def.definitionIndex = tl.nextDefinitionIndex++;
            int spawned = 0;
            var used = new HashSet<int>();
            var pairRoots = new List<int>();
            for (int k = 0; k < candidateCells.Count && spawned < count; k++)
            {
                int c0 = candidateCells[k];
                if (used.Contains(c0) || area.GetAnimalInCell(c0, CellLocationType.Ground) != null) continue;
                int cx = c0 / h, cy = c0 % h;
                bool tooClose = false;
                int minPairSep = obligateCarnivore ? 2 : 3;
                for (int r = 0; r < pairRoots.Count; r++)
                {
                    int rx = pairRoots[r] / h, ry = pairRoots[r] % h;
                    if (Math.Abs(rx - cx) <= minPairSep && Math.Abs(ry - cy) <= minPairSep) { tooClose = true; break; }
                }
                if (tooClose) continue;
                int c1 = -1;
                for (int dx = -1; dx <= 1 && c1 < 0; dx++)
                    for (int dy = -1; dy <= 1 && c1 < 0; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = nx * h + ny;
                        if (!habitable[ni]) continue;
                        if (!used.Contains(ni) && area.GetAnimalInCell(ni, CellLocationType.Ground) == null) c1 = ni;
                    }
                if (c1 < 0) continue;
                pairRoots.Add(c0);
                int[] pairCells = new[] { c0, c1 };
                for (int s = 0; s < 2 && spawned < count; s++)
                {
                    used.Add(pairCells[s]);
                    AnimalDefinition instDef = (AnimalDefinition)def.Clone();
                    instDef.definitionIndex = def.definitionIndex;
                    instDef.currentSex = s;
                    tl.nextSpeciesName = speciesName;
                    tl.InsertAnimal(instDef, 0, pairCells[s], CellLocationType.Ground, true, -1);
                    LivingAnimal placed = area.GetAnimalInCell(pairCells[s], CellLocationType.Ground);
                    if (placed != null)
                    {
                        placed.isChild = false;
                        // Stagger founder ages: spawning a whole cohort at one age makes them all
                        // die of old age together, collapsing the species before generation 2 breeds.
                        int ageMax = Math.Max(13, (int)ac0.expectedAge - 1);
                        placed.age = obligateCarnivore
                            ? 12 + (spawned * 7) % Math.Max(1, ageMax - 12)
                            : Math.Max(12, ac0.expectedAge / 4 + 1);
                        placed.UpdateTemperature(area.cells[pairCells[s]].temperature);
                        if (spawned == 0)
                        {
                            if (obligateCarnivore) { _carnDeaths.Clear(); _carnSpeciesMin = tl.populationBasedAnimalClassifier.LookUp(placed.definition.definitionIndex); }
                            var flist = new List<string>();
                            if (placed.orderedDetailedFoodTypes != null)
                                for (int fi = 0; fi < placed.orderedDetailedFoodTypes.Length; fi++)
                                    flist.Add($"{placed.orderedDetailedFoodTypes[fi]}:{placed.energyPerDetailedFoodType[fi]:F1}");
                            log($"[COEVO] '{speciesName}' diet=[{string.Join(",", flist)}] loss={placed.dailyEnergyLoss:F2}/d reproThr={placed.CalculateReproductionThreshold():F1} reproCost={placed.energyNeededToReproduce:F1} see={tl.GetSeeingDistance(placed, CellLocationType.Ground):F1} maxKids={placed.maximumNrOfChildren}");
                        }
                        spawned++;
                    }
                }
            }
            log($"[COEVO] Spawned {spawned}/{count} '{speciesName}' ({(obligateCarnivore ? "CARN" : "HERB")}) in dense connected basins (largest={(compCells.Count > 0 ? compCells[0].Count : 0)} cells, candidates={candidateCells.Count}).");
        }

        // Places a built definition in open water. The archived-bodyplan seeder rejects every
        // cell with oceanDepth >= 0, so aquatic species need their own placement pass.
        private static void SeedAquatic(TimeLine tl, OrganismObjectFileImporter importer, AnimalDefinition def, string name, int count, Action<string> log)
        {
            WorldArea area = tl.today.areas[0];
            var calc = new AnimalStatsCalculator(importer, tl.gravity);
            calc.Calculate(def, false);
            var checker = new AnimalCellRequirementChecker();
            bool[] ok = new bool[area.cells.Length];
            int okN = 0;
            float tMin = 99f, tMax = -99f; double tSum = 0;
            for (int i = 0; i < area.cells.Length; i++)
            {
                WorldCell c = area.cells[i];
                if (c == null || !c.usable || c.oceanDepth < 0f) continue;
                checker.Check(c, def, calc, true);
                if (checker.okayForAnimal) { ok[i] = true; okN++; tMin = Math.Min(tMin, c.temperature); tMax = Math.Max(tMax, c.temperature); tSum += c.temperature; }
            }
            int w = (int)area.size.x, h = (int)area.size.y;
            def.definitionIndex = tl.nextDefinitionIndex++;
            int spawned = 0;
            var used = new HashSet<int>();
            // Group warm cells into 12x12 blocks and fill the warmest blocks first. Founders land
            // in dense neighbourhoods (mates within sight) without all stripping one patch.
            var cand = new List<int>();
            for (int i = 0; i < area.cells.Length; i++) if (ok[i]) cand.Add(i);
            var blocks = new Dictionary<int, List<int>>();
            foreach (int i in cand)
            {
                int key = (i / h / 12) * 10000 + (i % h / 12);
                if (!blocks.TryGetValue(key, out List<int> bl)) blocks[key] = bl = new List<int>();
                bl.Add(i);
            }
            var ordered = new List<List<int>>(blocks.Values);
            ordered.Sort((a, b) =>
            {
                float sa = -999f, sb = -999f;
                foreach (int i in a) sa = Math.Max(sa, area.cells[i].temperature - area.cells[i].oceanCurrentSpeed);
                foreach (int i in b) sb = Math.Max(sb, area.cells[i].temperature - area.cells[i].oceanCurrentSpeed);
                return sb.CompareTo(sa);
            });
            cand.Clear();
            foreach (List<int> bl in ordered) cand.AddRange(bl);
            foreach (int i in cand)
            {
                if (spawned >= count) break;
                if (!ok[i] || used.Contains(i) || area.GetAnimalInCell(i, CellLocationType.UnderWater) != null) continue;
                int cx = i / h, cy = i % h, nb = -1;
                for (int dx = -1; dx <= 1 && nb < 0; dx++)
                    for (int dy = -1; dy <= 1 && nb < 0; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = nx * h + ny;
                        if (!ok[ni] || used.Contains(ni)) continue;
                        if (area.GetAnimalInCell(ni, CellLocationType.UnderWater) == null) nb = ni;
                    }
                if (nb < 0) continue;
                used.Add(i); used.Add(nb);
                int[] cells = new[] { i, nb };
                for (int s = 0; s < 2 && spawned < count; s++)
                {
                    AnimalDefinition inst = (AnimalDefinition)def.Clone();
                    inst.definitionIndex = def.definitionIndex;
                    inst.currentSex = s;
                    tl.nextSpeciesName = name;
                    tl.InsertAnimal(inst, 0, cells[s], CellLocationType.UnderWater, true, -1);
                    LivingAnimal placed = area.GetAnimalInCell(cells[s], CellLocationType.UnderWater);
                    if (placed != null)
                    {
                        placed.isChild = false;
                        int ageMax = Math.Max(13, (int)calc.expectedAge - 1);
                        placed.age = 12 + (spawned * 7) % Math.Max(1, ageMax - 12);
                        placed.UpdateTemperature(area.cells[cells[s]].temperature);
                        if (spawned == 0)
                        {
                            _aquaSpecies = tl.populationBasedAnimalClassifier.LookUp(placed.definition.definitionIndex);
                            _aquaDeaths.Clear();
                            var fl = new List<string>();
                            if (placed.orderedDetailedFoodTypes != null)
                                for (int fi = 0; fi < placed.orderedDetailedFoodTypes.Length; fi++)
                                    fl.Add($"{placed.orderedDetailedFoodTypes[fi]}:{placed.energyPerDetailedFoodType[fi]:F1}");
                            log($"[AQUA] diet=[{string.Join(",", fl)}] canWalk={placed.canWalk} canSwim={placed.canSwim} inW={placed.timeInWater} onL={placed.timeOnLand}");
                        }
                        spawned++;
                    }
                }
            }
            log($"[AQUA] Spawned {spawned}/{count} '{name}' size={def._bodySize} age={calc.expectedAge}d swim={calc.canSwim} swSpeed={calc.swimmingSpeed:F2} inW={calc.timeInWater} onL={calc.timeOnLand} wt={calc.weight:F1}/{calc.weightSupportedForWalking:F1} waterCells={okN} temp=[{calc.minimumTemperatureThisOrganismCanHandle:F1}..{calc.maximumTemperatureThisOrganismCanHandle:F1}]");
            log($"[AQUA] water temp=[{tMin:F2}..{tMax:F2}] avg={(okN > 0 ? tSum / okN : 0):F2}");
        }

        private static void LogAnimalGuildBreakdown(TimeLine tl, Action<string> log)
        {
            var herbSpp = new Dictionary<int, int[]>();
            var carnSpp = new Dictionary<int, int[]>();
            int backMouths = 0, frontMouths = 0;
            foreach (WorldArea area in tl.today.areas)
            {
                foreach (AnimalPosition pos in area.animalPositionTracker.animalPositions)
                {
                    LivingAnimal la = area.GetAnimalInCell(pos.cellIndex, pos.locationType);
                    if (la == null || la.eaten || la.definition == null) continue;
                    int spIdx = tl.usePopulationBasedClassification ? tl.populationBasedAnimalClassifier.LookUp(la.definition.definitionIndex) : -1;
                    bool carn = IsObligateCarnivoreDef(la.definition);
                    var dict = carn ? carnSpp : herbSpp;
                    if (!dict.ContainsKey(spIdx)) dict[spIdx] = new int[2];
                    dict[spIdx][la.definition.currentSex == 0 ? 0 : 1]++;
                    if (la.definition.torso != null && la.definition.torso.Length > 0 && la.definition.torso[0] != null)
                    {
                        foreach (var kv in la.definition.torso[0].children)
                            if (kv.Value is MouthDefinition)
                            {
                                if (kv.Key.side == TorsoPartSide.Back) backMouths++;
                                else if (kv.Key.side == TorsoPartSide.Front) frontMouths++;
                            }
                    }
                }
            }
            int hTot = 0, hF = 0, hM = 0, cTot = 0, cF = 0, cM = 0;
            foreach (var kv in herbSpp) { hF += kv.Value[0]; hM += kv.Value[1]; hTot += kv.Value[0] + kv.Value[1]; }
            foreach (var kv in carnSpp) { cF += kv.Value[0]; cM += kv.Value[1]; cTot += kv.Value[0] + kv.Value[1]; }
            log($"[GUILDS] Herbivores: {hTot} (F={hF}/M={hM}) across {herbSpp.Count} spp | Obligate Carnivores: {cTot} (F={cF}/M={cM}) across {carnSpp.Count} spp | torso[0] mouths: Back(OK)={backMouths}, Front(BAD)={frontMouths}");
            foreach (var kv in carnSpp)
            {
                string nm = (kv.Key >= 0 && kv.Key < tl.allSpecies.Count) ? tl.allSpecies[kv.Key].name : "?";
                log($"  [CARN] spp {kv.Key} '{nm}': {kv.Value[0] + kv.Value[1]} (F={kv.Value[0]}, M={kv.Value[1]})");
            }
            int shownH = 0;
            foreach (var kv in herbSpp)
            {
                if (shownH++ >= 12) break;
                string nm = (kv.Key >= 0 && kv.Key < tl.allSpecies.Count) ? tl.allSpecies[kv.Key].name : "?";
                log($"  [HERB] spp {kv.Key} '{nm}': {kv.Value[0] + kv.Value[1]} (F={kv.Value[0]}, M={kv.Value[1]})");
            }
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
            // On horizontal animals, torso[0] is the head and its outward front face is TorsoPartSide.Back.
            seg0.SetChild(mouth, TorsoPartSide.Back);
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

        // Aquatic plankton feeder built from parts, same method as Solara.
        // rakers mouth (id 30) breathes water and digests plankton; waterwing (id 25) swims.
        private static AnimalDefinition BuildAquaticDefinition(OrganismObjectFileImporter importer, int bodySize = 12)
        {
            var def = new AnimalDefinition();
            def.definitionIndex = 0;
            def._bodySize = bodySize;
            def._skinType = SkinType.Feathered; // +10C cold / +5C heat, the shallow water here runs cold
            def._skinPattern = SkinPattern.Plain;
            def._colorHue = 0.55f;
            def._colorBrightness = 0.63f;
            def._secondaryColorHue = 0.48f;
            def._secondaryColorBrightness = 0.82f;
            def._upright = false;
            def._nestType = NestType.None;
            def._nestEnclosed = false;
            def._embryoTimeInsideMother = 4; // viviparous, no eggs to lose in water
            def._eggType = EggType.Capsule; // UnderWater egg location; Ovoid is DryLand and blocks water births
            def._noEggsBelowTemperature = 0f;
            def._noEggsAboveTemperature = 1f;
            def._noHatchBelowTemperature = 0f;
            def._noHatchAboveTemperature = 1f;
            def._sleepPattern = (SleepPattern)0;
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

            var seg0 = new TorsoPartDefinition(0f, 3.0f);
            var mouth = importer.CreateMouthWithShapeIndex(9); // filter: plankton 5, drag 1, water oxygen
            mouth.scale = 1.0f;
            if (mouth.energyPerFoodType != null)
            {
                mouth.energyPerFoodType.Remove(DetailedFoodType.WholeAnimal);
                mouth.energyPerFoodType.Remove(DetailedFoodType.FreshMeat);
                mouth.energyPerFoodType.Remove(DetailedFoodType.OldMeat);
                mouth.energyPerFoodType.Remove(DetailedFoodType.Egg);
                mouth.energyPerFoodType.Remove(DetailedFoodType.Blood);
            }
            mouth.eatsMeat = false;
            mouth.getsMostEnergyFromMeat = false;
            // Macro algae count as SoftPlantTissue and marine snow as its own type. Both are in
            // the water already, so a broader diet raises the carrying capacity without meat.
            if (mouth.energyPerFoodType != null)
            {
                mouth.energyPerFoodType[DetailedFoodType.SoftPlantTissue] = 6f;
                mouth.energyPerFoodType[DetailedFoodType.MarineSnow] = 4f;
                mouth.energyPerFoodType[DetailedFoodType.Plankton] = 10f; // efficient filter feeder
            }
            mouth.eatsPlants = true;
            var eye = importer.CreateEyeWithShapeIndex(18);     // aquavision: underwater sight
            eye.scale = 1.0f;
            var ear = importer.CreateEarWithShapeIndex(2);      // elven, hear 4.0 for mate calls
            ear.scale = 1.0f;
            seg0.SetChild(mouth, TorsoPartSide.Back);
            seg0.SetChild(eye, TorsoPartSide.Side);
            seg0.SetChild(ear, TorsoPartSide.Side);

            var seg1 = new TorsoPartDefinition(0f, 5.0f);
            var fin = importer.CreateLimbWithShapeIndex(25);    // waterwing, underWaterSpeed 5
            fin.scale = 1.0f;
            seg1.SetChild(fin, TorsoPartSide.Side);

            var seg2 = new TorsoPartDefinition(0f, 4.0f);
            var fin2 = importer.CreateLimbWithShapeIndex(25);   // second fin pair for feeding speed
            fin2.scale = 1.0f;
            seg2.SetChild(fin2, TorsoPartSide.Side);

            // webbed feet give maximumSteepness > 0: EnoughSpaceForReproduction steps to a
            // neighbouring cell with that limit, and 0 makes a swimmer sterile.
            var seg3 = new TorsoPartDefinition(0f, 3.0f);
            var web = importer.CreateLimbWithShapeIndex(26);    // webbed
            web.scale = 1.0f;
            seg3.SetChild(web, TorsoPartSide.Side);
            def.torso = new TorsoPartDefinition[] { seg0, seg1, seg2, seg3 };

            def._instincts = new InstinctDefinition[]
            {
                MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, InstinctAction.GoTowards),
                MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.Food, InstinctAction.GoTowards),
            };
            InstinctDefinition[] maleInstincts = new InstinctDefinition[]
            {
                MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.APotentialMate, InstinctAction.GoTowards),
                MakeInstinct(InstinctConditionSense.Hear, InstinctConditionObject.ASound, InstinctAction.GoTowards),
                MakeInstinct(InstinctConditionSense.See, InstinctConditionObject.Food, InstinctAction.GoTowards),
            };

            def._canGiveBirthAsexually = false;
            def._canGiveBirthSexually = true;
            def._canFertilizeInternally = true; // hermaphrodite: any two adults can cross-fertilize
            def._canFertilizeExternally = false;
            def._offspringSexDecisionType = OffspringSexDecisionType.Chance;
            def._offspringSexChanceBracket0 = 0; def._offspringSexChanceBracket1 = 0; def._offspringSexChanceBracket2 = 0;
            def._offspringSexChanceBracket3 = 0; def._offspringSexChanceBracket4 = 0; def._offspringSexChanceBracket5 = 0;
            def._offspringSexChanceBracket6 = 1; def._offspringSexChanceBracket7 = 1; def._offspringSexChanceBracket8 = 1;
            def._offspringSexChanceBracket9 = 1; def._offspringSexChanceBracket10 = 1; def._offspringSexChanceBracket11 = 1;

            def.sexes = new AnimalMorph[0];
            def.youngSexes = new AnimalMorph[0];
            def.currentSex = 0;
            def.young = AnimalMorph.CreateMorphBasedOnAnimalDefinition(def);

            AnimalMorph maleMorph = AnimalMorph.CreateMorphBasedOnAnimalDefinition(def);
            maleMorph.instincts = maleInstincts;
            maleMorph.canGiveBirthAsexually = false;
            maleMorph.canGiveBirthSexually = false;
            maleMorph.canFertilizeInternally = true;
            maleMorph.canFertilizeExternally = false;
            maleMorph.colorHue = 0.62f;
            // Display trait: Solara's male tuft is what pushes attractiveness up; without a
            // visible difference the males score 0 and are ignored as mates.
            maleMorph.bodyPartDiffs = new Dictionary<BodyPartDiffLocation, BodyPartDiff>();
            var tuftLoc = new BodyPartDiffLocation { torsoPart = 0, side = TorsoPartSide.Top, offset = TorsoPartLocationOffset.None };
            maleMorph.bodyPartDiffs[tuftLoc] = new BodyPartDiff
            {
                type = BodyPartDiffType.OtherBodyPart,
                otherBodyPartTypeIndex = 10,
                otherBodyPartShapeIndex = 45,
                otherScale = 1.0f
            };
            maleMorph.colorBrightness = 0.72f;
            maleMorph.offspringSexDecisionType = OffspringSexDecisionType.Chance;
            maleMorph.offspringSexChanceBracket0 = 0; maleMorph.offspringSexChanceBracket1 = 0; maleMorph.offspringSexChanceBracket2 = 0;
            maleMorph.offspringSexChanceBracket3 = 0; maleMorph.offspringSexChanceBracket4 = 0; maleMorph.offspringSexChanceBracket5 = 0;
            maleMorph.offspringSexChanceBracket6 = 1; maleMorph.offspringSexChanceBracket7 = 1; maleMorph.offspringSexChanceBracket8 = 1;
            maleMorph.offspringSexChanceBracket9 = 1; maleMorph.offspringSexChanceBracket10 = 1; maleMorph.offspringSexChanceBracket11 = 1;

            def.sexes = new AnimalMorph[] { maleMorph };
            def.youngSexes = new AnimalMorph[] { AnimalMorph.Clone(maleMorph) };
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

            if (args != null && args.Length > 1 && args[0] == "--inspect")
            {
                return InspectFile(args[1], importer);
            }
            if (args != null && args.Length > 3 && args[0] == "--export-top-sexual")
            {
                return ExportTopSexualFromSave(args[1], args[2], args[3], importer);
            }

            if (args != null && args.Length > 3 && args[0] == "--export-species")
            {
                return ExportSpeciesFromSave(args[1], args[2], args[3], importer);
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

            // Rectangular grid parity self-check: asserts 0 mismatches between vanilla and FastSimPatches
            // on both 50x100 and 100x50 WorldAreas across all cells and movement pairs.
            if (args != null && args.Length > 0 && args[0] == "--rect-check")
            {
                int[][] dims = { new[] { 50, 100 }, new[] { 100, 50 } };
                var vanAround = new List<int[][]>();
                var vanStep = new List<int[]>();
                var vanEmpty = new List<int[]>();
                var vanGetEmpty = new List<int[]>();
                var vanTemp = new List<int[]>();
                var vanCount = new List<int[]>();
                var areas = new List<WorldArea>();
                var rng = new System.Random(42);
                var dummyAnimal = (LivingAnimal)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(LivingAnimal));
                var dummyTl = (TimeLine)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TimeLine));
                foreach (var d in dims)
                {
                    int w = d[0], h = d[1], n = w * h;
                    var a = new WorldArea();
                    a.size = new Vector2(w, h);
                    a.cells = new WorldCell[n];
                    for (int i = 0; i < n; i++)
                    {
                        var c = new WorldCell(0.5f, 0.5f, 0.5f, (float)(rng.NextDouble() * 40.0), 0.5f, -1f, 0f, 0.5f);
                        c.usable = true;
                        c.walkable = true;
                        c.steepness = (float)(rng.NextDouble() * 90.0);
                        if (rng.Next(4) == 0) c.animal = dummyAnimal;
                        a.cells[i] = c;
                    }
                    areas.Add(a);
                    var ar = new int[n][];
                    var em = new int[n];
                    var ge = new int[n];
                    var tp = new int[n];
                    var cn = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        ar[i] = (int[])a.GetAllCellsDirectlyAround(i).Clone();
                        em[i] = a.FindEmptyCellNearCell(i);
                        ge[i] = a.GetEmptyCellNearCell(i, CellLocationType.Ground);
                        tp[i] = a.FindCellWithDifferentTemperatureThanCell(i, (i & 1) == 0);
                        cn[i] = dummyTl.FindNumberOfAnimalsAroundCell(a, i, CellLocationType.Ground);
                    }
                    var st = new int[1000];
                    for (int k = 0; k < 1000; k++)
                    {
                        int cur = (k * 37) % n;
                        int goal = (k * 997 + 13) % n;
                        st[k] = a.MoveSingleStepInDirectionOfCellWithMaxSteepness(cur, goal, 0.8f);
                    }
                    vanAround.Add(ar); vanEmpty.Add(em); vanGetEmpty.Add(ge); vanTemp.Add(tp); vanCount.Add(cn); vanStep.Add(st);
                }

                ApplyBridgePatches(false, new Type[] { typeof(SaplingRustBridge.Patches.BandPatches), typeof(SaplingRustBridge.Patches.FastSimPatches) });
                int mismatches = 0;
                for (int di = 0; di < dims.Length; di++)
                {
                    var a = areas[di]; int n = a.cells.Length;
                    for (int i = 0; i < n; i++)
                    {
                        int[] got = a.GetAllCellsDirectlyAround(i);
                        for (int j = 0; j < 8; j++) if (got[j] != vanAround[di][i][j]) { mismatches++; break; }
                        if (a.FindEmptyCellNearCell(i) != vanEmpty[di][i]) mismatches++;
                        if (a.GetEmptyCellNearCell(i, CellLocationType.Ground) != vanGetEmpty[di][i]) mismatches++;
                        if (a.FindCellWithDifferentTemperatureThanCell(i, (i & 1) == 0) != vanTemp[di][i]) mismatches++;
                        if (dummyTl.FindNumberOfAnimalsAroundCell(a, i, CellLocationType.Ground) != vanCount[di][i]) mismatches++;
                    }
                    for (int k = 0; k < 1000; k++)
                    {
                        int cur = (k * 37) % n;
                        int goal = (k * 997 + 13) % n;
                        if (a.MoveSingleStepInDirectionOfCellWithMaxSteepness(cur, goal, 0.8f) != vanStep[di][k]) mismatches++;
                    }
                }
                Console.WriteLine($"[RECT-CHECK] 50x100 + 100x50 parity mismatches = {mismatches}");
                return mismatches == 0 ? 0 : 1;
            }

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
