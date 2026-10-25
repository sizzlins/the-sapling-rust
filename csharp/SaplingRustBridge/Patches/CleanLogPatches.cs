using System;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Eliminates the root causes of log spam, console locks, and engine stalls:
    /// 1. Prevents duplicate child LODGroups on fruit/branches (OrganismObjectBuilder.addLODGroup = false).
    /// 2. Bounds checks WalkingAnimationControllerV2 to eliminate IndexOutOfRangeException on hopping/mutated creatures.
    /// 3. Sets anim.playAutomatically = false on AnimalView to stop 'Default clip could not be found' warnings.
    /// 4. Silences benign 'updating focussed organism centerpoints' warnings.
    /// </summary>
    [HarmonyPatch]
    public static class CleanLogPatches
    {
        // 1. Root fix for 86,000+ LODGroup warnings:
        // Individual body parts (like fruit sacs) must NOT have their own LODGroups when the parent tree has the root LODGroup.
        [HarmonyPatch(typeof(OrganismObjectBuilder), MethodType.Constructor)]
        [HarmonyPostfix]
        public static void OrganismObjectBuilder_ctor_Postfix(OrganismObjectBuilder __instance)
        {
            if (__instance != null)
            {
                __instance.addLODGroup = false;
            }
        }

        // 2. Root fix for IndexOutOfRangeException in WalkingAnimationControllerV2.SolveInverseKinematicsForLeg:
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

        // 3. Stop Unity looking for a default clip on frame 1 before procedural clips are generated
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

        // 4. Suppress benign 'updating focussed organism centerpoints while no organism is in focus'
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
    }
}
