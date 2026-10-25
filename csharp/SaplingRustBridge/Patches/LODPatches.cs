using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Slashes the 604,000 active Transform and bone hierarchy overhead that consumes ~92% of frame time.
    /// Sets offscreen culling on SkinnedMeshRenderers and Animators, and disables unused young/old GameObjects.
    /// </summary>
    [HarmonyPatch]
    public static class LODPatches
    {
        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.Start))]
        [HarmonyPostfix]
        public static void AnimalView_Start_Postfix(AnimalView __instance)
        {
            OptimizeAnimalInstance(__instance);
        }

        [HarmonyPatch(typeof(PlantView), nameof(PlantView.Start))]
        [HarmonyPostfix]
        public static void PlantView_Start_Postfix(PlantView __instance)
        {
            OptimizePlantInstance(__instance);
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.UpdateAnimationActivityBasedOnDistanceToCamera))]
        [HarmonyPostfix]
        public static void AnimalView_UpdateAnimationActivity_Postfix(
            AnimalView __instance,
            float newDistanceToCamera,
            float midThreshold,
            float farThreshold)
        {
            if (__instance == null || __instance.gameObject == null) return;

            // Distance LOD culling for shadow casting and heavy renderers
            bool isFar = __instance.distanceToCameraCategory == DistanceToCameraCategory.Far;
            var smr = __instance.gameObject.GetComponent<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.shadowCastingMode = isFar ? ShadowCastingMode.Off : ShadowCastingMode.On;
            }

            // If distant, disable IK solver completely
            if (__instance.walkingAnimationController != null)
            {
                __instance.walkingAnimationController.solveIK = (__instance.distanceToCameraCategory == DistanceToCameraCategory.Close);
            }
        }

        private static void OptimizeAnimalInstance(AnimalView animal)
        {
            if (animal == null) return;

            // Optimize young version if it exists
            if (animal.gameObjectYoungVersion != null)
            {
                ConfigureSkinnedMesh(animal.gameObjectYoungVersion);
                ConfigureLegacyAnimation(animal.gameObjectYoungVersion);

                // Deactivate if currently showing old version
                if (!animal.currentlyShowingYoungVersion)
                {
                    animal.gameObjectYoungVersion.SetActive(false);
                }
            }

            // Optimize old version if it exists
            if (animal.gameObjectOldVersion != null)
            {
                ConfigureSkinnedMesh(animal.gameObjectOldVersion);
                ConfigureLegacyAnimation(animal.gameObjectOldVersion);

                // Deactivate if currently showing young version
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
                // NEVER update bone skinning transforms when offscreen!
                renderers[i].updateWhenOffscreen = false;
            }
        }

        private static void ConfigureLegacyAnimation(GameObject go)
        {
            if (go == null) return;
            var anims = go.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                // Stop animating when renderer is culled by camera
                anims[i].cullingType = AnimationCullingType.BasedOnRenderers;
            }
        }

        private static void ConfigureAnimator(GameObject go)
        {
            if (go == null) return;
            var animators = go.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                // Stop evaluating animation transforms when culled
                animators[i].cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }
        }
    }
}
