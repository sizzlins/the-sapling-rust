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
    /// Distant (>350 m, matching vanilla's cameraCloseDistanceThreshold) creatures bypass
    /// 40-bone evaluations, IK raycasts, and shadow caster draw calls.
    /// </summary>
    [HarmonyPatch]
    public static class LODPatches
    {
        // These are vanilla's own defaults (WorldView.cs:1551-1552). The previous class comment
        // claimed ">35m", which was simply wrong: 350 m / 1000 m is the shipped behaviour.
        public const float MidDistanceThreshold = 1000f;

        /// <summary>
        /// How far an organism can be and still get an animator and a shadow
        /// caster. Automatically adapted to live scene density by the LOD governor.
        /// </summary>
        public static float CloseDistanceThreshold = 180f;

        /// <summary>
        /// Radius within which animals keep SkinnedMeshRenderer.updateWhenOffscreen = true
        /// (vanilla behaviour) so zoomed-in tall/long-necked or animated creatures are never
        /// frustum-culled by their unposed bind-pose bounds. Beyond 40 m, Unity frustum-culls
        /// offscreen rigs instead of skinning 10,000+ unseen animals every frame.
        /// </summary>
        public const float OffscreenSkinDistance = 40f;

        /// <summary>
        /// Max distance at which plants run Mecanim wind Animators.
        /// </summary>
        public const float MaxPlantAnimatorDistance = 80f;

        // Hysteresis distances (metres) for automatically deactivating far-away static
        // organisms (plants, fungi, algae) and disabling far animal renderers so dense
        // 9,000+ animal worlds never render 75,000+ active Renderers / 40,000+ SkinnedMeshes.
        // Everything within 160-220 m of the camera stays 100% rendered; EnsurePooledObjectVisible
        // guarantees recycled pooled animals always re-enable all renderers.
        private const float PlantHideDistSqr = 260f * 260f;
        private const float PlantShowDistSqr = 240f * 240f;
        private const float PlantHideDistSqrDense = 145f * 145f;
        private const float PlantShowDistSqrDense = 130f * 130f;
        private const float SmallOrganismHideDistSqr = 170f * 170f;
        private const float SmallOrganismShowDistSqr = 150f * 150f;
        private const float SmallOrganismHideDistSqrDense = 105f * 105f;
        private const float SmallOrganismShowDistSqrDense = 90f * 90f;
        private const float AnimalHideDist = 260f;
        private const float AnimalShowDist = 235f;
        private const float AnimalHideDistDense = 130f;
        private const float AnimalShowDistDense = 115f;

        private static int _lastDensityTier = -1;
        private static int _cullPass = 0;

        public static WorldView ActiveWorldView = null;
        private static float _lastLODScanTime = 0f;
        private const float LODScanInterval = 0.20f;

        // GetComponentsInChildren allocates a fresh array on every call. Cache per GameObject
        // instance ID with a cap well above late-game animal counts (~10,000+) so the cache
        // never thrashes every scan (previously 2048 < 9000 caused 18k GetComponentsInChildren
        // calls per scan = 16 ms spike every 0.25s).
        private sealed class RendererCache
        {
            public SkinnedMeshRenderer[] Skinned;
            public MeshRenderer[] Meshes;
        }

        private const int MaxRendererCaches = 32768;
        private static readonly Dictionary<int, RendererCache> _rendererCaches = new Dictionary<int, RendererCache>();

        private static RendererCache GetRendererCache(GameObject go)
        {
            int id = go.GetInstanceID();

            RendererCache cache;
            if (_rendererCaches.TryGetValue(id, out cache)) return cache;

            cache = new RendererCache();
            cache.Skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            cache.Meshes = go.GetComponentsInChildren<MeshRenderer>(true);

            if (_rendererCaches.Count >= MaxRendererCaches) _rendererCaches.Clear();
            _rendererCaches[id] = cache;
            return cache;
        }


        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_Postfix(WorldView __instance)
        {
            if (__instance == null) return;
            ActiveWorldView = __instance;
            __instance.cameraCloseDistanceThreshold = CloseDistanceThreshold;
            __instance.cameraMidDistanceThreshold = MidDistanceThreshold;
        }

        [HarmonyPatch(typeof(WorldView), "Update")]
        [HarmonyPostfix]
        public static void WorldView_Update_LODGovernor(WorldView __instance)
        {
            if (__instance == null || __instance.worldCamera == null) return;
            ActiveWorldView = __instance;

            // Maintain camera distance thresholds automatically.
            __instance.cameraCloseDistanceThreshold = CloseDistanceThreshold;
            __instance.cameraMidDistanceThreshold = MidDistanceThreshold;

            if (TimeJumpPatches.IsTimeJumpActive)
            {
                return;
            }

            Vector3 camPos = __instance.worldCamera.transform.position;

            // Throttle LOD scanning to avoid per-frame dictionary iterations
            if (Time.time - _lastLODScanTime < LODScanInterval) return;
            _lastLODScanTime = Time.time;

            var animals = __instance.animalViewsPerLifeFormIndex;

            // Automatically adapt CloseDistanceThreshold and cap QualitySettings.lodBias on
            // dense late-game worlds, while NEVER dropping lodBias below vanilla's minimum
            // (OptionsController.lodBiases[0] = 1.0f) so zoomed-in animals never disappear.
            int activeAnimals = (animals != null) ? animals.Count : 0;
            int tier = (activeAnimals > 1500) ? 2 : ((activeAnimals > 400) ? 1 : 0);
            float maxLodBias = (tier == 0) ? 2.0f : ((tier == 1) ? 1.5f : 1.0f);
            if (tier != _lastDensityTier)
            {
                _lastDensityTier = tier;
                CloseDistanceThreshold = (tier == 0) ? 220f : ((tier == 1) ? 140f : 90f);
            }
            if (QualitySettings.lodBias > maxLodBias)
            {
                QualitySettings.lodBias = maxLodBias;
            }

            if (animals != null && animals.Count > 0)
            {
                float closeSqr = CloseDistanceThreshold * CloseDistanceThreshold;
                float midSqr = MidDistanceThreshold * MidDistanceThreshold;
                float offscreenSqr = OffscreenSkinDistance * OffscreenSkinDistance;
                float hideDist = (tier >= 2) ? AnimalHideDistDense : AnimalHideDist;
                float showDist = (tier >= 2) ? AnimalShowDistDense : AnimalShowDist;
                float animalHideSqr = hideDist * hideDist;
                float animalShowSqr = showDist * showDist;
                AnimalView focusedAnimal = __instance.animalViewInFocus;

                foreach (var kvp in animals)
                {
                    var animal = kvp.Value;
                    if (animal == null || animal.JITOptimized || animal.gameObject == null) continue;

                    Vector3 delta = animal.gameObject.transform.position - camPos;
                    float distSqr = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
                    float dist = Mathf.Sqrt(distSqr);
                    animal.distanceToCamera = dist;

                    bool isFocused = object.ReferenceEquals(animal, focusedAnimal);
                    DistanceToCameraCategory newCategory = (distSqr <= closeSqr || isFocused)
                        ? DistanceToCameraCategory.Close
                        : ((distSqr <= midSqr) ? DistanceToCameraCategory.Mid : DistanceToCameraCategory.Far);

                    bool isClose = (newCategory == DistanceToCameraCategory.Close);
                    bool wantOffscreen = (distSqr <= offscreenSqr) || isFocused;
                    bool hideRenderers = !isFocused && (tier >= 1) && (distSqr > animalHideSqr);
                    bool showRenderers = isFocused || (tier == 0) || (distSqr <= animalShowSqr);

                    RendererCache cache = GetRendererCache(animal.gameObject);
                    bool currentlyHidden = (cache.Skinned.Length > 0 && cache.Skinned[0] != null && !cache.Skinned[0].enabled) ||
                                           (cache.Meshes.Length > 0 && cache.Meshes[0] != null && !cache.Meshes[0].enabled);

                    if (hideRenderers)
                    {
                        if (!currentlyHidden || animal.distanceToCameraCategory != newCategory)
                        {
                            animal.distanceToCameraCategory = newCategory;
                            ApplyAnimalLOD(animal, false, false, false, cache);
                        }
                        continue;
                    }

                    bool needsApply = (animal.distanceToCameraCategory != newCategory) || (currentlyHidden && showRenderers);
                    if (!needsApply && cache.Skinned.Length > 0 && cache.Skinned[0] != null && cache.Skinned[0].updateWhenOffscreen != wantOffscreen)
                    {
                        needsApply = true;
                    }

                    if (needsApply)
                    {
                        animal.distanceToCameraCategory = newCategory;
                        ApplyAnimalLOD(animal, isClose, wantOffscreen, true, cache);
                    }
                    else if (!isClose)
                    {
                        if (!animal.isWalking && animal.walkingAnimationController != null && animal.walkingAnimationController.enabled)
                        {
                            animal.walkingAnimationController.enabled = false;
                        }
                    }
                }
            }

            // Round-robin distance culling of static organisms (plants / fungi / algae)
            // so Unity never keeps 150,000+ far-away Renderers and Animators active.
            // PlantView / FungusView / AlgaView already guard activeSelf (PlantView.cs:298, AlgaView.cs:102).
            int pass = (_cullPass++) % 3;
            bool dense = (tier >= 2);
            if (pass == 0)
            {
                float pHide = dense ? PlantHideDistSqrDense : PlantHideDistSqr;
                float pShow = dense ? PlantShowDistSqrDense : PlantShowDistSqr;
                PlantView focusedPlant = __instance.plantViewInFocus;
                CullPlantArray(__instance.plantViewsArea0, camPos, focusedPlant, pHide, pShow);
                CullPlantArray(__instance.plantViewsArea1, camPos, focusedPlant, pHide, pShow);
                CullPlantArray(__instance.plantViewsArea2, camPos, focusedPlant, pHide, pShow);
            }
            else if (pass == 1)
            {
                float sHide = dense ? SmallOrganismHideDistSqrDense : SmallOrganismHideDistSqr;
                float sShow = dense ? SmallOrganismShowDistSqrDense : SmallOrganismShowDistSqr;
                CullFungusArray(__instance.fungusViewsArea0, camPos, sHide, sShow);
                CullFungusArray(__instance.fungusViewsArea1, camPos, sHide, sShow);
                CullFungusArray(__instance.fungusViewsArea2, camPos, sHide, sShow);
            }
            else
            {
                float sHide = dense ? SmallOrganismHideDistSqrDense : SmallOrganismHideDistSqr;
                float sShow = dense ? SmallOrganismShowDistSqrDense : SmallOrganismShowDistSqr;
                CullAlgaArray(__instance.macroAlgaViewsArea0, camPos, sHide, sShow);
                CullAlgaArray(__instance.macroAlgaViewsArea1, camPos, sHide, sShow);
                CullAlgaArray(__instance.macroAlgaViewsArea2, camPos, sHide, sShow);
                CullAlgaArray(__instance.planktonViewsArea0, camPos, sHide, sShow);
                CullAlgaArray(__instance.planktonViewsArea1, camPos, sHide, sShow);
                CullAlgaArray(__instance.planktonViewsArea2, camPos, sHide, sShow);
            }
        }

        private static void CullPlantArray(PlantView[] arr, Vector3 camPos, PlantView focusedPlant, float hideSqr, float showSqr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                PlantView pv = arr[i];
                if (pv == null || pv.JITOptimized) continue;
                GameObject go = pv.gameObject;
                if (go == null) continue;

                if (object.ReferenceEquals(pv, focusedPlant))
                {
                    if (!go.activeSelf) go.SetActive(true);
                    continue;
                }

                Vector3 d = go.transform.position - camPos;
                float sqr = d.x * d.x + d.y * d.y + d.z * d.z;
                if (go.activeSelf)
                {
                    if (sqr > hideSqr) go.SetActive(false);
                }
                else
                {
                    if (sqr <= showSqr) go.SetActive(true);
                }
            }
        }

        private static void CullFungusArray(FungusView[] arr, Vector3 camPos, float hideSqr, float showSqr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                FungusView fv = arr[i];
                if (fv == null) continue;
                GameObject go = fv.gameObject;
                if (go == null) continue;

                Vector3 d = go.transform.position - camPos;
                float sqr = d.x * d.x + d.y * d.y + d.z * d.z;
                if (go.activeSelf)
                {
                    if (sqr > hideSqr) go.SetActive(false);
                }
                else
                {
                    if (sqr <= showSqr) go.SetActive(true);
                }
            }
        }

        private static void CullAlgaArray(AlgaView[] arr, Vector3 camPos, float hideSqr, float showSqr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                AlgaView av = arr[i];
                if (av == null || av.JITOptimized) continue;
                GameObject go = av.gameObject;
                if (go == null) continue;

                Vector3 d = go.transform.position - camPos;
                float sqr = d.x * d.x + d.y * d.y + d.z * d.z;
                if (go.activeSelf)
                {
                    if (sqr > hideSqr) go.SetActive(false);
                }
                else
                {
                    if (sqr <= showSqr) go.SetActive(true);
                }
            }
        }

        // Ensure any organism popped from the pool starts active with all renderers enabled.
        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.GetFullSpecificOrganism), new Type[] { typeof(int), typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        public static void OrganismObjectPool_GetFullSpecificOrganism_EnsureActive(GameObject __result)
        {
            EnsurePooledObjectVisible(__result);
        }

        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.GetFullSpecificOrganism), new Type[] { typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        public static void OrganismObjectPool_GetFullSpecificOrganism2_EnsureActive(GameObject __result)
        {
            EnsurePooledObjectVisible(__result);
        }

        private static void EnsurePooledObjectVisible(GameObject go)
        {
            if (go == null) return;
            if (!go.activeSelf) go.SetActive(true);
            RendererCache cache = GetRendererCache(go);
            for (int r = 0; r < cache.Skinned.Length; r++)
            {
                if (cache.Skinned[r] != null && !cache.Skinned[r].enabled) cache.Skinned[r].enabled = true;
            }
            for (int m = 0; m < cache.Meshes.Length; m++)
            {
                if (cache.Meshes[m] != null && !cache.Meshes[m].enabled) cache.Meshes[m].enabled = true;
            }
        }

        // Pooled organisms stay SetActive(true) at outOfSightLocation when
        // outOfSightInsteadOfDisabling is true. Reset updateWhenOffscreen and
        // AlwaysAnimate before parking them so Unity frustum-culls them instead
        // of skinning thousands of offscreen pooled rigs every frame.
        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.HideNewSpecificGameObjects))]
        [HarmonyPrefix]
        public static void OrganismObjectPool_HideNewSpecificGameObjects_Prefix(OrganismObjectPool __instance)
        {
            if (__instance == null) return;
            ResetPooledRenderers(__instance.newlyAddedSpecificYoungGameObjects);
            ResetPooledRenderers(__instance.newlyAddedSpecificOldGameObjects);
        }

        private static void ResetPooledRenderers(List<GameObject> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                GameObject go = list[i];
                if (go == null) continue;
                RendererCache cache = GetRendererCache(go);
                for (int r = 0; r < cache.Skinned.Length; r++)
                {
                    if (cache.Skinned[r] != null) cache.Skinned[r].updateWhenOffscreen = false;
                }
                var anim = go.GetComponent<Animation>();
                if (anim != null)
                {
                    anim.cullingType = AnimationCullingType.BasedOnRenderers;
                }
            }
        }

        private static void ApplyAnimalLOD(AnimalView animal, bool isClose, bool wantOffscreen, bool renderVisible, RendererCache cache)
        {
            if (animal == null || animal.gameObject == null) return;

            // 1. Legacy animation engine (bone matrix evaluations)
            if (animal.anim != null)
            {
                animal.anim.playAutomatically = false;
                animal.anim.cullingType = wantOffscreen ? AnimationCullingType.AlwaysAnimate : AnimationCullingType.BasedOnRenderers;
                animal.anim.enabled = renderVisible && (isClose || animal.canFly);
            }

            // 2. Procedural Inverse Kinematics & Walking Controller
            if (animal.walkingAnimationController != null)
            {
                animal.walkingAnimationController.solveIK = renderVisible && isClose;
                animal.walkingAnimationController.footPrecisionMode = (renderVisible && isClose)
                    ? FootPrecisionMode.SometimesUpdated
                    : FootPrecisionMode.NeverUpdated;

                // If hidden or distant-and-not-walking, disable MonoBehaviour completely
                animal.walkingAnimationController.enabled = renderVisible && (isClose || animal.isWalking);
            }

            // 3. Renderers (Visibility, Shadows, and Offscreen Updates)
            // Note: Do NOT override smr.quality (keep vanilla SkinQuality.Auto).
            // Retail AnimalArmatureBuilder.cs:726 binds mouth child submeshes (teeth) with
            // unnormalized weights (weight0=1f, weight1=1f, sum=2.0); forcing Bone2/Bone4
            // evaluates both bones and projects teeth 2x forward-and-up off the head.
            var renderers = cache.Skinned;
            for (int r = 0; r < renderers.Length; r++)
            {
                var smr = renderers[r];
                if (smr != null)
                {
                    smr.enabled = renderVisible;
                    smr.shadowCastingMode = (renderVisible && isClose) ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    smr.receiveShadows = renderVisible && isClose;
                    smr.updateWhenOffscreen = renderVisible && wantOffscreen;
                }
            }

            var meshRenderers = cache.Meshes;
            for (int m = 0; m < meshRenderers.Length; m++)
            {
                if (meshRenderers[m] != null) meshRenderers[m].enabled = renderVisible;
            }
        }

        // Configure newly built plants so offscreen Animators cull cleanly, distant LOD4 stubs
        // never cast shadows, and tiny horizon specs (<1.5% screen height) cull in C++.
        [HarmonyPatch(typeof(PlantObjectBuilder), nameof(PlantObjectBuilder.Run))]
        [HarmonyPostfix]
        public static void PlantObjectBuilder_Run_OptimizeLOD(GameObject __result)
        {
            if (__result == null) return;
            var anim = __result.GetComponent<Animator>();
            if (anim != null)
            {
                anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                anim.enabled = false;
            }
            Transform simple = __result.transform.Find("simple");
            if (simple != null)
            {
                var mr = simple.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
            }
            var lodGroup = __result.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                LOD[] lods = lodGroup.GetLODs();
                if (lods != null && lods.Length > 0 && lods[lods.Length - 1].screenRelativeTransitionHeight <= 0.001f)
                {
                    lods[lods.Length - 1].screenRelativeTransitionHeight = 0.015f;
                    lodGroup.SetLODs(lods);
                }
            }
        }

        [HarmonyPatch(typeof(AnimalView), nameof(AnimalView.Start))]
        [HarmonyPostfix]
        public static void AnimalView_Start_Postfix(AnimalView __instance)
        {
            if (__instance == null) return;
            OptimizeAnimalInstance(__instance);
        }

        // Null-safe animation speed update: prevents NullReferenceException when plantView.gameObject is null or animator is unassigned
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.SetAnimationsToSpeedWithIndex))]
        [HarmonyPrefix]
        public static bool WorldView_SetAnimationsToSpeedWithIndex_Prefix(WorldView __instance, int speedIndex)
        {
            if (__instance == null) return false;

            AlgaView[][] algaArrays = new AlgaView[6][]
            {
                __instance.macroAlgaViewsArea0, __instance.macroAlgaViewsArea1, __instance.macroAlgaViewsArea2,
                __instance.planktonViewsArea0, __instance.planktonViewsArea1, __instance.planktonViewsArea2
            };
            for (int i = 0; i < algaArrays.Length; i++)
            {
                AlgaView[] arr = algaArrays[i];
                if (arr == null) continue;
                for (int j = 0; j < arr.Length; j++)
                {
                    var av = arr[j];
                    if (av != null && !av.JITOptimized && av.animator != null && __instance.windAnimationSpeeds != null && speedIndex >= 0 && speedIndex < __instance.windAnimationSpeeds.Length)
                    {
                        av.SetAnimationSpeed(av.animator, __instance.windAnimationSpeeds[speedIndex]);
                    }
                }
            }

            PlantView[][] plantArrays = new PlantView[3][]
            {
                __instance.plantViewsArea0, __instance.plantViewsArea1, __instance.plantViewsArea2
            };
            for (int k = 0; k < plantArrays.Length; k++)
            {
                PlantView[] pArr = plantArrays[k];
                if (pArr == null) continue;
                for (int l = 0; l < pArr.Length; l++)
                {
                    var pv = pArr[l];
                    if (pv == null || pv.JITOptimized) continue;

                    if (pv.gameObject == null)
                    {
                        pv.gameObject = pv.currentlyShowingYoungVersion ? pv.gameObjectYoungVersion : pv.gameObjectOldVersion;
                        if (pv.gameObject == null) pv.gameObject = pv.gameObjectYoungVersion ?? pv.gameObjectOldVersion;
                    }

                    if (pv.gameObject != null)
                    {
                        var anim = pv.gameObject.GetComponent<Animator>();
                        if (anim != null && __instance.windAnimationSpeeds != null && speedIndex >= 0 && speedIndex < __instance.windAnimationSpeeds.Length)
                        {
                            pv.SetAnimationSpeed(anim, __instance.windAnimationSpeeds[speedIndex]);
                        }
                    }
                }
            }

            FungusView[][] fungusArrays = new FungusView[3][]
            {
                __instance.fungusViewsArea0, __instance.fungusViewsArea1, __instance.fungusViewsArea2
            };
            for (int m = 0; m < fungusArrays.Length; m++)
            {
                FungusView[] fArr = fungusArrays[m];
                if (fArr == null) continue;
                for (int n = 0; n < fArr.Length; n++)
                {
                    var fv = fArr[n];
                    if (fv != null && __instance.windAnimationSpeeds != null && speedIndex >= 0 && speedIndex < __instance.windAnimationSpeeds.Length)
                    {
                        fv.animationSpeed = __instance.windAnimationSpeeds[speedIndex];
                    }
                }
            }

            if (__instance.particleSystems != null)
            {
                for (int p = 0; p < __instance.particleSystems.Count; p++)
                {
                    var ps = __instance.particleSystems[p];
                    if (ps != null)
                    {
                        if (speedIndex == 0) ps.Pause();
                        else ps.Play();
                    }
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(PlantView), nameof(PlantView.UpdateAnimatorActivityBasedOnDistanceToCamera))]
        [HarmonyPrefix]
        public static bool PlantView_UpdateAnimatorActivity_Prefix(PlantView __instance, float newDistanceToCamera, float threshold)
        {
            if (__instance == null || __instance.JITOptimized) return false;

            if (__instance.gameObject == null)
            {
                __instance.gameObject = __instance.currentlyShowingYoungVersion ? __instance.gameObjectYoungVersion : __instance.gameObjectOldVersion;
                if (__instance.gameObject == null) __instance.gameObject = __instance.gameObjectYoungVersion ?? __instance.gameObjectOldVersion;
            }

            if (__instance.gameObject == null) return false;

            __instance.distanceToCamera = newDistanceToCamera;
            float scale = __instance.gameObject.transform.localScale.x;
            float effectiveThreshold = (threshold > MaxPlantAnimatorDistance) ? MaxPlantAnimatorDistance : threshold;
            __instance.SetAnimatorActivity(newDistanceToCamera < effectiveThreshold * scale);
            return false;
        }

        [HarmonyPatch(typeof(PlantView), nameof(PlantView.SetAnimatorActivity))]
        [HarmonyPrefix]
        public static bool PlantView_SetAnimatorActivity_Prefix(PlantView __instance, bool active)
        {
            if (__instance == null || __instance.JITOptimized || __instance.gameObject == null)
                return false;

            var component = __instance.gameObject.GetComponent<Animator>();
            if (component == null) return false;
            component.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            return true;
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
        [HarmonyPatch(typeof(AnimalView), "UpdateAnimationActivityBasedOnDistanceToCamera")]
        [HarmonyPrefix]
        public static void AnimalView_UpdateAnimationActivity_Prefix(ref float midThreshold, ref float farThreshold)
        {
            if (midThreshold > CloseDistanceThreshold) midThreshold = CloseDistanceThreshold;
            if (farThreshold > MidDistanceThreshold) farThreshold = MidDistanceThreshold;
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

        private static void ConfigureSkinnedMesh(GameObject go)
        {
            if (go == null) return;
            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                // Start visible & frustum-culled; ApplyAnimalLOD adjusts Close/Far animals.
                renderers[i].enabled = true;
                renderers[i].updateWhenOffscreen = false;
            }
            var meshes = go.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                meshes[i].enabled = true;
            }
        }

        private static void ConfigureLegacyAnimation(GameObject go)
        {
            if (go == null) return;
            var anims = go.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                anims[i].playAutomatically = false;
                anims[i].cullingType = AnimationCullingType.BasedOnRenderers;
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

        // Fix vanilla visual bug (WorldView.cs:4405-4408): ShowAnimalViewsForArea calls
        // AnimateSleeping(true) when an animal falls asleep, but never calls AnimateSleeping(false)
        // when livingAnimal.sleeping becomes false, leaving awake idle animals stuck in sleep pose
        // with floating Zzz particles.
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowAnimalViewsForArea))]
        [HarmonyPostfix]
        public static void WorldView_ShowAnimalViewsForArea_WakeSleepingViews(WorldView __instance, WorldArea area)
        {
            if (__instance == null || area == null || area.cells == null || area.animalPositionTracker == null) return;
            var views = __instance.animalViewsPerLifeFormIndex;
            if (views == null || views.Count == 0) return;

            var positions = area.animalPositionTracker.animalPositions;
            if (positions == null || positions.Count == 0) return;

            WorldCell[] cells = area.cells;
            foreach (AnimalPosition pos in positions)
            {
                int cellIdx = pos.cellIndex;
                if (cellIdx < 0 || cellIdx >= cells.Length) continue;
                LivingAnimal la = area.GetAnimalInCell(cellIdx, pos.locationType);
                if (la == null || la.sleeping) continue;

                AnimalView av;
                if (views.TryGetValue(la.lifeFormIndex, out av) && av != null && av.sleeping)
                {
                    av.AnimateSleeping(false);
                }
            }
        }
    }
}
