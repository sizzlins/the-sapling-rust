using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;


namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// High-performance LOD, Animation, and Inverse Kinematics Governor.
    /// Eliminates CPU framerate collapse at high populations even when the camera is still.
    /// Distant (>35m) creatures bypass 40-bone evaluations, IK raycasts, and shadow caster draw calls.
    /// </summary>
    [HarmonyPatch]
    public static class LODPatches
    {
        public const float CloseDistanceThreshold = 150f;
        public const float MidDistanceThreshold = 500f;
        public const float PlantDemoteDistance = 450f;
        public const float AnimalDemoteDistance = 1500f;

        public static WorldView ActiveWorldView = null;
        private static float _lastLODScanTime = 0f;
        private const float LODScanInterval = 0.25f; // Scan 4 times per second (negligible CPU cost)

        private static readonly AccessTools.FieldRef<WalkingAnimationControllerV2, Vector3> GoalRef =
            AccessTools.FieldRefAccess<WalkingAnimationControllerV2, Vector3>("goal");

        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_Postfix(WorldView __instance)
        {
            if (__instance == null) return;
            ActiveWorldView = __instance;
            __instance.cameraCloseDistanceThreshold = CloseDistanceThreshold;
            __instance.cameraMidDistanceThreshold = MidDistanceThreshold;
            __instance.limitDetailInTheDistance = true;
            __instance.detailInTheDistanceNrOfCellsThreshold = 350;
        }

        [HarmonyPatch(typeof(WorldView), "Update")]
        [HarmonyPostfix]
        public static void WorldView_Update_LODGovernor(WorldView __instance)
        {
            if (__instance == null || __instance.worldCamera == null) return;
            ActiveWorldView = __instance;

            // Maintain camera distance thresholds so models are visible across whole terrain
            if (__instance.cameraCloseDistanceThreshold < CloseDistanceThreshold)
            {
                __instance.cameraCloseDistanceThreshold = CloseDistanceThreshold;
                __instance.cameraMidDistanceThreshold = MidDistanceThreshold;
            }

            if (AsyncJumpWorker.IsRunning || TimeJumpPatches.IsTimeJumpActive)
            {
                return;
            }

            Vector3 camPos = __instance.worldCamera.transform.position;

            // Throttle LOD scanning to avoid per-frame dictionary iterations
            if (Time.time - _lastLODScanTime < LODScanInterval) return;
            _lastLODScanTime = Time.time;

            var animals = __instance.animalViewsPerLifeFormIndex;

            if (animals != null && animals.Count > 0)
            {
                foreach (var kvp in animals)
                {
                    var animal = kvp.Value;
                    if (animal == null || animal.JITOptimized || animal.gameObject == null) continue;

                    float dist = Vector3.Distance(camPos, animal.gameObject.transform.position);
                    animal.distanceToCamera = dist;

                    // Do not demote or destroy animals: let creatures stay visible, alive, and rendered on camera!

                    DistanceToCameraCategory newCategory = (dist <= CloseDistanceThreshold)
                        ? DistanceToCameraCategory.Close
                        : ((dist <= MidDistanceThreshold) ? DistanceToCameraCategory.Mid : DistanceToCameraCategory.Far);

                    bool isClose = (newCategory == DistanceToCameraCategory.Close);

                    // Update category and apply optimizations if category changed or if newly initialized
                    if (animal.distanceToCameraCategory != newCategory)
                    {
                        animal.distanceToCameraCategory = newCategory;
                        ApplyAnimalLOD(animal, isClose);
                    }
                    else if (!isClose)
                    {
                        // Distant animals that stopped walking: make sure walking controller is turned off
                        if (!animal.isWalking && animal.walkingAnimationController != null && animal.walkingAnimationController.enabled)
                        {
                            animal.walkingAnimationController.enabled = false;
                        }
                    }
                }
            }
        }

        public static void DemotePlantToBillboard(WorldView view, PlantView plant)
        {
            // No-op: Do not destroy plant GameObjects so spawned models stay persistent and visible
            return;
        }

        public static void DemoteAnimalToJIT(WorldView view, AnimalView animal)
        {
            // No-op: Do not destroy animal GameObjects in active play so spawned models stay visible and alive!
            return;
        }

        public static void DemoteAllDistantOrganisms(WorldView view, float plantDist = 350f, float animalDist = 1200f)
        {
            // No-op: Do not destroy organisms during memory trims
            return;
        }

        // Fast-Forward Optimization:
        // Skip scanning 60,000-120,000 empty map cells every world state change;
        // directly query the active animals from AnimalPositionTracker in O(active animals)
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.UpdateAnimationsBetweenWorldStates))]
        [HarmonyPrefix]
        public static bool UpdateAnimationsBetweenWorldStates_Prefix(WorldView __instance, float percentageOfInterStateTimePassed, WorldState lastShownState)
        {
            if (__instance == null || !__instance.stateWasShownLastFrame || lastShownState == null) return true;

            __instance.animalViewsForWhichToUpdateRoughAnimations.Clear();
            __instance.animalViewsForWhichToUpdateDetailedAnimations.Clear();
            __instance.cellIndicesForWhichToUpdateAnimations.Clear();
            __instance.cellLocationTypesForWhichToUpdateAnimations.Clear();
            __instance.areaIndicesForWhichToUpdateAnimations.Clear();

            var areas = lastShownState.areas;
            if (areas != null)
            {
                for (int i = 0; i < areas.Length; i++)
                {
                    var tracker = areas[i].animalPositionTracker;
                    if (tracker == null || tracker.animalPositions == null) continue;

                    foreach (var ap in tracker.animalPositions)
                    {
                        int cIdx = ap.cellIndex;
                        var loc = ap.locationType;
                        var array = __instance.GetAnimalViewsForArea(i, loc);
                        if (array != null && cIdx >= 0 && cIdx < array.Length)
                        {
                            var av = array[cIdx];
                            if (av != null)
                            {
                                __instance.animalViewsForWhichToUpdateDetailedAnimations.Add(av);
                                __instance.cellLocationTypesForWhichToUpdateAnimations.Add(loc);
                                __instance.cellIndicesForWhichToUpdateAnimations.Add(cIdx);
                                __instance.areaIndicesForWhichToUpdateAnimations.Add(i);
                            }
                        }
                    }
                }
            }

            __instance.stateWasShownLastFrame = false;
            __instance.roughAnimationsPending = true;
            return false; // Skip the 360,000-cell vanilla scan
        }

        private static void ApplyAnimalLOD(AnimalView animal, bool isClose)
        {
            if (animal == null || animal.gameObject == null) return;

            // 1. Legacy animation engine (bone matrix evaluations)
            if (animal.anim != null)
            {
                animal.anim.playAutomatically = false;
                animal.anim.cullingType = AnimationCullingType.AlwaysAnimate;
                animal.anim.enabled = isClose || animal.canFly;
            }

            // 2. Procedural Inverse Kinematics & Walking Controller
            if (animal.walkingAnimationController != null)
            {
                animal.walkingAnimationController.solveIK = isClose;
                animal.walkingAnimationController.footPrecisionMode = isClose 
                    ? FootPrecisionMode.SometimesUpdated 
                    : FootPrecisionMode.NeverUpdated;

                // If distant and not walking, disable the MonoBehaviour completely to stop Unity's Update() call
                animal.walkingAnimationController.enabled = isClose || animal.isWalking;
            }

            // 3. Renderers (Shadows, Offscreen Updates, and Multi-part meshes)
            var renderers = animal.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                var smr = renderers[r];
                if (smr != null)
                {
                    smr.enabled = true;
                    smr.shadowCastingMode = isClose ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    smr.receiveShadows = isClose;
                    smr.updateWhenOffscreen = true; // FIX: Prevents Unity from freezing mouth/jaw bone transforms on procedural rigs!
                    smr.quality = isClose ? SkinQuality.Bone4 : SkinQuality.Bone2;
                }
            }

            var meshRenderers = animal.gameObject.GetComponentsInChildren<MeshRenderer>(true);
            for (int m = 0; m < meshRenderers.Length; m++)
            {
                if (meshRenderers[m] != null) meshRenderers[m].enabled = true;
            }
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.Start))]
        [HarmonyPostfix]
        public static void AnimalView_Start_Postfix(AnimalView __instance)
        {
            if (__instance == null) return;
            OptimizeAnimalInstance(__instance);
        }

        [HarmonyPatch(typeof(PlantView), nameof(PlantView.Start))]
        [HarmonyPostfix]
        public static void PlantView_Start_Postfix(PlantView __instance)
        {
            if (__instance == null) return;
            OptimizePlantInstance(__instance);
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.AnimateIdle))]
        [HarmonyPrefix]
        public static bool AnimalView_AnimateIdle_Prefix(AnimalView __instance)
        {
            if (__instance == null) return true;
            // Distant creatures do not need idle animation curves and bone rewind
            if (__instance.distanceToCameraCategory != DistanceToCameraCategory.Close)
            {
                if (__instance.anim != null && __instance.anim.enabled)
                {
                    __instance.anim.enabled = false;
                }
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.AnimateAquaticIdle))]
        [HarmonyPrefix]
        public static bool AnimalView_AnimateAquaticIdle_Prefix(AnimalView __instance)
        {
            if (__instance == null) return true;
            if (__instance.distanceToCameraCategory != DistanceToCameraCategory.Close)
            {
                if (__instance.anim != null && __instance.anim.enabled)
                {
                    __instance.anim.enabled = false;
                }
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.AnimateIdleInTree))]
        [HarmonyPrefix]
        public static bool AnimalView_AnimateIdleInTree_Prefix(AnimalView __instance)
        {
            if (__instance == null) return true;
            if (__instance.distanceToCameraCategory != DistanceToCameraCategory.Close)
            {
                if (__instance.anim != null && __instance.anim.enabled)
                {
                    __instance.anim.enabled = false;
                }
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(WalkingAnimationControllerV2), nameof(WalkingAnimationControllerV2.WalkTo))]
        [HarmonyPrefix]
        public static void WalkingAnimationControllerV2_WalkTo_Prefix(WalkingAnimationControllerV2 __instance)
        {
            if (__instance != null)
            {
                __instance.enabled = true;
            }
        }

        [HarmonyPatch(typeof(WalkingAnimationControllerV2), nameof(WalkingAnimationControllerV2.StopWalking))]
        [HarmonyPostfix]
        public static void WalkingAnimationControllerV2_StopWalking_Postfix(WalkingAnimationControllerV2 __instance)
        {
            if (__instance != null && !__instance.solveIK)
            {
                __instance.enabled = false;
            }
        }

        [HarmonyPatch(typeof(WalkingAnimationControllerV2), "Update")]
        [HarmonyPrefix]
        public static bool WalkingAnimationControllerV2_Update_Prefix(WalkingAnimationControllerV2 __instance)
        {
            if (__instance == null || !__instance.walking || __instance.paused) return true;

            // When distant (solveIK is false), perform lightweight linear movement without bone/joint/raycast calculations
            if (!__instance.solveIK)
            {
                Vector3 curPos = __instance.transform.position;
                Vector3 goal = GoalRef(__instance);

                __instance.transform.position = Vector3.MoveTowards(curPos, goal, __instance.speed * Time.deltaTime * __instance.timeSpeed);

                if (Vector3.Distance(goal, __instance.transform.position) < 0.05f)
                {
                    __instance.StopWalking();
                }
                return false; // Skip MovePrimitive, MoveNormal, UpdateTail, UpdateNeck, and leg trigonometry!
            }

            return true;
        }

        [HarmonyPatch(typeof(AnimalView), "UpdateAnimationActivityBasedOnDistanceToCamera")]
        [HarmonyPrefix]
        public static void AnimalView_UpdateAnimationActivity_Prefix(ref float midThreshold, ref float farThreshold)
        {
            if (midThreshold > CloseDistanceThreshold) midThreshold = CloseDistanceThreshold;
            if (farThreshold > MidDistanceThreshold) farThreshold = MidDistanceThreshold;
        }

        [HarmonyPatch(typeof(PlantView), "UpdateAnimatorActivityBasedOnDistanceToCamera")]
        [HarmonyPrefix]
        public static void PlantView_UpdateAnimatorActivity_Prefix(ref float threshold)
        {
            if (threshold > CloseDistanceThreshold) threshold = CloseDistanceThreshold;
        }

        private static void OptimizeAnimalInstance(AnimalView animal)
        {
            if (animal == null) return;

            if (animal.gameObject != null)
            {
                ConfigureSkinnedMesh(animal.gameObject);
                ConfigureLegacyAnimation(animal.gameObject);
            }
            if (animal.gameObjectYoungVersion != null)
            {
                ConfigureSkinnedMesh(animal.gameObjectYoungVersion);
                ConfigureLegacyAnimation(animal.gameObjectYoungVersion);
            }
            if (animal.gameObjectOldVersion != null)
            {
                ConfigureSkinnedMesh(animal.gameObjectOldVersion);
                ConfigureLegacyAnimation(animal.gameObjectOldVersion);
            }
        }

        private static void OptimizePlantInstance(PlantView plant)
        {
            if (plant == null) return;

            if (plant.gameObjectYoungVersion != null)
            {
                ConfigureSkinnedMesh(plant.gameObjectYoungVersion);
                ConfigureAnimator(plant.gameObjectYoungVersion);

                if (!plant.currentlyShowingYoungVersion)
                {
                    plant.gameObjectYoungVersion.SetActive(false);
                }
            }

            if (plant.gameObjectOldVersion != null)
            {
                ConfigureSkinnedMesh(plant.gameObjectOldVersion);
                ConfigureAnimator(plant.gameObjectOldVersion);

                if (plant.currentlyShowingYoungVersion)
                {
                    plant.gameObjectOldVersion.SetActive(false);
                }
            }
        }

        private static void ConfigureSkinnedMesh(GameObject go)
        {
            if (go == null) return;
            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].updateWhenOffscreen = true; // FIX: Ensure procedural models (mouth/head/limbs) never detach
            }
        }

        private static void ConfigureLegacyAnimation(GameObject go)
        {
            if (go == null) return;
            var anims = go.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                anims[i].playAutomatically = false;
                anims[i].cullingType = AnimationCullingType.AlwaysAnimate;
            }
        }

        private static void ConfigureAnimator(GameObject go)
        {
            if (go == null) return;
            var animators = go.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }
        }

        // Guard against NullReferenceException in FungusView.Update when gameObject is not yet initialized
        [HarmonyPatch(typeof(FungusView), nameof(FungusView.Update))]
        [HarmonyPrefix]
        public static bool FungusView_Update_Prefix(FungusView __instance)
        {
            if (__instance == null || __instance.gameObject == null) return false;
            return true;
        }
    }
}
