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
        public const float CloseDistanceThreshold = 35f;
        public const float MidDistanceThreshold = 80f;

        private static float _lastLODScanTime = 0f;
        private const float LODScanInterval = 0.25f; // Scan 4 times per second (negligible CPU cost)

        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_Postfix(WorldView __instance)
        {
            if (__instance == null) return;
            __instance.cameraCloseDistanceThreshold = CloseDistanceThreshold;
            __instance.cameraMidDistanceThreshold = MidDistanceThreshold;
            __instance.limitDetailInTheDistance = true;
            __instance.detailInTheDistanceNrOfCellsThreshold = 20;
        }

        [HarmonyPatch(typeof(WorldView), "Update")]
        [HarmonyPostfix]
        public static void WorldView_Update_LODGovernor(WorldView __instance)
        {
            if (__instance == null || __instance.worldCamera == null) return;

            // Ensure distance thresholds stay enforced even if game reloads scenes/settings
            if (__instance.cameraCloseDistanceThreshold > CloseDistanceThreshold)
            {
                __instance.cameraCloseDistanceThreshold = CloseDistanceThreshold;
                __instance.cameraMidDistanceThreshold = MidDistanceThreshold;
            }

            // Throttle LOD scanning to avoid per-frame dictionary iterations
            if (Time.time - _lastLODScanTime < LODScanInterval) return;
            _lastLODScanTime = Time.time;

            var animals = __instance.animalViewsPerLifeFormIndex;
            if (animals == null || animals.Count == 0) return;

            Vector3 camPos = __instance.worldCamera.transform.position;

            foreach (var kvp in animals)
            {
                var animal = kvp.Value;
                if (animal == null || animal.gameObject == null) continue;

                float dist = Vector3.Distance(camPos, animal.gameObject.transform.position);
                animal.distanceToCamera = dist;

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

        private static void ApplyAnimalLOD(AnimalView animal, bool isClose)
        {
            if (animal == null || animal.gameObject == null) return;

            // 1. Legacy animation engine (bone matrix evaluations)
            if (animal.anim != null)
            {
                animal.anim.enabled = isClose;
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

            // 3. SkinnedMeshRenderer (Shadows and Skin Quality)
            var smr = animal.gameObject.GetComponent<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.shadowCastingMode = isClose ? ShadowCastingMode.On : ShadowCastingMode.Off;
                smr.receiveShadows = isClose;
                smr.updateWhenOffscreen = false;
                smr.quality = isClose ? SkinQuality.Bone4 : SkinQuality.Bone1;
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

        private static void OptimizeAnimalInstance(AnimalView animal)
        {
            if (animal == null) return;

            if (animal.gameObjectYoungVersion != null)
            {
                ConfigureSkinnedMesh(animal.gameObjectYoungVersion);
                ConfigureLegacyAnimation(animal.gameObjectYoungVersion);

                if (!animal.currentlyShowingYoungVersion)
                {
                    animal.gameObjectYoungVersion.SetActive(false);
                }
            }

            if (animal.gameObjectOldVersion != null)
            {
                ConfigureSkinnedMesh(animal.gameObjectOldVersion);
                ConfigureLegacyAnimation(animal.gameObjectOldVersion);

                if (animal.currentlyShowingYoungVersion)
                {
                    animal.gameObjectOldVersion.SetActive(false);
                }
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
                renderers[i].updateWhenOffscreen = false;
            }
        }

        private static void ConfigureLegacyAnimation(GameObject go)
        {
            if (go == null) return;
            var anims = go.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                anims[i].cullingType = AnimationCullingType.BasedOnRenderers;
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
    }
}
