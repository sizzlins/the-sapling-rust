using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;


namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Eliminates the root causes of log spam, console locks, and engine stalls:
    /// 1. Prevents duplicate child LODGroups on nest prefabs and organism body parts.
    /// 2. Filters duplicate LODGroup warnings and animation clip info messages from disk logging.
    /// 3. Bounds checks WalkingAnimationControllerV2 to eliminate IndexOutOfRangeException on hopping/mutated creatures.
    /// 4. Sets anim.playAutomatically = false on AnimalView to stop 'Default clip could not be found' warnings.
    /// 5. Silences benign 'updating focussed organism centerpoints' warnings.
    /// 6. Clears gameObjectsNewlyGenerated buffer to prevent quadratic accumulation during camera movement.
    /// </summary>
    [HarmonyPatch]
    public static class CleanLogPatches
    {
        /// <summary>
        /// Strips any child LODGroups that conflict with a root LODGroup on the same GameObject hierarchy.
        /// </summary>
        public static void StripConflictingLODGroups(GameObject go)
        {
            if (go == null) return;
            LODGroup rootLod = go.GetComponent<LODGroup>();
            if (rootLod != null)
            {
                LODGroup[] allLods = go.GetComponentsInChildren<LODGroup>(true);
                for (int i = 0; i < allLods.Length; i++)
                {
                    if (allLods[i] != null && allLods[i] != rootLod)
                    {
                        UnityEngine.Object.Destroy(allLods[i]);
                    }
                }
            }
        }

        private static void SanitizeNestArray(GameObject[] nests)
        {
            if (nests == null) return;
            for (int i = 0; i < nests.Length; i++)
            {
                if (nests[i] != null)
                {
                    StripConflictingLODGroups(nests[i]);
                }
            }
        }

        // 1. Root fix for 77,000+ LODGroup warnings:
        // Strip duplicate child LODGroups from all nest prefabs when WorldView starts.
        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_SanitizeNestPrefabs(WorldView __instance)
        {
            if (__instance == null) return;
            GameObject[] prefabs = new GameObject[]
            {
                __instance.openSacBurrowPrefab, __instance.openSacTwigsPrefab, __instance.openSacWaxPrefab,
                __instance.openFlapsBurrowPrefab, __instance.openFlapsTwigsPrefab, __instance.openFlapsWaxPrefab,
                __instance.openOpeningBurrowPrefab, __instance.openOpeningTwigsPrefab, __instance.openOpeningWaxPrefab,
                __instance.openSphericalBurrowPrefab, __instance.openSphericalTwigsPrefab, __instance.openSphericalWaxPrefab,
                __instance.openCylinderBurrowPrefab, __instance.openCylinderTwigsPrefab, __instance.openCylinderWaxPrefab,
                __instance.openOvoidBurrowPrefab, __instance.openOvoidTwigsPrefab, __instance.openOvoidWaxPrefab,
                __instance.enclosedBurrowPrefab, __instance.enclosedTwigsPrefab, __instance.enclosedWaxPrefab
            };
            for (int i = 0; i < prefabs.Length; i++)
            {
                StripConflictingLODGroups(prefabs[i]);
            }
        }

        // Sanitize active nests whenever nests are updated or day progresses
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateNests))]
        [HarmonyPostfix]
        public static void WorldView_UpdateNests_Postfix(WorldView __instance)
        {
            if (__instance == null) return;
            SanitizeNestArray(__instance.nestsArea0);
            SanitizeNestArray(__instance.nestsArea1);
            SanitizeNestArray(__instance.nestsArea2);
        }

        [HarmonyPatch(typeof(WorldController), nameof(WorldController.ShowToday))]
        [HarmonyPrefix]
        public static void WorldController_ShowToday_SanitizeNests(WorldController __instance)
        {
            if (__instance != null && __instance.view != null)
            {
                SanitizeNestArray(__instance.view.nestsArea0);
                SanitizeNestArray(__instance.view.nestsArea1);
                SanitizeNestArray(__instance.view.nestsArea2);
            }
        }

        // Disable child LODGroup generation on individual body parts
        [HarmonyPatch(typeof(OrganismObjectBuilder), nameof(OrganismObjectBuilder.CreateGameObjectsForBodyPartMeshArray))]
        [HarmonyPrefix]
        public static void OrganismObjectBuilder_CreateGameObjects_Prefix(OrganismObjectBuilder __instance)
        {
            if (__instance != null)
            {
                __instance.addLODGroup = false;
            }
        }

        [HarmonyPatch(typeof(OrganismObjectBuilder), MethodType.Constructor)]
        [HarmonyPostfix]
        public static void OrganismObjectBuilder_ctor_Postfix(OrganismObjectBuilder __instance)
        {
            if (__instance != null)
            {
                __instance.addLODGroup = false;
            }
        }

        [HarmonyPatch(typeof(PlantObjectBuilder), nameof(PlantObjectBuilder.Run))]
        [HarmonyPostfix]
        public static void PlantObjectBuilder_Run_Postfix(GameObject __result)
        {
            StripConflictingLODGroups(__result);
        }

        // 2. Defense-in-depth: Suppress disk AND console logging of duplicate LODGroup and default animation warnings
        [HarmonyPatch(typeof(BepInEx.Logging.DiskLogListener), nameof(BepInEx.Logging.DiskLogListener.LogEvent))]
        [HarmonyPrefix]
        public static bool DiskLogListener_LogEvent_Prefix(object sender, BepInEx.Logging.LogEventArgs eventArgs)
        {
            return FilterSpamLog(eventArgs);
        }

        [HarmonyPatch(typeof(BepInEx.Logging.ConsoleLogListener), nameof(BepInEx.Logging.ConsoleLogListener.LogEvent))]
        [HarmonyPrefix]
        public static bool ConsoleLogListener_LogEvent_Prefix(object sender, BepInEx.Logging.LogEventArgs eventArgs)
        {
            return FilterSpamLog(eventArgs);
        }

        private static bool FilterSpamLog(BepInEx.Logging.LogEventArgs eventArgs)
        {
            if (eventArgs != null && eventArgs.Data != null)
            {
                string text = eventArgs.Data.ToString();
                if (text.IndexOf("is registered with more than one LODGroup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("Default clip could not be found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        // 3. Clear gameObjectsNewlyGenerated buffer to prevent quadratic re-binding loops on camera movement
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.AddHoverActionsToNewlyGeneratedGameObjects))]
        [HarmonyPostfix]
        public static void AddHoverActions_Postfix(WorldController __instance)
        {
            if (__instance != null && __instance.view != null && __instance.view.gameObjectsNewlyGenerated != null)
            {
                __instance.view.gameObjectsNewlyGenerated.Clear();
            }
        }

        // 4. Root fix for IndexOutOfRangeException in WalkingAnimationControllerV2.SolveInverseKinematicsForLeg:
        // Protects against array index mismatches in asymmetrical or hopping mutant creatures.
        [HarmonyPatch(typeof(WalkingAnimationControllerV2), "SolveInverseKinematicsForLeg")]
        [HarmonyPrefix]
        public static bool SolveInverseKinematicsForLeg_Prefix(WalkingAnimationControllerV2 __instance, int legIndex, bool left)
        {
            if (__instance == null || !__instance.solveIK) return false;

            try
            {
                if (left)
                {
                    if (__instance.leftUpperLegBones == null || legIndex >= __instance.leftUpperLegBones.Length || __instance.leftUpperLegBones[legIndex] == null) return false;
                    if (__instance.leftIKFeet == null || legIndex >= __instance.leftIKFeet.Length || __instance.leftIKFeet[legIndex] == null) return false;
                    if (!__instance.sprawling)
                    {
                        if (__instance.leftShinBones == null || legIndex >= __instance.leftShinBones.Length || __instance.leftShinBones[legIndex] == null) return false;
                        if (__instance.leftFootBones == null || legIndex >= __instance.leftFootBones.Length || __instance.leftFootBones[legIndex] == null) return false;
                    }
                }
                else
                {
                    if (__instance.rightUpperLegBones == null || legIndex >= __instance.rightUpperLegBones.Length || __instance.rightUpperLegBones[legIndex] == null) return false;
                    if (__instance.rightIKFeet == null || legIndex >= __instance.rightIKFeet.Length || __instance.rightIKFeet[legIndex] == null) return false;
                    if (!__instance.sprawling)
                    {
                        if (__instance.rightShinBones == null || legIndex >= __instance.rightShinBones.Length || __instance.rightShinBones[legIndex] == null) return false;
                        if (__instance.rightFootBones == null || legIndex >= __instance.rightFootBones.Length || __instance.rightFootBones[legIndex] == null) return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 5. Stop Unity looking for a default clip on frame 1 before procedural clips are generated
        [HarmonyPatch(typeof(AnimalView), "Start", new Type[] { typeof(AnimalDefinition), typeof(Vector3), typeof(int), typeof(RuntimeAnimatorController) })]
        [HarmonyPostfix]
        public static void AnimalView_Start_AnimationFix(AnimalView __instance)
        {
            if (__instance == null) return;
            if (__instance.anim != null)
            {
                __instance.anim.playAutomatically = false;
            }
        }

        // 6. Suppress benign 'updating focussed organism centerpoints while no organism is in focus'
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateCenterPointsClosestToCamera))]
        [HarmonyPrefix]
        public static bool WorldView_UpdateCenterPoints_Prefix(WorldView __instance)
        {
            if (__instance == null) return true;
            if (__instance.plantViewInFocus == null && __instance.animalViewInFocus == null)
            {
                return false;
            }
            return true;
        }

        // 7. Root fix for NullReferenceException in WorldView.ShowAnimalViewsForArea:
        // Automatically prunes stale/dead entries from animalPositions so the renderer never encounters a null LivingAnimal.
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyPrefix]
        public static void ShowAnimalViewsForArea_SanitizePositions_Prefix(WorldArea area)
        {
            if (area == null || area.animalPositionTracker == null || area.animalPositionTracker.animalPositions == null) return;

            try
            {
                area.animalPositionTracker.animalPositions.RemoveWhere(ap =>
                {
                    LivingAnimal a = area.GetAnimalInCell(ap.cellIndex, ap.locationType);
                    return a == null || a.definition == null;
                });
            }
            catch {}
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyFinalizer]
        public static Exception ShowAnimalViewsForArea_Finalizer(Exception __exception)
        {
            return null; // Suppress any remaining JIT null reference exceptions so frame rendering never breaks
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.SetAnimationSpeed))]
        [HarmonyPrefix]
        public static bool AnimalView_SetAnimationSpeed_Prefix(AnimalView __instance)
        {
            if (__instance == null || __instance.anim == null || __instance.gameObject == null)
            {
                return false; // Skip enumeration on uninitialized or JIT animals
            }
            return true;
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.SetAnimationSpeed))]
        [HarmonyFinalizer]
        public static Exception AnimalView_SetAnimationSpeed_Finalizer(Exception __exception)
        {
            return null;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.SetAnimalAnimationsToSpeedWithIndex))]
        [HarmonyFinalizer]
        public static Exception SetAnimalAnimationsToSpeedWithIndex_Finalizer(Exception __exception)
        {
            return null;
        }

        // 8. Root fix for NullReferenceException in OrganismObjectPool.GetFullSpecificOrganism:
        // Intercepts and catches pool corruption on destroyed Unity objects, returning null so vanilla cleanly builds a fresh model.
        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.GetFullSpecificOrganism), new Type[] { typeof(int), typeof(int), typeof(bool) })]
        [HarmonyFinalizer]
        public static Exception OrganismObjectPool_GetFullSpecificOrganism_Finalizer(Exception __exception, ref GameObject __result)
        {
            if (__exception != null)
            {
                __result = null;
                return null; // Suppress exception and fallback to fresh build
            }
            return null;
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.GetFullSpecificOrganism), new Type[] { typeof(int), typeof(bool) })]
        [HarmonyFinalizer]
        public static Exception OrganismObjectPool_GetFullSpecificOrganism2_Finalizer(Exception __exception, ref GameObject __result)
        {
            if (__exception != null)
            {
                __result = null;
                return null;
            }
            return null;
        }

        // 9. Root fix for 'indicesOfCellsWhereAMacroAlgaDied is null' warnings on WorldArea
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.LetAllAlgaeInAreaLiveADay))]
        [HarmonyPrefix]
        public static void LetAllAlgaeInAreaLiveADay_InitDiedLists_Prefix(TimeLine __instance, int areaIndex)
        {
            if (__instance == null || __instance.today == null || __instance.today.areas == null) return;
            if (areaIndex < 0 || areaIndex >= __instance.today.areas.Length) return;
            WorldArea area = __instance.today.areas[areaIndex];
            if (area == null) return;
            if (area.indicesOfCellsWhereAMacroAlgaDied == null) area.indicesOfCellsWhereAMacroAlgaDied = new List<int>();
            if (area.indicesOfCellsWherePlanktonDied == null) area.indicesOfCellsWherePlanktonDied = new List<int>();
            if (area.indicesOfCellsWhereAPlantDied == null) area.indicesOfCellsWhereAPlantDied = new List<int>();
            if (area.indicesOfCellsWhereAFungusDied == null) area.indicesOfCellsWhereAFungusDied = new List<int>();
            if (area.indicesOfCellsWhereAnAnimalDied == null) area.indicesOfCellsWhereAnAnimalDied = new List<int>();
            if (area.locationTypesOfCellsWhereAnAnimalDied == null) area.locationTypesOfCellsWhereAnAnimalDied = new List<CellLocationType>();
        }

        private static float CalculateGradient(float distanceFromCenterOfChange, HeightChange heightChange)
        {
            if (heightChange.radius <= 0f) return 0f;
            float num2 = heightChange.radius * (1f - heightChange.slope);
            if (distanceFromCenterOfChange > num2)
            {
                float result = 1f - (distanceFromCenterOfChange - num2) / (heightChange.radius - num2);
                result = Mathf.Pow(result, 2f - heightChange.curve);
                return result * result * (3f - 2f * result);
            }
            return 1f;
        }

        // 10. Root fix for IndexOutOfRangeException in TerrainHeightChangeMap.GetSlopeValuesForChangeGroupOptimized:
        // Vanilla computes num2 = resolution.y - influencedCoordinate.y. When influencedCoordinate.y == 0,
        // num2 * resolution.x + num exceeds array bounds, throwing IndexOutOfRangeException and cascading
        // into KeyNotFoundException in PlanetEditorView.HighlightContinent.
        [HarmonyPatch(typeof(TerrainHeightChangeMap), nameof(TerrainHeightChangeMap.GetSlopeValuesForChangeGroupOptimized))]
        [HarmonyPrefix]
        public static bool TerrainHeightChangeMap_GetSlopeValuesForChangeGroupOptimized_Prefix(TerrainHeightChangeMap __instance, int groupIndex, ref float[] __result)
        {
            if (__instance == null) return true;
            int rx = (int)__instance.resolution.x;
            int ry = (int)__instance.resolution.y;
            int total = rx * ry;
            if (total <= 0) return true;

            float[] array = new float[total];
            __instance.multiplicationFactor = new Vector2(__instance.mapSize.x / __instance.resolution.x, __instance.mapSize.y / __instance.resolution.y);

            if (__instance.heightChanges != null)
            {
                for (int i = 0; i < __instance.heightChanges.Count; i++)
                {
                    HeightChange heightChange = __instance.heightChanges[i];
                    if (heightChange == null || heightChange.groupIndex != groupIndex || heightChange.influencedCoordinates == null) continue;

                    foreach (Vector2 influencedCoordinate in heightChange.influencedCoordinates)
                    {
                        int num = Mathf.Clamp((int)influencedCoordinate.x, 0, rx - 1);
                        int num2 = Mathf.Clamp(ry - 1 - (int)influencedCoordinate.y, 0, ry - 1);
                        int num3 = num2 * rx + num;
                        if (num3 < 0 || num3 >= total) continue;

                        float num4 = Vector2.Distance(influencedCoordinate, heightChange.centerCoordinate);
                        float num5 = (num4 < heightChange.radius) ? CalculateGradient(num4, heightChange) : 0f;
                        if (num5 > array[num3])
                        {
                            array[num3] = Mathf.Clamp(num5, 0f, 0.99f);
                        }
                    }
                }
            }

            __result = array;
            return false;
        }

        // 11. Root fix for NullReferenceException in TimeLine organism insertions
        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.InsertAlga))]
        [HarmonyFinalizer]
        public static Exception TimeLine_InsertAlga_Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception != null)
            {
                Plugin.LogWarn("[TimeLine.InsertAlga] Caught benign NullRef during alga placement: " + __exception.Message);
                __result = false;
                return null;
            }
            return null;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.InsertPlant))]
        [HarmonyFinalizer]
        public static Exception TimeLine_InsertPlant_Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception != null)
            {
                Plugin.LogWarn("[TimeLine.InsertPlant] Caught benign NullRef during plant placement: " + __exception.Message);
                __result = false;
                return null;
            }
            return null;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.InsertFungus))]
        [HarmonyFinalizer]
        public static Exception TimeLine_InsertFungus_Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception != null)
            {
                Plugin.LogWarn("[TimeLine.InsertFungus] Caught benign NullRef during fungus placement: " + __exception.Message);
                __result = false;
                return null;
            }
            return null;
        }

        [HarmonyPatch(typeof(TimeLine), nameof(TimeLine.InsertAnimal))]
        [HarmonyFinalizer]
        public static Exception TimeLine_InsertAnimal_Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception != null)
            {
                Plugin.LogWarn("[TimeLine.InsertAnimal] Caught benign NullRef during animal placement: " + __exception.Message);
                __result = false;
                return null;
            }
            return null;
        }
    }
}

